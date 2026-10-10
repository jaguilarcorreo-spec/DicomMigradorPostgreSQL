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
// WINDOW SCHEDULER
// ══════════════════════════════════════════════════════════════════════════════

public class WindowScheduler(
    IServiceScopeFactory scopeFactory,
    IMigrationWorker worker,
    ILogger<WindowScheduler> logger) : IWindowScheduler
{
    // Reanudación por ventana que no llegó a arrancar (licencia no válida, poblado en
    // curso…): no reintentar cada minuto, para no llenar el log y la auditoría.
    private readonly Dictionary<int, DateTime> _resumeRetryAfter = new();
    private static readonly TimeSpan ResumeRetryInterval = TimeSpan.FromMinutes(15);

    /// <summary>Evalúa el conjunto de tramos de una migración. Abierto = lo está
    /// CUALQUIERA de ellos. Sin tramos definidos → sin restricción horaria (true).</summary>
    public bool IsWindowOpen(IEnumerable<ExecutionWindow>? windows)
    {
        if (windows is null) return true;
        var list = windows as IReadOnlyList<ExecutionWindow> ?? windows.ToList();
        if (list.Count == 0) return true;
        foreach (var w in list)
            if (IsWindowOpen(w)) return true;
        return false;
    }

    // Días ISO (1=Lun … 7=Dom) a partir del CSV de EnabledDays.
    private static HashSet<int> ParseDays(string? csv) =>
        (csv ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(d => int.TryParse(d, out var n) ? n : -1)
            .Where(n => n is >= 1 and <= 7)
            .ToHashSet();

    private static int IsoDow(DateTime d) =>
        d.DayOfWeek == DayOfWeek.Sunday ? 7 : (int)d.DayOfWeek;

    public bool IsWindowOpen(ExecutionWindow window)
    {
        try
        {
            var tz  = TimeZoneInfo.FindSystemTimeZoneById(window.TimeZoneId);
            var now = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, tz);
            var currentTime = new TimeOnly(now.Hour, now.Minute);

            var enabledDays = ParseDays(window.EnabledDays);
            if (enabledDays.Count == 0) return false;

            var todayIso = IsoDow(now);

            // Tramo de 24 h: cubre el día natural completo de cada día habilitado.
            // NO se prolonga al día siguiente (S-D a 24 h acaba el domingo a las 23:59).
            if (window.AllDay) return enabledDays.Contains(todayIso);

            // Tramo normal dentro del mismo día.
            if (!window.CrossesMidnight)
                return enabledDays.Contains(todayIso)
                    && currentTime >= window.StartTime
                    && currentTime <= window.EndTime;

            // ── Tramo que CRUZA MEDIANOCHE — manda el día de INICIO ──────────────
            // "L-V de 22:00 a 06:00" = las cinco noches de lunes a viernes, cada una
            // prolongándose hasta las 06:00 del día siguiente. Antes se miraba el día
            // en que estaba el reloj, con dos efectos poco intuitivos: la noche del
            // viernes se cortaba en seco a medianoche, y en cambio la madrugada del
            // domingo al lunes sí migraba pese a ser fin de semana.

            // Parte de hoy anterior a medianoche: [Start, 24:00) → la abre HOY.
            if (currentTime >= window.StartTime)
                return enabledDays.Contains(todayIso);

            // Cola de la ventana que arrancó AYER: [00:00, End] → la abre AYER.
            if (currentTime <= window.EndTime)
                return enabledDays.Contains(IsoDow(now.AddDays(-1)));

            return false;
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

                var liveIds = migrations.Select(m => m.Id).ToHashSet();
                foreach (var orphanId in _resumeRetryAfter.Keys.Where(k => !liveIds.Contains(k)).ToList())
                    _resumeRetryAfter.Remove(orphanId);

                // ── Decisión por ESTADO, no por transición (CONC-8) ──────────────────
                // Antes se recordaba en memoria si la ventana estaba abierta en la vuelta
                // anterior y se actuaba al cambiar. Tres fallos: al abrirse reanudaba
                // CUALQUIER migración en pausa (también las pausadas a mano o por un error
                // de configuración); tras reiniciar el servicio con la ventana ya abierta no
                // veía el cambio y la migración pausada por la ventana se quedaba parada
                // (perdía la noche o el fin de semana); y si se quitaban todas las ventanas,
                // la pausada por ventana no se reanudaba nunca.
                // Ahora, cada minuto:
                //   · En marcha con la ventana cerrada → pausar y marcar PausedByWindow.
                //   · Pausada POR LA VENTANA con la ventana abierta (o sin ventanas) → reanudar.
                // PausedByWindow está en la base, así que esto también vale tras un reinicio.
                foreach (var m in migrations)
                {
                    try
                    {
                        var open = IsWindowOpen(m.Windows);   // sin ventanas: siempre abierta

                        if (m.Status == "Running" && !open)
                        {
                            logger.LogInformation("Ventana cerrada: se pausa la migración {Id}.", m.Id);
                            await auditRepo.AddAsync(new MigrationAuditLog
                            {
                                MigrationId = m.Id, Action = "WINDOW_CLOSE", Result = "OK",
                                UserOrProcess = "SCHEDULER",
                                TechnicalMessage = "Ventana cerrada. Pausando workers; se reanudará al abrirse."
                            });
                            await worker.PauseAsync(m.Id, byWindow: true);
                        }
                        else if (m.Status == "Paused" && m.PausedByWindow && open)
                        {
                            if (_resumeRetryAfter.TryGetValue(m.Id, out var after) && DateTime.UtcNow < after)
                                continue;

                            var why = m.Windows.Count == 0
                                ? "Ya no tiene ventanas horarias. Reanudando workers."
                                : "Ventana abierta. Reanudando workers.";
                            logger.LogInformation("{Why} Migración {Id}.", why, m.Id);
                            await auditRepo.AddAsync(new MigrationAuditLog
                            {
                                MigrationId = m.Id, Action = "WINDOW_OPEN", Result = "OK",
                                UserOrProcess = "SCHEDULER", TechnicalMessage = why
                            });
                            await worker.ResumeAsync(m.Id, ct);

                            // Si no arrancó (licencia no válida…), sigue pausada y marcada:
                            // reintentar más tarde en vez de en cada vuelta.
                            var after2 = await migrationRepo.GetByIdAsync(m.Id);
                            if (after2 is { Status: "Paused", PausedByWindow: true })
                            {
                                _resumeRetryAfter[m.Id] = DateTime.UtcNow + ResumeRetryInterval;
                                logger.LogWarning("Migración {Id}: la ventana está abierta pero no se pudo reanudar; " +
                                    "se reintentará en {Min} min.", m.Id, (int)ResumeRetryInterval.TotalMinutes);
                            }
                            else
                                _resumeRetryAfter.Remove(m.Id);
                        }
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                    catch (Exception ex)
                    {
                        // Un fallo con una migración (p. ej. StartAsync rechaza arrancar porque
                        // se está poblando) no debe impedir atender a las demás.
                        _resumeRetryAfter[m.Id] = DateTime.UtcNow + ResumeRetryInterval;
                        logger.LogWarning(ex, "Planificador de ventanas: no se pudo atender la migración {Id}; " +
                            "se reintentará en {Min} min.", m.Id, (int)ResumeRetryInterval.TotalMinutes);
                    }
                }
            }
            catch (Exception ex)
            { logger.LogError(ex, "Scheduler error"); }

            await Task.Delay(TimeSpan.FromMinutes(1), ct);
        }
    }
}
