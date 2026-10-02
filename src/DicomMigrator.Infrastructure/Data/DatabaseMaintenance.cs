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
    /// Los índices de rendimiento ahora se definen en el modelo EF (OnModelCreating)
    /// y se crean mediante las migraciones, aplicadas por el dueño del esquema. Este
    /// método se conserva por compatibilidad de llamadas pero ya no hace nada: crear
    /// índices por SQL requería ser dueño de las tablas (error 42501 con un usuario de
    /// app que no lo es), y duplicaría lo que las migraciones ya crean.
    /// </summary>
    public Task EnsureIndexesAsync(CancellationToken ct = default) => Task.CompletedTask;

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

    // Tablas de alto volumen que se borran y repueblan en bloque (borrar/resetear una
    // migración o un job, purga de auditoría). Son las que inflan sus índices.
    public static readonly string[] HighChurnTables =
    [
        "MigrationStudies", "MigrationInstances",
        "DiscoveredStudies", "DiscoveredInstances",
        "DiscoveryPartitions", "DiscoveryRequests",
        "AuditLogs",
    ];

    // VACUUM/REINDEX sobre tablas con millones de filas superan de sobra el timeout
    // por defecto (30 s) de los comandos.
    private static readonly TimeSpan MaintenanceCommandTimeout = TimeSpan.FromHours(2);

    /// <summary>
    /// VACUUM (ANALYZE) de las tablas indicadas, best-effort (nunca lanza). Tras un borrado
    /// masivo deja reutilizables las páginas de índice vaciadas ANTES de repoblar: sin él,
    /// las claves nuevas (Ids crecientes) van siempre a páginas nuevas y el índice crece en
    /// cada ciclo borrar/repoblar. También refresca las estadísticas, de las que depende
    /// autovacuum (se pierden tras una parada no limpia de PostgreSQL). Si el usuario de
    /// la app no es dueño de la tabla, PostgreSQL la omite con un WARNING.
    /// </summary>
    public async Task VacuumTablesAsync(IEnumerable<string> tables, CancellationToken ct = default)
    {
        foreach (var table in tables.Distinct())
        {
            try
            {
                await using var db = await factory.CreateDbContextAsync(ct);
                db.Database.SetCommandTimeout(MaintenanceCommandTimeout);
                // Nombre de tabla de una lista fija del código (no entrada de usuario).
#pragma warning disable EF1002
                await db.Database.ExecuteSqlRawAsync($"VACUUM (ANALYZE) \"{table}\";", ct);
#pragma warning restore EF1002
                logger.LogInformation("VACUUM (ANALYZE) \"{Table}\" completado.", table);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "VACUUM de \"{Table}\" falló (no crítico).", table);
            }
        }
    }

    /// <summary>Fila de la estimación de inflado de índices (ver ReindexBloatedAsync).</summary>
    public sealed class IndexBloatRow
    {
        public string Name       { get; set; } = "";
        public long   Pages      { get; set; }
        public long   IdealPages { get; set; }
    }

    /// <summary>
    /// Detecta índices B-tree inflados y los reconstruye con REINDEX INDEX CONCURRENTLY
    /// (en línea: no bloquea lecturas ni escrituras). Un B-tree no encoge nunca con VACUUM;
    /// solo REINDEX devuelve el espacio. Sin pgstattuple, el tamaño ideal se estima con
    /// pg_stats (anchura media de las columnas + cabecera de tupla, relleno 90 %), así
    /// que conviene ejecutar ANALYZE antes. Solo actúa si el índice supera minPages y es
    /// más de 'ratio' veces su tamaño estimado. Best-effort: un fallo (p. ej. el usuario
    /// no es dueño de la tabla) se registra y se sigue con el siguiente. Devuelve cuántos
    /// índices se reconstruyeron.
    /// </summary>
    public async Task<int> ReindexBloatedAsync(double ratio = 4.0, long minPages = 128,
        CancellationToken ct = default)
    {
        List<IndexBloatRow> rows;
        try
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            rows = await db.Database.SqlQueryRaw<IndexBloatRow>(@"
SELECT format('%I.%I', n.nspname, i.relname)                              AS ""Name"",
       i.relpages::bigint                                                 AS ""Pages"",
       ceil(i.reltuples * (coalesce(w.width, 8) + 16) / (8192 * 0.9))::bigint AS ""IdealPages""
FROM pg_index x
JOIN pg_class i     ON i.oid = x.indexrelid
JOIN pg_class t     ON t.oid = x.indrelid
JOIN pg_namespace n ON n.oid = t.relnamespace
JOIN pg_am am       ON am.oid = i.relam AND am.amname = 'btree'
LEFT JOIN LATERAL (
    SELECT sum(s.avg_width) AS width
    FROM pg_attribute a
    JOIN pg_stats s ON s.schemaname = n.nspname AND s.tablename = t.relname AND s.attname = a.attname
    WHERE a.attrelid = t.oid AND a.attnum = ANY (x.indkey::int2[])
) w ON true
WHERE n.nspname = 'public' AND x.indisvalid AND i.reltuples >= 0 AND i.relpages > {0}",
                minPages).ToListAsync(ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "No se pudo estimar el inflado de índices (no crítico).");
            return 0;
        }

        var reindexed = 0;
        foreach (var r in rows.Where(r => r.Pages > ratio * Math.Max(r.IdealPages, 1)))
        {
            try
            {
                await using var db = await factory.CreateDbContextAsync(ct);
                db.Database.SetCommandTimeout(MaintenanceCommandTimeout);
                // r.Name viene ya entrecomillado por format('%I.%I') de PostgreSQL.
#pragma warning disable EF1002
                await db.Database.ExecuteSqlRawAsync($"REINDEX INDEX CONCURRENTLY {r.Name};", ct);
#pragma warning restore EF1002
                reindexed++;
                logger.LogInformation("REINDEX {Index}: {Pages} páginas (≈{Ideal} estimadas).",
                    r.Name, r.Pages, r.IdealPages);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                // Un REINDEX CONCURRENTLY interrumpido deja un índice inválido "<nombre>_ccnew"
                // que hay que eliminar a mano (DROP INDEX CONCURRENTLY).
                logger.LogWarning(ex, "REINDEX de {Index} falló (no crítico).", r.Name);
            }
        }
        return reindexed;
    }

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
