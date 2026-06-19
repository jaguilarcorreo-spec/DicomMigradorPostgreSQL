using DicomMigrator.Core.Interfaces;
using DicomMigrator.Infrastructure.Data;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog.Core;
using Serilog.Events;

namespace DicomMigrator.Web.Services;

// ══════════════════════════════════════════════════════════════════════════════
//  LocalConfigState — copiado del Tester sin cambios
// ══════════════════════════════════════════════════════════════════════════════

public class LocalConfigState
{
    private string _localAet  = "MIGRATOR_SCU";
    private int    _localPort = 11113;

    public string LocalAet  => _localAet;
    public int    LocalPort => _localPort;
    public event Action? OnChange;

    public void Update(string aet, int port)
    {
        _localAet  = aet;
        _localPort = port;
        OnChange?.Invoke();
    }
}

// ══════════════════════════════════════════════════════════════════════════════
//  LogLevelService — copiado del Tester sin cambios
// ══════════════════════════════════════════════════════════════════════════════

public class LogLevelService
{
    public static readonly LoggingLevelSwitch Switch = new(LogEventLevel.Information);
    private static readonly string[] _levels = ["Verbose", "Debug", "Information", "Warning", "Error", "Fatal"];
    public string[] AvailableLevels => _levels;
    public string CurrentLevel
    {
        get => Switch.MinimumLevel.ToString();
        set => Switch.MinimumLevel = value switch
        {
            "Verbose"     => LogEventLevel.Verbose,
            "Debug"       => LogEventLevel.Debug,
            "Information" => LogEventLevel.Information,
            "Warning"     => LogEventLevel.Warning,
            "Error"       => LogEventLevel.Error,
            "Fatal"       => LogEventLevel.Fatal,
            _             => LogEventLevel.Information,
        };
    }
    public event Action? OnChange;
    public void NotifyChange() => OnChange?.Invoke();

    /// <summary>Fija el nivel inicial del switch a partir de un texto de configuración
    /// (p. ej. Serilog:MinimumLevel:Default de appsettings). Si el valor es nulo o no
    /// reconocido, se mantiene el nivel por defecto (Information). El cambio en caliente
    /// desde la UI sigue funcionando y prevalece sobre este valor inicial.</summary>
    public static void InitializeFromConfig(string? level)
    {
        if (string.IsNullOrWhiteSpace(level)) return;
        Switch.MinimumLevel = level switch
        {
            "Verbose"     => LogEventLevel.Verbose,
            "Debug"       => LogEventLevel.Debug,
            "Information" => LogEventLevel.Information,
            "Warning"     => LogEventLevel.Warning,
            "Error"       => LogEventLevel.Error,
            "Fatal"       => LogEventLevel.Fatal,
            _             => Switch.MinimumLevel,
        };
    }
}

// ══════════════════════════════════════════════════════════════════════════════
//  WindowSchedulerHostedService — wraps IWindowScheduler as a BackgroundService
// ══════════════════════════════════════════════════════════════════════════════

public class WindowSchedulerHostedService(
    IWindowScheduler scheduler,
    ILogger<WindowSchedulerHostedService> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("WindowSchedulerHostedService started");
        await scheduler.RunAsync(stoppingToken);
    }
}

// ══════════════════════════════════════════════════════════════════════════════
//  AutoResumeHostedService — reanuda migraciones/verificaciones auto-pausadas
// ══════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Watches migrations that were auto-paused due to connection errors and probes the
/// relevant node (origin for migration, destination for verification) with C-ECHO on
/// an interval. When the node is reachable again, it resumes the affected process.
/// </summary>
public class AutoResumeHostedService(
    IServiceScopeFactory scopeFactory,
    Microsoft.Extensions.Configuration.IConfiguration config,
    ILogger<AutoResumeHostedService> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var enabled       = config.GetValue("AutoResume:Enabled", true);
        var intervalSecs  = config.GetValue("AutoResume:CheckIntervalSeconds", 60);
        if (intervalSecs < 10) intervalSecs = 60;

        if (!enabled)
        {
            logger.LogInformation("AutoResumeHostedService deshabilitado por configuración.");
            return;
        }

        logger.LogInformation("AutoResumeHostedService started · cada {Secs}s", intervalSecs);
        var interval = TimeSpan.FromSeconds(intervalSecs);

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await Task.Delay(interval, stoppingToken); }
            catch (OperationCanceledException) { break; }

            try { await CheckAndResumeAsync(stoppingToken); }
            catch (Exception ex) { logger.LogWarning(ex, "AutoResume: ciclo falló (no crítico)."); }
        }
    }

    private async Task CheckAndResumeAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var migRepo = scope.ServiceProvider.GetRequiredService<IMigrationRepository>();
        var health  = scope.ServiceProvider.GetRequiredService<IConnectionHealthService>();
        var worker  = scope.ServiceProvider.GetRequiredService<IMigrationWorker>();
        var verSvc  = scope.ServiceProvider.GetRequiredService<IVerificationService>();

        var paused = await migRepo.GetAutoPausedAsync();
        if (paused.Count == 0) return;

        foreach (var m in paused)
        {
            // Migración auto-pausada → probar el ORIGEN
            if (m.MigrationAutoPaused && m.OriginNode is not null)
            {
                var nh = await health.ProbeNodeAsync(m.OriginNode, ct);
                if (nh.Reachable)
                {
                    logger.LogInformation("AutoResume: origen {Alias} accesible de nuevo. " +
                        "Reanudando migración {Id}.", m.OriginNode.Alias, m.Id);
                    await migRepo.SetMigrationAutoPausedAsync(m.Id, false);
                    await worker.StartAsync(m.Id, ct);
                }
            }

            // Verificación auto-pausada → probar el DESTINO
            if (m.VerificationAutoPaused && m.DestNode is not null)
            {
                var nh = await health.ProbeNodeAsync(m.DestNode, ct);
                if (nh.Reachable)
                {
                    logger.LogInformation("AutoResume: destino {Alias} accesible de nuevo. " +
                        "Reanudando verificación {Id}.", m.DestNode.Alias, m.Id);
                    await migRepo.SetVerificationAutoPausedAsync(m.Id, false);
                    await verSvc.StartVerificationAsync(m.Id, ct);
                }
            }
        }
    }
}

