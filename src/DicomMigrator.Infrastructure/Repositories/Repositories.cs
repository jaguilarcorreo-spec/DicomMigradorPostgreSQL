using DicomMigrator.Core.Interfaces;
using DicomMigrator.Core.Models;
using DicomMigrator.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using System.Text;

namespace DicomMigrator.Infrastructure.Repositories;

// ══════════════════════════════════════════════════════════════════════════════
// NODE REPOSITORY
// ══════════════════════════════════════════════════════════════════════════════

public class NodeRepository(IDbContextFactory<AppDbContext> factory) : INodeRepository
{
    public async Task<List<DicomNode>> GetAllAsync()
    {
        await using var db = factory.CreateDbContext();
        return await db.DicomNodes.OrderBy(n => n.Alias).ToListAsync();
    }

    public async Task<DicomNode?> GetByIdAsync(int id)
    {
        await using var db = factory.CreateDbContext();
        return await db.DicomNodes.FindAsync(id);
    }

    public async Task<DicomNode> CreateAsync(DicomNode node)
    {
        await using var db = factory.CreateDbContext();
        node.CreatedAt = node.UpdatedAt = DateTime.UtcNow;
        db.DicomNodes.Add(node);
        await db.SaveChangesAsync();
        return node;
    }

    public async Task<DicomNode> UpdateAsync(DicomNode node)
    {
        await using var db = factory.CreateDbContext();
        var existing = await db.DicomNodes.FindAsync(node.Id)
            ?? throw new InvalidOperationException($"Nodo {node.Id} no encontrado");
        node.UpdatedAt = DateTime.UtcNow;
        db.Entry(existing).CurrentValues.SetValues(node);
        await db.SaveChangesAsync();
        return existing;
    }

    public async Task DeleteAsync(int id)
    {
        await using var db = factory.CreateDbContext();
        var node = await db.DicomNodes.FindAsync(id);
        if (node is not null) { db.DicomNodes.Remove(node); await db.SaveChangesAsync(); }
    }
}

// ══════════════════════════════════════════════════════════════════════════════
// MIGRATION REPOSITORY
// ══════════════════════════════════════════════════════════════════════════════

public class MigrationRepository(IDbContextFactory<AppDbContext> factory, DeferredVacuum vacuum) : IMigrationRepository
{
    public async Task<List<Migration>> GetAllAsync()
    {
        await using var db = factory.CreateDbContext();
        return await db.Migrations
            .Include(m => m.OriginNode)
            .Include(m => m.DestNode)
            .Include(m => m.Windows)
            .OrderByDescending(m => m.CreatedAt)
            .ToListAsync();
    }

    public async Task<Migration?> GetByIdAsync(int id)
    {
        await using var db = factory.CreateDbContext();
        return await db.Migrations
            .Include(m => m.OriginNode)
            .Include(m => m.DestNode)
            .Include(m => m.Windows)
            .FirstOrDefaultAsync(m => m.Id == id);
    }

    public async Task<Migration> CreateAsync(Migration migration)
    {
        await using var db = factory.CreateDbContext();
        migration.CreatedAt = migration.UpdatedAt = DateTime.UtcNow;
        db.Migrations.Add(migration);
        await db.SaveChangesAsync();
        return migration;
    }

    public async Task<Migration> UpdateAsync(Migration migration)
    {
        await using var db = factory.CreateDbContext();
        var existing = await db.Migrations
            .Include(m => m.Windows)
            .FirstOrDefaultAsync(m => m.Id == migration.Id)
            ?? throw new InvalidOperationException($"Migración {migration.Id} no encontrada");

        migration.UpdatedAt = DateTime.UtcNow;
        db.Entry(existing).CurrentValues.SetValues(migration);

        // Los tramos horarios NO se tocan aquí: se editan por SetWindowsAsync. Así un
        // guardado de otros campos (nombre, reintentos, workers…) hecho sobre una
        // migración cargada sin Include(Windows) no puede borrar la configuración
        // horaria por accidente.
        await db.SaveChangesAsync();
        return existing;
    }

    /// <summary>Reemplaza por completo los tramos horarios de una migración. Como mucho
    /// son dos (Tramo1 / Tramo2), así que se borran todos y se reinsertan: es más
    /// simple y más seguro que casar fila por fila. Lista vacía = sin restricción horaria.</summary>
    public async Task SetWindowsAsync(int migrationId, IReadOnlyList<ExecutionWindow> windows)
    {
        await using var db = factory.CreateDbContext();

        var existing = await db.ExecutionWindows
            .Where(w => w.MigrationId == migrationId)
            .ToListAsync();

        if (existing.Count > 0)
        {
            db.ExecutionWindows.RemoveRange(existing);
            await db.SaveChangesAsync();
        }

        foreach (var w in windows)
        {
            db.ExecutionWindows.Add(new ExecutionWindow
            {
                MigrationId = migrationId,
                Kind        = w.Kind,
                EnabledDays = w.EnabledDays,
                StartTime   = w.StartTime,
                EndTime     = w.EndTime,
                AllDay      = w.AllDay,
                TimeZoneId  = w.TimeZoneId,
            });
        }

        await db.SaveChangesAsync();

        await db.Migrations
            .Where(m => m.Id == migrationId)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.UpdatedAt, DateTime.UtcNow));
    }

    public async Task<bool> UpdateStatusAsync(int id, string status)
    {
        await using var db = factory.CreateDbContext();
        var rows = await db.Migrations
            .Where(m => m.Id == id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(m => m.Status, status)
                .SetProperty(m => m.UpdatedAt, DateTime.UtcNow));
        return rows > 0;
    }

    public async Task<bool> UpdateVerificationStatusAsync(int id, string verificationStatus)
    {
        await using var db = factory.CreateDbContext();
        var rows = await db.Migrations
            .Where(m => m.Id == id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(m => m.VerificationStatus, verificationStatus)
                .SetProperty(m => m.UpdatedAt, DateTime.UtcNow));
        return rows > 0;
    }

    // ── Poblado desde inventario en segundo plano (v225) ─────────────────────
    public async Task SetPopulateRunningAsync(int id, int total, int sourceJobId)
    {
        await using var db = factory.CreateDbContext();
        await db.Migrations.Where(m => m.Id == id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(m => m.PopulateStatus, "Running")
                .SetProperty(m => m.PopulateTotal, total)
                .SetProperty(m => m.PopulateDone, 0)
                .SetProperty(m => m.PopulateError, (string?)null)
                .SetProperty(m => m.PopulateSourceJobId, sourceJobId)
                .SetProperty(m => m.UpdatedAt, DateTime.UtcNow));
    }

    public async Task UpdatePopulateProgressAsync(int id, int done)
    {
        await using var db = factory.CreateDbContext();
        await db.Migrations.Where(m => m.Id == id)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.PopulateDone, done));
    }

    public async Task FinishPopulateAsync(int id, string status, string? error = null)
    {
        await using var db = factory.CreateDbContext();
        await db.Migrations.Where(m => m.Id == id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(m => m.PopulateStatus, status)
                .SetProperty(m => m.PopulateError, error)
                .SetProperty(m => m.UpdatedAt, DateTime.UtcNow));
    }

    public async Task<List<Migration>> GetPopulatingAsync()
    {
        await using var db = factory.CreateDbContext();
        return await db.Migrations
            .Where(m => m.PopulateStatus == "Running")
            .ToListAsync();
    }

    public async Task SetMigrationAutoPausedAsync(int id, bool autoPaused)
    {
        await using var db = factory.CreateDbContext();
        await db.Migrations.Where(m => m.Id == id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(m => m.MigrationAutoPaused, autoPaused)
                .SetProperty(m => m.UpdatedAt, DateTime.UtcNow));
    }

    public async Task SetVerificationAutoPausedAsync(int id, bool autoPaused)
    {
        await using var db = factory.CreateDbContext();
        await db.Migrations.Where(m => m.Id == id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(m => m.VerificationAutoPaused, autoPaused)
                .SetProperty(m => m.UpdatedAt, DateTime.UtcNow));
    }

    public async Task<List<Migration>> GetAutoPausedAsync()
    {
        await using var db = factory.CreateDbContext();
        return await db.Migrations
            .Include(m => m.OriginNode)
            .Include(m => m.DestNode)
            .Where(m => m.MigrationAutoPaused || m.VerificationAutoPaused)
            .ToListAsync();
    }

    public async Task DeleteAsync(int id)
    {
        await using var db = factory.CreateDbContext();
        var m = await db.Migrations.FindAsync(id);
        if (m is not null) { db.Migrations.Remove(m); await db.SaveChangesAsync(); }
        // El borrado arrastra en cascada estudios, instancias y auditoría: VACUUM en
        // segundo plano para que una nueva migración reutilice las páginas de índice.
        vacuum.Request("MigrationStudies", "MigrationInstances", "AuditLogs");
    }
}

