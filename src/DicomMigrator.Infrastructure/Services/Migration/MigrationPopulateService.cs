// ══════════════════════════════════════════════════════════════════════════════
//  MigrationPopulateService.cs  (v225)
//  Puebla una migración desde el inventario de un Discovery Job EN SEGUNDO PLANO.
//  Antes esto se hacía de forma síncrona dentro de la petición (ImportFromInventory),
//  lo que bloqueaba la UI y, con inventarios grandes, agotaba el timeout del comando.
//  Ahora corre en un Task de fondo, por lotes, y persiste su avance en las columnas
//  Populate* de la migración; la pantalla de migraciones las lee en cada refresco.
//  Idempotente: si el servicio se reinicia a mitad, el poblado huérfano (Running) se
//  reanuda al arrancar (ver Program.cs) y salta lo ya insertado.
//  Espeja la estructura de InstanceCaptureService.
// ══════════════════════════════════════════════════════════════════════════════
using System.Collections.Concurrent;
using DicomMigrator.Core.Interfaces;
using DicomMigrator.Core.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DicomMigrator.Infrastructure.Services.Migration;

public class MigrationPopulateService(
    IServiceScopeFactory scopeFactory,
    ILogger<MigrationPopulateService> logger) : IMigrationPopulateService
{
    // Poblados en marcha en esta instancia. Estático: el servicio es Scoped pero el
    // proceso debe sobrevivir entre peticiones.
    private static readonly ConcurrentDictionary<int, CancellationTokenSource> _cts = new();

    public bool IsRunning(int migrationId) =>
        _cts.TryGetValue(migrationId, out var cts) && !cts.IsCancellationRequested;

    public Task StartAsync(int migrationId, int sourceJobId, CancellationToken ct = default)
    {
        if (IsRunning(migrationId))
        {
            logger.LogWarning("El poblado de la migración {Id} ya está en marcha", migrationId);
            return Task.CompletedTask;
        }
        // Limpiar un CTS previo ya cancelado, si lo hubiera.
        if (_cts.TryRemove(migrationId, out var stale)) stale.Dispose();

        var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _cts[migrationId] = linked;
        var token = linked.Token;

        // Fire-and-forget: el trabajo corre en el pool; el estado se persiste en BD.
        _ = Task.Run(() => RunAsync(migrationId, sourceJobId, linked, token), token);
        return Task.CompletedTask;
    }

    private async Task RunAsync(int migrationId, int sourceJobId,
        CancellationTokenSource linked, CancellationToken token)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var migRepo   = scope.ServiceProvider.GetRequiredService<IMigrationRepository>();
            var studyRepo = scope.ServiceProvider.GetRequiredService<IStudyRepository>();

            var filter = new DiscoveredStudyFilter { DiscoveryJobId = sourceJobId };

            // Total inicial = estudios pendientes de importar. El propio import lo vuelve
            // a reportar en su primer onProgress(0,total), pero fijarlo aquí deja la barra
            // con el total correcto desde el primer instante.
            await migRepo.SetPopulateRunningAsync(migrationId, total: 0, sourceJobId: sourceJobId);
            logger.LogInformation("Poblado iniciado · migración {Id} · job origen {Job}", migrationId, sourceJobId);

            // Throttle de escrituras de progreso: como mucho una cada ~750 ms, para no
            // castigar la BD si los lotes van muy rápidos. Siempre se escribe el total al
            // principio y el done final.
            var lastWrite = DateTime.MinValue;
            var totalSeen = 0;

            var imported = await studyRepo.ImportFromInventoryAsync(migrationId, filter,
                onProgress: async (done, total) =>
                {
                    if (total != totalSeen)
                    {
                        totalSeen = total;
                        await migRepo.SetPopulateRunningAsync(migrationId, total, sourceJobId);
                    }
                    var now = DateTime.UtcNow;
                    if (done == total || (now - lastWrite).TotalMilliseconds >= 750)
                    {
                        lastWrite = now;
                        await migRepo.UpdatePopulateProgressAsync(migrationId, done);
                    }
                },
                ct: token);

            await migRepo.FinishPopulateAsync(migrationId, "Completed");
            logger.LogInformation("Poblado completado · migración {Id} · {N} estudios", migrationId, imported);

            // ── Notificación por correo (v227) ──
            try
            {
                var mig = await migRepo.GetByIdAsync(migrationId);
                await scope.ServiceProvider.GetRequiredService<INotificationService>()
                    .RaiseAsync(NotificationEvents.PopulateFinished, mig?.Name ?? $"#{migrationId}", new (string, string)[]
                    {
                        ("Origen → Destino", $"{mig?.OriginNode?.Alias ?? "?"} → {mig?.DestNode?.Alias ?? "?"}"),
                        ("Inventario",       $"job de descubrimiento #{sourceJobId}"),
                    }, migrationId, "migration",
                    kpis: new (string, string)[]
                    {
                        ("Estudios importados", imported.ToString("N0")),
                    });
            }
            catch (Exception nex) { logger.LogWarning(nex, "Notificación de fin de poblado (migración {Id}) falló (no crítico).", migrationId); }
        }
        catch (OperationCanceledException)
        {
            // Cancelado (apagado del servicio). Se queda en Running y se reanuda al
            // arrancar de nuevo; el import es idempotente.
            logger.LogInformation("Poblado cancelado · migración {Id} (se reanudará al reiniciar)", migrationId);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Poblado falló · migración {Id}", migrationId);
            try
            {
                using var scope = scopeFactory.CreateScope();
                var migRepo = scope.ServiceProvider.GetRequiredService<IMigrationRepository>();
                await migRepo.FinishPopulateAsync(migrationId, "Failed", ex.Message);
            }
            catch (Exception ex2)
            {
                logger.LogError(ex2, "No se pudo marcar el poblado como Failed · migración {Id}", migrationId);
            }
        }
        finally
        {
            if (_cts.TryGetValue(migrationId, out var current) && ReferenceEquals(current, linked))
                _cts.TryRemove(migrationId, out _);
            linked.Dispose();
        }
    }
}
