using DicomMigrator.Core.Interfaces;
using DicomMigrator.Core.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

// Alias explícito para evitar ambigüedad entre el tipo Migration y el namespace
// DicomMigrator.Infrastructure.Services.Migration en el que vive este fichero.
using MigrationEntity = DicomMigrator.Core.Models.Migration;

namespace DicomMigrator.Infrastructure.Services.Migration;

// ══════════════════════════════════════════════════════════════════════════════
// DISCOVERY SERVICE
// ══════════════════════════════════════════════════════════════════════════════

public class DiscoveryService(
    IMigrationRepository migrationRepo,
    IStudyRepository studyRepo,
    IAuditLogRepository auditRepo,
    IDimseService dimse,
    IDicomWebService dicomWeb,
    ILogger<DiscoveryService> logger) : IDiscoveryService
{
    public async Task<CsvImportResult> DiscoverViaCFindAsync(
        int migrationId, CFindQuery query, CancellationToken ct = default)
    {
        var result = new CsvImportResult();
        var migration = await migrationRepo.GetByIdAsync(migrationId)
            ?? throw new InvalidOperationException($"Migración {migrationId} no encontrada");

        // Split modalities — DICOM C-FIND only supports one modality per request
        // "CT,MR,MG" → three separate C-FIND calls merged into one result set
        var modalities = string.IsNullOrWhiteSpace(query.ModalitiesInStudy)
            ? new[] { (string?)null }   // no filter → one call without modality constraint
            : query.ModalitiesInStudy
                .Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(m => (string?)m.Trim())
                .Where(m => !string.IsNullOrEmpty(m))
                .ToArray();

        var filtersDesc = string.Join(" | ",
            new[]
            {
                string.IsNullOrWhiteSpace(query.StudyDate)       ? null : $"Fecha={query.StudyDate}",
                string.IsNullOrWhiteSpace(query.ModalitiesInStudy) ? null : $"Modalidad={query.ModalitiesInStudy}",
                string.IsNullOrWhiteSpace(query.PatientId)       ? null : $"PatientID={query.PatientId}",
                string.IsNullOrWhiteSpace(query.AccessionNumber) ? null : $"AccessionNumber={query.AccessionNumber}",
            }.Where(f => f is not null));

        logger.LogInformation("C-FIND discovery for migration {Id}. Filtros: {Filters}. Modalidades: {Count} petición(es)",
            migrationId, filtersDesc, modalities.Length);

        await auditRepo.AddAsync(new MigrationAuditLog
        {
            MigrationId   = migrationId,
            Action        = "DISCOVERY",
            Level         = "INFO",
            UserOrProcess = "DISCOVERY",
            TechnicalMessage = $"Iniciando C-FIND discovery. Filtros: {(string.IsNullOrWhiteSpace(filtersDesc) ? "ninguno (todos los estudios)" : filtersDesc)}. {modalities.Length} petición(es) C-FIND.",
        });

        var allStudies = new List<MigrationStudy>();

        foreach (var modality in modalities)
        {
            if (ct.IsCancellationRequested) break;

            // CFindQuery is a class, not a record — clone manually
            var singleQuery = new CFindQuery
            {
                Level             = query.Level,
                PatientId         = query.PatientId,
                PatientName       = query.PatientName,
                StudyDate         = query.StudyDate,
                AccessionNumber   = query.AccessionNumber,
                StudyInstanceUid  = query.StudyInstanceUid,
                SeriesInstanceUid = query.SeriesInstanceUid,
                SopInstanceUid    = query.SopInstanceUid,
                Modality          = query.Modality,
                ModalitiesInStudy = modality,   // one modality per request
                MaxResults        = query.MaxResults,
            };

            try
            {
                var findResult = await dimse.FindAsync(migration.OriginNode!, singleQuery, ct);

                if (!findResult.Success)
                {
                    var errMsg = findResult.ErrorMessage ?? "C-FIND falló";
                    result.Errors.Add(modality is null ? errMsg : $"[{modality}] {errMsg}");
                    logger.LogWarning("C-FIND error for modality {Mod}: {Err}", modality ?? "*", errMsg);
                    continue;
                }

                var studies = findResult.Studies
                    .Where(s => !string.IsNullOrEmpty(s.StudyInstanceUid))
                    .Select(s => new MigrationStudy
                    {
                        StudyInstanceUid    = s.StudyInstanceUid!,
                        PatientId           = s.PatientId,
                        AccessionNumber     = s.AccessionNumber,
                        StudyDate           = s.StudyDate,
                        ModalitiesInStudy   = s.ModalitiesInStudy,
                        SourceSeriesCount   = s.NumberOfSeries,
                        SourceInstanceCount = s.NumberOfInstances,
                        MigrationStatus     = "Pending",
                    }).ToList();

                logger.LogInformation("C-FIND [{Mod}] → {Count} estudios encontrados",
                    modality ?? "*", studies.Count);

                allStudies.AddRange(studies);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "C-FIND error for modality {Mod}", modality ?? "*");
                result.Errors.Add($"[{modality ?? "*"}] {ex.Message}");
            }
        }

        // Deduplicate by StudyInstanceUID (different modality calls may return overlapping results)
        var unique = allStudies
            .GroupBy(s => s.StudyInstanceUid)
            .Select(g => g.First())
            .ToList();

        result.TotalRows    = allStudies.Count;
        result.ValidRows    = unique.Count;
        result.DuplicateRows = allStudies.Count - unique.Count;

        if (unique.Count > 0)
        {
            result.ImportedRows  = await studyRepo.BulkInsertAsync(migrationId, unique);
            result.DuplicateRows += result.ValidRows - result.ImportedRows; // also count DB duplicates
        }

        await auditRepo.AddAsync(new MigrationAuditLog
        {
            MigrationId   = migrationId,
            Action        = "DISCOVERY",
            Level         = result.Errors.Count > 0 ? "WARN" : "INFO",
            Result        = result.Errors.Count > 0 && result.ImportedRows == 0 ? "ERROR" : "OK",
            UserOrProcess = "DISCOVERY",
            TechnicalMessage = $"C-FIND discovery completado. " +
                               $"Encontrados={result.TotalRows} Únicos={result.ValidRows} " +
                               $"Importados={result.ImportedRows} Duplicados={result.DuplicateRows}" +
                               (result.Errors.Count > 0 ? $" Errores={result.Errors.Count}" : ""),
        });

        return result;
    }

    public async Task<CsvImportResult> DiscoverViaQidoAsync(
        int migrationId, QidoQuery query, CancellationToken ct = default)
    {
        var result = new CsvImportResult();
        var migration = await migrationRepo.GetByIdAsync(migrationId)
            ?? throw new InvalidOperationException($"Migración {migrationId} no encontrada");

        logger.LogInformation("QIDO-RS discovery for migration {Id}", migrationId);

        try
        {
            // Paginate until no more results
            var allStudies = new List<MigrationStudy>();
            var offset = 0;
            const int pageSize = 200;

            while (true)
            {
                query.Offset = offset;
                query.Limit  = pageSize;
                var qidoResult = await dicomWeb.QidoAsync(migration.OriginNode!, query, ct);

                if (!qidoResult.Success || qidoResult.Studies.Count == 0) break;

                allStudies.AddRange(qidoResult.Studies.Select(s => new MigrationStudy
                {
                    StudyInstanceUid    = s.StudyInstanceUid ?? string.Empty,
                    PatientId           = s.PatientId,
                    AccessionNumber     = s.AccessionNumber,
                    StudyDate           = s.StudyDate,
                    ModalitiesInStudy   = s.ModalitiesInStudy,
                    SourceSeriesCount   = s.NumberOfSeries,
                    SourceInstanceCount = s.NumberOfInstances,
                    MigrationStatus     = "Pending",
                }).Where(s => !string.IsNullOrEmpty(s.StudyInstanceUid)));

                if (qidoResult.Studies.Count < pageSize) break;
                offset += pageSize;
            }

            result.TotalRows   = allStudies.Count;
            result.ValidRows   = allStudies.Count;
            result.ImportedRows = await studyRepo.BulkInsertAsync(migrationId, allStudies);
            result.DuplicateRows = result.ValidRows - result.ImportedRows;

            await auditRepo.AddAsync(new MigrationAuditLog
            {
                MigrationId = migrationId, Action = "DISCOVERY", Level = "INFO", Result = "OK",
                TechnicalMessage = $"QIDO-RS discovery OK. Encontrados={result.TotalRows} Importados={result.ImportedRows}"
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Discovery QIDO-RS error for migration {Id}", migrationId);
            result.Errors.Add(ex.Message);
        }
        return result;
    }

    public async Task<CsvImportResult> ImportFromCsvAsync(
        int migrationId, Stream csv, CancellationToken ct = default)
    {
        var result = new CsvImportResult();
        var studies = new List<MigrationStudy>();

        try
        {
            using var reader = new System.IO.StreamReader(csv);
            var header = await reader.ReadLineAsync(ct);
            if (header is null) { result.Errors.Add("CSV vacío"); return result; }

            // Parse header columns
            var cols = header.Split(',').Select(c => c.Trim('"', ' ').ToLowerInvariant()).ToArray();
            int uidCol = Array.IndexOf(cols, "studyinstanceuid");
            int pidCol = Array.IndexOf(cols, "patientid");
            int dateCol = Array.IndexOf(cols, "studydate");

            if (uidCol < 0) { result.Errors.Add("Columna StudyInstanceUID requerida"); return result; }

            string? line;
            int row = 0;
            var seenUids = new HashSet<string>();

            while ((line = await reader.ReadLineAsync(ct)) is not null)
            {
                row++;
                result.TotalRows++;
                if (string.IsNullOrWhiteSpace(line)) continue;

                var parts = ParseCsvLine(line);
                if (parts.Length <= uidCol) { result.InvalidRows++; result.Errors.Add($"Línea {row}: columnas insuficientes"); continue; }

                var uid = parts[uidCol].Trim('"', ' ');
                if (string.IsNullOrWhiteSpace(uid)) { result.InvalidRows++; result.Errors.Add($"Línea {row}: StudyInstanceUID vacío"); continue; }
                if (seenUids.Contains(uid)) { result.DuplicateRows++; result.Warnings.Add($"Línea {row}: UID duplicado en CSV"); continue; }

                seenUids.Add(uid);
                result.ValidRows++;

                studies.Add(new MigrationStudy
                {
                    StudyInstanceUid = uid,
                    PatientId  = pidCol  >= 0 && parts.Length > pidCol  ? parts[pidCol].Trim('"', ' ')  : null,
                    StudyDate  = dateCol >= 0 && parts.Length > dateCol ? parts[dateCol].Trim('"', ' ') : null,
                    MigrationStatus = "Pending",
                });
            }

            result.ImportedRows = await studyRepo.BulkInsertAsync(migrationId, studies);
            result.DuplicateRows += result.ValidRows - result.ImportedRows; // DB duplicates

            await auditRepo.AddAsync(new MigrationAuditLog
            {
                MigrationId = migrationId, Action = "IMPORT_CSV", Level = "INFO", Result = "OK",
                TechnicalMessage = $"CSV import OK. Total={result.TotalRows} Válidos={result.ValidRows} Importados={result.ImportedRows} Inválidos={result.InvalidRows} Duplicados={result.DuplicateRows}"
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "CSV import error for migration {Id}", migrationId);
            result.Errors.Add(ex.Message);
        }
        return result;
    }

    private static string[] ParseCsvLine(string line)
    {
        // Simple CSV parser — handles quoted fields with commas
        var parts = new List<string>();
        bool inQuote = false;
        var current = new System.Text.StringBuilder();
        foreach (var c in line)
        {
            if (c == '"') { inQuote = !inQuote; continue; }
            if (c == ',' && !inQuote) { parts.Add(current.ToString()); current.Clear(); continue; }
            current.Append(c);
        }
        parts.Add(current.ToString());
        return parts.ToArray();
    }
}

// ══════════════════════════════════════════════════════════════════════════════
// VERIFICATION SERVICE
// ══════════════════════════════════════════════════════════════════════════════

public class VerificationService(
    IMigrationRepository migrationRepo,
    IDimseService dimse,
    IDicomWebService dicomWeb,
    IServiceScopeFactory scopeFactory,
    IWindowScheduler windowScheduler,
    ILogger<VerificationService> logger) : IVerificationService
{
    public async Task<VerificationResult> VerifyStudyAsync(
        DicomNode destNode, MigrationStudy study, CancellationToken ct = default)
    {
        var result = new VerificationResult();
        try
        {
            // Use QIDO-RS if DICOMweb is enabled and BaseUrl is configured
            var hasQido = destNode.HasDicomWeb
                       && (!string.IsNullOrWhiteSpace(destNode.WebBaseUrl)
                           || !string.IsNullOrWhiteSpace(destNode.QidoBaseUrl));

            if (hasQido)
            {
                logger.LogDebug("Verificando via QIDO-RS · nodo={Node}", destNode.Alias);
                var qido = await dicomWeb.QidoAsync(destNode, new QidoQuery
                {
                    StudyInstanceUid = study.StudyInstanceUid,
                    Limit = 1,
                    IncludeField = "0020000D,00201206,00201208",
                }, ct);

                result.DurationMs = qido.DurationMs;
                result.StudyFoundInDest = qido.Success && qido.Studies.Count > 0;
                // Note: the QIDO HTTP call is already logged by DicomWebService.QidoAsync —
                // no second log line here to avoid duplicate entries per study
                if (result.StudyFoundInDest)
                {
                    var found = qido.Studies[0];
                    result.DestSeriesCount    = found.NumberOfSeries;
                    result.DestInstanceCount  = found.NumberOfInstances;
                    result.SeriesCountMatch   = study.SourceSeriesCount is null
                        || result.DestSeriesCount   == study.SourceSeriesCount;
                    result.InstanceCountMatch = study.SourceInstanceCount is null
                        || result.DestInstanceCount == study.SourceInstanceCount;
                }
                else if (!qido.Success)
                {
                    // La consulta NO se completó (PACS caído, timeout, error HTTP):
                    // fallo de operación, no del estudio. Reintentar.
                    result.ConnectionError = true;
                    result.ErrorMessage = qido.ErrorMessage ?? "No se pudo consultar el destino (QIDO-RS)";
                }
                else
                {
                    result.ErrorMessage = "Estudio no encontrado en destino (QIDO-RS)";
                }
            }
            else
            {
                // DICOMweb deshabilitado o no configurado → C-FIND DIMSE
                logger.LogDebug("Verificando via C-FIND DIMSE · nodo={Node} (DICOMweb deshabilitado)",
                    destNode.Alias);
                var cfind = await dimse.FindAsync(destNode, new CFindQuery
                {
                    StudyInstanceUid = study.StudyInstanceUid,
                }, ct);

                result.DurationMs = cfind.DurationMs;
                result.StudyFoundInDest = cfind.Success && cfind.Studies.Count > 0;
                // C-FIND call already logged by the DIMSE service — no duplicate here
                if (result.StudyFoundInDest)
                {
                    var found = cfind.Studies[0];
                    result.DestInstanceCount  = found.NumberOfInstances;
                    result.DestSeriesCount    = found.NumberOfSeries;
                    result.InstanceCountMatch = study.SourceInstanceCount is null
                        || result.DestInstanceCount == study.SourceInstanceCount;
                    result.SeriesCountMatch   = study.SourceSeriesCount is null
                        || result.DestSeriesCount   == study.SourceSeriesCount;
                }
                else if (!cfind.Success)
                {
                    // La consulta NO se completó (PACS caído, timeout, error de red):
                    // no es un fallo del estudio, sino de la operación. Reintentar.
                    result.ConnectionError = true;
                    result.ErrorMessage = cfind.ErrorMessage ?? "No se pudo consultar el destino (C-FIND)";
                }
                else
                {
                    // La consulta sí respondió, pero el estudio no está → fallo real.
                    result.ErrorMessage = "Estudio no encontrado en destino (C-FIND)";
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Verification error for study {Uid}", study.StudyInstanceUid);
            result.ErrorMessage = ex.Message;
            result.ConnectionError = true;  // excepción = fallo de operación, no del estudio
        }
        return result;
    }

    // ── Proceso de verificación gobernado (Start/Pause/Resume/Stop) ──────────
    // Estático: el servicio es Scoped (necesita repos/dimse Scoped en VerifyStudyAsync),
    // pero el registro de procesos en marcha debe persistir entre peticiones.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, CancellationTokenSource> _verifyCts = new();

    public async Task StartVerificationAsync(int migrationId, CancellationToken ct = default)
    {
        if (_verifyCts.TryGetValue(migrationId, out var existing))
        {
            if (!existing.IsCancellationRequested)
            {
                logger.LogWarning("La verificación de la migración {Id} ya está en marcha", migrationId);
                return;
            }
            // Entrada de una sesión ya cancelada (pausada/parada) cuyo handler aún no
            // ha limpiado: la retiramos para poder reanudar limpiamente.
            _verifyCts.TryRemove(migrationId, out var stale);
            stale?.Dispose();
        }

        var migration = await migrationRepo.GetByIdAsync(migrationId)
            ?? throw new InvalidOperationException($"Migración {migrationId} no encontrada");

        var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _verifyCts[migrationId] = linkedCts;

        await migrationRepo.UpdateVerificationStatusAsync(migrationId, "Running");
        await migrationRepo.SetVerificationAutoPausedAsync(migrationId, false);
        logger.LogInformation("Verificación iniciada · migración {Id} · {Threads} hilo(s)",
            migrationId, Math.Max(1, migration.WorkerThreads));

        var threads = Math.Max(1, migration.WorkerThreads);
        var token = linkedCts.Token;

        var workers = Enumerable.Range(0, threads)
            .Select(i => Task.Run(() => VerificationWorkerLoopAsync(migrationId, $"V{i}", linkedCts), token))
            .ToArray();

        // Handler de finalización: cuando todos los workers salen
        _ = Task.WhenAll(workers).ContinueWith(async t =>
        {
            try
            {
                var wasCancelled = linkedCts.IsCancellationRequested;

                // Solo actuar si ESTE CTS sigue siendo el vigente. Si una reanudación
                // ya registró una sesión nueva, este handler es de una sesión vieja y
                // no debe tocar ni el estado ni el registro de la sesión activa.
                var isCurrent = _verifyCts.TryGetValue(migrationId, out var current) && ReferenceEquals(current, linkedCts);
                if (!isCurrent)
                    return;

                _verifyCts.TryRemove(migrationId, out var removed);
                removed?.Dispose();

                if (wasCancelled)
                {
                    // Pausa/Stop ya fijó el estado; no lo pisamos
                    return;
                }
                // Salida natural: cola de Migrated vacía → Completed.
                // Usar un scope propio: el handler corre mucho después de iniciar,
                // cuando el servicio inyectado podría estar dispuesto.
                using var scope = scopeFactory.CreateScope();
                var migR = scope.ServiceProvider.GetRequiredService<IMigrationRepository>();
                await migR.UpdateVerificationStatusAsync(migrationId, "Completed");
                await migR.SetVerificationAutoPausedAsync(migrationId, false);
                logger.LogInformation("Verificación completada · migración {Id}", migrationId);

                // Promover el estado global de la migración a "Completed" solo si:
                //  - la migración ya terminó de migrar (estado "Migrated", sin fallos), y
                //  - no queda ningún estudio por migrar ni por verificar.
                // Así "Completed" significa "migrado Y verificado", y "Migrated" significa
                // "migrado, pendiente de verificar". Si aún queda trabajo de migración
                // (p. ej. la migración sigue activa y la verificación solo vació una tanda),
                // no se promueve: la verificación se relanzará y volverá a evaluar al acabar.
                var mig = await migR.GetByIdAsync(migrationId);
                if (mig is not null && mig.Status == "Migrated")
                {
                    var st = scope.ServiceProvider.GetRequiredService<IStudyRepository>();
                    var s = await st.GetStatsAsync(migrationId);
                    var pendingWork = s.Pending + s.Queued + s.Migrating
                                    + s.Migrated + s.VerificationPending
                                    + s.RetryPending + s.VerifyRetryPending;
                    if (pendingWork == 0)
                    {
                        await migR.UpdateStatusAsync(migrationId, "Completed");
                        logger.LogInformation("Migración {Id} promovida a Completed (migrada y verificada).", migrationId);
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error en el handler de finalización de verificación · migración {Id}", migrationId);
            }
        }, TaskScheduler.Default);
    }

    public async Task PauseVerificationAsync(int migrationId)
    {
        if (_verifyCts.TryRemove(migrationId, out var cts))
        {
            cts.Cancel();
            cts.Dispose();
        }
        await migrationRepo.UpdateVerificationStatusAsync(migrationId, "Paused");
        await migrationRepo.SetVerificationAutoPausedAsync(migrationId, false);
        logger.LogInformation("Verificación pausada · migración {Id}", migrationId);
    }

    public Task ResumeVerificationAsync(int migrationId, CancellationToken ct = default)
        => StartVerificationAsync(migrationId, ct);

    public async Task StopVerificationAsync(int migrationId)
    {
        if (_verifyCts.TryRemove(migrationId, out var cts))
        {
            cts.Cancel();
            cts.Dispose();
        }
        await migrationRepo.UpdateVerificationStatusAsync(migrationId, "Idle");
        await migrationRepo.SetVerificationAutoPausedAsync(migrationId, false);
        logger.LogInformation("Verificación detenida · migración {Id}", migrationId);
    }

    // Loop de un worker de verificación: adquiere 'Migrated' de uno en uno con el
    // lock de verificación separado, los verifica, reintenta según MaxRetries, y
    // sale cuando la cola está vacía (auto-completado).
    private async Task VerificationWorkerLoopAsync(int migrationId, string workerId, CancellationTokenSource cts)
    {
        var ct = cts.Token;
        int emptyPolls = 0;
        int connErrors = 0;   // errores de conexión consecutivos (PACS destino inaccesible)
        // Caché de ventana por worker (relectura cada 30 s)
        DateTime lastWindowCheck = DateTime.MinValue;
        bool windowOpen = true;

        try
        {
            using var scope = scopeFactory.CreateScope();
            var studyR = scope.ServiceProvider.GetRequiredService<IStudyRepository>();
            var migR   = scope.ServiceProvider.GetRequiredService<IMigrationRepository>();
            var verSvc = scope.ServiceProvider.GetRequiredService<IVerificationService>();
            var auditR = scope.ServiceProvider.GetRequiredService<IAuditLogRepository>();

            var migration = await migR.GetByIdAsync(migrationId);
            if (migration?.DestNode is null)
            {
                logger.LogError("Verificación: migración {Id} sin nodo destino", migrationId);
                return;
            }
            var maxRetries = migration.MaxRetries;
            var retryDelay = migration.RetryDelaySeconds;

            while (!ct.IsCancellationRequested)
            {
                // Respetar ventana de ejecución (con caché de 30 s)
                if ((DateTime.UtcNow - lastWindowCheck).TotalSeconds >= 30)
                {
                    var fresh = await migR.GetByIdAsync(migrationId);
                    windowOpen = fresh?.Window is null || windowScheduler.IsWindowOpen(fresh.Window);
                    lastWindowCheck = DateTime.UtcNow;
                }
                if (!windowOpen)
                {
                    await Task.Delay(30_000, ct);
                    continue;
                }

                var study = await studyR.AcquireNextForVerificationAsync(migrationId, workerId, retryDelay);

                if (study is null)
                {
                    // ¿Queda trabajo? Migrated, VerificationPending (en vuelo) o VerifyRetryPending.
                    var workLeft = await studyR.HasVerificationWorkPendingAsync(migrationId);
                    if (!workLeft)
                    {
                        logger.LogInformation("Verificación worker {W}: cola vacía, saliendo", workerId);
                        break;
                    }
                    emptyPolls++;
                    await Task.Delay(emptyPolls > 3 ? 30_000 : 5_000, ct);
                    continue;
                }
                emptyPolls = 0;

                try
                {
                    var result = await verSvc.VerifyStudyAsync(migration.DestNode, study, ct);

                    // Error de conexión/operación (PACS caído, timeout): NO es fallo del
                    // estudio. Devolverlo a 'Migrated' sin gastar reintento y esperar,
                    // por si el destino está temporalmente inaccesible.
                    if (result.ConnectionError)
                    {
                        await studyR.ReleaseVerificationLockAsync(study.Id);
                        connErrors++;
                        logger.LogWarning("Verificación: no se pudo consultar el destino para {Uid} ({Err}). " +
                            "Estudio devuelto a Migrado. Errores de conexión consecutivos: {N}",
                            study.StudyInstanceUid, result.ErrorMessage, connErrors);

                        // Si se acumulan unos pocos errores seguidos, el destino está caído:
                        // pausar pronto. La auto-reanudación lo retomará al volver la conexión.
                        if (connErrors >= ConnBackoff.AutoPauseThreshold)
                        {
                            logger.LogError("Verificación: {N} errores de conexión consecutivos. " +
                                "Pausando verificación de la migración {Id} (se reanudará sola).", connErrors, migrationId);
                            await migR.UpdateVerificationStatusAsync(migrationId, "Paused");
                            await migR.SetVerificationAutoPausedAsync(migrationId, true);
                            // Cancelar el token compartido: detiene a los demás workers de
                            // inmediato y hace que el handler de finalización NO marque
                            // "Completed" (verá wasCancelled=true).
                            cts.Cancel();
                            break;
                        }
                        await Task.Delay(ConnBackoff.PrePauseWait, ct);
                        continue;
                    }
                    connErrors = 0;  // reset al verificar con éxito de operación

                    var success = result.StudyFoundInDest && result.SeriesCountMatch && result.InstanceCountMatch;

                    await studyR.CompleteVerificationAsync(study.Id, success, maxRetries,
                        result.DestSeriesCount, result.DestInstanceCount,
                        success ? null : (result.ErrorMessage ?? "Conteos no coinciden"));

                    await auditR.AddAsync(new MigrationAuditLog
                    {
                        MigrationId      = migrationId,
                        Action           = "VERIFY",
                        StudyInstanceUid = study.StudyInstanceUid,
                        Level            = success ? "INFO" : "WARN",
                        Result           = success ? "OK" : "ERROR",
                        UserOrProcess    = $"VERIFY-{workerId}",
                        TechnicalMessage = success
                            ? $"Verificado OK. Series={result.DestSeriesCount} Instances={result.DestInstanceCount} ({result.DurationMs}ms)"
                            : result.ErrorMessage,
                    });
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Error verificando {Uid}", study.StudyInstanceUid);
                    // Marcar como fallido/reintento para que salga de la cola
                    await studyR.CompleteVerificationAsync(study.Id, false, maxRetries, null, null, ex.Message);
                }
            }
        }
        catch (OperationCanceledException) { /* parada normal */ }
        catch (Exception ex)
        {
            logger.LogError(ex, "Verificación worker {W} crashed", workerId);
        }
    }
}

// ══════════════════════════════════════════════════════════════════════════════
// MIGRATION WORKER
// ══════════════════════════════════════════════════════════════════════════════

public class MigrationWorker(
    IServiceScopeFactory scopeFactory,
    ILogger<MigrationWorker> logger) : IMigrationWorker
{
    // Track active CancellationTokenSources per migration (Singleton state — OK)
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, CancellationTokenSource> _cts = new();

    // ── Helpers: resolve Scoped services safely from Singleton ────────────────

    private IMigrationRepository MigrationRepo(IServiceScope scope) =>
        scope.ServiceProvider.GetRequiredService<IMigrationRepository>();
    private IStudyRepository StudyRepo(IServiceScope scope) =>
        scope.ServiceProvider.GetRequiredService<IStudyRepository>();
    private IAuditLogRepository AuditRepo(IServiceScope scope) =>
        scope.ServiceProvider.GetRequiredService<IAuditLogRepository>();
    private IDimseService Dimse(IServiceScope scope) =>
        scope.ServiceProvider.GetRequiredService<IDimseService>();
    private IWindowScheduler WindowSched(IServiceScope scope) =>
        scope.ServiceProvider.GetRequiredService<IWindowScheduler>();
    private IConnectionHealthService Health(IServiceScope scope) =>
        scope.ServiceProvider.GetRequiredService<IConnectionHealthService>();

    public async Task StartAsync(int migrationId, CancellationToken ct = default)
    {
        if (_cts.ContainsKey(migrationId))
        {
            logger.LogWarning("Migration {Id} already has active workers", migrationId);
            return;
        }

        MigrationEntity migration;
        using (var scope = scopeFactory.CreateScope())
        {
            migration = await MigrationRepo(scope).GetByIdAsync(migrationId)
                ?? throw new InvalidOperationException($"Migración {migrationId} no encontrada");

            var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _cts[migrationId] = linkedCts;

            await MigrationRepo(scope).UpdateStatusAsync(migrationId, "Running");
            // Acción manual: limpiar el flag de auto-pausa por conexión.
            await MigrationRepo(scope).SetMigrationAutoPausedAsync(migrationId, false);
            await AuditRepo(scope).AddAsync(new MigrationAuditLog
            {
                MigrationId = migrationId, Action = "START", Result = "OK",
                UserOrProcess = "WORKER",
                TechnicalMessage = $"Iniciando {migration.WorkerThreads} workers. Método: {migration.TransferMethod}"
            });
        }

        // Launch worker threads — each creates its own scope
        var priorities = (migration.ModalityPriority ?? "CT,MR,MG,CR,OT").Split(',');
        var cts = _cts[migrationId];
        var tasks = Enumerable.Range(0, migration.WorkerThreads)
            .Select(i => RunWorkerLoopAsync(migration, $"WORKER-{i + 1}", priorities, cts))
            .ToArray();

        // Fire and forget — completion handler runs when ALL workers exit
        // (either cancelled via Pause/Cancel, or naturally when the queue empties)
        _ = Task.WhenAll(tasks).ContinueWith(async _ =>
        {
            // If the token was cancelled, this was a Pause/Cancel — don't override status
            var wasCancelled = cts.IsCancellationRequested;
            _cts.TryRemove(migrationId, out CancellationTokenSource? removedCts);
            removedCts?.Dispose();

            if (wasCancelled) return;  // Pause/Cancel already set the right status

            using var scope = scopeFactory.CreateScope();
            var stats = await StudyRepo(scope).GetStatsAsync(migrationId);
            // Determine final status of the MIGRATION phase:
            // - Failed studies (and nothing recoverable left) → Failed
            // - Everything migrated → Migrated (NOT Completed yet)
            // The migration worker's job is done once nothing is left to MIGRATE;
            // verification is a separate phase. "Completed" is reserved for when
            // verification has also finished (promoted in CompleteVerification handler).
            var finalStatus = stats.Failed > 0 ? "Failed" : "Migrated";
            await MigrationRepo(scope).UpdateStatusAsync(migrationId, finalStatus);
            await AuditRepo(scope).AddAsync(new MigrationAuditLog
            {
                MigrationId = migrationId, Action = "COMPLETE", Result = "OK",
                UserOrProcess = "WORKER",
                TechnicalMessage = $"Migración finalizada. Estado: {finalStatus}. " +
                    $"Migrados={stats.Migrated} Verificados={stats.Verified} Fallidos={stats.Failed}"
            });
            logger.LogInformation("Migration {Id} finished with status {Status}", migrationId, finalStatus);
        }, TaskScheduler.Default);
    }

    private async Task RunWorkerLoopAsync(MigrationEntity migration, string workerId,
        string[] priorities, CancellationTokenSource cts)
    {
        var ct = cts.Token;
        logger.LogInformation("Worker {Id} started for migration {MigId}", workerId, migration.Id);
        int emptyPolls = 0;
        int connErrors = 0;   // errores de conexión consecutivos con el PACS origen

        try
        {
            while (!ct.IsCancellationRequested)
            {
                using var scope = scopeFactory.CreateScope();

                // Respect the execution window — reload migration to pick up window changes
                var freshMig = await MigrationRepo(scope).GetByIdAsync(migration.Id);
                if (freshMig?.Window is not null && !WindowSched(scope).IsWindowOpen(freshMig.Window))
                {
                    // Window closed — wait without processing. The WindowScheduler will
                    // flip the migration to Paused; meanwhile workers idle politely.
                    await Task.Delay(30_000, ct);
                    continue;
                }

                var study = await StudyRepo(scope).AcquireNextPendingAsync(
                    migration.Id, workerId, priorities,
                    migration.RetryDelaySeconds,
                    migration.StartFromDate,
                    migration.OldestFirst);

                if (study is null)
                {
                    // Nothing acquired. Check whether the queue is definitively empty:
                    // no Pending, nothing Migrating (in-flight on another worker), and
                    // no RetryPending waiting for its delay. If so, this worker is done.
                    var stats = await StudyRepo(scope).GetStatsAsync(migration.Id);
                    var workLeft = stats.Pending > 0 || stats.Migrating > 0 || stats.RetryPending > 0;
                    if (!workLeft)
                    {
                        logger.LogInformation("Worker {Id} sees empty queue — exiting", workerId);
                        break;
                    }

                    emptyPolls++;
                    // Backoff: 5s normally, 30s if we've been empty many times
                    // (remaining studies are in RetryPending delay)
                    var delay = emptyPolls > 3 ? 30_000 : 5_000;
                    await Task.Delay(delay, ct);
                    continue;
                }

                emptyPolls = 0;
                var wasConnError = await MigrateStudyAsync(migration, study, workerId, ct);

                if (wasConnError)
                {
                    connErrors++;
                    // Si se acumulan unos pocos errores de conexión seguidos, el destino
                    // (o el origen) está caído: pausar pronto para no machacar en bucle.
                    // El servicio de auto-reanudación lo retomará cuando vuelva la conexión.
                    if (connErrors >= ConnBackoff.AutoPauseThreshold)
                    {
                        logger.LogError("Migración: {N} errores de conexión consecutivos. " +
                            "Pausando migración {Id} (se reanudará sola al recuperarse la conexión).",
                            connErrors, migration.Id);
                        await MigrationRepo(scope).UpdateStatusAsync(migration.Id, "Paused");
                        await MigrationRepo(scope).SetMigrationAutoPausedAsync(migration.Id, true);
                        // Cancelar el token compartido: detiene a los demás workers de
                        // inmediato (no esperan a llegar cada uno al umbral) y hace que el
                        // handler de finalización NO marque "Completed" (wasCancelled=true).
                        cts.Cancel();
                        break;
                    }
                    // Espera corta y fija mientras acumulamos hacia el umbral, para que
                    // la auto-pausa se dispare en un tiempo razonable (no un backoff largo
                    // que retrasaría la pausa varios minutos).
                    await Task.Delay(ConnBackoff.PrePauseWait, ct);
                }
                else
                {
                    connErrors = 0;
                }
            }
        }
        catch (OperationCanceledException) { /* normal shutdown */ }
        catch (Exception ex)
        { logger.LogError(ex, "Worker {Id} crashed", workerId); }
        finally
        {
            using var scope = scopeFactory.CreateScope();
            await StudyRepo(scope).ReleaseLocksAsync(workerId);
            logger.LogInformation("Worker {Id} stopped", workerId);
        }
    }

    /// <summary>Migrate one study. Returns true if it failed due to a SOURCE connection
    /// error (transient — study returned to Pending), false otherwise.</summary>
    private async Task<bool> MigrateStudyAsync(MigrationEntity migration, MigrationStudy study,
        string workerId, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var studyRepo = StudyRepo(scope);
        var auditRepo = AuditRepo(scope);
        var dimse     = Dimse(scope);

        // Reload migration with fresh node data — picks up any config changes made while paused
        var freshMigration = await MigrationRepo(scope).GetByIdAsync(migration.Id) ?? migration;

        await studyRepo.UpdateStatusAsync(study.Id, "Migrating");
        logger.LogInformation("[{Worker}] Migrating {Uid}", workerId, study.StudyInstanceUid);

        try
        {
            var request = new CMoveRequest
            {
                Level            = "STUDY",
                StudyInstanceUid = study.StudyInstanceUid,
                DestinationAet   = freshMigration.DestNode!.RemoteAet,
            };

            var result = await dimse.MoveAsync(freshMigration.OriginNode!, request, ct);

            // C-MOVE hacia otro PACS: éxito = Completed > 0 y Failed == 0.
            // ReceivedCount siempre es 0 porque los C-STORE van directo del origen al destino.
            var success = result.Failed == 0
                       && result.Completed > 0
                       && (result.DicomStatus == 0x0000
                           || result.DicomStatus == 0xFF00
                           || result.DicomStatus == 0xFF01
                           || result.Success);

            var techMsg = $"Status=0x{result.DicomStatus:X4} " +
                          $"Completed={result.Completed} Received={result.ReceivedCount} " +
                          $"Failed={result.Failed} Warning={result.Warning} {result.DurationMs}ms";

            if (!success && result.Completed == 0 && result.DicomStatus == null)
            {
                // Sin respuesta alguna — timeout o asociación rechazada
                techMsg = result.ErrorMessage ?? "Sin respuesta del PACS origen";
            }
            else if (!success && result.Completed == 0)
            {
                techMsg += " — El PACS origen no procesó ninguna instancia. Verifica que el StudyInstanceUID existe en el origen.";
            }

            logger.LogInformation("[{Worker}] C-MOVE result: {Msg} StudyUID={Uid}",
                workerId, techMsg, study.StudyInstanceUid);

            if (success)
            {
                await studyRepo.UpdateStatusAsync(study.Id, "Migrated");
                await auditRepo.AddAsync(new MigrationAuditLog
                {
                    MigrationId      = migration.Id,
                    Action           = "C-MOVE",
                    Level            = "INFO",
                    Result           = "OK",
                    StudyInstanceUid = study.StudyInstanceUid,
                    UserOrProcess    = workerId,
                    TechnicalMessage = techMsg,
                });
                return false;  // no fue error de conexión
            }
            else if (result.ConnectionError)
            {
                // El PACS ORIGEN no se pudo alcanzar (asociación rechazada, conexión
                // perdida, timeout): NO es fallo del estudio. Devolverlo a Pending sin
                // gastar reintento, para reintentar cuando el origen vuelva.
                await studyRepo.ReleaseMigrationLockAsync(study.Id);
                logger.LogWarning("[{Worker}] No se pudo conectar con el origen para {Uid} ({Err}). " +
                    "Estudio devuelto a Pendiente.", workerId, study.StudyInstanceUid, result.ErrorMessage ?? techMsg);
                await auditRepo.AddAsync(new MigrationAuditLog
                {
                    MigrationId      = migration.Id,
                    Action           = "C-MOVE",
                    Level            = "WARN",
                    Result           = "ERROR",
                    StudyInstanceUid = study.StudyInstanceUid,
                    UserOrProcess    = workerId,
                    TechnicalMessage = $"Error de conexión con el origen (reintento sin penalizar): {techMsg}",
                });
                return true;   // fue error de conexión
            }
            else if (result.Completed == 0)
            {
                // Caso ambiguo: el C-MOVE no procesó nada (Status 0xC000). Puede ser
                // (a) el estudio no existe en el origen → fallo real, o
                // (b) el DESTINO está caído y el origen no pudo entregarlo → transitorio.
                // Opción A: sondear el destino con C-ECHO para distinguir.
                var destHealth = await Health(scope).ProbeNodeAsync(freshMigration.DestNode!, ct);
                if (!destHealth.Reachable)
                {
                    // El destino no responde: es transitorio. Devolver a Pending sin penalizar.
                    await studyRepo.ReleaseMigrationLockAsync(study.Id);
                    logger.LogWarning("[{Worker}] El destino {Dest} no responde (C-ECHO). El C-MOVE de {Uid} " +
                        "no pudo entregarse. Estudio devuelto a Pendiente.",
                        workerId, freshMigration.DestNode!.Alias, study.StudyInstanceUid);
                    await auditRepo.AddAsync(new MigrationAuditLog
                    {
                        MigrationId      = migration.Id,
                        Action           = "C-MOVE",
                        Level            = "WARN",
                        Result           = "ERROR",
                        StudyInstanceUid = study.StudyInstanceUid,
                        UserOrProcess    = workerId,
                        TechnicalMessage = $"Destino inaccesible (reintento sin penalizar): {techMsg}",
                    });
                    return true;   // tratar como error de conexión (transitorio)
                }
                // El destino SÍ responde → el estudio realmente no se pudo migrar.
                var currentStudy = await studyRepo.GetByIdAsync(study.Id);
                var retries      = (currentStudy?.RetryCount ?? study.RetryCount) + 1;
                var nextStatus   = retries >= migration.MaxRetries ? "Failed" : "RetryPending";

                await studyRepo.UpdateStatusAsync(study.Id, nextStatus,
                    $"[Intento {retries}/{migration.MaxRetries}] {techMsg}");

                await auditRepo.AddAsync(new MigrationAuditLog
                {
                    MigrationId      = migration.Id,
                    Action           = "C-MOVE",
                    Level            = nextStatus == "Failed" ? "ERROR" : "WARN",
                    Result           = "ERROR",
                    StudyInstanceUid = study.StudyInstanceUid,
                    UserOrProcess    = workerId,
                    TechnicalMessage = $"Fallo intento {retries}/{migration.MaxRetries} (destino accesible). {techMsg}",
                });
                return false;
            }
            else
            {
                // Fallo real del estudio: el origen respondió y procesó, pero alguna
                // instancia falló (Failed > 0). El destino está operativo.
                var currentStudy = await studyRepo.GetByIdAsync(study.Id);
                var retries      = (currentStudy?.RetryCount ?? study.RetryCount) + 1;
                var nextStatus   = retries >= migration.MaxRetries ? "Failed" : "RetryPending";

                await studyRepo.UpdateStatusAsync(study.Id, nextStatus,
                    $"[Intento {retries}/{migration.MaxRetries}] {techMsg}");

                await auditRepo.AddAsync(new MigrationAuditLog
                {
                    MigrationId      = migration.Id,
                    Action           = "C-MOVE",
                    Level            = nextStatus == "Failed" ? "ERROR" : "WARN",
                    Result           = "ERROR",
                    StudyInstanceUid = study.StudyInstanceUid,
                    UserOrProcess    = workerId,
                    TechnicalMessage = $"Fallo intento {retries}/{migration.MaxRetries}. {techMsg}",
                });
                return false;
            }
        }
        catch (OperationCanceledException)
        {
            // Cancelación (pausa/parada): devolver el estudio a Pending sin penalizar.
            await studyRepo.ReleaseMigrationLockAsync(study.Id);
            throw;
        }
        catch (Exception ex)
        {
            // Excepción inesperada de red/transporte = error de conexión: reintentar.
            await studyRepo.ReleaseMigrationLockAsync(study.Id);
            logger.LogWarning(ex, "[{Worker}] Error de conexión migrando {Uid}. Estudio devuelto a Pendiente.",
                workerId, study.StudyInstanceUid);
            return true;
        }
    }

    public async Task PauseAsync(int migrationId)
    {
        if (_cts.TryRemove(migrationId, out var cts))
        {
            await cts.CancelAsync();
            cts.Dispose();
        }
        using var scope = scopeFactory.CreateScope();
        await MigrationRepo(scope).UpdateStatusAsync(migrationId, "Paused");
        // Pausa/parada manual: limpiar el flag de auto-pausa (no debe auto-reanudar).
        await MigrationRepo(scope).SetMigrationAutoPausedAsync(migrationId, false);
        await AuditRepo(scope).AddAsync(new MigrationAuditLog
        {
            MigrationId = migrationId, Action = "PAUSE", Result = "OK",
            UserOrProcess = "SYSTEM", TechnicalMessage = "Workers detenidos. Migración pausada."
        });
    }

    public Task ResumeAsync(int migrationId, CancellationToken ct = default)
        => StartAsync(migrationId, ct);
}

// ══════════════════════════════════════════════════════════════════════════════
// WINDOW SCHEDULER
// ══════════════════════════════════════════════════════════════════════════════

public class WindowScheduler(
    IServiceScopeFactory scopeFactory,
    IMigrationWorker worker,
    ILogger<WindowScheduler> logger) : IWindowScheduler
{
    private readonly Dictionary<int, bool> _lastWindowState = new();

    public bool IsWindowOpen(ExecutionWindow window)
    {
        try
        {
            var tz = TimeZoneInfo.FindSystemTimeZoneById(window.TimeZoneId);
            var now = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, tz);
            var currentTime = new TimeOnly(now.Hour, now.Minute);
            var dayOfWeek = (int)now.DayOfWeek;
            var isoDow = dayOfWeek == 0 ? 7 : dayOfWeek;

            var enabledDays = window.EnabledDays.Split(',')
                .Select(d => int.TryParse(d.Trim(), out var n) ? n : -1)
                .ToHashSet();

            if (!enabledDays.Contains(isoDow)) return false;

            return window.CrossesMidnight
                ? currentTime >= window.StartTime || currentTime <= window.EndTime
                : currentTime >= window.StartTime && currentTime <= window.EndTime;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error evaluating window for timezone {Tz}", window.TimeZoneId);
            return false;
        }
    }

    public async Task RunAsync(CancellationToken ct = default)
    {
        logger.LogInformation("Window scheduler started");
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var migrationRepo = scope.ServiceProvider.GetRequiredService<IMigrationRepository>();
                var auditRepo     = scope.ServiceProvider.GetRequiredService<IAuditLogRepository>();

                var migrations = await migrationRepo.GetAllAsync();

                // Prune _lastWindowState entries for migrations that no longer exist.
                // Without this, deleted migrations leave permanent entries in the dictionary
                // and the dictionary grows over the process lifetime.
                var liveIds = migrations.Select(m => m.Id).ToHashSet();
                foreach (var orphanId in _lastWindowState.Keys.Where(k => !liveIds.Contains(k)).ToList())
                    _lastWindowState.Remove(orphanId);

                foreach (var m in migrations.Where(m => m is { Status: "Running" or "Paused", Window: not null }))
                {
                    var open = IsWindowOpen(m.Window!);
                    // Default to "was open" so that a migration started OUTSIDE its window
                    // is detected as a close transition on the first tick and gets paused.
                    // Without this, the first tick initializes wasOpen=open and the
                    // close transition is never detected.
                    var wasOpen = _lastWindowState.GetValueOrDefault(m.Id, true);

                    if (open && !wasOpen)
                    {
                        logger.LogInformation("Window OPENED for migration {Id}", m.Id);
                        await auditRepo.AddAsync(new MigrationAuditLog
                        {
                            MigrationId = m.Id, Action = "WINDOW_OPEN", Result = "OK",
                            UserOrProcess = "SCHEDULER",
                            TechnicalMessage = "Ventana abierta. Reanudando workers."
                        });
                        if (m.Status == "Paused")
                            await worker.ResumeAsync(m.Id, ct);
                    }
                    else if (!open && wasOpen)
                    {
                        logger.LogInformation("Window CLOSED for migration {Id}", m.Id);
                        await auditRepo.AddAsync(new MigrationAuditLog
                        {
                            MigrationId = m.Id, Action = "WINDOW_CLOSE", Result = "OK",
                            UserOrProcess = "SCHEDULER",
                            TechnicalMessage = "Ventana cerrada. Pausando workers."
                        });
                        if (m.Status == "Running")
                            await worker.PauseAsync(m.Id);
                    }

                    _lastWindowState[m.Id] = open;
                }
            }
            catch (Exception ex)
            { logger.LogError(ex, "Scheduler error"); }

            await Task.Delay(TimeSpan.FromMinutes(1), ct);
        }
    }
}

// ══════════════════════════════════════════════════════════════════════════════
// BACKOFF HELPER
// ══════════════════════════════════════════════════════════════════════════════

internal static class ConnBackoff
{
    /// <summary>Consecutive connection errors before a process auto-pauses. Kept low
    /// so the pause fires within ~1 minute of the PACS going down, not 20.</summary>
    public const int AutoPauseThreshold = 5;

    /// <summary>Short fixed wait between connection errors while counting toward the
    /// auto-pause threshold. Fixed (not exponential) so reaching the threshold is fast.</summary>
    public static readonly TimeSpan PrePauseWait = TimeSpan.FromSeconds(10);

    /// <summary>Exponential backoff for consecutive connection errors: 10s, 20s,
    /// 40s, 80s... capped at 5 minutes. consecutiveErrors starts at 1.</summary>
    public static TimeSpan ForAttempt(int consecutiveErrors)
    {
        var n = Math.Max(1, consecutiveErrors);
        // 10s * 2^(n-1), capped at 300s
        var seconds = Math.Min(300d, 10d * Math.Pow(2, n - 1));
        return TimeSpan.FromSeconds(seconds);
    }
}
