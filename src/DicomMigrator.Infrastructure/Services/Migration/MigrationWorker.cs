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

    // ── Pausa ordenada (auto-pausa y cierre de ventana) ──────────────────────
    // Cancelar el token corta los C-MOVE en curso: el PACS origen interrumpe el envío al
    // destino y la imagen que estaba mandando llega a medias (en el VNA: «Did Not Write a full
    // PixelData length», SERIOUS FAILURE; producción, 23-28/10/2026). Y un estudio grande que
    // se corta en cada pausa vuelve a la cola sin gastar intento y no termina nunca.
    // En una pausa ordenada se «drena» la ejecución: los workers dejan de tomar estudios,
    // terminan el que tienen en curso (el vigilante de inactividad del C-MOVE sigue
    // protegiendo de un origen colgado) y salen. Su CTS pasa de _cts a _draining, así que se
    // puede reanudar sin esperar a que acaben; la pausa manual y el apagado sí los cortan.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, CancellationTokenSource> _drain    = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, CancellationTokenSource> _draining = new();

    // Número de ejecución, para que los nombres de worker no se repitan entre una ejecución
    // que se está drenando y la siguiente (ReleaseLocksAsync libera por nombre de worker).
    private static int _runSeq;

    /// <summary>Pausa ordenada de la ejecución en curso: deja de tomar estudios y deja terminar
    /// los C-MOVE en curso. No toca el estado en BD.</summary>
    private void DrainRun(int migrationId, CancellationTokenSource cts, CancellationTokenSource drain)
    {
        try { drain.Cancel(); } catch (ObjectDisposedException) { }
        if (_cts.TryRemove(new KeyValuePair<int, CancellationTokenSource>(migrationId, cts)))
            _draining[migrationId] = cts;
    }

    /// <summary>Corta también los workers que se estaban drenando (pausa manual, parada).</summary>
    private void CancelDraining(int migrationId)
    {
        if (_draining.TryGetValue(migrationId, out var d))
            try { d.Cancel(); } catch (ObjectDisposedException) { }
    }

    /// <summary>Cancela TODOS los workers activos de forma inmediata, sin tocar el estado
    /// en BD. Pensado para el apagado del proceso: los estudios en curso quedan como
    /// huérfanos y se recuperan al reiniciar (ReleaseOrphanLocksAsync + auto-reanudación).
    /// No marca pausa de usuario; la migración sigue figurando como 'Running' para que se
    /// reanude sola al volver a arrancar.</summary>
    public void CancelAllForShutdown()
    {
        foreach (var kv in _cts.Concat(_draining))
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

    public async Task<bool> StartAsync(int migrationId, CancellationToken ct = default)
    {
        // ── Verja de licencia ────────────────────────────────────────────────
        // Choke point ÚNICO de arranque de migraciones (incluye la auto-reanudación
        // al iniciar el servicio): si la licencia no es válida, no se inicia ningún
        // worker. La UI ya avisa con un banner y la página de Licencia.
        var lic = licenseCache.Current;
        if (!lic.CanMigrate)
        {
            // Ya tiene workers (doble clic, o un arranque anterior con licencia válida): no se
            // toca su estado aquí; si la licencia ha dejado de valer, la pausa ordenada la hace
            // EnforceLicenseAsync.
            if (_cts.ContainsKey(migrationId)) return true;

            logger.LogError("Migración {Id} NO se inicia: licencia no válida ({Verdict}) — {Reason}",
                migrationId, lic.Verdict, lic.Reason);
            try
            {
                // Queda en pausa POR LICENCIA (OPS-5). Antes no se tocaba el estado: una
                // migración que estaba en marcha al reiniciar el servicio seguía «Running» sin
                // workers, y la pantalla decía «Migración iniciada». Ahora queda «Paused» con
                // la marca, y se iniciará sola al instalar una licencia válida. Se quitan las
                // marcas de auto-pausa y de ventana: así ni la auto-reanudación ni el
                // planificador la reintentan en bucle mientras no haya licencia.
                using var scope = scopeFactory.CreateScope();
                var repo = MigrationRepo(scope);
                await repo.UpdateStatusAsync(migrationId, "Paused");
                await repo.SetPausedByLicenseAsync(migrationId, true);
                await repo.SetMigrationAutoPausedAsync(migrationId, false);
                await repo.SetPausedByWindowAsync(migrationId, false);
                await AuditRepo(scope).AddAsync(new MigrationAuditLog
                {
                    MigrationId      = migrationId,
                    Action           = "LICENSE",
                    Level            = "WARN",
                    Result           = "ERROR",
                    UserOrProcess    = "LICENSE",
                    TechnicalMessage = $"Inicio bloqueado por licencia: {lic.Verdict} — {lic.Reason}. " +
                                       "Queda en pausa y se iniciará sola al instalar una licencia válida.",
                });
            }
            catch (Exception ex) { logger.LogWarning(ex, "Migración {Id}: no se pudo marcar la pausa por licencia.", migrationId); }
            return false;
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
            return true;
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
            var previousAlive = false;
            if (_runs.TryGetValue(migrationId, out var previousRun) && !previousRun.IsCompleted)
            {
                var finished = await Task.WhenAny(previousRun, Task.Delay(PreviousRunWait, ct)) == previousRun;
                if (!finished)
                {
                    // Sigue viva (p. ej. drenándose tras una auto-pausa, con un estudio grande en
                    // curso): sus estudios NO son huérfanos. Los cierran ellos al terminar;
                    // rescatarlos lanzaría un segundo C-MOVE del mismo estudio.
                    previousAlive = true;
                    logger.LogWarning("Migración {Id}: la ejecución anterior aún termina su(s) estudio(s) en curso; " +
                        "se arranca sin tocarlos.", migrationId);
                }
            }
            if (!previousAlive)
            {
                var rescued = await StudyRepo(scope).ReleaseOrphanMigrationLocksAsync(migrationId);
                if (rescued > 0)
                    logger.LogWarning("Migración {Id}: {N} estudio(s) huérfano(s) en 'Queued'/'Migrating' " +
                        "devuelto(s) a 'Pending' (sin consumir reintento).", migrationId, rescued);
            }

            // Rango de modalidad de los estudios en cola, según la prioridad ACTUAL de la
            // migración: lo usa el índice de la cola para que cada worker tome su estudio
            // en orden sin ordenar todos los pendientes (CONC-9). Recoge los estudios
            // poblados desde el último arranque y los cambios de prioridad hechos con la
            // migración en pausa. Solo actualiza las filas cuyo rango cambia.
            var ranked = await StudyRepo(scope).RecomputeModalityRankAsync(migrationId, ParsePriority(migration.ModalityPriority));
            if (ranked > 0)
                logger.LogInformation("Migración {Id}: prioridad de modalidad recalculada en {N} estudio(s).", migrationId, ranked);

            await MigrationRepo(scope).UpdateStatusAsync(migrationId, "Running");
            // Acción manual: limpiar el flag de auto-pausa por conexión.
            await MigrationRepo(scope).SetMigrationAutoPausedAsync(migrationId, false);
            // Y el de pausa por ventana (CONC-8) y por licencia (OPS-5): ya está en marcha.
            await MigrationRepo(scope).SetPausedByWindowAsync(migrationId, false);
            await MigrationRepo(scope).SetPausedByLicenseAsync(migrationId, false);
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
        var priorities = ParsePriority(migration.ModalityPriority);
        var drain = new CancellationTokenSource();
        _drain[migrationId] = drain;
        var run = Interlocked.Increment(ref _runSeq);
        var tasks = Enumerable.Range(0, migration.WorkerThreads)
            .Select(i => RunWorkerLoopAsync(migration, $"WORKER-{i + 1}.{run}", priorities, cts, drain))
            .ToArray();

        // Fire and forget — completion handler runs when ALL workers exit
        // (either cancelled via Pause/Cancel, or naturally when the queue empties)
        _runs[migrationId] = Task.WhenAll(tasks).ContinueWith(async _ =>
        {
            // Cancelado o drenado = pausa/parada (manual, auto-pausa, ventana…): el estado ya
            // está puesto, no se cierra la migración.
            var wasCancelled = cts.IsCancellationRequested || drain.IsCancellationRequested;
            // Retirar SOLO nuestros CTS: si tras una pausa ya se reanudó la migración, las
            // entradas son las de la nueva ejecución y no debemos quitarlas ni liberarlas.
            var pair = new KeyValuePair<int, CancellationTokenSource>(migrationId, cts);
            if (_cts.TryRemove(pair) | _draining.TryRemove(pair))
                cts.Dispose();
            _drain.TryRemove(new KeyValuePair<int, CancellationTokenSource>(migrationId, drain));
            drain.Dispose();

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

        // Verificación que quedó en espera (al día con la migración parada o sin empezar):
        // ahora que llegan estudios migrados, relanzarla (CONC-4).
        await RelaunchWaitingVerificationAsync(migrationId, "la migración se ha iniciado");
        return true;
    }

    /// <inheritdoc/>
    public async Task<int> EnforceLicenseAsync(CancellationToken ct = default)
    {
        var lic = licenseCache.Current;
        using var scope = scopeFactory.CreateScope();
        var repo = MigrationRepo(scope);
        var changed = 0;

        foreach (var m in await repo.GetAllAsync())
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (!lic.CanMigrate && m.Status == "Running")
                {
                    // Licencia caducada (o inválida) con la migración en marcha: pausa
                    // como el botón Pausar (el C-MOVE en curso vuelve a la cola sin gastar intento),
                    // con la marca para reanudarla sola en cuanto vuelva a haber licencia.
                    logger.LogWarning("Licencia no válida ({Verdict}): se pausa la migración {Id} ('{Name}').",
                        lic.Verdict, m.Id, m.Name);
                    await PauseAsync(m.Id);
                    await repo.SetPausedByLicenseAsync(m.Id, true);
                    await AuditRepo(scope).AddAsync(new MigrationAuditLog
                    {
                        MigrationId = m.Id, Action = "LICENSE", Level = "WARN", Result = "ERROR", UserOrProcess = "LICENSE",
                        TechnicalMessage = $"Migración pausada: licencia no válida ({lic.Verdict} — {lic.Reason}). " +
                                           "Se reanudará sola al instalar una licencia válida.",
                    });
                    try
                    {
                        await scope.ServiceProvider.GetRequiredService<INotificationService>()
                            .RaiseAsync(NotificationEvents.AutoPaused, m.Name, new (string, string)[]
                            {
                                ("Origen → Destino", $"{m.OriginNode?.Alias ?? "?"} → {m.DestNode?.Alias ?? "?"}"),
                                ("Proceso", "Migración"),
                                ("Motivo",  $"Licencia no válida: {lic.Reason}. Instala una licencia válida (menú Licencia); " +
                                            "la migración se reanudará sola."),
                            }, m.Id, "migration");
                    }
                    catch (Exception nex) { logger.LogWarning(nex, "Notificación de pausa por licencia (migración {Id}) falló.", m.Id); }
                    changed++;
                }
                else if (lic.CanMigrate && m.Status == "Paused" && m.PausedByLicense)
                {
                    logger.LogInformation("Licencia válida: se reanuda la migración {Id} ('{Name}'), que estaba en pausa por licencia.",
                        m.Id, m.Name);
                    if (await StartAsync(m.Id, ct)) changed++;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Una migración que no se puede pausar o reanudar (p. ej. se está poblando) no
                // impide atender a las demás.
                logger.LogWarning(ex, "Licencia: no se pudo aplicar el veredicto a la migración {Id}.", m.Id);
            }
        }
        return changed;
    }

    /// <summary>
    /// Relanza la verificación si quedó «al día» (VerificationStatus = Completed) y hay o
    /// habrá estudios por verificar (CONC-4). Es lo que la interfaz y la Ayuda prometían
    /// («se relanzará sola») y no ocurría: una verificación que vaciaba su cola con la
    /// migración en pausa, o lanzada antes que ella, no volvía a arrancar, y la migración
    /// se quedaba en «Migrated» sin pasar a «Completed».
    ///
    /// No toca una verificación pausada o detenida a mano (Paused/Idle), ni una que no se
    /// llegó a lanzar. Si ya no queda nada por migrar ni por verificar y la migración acaba
    /// de terminar sin fallos, la promueve a «Completed» (migrada y verificada).
    /// Best-effort: un fallo aquí no afecta a la migración.
    /// </summary>
    private async Task RelaunchWaitingVerificationAsync(int migrationId, string reason)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var mig = await MigrationRepo(scope).GetByIdAsync(migrationId);
            if (mig?.VerificationStatus != "Completed") return;

            var studies = StudyRepo(scope);
            if (await studies.HasVerificationWorkPendingAsync(migrationId)
                || await studies.HasMigrationWorkPendingAsync(migrationId))
            {
                logger.LogInformation("Migración {Id}: se relanza la verificación, que estaba en espera ({Reason}).",
                    migrationId, reason);
                await scope.ServiceProvider.GetRequiredService<IVerificationService>().StartVerificationAsync(migrationId);
            }
            else if (mig.Status == "Migrated")
            {
                await MigrationRepo(scope).UpdateStatusAsync(migrationId, "Completed");
                logger.LogInformation("Migración {Id} promovida a Completed (migrada y verificada).", migrationId);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Migración {Id}: no se pudo relanzar la verificación en espera ({Reason}). " +
                "Lánzala a mano desde la ficha.", migrationId, reason);
        }
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

        // Línea de tiempo para la pestaña Rendimiento, a salvo de la purga de auditoría.
        // Primero se vuelca el búfer de auditoría para que entren los últimos eventos.
        // Nunca impide terminar: el mantenimiento la vuelve a guardar antes de purgar.
        try
        {
            if (scope.ServiceProvider.GetService<DicomMigrator.Infrastructure.Data.AuditLogBuffer>() is { } auditBuffer)
                await auditBuffer.FlushAsync();
            await StudyRepo(scope).SavePerformanceEventsAsync(migrationId);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Migración {Id}: no se pudo guardar la línea de tiempo de rendimiento.", migrationId);
        }

        // Verificación en espera: verificar lo que quede o, si ya está todo verificado,
        // promover la migración a Completed (CONC-4).
        await RelaunchWaitingVerificationAsync(migrationId, "la migración ha terminado");

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
        string[] priorities, CancellationTokenSource cts, CancellationTokenSource drain)
    {
        var ct = cts.Token;
        // Esperas del bucle: las corta también el drenaje (pausa ordenada), para salir sin
        // esperar. El C-MOVE en curso usa solo 'ct': el drenaje no lo corta.
        using var idleCts = CancellationTokenSource.CreateLinkedTokenSource(ct, drain.Token);
        var idle = idleCts.Token;
        logger.LogInformation("Worker {Id} started for migration {MigId}", workerId, migration.Id);
        int emptyPolls = 0;
        int connErrors = 0;   // fallos de conexión consecutivos (origen caído o saturado, destino caído)
        string? lastCause = null, lastNode = null;   // el último, para el aviso de auto-pausa
        int unexpectedErrors = 0;   // errores inesperados seguidos (BD caída, etc.)
        bool releasePending = false; // la liberación de sus estudios falló (BD caída): repetirla

        try
        {
            while (!idle.IsCancellationRequested)
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
                        await Task.Delay(30_000, idle);
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
                        // Con SKIP LOCKED, "nada" ya no significa haber perdido la carrera: no hay
                        // ningún estudio disponible ahora. ¿Queda trabajo (en curso en otro worker
                        // o esperando su reintento)? EXISTS, sin agregar toda la migración.
                        var workLeft = await StudyRepo(scope).HasMigrationWorkPendingAsync(migration.Id);
                        if (!workLeft)
                        {
                            logger.LogInformation("Worker {Id} sees empty queue — exiting", workerId);
                            break;
                        }

                        emptyPolls++;
                        // Backoff: 5s normally, 30s if we've been empty many times
                        // (remaining studies are in RetryPending delay)
                        var delay = emptyPolls > 3 ? 30_000 : 5_000;
                        await Task.Delay(delay, idle);
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
                        lastCause = outcome.Cause;
                        lastNode  = outcome.Node;
                        // Si se acumulan unos pocos errores de conexión seguidos, el destino
                        // (o el origen) está caído: pausar pronto para no machacar en bucle.
                        // El servicio de auto-reanudación lo retomará cuando vuelva la conexión.
                        if (connErrors >= ConnBackoff.AutoPauseThreshold)
                        {
                            if (ConnBackoff.TryAnnouncePause("MIGRATE", migration.Id))
                            {
                                // Un correo por incidente (CONC-6): si la auto-reanudación
                                // vuelve a fallar sin haber migrado nada, no se avisa otra vez.
                                var newIncident = AutoPauseIncidents.RegisterPause("MIGRATE", migration.Id);
                                var reason = $"{connErrors} fallos seguidos que no son del estudio (conexión, PACS saturado o sin envío); el último: {lastCause}";
                                var node   = lastNode ?? "el nodo";
                                if (newIncident)
                                    logger.LogError("Migración {Id} pausada: {Reason}. Se reanudará sola cuando responda {Node}.",
                                        migration.Id, reason, node);
                                else
                                    logger.LogError("Migración {Id} pausada otra vez ({N}.ª del mismo incidente, sin aviso nuevo): {Reason}.",
                                        migration.Id, AutoPauseIncidents.Pauses("MIGRATE", migration.Id), reason);
                                await MigrationRepo(scope).UpdateStatusAsync(migration.Id, "Paused");
                                await AuditRepo(scope).AddAsync(new MigrationAuditLog
                                {
                                    MigrationId = migration.Id, Action = "PAUSE", Level = "WARN", Result = "ERROR",
                                    UserOrProcess = workerId,
                                    TechnicalMessage = $"Auto-pausa: {reason}. Se reanudará sola cuando responda {node}.",
                                });
                                if (newIncident)
                                {
                                    try
                                    {
                                        await scope.ServiceProvider.GetRequiredService<INotificationService>()
                                            .RaiseAsync(NotificationEvents.AutoPaused, migration.Name, new (string, string)[]
                                            {
                                                ("Origen → Destino", $"{migration.OriginNode?.Alias ?? "?"} → {migration.DestNode?.Alias ?? "?"}"),
                                                ("Proceso", "Migración"),
                                                ("Motivo",  reason),
                                                ("Nodo",    node),
                                            }, migration.Id, "migration");
                                    }
                                    catch (Exception nex) { logger.LogWarning(nex, "Notificación de auto-pausa (migración {Id}) falló.", migration.Id); }
                                }
                                await MigrationRepo(scope).SetMigrationAutoPausedAsync(migration.Id, true);
                            }
                            // Pausa ORDENADA: los demás workers dejan de tomar estudios (no
                            // esperan a llegar cada uno al umbral) pero terminan el C-MOVE que
                            // tengan en curso, que sí funcionaba: cortarlo dejaba una imagen a
                            // medias en el destino. El handler de finalización ve el drenaje
                            // y NO cierra la migración.
                            DrainRun(migration.Id, cts, drain);
                            break;
                        }
                        // Espera corta y fija mientras acumulamos hacia el umbral, para que
                        // la auto-pausa se dispare en un tiempo razonable (no un backoff largo
                        // que retrasaría la pausa varios minutos).
                        await Task.Delay(ConnBackoff.PrePauseWait, idle);
                    }
                    else
                    {
                        connErrors = 0;
                        // El origen respondió (estudio migrado o fallo propio del estudio):
                        // la conexión funciona, se cierra el incidente de auto-pausa si lo había.
                        AutoPauseIncidents.Resolve("MIGRATE", migration.Id);
                    }
                }
                catch (OperationCanceledException) when (idle.IsCancellationRequested)
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
                    await Task.Delay(wait, idle);
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
    /// <param name="Cause">Fallo transitorio: qué falló, para el aviso de auto-pausa
    /// («el destino X no responde»…). Antes el aviso decía siempre «origen» (CONC-6).</param>
    /// <param name="Node">Fallo transitorio: alias del nodo que falló.</param>
    private readonly record struct MoveOutcome(bool IsTransient, string? ConfigurationError,
        string? Cause = null, string? Node = null)
    {
        public static readonly MoveOutcome Done = new(false, null);
        public static MoveOutcome Transient(string cause, string? node) => new(true, null, cause, node);
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
        if (!await studyRepo.MarkMigratingAsync(study.Id, workerId))
        {
            // Entre tomarlo y empezar, el estudio dejó de ser nuestro (CONC-10): no se lanza
            // el C-MOVE. Con las acciones manuales ya atómicas es muy raro; puede pasar si lo
            // rescató la caducidad de bloqueos.
            logger.LogWarning("[{Worker}] {Uid}: el estudio ya no está en cola para este worker; no se migra.",
                workerId, study.StudyInstanceUid);
            return MoveOutcome.Done;
        }
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
            var baseMsg = techMsg;
            string? rejection = null;   // texto del rechazo del destino, si lo hubo (para detectar repeticiones)

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
            else if (!success && result.DicomStatus is >= 0xA700 and <= 0xA7FF && result.Failed == 0)
            {
                techMsg += " — El PACS origen no tiene recursos para atender el C-MOVE ahora (saturado). " +
                           "Se reintentará sin gastar intento." +
                           (result.ErrorComment is null ? "" : $" Mensaje del PACS: «{result.ErrorComment}».");
            }
            else if (!success && result.Failed > 0 && result.Completed > 0)
            {
                // El destino guardó una parte y rechazó el resto (SOP Class no admitida,
                // objeto que no acepta…). El rechazo con Completed = 0 se explica más abajo,
                // tras comprobar que el destino responde.
                rejection = RejectedByDestination(result, freshMigration);
                techMsg += rejection;
            }
            else if (!success && result.Completed == 0 && result.Failed == 0)
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
            // Solo si no hubo sub-operaciones fallidas: con Failed > 0, 0xA702 («Unable to
            // perform sub-operations») es lo que muchos PACS devuelven cuando fallaron TODOS
            // los envíos al destino (destino caído o que rechaza las instancias), y eso se
            // decide más abajo comprobando el destino.
            if (!success && result.DicomStatus is >= 0xA700 and <= 0xA7FF && result.Failed == 0)
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
                return MoveOutcome.Transient(
                    $"el origen {freshMigration.OriginNode?.Alias} está saturado (0x{result.DicomStatus:X4})",
                    freshMigration.OriginNode?.Alias);
            }

            if (success)
            {
                OriginEmptyMoves.RecordSent(migration.Id);
                await RecordAttemptAsync(studyRepo, study, workerId, "Migrated", null);
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
                return MoveOutcome.Transient(   // fue error de conexión
                    $"no se pudo conectar con el origen {freshMigration.OriginNode?.Alias}",
                    freshMigration.OriginNode?.Alias);
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
                    return MoveOutcome.Transient(   // tratar como error de conexión (transitorio)
                        $"el destino {freshMigration.DestNode!.Alias} no responde (C-ECHO)",
                        freshMigration.DestNode!.Alias);
                }
                // El destino SÍ responde y el origen dice que no pudo guardar nada en él
                // (Failed > 0): el DESTINO rechazó todas las instancias (p. ej. un estudio con
                // solo un objeto de una SOP Class que no admite). Es un fallo del estudio en
                // ese destino: gasta intento, y NO cuenta como «el origen no envía», que
                // llevaba a la auto-pausa por algo que no es del origen.
                if (result.Failed > 0)
                {
                    OriginEmptyMoves.RecordSent(migration.Id);
                    rejection = RejectedByDestination(result, freshMigration);
                    techMsg += rejection;
                    var cur      = await studyRepo.GetByIdAsync(study.Id);
                    var attempts = (cur?.RetryCount ?? study.RetryCount) + 1;
                    var next     = attempts >= migration.MaxRetries ? "Failed" : "RetryPending";
                    if (next != "Failed" && SameRejectionAsBefore(study.LastError, result, rejection))
                    {
                        next = "Failed";
                        techMsg += RepeatedRejectionNote;
                    }
                    await RecordAttemptAsync(studyRepo, study, workerId, next,
                        $"[Intento {attempts}/{migration.MaxRetries}] {techMsg}");
                    await auditRepo.AddAsync(new MigrationAuditLog
                    {
                        MigrationId      = migration.Id,
                        Action           = "C-MOVE",
                        Level            = next == "Failed" ? "ERROR" : "WARN",
                        Result           = "ERROR",
                        StudyInstanceUid = study.StudyInstanceUid,
                        UserOrProcess    = workerId,
                        TechnicalMessage = $"Fallo intento {attempts}/{migration.MaxRetries}. {techMsg}",
                    });
                    return MoveOutcome.Done;
                }

                // El destino SÍ responde. ¿Episodio del origen? Si la mayoría de los últimos
                // C-MOVE de la migración tampoco enviaron nada, el problema es del origen
                // (no de este estudio): devolverlo sin gastar intento y contar como fallo
                // transitorio, que lleva a la auto-pausa (ver OriginEmptyMoves).
                if (OriginEmptyMoves.RecordEmpty(migration.Id, study.StudyInstanceUid, out var empties))
                {
                    // A 'RetryPending', no a 'Pending': si volviera al principio de la cola, otro
                    // worker lo tomaría al instante y repetiría el mismo C-MOVE vacío en bucle.
                    await studyRepo.DeferMigrationAsync(study.Id);
                    logger.LogWarning("[{Worker}] {Uid}: el origen no envió ninguna instancia, como en {N} de los últimos {W} " +
                        "estudios distintos. Episodio del origen: estudio apartado sin gastar intento.",
                        workerId, study.StudyInstanceUid, empties, OriginEmptyMoves.Window);
                    await auditRepo.AddAsync(new MigrationAuditLog
                    {
                        MigrationId      = migration.Id,
                        Action           = "C-MOVE",
                        Level            = "WARN",
                        Result           = "ERROR",
                        StudyInstanceUid = study.StudyInstanceUid,
                        UserOrProcess    = workerId,
                        TechnicalMessage = $"Origen sin envío (reintento sin penalizar): {baseMsg} — {empties} estudios distintos de los últimos " +
                                           $"{OriginEmptyMoves.Window} C-MOVE tampoco recibieron nada. El problema parece del PACS " +
                                           "origen (tarea programada, almacenamiento ocupado…), no del estudio.",
                    });
                    return MoveOutcome.Transient(
                        $"el origen {freshMigration.OriginNode?.Alias} responde pero no envía imágenes " +
                        $"({empties} estudios distintos sin ninguna instancia en los últimos {OriginEmptyMoves.Window} C-MOVE)",
                        freshMigration.OriginNode?.Alias);
                }

                // Caso aislado: el estudio realmente no se pudo migrar.
                var currentStudy = await studyRepo.GetByIdAsync(study.Id);
                var retries      = (currentStudy?.RetryCount ?? study.RetryCount) + 1;
                var nextStatus   = retries >= migration.MaxRetries ? "Failed" : "RetryPending";

                await RecordAttemptAsync(studyRepo, study, workerId, nextStatus,
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
                OriginEmptyMoves.RecordSent(migration.Id);
                var currentStudy = await studyRepo.GetByIdAsync(study.Id);
                var retries      = (currentStudy?.RetryCount ?? study.RetryCount) + 1;
                var nextStatus   = retries >= migration.MaxRetries ? "Failed" : "RetryPending";
                if (nextStatus != "Failed" && rejection is not null && SameRejectionAsBefore(study.LastError, result, rejection))
                {
                    nextStatus = "Failed";
                    techMsg += RepeatedRejectionNote;
                }

                await RecordAttemptAsync(studyRepo, study, workerId, nextStatus,
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
            return MoveOutcome.Transient($"error de red con el origen {migration.OriginNode?.Alias}",
                migration.OriginNode?.Alias);
        }
        finally
        {
            heartbeatCts.Cancel();
            await heartbeat;   // nunca lanza: traga sus propios errores y la cancelación
        }
    }

    /// <summary>Nota del motivo cuando un rechazo se repite idéntico y se deja de reintentar.</summary>
    private const string RepeatedRejectionNote =
        " Mismo rechazo que en el intento anterior: no se reintenta más («Reintentar fallidos» lo vuelve a intentar con sus intentos completos).";

    /// <summary>¿El intento anterior del estudio terminó con exactamente el mismo rechazo del
    /// destino (mismo estado, mismos contadores y mismas instancias rechazadas)? Entonces
    /// reintentarlo no cambiará nada: el rechazo es del destino y es estable. En un lote de
    /// producción, los reintentos de 26 estudios así ocuparon ~10 min sin poder salir bien.
    /// <paramref name="previousError"/> es el LastError del estudio tal como se tomó de la cola:
    /// MarkMigratingAsync lo borra al empezar el intento.</summary>
    private static bool SameRejectionAsBefore(string? previousError, CMoveResult r, string rejection) =>
        previousError is not null
        && previousError.Contains(rejection, StringComparison.Ordinal)
        && previousError.Contains($"Status=0x{r.DicomStatus:X4} Completed={r.Completed} ", StringComparison.Ordinal)
        && previousError.Contains($" Failed={r.Failed} ", StringComparison.Ordinal);

    /// <summary>Máximo de UIDs de instancias rechazadas que se guardan en LastError.</summary>
    private const int MaxRejectedUidsShown = 50;

    /// <summary>Texto del rechazo de instancias por el destino, con los SOPInstanceUID que
    /// indique el origen (0008,0058), si los envía.</summary>
    private static string RejectedByDestination(CMoveResult r, MigrationEntity m)
    {
        var dest = m.DestNode?.Alias ?? "el destino";
        // Estudio entero rechazado: en producción (29/10/2026) los 26 fallos de un lote eran de
        // 20 pacientes, todos con TODOS sus estudios rechazados y ninguno a medias: la causa
        // estaba en el paciente en el destino, no en el tipo de objeto. Con una parte guardada,
        // lo habitual sí es una SOP Class o un objeto concreto que el destino no admite.
        var text = r.Completed > 0
            ? $" — El destino {dest} rechazó {r.Failed} instancia(s) y guardó {r.Completed}: suele ser una SOP Class que no " +
              "admite o un objeto concreto que no acepta. Reintentar no lo arregla si el destino no cambia."
            : $" — El destino {dest} rechazó las {r.Failed} instancia(s) y no guardó ninguna. Cuando rechaza el estudio entero, la " +
              "causa suele estar en el destino y no en las imágenes: el paciente ya existe allí con otros datos (nombre, sexo, " +
              "fecha de nacimiento o emisor del ID), está en cuarentena o fusionado, o el estudio está bloqueado. Revisa el " +
              "registro del destino. Si fallan todos los estudios de un mismo paciente, casi seguro es eso.";
        if (r.FailedSopInstanceUids.Count > 0)
        {
            var shown = r.FailedSopInstanceUids.Take(MaxRejectedUidsShown).ToList();
            text += $" Instancias rechazadas según el origen: {string.Join(", ", shown)}" +
                    (r.FailedSopInstanceUids.Count > shown.Count ? $" (y {r.FailedSopInstanceUids.Count - shown.Count} más)." : ".");
        }
        else
            text += " El origen no indicó cuáles: búscalas en el registro del destino.";
        if (r.ErrorComment is not null) text += $" Mensaje del PACS: «{r.ErrorComment}».";
        return text;
    }

    /// <summary>Registra el resultado del intento solo si el estudio sigue siendo de este worker
    /// (CONC-10). Si no, otro proceso lo tiene ya (rescate de bloqueo caducado): se deja
    /// constancia en el log en vez de pisar su estado.</summary>
    private async Task RecordAttemptAsync(IStudyRepository studyRepo, MigrationStudy study, string workerId,
        string status, string? error)
    {
        if (!await studyRepo.UpdateStatusAsync(study.Id, status, workerId, error))
            logger.LogWarning("[{Worker}] {Uid}: el estudio cambió de estado durante el C-MOVE; no se registra " +
                "«{Status}» para no pisarlo.", workerId, study.StudyInstanceUid, status);
    }

    /// <summary>Lista de prioridad de modalidades de la migración ("CT,MR,…"), sin
    /// espacios ni entradas vacías. Por defecto CT, MR, MG, CR, OT.</summary>
    /// <summary>Lista de prioridad de modalidades de la migración, en mayúsculas. Vacía
    /// significa SIN prioridad: todos los estudios quedan en la misma posición de la cola y
    /// se migran solo por fecha (más recientes o más antiguos primero). Antes una prioridad
    /// vacía se sustituía por «CT,MR,MG,CR,OT», que no se veía en ninguna pantalla.</summary>
    internal static string[] ParsePriority(string? modalityPriority) =>
        (modalityPriority ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(m => m.ToUpperInvariant())
            .ToArray();

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

    public async Task PauseAsync(int migrationId, bool byWindow = false)
    {
        if (byWindow)
        {
            // Cierre de ventana: pausa ORDENADA, como la auto-pausa. Los C-MOVE en curso
            // terminan (unos minutos después del cierre, como mucho lo que tarde ese estudio)
            // en vez de cortarse y dejar una imagen a medias en el destino.
            if (_cts.TryGetValue(migrationId, out var cur) && _drain.TryGetValue(migrationId, out var dr))
                DrainRun(migrationId, cur, dr);
        }
        else
        {
            if (_cts.TryRemove(migrationId, out var cts))
            {
                await cts.CancelAsync();
                cts.Dispose();
            }
            // Pausa o parada manual, o licencia: inmediata, también para lo que se drenaba.
            CancelDraining(migrationId);
        }
        using var scope = scopeFactory.CreateScope();
        await MigrationRepo(scope).UpdateStatusAsync(migrationId, "Paused");
        // Pausa/parada manual: limpiar el flag de auto-pausa (no debe auto-reanudar).
        await MigrationRepo(scope).SetMigrationAutoPausedAsync(migrationId, false);
        // Solo la pausa del planificador se reanuda al abrirse la ventana; una pausa
        // manual hecha mientras estaba pausada por ventana anula esa reanudación (CONC-8).
        await MigrationRepo(scope).SetPausedByWindowAsync(migrationId, byWindow);
        // Lo mismo con la pausa por licencia (OPS-5): EnforceLicenseAsync la vuelve a marcar.
        await MigrationRepo(scope).SetPausedByLicenseAsync(migrationId, false);
        if (byWindow) return;   // el planificador deja su propia entrada (WINDOW_CLOSE)
        var paused = await MigrationRepo(scope).GetByIdAsync(migrationId);
        await AuditRepo(scope).AddAsync(new MigrationAuditLog
        {
            MigrationId = migrationId, Action = "PAUSE", Result = "OK",
            UserOrProcess = AuditActorHelper.Actor("SYSTEM", paused?.CreatedBy),
            TechnicalMessage = "Workers detenidos. Migración pausada."
        });
    }

    public Task<bool> ResumeAsync(int migrationId, CancellationToken ct = default)
        => StartAsync(migrationId, ct);
}