// ══════════════════════════════════════════════════════════════════════════════
// STUDY REPOSITORY
// ══════════════════════════════════════════════════════════════════════════════

// ══════════════════════════════════════════════════════════════════════════════
// REPOSITORIO DE INSTANCIAS DESCUBIERTAS  (Nivel 2 — captura en descubrimiento)
// ══════════════════════════════════════════════════════════════════════════════

public class DiscoveredInstanceRepository(IDbContextFactory<AppDbContext> factory) : IDiscoveredInstanceRepository
{
    public async Task<int> AddRangeAsync(long discoveredStudyId,
        IEnumerable<(string SeriesInstanceUid, string SopInstanceUid)> instances)
    {
        await using var db = factory.CreateDbContext();
        var rows = instances
            .Where(i => !string.IsNullOrEmpty(i.SopInstanceUid))
            .Select(i => new DiscoveredInstance
            {
                DiscoveredStudyId = discoveredStudyId,
                SeriesInstanceUid = i.SeriesInstanceUid ?? string.Empty,
                SopInstanceUid    = i.SopInstanceUid,
            })
            .ToList();

        if (rows.Count == 0) return 0;
        db.DiscoveredInstances.AddRange(rows);
        await db.SaveChangesAsync();
        return rows.Count;
    }

    public async Task<int> CountForStudyAsync(long discoveredStudyId)
    {
        await using var db = factory.CreateDbContext();
        return await db.DiscoveredInstances
            .CountAsync(i => i.DiscoveredStudyId == discoveredStudyId);
    }

    public async Task<int> CountCapturedStudiesForJobAsync(int jobId)
    {
        await using var db = factory.CreateDbContext();
        return await db.DiscoveredStudies
            .CountAsync(s => s.DiscoveryJobId == jobId && s.Instances.Any());
    }

    public async Task<int> CountInstancesForJobAsync(int jobId)
    {
        await using var db = factory.CreateDbContext();
        return await db.DiscoveredInstances
            .CountAsync(i => i.Study != null && i.Study.DiscoveryJobId == jobId);
    }

    public async Task DeleteForStudyAsync(long discoveredStudyId)
    {
        await using var db = factory.CreateDbContext();
        await db.DiscoveredInstances
            .Where(i => i.DiscoveredStudyId == discoveredStudyId)
            .ExecuteDeleteAsync();
    }
}

// ══════════════════════════════════════════════════════════════════════════════
// REPOSITORIO DE INSTANCIAS  (Nivel 2 de verificación)
// ══════════════════════════════════════════════════════════════════════════════

public class InstanceRepository(IDbContextFactory<AppDbContext> factory) : IInstanceRepository
{
    public async Task<int> AddRangeAsync(long migrationStudyId,
        IEnumerable<(string SeriesInstanceUid, string SopInstanceUid)> instances)
    {
        await using var db = factory.CreateDbContext();
        var rows = instances
            .Where(i => !string.IsNullOrEmpty(i.SopInstanceUid))
            .Select(i => new MigrationInstance
            {
                MigrationStudyId  = migrationStudyId,
                SeriesInstanceUid = i.SeriesInstanceUid ?? string.Empty,
                SopInstanceUid    = i.SopInstanceUid,
            })
            .ToList();

        if (rows.Count == 0) return 0;
        db.MigrationInstances.AddRange(rows);
        await db.SaveChangesAsync();
        return rows.Count;
    }

    public async Task<int> CountForStudyAsync(long migrationStudyId)
    {
        await using var db = factory.CreateDbContext();
        return await db.MigrationInstances
            .CountAsync(i => i.MigrationStudyId == migrationStudyId);
    }

    public async Task<bool> HasAnyForMigrationAsync(int migrationId)
    {
        // Se recorren los ESTUDIOS de la migración y, por cada uno, se mira en el índice
        // (MigrationStudyId, SopInstanceUid) si tiene alguna instancia (CONC-18). La versión
        // anterior partía de las instancias: rápida si la migración las tenía, pero si no
        // tenía ninguna recorría TODAS las del resto de migraciones antes de responder
        // "no" (674 ms con 2 M de instancias, frente a 54 ms así). Se llama en cada
        // refresco del detalle de una migración.
        await using var db = factory.CreateDbContext();
        return await db.MigrationStudies
            .AnyAsync(s => s.MigrationId == migrationId && s.Instances.Any());
    }

    public async Task<HashSet<string>> GetSopUidsForStudyAsync(long migrationStudyId)
    {
        await using var db = factory.CreateDbContext();
        var uids = await db.MigrationInstances
            .Where(i => i.MigrationStudyId == migrationStudyId)
            .Select(i => i.SopInstanceUid)
            .ToListAsync();
        return new HashSet<string>(uids);
    }

    public async Task DeleteForStudyAsync(long migrationStudyId)
    {
        await using var db = factory.CreateDbContext();
        await db.MigrationInstances
            .Where(i => i.MigrationStudyId == migrationStudyId)
            .ExecuteDeleteAsync();
    }
}

public class StudyRepository(IDbContextFactory<AppDbContext> factory, DeferredVacuum vacuum) : IStudyRepository
{
    /// <summary>Cada cuánto renueva el worker el LockDate de un estudio 'Migrating'
    /// mientras dura su C-MOVE (un estudio grande puede tardar muchos minutos).</summary>
    public static readonly TimeSpan MigratingHeartbeat = TimeSpan.FromMinutes(1);

    /// <summary>Un 'Migrating' cuyo LockDate no se ha renovado en este tiempo se considera
    /// de un worker muerto. Holgado (15 latidos perdidos) para no rescatar un C-MOVE vivo
    /// por un fallo puntual de BD al renovar.</summary>
    public static readonly TimeSpan MigratingStaleAfter = TimeSpan.FromMinutes(15);

    public async Task<List<MigrationStudy>> GetPagedAsync(int migrationId, StudyFilter filter)
    {
        await using var db = factory.CreateDbContext();
        return await ApplyFilter(db.MigrationStudies, migrationId, filter)
            .OrderByDescending(s => s.DiscoveryDate)
            .Skip((filter.PageNumber - 1) * filter.PageSize)
            .Take(filter.PageSize)
            .ToListAsync();
    }

    public async Task<List<MigrationStudy>> GetPageKeysetAsync(int migrationId, StudyFilter filter)
    {
        await using var db = factory.CreateDbContext();
        var q = ApplyFilter(db.MigrationStudies, migrationId, filter);

        // Apply the cursor: rows strictly "after" (DiscoveryDate, Id) in DESC order.
        // The compound comparison keeps the cursor stable when DiscoveryDate repeats.
        if (filter.CursorDiscoveryDate is not null && filter.CursorId is not null)
        {
            var cd = filter.CursorDiscoveryDate.Value;
            var cid = filter.CursorId.Value;
            q = q.Where(s =>
                s.DiscoveryDate < cd ||
                (s.DiscoveryDate == cd && s.Id < cid));
        }

        return await q
            .OrderByDescending(s => s.DiscoveryDate)
            .ThenByDescending(s => s.Id)
            .Take(filter.PageSize)
            .ToListAsync();
    }

    public async Task<int> CountAsync(int migrationId, StudyFilter filter)
    {
        await using var db = factory.CreateDbContext();
        return await ApplyFilter(db.MigrationStudies, migrationId, filter).CountAsync();
    }

    public async Task<MigrationStudy?> GetByIdAsync(long id)
    {
        await using var db = factory.CreateDbContext();
        return await db.MigrationStudies.FindAsync(id);
    }

