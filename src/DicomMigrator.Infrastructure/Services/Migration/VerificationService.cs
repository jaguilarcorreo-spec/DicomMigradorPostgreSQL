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
                    // La consulta NO se completó. Tres casos (DCM-2):
                    //  · 401/403: credenciales rechazadas → CONFIGURACIÓN (pausa, sin auto-reanudar).
                    //  · Sin respuesta, o servidor no disponible/saturado (408, 429, 502-504)
                    //    → caída pasajera del destino: reintentar sin gastar intento.
                    //  · El servidor respondió con otro error (400, 404, 413, 500…) → falla
                    //    ESTE estudio: gasta intento. Antes también era "de conexión" y el
                    //    estudio se repetía para siempre.
                    var msg = qido.ErrorMessage ?? "No se pudo consultar el destino (QIDO-RS)";
                    // 404 a una búsqueda de estudios no significa "no está" (eso es 200 o 204
                    // sin resultados): significa que la URL de QIDO-RS no existe. Es la misma
                    // para todos los estudios, así que es CONFIGURACIÓN: si contara como fallo
                    // del estudio, una ruta mal escrita los marcaría todos VerifyFailed.
                    if (qido.HttpStatus == 404)
                    {
                        result.ConnectionError = true;
                        result.ConfigurationError = true;
                        result.ErrorMessage = $"El destino respondió HTTP 404 a la búsqueda QIDO-RS: la URL de DICOMweb del nodo no es correcta. {msg}";
                    }
                    else if (!qido.ConfigurationError && HttpAnswered(qido.HttpStatus))
                    {
                        result.DestQueryFailed = true;
                        result.ErrorMessage = $"El destino respondió HTTP {qido.HttpStatus} a la consulta QIDO-RS del estudio: {msg}";
                    }
                    else
                    {
                        result.ConnectionError = true;
                        result.ConfigurationError = qido.ConfigurationError;
                        result.ErrorMessage = msg;
                    }
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
                    // La consulta NO se completó. Si el PACS respondió (estado DIMSE de fallo,
                    // o empezó a enviar resultados y no terminó), falla ESTE estudio y gasta
                    // intento (DCM-2). Si no hubo respuesta (caído, red, rechazo pasajero),
                    // es de conexión: reintentar sin gastar intento. Un rechazo permanente
                    // de la asociación es de CONFIGURACIÓN.
                    var msg = cfind.ErrorMessage ?? "No se pudo consultar el destino (C-FIND)";
                    if (!cfind.ConfigurationError && DimseAnswered(cfind.DicomStatus, cfind.Studies.Count))
                    {
                        result.DestQueryFailed = true;
                        result.ErrorMessage = DimseAnswerMessage("C-FIND", cfind.DicomStatus, cfind.Studies.Count, "resultado(s)", msg);
                    }
                    else
                    {
                        result.ConnectionError = true;
                        result.ConfigurationError = cfind.ConfigurationError;
                        result.ErrorMessage = msg;
                    }
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
        catch (Exception ex) when (!ConnBackoff.IsDatabaseError(ex))   // los de BD suben al bucle del worker
        {
            logger.LogError(ex, "Verification error for study {Uid}", study.StudyInstanceUid);
            result.ErrorMessage = ex.Message;
            result.ConnectionError = true;  // excepción = fallo de operación, no del estudio
        }

        // ── Nivel 2: comparación de conjuntos de UIDs ──────────────────────────
        // Solo si el estudio tiene UIDs de origen capturados (MigrationInstances) y el
        // Nivel 1 lo ENCONTRÓ en el destino. Si no está, o la consulta falló, ya es un
        // fallo (o un reintento) y no hay nada que comparar: antes se enumeraba igual, una
        // consulta inútil que en algunos PACS daba otro error (DCM-2). Enumera el destino
        // por C-FIND IMAGE o QIDO y compara conjuntos: aprobado ⇔ no faltan UIDs (los
        // sobrantes solo avisan).
        if (!result.ConnectionError && result.StudyFoundInDest)
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
                        // No se pudo enumerar el destino. Mismo criterio que el Nivel 1
                        // (DCM-2): si el destino RESPONDIÓ con un error (p. ej. 0xA900/0xC000
                        // a la consulta de imágenes, HTTP 413 en un estudio enorme, o empezó a
                        // enviar instancias y no terminó), falla este estudio y gasta intento,
                        // en vez de repetirse para siempre; sin respuesta, es de conexión.
                        // En QIDO, DicomStatus lleva el código HTTP.
                        var msg = enumRes.ErrorMessage ?? (useQido
                            ? "No se pudo enumerar el destino (QIDO-RS instances)."
                            : "No se pudo enumerar el destino (C-FIND IMAGE).");
                        var answered = !enumRes.ConfigurationError && (useQido
                            ? HttpAnswered(enumRes.DicomStatus)
                            : DimseAnswered(enumRes.DicomStatus, enumRes.Instances.Count));
                        if (answered)
                        {
                            result.DestQueryFailed = true;
                            result.ErrorMessage = "Nivel 2: " + (useQido
                                ? $"el destino respondió HTTP {enumRes.DicomStatus} al listar las instancias del estudio: {msg}"
                                : DimseAnswerMessage("C-FIND IMAGE", enumRes.DicomStatus, enumRes.Instances.Count, "instancia(s)", msg));
                        }
                        else
                        {
                            result.ConnectionError = true;
                            result.ConfigurationError = enumRes.ConfigurationError;
                            result.ErrorMessage = msg;
                        }
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
            catch (Exception ex) when (!ConnBackoff.IsDatabaseError(ex))   // los de BD suben al bucle del worker
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

    // ── ¿Respondió el destino? (DCM-2) ───────────────────────────────────────
    /// <summary>Estudios seguidos con error de consulta del destino (no caída) que pausan la
    /// verificación como posible error de configuración.</summary>
    private const int QueryFailurePauseThreshold = 5;

    /// <summary>HTTP: hubo respuesta de error del servidor que NO indica "no disponible,
    /// saturado o tiempo agotado" (408, 429, 502, 503, 504), que son pasajeros. Sin código
    /// (no conectó, se cortó) tampoco es respuesta. 401/403 se tratan antes como configuración.</summary>
    private static bool HttpAnswered(int? httpStatus) =>
        httpStatus is >= 400 and not (408 or 429 or 502 or 503 or 504);

    /// <summary>DIMSE: el PACS respondió si llegó su respuesta final (con estado de fallo)
    /// o si ya había enviado resultados antes de cortarse o agotar el tiempo.</summary>
    private static bool DimseAnswered(int? dicomStatus, int partialResults) =>
        dicomStatus is not null || partialResults > 0;

    private static string DimseAnswerMessage(string what, int? dicomStatus, int partial, string unit, string error) =>
        dicomStatus is int st
            ? $"El destino respondió con estado 0x{st:X4} a la consulta {what} del estudio: {error}"
            : $"El destino empezó a responder a la consulta {what} ({partial} {unit} recibido/s) pero no terminó: {error}";

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

                // Solo es "Completed" si de verdad no queda nada por verificar. Si los
                // workers salieron con trabajo pendiente (salida anómala, o estudios que
                // migraron después de que la cola se vaciara), se pausa en vez de dar la
                // verificación por terminada y enviar el correo de fin.
                var studyRepo = scope.ServiceProvider.GetRequiredService<IStudyRepository>();
                if (await studyRepo.HasVerificationWorkPendingAsync(migrationId))
                {
                    await migR.UpdateVerificationStatusAsync(migrationId, "Paused");
                    logger.LogWarning("Verificación de la migración {Id}: los workers terminaron con estudios " +
                        "pendientes de verificar. Se pausa en vez de darla por completada; reanúdala para continuar.",
                        migrationId);
                    return;
                }

                await migR.UpdateVerificationStatusAsync(migrationId, "Completed");
                await migR.SetVerificationAutoPausedAsync(migrationId, false);

                // Verificación AL DÍA, no terminada (CONC-4): la migración aún tiene
                // estudios por migrar (está en pausa, o la verificación se lanzó antes que
                // ella). Queda en "Completed" sin correo ni promoción; MigrationWorker la
                // relanza al iniciar o reanudar la migración, y al terminarla. El panel la
                // muestra como «En espera · pendiente de migración».
                if (await studyRepo.HasMigrationWorkPendingAsync(migrationId))
                {
                    logger.LogInformation("Verificación de la migración {Id} al día: no quedan estudios migrados " +
                        "por verificar, pero sí por migrar. En espera: se relanzará cuando la migración continúe.",
                        migrationId);
                    return;
                }
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
    // sale cuando la cola está vacía (auto-completado), salvo que la migración siga en
    // marcha con estudios por migrar: entonces espera a que lleguen (CONC-4).
    private async Task VerificationWorkerLoopAsync(int migrationId, string workerId, CancellationTokenSource cts)
    {
        var ct = cts.Token;
        int emptyPolls = 0;
        bool waitingLogged = false;   // aviso «esperando a la migración» ya escrito (CONC-4)
        int connErrors = 0;   // errores de conexión consecutivos (PACS destino inaccesible)
        int queryFailStreak = 0;   // estudios DISTINTOS seguidos en los que el destino respondió con error (DCM-2)
        long? lastQueryFailStudy = null;
        int unexpectedErrors = 0;   // errores inesperados seguidos (BD caída, etc.)
        // Estudio que este worker tiene en 'VerificationPending' y aún no ha cerrado
        // (ni resultado registrado ni devuelto a 'Migrated'). Si una cancelación o un error
        // corta la verificación, se devuelve a la cola; si la BD está caída y no se puede,
        // se reintenta en la siguiente vuelta en vez de esperar 10 min a que caduque.
        // Se libera por Id (no por nombre de worker: "V0"… se repiten entre sesiones).
        long? heldStudyId = null;
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

            // Carga inicial con reintentos: un fallo puntual de la BD no debe matar al
            // worker antes de empezar (el cierre lo tomaría por "cola vacía").
            MigrationEntity? migration = null;
            for (var attempt = 1; migration is null; attempt++)
            {
                try
                {
                    migration = await migR.GetByIdAsync(migrationId);
                    if (migration?.DestNode is null)
                    {
                        logger.LogError("Verificación: migración {Id} sin nodo destino", migrationId);
                        return;
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    var wait = ConnBackoff.ForAttempt(attempt);
                    logger.LogError(ex, "Verificación worker {W}: no se pudo cargar la migración {Id}. Reintento en {S} s.",
                        workerId, migrationId, wait.TotalSeconds);
                    await Task.Delay(wait, ct);
                }
            }
            var destNode   = migration.DestNode!;   // comprobado al cargar (no nulo)
            var maxRetries = migration.MaxRetries;
            var retryDelay = migration.RetryDelaySeconds;

            while (!ct.IsCancellationRequested)
            {
                // try/catch por vuelta (igual que en MigrationWorker): un error inesperado de
                // BD no saca al worker. El estudio que tuviera tomado se devuelve a 'Migrated'
                // (ReleaseVerificationLockAsync solo actúa si sigue 'VerificationPending', así
                // que no deshace un resultado ya registrado).
                try
                {
                    if (heldStudyId is long pendingRelease)
                    {
                        await studyR.ReleaseVerificationLockAsync(pendingRelease);
                        heldStudyId = null;
                        logger.LogInformation("Verificación worker {W}: estudio pendiente de liberar devuelto a la cola.", workerId);
                    }

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
                    unexpectedErrors = 0;   // la BD ha respondido

                    if (study is null)
                    {
                        // ¿Queda trabajo? Migrated, VerificationPending (en vuelo) o VerifyRetryPending.
                        var workLeft = await studyR.HasVerificationWorkPendingAsync(migrationId);
                        if (!workLeft)
                        {
                            // Con la migración en marcha seguirán llegando estudios migrados:
                            // esperar en vez de salir (CONC-4). Antes, una verificación lanzada
                            // junto a la migración (o más rápida que ella) vaciaba la cola,
                            // salía, se daba por «completada» con 0 y nada la relanzaba: la
                            // migración se quedaba en «Migrated» para siempre.
                            if ((await migR.GetByIdAsync(migrationId))?.Status == "Running"
                                && await studyR.HasMigrationWorkPendingAsync(migrationId))
                            {
                                if (!waitingLogged)
                                {
                                    logger.LogInformation("Verificación worker {W}: nada que verificar por ahora; " +
                                        "la migración sigue en marcha, se espera a que migre más estudios.", workerId);
                                    waitingLogged = true;
                                }
                                await Task.Delay(30_000, ct);
                                continue;
                            }
                            logger.LogInformation("Verificación worker {W}: cola vacía, saliendo", workerId);
                            break;
                        }
                        emptyPolls++;
                        await Task.Delay(emptyPolls > 3 ? 30_000 : 5_000, ct);
                        continue;
                    }
                    emptyPolls = 0;
                    waitingLogged = false;
                    heldStudyId = study.Id;

                    try
                    {
                        var result = await verSvc.VerifyStudyAsync(destNode, study, ct);

                        // Error de conexión/operación (PACS caído, timeout): NO es fallo del
                        // estudio. Devolverlo a 'Migrated' sin gastar reintento y esperar,
                        // por si el destino está temporalmente inaccesible.
                        if (result.ConnectionError)
                        {
                            await studyR.ReleaseVerificationLockAsync(study.Id);
                            heldStudyId = null;

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
                                    // Pausa NORMAL, no auto-pausa (CONC-6): la auto-reanudación
                                    // sondea el destino, que responde, y la relanzaba en bucle
                                    // (pausa, correo, reanuda, falla…) sin arreglar nada. Se
                                    // reanuda a mano tras corregir la configuración, como en la
                                    // migración con 0xA801 o un rechazo permanente.
                                    await migR.UpdateVerificationStatusAsync(migrationId, "Paused");
                                    await migR.SetVerificationAutoPausedAsync(migrationId, false);
                                    await auditR.AddAsync(new MigrationAuditLog
                                    {
                                        MigrationId = migrationId, Action = "PAUSE", Level = "ERROR", Result = "ERROR",
                                        UserOrProcess = $"VERIFY-{workerId}",
                                        TechnicalMessage = $"Verificación pausada por error de configuración con el destino: " +
                                            $"{result.ErrorMessage}. Reanúdala a mano cuando esté corregido.",
                                    });
                                    try
                                    {
                                        await scope.ServiceProvider.GetRequiredService<INotificationService>()
                                            .RaiseAsync(NotificationEvents.AutoPaused, migration.Name, new (string, string)[]
                                            {
                                                ("Origen → Destino", $"{migration.OriginNode?.Alias ?? "?"} → {migration.DestNode?.Alias ?? "?"}"),
                                                ("Proceso", "Verificación"),
                                                ("Motivo",  $"Error de configuración (credenciales o AE Title rechazados por el destino): {result.ErrorMessage}. " +
                                                            "No se reanuda sola: corrígelo y reanúdala a mano."),
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
                                    // Un correo por incidente (CONC-6): si la auto-reanudación
                                    // vuelve a fallar sin haber verificado nada, no se avisa otra vez.
                                    var newIncident = AutoPauseIncidents.RegisterPause("VERIFY", migrationId);
                                    if (newIncident)
                                        logger.LogError("Verificación: {N} errores de conexión consecutivos con el destino ({Err}). " +
                                            "Pausando verificación de la migración {Id} (se reanudará sola).",
                                            connErrors, result.ErrorMessage, migrationId);
                                    else
                                        logger.LogError("Verificación de la migración {Id} pausada otra vez ({N}.ª del mismo incidente, " +
                                            "sin aviso nuevo): {Err}.", migrationId,
                                            AutoPauseIncidents.Pauses("VERIFY", migrationId), result.ErrorMessage);
                                    await migR.UpdateVerificationStatusAsync(migrationId, "Paused");
                                    await migR.SetVerificationAutoPausedAsync(migrationId, true);
                                    if (newIncident)
                                    {
                                        try
                                        {
                                            await scope.ServiceProvider.GetRequiredService<INotificationService>()
                                                .RaiseAsync(NotificationEvents.AutoPaused, migration.Name, new (string, string)[]
                                                {
                                                    ("Origen → Destino", $"{migration.OriginNode?.Alias ?? "?"} → {migration.DestNode?.Alias ?? "?"}"),
                                                    ("Proceso", "Verificación"),
                                                    ("Motivo",  $"{connErrors} errores de conexión con el destino; el último: {result.ErrorMessage}"),
                                                    ("Nodo",    migration.DestNode?.Alias ?? "destino"),
                                                }, migrationId, "migration");
                                        }
                                        catch (Exception nex) { logger.LogWarning(nex, "Notificación de auto-pausa (verificación {Id}) falló.", migrationId); }
                                    }
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
                        AutoPauseIncidents.Resolve("VERIFY", migrationId);   // el destino responde: fin del incidente

                        // Nivel 2 (si se comparó por conjuntos): aprobado ⇔ estudio presente y
                        // sin UIDs faltantes (los sobrantes solo avisan). Si no, Nivel 1 por conteos.
                        // Si el destino respondió con un error a alguna consulta del estudio
                        // (DestQueryFailed), no está verificado: gasta intento (DCM-2).
                        var success = !result.DestQueryFailed && (result.Level2Checked
                            ? (result.StudyFoundInDest && result.MissingCount == 0)
                            : (result.StudyFoundInDest && result.SeriesCountMatch && result.InstanceCountMatch));

                        await studyR.CompleteVerificationAsync(study.Id, success, maxRetries,
                            result.DestSeriesCount, result.DestInstanceCount,
                            success ? null : (result.ErrorMessage ?? (result.Level2Checked
                                ? $"Faltan {result.MissingCount} UIDs en destino"
                                : "Conteos no coinciden")),
                            result.MissingCount, result.ExtraCount,
                            result.MissingUids.Count > 0 ? string.Join("\n", result.MissingUids) : null,
                            result.VerifiedBy);
                        heldStudyId = null;   // resultado registrado: ya no está en vuelo

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

                        // Freno ante un error SISTEMÁTICO (DCM-2): si el destino responde con
                        // error a varios estudios seguidos, lo más probable es que rechace la
                        // consulta en sí (PACS que no admite la búsqueda de imágenes, parámetro
                        // no soportado…), no que fallen todos esos estudios. Seguir gastaría los
                        // intentos de toda la migración hasta dejarla en VerifyFailed. Se pausa
                        // como un error de configuración: no se reanuda sola.
                        // Cuenta estudios distintos: el mismo estudio reintentado varias veces
                        // seguidas (p. ej. el último que queda) es un fallo de ESE estudio.
                        if (!result.DestQueryFailed) { queryFailStreak = 0; lastQueryFailStudy = null; }
                        else if (lastQueryFailStudy != study.Id) { queryFailStreak++; lastQueryFailStudy = study.Id; }
                        if (queryFailStreak >= QueryFailurePauseThreshold)
                        {
                            if (ConnBackoff.TryAnnouncePause("VERIFY", migrationId))
                            {
                                var why = $"el destino ha respondido con error a {queryFailStreak} estudios seguidos; " +
                                          $"el último: {result.ErrorMessage}";
                                logger.LogError("Verificación de la migración {Id} pausada: {Why}. Revisa el nodo destino " +
                                    "(o su soporte de la consulta) y reanúdala.", migrationId, why);
                                await migR.UpdateVerificationStatusAsync(migrationId, "Paused");
                                await migR.SetVerificationAutoPausedAsync(migrationId, false);
                                await auditR.AddAsync(new MigrationAuditLog
                                {
                                    MigrationId = migrationId, Action = "PAUSE", Level = "ERROR", Result = "ERROR",
                                    UserOrProcess = $"VERIFY-{workerId}",
                                    TechnicalMessage = $"Verificación pausada: {why}. Reanúdala a mano tras revisarlo.",
                                });
                                try
                                {
                                    await scope.ServiceProvider.GetRequiredService<INotificationService>()
                                        .RaiseAsync(NotificationEvents.AutoPaused, migration.Name, new (string, string)[]
                                        {
                                            ("Origen → Destino", $"{migration.OriginNode?.Alias ?? "?"} → {migration.DestNode?.Alias ?? "?"}"),
                                            ("Proceso", "Verificación"),
                                            ("Motivo",  $"Posible error de configuración: {why}. No se reanuda sola: revísalo y reanúdala a mano."),
                                            ("Nodo",    migration.DestNode?.Alias ?? "destino"),
                                        }, migrationId, "migration");
                                }
                                catch (Exception nex) { logger.LogWarning(nex, "Notificación de pausa (verificación {Id}) falló.", migrationId); }
                            }
                            cts.Cancel();
                            break;
                        }
                    }
                    catch (OperationCanceledException) { break; }
                    // Un error de BD (p. ej. al guardar el resultado) no es fallo del estudio:
                    // no se gasta un reintento de verificación; sube al catch del bucle.
                    catch (Exception ex) when (!ConnBackoff.IsDatabaseError(ex))
                    {
                        logger.LogError(ex, "Error verificando {Uid}", study.StudyInstanceUid);
                        // Marcar como fallido/reintento para que salga de la cola
                        await studyR.CompleteVerificationAsync(study.Id, false, maxRetries, null, null, ex.Message);
                        heldStudyId = null;
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    unexpectedErrors++;
                    var wait = ConnBackoff.ForAttempt(unexpectedErrors);
                    logger.LogError(ex, "Verificación worker {W} (migración {Id}): error inesperado ({N} seguido/s). " +
                        "Se reintenta en {S} s.", workerId, migrationId, unexpectedErrors, wait.TotalSeconds);
                    // Devolver a la cola el estudio en vuelo; si la BD sigue caída, queda
                    // anotado en heldStudyId y se reintenta al principio de la siguiente vuelta.
                    if (await TryReleaseVerificationAsync(heldStudyId, workerId))
                        heldStudyId = null;
                    await Task.Delay(wait, ct);
                }
            }
        }
        catch (OperationCanceledException) { /* parada normal */ }
        catch (Exception ex)
        {
            logger.LogError(ex, "Verificación worker {W} crashed", workerId);
        }
        finally
        {
            // Pausa, parada o salida por error: un estudio a medio verificar vuelve a
            // 'Migrated' en vez de quedarse en 'VerificationPending' (CONC-3).
            await TryReleaseVerificationAsync(heldStudyId, workerId);
        }
    }

    /// <summary>Best-effort: devuelve a 'Migrated' el estudio en vuelo (si lo hay). Usa su
    /// propio scope porque puede llamarse tras desechar el del worker. False si falló.</summary>
    private async Task<bool> TryReleaseVerificationAsync(long? studyId, string workerId)
    {
        if (studyId is not long id) return true;
        try
        {
            using var scope = scopeFactory.CreateScope();
            await scope.ServiceProvider.GetRequiredService<IStudyRepository>().ReleaseVerificationLockAsync(id);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Verificación worker {W}: no se pudo devolver el estudio {Id} a la cola " +
                "(se reintentará; si no, lo rescata la caducidad del bloqueo).", workerId, id);
            return false;
        }
    }
}
