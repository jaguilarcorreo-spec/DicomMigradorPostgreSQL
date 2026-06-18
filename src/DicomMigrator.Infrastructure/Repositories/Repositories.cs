using DicomMigrator.Core.Interfaces;
using DicomMigrator.Core.Models;
using DicomMigrator.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
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

public class MigrationRepository(IDbContextFactory<AppDbContext> factory) : IMigrationRepository
{
    public async Task<List<Migration>> GetAllAsync()
    {
        await using var db = factory.CreateDbContext();
        return await db.Migrations
            .Include(m => m.OriginNode)
            .Include(m => m.DestNode)
            .Include(m => m.Window)
            .OrderByDescending(m => m.CreatedAt)
            .ToListAsync();
    }

    public async Task<Migration?> GetByIdAsync(int id)
    {
        await using var db = factory.CreateDbContext();
        return await db.Migrations
            .Include(m => m.OriginNode)
            .Include(m => m.DestNode)
            .Include(m => m.Window)
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
            .Include(m => m.Window)
            .FirstOrDefaultAsync(m => m.Id == migration.Id)
            ?? throw new InvalidOperationException($"Migración {migration.Id} no encontrada");

        migration.UpdatedAt = DateTime.UtcNow;
        db.Entry(existing).CurrentValues.SetValues(migration);

        if (migration.Window is not null)
        {
            if (existing.Window is null)
            {
                // Primera vez que se asigna ventana — insertar directamente
                migration.Window.MigrationId = existing.Id;
                existing.Window = migration.Window;
            }
            else
            {
                // EF Core no permite modificar la PK de una entidad dependiente
                // con FK identificadora → borrar y volver a crear
                db.ExecutionWindows.Remove(existing.Window);
                await db.SaveChangesAsync();

                var newWindow = new ExecutionWindow
                {
                    MigrationId  = existing.Id,
                    EnabledDays  = migration.Window.EnabledDays,
                    StartTime    = migration.Window.StartTime,
                    EndTime      = migration.Window.EndTime,
                    TimeZoneId   = migration.Window.TimeZoneId,
                };
                db.ExecutionWindows.Add(newWindow);
                existing.Window = newWindow;
            }
        }

        await db.SaveChangesAsync();
        return existing;
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
    }
}

// ══════════════════════════════════════════════════════════════════════════════
// STUDY REPOSITORY
// ══════════════════════════════════════════════════════════════════════════════