/// <summary>
/// Background service that periodically purges old INFO audit logs so the
/// fastest-growing table doesn't grow unbounded. Runs once on startup (optional)
/// and then on a fixed interval. Reads settings from the "Maintenance" section.
/// </summary>
public class DatabaseMaintenanceHostedService(
    IServiceScopeFactory scopeFactory,
    Microsoft.Extensions.Configuration.IConfiguration config,
    ILogger<DatabaseMaintenanceHostedService> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var retentionDays = config.GetValue("Maintenance:AuditLogRetentionDays", 90);
        var intervalHours = config.GetValue("Maintenance:PurgeIntervalHours", 24);
        var runOnStartup  = config.GetValue("Maintenance:RunPurgeOnStartup", true);

        if (intervalHours < 1) intervalHours = 24;
        var interval = TimeSpan.FromHours(intervalHours);

        logger.LogInformation("DatabaseMaintenanceHostedService started · retención={Days}d · intervalo={Hours}h",
            retentionDays, intervalHours);

        // Pequeña espera inicial para no competir con la inicialización de la BD.
        try { await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken); }
        catch (OperationCanceledException) { return; }

        if (runOnStartup)
            await PurgeAsync(retentionDays, stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await Task.Delay(interval, stoppingToken); }
            catch (OperationCanceledException) { break; }

            await PurgeAsync(retentionDays, stoppingToken);
        }
    }

    private async Task PurgeAsync(int retentionDays, CancellationToken ct)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var maintenance = scope.ServiceProvider
                .GetRequiredService<DicomMigrator.Infrastructure.Data.DatabaseMaintenance>();

            var deleted = await maintenance.PurgeOldAuditLogsAsync(retentionDays, ct);
            if (deleted > 0)
            {
                // Tras una purga grande, recuperar espacio del fichero.
                await maintenance.IncrementalVacuumAsync(ct);
                logger.LogInformation("Mantenimiento periódico: {Count} logs purgados y espacio recuperado.", deleted);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Mantenimiento periódico falló (no crítico, se reintentará).");
        }
    }
}

// ══════════════════════════════════════════════════════════════════════════════
//  AuditLogFlushService — vuelca el buffer de auditoría en lotes
// ══════════════════════════════════════════════════════════════════════════════
//
// La escritura de auditoría se acumula en AuditLogBuffer (en memoria) y se
// persiste aquí periódicamente en una sola transacción por lote. Esto elimina el
// commit-por-estudio que, con N workers y millones de estudios, convertía el
// único escritor de SQLite en el cuello de botella de la migración.
//
// - Flush por intervalo (cada FlushIntervalSeconds) o cuando el buffer supera su
//   tamaño de lote, lo que ocurra antes.
// - Flush final garantizado al apagar la aplicación (StopAsync), para no perder
//   las entradas que queden en cola.
public sealed class AuditLogFlushService(
    AuditLogBuffer buffer,
    ILogger<AuditLogFlushService> logger) : BackgroundService
{
    private const int FlushIntervalSeconds = 5;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("AuditLogFlushService started · cada {N}s · lote {B}",
            FlushIntervalSeconds, AuditLogBuffer.BatchSize);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Vuelca ya si el buffer ha crecido; si no, espera al intervalo.
                if (buffer.PendingCount >= AuditLogBuffer.BatchSize)
                    await buffer.FlushAsync(stoppingToken);

                await Task.Delay(TimeSpan.FromSeconds(FlushIntervalSeconds), stoppingToken);
                await buffer.FlushAsync(stoppingToken);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Ciclo de flush de auditoría falló (no crítico, se reintentará).");
            }
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        // Flush final: persistir lo que quede en cola antes de cerrar.
        // IMPORTANTE: durante el apagado, 'cancellationToken' suele llegar ya cancelado
        // (o se cancela de inmediato), lo que hacía fallar este flush con
        // TaskCanceledException y perder las últimas entradas de auditoría. Por eso
        // usamos un token PROPIO con un timeout corto, independiente del de apagado,
        // para darle al flush final una ventana real de ejecución.
        using var flushCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try { await buffer.FlushAsync(flushCts.Token); }
        catch (Exception ex) { logger.LogWarning(ex, "Flush final de auditoría falló."); }
        await base.StopAsync(cancellationToken);
    }
}