    public async Task<MigrationStudy?> GetByUidAsync(int migrationId, string studyInstanceUid)
    {
        await using var db = factory.CreateDbContext();
        return await db.MigrationStudies
            .Where(s => s.MigrationId == migrationId && s.StudyInstanceUid == studyInstanceUid)
            .OrderBy(s => s.Id)
            .FirstOrDefaultAsync();
    }

    public async Task<int> BulkInsertAsync(int migrationId, IEnumerable<MigrationStudy> studies)
    {
        await using var db = factory.CreateDbContext();
        // Get existing UIDs to avoid duplicates
        var existingUids = await db.MigrationStudies
            .Where(s => s.MigrationId == migrationId)
            .Select(s => s.StudyInstanceUid)
            .ToHashSetAsync();

        var toInsert = studies
            .Where(s => !existingUids.Contains(s.StudyInstanceUid))
            .Select(s => { s.MigrationId = migrationId; s.MigrationStatus = "Pending"; s.DiscoveryDate = DateTime.UtcNow; return s; })
            .ToList();

        if (toInsert.Count == 0) return 0;

        db.MigrationStudies.AddRange(toInsert);
        try
        {
            await db.SaveChangesAsync();
            return toInsert.Count;
        }
        catch (DbUpdateException)
        {
            // Otro proceso insertó alguno de estos mismos UIDs entre la comprobación de
            // existentes (arriba) y este guardado — p. ej. un poblado manual solapado con
            // la reanudación automática tras un corte sobre la misma migración. En vez de
            // abortar el lote completo por la violación del índice único (MigrationId,
            // StudyInstanceUid), reintentamos quitando del lote los que ya existen ahora:
            // el método es idempotente por contrato.
            db.ChangeTracker.Clear();
            var stillMissing = await db.MigrationStudies
                .Where(s => s.MigrationId == migrationId)
                .Select(s => s.StudyInstanceUid)
                .ToHashSetAsync();
            var retry = toInsert.Where(s => !stillMissing.Contains(s.StudyInstanceUid)).ToList();
            if (retry.Count == 0) return 0;

            db.MigrationStudies.AddRange(retry);
            await db.SaveChangesAsync();
            return retry.Count;
        }
    }

    // Tamaño de lote del poblado. Cada lote inserta sus estudios y copia sus UIDs, y
    // reporta avance. Suficientemente grande para no encarecer con ida-y-vuelta por
    // estudio, y suficientemente pequeño para que la barra de progreso se mueva.
    private const int InventoryBatchSize = 500;

    /// <summary>Puebla una migración desde el inventario: inserta los MigrationStudy y
    /// copia sus UIDs Nivel 2 (Opción A). Trabaja POR LOTES, reportando avance por
    /// <paramref name="onProgress"/> (estudios procesados, total) y respetando la
    /// cancelación. Idempotente: salta estudios y UIDs ya presentes, así que se puede
    /// reanudar tras un corte. Devuelve el nº de estudios insertados.</summary>
    public async Task<int> ImportFromInventoryAsync(int migrationId, DiscoveredStudyFilter filter,
        Func<int, int, Task>? onProgress = null, CancellationToken ct = default)
    {
        await using var db = factory.CreateDbContext();

        // El copiado puede mover MILLONES de filas; el timeout por defecto (30 s) cortaba
        // el INSERT masivo. Lo subimos SOLO para este contexto. Aun así ahora va por
        // lotes, con lo que cada comando individual es mucho más corto.
        db.Database.SetCommandTimeout(TimeSpan.FromMinutes(10));

        // Build the inventory query from the filter
        var q = db.DiscoveredStudies.AsQueryable();
        if (filter.SourcePacsId.HasValue)   q = q.Where(s => s.SourcePacsId == filter.SourcePacsId);
        if (filter.DiscoveryJobId.HasValue) q = q.Where(s => s.DiscoveryJobId == filter.DiscoveryJobId);
        if (!string.IsNullOrWhiteSpace(filter.PatientId))       q = q.Where(s => s.PatientId!.Contains(filter.PatientId));
        if (!string.IsNullOrWhiteSpace(filter.AccessionNumber)) q = q.Where(s => s.AccessionNumber!.Contains(filter.AccessionNumber));
        if (!string.IsNullOrWhiteSpace(filter.StudyDateFrom))   q = q.Where(s => string.Compare(s.StudyDate, filter.StudyDateFrom) >= 0);
        if (!string.IsNullOrWhiteSpace(filter.StudyDateTo))     q = q.Where(s => string.Compare(s.StudyDate, filter.StudyDateTo) <= 0);
        if (!string.IsNullOrWhiteSpace(filter.Modality))        q = q.Where(s => s.ModalitiesInStudy!.Contains(filter.Modality));

        var inventory = await q.ToListAsync(ct);

        var existingUids = await db.MigrationStudies
            .Where(s => s.MigrationId == migrationId)
            .Select(s => s.StudyInstanceUid)
            .ToHashSetAsync(ct);

        // DistinctBy por StudyInstanceUID: con el inventario POR JOB, un mismo estudio puede
        // aparecer en varios jobs. Si un filtro llegara a abarcar más de uno (p. ej. por PACS
        // o por fecha sin acotar el job), el inventario traería el UID repetido; nos quedamos
        // con una sola copia para no violar el índice único (MigrationId, StudyInstanceUid).
        var toInsert = inventory
            .Where(d => !existingUids.Contains(d.StudyInstanceUid))
            .DistinctBy(d => d.StudyInstanceUid)
            .ToList();

        var total = toInsert.Count;
        if (onProgress is not null) await onProgress(0, total);
        if (total == 0) return 0;

        var done = 0;
        for (var offset = 0; offset < total; offset += InventoryBatchSize)
        {
            ct.ThrowIfCancellationRequested();

            var slice = toInsert.Skip(offset).Take(InventoryBatchSize).ToList();

            // 1) Insertar los estudios del lote. Contexto limpio por lote para no
            //    acumular entidades rastreadas con inventarios enormes.
            var newStudies = slice.Select(d => new MigrationStudy
            {
                MigrationId         = migrationId,
                StudyInstanceUid    = d.StudyInstanceUid,
                PatientId           = d.PatientId,
                AccessionNumber     = d.AccessionNumber,
                StudyDate           = d.StudyDate,
                ModalitiesInStudy   = d.ModalitiesInStudy,
                SourceSeriesCount   = d.NumberOfStudyRelatedSeries,
                SourceInstanceCount = d.NumberOfStudyRelatedInstances,
                MigrationStatus     = "Pending",
                DiscoveryDate       = DateTime.UtcNow,
            }).ToList();
            db.MigrationStudies.AddRange(newStudies);
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException)
            {
                // Otro proceso insertó alguno de estos mismos UIDs entre la comprobación de
                // existentes (antes del bucle) y este guardado — p. ej. un poblado manual
                // solapado con la reanudación automática tras un corte sobre la misma
                // migración. En vez de abortar el poblado completo por la violación del
                // índice único (MigrationId, StudyInstanceUid), reintentamos quitando del
                // lote los que ya existen ahora: el método es idempotente por contrato.
                db.ChangeTracker.Clear();
                var stillMissing = await db.MigrationStudies
                    .Where(s => s.MigrationId == migrationId)
                    .Select(s => s.StudyInstanceUid)
                    .ToHashSetAsync(ct);
                var retry = newStudies.Where(s => !stillMissing.Contains(s.StudyInstanceUid)).ToList();
                if (retry.Count > 0)
                {
                    db.MigrationStudies.AddRange(retry);
                    await db.SaveChangesAsync(ct);
                }
            }
            db.ChangeTracker.Clear();

            // 2) Copiar los UIDs Nivel 2 SOLO de los estudios de este lote. Se atan por la
            //    FILA de DiscoveredStudy exacta (DiscoveredStudyId), no por UID: con el
            //    inventario POR JOB el mismo UID puede existir en otros jobs, y unir por UID
            //    arrastraría instancias ajenas. Set-based; NOT EXISTS evita colisión con el
            //    índice único al reanudar.
            var batchStudyIds = slice.Select(d => d.Id).ToArray();
            await db.Database.ExecuteSqlInterpolatedAsync($@"
INSERT INTO ""MigrationInstances"" (""MigrationStudyId"", ""SeriesInstanceUid"", ""SopInstanceUid"")
SELECT ms.""Id"", di.""SeriesInstanceUid"", di.""SopInstanceUid""
FROM ""DiscoveredInstances"" di
JOIN ""DiscoveredStudies"" ds ON ds.""Id"" = di.""DiscoveredStudyId""
JOIN ""MigrationStudies"" ms ON ms.""StudyInstanceUid"" = ds.""StudyInstanceUid"" AND ms.""MigrationId"" = {migrationId}
WHERE di.""DiscoveredStudyId"" = ANY({batchStudyIds})
  AND NOT EXISTS (SELECT 1 FROM ""MigrationInstances"" mi
                  WHERE mi.""MigrationStudyId"" = ms.""Id"" AND mi.""SopInstanceUid"" = di.""SopInstanceUid"")", ct);

            done += slice.Count;
            if (onProgress is not null) await onProgress(done, total);
        }

        return total;
    }

