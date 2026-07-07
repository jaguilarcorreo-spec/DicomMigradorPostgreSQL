// ══════════════════════════════════════════════════════════════════════════════
//  InstanceCaptureService.cs
//  Nivel 2 de verificación — captura de UIDs de ORIGEN en el DESCUBRIMIENTO.
//  Enumera por C-FIND IMAGE los SOPInstanceUID de cada estudio de un DiscoveryJob
//  y los persiste (DiscoveredInstance). Idempotente (borra y reinserta por estudio).
//  Proceso gobernado por jobId (Start/Pause/Resume/Stop), espejando verificación.
//  Al crear una migración desde el inventario, estos UIDs se copian a
//  MigrationInstance (ver ImportFromInventoryAsync, Opción A).
// ══════════════════════════════════════════════════════════════════════════════
using DicomMigrator.Core.Interfaces;
using DicomMigrator.Core.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DicomMigrator.Infrastructure.Services.Migration;

public class InstanceCaptureService(
    IDiscoveryJobRepository       jobRepo,
    IDiscoveredStudyRepository    discStudyRepo,
    IDiscoveredInstanceRepository discInstanceRepo,
    INodeRepository               nodeRepo,
    IDimseService                 dimse,
    IServiceScopeFactory          scopeFactory,
    ILogger<InstanceCaptureService> logger) : IInstanceCaptureService
{
    // Registro en memoria de capturas en marcha (para pausar/reanudar). Estático
    // porque el servicio es Scoped pero el proceso debe sobrevivir entre peticiones.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, CancellationTokenSource> _captureCts = new();

    // ── Proceso gobernado (por job) ───────────────────────────────────────────
    public async Task StartCaptureAsync(int jobId, CancellationToken ct = default)
    {
        if (_captureCts.TryGetValue(jobId, out var existing))
        {
            if (!existing.IsCancellationRequested)
            {
                logger.LogWarning("La captura del job {Id} ya está en marcha", jobId);
                return;
            }
            _captureCts.TryRemove(jobId, out var stale);
            stale?.Dispose();
        }

        var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _captureCts[jobId] = linkedCts;
        var token = linkedCts.Token;

        // Reanudar (estado previo "Paused") conserva el cronómetro; un inicio nuevo lo reinicia.
        var job0 = await jobRepo.GetByIdAsync(jobId);
        var resume = job0?.CaptureStatus == "Paused";
        await jobRepo.SetCaptureRunningAsync(jobId, resetTimer: !resume);
        logger.LogInformation("Captura Nivel 2 iniciada · job {Id}", jobId);

        var task = Task.Run(() => CaptureForJobAsync(jobId, false, token), token);

        _ = task.ContinueWith(async t =>
        {
            try
            {
                var isCurrent = _captureCts.TryGetValue(jobId, out var current) && ReferenceEquals(current, linkedCts);
                if (!isCurrent) return;

                _captureCts.TryRemove(jobId, out var removed);
                removed?.Dispose();

                if (linkedCts.IsCancellationRequested)
                    return;   // Pause/Stop ya fijó el estado

                using var scope = scopeFactory.CreateScope();
                var jr = scope.ServiceProvider.GetRequiredService<IDiscoveryJobRepository>();
                var status = t.IsFaulted || t.Result?.ErrorMessage is not null ? "Failed" : "Completed";
                await jr.FinishCaptureAsync(jobId, status);
                logger.LogInformation("Captura Nivel 2 {Status} · job {Id}", status, jobId);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error en el handler de finalización de captura · job {Id}", jobId);
            }
        }, TaskScheduler.Default);
    }

    public async Task PauseCaptureAsync(int jobId)
    {
        if (_captureCts.TryRemove(jobId, out var cts)) { cts.Cancel(); cts.Dispose(); }
        await jobRepo.UpdateCaptureStatusAsync(jobId, "Paused");
        logger.LogInformation("Captura Nivel 2 pausada · job {Id}", jobId);
    }

    // Reanudar = reiniciar: idempotente, salta los estudios ya capturados.
    public Task ResumeCaptureAsync(int jobId, CancellationToken ct = default)
        => StartCaptureAsync(jobId, ct);

    public async Task StopCaptureAsync(int jobId)
    {
        if (_captureCts.TryRemove(jobId, out var cts)) { cts.Cancel(); cts.Dispose(); }
        await jobRepo.UpdateCaptureStatusAsync(jobId, "Idle");
        logger.LogInformation("Captura Nivel 2 detenida · job {Id}", jobId);
    }

    // ── Captura (motor) ───────────────────────────────────────────────────────
    public async Task<InstanceCaptureResult> CaptureForJobAsync(
        int jobId, bool forceRecapture = false, CancellationToken ct = default)
    {
        var res = new InstanceCaptureResult();

        var job = await jobRepo.GetByIdAsync(jobId);
        if (job is null)
        {
            res.ErrorMessage = "Job de descubrimiento no encontrado.";
            return res;
        }

        var origin = await nodeRepo.GetByIdAsync(job.SourcePacsId);
        if (origin is null)
        {
            res.ErrorMessage = "Nodo de origen (PACS del descubrimiento) no encontrado.";
            return res;
        }

        // Materializar (Id, StudyInstanceUid) primero para no mantener abierto el
        // lector de BD mientras se hacen los C-FIND (lentos, por red).
        var studies = new List<(long Id, string Uid)>();
        await foreach (var s in discStudyRepo.StreamForExportAsync(
            new DiscoveredStudyFilter { DiscoveryJobId = jobId }, ct))
        {
            if (!string.IsNullOrEmpty(s.StudyInstanceUid))
                studies.Add((s.Id, s.StudyInstanceUid));
        }

        var threads = Math.Max(1, job.WorkerThreads);
        logger.LogInformation("Captura Nivel 2 · job {Id} · {N} estudios · origen {Alias} · {T} hilo(s)",
            jobId, studies.Count, origin.Alias, threads);

        int captured = 0, skipped = 0, failed = 0, instances = 0;
        var failures = new System.Collections.Concurrent.ConcurrentBag<string>();

        // Paraleliza con el MISMO nº de hilos definido en el job de descubrimiento.
        // Un fallo de un estudio no aborta el resto (se contabiliza y sigue); solo la
        // cancelación (pausa/parada) detiene el proceso. Cada operación abre su propio
        // DbContext vía factory, así que la concurrencia es segura.
        await Parallel.ForEachAsync(studies,
            new ParallelOptions { MaxDegreeOfParallelism = threads, CancellationToken = ct },
            async (study, token) =>
            {
                if (!forceRecapture && await discInstanceRepo.CountForStudyAsync(study.Id) > 0)
                {
                    Interlocked.Increment(ref skipped);
                    return;
                }
                try
                {
                    var n = await CaptureForStudyAsync(origin, study.Id, study.Uid, token);
                    Interlocked.Increment(ref captured);
                    Interlocked.Add(ref instances, n);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref failed);
                    failures.Add($"{study.Uid}: {ex.Message}");
                    logger.LogWarning(ex, "Captura Nivel 2 falló para estudio {Uid}", study.Uid);
                }
            });

        res.StudiesCaptured   = captured;
        res.StudiesSkipped    = skipped;
        res.StudiesFailed     = failed;
        res.InstancesCaptured = instances;
        res.Failures.AddRange(failures);

        logger.LogInformation("Captura Nivel 2 · job {Id} · capturados={C} saltados={S} fallidos={F} instancias={I}",
            jobId, res.StudiesCaptured, res.StudiesSkipped, res.StudiesFailed, res.InstancesCaptured);

        return res;
    }

    public async Task<int> CaptureForStudyAsync(
        DicomNode originNode, long discoveredStudyId, string studyInstanceUid, CancellationToken ct = default)
    {
        var enumRes = await dimse.EnumerateInstancesAsync(originNode, studyInstanceUid, ct);
        if (!enumRes.Success)
            throw new InvalidOperationException(enumRes.ErrorMessage ?? "C-FIND IMAGE sin éxito.");

        // Idempotente: delete-then-insert.
        await discInstanceRepo.DeleteForStudyAsync(discoveredStudyId);
        return await discInstanceRepo.AddRangeAsync(discoveredStudyId,
            enumRes.Instances.Select(i => (i.SeriesInstanceUid, i.SopInstanceUid)));
    }
}
