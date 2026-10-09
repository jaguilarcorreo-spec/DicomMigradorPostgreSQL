using DicomMigrator.Core.Interfaces;
using DicomMigrator.Core.Models;
using DicomMigrator.Infrastructure.Repositories;
using DicomMigrator.Infrastructure.Services.Licensing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

// Alias explícito para evitar ambigüedad entre el tipo Migration y el namespace
// DicomMigrator.Infrastructure.Services.Migration en el que vive este fichero.
using MigrationEntity = DicomMigrator.Core.Models.Migration;

namespace DicomMigrator.Infrastructure.Services.Migration;

// ══════════════════════════════════════════════════════════════════════════════
//  Atribución en la auditoría
// ══════════════════════════════════════════════════════════════════════════════

internal static class AuditActorHelper
{
    /// <summary>Compone el actor de una entrada de auditoría escrita por un proceso
    /// automático. El proceso no lo inicia una persona en ese instante, sino quien
    /// creó la migración o el job, así que se registran ambos: qué lo ejecutó y por
    /// orden de quién. Si no consta autoría (datos anteriores al login), queda solo
    /// el proceso.</summary>
    public static string Actor(string process, string? user)
        => string.IsNullOrWhiteSpace(user) ? process : $"{process} (por {user})";
}

// ══════════════════════════════════════════════════════════════════════════════
// MIGRATION WORKER
// ══════════════════════════════════════════════════════════════════════════════