    public async Task<MigrationStudy?> AcquireNextPendingAsync(int migrationId, string workerId,
        IEnumerable<string> modalityPriority, int retryDelaySeconds = 60,
        DateOnly? startFromDate = null, bool oldestFirst = false)
    {
        await using var db = factory.CreateDbContext();

        // La prioridad de modalidades ya no se aplica aquí: está precalculada en
        // ModalityRank (RecomputeModalityRankAsync, al iniciar la migración).
        _ = modalityPriority;

        // Release stale locks (> 10 min without update)
        var staleCutoff = DateTime.UtcNow.AddMinutes(-10);
        await db.MigrationStudies
            .Where(s => s.MigrationId == migrationId
                     && s.MigrationStatus == "Queued"
                     && s.LockDate < staleCutoff)
            .ExecuteUpdateAsync(u => u
                .SetProperty(s => s.MigrationStatus, "Pending")
                .SetProperty(s => s.LockedByWorker, (string?)null)
                .SetProperty(s => s.LockDate, (DateTime?)null));

        // Liberar 'Migrating' caducados: el worker renueva LockDate cada
        // MigratingHeartbeat mientras dura el C-MOVE, así que un lock sin renovar
        // durante MigratingStaleAfter es de un worker muerto (caída abrupta, o fallo
        // de BD al liberar). LockDate NULL = 'Migrating' de versiones anteriores, que
        // no conservaban el lock: también es huérfano. Vuelve a 'Pending' SIN consumir
        // reintento (no es culpa del estudio).
        var migratingCutoff = DateTime.UtcNow - MigratingStaleAfter;
        await db.MigrationStudies
            .Where(s => s.MigrationId == migrationId
                     && s.MigrationStatus == "Migrating"
                     && (s.LockDate == null || s.LockDate < migratingCutoff))
            .ExecuteUpdateAsync(u => u
                .SetProperty(s => s.MigrationStatus, "Pending")
                .SetProperty(s => s.LockedByWorker, (string?)null)
                .SetProperty(s => s.LockDate, (DateTime?)null)
                .SetProperty(s => s.MigrationStartDate, (DateTime?)null)
                .SetProperty(s => s.LastUpdateDate, DateTime.UtcNow));

        // RetryPending must wait at least RetryDelaySeconds since last attempt
        var retryCutoff = DateTime.UtcNow.AddSeconds(-retryDelaySeconds);

        // StudyDate se guarda como string en formato DICOM DA (YYYYMMDD, 8 caracteres
        // fijos), así que la comparación de cadenas da el orden cronológico.
        var startDateStr = startFromDate?.ToString("yyyyMMdd");

        // ── Adquisición atómica (CONC-9) ──────────────────────────────────────
        // Antes: leer 50 candidatos, elegir en memoria y bloquear en otro paso. Todos
        // los workers elegían el MISMO estudio; uno ganaba y los demás recibían "nada"
        // y dormían 5-30 s como si la cola estuviera vacía. Y la prioridad por
        // modalidad solo se aplicaba dentro de esos 50 (CONC-16).
        // Ahora: una sola sentencia que recorre el índice de la cola en orden y bloquea
        // el primer estudio que nadie tenga (FOR UPDATE SKIP LOCKED). Cada worker se
        // queda con uno distinto, la prioridad se aplica a todos los pendientes y el
        // coste no depende de cuántos haya (~1 ms con millones, índices
        // IX_MigStudies_queue_newest / _oldest). Orden: rango de modalidad (calculado en
        // StartAsync), reintentos, fecha (más recientes o más antiguos primero, sin fecha
        // al final) e Id. Los RetryPending solo cuando no queda ningún Pending.
        var dateOrder  = oldestFirst ? @"s.""StudyDate"" ASC NULLS LAST" : @"s.""StudyDate"" DESC NULLS LAST";
        var dateFilter = startDateStr is null ? "" : @" AND (s.""StudyDate"" IS NULL OR s.""StudyDate"" >= @start)";
        string ClaimSql(string statusFilter) => $@"
UPDATE ""MigrationStudies"" AS m
SET ""MigrationStatus"" = 'Queued', ""LockedByWorker"" = @worker, ""LockDate"" = @now, ""MigrationStartDate"" = @now
WHERE m.""Id"" = (
    SELECT s.""Id"" FROM ""MigrationStudies"" AS s
    WHERE s.""MigrationId"" = @mig AND s.""LockedByWorker"" IS NULL AND {statusFilter}{dateFilter}
    ORDER BY s.""ModalityRank"", s.""RetryCount"", {dateOrder}, s.""Id""
    LIMIT 1
    FOR UPDATE SKIP LOCKED)
RETURNING m.*";

        NpgsqlParameter[] Params() =>
        [
            new("mig", migrationId), new("worker", workerId), new("now", DateTime.UtcNow),
            new("start", (object?)startDateStr ?? DBNull.Value), new("retryCutoff", retryCutoff),
        ];

        var claimed = await db.MigrationStudies
            .FromSqlRaw(ClaimSql(@"s.""MigrationStatus"" = 'Pending'"), Params())
            .AsNoTracking().ToListAsync();
        if (claimed.Count == 0)
            claimed = await db.MigrationStudies
                .FromSqlRaw(ClaimSql(@"s.""MigrationStatus"" = 'RetryPending' AND s.""LastUpdateDate"" < @retryCutoff"), Params())
                .AsNoTracking().ToListAsync();
        return claimed.FirstOrDefault();
    }

    /// <summary>True si a la migración le queda trabajo de migración: algún estudio
    /// Pending, Migrating o RetryPending. EXISTS, sin agregar toda la migración.</summary>
    public async Task<bool> HasMigrationWorkPendingAsync(int migrationId)
    {
        await using var db = factory.CreateDbContext();
        return await db.MigrationStudies.AnyAsync(s => s.MigrationId == migrationId
            && (s.MigrationStatus == "Pending"
                || s.MigrationStatus == "Migrating"
                || s.MigrationStatus == "RetryPending"));
    }

