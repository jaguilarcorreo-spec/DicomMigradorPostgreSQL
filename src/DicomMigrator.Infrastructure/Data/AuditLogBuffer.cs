using System.Collections.Concurrent;
using DicomMigrator.Core.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DicomMigrator.Infrastructure.Data;

/// <summary>
/// Singleton in-memory buffer for audit log entries. Instead of one DB commit per
/// study (which, with N workers and millions of studies, makes the single-writer
/// SQLite the bottleneck of the whole migration), entries are queued here and
/// flushed in batches inside a single transaction by <see cref="AuditLogFlushService"/>.
///
/// AddAsync (called from the workers) only enqueues — it never touches the DB.
/// Reads from the repository call FlushAsync() first so the UI never shows stale data.
/// Pause/Stop/shutdown also force a flush so nothing is lost.
///
/// NOTE: IDbContextFactory is registered Scoped in this project, so a singleton
/// cannot take it via the constructor. We inject IServiceScopeFactory and resolve
/// the factory inside a short-lived scope on each flush — the same pattern the
/// hosted services use to reach scoped services.
/// </summary>
public sealed class AuditLogBuffer(
    IServiceScopeFactory scopeFactory,
    ILogger<AuditLogBuffer> logger)
{
    /// <summary>Flush when the queue reaches this many entries (size trigger).</summary>
    public const int BatchSize = 200;

    private readonly ConcurrentQueue<MigrationAuditLog> _queue = new();
    // Guards the actual DB write so two concurrent flushes don't interleave.
    private readonly SemaphoreSlim _flushLock = new(1, 1);

    /// <summary>Approximate number of entries waiting to be persisted.</summary>
    public int PendingCount => _queue.Count;

    /// <summary>Enqueue an entry. Never blocks on the DB. Stamps the timestamp now
    /// if the caller left it unset, so batched entries keep their real order.</summary>
    public void Enqueue(MigrationAuditLog log)
    {
        if (log.Timestamp == default)
            log.Timestamp = DateTime.UtcNow;
        _queue.Enqueue(log);
    }

    /// <summary>Drain the queue and persist everything currently buffered in a single
    /// transaction. Safe to call concurrently — only one writer proceeds at a time.</summary>
    public async Task FlushAsync(CancellationToken ct = default)
    {
        // Nothing to do — avoid taking the lock or opening a context.
        if (_queue.IsEmpty) return;

        await _flushLock.WaitAsync(ct);
        try
        {
            // Drain a snapshot. Entries enqueued after this point go in the next flush.
            var batch = new List<MigrationAuditLog>(BatchSize);
            while (_queue.TryDequeue(out var entry))
                batch.Add(entry);

            if (batch.Count == 0) return;

            try
            {
                // IDbContextFactory is Scoped → resolve it inside a scope.
                using var scope = scopeFactory.CreateScope();
                var factory = scope.ServiceProvider
                    .GetRequiredService<IDbContextFactory<AppDbContext>>();
                await using var db = await factory.CreateDbContextAsync(ct);
                db.AuditLogs.AddRange(batch);
                await db.SaveChangesAsync(ct);
            }
            catch (Exception ex)
            {
                // Auditing must never break a migration. Log and drop — re-enqueueing
                // on a persistent DB error would grow memory unbounded.
                logger.LogWarning(ex,
                    "No se pudo persistir un lote de {Count} entradas de auditoría (no crítico).",
                    batch.Count);
            }
        }
        finally
        {
            _flushLock.Release();
        }
    }
}
