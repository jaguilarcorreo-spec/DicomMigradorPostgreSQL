using System.Collections.Concurrent;
using DicomMigrator.Core.Interfaces;
using DicomMigrator.Core.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DicomMigrator.Infrastructure.Services.Migration;

// ══════════════════════════════════════════════════════════════════════════════
// CONNECTION HEALTH SERVICE
// ══════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Probes origin/destination nodes of a migration with C-ECHO and caches the result
/// for a short window, so the UI can show live connectivity without flooding the PACS.
/// Scoped service (uses scoped repos/dimse); the cache is static so it survives across
/// requests, mirroring the pattern used by the verification service.
/// </summary>
public class ConnectionHealthService(
    IMigrationRepository migrationRepo,
    IDimseService dimse,
    ILogger<ConnectionHealthService> logger) : IConnectionHealthService
{
    // Cache window: how long a probe result is considered fresh.
    private static readonly TimeSpan CacheWindow = TimeSpan.FromSeconds(8);

    private static readonly ConcurrentDictionary<int, ConnectionHealth> _cache = new();
    // Per-node cache so repeated probes to the same node (e.g. many studies failing
    // while a PACS is down) reuse one C-ECHO instead of flooding it.
    private static readonly ConcurrentDictionary<string, NodeHealth> _nodeCache = new();

    public async Task<ConnectionHealth> GetHealthAsync(int migrationId, CancellationToken ct = default)
    {
        // Construir la salud a partir del caché POR NODO, que es el que mantienen
        // frescos los workers (opción A sondea el destino al fallar un C-MOVE, etc.).
        // Así el panel refleja lo último que vieron los procesos, no un valor agregado
        // antiguo. Solo se hace C-ECHO de un nodo si su entrada está caduca.
        var migration = await migrationRepo.GetByIdAsync(migrationId);
        var health = new ConnectionHealth { MigrationId = migrationId, CheckedAt = DateTime.UtcNow };

        if (migration?.OriginNode is not null)
            health.Origin = await ProbeNodeAsync(migration.OriginNode, ct);
        if (migration?.DestNode is not null)
            health.Destination = await ProbeNodeAsync(migration.DestNode, ct);

        health.CheckedAt = new[] { health.Origin?.CheckedAt, health.Destination?.CheckedAt }
            .Where(d => d.HasValue).Select(d => d!.Value).DefaultIfEmpty(DateTime.UtcNow).Max();
        _cache[migrationId] = health;
        return health;
    }

    public async Task<ConnectionHealth> ProbeNowAsync(int migrationId, CancellationToken ct = default)
    {
        var migration = await migrationRepo.GetByIdAsync(migrationId);
        var health = new ConnectionHealth { MigrationId = migrationId, CheckedAt = DateTime.UtcNow };

        // ProbeNowAsync fuerza un sondeo fresco: invalida el caché por nodo primero.
        if (migration?.OriginNode is not null)
        {
            _nodeCache.TryRemove(NodeKey(migration.OriginNode), out _);
            health.Origin = await ProbeNodeAsync(migration.OriginNode, ct);
        }
        if (migration?.DestNode is not null)
        {
            _nodeCache.TryRemove(NodeKey(migration.DestNode), out _);
            health.Destination = await ProbeNodeAsync(migration.DestNode, ct);
        }

        _cache[migrationId] = health;
        return health;
    }

    private static string NodeKey(DicomNode n) => $"{n.RemoteHost}:{n.RemotePort}:{n.RemoteAet}";

    public async Task<NodeHealth> ProbeNodeAsync(DicomNode node, CancellationToken ct = default)
    {
        // Reutilizar un sondeo reciente al mismo nodo (ventana de caché) para no
        // lanzar un C-ECHO por cada estudio cuando un PACS está caído.
        var key = NodeKey(node);
        if (_nodeCache.TryGetValue(key, out var cachedNode)
            && (DateTime.UtcNow - cachedNode.CheckedAt) < CacheWindow)
        {
            return cachedNode;
        }

        var nh = new NodeHealth { Alias = node.Alias, CheckedAt = DateTime.UtcNow };
        try
        {
            var echo = await dimse.EchoAsync(node, ct);
            nh.Reachable  = echo.Success;
            nh.DurationMs = echo.DurationMs;
            nh.Error      = echo.Success ? null : (echo.ErrorMessage ?? "C-ECHO sin respuesta");
        }
        catch (Exception ex)
        {
            nh.Reachable = false;
            nh.Error     = ex.Message;
            logger.LogDebug(ex, "C-ECHO falló para {Alias}", node.Alias);
        }
        _nodeCache[key] = nh;
        return nh;
    }
}