    /// <summary>Recalcula ModalityRank de los estudios en cola según la prioridad de
    /// modalidades de la migración: posición (1..n) de la PRIMERA modalidad del estudio
    /// (ModalitiesInStudy, separada por \, / o ,) en la lista; 999 si no está. Solo
    /// toca las filas cuyo rango cambia, así que tras la primera vez es casi gratis.</summary>
    public async Task<int> RecomputeModalityRankAsync(int migrationId, IReadOnlyList<string> modalityPriority)
    {
        await using var db = factory.CreateDbContext();
        db.Database.SetCommandTimeout(TimeSpan.FromMinutes(10));   // la 1.ª vez con millones de filas
        const string rank = @"COALESCE(array_position(@prio,
            split_part(translate(COALESCE(""ModalitiesInStudy"", ''), '/,', chr(92) || chr(92)), chr(92), 1)), 999)::smallint";
        return await db.Database.ExecuteSqlRawAsync($@"
UPDATE ""MigrationStudies"" SET ""ModalityRank"" = {rank}
WHERE ""MigrationId"" = @mig AND ""MigrationStatus"" IN ('Pending', 'RetryPending')
  AND ""ModalityRank"" <> {rank}",
            new NpgsqlParameter("mig", migrationId),
            new NpgsqlParameter("prio", modalityPriority.ToArray()));
    }

    public async Task<bool> HasVerificationWorkPendingAsync(int migrationId)
    {
        await using var db = factory.CreateDbContext();
        return await db.MigrationStudies.AnyAsync(s => s.MigrationId == migrationId
            && (s.MigrationStatus == "Migrated"
                || s.MigrationStatus == "VerificationPending"
                || s.MigrationStatus == "VerifyRetryPending"));
    }

    public async Task<MigrationStudy?> AcquireNextForVerificationAsync(int migrationId, string workerId,
        int retryDelaySeconds = 60)
    {
        await using var db = factory.CreateDbContext();

        // Release stale verification locks (> 10 min without update). También los
        // 'VerificationPending' SIN fecha de bloqueo: ningún worker vivo los tiene (la
        // adquisición fija estado y bloqueo en el mismo UPDATE), así que son huérfanos de
        // versiones anteriores de ReleaseOrphanLocksAsync. Con solo "< cutoff" (NULL nunca
        // lo cumple) se quedaban ahí para siempre y la verificación no terminaba.
        var staleCutoff = DateTime.UtcNow.AddMinutes(-10);
        await db.MigrationStudies
            .Where(s => s.MigrationId == migrationId
                     && s.MigrationStatus == "VerificationPending"
                     && (s.VerifyLockDate == null || s.VerifyLockDate < staleCutoff))
            .ExecuteUpdateAsync(u => u
                .SetProperty(s => s.MigrationStatus, "Migrated")
                .SetProperty(s => s.VerifyLockedByWorker, (string?)null)
                .SetProperty(s => s.VerifyLockDate, (DateTime?)null));

        // VerifyRetryPending must wait at least retryDelaySeconds since last attempt
        var retryCutoff = DateTime.UtcNow.AddSeconds(-retryDelaySeconds);

        // Adquisición atómica con SKIP LOCKED (CONC-9 / BD-2): cada worker de verificación
        // se queda con un estudio distinto en ~1 ms (índice IX_MigStudies_verify_queue),
        // en vez de ordenar todos los migrados y competir por el mismo. Los
        // VerifyRetryPending solo cuando no queda ningún Migrated.
        static string ClaimSql(string statusFilter) => $@"
UPDATE ""MigrationStudies"" AS m
SET ""MigrationStatus"" = 'VerificationPending', ""VerifyLockedByWorker"" = @worker,
    ""VerifyLockDate"" = @now, ""VerificationStartDate"" = @now
WHERE m.""Id"" = (
    SELECT s.""Id"" FROM ""MigrationStudies"" AS s
    WHERE s.""MigrationId"" = @mig AND s.""VerifyLockedByWorker"" IS NULL AND {statusFilter}
    ORDER BY s.""Id""
    LIMIT 1
    FOR UPDATE SKIP LOCKED)
RETURNING m.*";

        NpgsqlParameter[] Params() =>
        [
            new("mig", migrationId), new("worker", workerId), new("now", DateTime.UtcNow),
            new("retryCutoff", retryCutoff),
        ];

        var claimed = await db.MigrationStudies
            .FromSqlRaw(ClaimSql(@"s.""MigrationStatus"" = 'Migrated'"), Params())
            .AsNoTracking().ToListAsync();
        if (claimed.Count == 0)
            claimed = await db.MigrationStudies
                .FromSqlRaw(ClaimSql(@"s.""MigrationStatus"" = 'VerifyRetryPending' AND s.""LastUpdateDate"" < @retryCutoff"), Params())
                .AsNoTracking().ToListAsync();
        return claimed.FirstOrDefault();
    }

    /// <summary>Finalize a verification attempt: Verified on success, or VerifyRetryPending
    /// (if retries remain) / Failed (if exhausted) on mismatch. Clears the verify lock.</summary>
    public async Task ReleaseVerificationLockAsync(long id)
    {
        // Connection error during verification: put the study back to 'Migrated'
        // and clear the verify lock, leaving VerifyRetryCount untouched (it wasn't
        // the study's fault that the destination was unreachable).
        // Solo si sigue 'VerificationPending': si el resultado ya se registró (Verified,
        // VerifyRetryPending, VerifyFailed), liberar no debe deshacerlo. Así el worker puede
        // llamarlo sin riesgo tras un error o una cancelación, sepa o no en qué punto quedó.
        await using var db = factory.CreateDbContext();
        await db.MigrationStudies
            .Where(s => s.Id == id && s.MigrationStatus == "VerificationPending")
            .ExecuteUpdateAsync(u => u
                .SetProperty(s => s.MigrationStatus, "Migrated")
                .SetProperty(s => s.VerifyLockedByWorker, (string?)null)
                .SetProperty(s => s.VerifyLockDate, (DateTime?)null)
                .SetProperty(s => s.VerificationStartDate, (DateTime?)null)
                .SetProperty(s => s.LastUpdateDate, DateTime.UtcNow));
    }

    public async Task CompleteVerificationAsync(long id, bool success, int maxRetries,
        int? targetSeries, int? targetInstances, string? error = null,
        int missingCount = 0, int extraCount = 0, string? missingUids = null,
        string? verifiedBy = null)
    {
        await using var db = factory.CreateDbContext();
        await db.MigrationStudies
            .Where(s => s.Id == id)
            .ExecuteUpdateAsync(u => u
                .SetProperty(s => s.MigrationStatus,
                    s => success ? "Verified"
                       : s.VerifyRetryCount < maxRetries ? "VerifyRetryPending"
                       : "VerifyFailed")
                .SetProperty(s => s.VerifyRetryCount,
                    s => success ? s.VerifyRetryCount
                       : s.VerifyRetryCount + 1)
                .SetProperty(s => s.TargetSeriesCount, targetSeries)
                .SetProperty(s => s.TargetInstanceCount, targetInstances)
                .SetProperty(s => s.LastError, error)
                .SetProperty(s => s.VerifyMissingCount, missingCount)
                .SetProperty(s => s.VerifyExtraCount, extraCount)
                .SetProperty(s => s.VerifyMissingUids, missingUids)
                .SetProperty(s => s.VerifiedBy, verifiedBy)
                .SetProperty(s => s.VerifyLockedByWorker, (string?)null)
                .SetProperty(s => s.VerifyLockDate, (DateTime?)null)
                .SetProperty(s => s.VerificationDate, success ? (DateTime?)DateTime.UtcNow : null)
                .SetProperty(s => s.LastUpdateDate, DateTime.UtcNow));
    }

    public async Task UpdateStatusAsync(long id, string status, string? error = null)
    {
        await using var db = factory.CreateDbContext();
        await db.MigrationStudies
            .Where(s => s.Id == id)
            .ExecuteUpdateAsync(u => u
                .SetProperty(s => s.MigrationStatus, status)
                .SetProperty(s => s.LastError, error)
                .SetProperty(s => s.LockedByWorker, (string?)null)
                .SetProperty(s => s.LockDate, (DateTime?)null)
                .SetProperty(s => s.LastUpdateDate, DateTime.UtcNow)
                .SetProperty(s => s.RetryCount,
                    s => status == "RetryPending" ? s.RetryCount + 1 : s.RetryCount)
                // MigrationDate: set when Migrated, clear when Pending/RetryPending, preserve otherwise
                .SetProperty(s => s.MigrationDate,
                    s => status == "Migrated"                             ? (DateTime?)DateTime.UtcNow :
                         status == "Pending" || status == "RetryPending"  ? (DateTime?)null :
                         s.MigrationDate));
    }

    public async Task MarkMigratingAsync(long id, string workerId)
    {
        // A diferencia de UpdateStatusAsync, CONSERVA el lock del worker y renueva
        // LockDate: así un estudio 'Migrating' siempre tiene dueño y fecha, y si el
        // proceso muere a mitad del C-MOVE la limpieza de caducados / huérfanos lo
        // puede rescatar (sin lock, quedaría atascado en 'Migrating' para siempre).
        await using var db = factory.CreateDbContext();
        await db.MigrationStudies
            .Where(s => s.Id == id)
            .ExecuteUpdateAsync(u => u
                .SetProperty(s => s.MigrationStatus, "Migrating")
                .SetProperty(s => s.LastError, (string?)null)
                .SetProperty(s => s.LockedByWorker, workerId)
                .SetProperty(s => s.LockDate, DateTime.UtcNow)
                .SetProperty(s => s.LastUpdateDate, DateTime.UtcNow));
    }

    public async Task<bool> RenewMigrationLockAsync(long id, string workerId)
    {
        // Latido durante el C-MOVE: solo si el estudio sigue en 'Migrating' y bloqueado
        // por ESTE worker (si otro proceso lo rescató, no se lo "robamos" de vuelta).
        await using var db = factory.CreateDbContext();
        var rows = await db.MigrationStudies
            .Where(s => s.Id == id && s.MigrationStatus == "Migrating" && s.LockedByWorker == workerId)
            .ExecuteUpdateAsync(u => u
                .SetProperty(s => s.LockDate, DateTime.UtcNow));
        return rows > 0;
    }

    public async Task UpdateVerificationAsync(long id, string status, int? targetSeries, int? targetInstances)
    {
        await using var db = factory.CreateDbContext();
        await db.MigrationStudies
            .Where(s => s.Id == id)
            .ExecuteUpdateAsync(u => u
                .SetProperty(s => s.MigrationStatus, status)
                .SetProperty(s => s.TargetSeriesCount, targetSeries)
                .SetProperty(s => s.TargetInstanceCount, targetInstances)
                .SetProperty(s => s.VerificationDate, DateTime.UtcNow)
                .SetProperty(s => s.LastUpdateDate, DateTime.UtcNow));
    }

    public async Task UpdateVerificationStartAsync(long id)
    {
        // Called per-worker just before the actual QIDO/C-FIND call —
        // NOT during bulk enqueue, so the measured time is the real verification time
        await using var db = factory.CreateDbContext();
        await db.MigrationStudies
            .Where(s => s.Id == id)
            .ExecuteUpdateAsync(u => u
                .SetProperty(s => s.MigrationStatus, "VerificationPending")
                .SetProperty(s => s.VerificationStartDate, DateTime.UtcNow)
                .SetProperty(s => s.LastUpdateDate, DateTime.UtcNow));
    }

    public async Task EnqueueForVerificationAsync(long id)
    {
        // Called during bulk enqueue — sets status only, leaves VerificationStartDate null
        // so the timer starts when the worker actually picks it up, not when it's queued
        await using var db = factory.CreateDbContext();
        await db.MigrationStudies
            .Where(s => s.Id == id)
            .ExecuteUpdateAsync(u => u
                .SetProperty(s => s.MigrationStatus, "VerificationPending")
                .SetProperty(s => s.LastUpdateDate, DateTime.UtcNow));
    }

    public async Task<int> EnqueueAllMigratedForVerificationAsync(int migrationId)
    {
        await using var db = factory.CreateDbContext();
        return await db.MigrationStudies
            .Where(s => s.MigrationId == migrationId && s.MigrationStatus == "Migrated")
            .ExecuteUpdateAsync(u => u
                .SetProperty(s => s.MigrationStatus, "VerificationPending")
                .SetProperty(s => s.LastUpdateDate, DateTime.UtcNow));
    }

    public async Task<List<MigrationStudy>> GetNextVerificationBatchAsync(
        int migrationId, int limit, CancellationToken ct = default)
    {
        await using var db = factory.CreateDbContext();
        return await db.MigrationStudies
            .AsNoTracking()
            .Where(s => s.MigrationId == migrationId && s.MigrationStatus == "VerificationPending")
            .OrderBy(s => s.Id)
            .Take(limit)
            .ToListAsync(ct);
    }

    public async Task<int> ReleaseOrphanMigrationLocksAsync(int migrationId)
    {
        // Migración: estudios atrapados en 'Queued' o 'Migrating' (lock de migración)
        // → 'Pending', SIN consumir reintento: el C-MOVE se cortó por la caída del
        // proceso, no por culpa del estudio. Re-enviar un estudio ya entregado en parte
        // es inocuo (el destino deduplica por SOPInstanceUID).
        await using var db = factory.CreateDbContext();
        return await db.MigrationStudies
            .Where(s => s.MigrationId == migrationId
                     && (s.MigrationStatus == "Queued" || s.MigrationStatus == "Migrating"))
            .ExecuteUpdateAsync(u => u
                .SetProperty(s => s.MigrationStatus, "Pending")
                .SetProperty(s => s.LockedByWorker, (string?)null)
                .SetProperty(s => s.LockDate, (DateTime?)null)
                .SetProperty(s => s.MigrationStartDate, (DateTime?)null)
                .SetProperty(s => s.LastUpdateDate, DateTime.UtcNow));
    }

    public async Task ReleaseOrphanLocksAsync(int migrationId)
    {
        await ReleaseOrphanMigrationLocksAsync(migrationId);

        await using var db = factory.CreateDbContext();

        // Verificación: estudios que se quedaron a medio verificar ('VerificationPending')
        // → de vuelta a 'Migrated', sin gastar reintento, para que se verifiquen otra vez.
        // Antes solo se borraba el bloqueo y el estudio seguía en 'VerificationPending':
        // ningún worker lo volvía a tomar y la verificación no terminaba nunca.
        await db.MigrationStudies
            .Where(s => s.MigrationId == migrationId && s.MigrationStatus == "VerificationPending")
            .ExecuteUpdateAsync(u => u
                .SetProperty(s => s.MigrationStatus, "Migrated")
                .SetProperty(s => s.VerifyLockedByWorker, (string?)null)
                .SetProperty(s => s.VerifyLockDate, (DateTime?)null)
                .SetProperty(s => s.VerificationStartDate, (DateTime?)null)
                .SetProperty(s => s.LastUpdateDate, DateTime.UtcNow));

        // Cualquier otro bloqueo de verificación colgado (estado ya final): solo liberarlo.
        await db.MigrationStudies
            .Where(s => s.MigrationId == migrationId && s.VerifyLockedByWorker != null)
            .ExecuteUpdateAsync(u => u
                .SetProperty(s => s.VerifyLockedByWorker, (string?)null)
                .SetProperty(s => s.VerifyLockDate, (DateTime?)null));
    }

    public async Task ReleaseLocksAsync(int migrationId, string workerId)
    {
        // Filtrado por migración: los nombres de worker ("WORKER-1"…) se repiten entre
        // migraciones concurrentes. Sin este filtro, el worker que termina en una
        // migración devolvería a 'Pending' el estudio que su homónimo de OTRA migración
        // tiene en pleno C-MOVE ('Migrating' conserva ahora el lock).
        await using var db = factory.CreateDbContext();
        await db.MigrationStudies
            .Where(s => s.MigrationId == migrationId
                     && s.LockedByWorker == workerId
                     && (s.MigrationStatus == "Queued" || s.MigrationStatus == "Migrating"))
            .ExecuteUpdateAsync(u => u
                .SetProperty(s => s.MigrationStatus, "Pending")
                .SetProperty(s => s.LockedByWorker, (string?)null)
                .SetProperty(s => s.LockDate, (DateTime?)null)
                .SetProperty(s => s.MigrationStartDate, (DateTime?)null)
                .SetProperty(s => s.LastUpdateDate, DateTime.UtcNow));
    }

    public async Task ReleaseMigrationLockAsync(long id)
    {
        // Source connection error during migration: return the study to 'Pending'
        // and clear the migration lock, WITHOUT consuming a retry (it wasn't the
        // study's fault that the source PACS was unreachable).
        await using var db = factory.CreateDbContext();
        await db.MigrationStudies
            .Where(s => s.Id == id)
            .ExecuteUpdateAsync(u => u
                .SetProperty(s => s.MigrationStatus, "Pending")
                .SetProperty(s => s.LockedByWorker, (string?)null)
                .SetProperty(s => s.LockDate, (DateTime?)null)
                .SetProperty(s => s.MigrationStartDate, (DateTime?)null)
                .SetProperty(s => s.LastUpdateDate, DateTime.UtcNow));
    }

    public async Task DeleteAllAsync(int migrationId)
    {
        await using var db = factory.CreateDbContext();
        await db.MigrationStudies
            .Where(s => s.MigrationId == migrationId)
            .ExecuteDeleteAsync();
        vacuum.Request("MigrationStudies", "MigrationInstances");
    }

    public async Task RetryFailedAsync(int migrationId)
    {
        await using var db = factory.CreateDbContext();
        await db.MigrationStudies
            .Where(s => s.MigrationId == migrationId && s.MigrationStatus == "Failed")
            .ExecuteUpdateAsync(u => u
                .SetProperty(s => s.MigrationStatus, "RetryPending")
                .SetProperty(s => s.LastUpdateDate, DateTime.UtcNow));
    }

    public async Task RetryVerifyFailedAsync(int migrationId)
    {
        // Verification failures (VerifyFailed) go back to 'Migrated' so the
        // verification workers pick them up again. Reset the verify retry counter
        // so they get a fresh set of attempts.
        await using var db = factory.CreateDbContext();
        await db.MigrationStudies
            .Where(s => s.MigrationId == migrationId && s.MigrationStatus == "VerifyFailed")
            .ExecuteUpdateAsync(u => u
                .SetProperty(s => s.MigrationStatus, "Migrated")
                .SetProperty(s => s.VerifyRetryCount, 0)
                .SetProperty(s => s.VerifyLockedByWorker, (string?)null)
                .SetProperty(s => s.VerifyLockDate, (DateTime?)null)
                .SetProperty(s => s.LastUpdateDate, DateTime.UtcNow));
    }

    public async Task CancelStudyAsync(long id)
    {
        await using var db = factory.CreateDbContext();
        await db.MigrationStudies
            .Where(s => s.Id == id)
            .ExecuteUpdateAsync(u => u
                .SetProperty(s => s.MigrationStatus, "Cancelled")
                .SetProperty(s => s.LockedByWorker, (string?)null)
                .SetProperty(s => s.LockDate, (DateTime?)null)
                .SetProperty(s => s.LastUpdateDate, DateTime.UtcNow));
    }

    /// <summary>Fila del agregado de GetStatsAsync (recuentos por estado + tiempos).</summary>
    public sealed class MigrationStatsRow
    {
        public long   Total                    { get; set; }
        public long   Pending                  { get; set; }
        public long   Queued                   { get; set; }
        public long   Migrating                { get; set; }
        public long   Migrated                 { get; set; }
        public long   VerificationPending      { get; set; }
        public long   Verified                 { get; set; }
        public long   Failed                   { get; set; }
        public long   RetryPending             { get; set; }
        public long   VerifyFailed             { get; set; }
        public long   VerifyRetryPending       { get; set; }
        public long   Cancelled                { get; set; }
        public double TotalMigrationSeconds    { get; set; }
        public double TotalVerificationSeconds { get; set; }
        public double WallMigrationSeconds     { get; set; }
        public double WallVerificationSeconds  { get; set; }
    }

    public async Task<MigrationStats> GetStatsAsync(int migrationId)
    {
        await using var db = factory.CreateDbContext();
        // Todo se agrega en PostgreSQL en UNA sentencia: antes los tiempos traían a memoria
        // una fila por estudio terminado (cientos de miles en una migración grande) solo
        // para sumarlos en C#. Al ser una única sentencia ve un snapshot coherente, así que
        // ya no hace falta la transacción RepeatableRead que alineaba las dos consultas.
        //  - Tiempo acumulado: suma de (fin − inicio) de cada estudio con ambas fechas.
        //  - Tiempo de reloj: del primer inicio al último fin, sobre esos mismos estudios.
        var rows = await db.Database.SqlQueryRaw<MigrationStatsRow>(@"
SELECT count(*)                                                        AS ""Total"",
       count(*) FILTER (WHERE ""MigrationStatus"" = 'Pending')             AS ""Pending"",
       count(*) FILTER (WHERE ""MigrationStatus"" = 'Queued')              AS ""Queued"",
       count(*) FILTER (WHERE ""MigrationStatus"" = 'Migrating')           AS ""Migrating"",
       count(*) FILTER (WHERE ""MigrationStatus"" = 'Migrated')            AS ""Migrated"",
       count(*) FILTER (WHERE ""MigrationStatus"" = 'VerificationPending') AS ""VerificationPending"",
       count(*) FILTER (WHERE ""MigrationStatus"" = 'Verified')            AS ""Verified"",
       count(*) FILTER (WHERE ""MigrationStatus"" = 'Failed')              AS ""Failed"",
       count(*) FILTER (WHERE ""MigrationStatus"" = 'RetryPending')        AS ""RetryPending"",
       count(*) FILTER (WHERE ""MigrationStatus"" = 'VerifyFailed')        AS ""VerifyFailed"",
       count(*) FILTER (WHERE ""MigrationStatus"" = 'VerifyRetryPending')  AS ""VerifyRetryPending"",
       count(*) FILTER (WHERE ""MigrationStatus"" = 'Cancelled')           AS ""Cancelled"",
       coalesce(sum(extract(epoch FROM ""MigrationDate"" - ""MigrationStartDate""))
                FILTER (WHERE ""MigrationDate"" IS NOT NULL AND ""MigrationStartDate"" IS NOT NULL), 0)::float8
                                                                       AS ""TotalMigrationSeconds"",
       coalesce(sum(extract(epoch FROM ""VerificationDate"" - ""VerificationStartDate""))
                FILTER (WHERE ""VerificationDate"" IS NOT NULL AND ""VerificationStartDate"" IS NOT NULL), 0)::float8
                                                                       AS ""TotalVerificationSeconds"",
       coalesce(extract(epoch FROM
                  max(""MigrationDate"")      FILTER (WHERE ""MigrationDate"" IS NOT NULL AND ""MigrationStartDate"" IS NOT NULL)
                - min(""MigrationStartDate"") FILTER (WHERE ""MigrationDate"" IS NOT NULL AND ""MigrationStartDate"" IS NOT NULL)), 0)::float8
                                                                       AS ""WallMigrationSeconds"",
       coalesce(extract(epoch FROM
                  max(""VerificationDate"")      FILTER (WHERE ""VerificationDate"" IS NOT NULL AND ""VerificationStartDate"" IS NOT NULL)
                - min(""VerificationStartDate"") FILTER (WHERE ""VerificationDate"" IS NOT NULL AND ""VerificationStartDate"" IS NOT NULL)), 0)::float8
                                                                       AS ""WallVerificationSeconds""
FROM ""MigrationStudies""
WHERE ""MigrationId"" = {0}", migrationId).ToListAsync();

        var r = rows.Single();
        return new MigrationStats
        {
            MigrationId              = migrationId,
            Total                    = r.Total,
            Pending                  = r.Pending,
            Queued                   = r.Queued,
            Migrating                = r.Migrating,
            Migrated                 = r.Migrated,
            VerificationPending      = r.VerificationPending,
            Verified                 = r.Verified,
            Failed                   = r.Failed,
            RetryPending             = r.RetryPending,
            VerifyFailed             = r.VerifyFailed,
            VerifyRetryPending       = r.VerifyRetryPending,
            Cancelled                = r.Cancelled,
            TotalMigrationSeconds    = r.TotalMigrationSeconds,
            TotalVerificationSeconds = r.TotalVerificationSeconds,
            WallMigrationSeconds     = r.WallMigrationSeconds,
            WallVerificationSeconds  = r.WallVerificationSeconds,
        };
    }

    public async Task<Dictionary<int, MigrationStats>> GetCountsByMigrationAsync()
    {
        await using var db = factory.CreateDbContext();
        // Una sola consulta para todas las fichas: el índice (MigrationId, MigrationStatus)
        // la resuelve sin leer la tabla (Index Only Scan). Sin tiempos: quien los necesite
        // (detalle, Excel) sigue usando GetStatsAsync.
        var groups = await db.MigrationStudies
            .GroupBy(s => new { s.MigrationId, s.MigrationStatus })
            .Select(g => new { g.Key.MigrationId, g.Key.MigrationStatus, Count = (long)g.Count() })
            .ToListAsync();

        var result = new Dictionary<int, MigrationStats>();
        foreach (var g in groups)
        {
            if (!result.TryGetValue(g.MigrationId, out var stats))
                result[g.MigrationId] = stats = new MigrationStats { MigrationId = g.MigrationId };
            AddStatusCount(stats, g.MigrationStatus, g.Count);
        }
        return result;
    }

    // Suma un recuento al contador de su estado y al Total. Un estado desconocido cuenta
    // solo en Total, igual que en GetStatsAsync (count(*) sin FILTER).
    private static void AddStatusCount(MigrationStats stats, string status, long count)
    {
        stats.Total += count;
        switch (status)
        {
            case "Pending":             stats.Pending             += count; break;
            case "Queued":              stats.Queued              += count; break;
            case "Migrating":           stats.Migrating           += count; break;
            case "Migrated":            stats.Migrated            += count; break;
            case "VerificationPending": stats.VerificationPending += count; break;
            case "Verified":            stats.Verified            += count; break;
            case "Failed":              stats.Failed              += count; break;
            case "RetryPending":        stats.RetryPending        += count; break;
            case "VerifyFailed":        stats.VerifyFailed        += count; break;
            case "VerifyRetryPending":  stats.VerifyRetryPending  += count; break;
            case "Cancelled":           stats.Cancelled           += count; break;
        }
    }

    // RFC 4180: envolver siempre entre comillas y duplicar las comillas internas ("").
    // Los valores de texto (PatientId, AccessionNumber, ModalitiesInStudy, LastError…)
    // vienen de PACS externos y no se puede asumir que no contengan comillas, comas o
    // saltos de línea — sin este escapado, un solo valor así desplaza todas las columnas
    // siguientes de esa fila sin que el usuario lo note.
    private static string CsvField(string? value) =>
        "\"" + (value ?? string.Empty).Replace("\"", "\"\"") + "\"";

    public async Task ExportToCsvAsync(int migrationId, StudyFilter filter, Stream output)
    {
        await using var db = factory.CreateDbContext();
        var studies = await ApplyFilter(db.MigrationStudies, migrationId, filter)
            .OrderByDescending(s => s.DiscoveryDate)
            .ToListAsync();

        await using var writer = new StreamWriter(output, Encoding.UTF8, leaveOpen: true);
        await writer.WriteLineAsync("StudyInstanceUID,PatientID,AccessionNumber,StudyDate,Modality,Status,SrcSeries,SrcInstances,TgtSeries,TgtInstances,Retries,LastError,DiscoveryDate,MigrationDate,VerificationDate");
        foreach (var s in studies)
            await writer.WriteLineAsync(string.Join(",",
                CsvField(s.StudyInstanceUid),
                CsvField(s.PatientId),
                CsvField(s.AccessionNumber),
                CsvField(s.StudyDate),
                CsvField(s.ModalitiesInStudy),
                CsvField(s.MigrationStatus),
                s.SourceSeriesCount?.ToString() ?? "",
                s.SourceInstanceCount?.ToString() ?? "",
                s.TargetSeriesCount?.ToString() ?? "",
                s.TargetInstanceCount?.ToString() ?? "",
                s.RetryCount.ToString(),
                CsvField(s.LastError),
                CsvField(s.DiscoveryDate.ToString("yyyy-MM-dd HH:mm:ss")),
                CsvField(s.MigrationDate?.ToString("yyyy-MM-dd HH:mm:ss")),
                CsvField(s.VerificationDate?.ToString("yyyy-MM-dd HH:mm:ss"))));
    }

    public async IAsyncEnumerable<MigrationStudy> StreamForExportAsync(
        int migrationId, StudyFilter filter,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        const int PageSize = 1000;
        int offset = 0;
        while (true)
        {
            await using var db = factory.CreateDbContext();
            var page = await ApplyFilter(db.MigrationStudies.AsNoTracking(), migrationId, filter)
                .OrderByDescending(s => s.DiscoveryDate)
                .Skip(offset)
                .Take(PageSize)
                .ToListAsync(ct);
            if (page.Count == 0) yield break;
            foreach (var s in page) yield return s;
            if (page.Count < PageSize) yield break;
            offset += PageSize;
        }
    }

    // ── helpers ───────────────────────────────────────────────────────────────
    private static IQueryable<MigrationStudy> ApplyFilter(
        IQueryable<MigrationStudy> q, int migrationId, StudyFilter f)
    {
        q = q.Where(s => s.MigrationId == migrationId);
        if (!string.IsNullOrWhiteSpace(f.StudyInstanceUid))
            q = q.Where(s => s.StudyInstanceUid.Contains(f.StudyInstanceUid));
        if (!string.IsNullOrWhiteSpace(f.PatientId))
            q = q.Where(s => s.PatientId != null && s.PatientId.Contains(f.PatientId));
        if (!string.IsNullOrWhiteSpace(f.AccessionNumber))
            q = q.Where(s => s.AccessionNumber != null && s.AccessionNumber.Contains(f.AccessionNumber));
        if (!string.IsNullOrWhiteSpace(f.Status))
            q = q.Where(s => s.MigrationStatus == f.Status);
        if (!string.IsNullOrWhiteSpace(f.Modality))
            q = q.Where(s => s.ModalitiesInStudy != null && s.ModalitiesInStudy.Contains(f.Modality));
        if (f.HasError == true)
            q = q.Where(s => s.LastError != null);
        if (f.MinRetries.HasValue)
            q = q.Where(s => s.RetryCount >= f.MinRetries);
        if (f.MaxRetries.HasValue)
            q = q.Where(s => s.RetryCount <= f.MaxRetries);
        return q;
    }
}

