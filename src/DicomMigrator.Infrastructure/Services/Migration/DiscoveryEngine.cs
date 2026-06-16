using System.Collections.Concurrent;
using System.Diagnostics;
using DicomMigrator.Core.Interfaces;
using DicomMigrator.Core.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DicomMigrator.Infrastructure.Services.Migration;

// ═══════════════════════════════════════════════════════════════════════════
// RF-020 — Discovery Engine
// Descubrimiento progresivo por particiones con detección de truncamiento y
// subdivisión adaptativa (día → modalidad → rango horario).
// ═══════════════════════════════════════════════════════════════════════════

public class DiscoveryEngine(
    IServiceScopeFactory scopeFactory,
    ILogger<DiscoveryEngine> logger) : IDiscoveryEngine
{
    // Cancellation tokens per running job
    private static readonly ConcurrentDictionary<int, CancellationTokenSource> _running = new();

    // Default modalities used when subdividing a truncated day partition
    private static readonly string[] CommonModalities =
        ["CT", "MR", "CR", "DX", "US", "MG", "NM", "PT", "XA", "OT"];

    // Time ranges used in level-3 subdivision (StudyTime HHmmss)
    private static readonly (string from, string to)[] TimeRanges =
    [
        ("000000", "055959"),
        ("060000", "115959"),
        ("120000", "175959"),
        ("180000", "235959"),
    ];

    // ── Partition generation ──────────────────────────────────────────────────
    public async Task GeneratePartitionsAsync(int jobId, CancellationToken ct = default)
    {
        using var scope = scopeFactory.CreateScope();
        var jobRepo = scope.ServiceProvider.GetRequiredService<IDiscoveryJobRepository>();

        var job = await jobRepo.GetByIdAsync(jobId)
            ?? throw new InvalidOperationException($"Discovery Job {jobId} no encontrado");

        if (job.DiscoveryType != "Temporal")
        {
            logger.LogInformation("Job {Id} no es Temporal — no se generan particiones por fecha", jobId);
            return;
        }
        if (job.StartDate is null || job.EndDate is null)
            throw new InvalidOperationException("El job Temporal requiere StartDate y EndDate");

        // Default strategy: 1 day = 1 partition
        var partitions = new List<DiscoveryPartition>();
        for (var d = job.StartDate.Value; d <= job.EndDate.Value; d = d.AddDays(1))
        {
            ct.ThrowIfCancellationRequested();
            partitions.Add(new DiscoveryPartition
            {
                DiscoveryJobId = jobId,
                PartitionType  = "Day",
                StartDate      = d,
                EndDate        = d,
                Status         = "Pending",
            });
        }

        await jobRepo.AddPartitionsAsync(partitions);
        logger.LogInformation("Generadas {Count} particiones diarias para job {Id}", partitions.Count, jobId);
    }

    // ── Lifecycle ───────────────────────────────────────────────────────────
    public async Task StartAsync(int jobId, CancellationToken ct = default)
    {
        using var scope = scopeFactory.CreateScope();
        var jobRepo = scope.ServiceProvider.GetRequiredService<IDiscoveryJobRepository>();

        var job = await jobRepo.GetByIdAsync(jobId)
            ?? throw new InvalidOperationException($"Discovery Job {jobId} no encontrado");

        // Ensure partitions match the job's CURRENT date range. For Temporal jobs,
        // if the existing partitions fall outside [StartDate, EndDate] (e.g. the job
        // was edited with a new range, or they're stale from a previous config),
        // delete and regenerate them. This prevents orphan partitions from a prior
        // configuration lingering in the table.
        if (job.DiscoveryType == "Temporal")
        {
            var partitions = await jobRepo.GetPartitionsAsync(jobId);
            var rangeMismatch = job.StartDate is not null && job.EndDate is not null &&
                partitions.Any(p =>
                    p.StartDate is null || p.EndDate is null ||
                    p.StartDate < job.StartDate || p.EndDate > job.EndDate);

            if (partitions.Count == 0 || rangeMismatch)
            {
                if (rangeMismatch)
                {
                    logger.LogInformation(
                        "Las particiones del job {Id} no coinciden con el rango {Start}–{End}; regenerando.",
                        jobId, job.StartDate, job.EndDate);
                    await jobRepo.DeletePartitionsAsync(jobId);
                }
                await GeneratePartitionsAsync(jobId, ct);
            }
        }

        await jobRepo.UpdateStatusAsync(jobId, "Running");
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _running[jobId] = cts;

        // Fire-and-forget background processing
        _ = Task.Run(() => RunWorkersAsync(jobId, job.WorkerThreads, cts.Token), cts.Token);
    }

    public Task PauseAsync(int jobId)
    {
        if (_running.TryRemove(jobId, out var cts))
        {
            cts.Cancel();
            cts.Dispose();   // ← Release OS handles and internal timer/callbacks
            logger.LogInformation("Discovery Job {Id} pausado", jobId);
        }
        using var scope = scopeFactory.CreateScope();
        var jobRepo = scope.ServiceProvider.GetRequiredService<IDiscoveryJobRepository>();
        return jobRepo.UpdateStatusAsync(jobId, "Paused");
    }

    public Task ResumeAsync(int jobId, CancellationToken ct = default) => StartAsync(jobId, ct);

    // ── Worker pool ───────────────────────────────────────────────────────────
    private async Task RunWorkersAsync(int jobId, int threads, CancellationToken ct)
    {
        threads = Math.Max(1, threads);
        logger.LogInformation("Discovery Job {Id} iniciado con {Threads} worker(s)", jobId, threads);

        try
        {
            var workers = Enumerable.Range(1, threads)
                .Select(i => WorkerLoopAsync(jobId, $"DISC-W{i}", ct))
                .ToList();

            await Task.WhenAll(workers);

            // Mark job completed if not cancelled
            if (!ct.IsCancellationRequested)
            {
                using var scope = scopeFactory.CreateScope();
                var jobRepo = scope.ServiceProvider.GetRequiredService<IDiscoveryJobRepository>();
                await jobRepo.UpdateStatusAsync(jobId, "Completed");
                logger.LogInformation("Discovery Job {Id} completado", jobId);
            }
        }
        catch (OperationCanceledException) { /* expected on Pause */ }
        catch (Exception ex)
        {
            logger.LogError(ex, "Discovery Job {Id} terminó con excepción no controlada", jobId);
        }
        finally
        {
            // Always release the CTS — guarantees no leak on exceptions, completion or cancellation
            if (_running.TryRemove(jobId, out var cts))
                cts.Dispose();
        }
    }

    private async Task WorkerLoopAsync(int jobId, string workerId, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            using var scope = scopeFactory.CreateScope();
            var jobRepo = scope.ServiceProvider.GetRequiredService<IDiscoveryJobRepository>();

            var partition = await jobRepo.AcquireNextPendingPartitionAsync(jobId, workerId);
            if (partition is null) break;   // no more work

            await ProcessPartitionAsync(jobId, partition, workerId, ct);
        }
    }

    // ── Partition processing with adaptive subdivision ─────────────────────────
    private async Task ProcessPartitionAsync(
        int jobId, DiscoveryPartition partition, string workerId, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var jobRepo     = scope.ServiceProvider.GetRequiredService<IDiscoveryJobRepository>();
        var studyRepo   = scope.ServiceProvider.GetRequiredService<IDiscoveredStudyRepository>();
        var dimse       = scope.ServiceProvider.GetRequiredService<IDimseService>();
        var dicomWeb    = scope.ServiceProvider.GetRequiredService<IDicomWebService>();

        var job = await jobRepo.GetByIdAsync(jobId);
        if (job?.SourcePacs is null)
        {
            partition.Status = "Failed";
            partition.LastError = "PACS origen no disponible";
            await jobRepo.UpdatePartitionAsync(partition);
            return;
        }

        var sw = Stopwatch.StartNew();
        partition.AttemptCount++;

        // Safety guard: if a partition has been attempted too many times, mark as failed
        if (partition.AttemptCount > 5)
        {
            logger.LogError("[{Worker}] Partición {Id} superó el máximo de intentos ({N}), marcando como Failed",
                workerId, partition.Id, partition.AttemptCount);
            partition.Status = "Failed";
            partition.LastError = $"Superado el límite de intentos ({partition.AttemptCount})";
            partition.FinishedAt = DateTime.UtcNow;
            partition.LockedByWorker = null;
            await jobRepo.UpdatePartitionAsync(partition);
            return;
        }

        var dateStr = partition.StartDate?.ToString("yyyyMMdd") ?? "";
        var filters = $"StudyDate={dateStr}"
                    + (partition.Modality is not null ? $" Modality={partition.Modality}" : "")
                    + (partition.StudyTimeFrom is not null ? $" Time={partition.StudyTimeFrom}-{partition.StudyTimeTo}" : "");

        logger.LogInformation("[{Worker}] Partición {Type} {Date} {Mod} {Time}",
            workerId, partition.PartitionType, dateStr,
            partition.Modality ?? "", partition.StudyTimeFrom ?? "");

        try
        {
            List<DicomStudyDto> studies;
            string result = "OK";

            if (job.QueryMethod == "QIDO")
            {
                var qido = await dicomWeb.QidoAsync(job.SourcePacs, new QidoQuery
                {
                    StudyDate = BuildDateForQuery(partition),
                    Modality  = partition.Modality,
                    Limit     = job.PacsResultLimit + 1,  // +1 to detect truncation
                }, ct);
                studies = qido.Studies;
                if (!qido.Success) result = "ERROR";
            }
            else // CFIND
            {
                var find = await dimse.FindAsync(job.SourcePacs, new CFindQuery
                {
                    Level             = "STUDY",
                    StudyDate         = BuildDateForQuery(partition),
                    ModalitiesInStudy = partition.Modality,
                }, ct);
                studies = find.Studies;
                if (!find.Success) result = "ERROR";
            }

            sw.Stop();

            // Log the request
            await jobRepo.AddRequestLogAsync(new DiscoveryRequest
            {
                DiscoveryJobId  = jobId,
                PartitionId     = partition.Id,
                SourcePacsId    = job.SourcePacsId,
                QueryType       = job.QueryMethod,
                Filters         = filters,
                DurationMs      = sw.Elapsed.TotalMilliseconds,
                Result          = result,
                StudiesReturned = studies.Count,
                Attempt         = partition.AttemptCount,
            });

            partition.StudiesFound = studies.Count;
            partition.DurationMs   = sw.Elapsed.TotalMilliseconds;

            // ── Request failed (PACS unreachable, timeout, bad response) ──────
            // result == "ERROR" when find.Success / qido.Success is false.
            // Must mark the partition Failed here — otherwise truncation logic
            // would mark it Completed with 0 studies (incorrect).
            if (result == "ERROR")
            {
                partition.Status         = "Failed";
                partition.LastError      = job.QueryMethod == "QIDO"
                    ? "Petición QIDO-RS fallida (ver logs)"
                    : "Petición C-FIND fallida (ver logs)";
                partition.FinishedAt     = DateTime.UtcNow;
                partition.LockedByWorker = null;
                await jobRepo.UpdatePartitionAsync(partition);
                return;
            }

            // ── Truncation detection ──
            // If results >= configured limit, the PACS may have truncated the response.
            bool truncated = studies.Count >= job.PacsResultLimit;

            if (truncated && CanSubdivide(partition))
            {
                logger.LogWarning("[{Worker}] Partición posiblemente truncada ({Count} >= {Limit}) — subdividiendo",
                    workerId, studies.Count, job.PacsResultLimit);

                var children = Subdivide(partition, jobId);
                await jobRepo.AddPartitionsAsync(children);

                partition.Status   = "Subdivided";
                partition.LastError = $"Truncada con {studies.Count} resultados (límite {job.PacsResultLimit}). Subdividida en {children.Count} particiones.";
                partition.FinishedAt = DateTime.UtcNow;
                partition.LockedByWorker = null;
                await jobRepo.UpdatePartitionAsync(partition);
                return;
            }

            // ── Persist studies to inventory ──
            var toInsert = studies
                .Where(s => !string.IsNullOrEmpty(s.StudyInstanceUid))
                .Select(s => MapToDiscovered(s, job))
                .ToList();

            var (inserted, updated) = await studyRepo.UpsertAsync(toInsert);

            partition.StudiesInserted = inserted;
            partition.StudiesUpdated  = updated;
            partition.Status          = truncated ? "PossiblyTruncated" : "Completed";
            partition.FinishedAt      = DateTime.UtcNow;
            partition.LockedByWorker  = null;
            if (truncated)
                partition.LastError = $"Posible truncamiento: {studies.Count} resultados (límite {job.PacsResultLimit}). No se pudo subdividir más.";

            await jobRepo.UpdatePartitionAsync(partition);

            logger.LogInformation("[{Worker}] Partición {Date} {Mod}: {Found} encontrados, {Ins} nuevos, {Upd} actualizados",
                workerId, dateStr, partition.Modality ?? "*", studies.Count, inserted, updated);
        }
        catch (Exception ex)
        {
            sw.Stop();
            logger.LogError(ex, "[{Worker}] Error procesando partición {Id}", workerId, partition.Id);

            await jobRepo.AddRequestLogAsync(new DiscoveryRequest
            {
                DiscoveryJobId = jobId, PartitionId = partition.Id, SourcePacsId = job.SourcePacsId,
                QueryType = job.QueryMethod, Filters = filters,
                DurationMs = sw.Elapsed.TotalMilliseconds, Result = "ERROR",
                Attempt = partition.AttemptCount, Error = ex.Message,
            });

            partition.Status = "Failed";
            partition.LastError = ex.Message;
            partition.FinishedAt = DateTime.UtcNow;
            partition.LockedByWorker = null;
            await jobRepo.UpdatePartitionAsync(partition);
        }
    }

    // ── Adaptive subdivision logic ──────────────────────────────────────────────
    private static bool CanSubdivide(DiscoveryPartition p) =>
        // Day → DayModality → DayModalityTime; can't subdivide beyond time ranges
        p.PartitionType is "Day" or "DayModality";

    private static List<DiscoveryPartition> Subdivide(DiscoveryPartition parent, int jobId)
    {
        var children = new List<DiscoveryPartition>();

        if (parent.PartitionType == "Day")
        {
            // Level 2: split by modality
            foreach (var mod in CommonModalities)
            {
                children.Add(new DiscoveryPartition
                {
                    DiscoveryJobId = jobId,
                    PartitionType  = "DayModality",
                    StartDate      = parent.StartDate,
                    EndDate        = parent.EndDate,
                    Modality       = mod,
                    Status         = "Pending",
                });
            }
        }
        else if (parent.PartitionType == "DayModality")
        {
            // Level 3: split by time range
            foreach (var (from, to) in TimeRanges)
            {
                children.Add(new DiscoveryPartition
                {
                    DiscoveryJobId = jobId,
                    PartitionType  = "DayModalityTime",
                    StartDate      = parent.StartDate,
                    EndDate        = parent.EndDate,
                    Modality       = parent.Modality,
                    StudyTimeFrom  = from,
                    StudyTimeTo    = to,
                    Status         = "Pending",
                });
            }
        }

        return children;
    }

    // ── Mapping helpers ─────────────────────────────────────────────────────────
    private static string BuildDateForQuery(DiscoveryPartition p)
    {
        // Single-day partition → exact date; range → DICOM range syntax
        if (p.StartDate is null) return "";
        var start = p.StartDate.Value.ToString("yyyyMMdd");
        if (p.EndDate is null || p.EndDate == p.StartDate) return start;
        return $"{start}-{p.EndDate.Value:yyyyMMdd}";
    }

    private static DiscoveredStudy MapToDiscovered(DicomStudyDto s, DiscoveryJob job) => new()
    {
        StudyInstanceUid              = s.StudyInstanceUid!,
        PatientId                     = s.PatientId,
        PatientName                   = s.PatientName,
        AccessionNumber               = s.AccessionNumber,
        StudyDate                     = s.StudyDate,
        StudyDescription              = s.StudyDescription,
        ModalitiesInStudy             = s.ModalitiesInStudy,
        NumberOfStudyRelatedSeries    = s.NumberOfSeries,
        NumberOfStudyRelatedInstances = s.NumberOfInstances,
        DiscoveryDate                 = DateTime.UtcNow,
        SourcePacsId                  = job.SourcePacsId,
        DiscoveryJobId                = job.Id,
    };
}
