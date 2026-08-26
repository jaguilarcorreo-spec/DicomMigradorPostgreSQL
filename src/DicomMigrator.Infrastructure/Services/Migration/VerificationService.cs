using DicomMigrator.Core.Interfaces;
using DicomMigrator.Core.Models;
using DicomMigrator.Infrastructure.Services.Licensing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

// Alias explícito para evitar ambigüedad entre el tipo Migration y el namespace
// DicomMigrator.Infrastructure.Services.Migration en el que vive este fichero.
using MigrationEntity = DicomMigrator.Core.Models.Migration;

namespace DicomMigrator.Infrastructure.Services.Migration;

// ══════════════════════════════════════════════════════════════════════════════
// VERIFICATION SERVICE
// ══════════════════════════════════════════════════════════════════════════════

public class VerificationService(
    IMigrationRepository migrationRepo,
    IDimseService dimse,
    IDicomWebService dicomWeb,
    IInstanceRepository instanceRepo,
    IServiceScopeFactory scopeFactory,
    IWindowScheduler windowScheduler,
    ILogger<VerificationService> logger) : IVerificationService
{
    /// <summary>Cancela TODAS las verificaciones activas de inmediato, sin tocar el estado
    /// en BD. Para el apagado del proceso: se reanudan solas al reiniciar.</summary>
    public void CancelAllForShutdown()
    {
        foreach (var kv in _verifyCts)
        {
            try { kv.Value.Cancel(); } catch { /* ignorar */ }
        }
    }

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
                    // fallo de operación, no del estudio. Reintentar. Salvo que sea un
                    // 401/403 (credenciales rechazadas): eso es un problema de
                    // CONFIGURACIÓN permanente, no una caída transitoria del destino.
                    result.ConnectionError = true;
                    result.ConfigurationError = qido.ConfigurationError;
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
                    // no es un fallo del estudio, sino de la operación. Reintentar. Salvo
                    // que el PACS destino haya RECHAZADO la asociación (AE Title no
                    // autorizado): eso es un problema de CONFIGURACIÓN permanente.
                    result.ConnectionError = true;
                    result.ConfigurationError = cfind.ConfigurationError;
                    result.ErrorMessage = cfind.ErrorMessage ?? "No se pudo consultar el destino (C-FIND)";
                }
                else
                {
                    // La consulta sí respondió, pero el estudio no está → fallo real.
                    result.ErrorMessage = "Estudio no encontrado en destino (C-FIND)";
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Cancelación real (pausa/parada del usuario): propagarla para que
            // VerificationWorkerLoopAsync la trate como tal, no como fallo de conexión
            // (si se tragara aquí como ConnectionError, una pausa manual generaría
            // "errores de conexión consecutivos" y podría disparar una auto-pausa
            // espuria con su notificación por correo).
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Verification error for study {Uid}", study.StudyInstanceUid);
            result.ErrorMessage = ex.Message;
            result.ConnectionError = true;  // excepción = fallo de operación, no del estudio
        }

        // ── Nivel 2: comparación de conjuntos de UIDs ──────────────────────────
        // Solo si el estudio tiene UIDs de origen capturados (MigrationInstances) y la
        // consulta de conteos no falló por conexión. Enumera el destino por C-FIND
        // IMAGE y compara conjuntos: aprobado ⇔ no faltan UIDs (los sobrantes solo avisan).
        if (!result.ConnectionError)
        {
            try
            {
                var srcUids = await instanceRepo.GetSopUidsForStudyAsync(study.Id);
                if (srcUids.Count > 0)
                {
                    // 4A: enumerar el destino por QIDO-instances si tiene DICOMweb; si no, C-FIND IMAGE.
                    var useQido = destNode.HasDicomWeb &&
                                  !string.IsNullOrWhiteSpace(destNode.WebBaseUrl ?? destNode.QidoBaseUrl);
                    var enumRes = useQido
                        ? await dicomWeb.EnumerateInstancesAsync(destNode, study.StudyInstanceUid, ct)
                        : await dimse.EnumerateInstancesAsync(destNode, study.StudyInstanceUid, ct);
                    if (!enumRes.Success)
                    {
                        // No se pudo enumerar el destino → fallo de operación (reintentar),
                        // no del estudio. Salvo que sea un problema de CONFIGURACIÓN
                        // permanente (credenciales/AE Title rechazados).
                        result.ConnectionError = true;
                        result.ConfigurationError = enumRes.ConfigurationError;
                        result.ErrorMessage = enumRes.ErrorMessage ?? "No se pudo enumerar el destino (C-FIND IMAGE).";
                    }
                    else
                    {
                        var dstUids = enumRes.Instances
                            .Select(i => i.SopInstanceUid)
                            .Where(u => !string.IsNullOrEmpty(u))
                            .ToHashSet();

                        var missing = new HashSet<string>(srcUids);
                        missing.ExceptWith(dstUids);
                        var extra = new HashSet<string>(dstUids);
                        extra.ExceptWith(srcUids);

                        result.Level2Checked  = true;
                        result.SourceUidCount = srcUids.Count;
                        result.DestUidCount   = dstUids.Count;
                        result.MissingCount   = missing.Count;
                        result.ExtraCount     = extra.Count;
                        result.MissingUids    = missing.Take(500).ToList();   // informe (capado)

                        // Traza siempre visible: confirma que se comparó por conjuntos y con qué números.
                        logger.LogInformation("Nivel 2 · estudio {Uid}: origen={Src} destino={Dst} faltan={Miss} sobran={Extra}",
                            study.StudyInstanceUid, srcUids.Count, dstUids.Count, missing.Count, extra.Count);
                        if (extra.Count > 0)
                            logger.LogWarning("Nivel 2 · estudio {Uid}: {N} UIDs sobrantes en destino",
                                study.StudyInstanceUid, extra.Count);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Cancelación real: propagarla (ver comentario en el catch de arriba).
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Nivel 2 · error enumerando/comparando destino para {Uid}", study.StudyInstanceUid);
                result.ErrorMessage = ex.Message;
                result.ConnectionError = true;   // tratar como fallo de operación → reintento
            }
        }

        // Qué comprobación se aplicó realmente. Es el dato que hace honesto el
        // "Verified": sin él, un estudio sin conteos de origen conocidos aprueba
        // por la tolerancia de más abajo sin haberse comparado nada.
        result.VerifiedBy = result.Level2Checked
            ? "UidSet"
            : (study.SourceSeriesCount is not null || study.SourceInstanceCount is not null)
                ? "Counts"
                : "ExistenceOnly";

        return result;
    }

    // ── Proceso de verificación gobernado (Start/Pause/Resume/Stop) ──────────
    // Estático: el servicio es Scoped (necesita repos/dimse Scoped en VerifyStudyAsync),
    // pero el registro de procesos en marcha debe persistir entre peticiones.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, CancellationTokenSource> _verifyCts = new();

    public async Task StartVerificationAsync(int migrationId, CancellationToken ct = default)
    {
        // Rearma el aviso de auto-pausa: si vuelve a caerse el destino, se registrará.
        ConnBackoff.ResetPauseAnnouncement("VERIFY", migrationId);

        // Guardia de arranque ATÓMICA (mismo patrón que DiscoveryEngine/MigrationWorker):
        // reserva el slot en _verifyCts en un solo paso. Antes había una ventana entre
        // "comprobar" (TryGetValue) y "reservar" (asignación) en la que dos llamadas
        // concurrentes a StartVerificationAsync (doble clic, o UI + auto-reanudación al
        // arrancar el servicio) podían pasar ambas la comprobación y lanzar dos juegos de
        // workers, perdiendo el CancellationTokenSource de la primera (fuga + trabajo
        // duplicado). TryAdd/TryUpdate hacen el "comprobar y reservar" atómicamente.
        var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        while (!_verifyCts.TryAdd(migrationId, linkedCts))
        {
            if (!_verifyCts.TryGetValue(migrationId, out var existing))
                continue; // el registro desapareció justo ahora (otra sesión terminó) — reintentar

            if (!existing.IsCancellationRequested)
            {
                linkedCts.Dispose();
                logger.LogWarning("La verificación de la migración {Id} ya está en marcha", migrationId);
                return;
            }

            // Entrada de una sesión ya cancelada (pausada/parada) cuyo handler aún no ha
            // limpiado: la reemplazamos atómicamente. Si cambió entretanto (otra llamada
            // nos ganó), reintentamos el bucle.
            if (_verifyCts.TryUpdate(migrationId, linkedCts, existing))
                break;
        }

        MigrationEntity migration;
        try
        {
            migration = await migrationRepo.GetByIdAsync(migrationId)
                ?? throw new InvalidOperationException($"Migración {migrationId} no encontrada");

            await migrationRepo.UpdateVerificationStatusAsync(migrationId, "Running");
            await migrationRepo.SetVerificationAutoPausedAsync(migrationId, false);
        }
        catch
        {
            // El arranque falló ANTES de lanzar los workers: liberar el slot reservado
            // para no dejar la verificación bloqueada (el continuation de abajo no
            // llegará a ejecutarse).
            if (_verifyCts.TryRemove(migrationId, out var reserved))
                reserved.Dispose();
            throw;
        }

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

                var mig = await migR.GetByIdAsync(migrationId);
                var st  = scope.ServiceProvider.GetRequiredService<IStudyRepository>();
                var s   = await st.GetStatsAsync(migrationId);

                // ── Notificación por correo (v228): fin del proceso de verificación ──
                // Si hubo algún fallo de verificación se manda el evento "con fallos";
                // si no, el de "completada". Encolar nunca rompe la finalización.
                try
                {
                    var kind = s.VerifyFailed > 0
                        ? NotificationEvents.VerificationFailed
                        : NotificationEvents.VerificationCompleted;
                    await scope.ServiceProvider.GetRequiredService<INotificationService>()
                        .RaiseAsync(kind, mig?.Name ?? $"#{migrationId}", new (string, string)[]
                        {
                            ("Origen → Destino", $"{mig?.OriginNode?.Alias ?? "?"} → {mig?.DestNode?.Alias ?? "?"}"),
                        },
                        migrationId, "migration",
                        kpis: new (string, string)[]
                        {
                            ("Verificados OK", s.Verified.ToString("N0")),
                            ("Con fallos",     s.VerifyFailed.ToString("N0")),
                            ("Total",          s.Total.ToString("N0")),
                        });
                }
                catch (Exception nex) { logger.LogWarning(nex, "Notificación de fin de verificación (migración {Id}) falló (no crítico).", migrationId); }

                // Promover el estado global de la migración a "Completed" solo si:
                //  - la migración ya terminó de migrar (estado "Migrated", sin fallos), y
                //  - no queda ningún estudio por migrar ni por verificar.
                // Así "Completed" significa "migrado Y verificado", y "Migrated" significa
                // "migrado, pendiente de verificar". Si aún queda trabajo de migración
                // (p. ej. la migración sigue activa y la verificación solo vació una tanda),
                // no se promueve: la verificación se relanzará y volverá a evaluar al acabar.
                if (mig is not null && mig.Status == "Migrated")
                {
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
                    windowOpen = windowScheduler.IsWindowOpen(fresh?.Windows);
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

                        if (result.ConfigurationError)
                        {
                            // Problema de CONFIGURACIÓN permanente (credenciales/AE Title
                            // rechazados por el destino) — a diferencia de una caída de red,
                            // reintentar nunca lo arregla solo. Pausar de inmediato (sin
                            // esperar a acumular AutoPauseThreshold fallos) y decirlo con
                            // honestidad en el log/notificación, en vez de "error de conexión".
                            logger.LogError("Verificación: error de CONFIGURACIÓN (no de conexión) con el " +
                                "destino para {Uid} ({Err}). Pausando verificación de la migración {Id} de " +
                                "inmediato — revisa las credenciales/AE Title del nodo destino.",
                                study.StudyInstanceUid, result.ErrorMessage, migrationId);

                            if (ConnBackoff.TryAnnouncePause("VERIFY", migrationId))
                            {
                                await migR.UpdateVerificationStatusAsync(migrationId, "Paused");
                                await migR.SetVerificationAutoPausedAsync(migrationId, true);
                                try
                                {
                                    await scope.ServiceProvider.GetRequiredService<INotificationService>()
                                        .RaiseAsync(NotificationEvents.AutoPaused, migration.Name, new (string, string)[]
                                        {
                                            ("Origen → Destino", $"{migration.OriginNode?.Alias ?? "?"} → {migration.DestNode?.Alias ?? "?"}"),
                                            ("Proceso", "Verificación"),
                                            ("Motivo",  "Error de configuración (credenciales/AE Title rechazados por el destino)"),
                                            ("Nodo",    migration.DestNode?.Alias ?? "destino"),
                                        }, migrationId, "migration");
                                }
                                catch (Exception nex) { logger.LogWarning(nex, "Notificación de auto-pausa (verificación {Id}) falló.", migrationId); }
                            }
                            cts.Cancel();
                            break;
                        }

                        connErrors++;
                        logger.LogWarning("Verificación: no se pudo consultar el destino para {Uid} ({Err}). " +
                            "Estudio devuelto a Migrado. Errores de conexión consecutivos: {N}",
                            study.StudyInstanceUid, result.ErrorMessage, connErrors);

                        // Si se acumulan unos pocos errores seguidos, el destino está caído:
                        // pausar pronto. La auto-reanudación lo retomará al volver la conexión.
                        if (connErrors >= ConnBackoff.AutoPauseThreshold)
                        {
                            if (ConnBackoff.TryAnnouncePause("VERIFY", migrationId))
                            {
                                logger.LogError("Verificación: {N} errores de conexión consecutivos. " +
                                    "Pausando verificación de la migración {Id} (se reanudará sola).", connErrors, migrationId);
                                await migR.UpdateVerificationStatusAsync(migrationId, "Paused");
                                await migR.SetVerificationAutoPausedAsync(migrationId, true);
                                try
                                {
                                    await scope.ServiceProvider.GetRequiredService<INotificationService>()
                                        .RaiseAsync(NotificationEvents.AutoPaused, migration.Name, new (string, string)[]
                                        {
                                            ("Origen → Destino", $"{migration.OriginNode?.Alias ?? "?"} → {migration.DestNode?.Alias ?? "?"}"),
                                            ("Proceso", "Verificación"),
                                            ("Motivo",  $"{connErrors} errores de conexión con el destino"),
                                            ("Nodo",    migration.DestNode?.Alias ?? "destino"),
                                        }, migrationId, "migration");
                                }
                                catch (Exception nex) { logger.LogWarning(nex, "Notificación de auto-pausa (verificación {Id}) falló.", migrationId); }
                            }
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

                    // Nivel 2 (si se comparó por conjuntos): aprobado ⇔ estudio presente y
                    // sin UIDs faltantes (los sobrantes solo avisan). Si no, Nivel 1 por conteos.
                    var success = result.Level2Checked
                        ? (result.StudyFoundInDest && result.MissingCount == 0)
                        : (result.StudyFoundInDest && result.SeriesCountMatch && result.InstanceCountMatch);

                    await studyR.CompleteVerificationAsync(study.Id, success, maxRetries,
                        result.DestSeriesCount, result.DestInstanceCount,
                        success ? null : (result.ErrorMessage ?? (result.Level2Checked
                            ? $"Faltan {result.MissingCount} UIDs en destino"
                            : "Conteos no coinciden")),
                        result.MissingCount, result.ExtraCount,
                        result.MissingUids.Count > 0 ? string.Join("\n", result.MissingUids) : null,
                        result.VerifiedBy);

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