// ══════════════════════════════════════════════════════════════════════════════
// AUDIT LOG REPOSITORY
// ══════════════════════════════════════════════════════════════════════════════

public class AuditLogRepository(
    IDbContextFactory<AppDbContext> factory,
    AuditLogBuffer buffer) : IAuditLogRepository
{
    public async Task<List<MigrationAuditLog>> GetAsync(int migrationId, int limit = 200)
    {
        // Persist anything buffered first so the UI never shows stale data.
        await buffer.FlushAsync();
        await using var db = factory.CreateDbContext();
        return await db.AuditLogs
            .Where(l => l.MigrationId == migrationId)
            .OrderByDescending(l => l.Timestamp)
            .Take(limit)
            .ToListAsync();
    }

    public async Task<List<MigrationAuditLog>> GetRecentAsync(int limit = 200)
    {
        // Single SQL query: ORDER BY Timestamp DESC LIMIT N — avoids N queries
        // (one per migration) and never loads more than 'limit' rows into memory,
        // regardless of how many migrations exist. Flush buffered entries first.
        await buffer.FlushAsync();
        await using var db = factory.CreateDbContext();
        return await db.AuditLogs
            .OrderByDescending(l => l.Timestamp)
            .Take(limit)
            .ToListAsync();
    }

    public Task AddAsync(MigrationAuditLog log)
    {
        // Enqueue only — no DB commit here. The flush service persists in batches.
        // This is what removes the per-study commit bottleneck on massive migrations.
        buffer.Enqueue(log);
        return Task.CompletedTask;
    }
}

