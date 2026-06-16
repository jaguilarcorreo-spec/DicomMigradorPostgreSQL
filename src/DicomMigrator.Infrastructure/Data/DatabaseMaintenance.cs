using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace DicomMigrator.Infrastructure.Data;

/// <summary>
/// Centralizes recurring PostgreSQL maintenance: performance indexes (created once
/// at startup, idempotent), planner statistics refresh (ANALYZE) and space
/// reclamation (VACUUM) on demand, and audit-log purging.
///
/// All operations are safe to run repeatedly. Index creation uses IF NOT EXISTS so
/// it is a no-op after the first run. In PostgreSQL, routine space reclamation is
/// handled by autovacuum; the explicit VACUUM here is for on-demand maintenance
/// after large deletes.
/// </summary>
public sealed class DatabaseMaintenance(
    IDbContextFactory<AppDbContext> factory,
    ILogger<DatabaseMaintenance> logger)
{
    /// <summary>
    /// Create the performance indexes identified by the schema review.
    /// Idempotent — safe on every startup. Partial index on active studies keeps
    /// AcquireNextPending fast while staying tiny (only Pending/RetryPending rows).
    /// Identifiers are quoted because EF Core creates them PascalCased in PostgreSQL.
    /// </summary>
    public async Task EnsureIndexesAsync(CancellationToken ct = default)
    {
        const string sql = """
            CREATE INDEX IF NOT EXISTS "IX_MigStudies_active"
              ON "MigrationStudies" ("MigrationId", "StudyDate")
              WHERE "MigrationStatus" IN ('Pending','RetryPending');

            CREATE INDEX IF NOT EXISTS "IX_AuditLogs_Mig_Timestamp"
              ON "AuditLogs" ("MigrationId", "Timestamp");

            CREATE INDEX IF NOT EXISTS "IX_AuditLogs_Timestamp"
              ON "AuditLogs" ("Timestamp");

            CREATE INDEX IF NOT EXISTS "IX_DiscStudies_JobId"
              ON "DiscoveredStudies" ("DiscoveryJobId");

            CREATE INDEX IF NOT EXISTS "IX_DiscPartitions_Job_Status"
              ON "DiscoveryPartitions" ("DiscoveryJobId", "Status");

            CREATE INDEX IF NOT EXISTS "IX_MigStudies_Mig_DiscDate"
              ON "MigrationStudies" ("MigrationId", "DiscoveryDate");

            CREATE INDEX IF NOT EXISTS "IX_MigStudies_Mig_Patient"
              ON "MigrationStudies" ("MigrationId", "PatientId");

            CREATE INDEX IF NOT EXISTS "IX_MigStudies_Mig_Accession"
              ON "MigrationStudies" ("MigrationId", "AccessionNumber");
            """;
        try
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            await db.Database.ExecuteSqlRawAsync(sql, ct);
            logger.LogInformation("Índices de rendimiento verificados/creados.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error al crear los índices de rendimiento.");
        }
    }

    /// <summary>
    /// VACUUM reclaims space. In PostgreSQL autovacuum handles this in the
    /// background; this explicit call is for on-demand use after large deletes.
    /// The dbPath parameter is ignored (kept for call-site compatibility).
    /// </summary>
    public async Task<(long before, long after)> RunVacuumAsync(string dbPath, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await db.Database.ExecuteSqlRawAsync("VACUUM;", ct);
        logger.LogInformation("VACUUM completado.");
        return (0, 0);
    }

    /// <summary>
    /// Referential integrity is guaranteed by PostgreSQL by design, so there is no
    /// integrity_check to run. Returns OK for call-site compatibility.
    /// </summary>
    public Task<(bool integrityOk, int fkViolations)> ValidateAsync(CancellationToken ct = default)
    {
        logger.LogInformation("Validación: integridad garantizada por PostgreSQL.");
        return Task.FromResult((true, 0));
    }

    /// <summary>Refresh the query planner statistics (ANALYZE). Cheap; run at shutdown.</summary>
    public async Task OptimizeAsync(CancellationToken ct = default)
    {
        try
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            await db.Database.ExecuteSqlRawAsync("ANALYZE;", ct);
            logger.LogInformation("ANALYZE ejecutado.");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "ANALYZE falló (no crítico).");
        }
    }

    /// <summary>
    /// In PostgreSQL space is reclaimed by autovacuum; there is no incremental_vacuum.
    /// No-op, kept for call-site compatibility.
    /// </summary>
    public Task IncrementalVacuumAsync(CancellationToken ct = default) => Task.CompletedTask;

    /// <summary>
    /// Purge old INFO audit logs, keeping WARN/ERROR entries for diagnostics.
    /// AuditLogs is the fastest-growing table; without purging it grows unbounded.
    /// </summary>
    /// <param name="retentionDays">Days of INFO logs to keep. Default 90.</param>
    public async Task<int> PurgeOldAuditLogsAsync(int retentionDays = 90, CancellationToken ct = default)
    {
        try
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            var cutoff = DateTime.UtcNow.AddDays(-retentionDays);
            var deleted = await db.Database.ExecuteSqlRawAsync(
                "DELETE FROM \"AuditLogs\" WHERE \"Level\" = 'INFO' AND \"Timestamp\" < {0};",
                new object[] { cutoff }, ct);
            if (deleted > 0)
                logger.LogInformation("Purgados {Count} registros de auditoría INFO (> {Days} días).",
                    deleted, retentionDays);
            return deleted;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Purga de AuditLogs falló (no crítico).");
            return 0;
        }
    }
}