public class MigrationWorker(
    IServiceScopeFactory scopeFactory,
    LicenseStatusCache licenseCache,
    ILogger<MigrationWorker> logger) : IMigrationWorker
{
    // Track active CancellationTokenSources per migration (Singleton state — OK)
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, CancellationTokenSource> _cts = new();

    // Última ejecución lanzada por migración (workers + handler de finalización). Tras una
    // pausa, _cts ya no tiene entrada pero los workers viejos pueden seguir terminando su
    // estudio: StartAsync espera a que acaben antes de rescatar locks huérfanos.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, Task> _runs = new();

    // Tope de espera a que terminen los workers de una ejecución anterior al reanudar.
    private static readonly TimeSpan PreviousRunWait = TimeSpan.FromSeconds(60);

    /// <summary>Cancela TODOS los workers activos de forma inmediata, sin tocar el estado
    /// en BD. Pensado para el apagado del proceso: los estudios en curso quedan como
    /// huérfanos y se recuperan al reiniciar (ReleaseOrphanLocksAsync + auto-reanudación).
    /// No marca pausa de usuario; la migración sigue figurando como 'Running' para que se
    /// reanude sola al volver a arrancar.</summary>
    public void CancelAllForShutdown()
    {
        foreach (var kv in _cts)
        {
            try { kv.Value.Cancel(); } catch { /* ignorar */ }
        }
    }

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
        // ── Verja de licencia ────────────────────────────────────────────────
        // Choke point ÚNICO de arranque de migraciones (incluye la auto-reanudación
        // al iniciar el servicio): si la licencia no es válida, no se inicia ningún
        // worker. La UI ya avisa con un banner y la página de Licencia.
        var lic = licenseCache.Current;
        if (!lic.CanMigrate)
        {
            logger.LogError("Migración {Id} NO se inicia: licencia no válida ({Verdict}) — {Reason}",
                migrationId, lic.Verdict, lic.Reason);
            try
            {
                using var scope = scopeFactory.CreateScope();
                await AuditRepo(scope).AddAsync(new MigrationAuditLog
                {
                    MigrationId      = migrationId,
                    Action           = "LICENSE",
                    Level            = "WARN",
                    Result           = "ERROR",
                    UserOrProcess    = "LICENSE",
                    TechnicalMessage = $"Inicio bloqueado por licencia: {lic.Verdict} — {lic.Reason}",
                });
            }
            catch { /* el bloqueo nunca debe fallar por la auditoría */ }
            return;
        }

        ConnBackoff.ResetPauseAnnouncement("MIGRATE", migrationId);

        // Guardia de arranque ATÓMICA: reserva el slot en _cts antes de hacer nada.
        // Como ResumeAsync es un alias de StartAsync, una segunda llamada estando ya en
        // marcha (doble clic, o UI + auto-reanudación al arrancar el servicio) NO debe
        // sobrescribir el CTS anterior — eso dejaría a los workers viejos con un token que
        // ya nadie puede cancelar (fuga de CTS) y lanzaría un segundo juego de workers.
        // TryAdd hace el "comprobar y reservar" en un solo paso, sin ventana de carrera
        // (mismo patrón que DiscoveryEngine.StartAsync).
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (!_cts.TryAdd(migrationId, cts))
        {
            cts.Dispose();
            logger.LogWarning("Migration {Id} already has active workers", migrationId);
            return;
        }

        MigrationEntity migration;
        try
        {
            using var scope = scopeFactory.CreateScope();
            migration = await MigrationRepo(scope).GetByIdAsync(migrationId)
                ?? throw new InvalidOperationException($"Migración {migrationId} no encontrada");

            // No arrancar mientras se puebla la lista de estudios desde el inventario
            // (CONC-5): con la lista aún vacía o a medias, los workers no encuentran nada,
            // salen, la migración se declara terminada (y se envía el correo de
            // "completada"); los estudios que el poblado inserta después quedan Pending
            // sin que nadie los procese. La guardia está aquí, no solo en la interfaz,
            // para cubrir todos los caminos de arranque (botones, reanudaciones, ventana).
            if (migration.PopulateStatus == "Running")
                throw new InvalidOperationException(
                    $"La migración '{migration.Name}' aún está cargando su lista de estudios desde el inventario. " +
                    "Espera a que termine el poblado para iniciarla.");

            // ── Rescate de estudios huérfanos ─────────────────────────────────────
            // Ya tenemos el slot reservado, así que no hay workers NUEVOS de esta
            // migración. Si quedan los de una ejecución anterior (pausa → reanudar
            // rápido), esperar a que terminen: ellos mismos devuelven su estudio a
            // 'Pending'. Lo que siga en 'Queued'/'Migrating' después es huérfano (caída
            // abrupta del proceso: corte de luz, kill, parada forzada del servicio) y se
            // devuelve a 'Pending' sin consumir reintento.
            if (_runs.TryGetValue(migrationId, out var previousRun) && !previousRun.IsCompleted)
            {
                var finished = await Task.WhenAny(previousRun, Task.Delay(PreviousRunWait, ct)) == previousRun;
                if (!finished)
                    logger.LogWarning("Migración {Id}: los workers de la ejecución anterior no terminaron en {S}s; " +
                        "se rescatan igualmente sus estudios.", migrationId, PreviousRunWait.TotalSeconds);
            }
            var rescued = await StudyRepo(scope).ReleaseOrphanMigrationLocksAsync(migrationId);
            if (rescued > 0)
                logger.LogWarning("Migración {Id}: {N} estudio(s) huérfano(s) en 'Queued'/'Migrating' " +
                    "devuelto(s) a 'Pending' (sin consumir reintento).", migrationId, rescued);

            await MigrationRepo(scope).UpdateStatusAsync(migrationId, "Running");
            // Acción manual: limpiar el flag de auto-pausa por conexión.
            await MigrationRepo(scope).SetMigrationAutoPausedAsync(migrationId, false);
            await AuditRepo(scope).AddAsync(new MigrationAuditLog
            {
                MigrationId = migrationId, Action = "START", Result = "OK",
                UserOrProcess = AuditActorHelper.Actor("WORKER", migration.CreatedBy),
                TechnicalMessage = $"Iniciando {migration.WorkerThreads} workers. Método: {migration.TransferMethod}"
            });
        }
        catch
        {
            // El arranque falló ANTES de lanzar los workers (migración inexistente, error de
            // BD…). Liberar el slot reservado para no dejar la migración bloqueada: el
            // continuation de abajo, que normalmente libera _cts, no llegará a ejecutarse.
            if (_cts.TryRemove(migrationId, out var reserved))
                reserved.Dispose();
            throw;
        }

        // Launch worker threads — each creates its own scope
        var priorities = (migration.ModalityPriority ?? "CT,MR,MG,CR,OT").Split(',');
        var tasks = Enumerable.Range(0, migration.WorkerThreads)
            .Select(i => RunWorkerLoopAsync(migration, $"WORKER-{i + 1}", priorities, cts))
            .ToArray();

        // Fire and forget — completion handler runs when ALL workers exit
        // (either cancelled via Pause/Cancel, or naturally when the queue empties)
        _runs[migrationId] = Task.WhenAll(tasks).ContinueWith(async _ =>
        {
            // If the token was cancelled, this was a Pause/Cancel — don't override status
            var wasCancelled = cts.IsCancellationRequested;
            // Retirar SOLO nuestro CTS: si tras una pausa ya se reanudó la migración, la
            // entrada de _cts es la de la nueva ejecución y no debemos quitarla ni liberarla.
            if (_cts.TryRemove(new KeyValuePair<int, CancellationTokenSource>(migrationId, cts)))
                cts.Dispose();

            if (wasCancelled) return;  // Pause/Cancel already set the right status

            // Cierre con reintentos: si la BD falla justo ahora, no dejar la migración
            // "Running" sin workers a la primera. Cada intento comprueba que no se haya
            // lanzado entretanto una ejecución nueva (reanudación), para no pisarla.
            for (var attempt = 1; ; attempt++)
            {
                if (_cts.ContainsKey(migrationId)) return;   // ya hay otra ejecución en marcha
                try
                {
                    await FinishMigrationAsync(migrationId);
                    return;
                }
                catch (Exception ex) when (attempt < FinishAttempts)
                {
                    logger.LogWarning(ex, "Migración {Id}: fallo al cerrar la ejecución (intento {N}/{Max}); se reintenta.",
                        migrationId, attempt, FinishAttempts);
                    await Task.Delay(ConnBackoff.ForAttempt(attempt));
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Migración {Id}: no se pudo cerrar la ejecución tras {Max} intentos. " +
                        "Sigue figurando como 'Running' y se reanudará al reiniciar el servicio.", migrationId, FinishAttempts);
                    return;
                }
            }
        }, TaskScheduler.Default).Unwrap();
    }

    // Intentos del cierre de una ejecución ante errores de BD (esperas de ConnBackoff).
    private const int FinishAttempts = 5;

    /// <summary>Cierre de una ejecución cuyos workers salieron sin cancelación. Solo da la
    /// migración por terminada si de verdad no queda trabajo; si quedan estudios por
    /// migrar (salida anómala de los workers), la pausa y lo deja en la auditoría en vez
    /// de declarar "Migrated" y enviar el correo de fin.</summary>
    private async Task FinishMigrationAsync(int migrationId)
    {
        using var scope = scopeFactory.CreateScope();
        var stats = await StudyRepo(scope).GetStatsAsync(migrationId);

        var left = stats.Pending + stats.Queued + stats.Migrating + stats.RetryPending;
        if (left > 0)
        {
            logger.LogError("Migración {Id}: los workers terminaron con {Left} estudio(s) por migrar " +
                "(Pendientes={P} En cola={Q} Migrando={M} Reintento={R}). Se pausa en vez de darla por finalizada.",
                migrationId, left, stats.Pending, stats.Queued, stats.Migrating, stats.RetryPending);
            await MigrationRepo(scope).UpdateStatusAsync(migrationId, "Paused");
            await AuditRepo(scope).AddAsync(new MigrationAuditLog
            {
                MigrationId = migrationId, Action = "COMPLETE", Level = "WARN", Result = "ERROR",
                UserOrProcess = "WORKER",
                TechnicalMessage = $"Los workers terminaron con {left} estudio(s) por migrar " +
                    $"(Pendientes={stats.Pending} En cola={stats.Queued} Migrando={stats.Migrating} " +
                    $"Reintento={stats.RetryPending}). Migración pausada: reanúdala para continuar."
            });
            return;
        }

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

        // ── Notificación por correo (v227) ──
        try
        {
            var mig = await MigrationRepo(scope).GetByIdAsync(migrationId);
            var notif = scope.ServiceProvider.GetRequiredService<INotificationService>();
            var kind = finalStatus == "Failed"
                ? NotificationEvents.MigrationFailed
                : NotificationEvents.MigrationCompleted;
            await notif.RaiseAsync(kind, mig?.Name ?? $"#{migrationId}", new (string, string)[]
            {
                ("Origen → Destino",    $"{mig?.OriginNode?.Alias ?? "?"} → {mig?.DestNode?.Alias ?? "?"}"),
            }, migrationId, "migration",
            kpis: new (string, string)[]
            {
                ("Estudios",    stats.Total.ToString("N0")),
                ("Migrados",    stats.Migrated.ToString("N0")),
                ("Verificados", stats.Verified.ToString("N0")),
                ("Fallidos",    stats.Failed.ToString("N0")),
            });
        }
        catch (Exception nex) { logger.LogWarning(nex, "Notificación de fin de migración {Id} falló (no crítico).", migrationId); }
    }

    /// <summary>Devuelve a 'Pending' los estudios que este worker tenga en 'Queued' o
    /// 'Migrating' (sin gastar reintento). Best-effort: si la BD tampoco responde, el
    /// bloqueo caducará solo (AcquireNextPendingAsync) o se rescatará al reanudar.</summary>
    private async Task<bool> ReleaseOwnLocksAsync(int migrationId, string workerId)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            await StudyRepo(scope).ReleaseLocksAsync(migrationId, workerId);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Worker {Id}: no se pudieron liberar sus estudios (se reintentará en la siguiente vuelta).", workerId);
            return false;
        }
    }

    private async Task RunWorkerLoopAsync(MigrationEntity migration, string workerId,
        string[] priorities, CancellationTokenSource cts)
    {
        var ct = cts.Token;
        logger.LogInformation("Worker {Id} started for migration {MigId}", workerId, migration.Id);
        int emptyPolls = 0;
        int connErrors = 0;   // errores de conexión consecutivos con el PACS origen
        int unexpectedErrors = 0;   // errores inesperados seguidos (BD caída, etc.)
        bool releasePending = false; // la liberación de sus estudios falló (BD caída): repetirla

        try
        {
            while (!ct.IsCancellationRequested)
            {
                // Cada vuelta del bucle tiene su propio try/catch: un error inesperado
                // (BD caída unos segundos, fallo puntual de red…) NO saca al worker para
                // siempre. Antes, el try envolvía todo el bucle: el worker moría y, cuando
                // morían todos, el cierre declaraba la migración terminada con estudios
                // pendientes. Ahora el worker libera su estudio, espera con backoff y sigue;
                // solo sale al cancelar (pausa/parada) o cuando la cola está vacía.
                try
                {
                    using var scope = scopeFactory.CreateScope();

                    // Si tras un error no se pudieron devolver a 'Pending' los estudios que
                    // este worker tenía tomados (la BD seguía caída), hacerlo ahora: si no,
                    // quedarían bloqueados hasta que caduque el lock (10-15 min) y la
                    // migración se quedaría parada cerca del final. Aquí el worker no tiene
                    // ningún estudio en curso, así que liberar los suyos es seguro.
                    if (releasePending)
                    {
                        await StudyRepo(scope).ReleaseLocksAsync(migration.Id, workerId);
                        releasePending = false;
                        logger.LogInformation("Worker {Id}: estudios pendientes de liberar devueltos a la cola.", workerId);
                    }

                    // Respect the execution window — reload migration to pick up window changes
                    var freshMig = await MigrationRepo(scope).GetByIdAsync(migration.Id);
                    if (freshMig is not null && !WindowSched(scope).IsWindowOpen(freshMig.Windows))
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

                    // La base de datos ha respondido: se reinicia la cuenta de errores seguidos.
                    unexpectedErrors = 0;

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
                    var outcome = await MigrateStudyAsync(migration, study, workerId, ct);

                    if (outcome.ConfigurationError is string configError)
                    {
                        // Error de configuración permanente (0xA801): pausar la migración
                        // entera al primero. NO se marca como auto-pausa: la auto-reanudación
                        // solo comprueba que el origen responde y la relanzaría en bucle,
                        // fallando igual. Se reanuda a mano tras corregir la configuración.
                        if (ConnBackoff.TryAnnouncePause("MIGRATE", migration.Id))
                        {
                            logger.LogError("Migración {Id}: {Reason}", migration.Id, configError);
                            await MigrationRepo(scope).UpdateStatusAsync(migration.Id, "Paused");
                            await AuditRepo(scope).AddAsync(new MigrationAuditLog
                            {
                                MigrationId = migration.Id, Action = "PAUSE", Level = "ERROR", Result = "ERROR",
                                UserOrProcess = workerId,
                                TechnicalMessage = $"Migración pausada por error de configuración: {configError}",
                            });
                            try
                            {
                                await scope.ServiceProvider.GetRequiredService<INotificationService>()
                                    .RaiseAsync(NotificationEvents.AutoPaused, migration.Name, new (string, string)[]
                                    {
                                        ("Origen → Destino", $"{migration.OriginNode?.Alias ?? "?"} → {migration.DestNode?.Alias ?? "?"}"),
                                        ("Proceso", "Migración"),
                                        ("Motivo",  configError),
                                        ("Nodo",    migration.OriginNode?.Alias ?? "origen"),
                                    }, migration.Id, "migration");
                            }
                            catch (Exception nex) { logger.LogWarning(nex, "Notificación de pausa (migración {Id}) falló.", migration.Id); }
                        }
                        // Detiene a los demás workers ya (sus estudios vuelven a Pending sin
                        // gastar reintento) y evita que el cierre marque la migración como terminada.
                        cts.Cancel();
                        break;
                    }

                    if (outcome.IsTransient)
                    {
                        connErrors++;
                        // Si se acumulan unos pocos errores de conexión seguidos, el destino
                        // (o el origen) está caído: pausar pronto para no machacar en bucle.
                        // El servicio de auto-reanudación lo retomará cuando vuelva la conexión.
                        if (connErrors >= ConnBackoff.AutoPauseThreshold)
                        {
                            if (ConnBackoff.TryAnnouncePause("MIGRATE", migration.Id))
                            {
                                logger.LogError("Migración: {N} errores de conexión consecutivos. " +
                                    "Pausando migración {Id} (se reanudará sola al recuperarse la conexión).",
                                    connErrors, migration.Id);
                                await MigrationRepo(scope).UpdateStatusAsync(migration.Id, "Paused");
                                try
                                {
                                    await scope.ServiceProvider.GetRequiredService<INotificationService>()
                                        .RaiseAsync(NotificationEvents.AutoPaused, migration.Name, new (string, string)[]
                                        {
                                            ("Origen → Destino", $"{migration.OriginNode?.Alias ?? "?"} → {migration.DestNode?.Alias ?? "?"}"),
                                            ("Proceso", "Migración"),
                                            ("Motivo",  $"{connErrors} errores de conexión con el origen"),
                                            ("Nodo",    migration.OriginNode?.Alias ?? "origen"),
                                        }, migration.Id, "migration");
                                }
                                catch (Exception nex) { logger.LogWarning(nex, "Notificación de auto-pausa (migración {Id}) falló.", migration.Id); }
                                await MigrationRepo(scope).SetMigrationAutoPausedAsync(migration.Id, true);
                            }
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
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    unexpectedErrors++;
                    var wait = ConnBackoff.ForAttempt(unexpectedErrors);
                    logger.LogError(ex, "Worker {Id} (migración {MigId}): error inesperado ({N} seguido/s). " +
                        "Se reintenta en {S} s.", workerId, migration.Id, unexpectedErrors, wait.TotalSeconds);
                    // Devolver a 'Pending' el estudio que tuviera tomado (sin gastar reintento),
                    // para que no espere a la caducidad del bloqueo.
                    if (!await ReleaseOwnLocksAsync(migration.Id, workerId))
                        releasePending = true;
                    await Task.Delay(wait, ct);
                }
            }
        }
        catch (OperationCanceledException) { /* normal shutdown */ }
        catch (Exception ex)
        { logger.LogError(ex, "Worker {Id} crashed", workerId); }
        finally
        {
            await ReleaseOwnLocksAsync(migration.Id, workerId);
            logger.LogInformation("Worker {Id} stopped", workerId);
        }
    }

    /// <summary>Resultado de migrar un estudio, para el bucle del worker.</summary>
    /// <param name="IsTransient">Fallo transitorio de conexión o de recursos del PACS: el
    /// estudio volvió a Pending sin gastar reintento y cuenta para la auto-pausa.</param>
    /// <param name="ConfigurationError">Error de configuración permanente (p. ej. 0xA801):
    /// motivo para pausar la migración entera. Null si no lo hubo.</param>
    private readonly record struct MoveOutcome(bool IsTransient, string? ConfigurationError)
    {
        public static readonly MoveOutcome Done      = new(false, null);
        public static readonly MoveOutcome Transient = new(true, null);
        public static MoveOutcome Config(string reason) => new(false, reason);
    }

    /// <summary>Migra un estudio y clasifica el resultado (ver MoveOutcome).</summary>
    private async Task<MoveOutcome> MigrateStudyAsync(MigrationEntity migration, MigrationStudy study,
        string workerId, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var studyRepo = StudyRepo(scope);
        var auditRepo = AuditRepo(scope);
        var dimse     = Dimse(scope);

        // Reload migration with fresh node data — picks up any config changes made while paused
        var freshMigration = await MigrationRepo(scope).GetByIdAsync(migration.Id) ?? migration;

        // 'Migrating' CONSERVA el lock (LockedByWorker + LockDate) y un latido lo renueva
        // mientras dura el C-MOVE: si el proceso muere a mitad, el estudio es rescatable
        // (caducidad en AcquireNextPendingAsync, o ReleaseOrphanMigrationLocksAsync al
        // arrancar) en vez de quedarse en 'Migrating' para siempre.
        await studyRepo.MarkMigratingAsync(study.Id, workerId);
        logger.LogInformation("[{Worker}] Migrating {Uid}", workerId, study.StudyInstanceUid);

        using var heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var heartbeat = MigrationHeartbeatAsync(study.Id, workerId, heartbeatCts.Token);

        try
        {
            var request = new CMoveRequest
            {
                Level            = "STUDY",
                StudyInstanceUid = study.StudyInstanceUid,
                DestinationAet   = freshMigration.DestNode!.RemoteAet,
            };

            var result = await dimse.MoveAsync(freshMigration.OriginNode!, request, ct);

            // La regla de éxito vive en un solo sitio (CMoveService): respuesta FINAL del
            // PACS, sin fallos ni pendientes. Antes aquí había otra copia, más laxa, que
            // daba por migrado un C-MOVE cortado con solo las respuestas Pending (CONC-2).
            // ReceivedCount siempre es 0 porque los C-STORE van directo del origen al destino.
            var success = result.Success;

            var techMsg = $"Status=0x{result.DicomStatus:X4} " +
                          $"Completed={result.Completed} Received={result.ReceivedCount} " +
                          $"Failed={result.Failed} Warning={result.Warning} Remaining={result.Remaining} {result.DurationMs}ms";

            if (!success && result.Completed == 0 && result.DicomStatus == null)
            {
                // Sin respuesta alguna — timeout o asociación rechazada
                techMsg = result.ErrorMessage ?? "Sin respuesta del PACS origen";
            }
            else if (!success && !result.FinalResponseReceived)
            {
                // Hubo respuestas Pending pero no la final: el C-MOVE se cortó a medias
                // (inactividad, red, aborto del PACS). El estudio puede estar incompleto en
                // el destino: se reintenta entero (el destino descarta los duplicados).
                techMsg += " — C-MOVE cortado sin respuesta final del PACS origen: el estudio puede estar incompleto. " +
                           (result.ErrorMessage ?? "");
            }
            else if (!success && result.DicomStatus == 0xA801)
            {
                techMsg += " — " + MoveDestinationUnknownMessage(freshMigration, result.ErrorComment);
            }
            else if (!success && result.DicomStatus is >= 0xA700 and <= 0xA7FF)
            {
                techMsg += " — El PACS origen no tiene recursos para atender el C-MOVE ahora (saturado). " +
                           "Se reintentará sin gastar intento." +
                           (result.ErrorComment is null ? "" : $" Mensaje del PACS: «{result.ErrorComment}».");
            }
            else if (!success && result.Completed == 0)
            {
                techMsg += " — El PACS origen no procesó ninguna instancia. Verifica que el StudyInstanceUID existe en el origen." +
                           (result.ErrorComment is null ? "" : $" Mensaje del PACS: «{result.ErrorComment}».");
            }

            logger.LogInformation("[{Worker}] C-MOVE result: {Msg} StudyUID={Uid}",
                workerId, techMsg, study.StudyInstanceUid);

            // ── Errores que NO son del estudio (DCM-4, CONC-7) ──────────────────
            // Rechazo PERMANENTE de la asociación (el PACS origen no reconoce o no admite
            // al migrador): antes caía en "el PACS no procesó ninguna instancia" y cada
            // estudio gastaba sus reintentos hasta acabar "Failed". Ahora se pausa la
            // migración entera, como con 0xA801. El rechazo TRANSITORIO llega como
            // ConnectionError y lo trata la rama de conexión (sin gastar reintento).
            if (!success && result.ConfigurationError)
            {
                await studyRepo.ReleaseMigrationLockAsync(study.Id);
                await auditRepo.AddAsync(new MigrationAuditLog
                {
                    MigrationId      = migration.Id,
                    Action           = "C-MOVE",
                    Level            = "ERROR",
                    Result           = "ERROR",
                    StudyInstanceUid = study.StudyInstanceUid,
                    UserOrProcess    = workerId,
                    TechnicalMessage = $"Error de configuración (sin gastar reintento): {techMsg}",
                });
                return MoveOutcome.Config((result.ErrorMessage ?? "El PACS origen rechazó la conexión de forma permanente.") +
                    " La migración se ha pausado; reanúdala cuando esté corregido.");
            }
            // 0xA801 "Move Destination unknown": el PACS origen no tiene dado de alta el
            // AE destino. Es configuración, igual para todos los estudios: no se gasta
            // reintento (antes cada estudio agotaba sus intentos y acababa "Failed") y se
            // pausa la migración entera con el motivo exacto.
            if (!success && result.DicomStatus == 0xA801)
            {
                await studyRepo.ReleaseMigrationLockAsync(study.Id);
                await auditRepo.AddAsync(new MigrationAuditLog
                {
                    MigrationId      = migration.Id,
                    Action           = "C-MOVE",
                    Level            = "ERROR",
                    Result           = "ERROR",
                    StudyInstanceUid = study.StudyInstanceUid,
                    UserOrProcess    = workerId,
                    TechnicalMessage = $"Error de configuración (sin gastar reintento): {techMsg}",
                });
                return MoveOutcome.Config(MoveDestinationUnknownMessage(freshMigration, result.ErrorComment));
            }
            // 0xA7xx "Out of resources": el PACS origen está saturado. Transitorio: se
            // devuelve el estudio sin gastar reintento y cuenta para la auto-pausa.
            if (!success && result.DicomStatus is >= 0xA700 and <= 0xA7FF)
            {
                await studyRepo.ReleaseMigrationLockAsync(study.Id);
                await auditRepo.AddAsync(new MigrationAuditLog
                {
                    MigrationId      = migration.Id,
                    Action           = "C-MOVE",
                    Level            = "WARN",
                    Result           = "ERROR",
                    StudyInstanceUid = study.StudyInstanceUid,
                    UserOrProcess    = workerId,
                    TechnicalMessage = $"PACS origen sin recursos (reintento sin penalizar): {techMsg}",
                });
                return MoveOutcome.Transient;
            }

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
                return MoveOutcome.Done;  // no fue error de conexión
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
                return MoveOutcome.Transient;   // fue error de conexión
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
                    return MoveOutcome.Transient;   // tratar como error de conexión (transitorio)
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
                return MoveOutcome.Done;
            }
            else
            {
                // Fallo real del estudio: el origen respondió y procesó, pero alguna
                // instancia falló (Failed > 0), o el C-MOVE se cortó a medias sin respuesta
                // final. Se gasta un reintento: así un estudio que se corta siempre acaba
                // "Failed" y visible, en vez de quedar en bucle o darse por migrado.
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
                return MoveOutcome.Done;
            }
        }
        catch (OperationCanceledException)
        {
            // Cancelación (pausa/parada): devolver el estudio a Pending sin penalizar.
            await studyRepo.ReleaseMigrationLockAsync(study.Id);
            throw;
        }
        // Los errores de la BASE DE DATOS (p. ej. caída justo tras un C-MOVE correcto, al
        // marcar 'Migrated') no se capturan aquí: no son fallo del PACS y no deben contar
        // hacia la auto-pausa por "errores de conexión con el origen". Suben al bucle de
        // RunWorkerLoopAsync, que libera el estudio y reintenta con backoff.
        catch (Exception ex) when (!ConnBackoff.IsDatabaseError(ex))
        {
            // Excepción inesperada de red/transporte = error de conexión: reintentar.
            await studyRepo.ReleaseMigrationLockAsync(study.Id);
            logger.LogWarning(ex, "[{Worker}] Error de conexión migrando {Uid}. Estudio devuelto a Pendiente.",
                workerId, study.StudyInstanceUid);
            return MoveOutcome.Transient;
        }
        finally
        {
            heartbeatCts.Cancel();
            await heartbeat;   // nunca lanza: traga sus propios errores y la cancelación
        }
    }

    /// <summary>Explicación de 0xA801 con los datos exactos que hay que dar de alta.</summary>
    private static string MoveDestinationUnknownMessage(MigrationEntity m, string? pacsComment)
    {
        var dest   = m.DestNode;
        var origin = m.OriginNode;
        return $"El PACS origen {origin?.Alias} ({origin?.RemoteAet}) no conoce el AE destino '{dest?.RemoteAet}' " +
               "(0xA801, Move Destination unknown" + (pacsComment is null ? "" : $": «{pacsComment}»") + "). " +
               $"Dalo de alta en el PACS origen como destino de C-MOVE: AE '{dest?.RemoteAet}', " +
               $"IP {dest?.RemoteHost}, puerto {dest?.RemotePort}. La migración se ha pausado; reanúdala cuando esté corregido.";
    }

    /// <summary>Renueva periódicamente el LockDate del estudio en 'Migrating' mientras dura
    /// su C-MOVE, para que la limpieza de caducados no lo rescate estando vivo. Un fallo
    /// puntual al renovar solo se registra (hay margen de MigratingStaleAfter).</summary>
    private async Task MigrationHeartbeatAsync(long studyId, string workerId, CancellationToken ct)
    {
        try
        {
            using var timer = new PeriodicTimer(StudyRepository.MigratingHeartbeat);
            while (await timer.WaitForNextTickAsync(ct))
            {
                try
                {
                    using var scope = scopeFactory.CreateScope();
                    if (!await StudyRepo(scope).RenewMigrationLockAsync(studyId, workerId))
                        return;   // el estudio ya no es nuestro (terminado o rescatado)
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "[{Worker}] No se pudo renovar el lock del estudio {Id} en 'Migrating'.",
                        workerId, studyId);
                }
            }
        }
        catch (OperationCanceledException) { /* fin normal del C-MOVE */ }
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
        var paused = await MigrationRepo(scope).GetByIdAsync(migrationId);
        await AuditRepo(scope).AddAsync(new MigrationAuditLog
        {
            MigrationId = migrationId, Action = "PAUSE", Result = "OK",
            UserOrProcess = AuditActorHelper.Actor("SYSTEM", paused?.CreatedBy),
            TechnicalMessage = "Workers detenidos. Migración pausada."
        });
    }

    public Task ResumeAsync(int migrationId, CancellationToken ct = default)
        => StartAsync(migrationId, ct);
}
