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
    private readonly Dictionary<int, bool> _lastWindowState = new();

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

                // Prune _lastWindowState entries for migrations that no longer exist.
                // Without this, deleted migrations leave permanent entries in the dictionary
                // and the dictionary grows over the process lifetime.
                var liveIds = migrations.Select(m => m.Id).ToHashSet();
                foreach (var orphanId in _lastWindowState.Keys.Where(k => !liveIds.Contains(k)).ToList())
                    _lastWindowState.Remove(orphanId);

                foreach (var m in migrations.Where(m => m is { Status: "Running" or "Paused" }
                                                     && m.Windows.Count > 0))
                {
                    var open = IsWindowOpen(m.Windows);
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