public class StudyRepository(IDbContextFactory<AppDbContext> factory) : IStudyRepository
{
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
        await db.SaveChangesAsync();
        return toInsert.Count;
    }

    public async Task<int> ImportFromInventoryAsync(int migrationId, DiscoveredStudyFilter filter)
    {
        await using var db = factory.CreateDbContext();

        // Build the inventory query from the filter
        var q = db.DiscoveredStudies.AsQueryable();
        if (filter.SourcePacsId.HasValue)   q = q.Where(s => s.SourcePacsId == filter.SourcePacsId);
        if (filter.DiscoveryJobId.HasValue) q = q.Where(s => s.DiscoveryJobId == filter.DiscoveryJobId);
        if (!string.IsNullOrWhiteSpace(filter.PatientId))       q = q.Where(s => s.PatientId!.Contains(filter.PatientId));
        if (!string.IsNullOrWhiteSpace(filter.AccessionNumber)) q = q.Where(s => s.AccessionNumber!.Contains(filter.AccessionNumber));
        if (!string.IsNullOrWhiteSpace(filter.StudyDateFrom))   q = q.Where(s => string.Compare(s.StudyDate, filter.StudyDateFrom) >= 0);
        if (!string.IsNullOrWhiteSpace(filter.StudyDateTo))     q = q.Where(s => string.Compare(s.StudyDate, filter.StudyDateTo) <= 0);
        if (!string.IsNullOrWhiteSpace(filter.Modality))        q = q.Where(s => s.ModalitiesInStudy!.Contains(filter.Modality));

        var inventory = await q.ToListAsync();

        var existingUids = await db.MigrationStudies
            .Where(s => s.MigrationId == migrationId)
            .Select(s => s.StudyInstanceUid)
            .ToHashSetAsync();

        var toInsert = inventory
            .Where(d => !existingUids.Contains(d.StudyInstanceUid))
            .Select(d => new MigrationStudy
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
            })
            .ToList();

        if (toInsert.Count == 0) return 0;
        db.MigrationStudies.AddRange(toInsert);
        await db.SaveChangesAsync();
        return toInsert.Count;
    }

    public async Task<MigrationStudy?> AcquireNextPendingAsync(int migrationId, string workerId,
        IEnumerable<string> modalityPriority, int retryDelaySeconds = 60,
        DateOnly? startFromDate = null, bool oldestFirst = false)
    {
        await using var db = factory.CreateDbContext();

        var priorityList = modalityPriority.Select((m, i) => (m, i)).ToList();

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

        // RetryPending must wait at least RetryDelaySeconds since last attempt
        var retryCutoff = DateTime.UtcNow.AddSeconds(-retryDelaySeconds);

        // Convert DateOnly to string for SQLite TEXT comparison (YYYYMMDD format)
        var startDateStr = startFromDate?.ToString("yyyyMMdd");

        var query = db.MigrationStudies
            .Where(s => s.MigrationId == migrationId
                     && s.LockedByWorker == null
                     && (s.MigrationStatus == "Pending"
                         || (s.MigrationStatus == "RetryPending" && s.LastUpdateDate < retryCutoff)));

        // Apply StartFromDate filter if configured
        if (startDateStr is not null)
            query = query.Where(s => s.StudyDate == null || string.Compare(s.StudyDate, startDateStr) >= 0);

        var candidates = await query
            .OrderBy(s => s.MigrationStatus == "RetryPending" ? 1 : 0)
            .ThenBy(s => s.Id)
            .Take(50)
            .ToListAsync();

        if (candidates.Count == 0) return null;

        // Sort by priority in memory:
        // 1. RetryPending studies go last
        // 2. Modality priority (independent of date ordering)
        // 3. Date direction controlled by OldestFirst switch
        IOrderedEnumerable<MigrationStudy> ordered;
        var byModality = candidates
            .OrderBy(s => s.MigrationStatus == "RetryPending" ? 1 : 0)
            .ThenBy(s =>
            {
                var m = s.ModalitiesInStudy?.Split('\\', '/', ',').FirstOrDefault() ?? "";
                var entry = priorityList.FirstOrDefault(p => p.m == m);
                return entry == default ? 999 : entry.i;
            })
            .ThenBy(s => s.RetryCount);

        ordered = oldestFirst
            ? byModality.ThenBy(s => s.StudyDate ?? "99999999")          // ASC — oldest first
            : byModality.ThenByDescending(s => s.StudyDate ?? "00000000"); // DESC — newest first

        var selected = ordered.FirstOrDefault();

        if (selected is null) return null;

        // Atomic lock
        var locked = await db.MigrationStudies
            .Where(s => s.Id == selected.Id && s.LockedByWorker == null
                     && (s.MigrationStatus == "Pending" || s.MigrationStatus == "RetryPending"))
            .ExecuteUpdateAsync(u => u
                .SetProperty(s => s.MigrationStatus, "Queued")
                .SetProperty(s => s.LockedByWorker, workerId)
                .SetProperty(s => s.LockDate, DateTime.UtcNow)
                .SetProperty(s => s.MigrationStartDate, DateTime.UtcNow));

        return locked > 0 ? selected : null;
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

        // Release stale verification locks (> 10 min without update)
        var staleCutoff = DateTime.UtcNow.AddMinutes(-10);
        await db.MigrationStudies
            .Where(s => s.MigrationId == migrationId
                     && s.MigrationStatus == "VerificationPending"
                     && s.VerifyLockDate < staleCutoff)
            .ExecuteUpdateAsync(u => u
                .SetProperty(s => s.MigrationStatus, "Migrated")
                .SetProperty(s => s.VerifyLockedByWorker, (string?)null)
                .SetProperty(s => s.VerifyLockDate, (DateTime?)null));

        // VerifyRetryPending must wait at least retryDelaySeconds since last attempt
        var retryCutoff = DateTime.UtcNow.AddSeconds(-retryDelaySeconds);

        // Acquire studies in 'Migrated' state, or 'VerifyRetryPending' whose delay elapsed.
        var candidate = await db.MigrationStudies
            .Where(s => s.MigrationId == migrationId
                     && s.VerifyLockedByWorker == null
                     && (s.MigrationStatus == "Migrated"
                         || (s.MigrationStatus == "VerifyRetryPending" && s.LastUpdateDate < retryCutoff)))
            .OrderBy(s => s.MigrationStatus == "VerifyRetryPending" ? 1 : 0)
            .ThenBy(s => s.Id)
            .FirstOrDefaultAsync();

        if (candidate is null) return null;

        // Atomic lock: mark VerificationPending + set verify lock + start timestamp
        var locked = await db.MigrationStudies
            .Where(s => s.Id == candidate.Id && s.VerifyLockedByWorker == null
                     && (s.MigrationStatus == "Migrated" || s.MigrationStatus == "VerifyRetryPending"))
            .ExecuteUpdateAsync(u => u
                .SetProperty(s => s.MigrationStatus, "VerificationPending")
                .SetProperty(s => s.VerifyLockedByWorker, workerId)
                .SetProperty(s => s.VerifyLockDate, DateTime.UtcNow)
                .SetProperty(s => s.VerificationStartDate, DateTime.UtcNow));

        return locked > 0 ? candidate : null;
    }

    /// <summary>Finalize a verification attempt: Verified on success, or VerifyRetryPending
    /// (if retries remain) / Failed (if exhausted) on mismatch. Clears the verify lock.</summary>
    public async Task ReleaseVerificationLockAsync(long id)
    {
        // Connection error during verification: put the study back to 'Migrated'
        // and clear the verify lock, leaving VerifyRetryCount untouched (it wasn't
        // the study's fault that the destination was unreachable).
        await using var db = factory.CreateDbContext();
        await db.MigrationStudies
            .Where(s => s.Id == id)
            .ExecuteUpdateAsync(u => u
                .SetProperty(s => s.MigrationStatus, "Migrated")
                .SetProperty(s => s.VerifyLockedByWorker, (string?)null)
                .SetProperty(s => s.VerifyLockDate, (DateTime?)null)
                .SetProperty(s => s.VerificationStartDate, (DateTime?)null)
                .SetProperty(s => s.LastUpdateDate, DateTime.UtcNow));
    }

    public async Task CompleteVerificationAsync(long id, bool success, int maxRetries,
        int? targetSeries, int? targetInstances, string? error = null)
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

    public async Task ReleaseOrphanLocksAsync(int migrationId)
    {
        await using var db = factory.CreateDbContext();

        // Migración: estudios atrapados en 'Queued' (lock de migración) → 'Pending'.
        await db.MigrationStudies
            .Where(s => s.MigrationId == migrationId && s.MigrationStatus == "Queued")
            .ExecuteUpdateAsync(u => u
                .SetProperty(s => s.MigrationStatus, "Pending")
                .SetProperty(s => s.LockedByWorker, (string?)null)
                .SetProperty(s => s.LockDate, (DateTime?)null)
                .SetProperty(s => s.MigrationStartDate, (DateTime?)null));

        // Verificación: estudios con lock de verificación colgado → liberar el lock,
        // dejándolos de nuevo verificables (su MigrationStatus 'Migrated' no cambia).
        await db.MigrationStudies
            .Where(s => s.MigrationId == migrationId && s.VerifyLockedByWorker != null)
            .ExecuteUpdateAsync(u => u
                .SetProperty(s => s.VerifyLockedByWorker, (string?)null)
                .SetProperty(s => s.VerifyLockDate, (DateTime?)null));
    }

    public async Task ReleaseLocksAsync(string workerId)
    {
        await using var db = factory.CreateDbContext();
        await db.MigrationStudies
            .Where(s => s.LockedByWorker == workerId)
            .ExecuteUpdateAsync(u => u
                .SetProperty(s => s.MigrationStatus, "Pending")
                .SetProperty(s => s.LockedByWorker, (string?)null)
                .SetProperty(s => s.LockDate, (DateTime?)null));
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

    public async Task<MigrationStats> GetStatsAsync(int migrationId)
    {
        await using var db = factory.CreateDbContext();
        var groups = await db.MigrationStudies
            .Where(s => s.MigrationId == migrationId)
            .GroupBy(s => s.MigrationStatus)
            .Select(g => new { Status = g.Key, Count = (long)g.Count() })
            .ToListAsync();

        var stats = new MigrationStats { MigrationId = migrationId };
        foreach (var g in groups)
        {
            switch (g.Status)
            {
                case "Pending":             stats.Pending             = g.Count; break;
                case "Queued":              stats.Queued              = g.Count; break;
                case "Migrating":           stats.Migrating           = g.Count; break;
                case "Migrated":            stats.Migrated            = g.Count; break;
                case "VerificationPending": stats.VerificationPending = g.Count; break;
                case "Verified":            stats.Verified            = g.Count; break;
                case "Failed":              stats.Failed              = g.Count; break;
                case "RetryPending":        stats.RetryPending        = g.Count; break;
                case "VerifyFailed":        stats.VerifyFailed        = g.Count; break;
                case "VerifyRetryPending":  stats.VerifyRetryPending  = g.Count; break;
                case "Cancelled":           stats.Cancelled           = g.Count; break;
            }
        }
        stats.Total = groups.Sum(g => g.Count);

        // Calculate total elapsed time: sum of individual study durations
        // Migration: MigrationDate - MigrationStartDate (for Migrated + Verified + VerificationPending)
        // Verification: VerificationDate - VerificationStartDate (for Verified only)
        var timings = await db.MigrationStudies
            .Where(s => s.MigrationId == migrationId
                     && (s.MigrationDate != null || s.VerificationDate != null))
            .Select(s => new
            {
                s.MigrationDate,
                s.MigrationStartDate,
                s.VerificationDate,
                s.VerificationStartDate,
            })
            .ToListAsync();

        stats.TotalMigrationSeconds = timings
            .Where(s => s.MigrationDate.HasValue && s.MigrationStartDate.HasValue)
            .Sum(s => (s.MigrationDate!.Value - s.MigrationStartDate!.Value).TotalSeconds);

        stats.TotalVerificationSeconds = timings
            .Where(s => s.VerificationDate.HasValue && s.VerificationStartDate.HasValue)
            .Sum(s => (s.VerificationDate!.Value - s.VerificationStartDate!.Value).TotalSeconds);

        // Wall-clock: from first start to last finish (actual elapsed time)
        var migDone = timings.Where(s => s.MigrationDate.HasValue && s.MigrationStartDate.HasValue).ToList();
        if (migDone.Count > 0)
            stats.WallMigrationSeconds = (migDone.Max(s => s.MigrationDate!.Value)
                                        - migDone.Min(s => s.MigrationStartDate!.Value)).TotalSeconds;

        var verDone = timings.Where(s => s.VerificationDate.HasValue && s.VerificationStartDate.HasValue).ToList();
        if (verDone.Count > 0)
            stats.WallVerificationSeconds = (verDone.Max(s => s.VerificationDate!.Value)
                                           - verDone.Min(s => s.VerificationStartDate!.Value)).TotalSeconds;

        return stats;
    }

    public async Task ExportToCsvAsync(int migrationId, StudyFilter filter, Stream output)
    {
        await using var db = factory.CreateDbContext();
        var studies = await ApplyFilter(db.MigrationStudies, migrationId, filter)
            .OrderByDescending(s => s.DiscoveryDate)
            .ToListAsync();

        await using var writer = new StreamWriter(output, Encoding.UTF8, leaveOpen: true);
        await writer.WriteLineAsync("StudyInstanceUID,PatientID,AccessionNumber,StudyDate,Modality,Status,SrcSeries,SrcInstances,TgtSeries,TgtInstances,Retries,LastError,DiscoveryDate,MigrationDate,VerificationDate");
        foreach (var s in studies)
            await writer.WriteLineAsync(
                $"\"{s.StudyInstanceUid}\",\"{s.PatientId}\",\"{s.AccessionNumber}\"," +
                $"\"{s.StudyDate}\",\"{s.ModalitiesInStudy}\",\"{s.MigrationStatus}\"," +
                $"{s.SourceSeriesCount},{s.SourceInstanceCount}," +
                $"{s.TargetSeriesCount},{s.TargetInstanceCount}," +
                $"{s.RetryCount},\"{s.LastError?.Replace("\"", "'")}\"," +
                $"\"{s.DiscoveryDate:yyyy-MM-dd HH:mm:ss}\"," +
                $"\"{s.MigrationDate:yyyy-MM-dd HH:mm:ss}\"," +
                $"\"{s.VerificationDate:yyyy-MM-dd HH:mm:ss}\"");
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