// ══════════════════════════════════════════════════════════════════════════════
// LOCAL CONFIG REPOSITORY  (idéntico al del Tester)
// ══════════════════════════════════════════════════════════════════════════════

public class LocalConfigRepository(IDbContextFactory<AppDbContext> factory) : ILocalConfigRepository
{
    public async Task<LocalConfiguration> GetAsync()
    {
        await using var db = factory.CreateDbContext();
        return await db.LocalConfigurations.OrderBy(x => x.Id).FirstOrDefaultAsync()
               ?? new LocalConfiguration { Id = 1 };
    }

    public async Task<LocalConfiguration> SaveAsync(LocalConfiguration config)
    {
        await using var db = factory.CreateDbContext();
        var existing = await db.LocalConfigurations.OrderBy(x => x.Id).FirstOrDefaultAsync();
        if (existing is null)
        {
            config.Id = 1;
            db.LocalConfigurations.Add(config);
        }
        else
        {
            existing.LocalAet      = config.LocalAet;
            existing.LocalPort     = config.LocalPort;
            existing.LocalHostname = config.LocalHostname;
            existing.Description   = config.Description;
            existing.MaxConcurrentMigrations = config.MaxConcurrentMigrations;
            existing.UpdatedAt     = DateTime.UtcNow;
        }
        await db.SaveChangesAsync();
        return existing ?? config;
    }
}
