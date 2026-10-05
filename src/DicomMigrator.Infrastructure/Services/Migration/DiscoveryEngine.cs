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

    // Última ejecución (RunWorkersAsync) lanzada por job. Tras una pausa, _running ya no
    // tiene entrada pero los workers viejos pueden seguir cerrando su partición: StartAsync
    // espera a que acaben antes de rescatar particiones 'Running' huérfanas.
    private static readonly ConcurrentDictionary<int, Task> _runs = new();

    // Tope de espera a que terminen los workers de una ejecución anterior al reanudar.
    private static readonly TimeSpan PreviousRunWait = TimeSpan.FromSeconds(60);

    // Rondas extra de workers si al terminar quedan particiones 'Running' huérfanas.
    private const int MaxOrphanRounds = 3;

    // Modalidades BASE para la subdivisión por modalidad, que ahora es el ÚLTIMO
    // recurso (solo cuando una franja horaria mínima sigue truncada; ver Subdivide).
    // Se amplía en tiempo de ejecución con las modalidades presentes en la muestra.
    // Cubre los términos definidos de Modality (PS3.3 C.7.3.1.1.1 / CID 29 y CID 33)
    // y los no estándar habituales en PACS reales (SC, DR, ST…): una modalidad que
    // solo tuviera estudios más allá del corte y no estuviera aquí no se descubriría.
    private static readonly string[] BaselineModalities =
    [
        "CT","MR","CR","DX","DR","RG","RF","XA","MG","US","NM","PT",
        "MA","XC","PX","IO","GM","SM","OP","OPT","OPTBSV","OPTENF","OPM","OPV","OAM",
        "OCT","IVOCT","ES","IVUS","BI","BMD","BDUS","ECG","EEG","EMG","EOG","EPS",
        "HD","RESP","LS","TG","ST","HC","AU","DG","SC","AR","KER","LEN","IOL","VA",
        "SRF","POS","FID","M3D","DOC","SR","KO","PR","REG","SEG","PLAN","ASMT",
        "RWV","SMR","STAIN","TEXTUREMAP","CTPROTOCOL","XAPROTOCOL",
        "RTIMAGE","RTDOSE","RTSTRUCT","RTPLAN","RTRECORD","RTINTENT","RTRAD","RTSEGANN",
        "OSS","OT"
    ];

    // Separa un ModalitiesInStudy multivaluado ("CT\MR", "CT,PR"…) en códigos sueltos.
    private static IEnumerable<string> SplitModalities(string? modalities)
    {
        if (string.IsNullOrWhiteSpace(modalities)) yield break;
        foreach (var part in modalities.Split(['\\', ',', '/'],
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var m = part.ToUpperInvariant();
            if (m.Length > 0) yield return m;
        }
    }

    // Primeras franjas horarias al subdividir un día truncado (StudyTime HHmmss).
    // Se SOLAPAN un segundo (…060000 / 060000…): con límites como 055959 y 060000, un
    // StudyTime con fracción (055959.4) quedaba fuera de las dos. El upsert por
    // StudyInstanceUID elimina el duplicado que pueda traer el segundo compartido.
    private static readonly (string from, string to)[] TimeRanges =
    [
        ("000000", "060000"),
        ("060000", "120000"),
        ("120000", "180000"),
        ("180000", "235959"),
    ];

    // QIDO-RS: páginas máximas por partición antes de rendirse y subdividir (protege
    // frente a un servidor que pagine mal). Con un límite de 500 son 100.000 estudios/día.
    private const int MaxQidoPages = 200;

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
        // Guardia de arranque ATÓMICA: reserva el slot en _running antes de hacer nada.
        // Como ResumeAsync es un alias de StartAsync, una segunda llamada estando ya en
        // marcha (doble clic, o UI + auto-reanudación al arrancar el servicio) NO debe
        // sobrescribir el CTS anterior — eso dejaría a los workers viejos con un token que
        // ya nadie puede cancelar (fuga de CTS) y lanzaría un segundo juego de workers.
        // TryAdd hace el "comprobar y reservar" en un solo paso, sin ventana de carrera.
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (!_running.TryAdd(jobId, cts))
        {
            cts.Dispose();
            logger.LogWarning("Discovery Job {Id} ya está en marcha; se ignora el arranque duplicado", jobId);
            return;
        }

        try
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

            // ── Rescate de particiones huérfanas ──────────────────────────────────
            // Con el slot reservado no hay workers NUEVOS de este job. Si quedan los de
            // una ejecución anterior (pausa → reanudar rápido), esperar a que terminen:
            // ellos mismos devuelven su partición a 'Pending'. Lo que siga en 'Running'
            // después es huérfano (caída abrupta del proceso o fallo de BD al cerrarla) y
            // AcquireNextPendingPartitionAsync, que solo toma 'Pending', no lo recogería
            // nunca: se devuelve a 'Pending' sin consumir AttemptCount.
            if (_runs.TryGetValue(jobId, out var previousRun) && !previousRun.IsCompleted)
            {
                var finished = await Task.WhenAny(previousRun, Task.Delay(PreviousRunWait, ct)) == previousRun;
                if (!finished)
                    logger.LogWarning("Discovery Job {Id}: los workers de la ejecución anterior no terminaron en {S}s; " +
                        "se rescatan igualmente sus particiones.", jobId, PreviousRunWait.TotalSeconds);
            }
            var rescued = await jobRepo.ReleaseOrphanPartitionsAsync(jobId);
            if (rescued > 0)
                logger.LogWarning("Discovery Job {Id}: {N} partición(es) huérfana(s) en 'Running' devuelta(s) a 'Pending'.",
                    jobId, rescued);

            await jobRepo.UpdateStatusAsync(jobId, "Running");

            // Fire-and-forget background processing. A partir de aquí, la propiedad del CTS
            // pasa a RunWorkersAsync, cuyo bloque finally lo retira de _running y lo libera.
            _runs[jobId] = Task.Run(() => RunWorkersAsync(jobId, job.WorkerThreads, cts), cts.Token);
        }
        catch
        {
            // El arranque falló ANTES de lanzar los workers (job inexistente, error al
            // generar particiones…). Liberar el slot reservado para no dejar el job
            // bloqueado: el finally de RunWorkersAsync no llegará a ejecutarse.
            if (_running.TryRemove(jobId, out var reserved))
                reserved.Dispose();
            throw;
        }
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
    private async Task RunWorkersAsync(int jobId, int threads, CancellationTokenSource runCts)
    {
        var ct = runCts.Token;
        threads = Math.Max(1, threads);
        logger.LogInformation("Discovery Job {Id} iniciado con {Threads} worker(s)", jobId, threads);

        try
        {
            for (var round = 1; ; round++)
            {
                var workers = Enumerable.Range(1, threads)
                    .Select(i => WorkerLoopAsync(jobId, $"DISC-W{i}", ct))
                    .ToList();

                await Task.WhenAll(workers);
                if (ct.IsCancellationRequested) break;

                // Todos los workers de este job han salido: cualquier partición que siga
                // en 'Running' es huérfana (p. ej. falló la BD al cerrarla). Devolverla a
                // 'Pending' y dar otra ronda, con tope por si el fallo es persistente.
                using var rscope = scopeFactory.CreateScope();
                var rescued = await rscope.ServiceProvider.GetRequiredService<IDiscoveryJobRepository>()
                    .ReleaseOrphanPartitionsAsync(jobId);
                if (rescued == 0 || round >= MaxOrphanRounds) break;
                logger.LogWarning("Discovery Job {Id}: {N} partición(es) 'Running' huérfana(s) al terminar la ronda {R}; " +
                    "devueltas a 'Pending' y relanzando workers.", jobId, rescued, round);
            }

            // Mark job completed if not cancelled
            if (!ct.IsCancellationRequested)
            {
                using var scope = scopeFactory.CreateScope();
                var jobRepo = scope.ServiceProvider.GetRequiredService<IDiscoveryJobRepository>();

                // No dar el job por completado si quedan particiones sin consultar
                // ('Pending'/'Running'): se deja en pausa para reanudarlo (StartAsync
                // rescata las huérfanas) en vez de cerrar con huecos en el inventario.
                var left = await jobRepo.GetStatsAsync(jobId);
                if (left.PendingPartitions > 0 || left.RunningPartitions > 0)
                {
                    logger.LogWarning("Discovery Job {Id}: los workers terminaron con {P} partición(es) 'Pending' y " +
                        "{R} 'Running'. No se marca 'Completed'; queda en 'Paused' para reanudarlo.",
                        jobId, left.PendingPartitions, left.RunningPartitions);
                    await jobRepo.UpdateStatusAsync(jobId, "Paused");
                    return;
                }

                await jobRepo.UpdateStatusAsync(jobId, "Completed");
                logger.LogInformation("Discovery Job {Id} completado", jobId);

                // ── Notificación por correo (v227) ──
                try
                {
                    var job   = await jobRepo.GetByIdAsync(jobId);
                    var stats = await jobRepo.GetStatsAsync(jobId);
                    await scope.ServiceProvider.GetRequiredService<INotificationService>()
                        .RaiseAsync(NotificationEvents.DiscoveryCompleted, job?.Name ?? $"#{jobId}", new (string, string)[]
                        {
                            ("Origen → Destino", $"{job?.SourcePacs?.Alias ?? "?"} → Inventario MOVE"),
                        },
                        jobId, "discovery",
                        kpis: new (string, string)[]
                        {
                            ("Estudios",    stats.StudiesDiscovered.ToString("N0")),
                            ("Nuevos",      stats.StudiesNew.ToString("N0")),
                            ("Particiones", stats.TotalPartitions.ToString("N0")),
                            ("Fallidas",    stats.FailedPartitions.ToString("N0")),
                        });
                }
                catch (Exception nex) { logger.LogWarning(nex, "Notificación de fin de descubrimiento {Id} falló (no crítico).", jobId); }
            }
        }
        catch (OperationCanceledException) { /* expected on Pause */ }
        catch (Exception ex)
        {
            logger.LogError(ex, "Discovery Job {Id} terminó con excepción no controlada", jobId);

            // ── Notificación por correo (v227) ──
            try
            {
                using var nscope = scopeFactory.CreateScope();
                var job = await nscope.ServiceProvider.GetRequiredService<IDiscoveryJobRepository>().GetByIdAsync(jobId);
                await nscope.ServiceProvider.GetRequiredService<INotificationService>()
                    .RaiseAsync(NotificationEvents.DiscoveryFailed, job?.Name ?? $"#{jobId}", new (string, string)[]
                    {
                        ("Origen → Destino", $"{job?.SourcePacs?.Alias ?? "?"} → Inventario MOVE"),
                        ("Error", ex.Message),
                    }, jobId, "discovery");
            }
            catch (Exception nex) { logger.LogWarning(nex, "Notificación de fallo de descubrimiento {Id} falló (no crítico).", jobId); }
        }
        finally
        {
            // Always release the CTS — guarantees no leak on exceptions, completion or cancellation.
            // Retirar SOLO el nuestro: si tras una pausa ya se reanudó el job, la entrada de
            // _running es la de la nueva ejecución y no debemos quitarla ni liberarla.
            if (_running.TryRemove(new KeyValuePair<int, CancellationTokenSource>(jobId, runCts)))
                runCts.Dispose();
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
            bool truncated;
            string? queryError = null;   // motivo concreto del fallo de la consulta, si lo hay
            int unreadable = 0;          // estudios de la respuesta que no se pudieron leer

            if (job.QueryMethod == "QIDO")
            {
                // QIDO-RS admite paginación (offset): se leen todas las páginas de la
                // partición en vez de subdividirla. Así no hace falta ningún filtro de hora
                // ni de modalidad, que dejarían fuera estudios sin StudyTime o con una
                // modalidad no prevista. Solo si el servidor no pagina bien (o hay más de
                // MaxQidoPages páginas) se cae a la subdivisión, con lo leído como muestra.
                var (pages, ok, complete, pageCount, skipped, qidoError) = await QidoAllPagesAsync(dicomWeb, job.SourcePacs, partition, job.PacsResultLimit, ct);
                studies = pages;
                if (!ok) { result = "ERROR"; queryError = qidoError; }
                truncated = !complete;
                unreadable = skipped;
                if (pageCount > 1) filters += $" páginas={pageCount}";
                if (skipped > 0) filters += $" descartados={skipped}";
            }
            else // CFIND
            {
                var find = await dimse.FindAsync(job.SourcePacs, new CFindQuery
                {
                    Level             = "STUDY",
                    StudyDate         = BuildDateForQuery(partition),
                    ModalitiesInStudy = partition.Modality,
                    StudyTime         = BuildTimeForQuery(partition),
                }, ct);
                studies = find.Studies;
                if (!find.Success) result = "ERROR";
                // C-FIND no pagina: si se alcanza el límite, el PACS pudo cortar la respuesta.
                truncated = studies.Count >= job.PacsResultLimit;
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
                Error           = queryError,
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
                partition.LastError      = (job.QueryMethod == "QIDO"
                    ? "Petición QIDO-RS fallida"
                    : "Petición C-FIND fallida")
                    + (queryError is null ? " (ver logs)" : $": {queryError}");
                partition.FinishedAt     = DateTime.UtcNow;
                partition.LockedByWorker = null;
                await jobRepo.UpdatePartitionAsync(partition);
                return;
            }

            // ── Truncation detection ── (calculado arriba según el método de consulta)
            if (truncated && CanSubdivide(partition))
            {
                logger.LogWarning("[{Worker}] Partición posiblemente truncada ({Count} >= {Limit}) — subdividiendo",
                    workerId, studies.Count, job.PacsResultLimit);

                // Persistir la MUESTRA truncada antes de subdividir: los estudios de la
                // primera página quedan guardados aunque tengan StudyTime o modalidad
                // vacíos (que los filtros de las particiones hijas no capturarían). El
                // upsert por StudyInstanceUID deduplica con lo que traigan las hijas.
                var sample = studies
                    .Where(s => !string.IsNullOrEmpty(s.StudyInstanceUid))
                    .Select(s => { var d = MapToDiscovered(s, job); d.PartitionId = partition.Id; return d; })
                    .ToList();
                await studyRepo.UpsertAsync(sample);

                var (children, coverageGap) = Subdivide(partition, jobId, studies);
                if (unreadable > 0)
                    coverageGap = (coverageGap is null ? "" : coverageGap + " ") + UnreadableNote(unreadable);
                await jobRepo.AddPartitionsAsync(children);

                // Si la subdivisión no puede garantizar que cubre todo lo que había más
                // allá del corte (estudios sin hora, o reparto por modalidad), la partición
                // queda como "Posible truncamiento" en vez de "Subdividida": el job no debe
                // parecer completo y limpio cuando puede faltar algún estudio. Las hijas se
                // procesan igual.
                partition.Status   = coverageGap is null ? "Subdivided" : "PossiblyTruncated";
                partition.LastError = $"Truncada con {studies.Count} resultados (límite {job.PacsResultLimit}). Subdividida en {children.Count} particiones."
                                    + (coverageGap is null ? "" : " ATENCIÓN: " + coverageGap);
                if (coverageGap is not null)
                    logger.LogWarning("[{Worker}] Partición {Id} ({Date}): {Gap}", workerId, partition.Id, dateStr, coverageGap);
                partition.FinishedAt = DateTime.UtcNow;
                partition.LockedByWorker = null;
                await jobRepo.UpdatePartitionAsync(partition);
                return;
            }

            // ── Persist studies to inventory ──
            var toInsert = studies
                .Where(s => !string.IsNullOrEmpty(s.StudyInstanceUid))
                .Select(s => { var d = MapToDiscovered(s, job); d.PartitionId = partition.Id; return d; })
                .ToList();

            var (inserted, updated) = await studyRepo.UpsertAsync(toInsert);

            partition.StudiesInserted = inserted;
            partition.StudiesUpdated  = updated;
            // Estudios ilegibles en la respuesta: la partición no está completa aunque no
            // se haya truncado. Queda como "Posible truncamiento" con el motivo, en vez de
            // "Completed" con estudios de menos y sin rastro (DISC-2).
            partition.Status          = truncated || unreadable > 0 ? "PossiblyTruncated" : "Completed";
            partition.FinishedAt      = DateTime.UtcNow;
            partition.LockedByWorker  = null;
            if (truncated)
                partition.LastError = $"Posible truncamiento: {studies.Count} resultados (límite {job.PacsResultLimit}). No se pudo subdividir más.";
            if (unreadable > 0)
            {
                partition.LastError = (truncated ? partition.LastError + " " : "") + UnreadableNote(unreadable);
                logger.LogWarning("[{Worker}] Partición {Id} ({Date}): {Note}", workerId, partition.Id, dateStr, UnreadableNote(unreadable));
            }

            await jobRepo.UpdatePartitionAsync(partition);

            logger.LogInformation("[{Worker}] Partición {Date} {Mod}: {Found} encontrados, {Ins} nuevos, {Upd} actualizados",
                workerId, dateStr, partition.Modality ?? "*", studies.Count, inserted, updated);
        }
        catch (OperationCanceledException)
        {
            // Pausa/parada: NO es un fallo de la partición. Debe ir ANTES del catch
            // genérico, que si no la marcaría "Failed" (AttemptCount++), y como
            // AcquireNextPendingPartitionAsync solo toma 'Pending', esa partición no se
            // reintentaría al reanudar y quedaría perdida. La devolvemos a 'Pending' y
            // liberamos el lock, sin consumir intento (deshaciendo el AttemptCount++ de
            // arriba: una pausa no debe penalizar). UpdatePartitionAsync abre su propio
            // DbContext y no observa el token ya cancelado.
            sw.Stop();
            partition.AttemptCount--;
            partition.Status         = "Pending";
            partition.LockedByWorker = null;
            partition.LockDate       = null;
            try
            {
                await jobRepo.UpdatePartitionAsync(partition);
            }
            catch (Exception relEx)
            {
                // Caso raro (p. ej. BD caída justo al pausar): la partición se queda en
                // 'Running' con lock. Al reanudar el job, StartAsync la rescata
                // (ReleaseOrphanPartitionsAsync) y la devuelve a 'Pending'.
                logger.LogWarning(relEx,
                    "[{Worker}] No se pudo devolver a 'Pending' la partición {Id} tras la " +
                    "cancelación; se rescatará al reanudar el job", workerId, partition.Id);
            }
            throw;   // propaga la cancelación (esperada al pausar; RunWorkersAsync la trata)
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

    private static string UnreadableNote(int n) =>
        $"{n} estudio(s) de la respuesta del PACS no se pudieron leer (sin StudyInstanceUID o con formato " +
        "inesperado) y no están en el inventario. Revisa el log de QIDO-RS y el servidor DICOMweb.";

    // ── Adaptive subdivision logic ──────────────────────────────────────────────
    // Jerarquía de subdivisión (DISC-1). PRIMERO por hora y, solo como último recurso,
    // por modalidad:
    //   Day ─► DayTime (4 franjas de 6 h) ─► DayTime (bisección… hasta 5 min)
    //       ─► DayTimeModality (franja mínima aún truncada: una hija por modalidad)
    // Partir por hora no depende de la modalidad, así que no pierde estudios de
    // modalidades raras (SC, AU…) ni sin modalidad. Antes era al revés (Day ─►
    // DayModality ─► DayModalityTime) y lo de modalidades fuera de la lista se perdía.
    // Los tipos antiguos (DayModality, DayModalityTime) se siguen procesando para los
    // jobs que ya los tenían creados.
    private static bool CanSubdivide(DiscoveryPartition p) => p.PartitionType switch
    {
        "Day"             => true,
        "DayTime"         => true,    // biseca o, en el suelo, reparte por modalidad
        "DayTimeModality" => false,   // último nivel: si sigue truncada, PossiblyTruncated
        "DayModality"     => true,    // (antiguo)
        "DayModalityTime" => TimeWindowSeconds(p) > MinTimeWindowSeconds,   // (antiguo)
        _                 => false,
    };

    /// <summary>Genera las particiones hijas. <c>coverageGap</c> no es null cuando la
    /// subdivisión NO puede garantizar que encuentra todo lo que quedó más allá del corte;
    /// explica por qué, y la partición padre se marca PossiblyTruncated.</summary>
    private static (List<DiscoveryPartition> children, string? coverageGap) Subdivide(
        DiscoveryPartition parent, int jobId, IReadOnlyList<DicomStudyDto> studies)
    {
        var children = new List<DiscoveryPartition>();
        string? gap = null;

        switch (parent.PartitionType)
        {
            case "Day":
            {
                foreach (var (from, to) in TimeRanges)
                    children.Add(TimeChild(parent, jobId, "DayTime", ParseHms(from), ParseHms(to)));

                // Un estudio sin StudyTime no cae en ninguna franja horaria. Los de la
                // muestra ya se han guardado; si la muestra trae alguno, es probable que
                // haya más tras el corte, y esos no los puede encontrar la subdivisión.
                var noTime = studies.Count(s => string.IsNullOrWhiteSpace(s.StudyTime));
                if (noTime > 0)
                    gap = $"{noTime} estudio(s) de la muestra no tienen hora (StudyTime). Los estudios sin hora que " +
                          "hubiera más allá del corte no los encuentra ninguna franja horaria: revisa ese día en el PACS " +
                          "o sube el límite de resultados.";
                break;
            }
            case "DayTime" when TimeWindowSeconds(parent) > MinTimeWindowSeconds:
                Bisect(parent, jobId, "DayTime", children);
                break;
            case "DayTime":
            {
                // Franja mínima aún truncada: repartir por modalidad (base ∪ las vistas en
                // la muestra, troceando ModalitiesInStudy multivaluado), conservando la hora.
                foreach (var mod in ModalitySet(studies))
                {
                    var c = TimeChild(parent, jobId, "DayTimeModality", ParseHms(parent.StudyTimeFrom), ParseHms(parent.StudyTimeTo));
                    c.Modality = mod;
                    children.Add(c);
                }
                gap = $"La franja de {TimeWindowSeconds(parent) / 60} min sigue superando el límite; se ha repartido por " +
                      "modalidad como último recurso. Los estudios sin modalidad, o de una modalidad que no esté en la " +
                      "lista, que hubiera más allá del corte, pueden faltar.";
                break;
            }

            // ── Tipos antiguos (jobs creados antes de este cambio) ──
            case "DayModality":
                foreach (var (from, to) in TimeRanges)
                    children.Add(TimeChild(parent, jobId, "DayModalityTime", ParseHms(from), ParseHms(to)));
                break;
            case "DayModalityTime":
                Bisect(parent, jobId, "DayModalityTime", children);
                break;
        }

        return (children, gap);
    }

    /// <summary>Parte la ventana horaria del padre en dos mitades que comparten el segundo
    /// central (sin hueco para StudyTime con fracción; el upsert deduplica).</summary>
    private static void Bisect(DiscoveryPartition parent, int jobId, string type, List<DiscoveryPartition> children)
    {
        int from = ParseHms(parent.StudyTimeFrom);
        int to   = ParseHms(parent.StudyTimeTo);
        if (to <= from) return;
        int mid = from + (to - from) / 2;
        children.Add(TimeChild(parent, jobId, type, from, mid));
        children.Add(TimeChild(parent, jobId, type, mid, to));
    }

    private static IEnumerable<string> ModalitySet(IReadOnlyList<DicomStudyDto> studies)
    {
        var mods = new HashSet<string>(BaselineModalities, StringComparer.OrdinalIgnoreCase);
        foreach (var s in studies)
            foreach (var m in SplitModalities(s.ModalitiesInStudy))
                if (m.Length <= 16) mods.Add(m);   // 16 = longitud máxima de la columna
        return mods.OrderBy(m => m, StringComparer.Ordinal);
    }

    /// <summary>Lee todas las páginas QIDO-RS de la partición (offset). <c>complete</c> es
    /// false si el servidor no avanza con el offset o se supera MaxQidoPages: entonces lo
    /// leído se usa como muestra y la partición se subdivide.</summary>
    private static async Task<(List<DicomStudyDto> studies, bool ok, bool complete, int pages, int skipped, string? error)> QidoAllPagesAsync(
        IDicomWebService dicomWeb, DicomNode node, DiscoveryPartition partition, int limit, CancellationToken ct)
    {
        var all  = new List<DicomStudyDto>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        limit = Math.Max(1, limit);
        int offset = 0, skipped = 0;

        for (int page = 1; ; page++)
        {
            var qido = await dicomWeb.QidoAsync(node, new QidoQuery
            {
                StudyDate         = BuildDateForQuery(partition),
                // ModalitiesInStudy es el atributo de nivel ESTUDIO; Modality es de serie
                // y un servidor podía ignorarlo o rechazarlo (DISC-6).
                ModalitiesInStudy = partition.Modality,
                StudyTime         = BuildTimeForQuery(partition),
                Limit             = limit,
                Offset            = offset,
                IncludeField      = DiscoveryIncludeFields,
            }, ct);
            if (!qido.Success) return (all, false, false, page, skipped, qido.ErrorMessage);

            skipped += qido.SkippedCount;
            int fresh = 0;
            foreach (var s in qido.Studies)
            {
                if (string.IsNullOrEmpty(s.StudyInstanceUid)) continue;
                if (seen.Add(s.StudyInstanceUid)) { all.Add(s); fresh++; }
            }

            // Página llena o no se mide por los elementos que TRAÍA la respuesta, no por los
            // legibles: si se descartara alguno, una página llena parecería la última.
            var pageItems = qido.ResultCount;
            if (pageItems < limit) return (all, true, true, page, skipped, null);   // última página
            if (fresh == 0 || page >= MaxQidoPages) return (all, true, false, page, skipped, null);   // no pagina bien
            offset += pageItems;
        }
    }

    // ── Helpers de ventana horaria (subdivisión temporal recursiva) ──────────────
    /// <summary>Suelo de bisección: si una ventana de ≤ este tamaño sigue truncándose,
    /// se marca PossiblyTruncated en vez de seguir partiendo (evita partición infinita).</summary>
    private const int MinTimeWindowSeconds = 300;   // 5 minutos

    private static int TimeWindowSeconds(DiscoveryPartition p)
        => ParseHms(p.StudyTimeTo) - ParseHms(p.StudyTimeFrom);

    private static int ParseHms(string? hms)
    {
        if (string.IsNullOrEmpty(hms) || hms.Length < 6) return 0;
        _ = int.TryParse(hms.AsSpan(0, 2), out var h);
        _ = int.TryParse(hms.AsSpan(2, 2), out var m);
        _ = int.TryParse(hms.AsSpan(4, 2), out var s);
        return h * 3600 + m * 60 + s;
    }

    private static string FmtHms(int totalSeconds)
    {
        totalSeconds = Math.Clamp(totalSeconds, 0, 86399);
        return $"{totalSeconds / 3600:D2}{(totalSeconds % 3600) / 60:D2}{totalSeconds % 60:D2}";
    }

    private static DiscoveryPartition TimeChild(DiscoveryPartition parent, int jobId, string type, int fromSec, int toSec) => new()
    {
        DiscoveryJobId = jobId,
        PartitionType  = type,
        StartDate      = parent.StartDate,
        EndDate        = parent.EndDate,
        Modality       = parent.Modality,
        StudyTimeFrom  = FmtHms(fromSec),
        StudyTimeTo    = FmtHms(toSec),
        Status         = "Pending",
    };

    // ── Mapping helpers ─────────────────────────────────────────────────────────
    private static string BuildDateForQuery(DiscoveryPartition p)
    {
        // Single-day partition → exact date; range → DICOM range syntax
        if (p.StartDate is null) return "";
        var start = p.StartDate.Value.ToString("yyyyMMdd");
        if (p.EndDate is null || p.EndDate == p.StartDate) return start;
        return $"{start}-{p.EndDate.Value:yyyyMMdd}";
    }

    /// <summary>Rango horario para la consulta ("HHMMSS-HHMMSS"), o null si la partición
    /// no acota por hora. Se aplica como matching key en C-FIND (StudyTime) y como
    /// parámetro StudyTime en QIDO-RS, de modo que la subdivisión por hora reduce de
    /// verdad el conjunto (antes viajaba solo en la entidad y no llegaba a la consulta).</summary>
    private static string? BuildTimeForQuery(DiscoveryPartition p)
    {
        if (string.IsNullOrEmpty(p.StudyTimeFrom) || string.IsNullOrEmpty(p.StudyTimeTo))
            return null;
        return $"{p.StudyTimeFrom}-{p.StudyTimeTo}";
    }

    /// <summary>Claves de retorno pedidas al PACS por QIDO-RS en el descubrimiento (v207).
    /// Antes no se enviaba includefield y se dependía del conjunto por defecto del servidor,
    /// que varía entre implementaciones. Ahora es explícito y determinista.</summary>
    private const string DiscoveryIncludeFields =
        "0020000D,00080020,00080030,00080050,00080054,00080061,00080080,00081030," +
        "00100010,00100020,00100021,00100030,00100040,00201206,00201208";

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
        // Claves de retorno adicionales (v207). Se normaliza "" → null para que el
        // upsert (que usa ??=) pueda rellenarlas más adelante si otro PACS sí las da.
        StudyTime                     = NullIfEmpty(s.StudyTime),
        InstitutionName               = NullIfEmpty(s.InstitutionName),
        RetrieveAETitle               = NullIfEmpty(s.RetrieveAETitle),
        PatientBirthDate              = NullIfEmpty(s.PatientBirthDate),
        PatientSex                    = NullIfEmpty(s.PatientSex),
        IssuerOfPatientId             = NullIfEmpty(s.IssuerOfPatientId),
        DiscoveryDate                 = DateTime.UtcNow,
        SourcePacsId                  = job.SourcePacsId,
        DiscoveryJobId                = job.Id,
    };

    /// <summary>Cadena vacía → null. Los atributos opcionales que el PACS no soporta
    /// llegan como "" y guardarlos así impediría rellenarlos después (el upsert usa ??=).</summary>
    private static string? NullIfEmpty(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();
}
