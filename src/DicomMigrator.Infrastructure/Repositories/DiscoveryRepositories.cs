using DicomMigrator.Core.Interfaces;
using DicomMigrator.Core.Models;
using DicomMigrator.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace DicomMigrator.Infrastructure.Repositories;

file static class StringExtensions
{
    internal static string? NullIfEmpty(this string? s) =>
        string.IsNullOrWhiteSpace(s) ? null : s;
}

// ═══════════════════════════════════════════════════════════════════════════
// RF-020 — Discovery Engine repositories
// ═══════════════════════════════════════════════════════════════════════════

public class DiscoveryJobRepository(IDbContextFactory<AppDbContext> factory) : IDiscoveryJobRepository
{
    public async Task<List<DiscoveryJob>> GetAllAsync()
    {
        await using var db = factory.CreateDbContext();
        return await db.DiscoveryJobs
            .Include(j => j.SourcePacs)
            .OrderByDescending(j => j.CreatedDate)
            .ToListAsync();
    }

    public async Task<DiscoveryJob?> GetByIdAsync(int id)
    {
        await using var db = factory.CreateDbContext();
        return await db.DiscoveryJobs
            .Include(j => j.SourcePacs)
            .FirstOrDefaultAsync(j => j.Id == id);
    }

    public async Task<DiscoveryJob> CreateAsync(DiscoveryJob job)
    {
        await using var db = factory.CreateDbContext();
        db.DiscoveryJobs.Add(job);
        await db.SaveChangesAsync();
        return job;
    }

    public async Task<DiscoveryJob> UpdateAsync(DiscoveryJob job)
    {
        await using var db = factory.CreateDbContext();
        db.DiscoveryJobs.Update(job);
        await db.SaveChangesAsync();
        return job;
    }

    public async Task<bool> UpdateStatusAsync(int id, string status)
    {
        await using var db = factory.CreateDbContext();
        var rows = await db.DiscoveryJobs
            .Where(j => j.Id == id)
            .ExecuteUpdateAsync(u => u
                .SetProperty(j => j.Status, status)
                .SetProperty(j => j.StartedDate,
                    j => status == "Running" && j.StartedDate == null ? DateTime.UtcNow : j.StartedDate)
                .SetProperty(j => j.FinishedDate,
                    j => status == "Completed" || status == "Cancelled" || status == "Failed"
                        ? DateTime.UtcNow : j.FinishedDate));
        return rows > 0;
    }

    public async Task<bool> UpdateCaptureStatusAsync(int id, string captureStatus)
    {
        await using var db = factory.CreateDbContext();
        var rows = await db.DiscoveryJobs
            .Where(j => j.Id == id)
            .ExecuteUpdateAsync(u => u.SetProperty(j => j.CaptureStatus, captureStatus));
        return rows > 0;
    }

    public async Task<bool> SetCaptureRunningAsync(int id, bool resetTimer)
    {
        await using var db = factory.CreateDbContext();
        var q = db.DiscoveryJobs.Where(j => j.Id == id);
        int rows;
        if (resetTimer)
        {
            var now = DateTime.UtcNow;
            rows = await q.ExecuteUpdateAsync(u => u
                .SetProperty(j => j.CaptureStatus, "Running")
                .SetProperty(j => j.CaptureStartedDate, now)
                .SetProperty(j => j.CaptureFinishedDate, (DateTime?)null));
        }
        else
        {
            rows = await q.ExecuteUpdateAsync(u => u
                .SetProperty(j => j.CaptureStatus, "Running"));
        }
        return rows > 0;
    }

    public async Task<bool> FinishCaptureAsync(int id, string captureStatus)
    {
        await using var db = factory.CreateDbContext();
        var now = DateTime.UtcNow;
        var rows = await db.DiscoveryJobs
            .Where(j => j.Id == id)
            .ExecuteUpdateAsync(u => u
                .SetProperty(j => j.CaptureStatus, captureStatus)
                .SetProperty(j => j.CaptureFinishedDate, now));
        return rows > 0;
    }

    public async Task<int> RetryFailedPartitionsAsync(int jobId)
    {
        await using var db = factory.CreateDbContext();
        return await db.DiscoveryPartitions
            .Where(p => p.DiscoveryJobId == jobId && p.Status == "Failed")
            .ExecuteUpdateAsync(u => u
                .SetProperty(p => p.Status,          "Pending")
                .SetProperty(p => p.AttemptCount,    0)
                .SetProperty(p => p.LastError,       (string?)null)
                .SetProperty(p => p.LockedByWorker,  (string?)null)
                .SetProperty(p => p.LockDate,        (DateTime?)null)
                .SetProperty(p => p.StartedAt,       (DateTime?)null)
                .SetProperty(p => p.FinishedAt,      (DateTime?)null));
    }

    public async Task<CsvImportStats> ImportFromCsvAsync(
        int jobId, Stream csvStream, CancellationToken ct = default)
    {
        var stats = new CsvImportStats();
        var seenUids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var batch    = new List<DiscoveredStudy>(500);
        var now      = DateTime.UtcNow;

        using var reader = new StreamReader(csvStream, System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: true);

        string? headerLine = await reader.ReadLineAsync(ct);
        if (headerLine is null) return stats;

        // Detect separator: prefer semicolon if it appears more than comma
        char sep = headerLine.Count(c => c == ';') >= headerLine.Count(c => c == ',') ? ';' : ',';

        // Map column names → indexes (case-insensitive)
        var headers = headerLine.Split(sep).Select(h => h.Trim().Trim('"').ToUpperInvariant()).ToList();
        int Idx(params string[] names) => names.Select(n => headers.IndexOf(n)).FirstOrDefault(i => i >= 0, -1);

        int iUid  = Idx("STUDYINSTANCEUID", "STUDY_INSTANCE_UID", "UID");
        int iPat  = Idx("PATIENTID",  "PATIENT_ID",  "MRN");
        int iName = Idx("PATIENTNAME","PATIENT_NAME","NAME");
        int iAcc  = Idx("ACCESSIONNUMBER","ACCESSION_NUMBER","ACCESSION");
        int iDate = Idx("STUDYDATE",  "STUDY_DATE",  "DATE");
        int iTime = Idx("STUDYTIME",  "STUDY_TIME",  "TIME");
        int iMod  = Idx("MODALITIES", "MODALITY",    "MOD");
        int iDesc = Idx("STUDYDESCRIPTION","STUDY_DESCRIPTION","DESCRIPTION","DESC");

        if (iUid < 0)
        {
            stats.Errors++;
            stats.ErrorDetails.Add("No se encontró la columna StudyInstanceUID en la cabecera.");
            return stats;
        }

        string? line;
        while ((line = await reader.ReadLineAsync(ct)) is not null)
        {
            ct.ThrowIfCancellationRequested();
            stats.TotalLines++;

            if (string.IsNullOrWhiteSpace(line)) { stats.Skipped++; continue; }

            var cols = line.Split(sep);
            string uid = cols.Length > iUid ? cols[iUid].Trim().Trim('"') : "";

            if (string.IsNullOrWhiteSpace(uid))
            {
                stats.Errors++;
                if (stats.ErrorDetails.Count < 20)
                    stats.ErrorDetails.Add($"Línea {stats.TotalLines}: UID vacío.");
                continue;
            }

            if (!seenUids.Add(uid)) { stats.Skipped++; continue; }   // dup in same file

            string? Get(int i) => (i >= 0 && i < cols.Length)
                ? cols[i].Trim().Trim('"').NullIfEmpty()
                : null;

            batch.Add(new DiscoveredStudy
            {
                DiscoveryJobId  = jobId,
                StudyInstanceUid = uid,
                PatientId        = Get(iPat),
                PatientName      = Get(iName),
                AccessionNumber  = Get(iAcc),
                StudyDate        = Get(iDate),
                StudyTime        = Get(iTime),
                ModalitiesInStudy = Get(iMod),
                StudyDescription = Get(iDesc),
                DiscoveryDate    = now,
                LastUpdatedDate  = now,
            });

            if (batch.Count >= 500)
            {
                var (ins, upd) = await UpsertBatchAsync(batch, ct);
                stats.Inserted += ins;
                stats.Updated  += upd;
                batch.Clear();
            }
        }

        if (batch.Count > 0)
        {
            var (ins, upd) = await UpsertBatchAsync(batch, ct);
            stats.Inserted += ins;
            stats.Updated  += upd;
        }

        // Mark the job Completed — CSV mode has no partitions or workers
        await using var db = factory.CreateDbContext();
        await db.DiscoveryJobs.Where(j => j.Id == jobId)
            .ExecuteUpdateAsync(u => u
                .SetProperty(j => j.Status,       "Completed")
                .SetProperty(j => j.StartedDate,  now)
                .SetProperty(j => j.FinishedDate, now), ct);

        return stats;
    }

    private async Task<(int inserted, int updated)> UpsertBatchAsync(
        List<DiscoveredStudy> batch, CancellationToken ct)
    {
        int ins = 0, upd = 0;
        await using var db = factory.CreateDbContext();
        foreach (var s in batch)
        {
            var existing = await db.DiscoveredStudies
                .Where(x => x.StudyInstanceUid == s.StudyInstanceUid)
                .OrderBy(x => x.Id)
                .FirstOrDefaultAsync(ct);
            if (existing is null)
            {
                db.DiscoveredStudies.Add(s);
                ins++;
            }
            else
            {
                existing.PatientId        ??= s.PatientId;
                existing.PatientName      ??= s.PatientName;
                existing.AccessionNumber  ??= s.AccessionNumber;
                existing.StudyDate        ??= s.StudyDate;
                existing.StudyTime        ??= s.StudyTime;
                existing.ModalitiesInStudy ??= s.ModalitiesInStudy;
                existing.StudyDescription ??= s.StudyDescription;
                existing.DiscoveryJobId   = s.DiscoveryJobId;
                existing.LastUpdatedDate  = s.LastUpdatedDate;
                upd++;
            }
        }
        await db.SaveChangesAsync(ct);
        return (ins, upd);
    }

    public async Task DeleteAsync(int id)
    {
        await using var db = factory.CreateDbContext();
        // Remove all data associated with this job from the database:
        // - DiscoveredStudies discovered by this job
        // - DiscoveryRequests logged for this job
        // - DiscoveryPartitions (also cascade-deleted, but explicit for clarity)
        // - the DiscoveryJob itself
        await db.DiscoveredStudies.Where(s => s.DiscoveryJobId == id).ExecuteDeleteAsync();
        await db.DiscoveryRequests.Where(r => r.DiscoveryJobId == id).ExecuteDeleteAsync();
        await db.DiscoveryPartitions.Where(p => p.DiscoveryJobId == id).ExecuteDeleteAsync();
        await db.DiscoveryJobs.Where(j => j.Id == id).ExecuteDeleteAsync();
    }

    public async Task ResetJobAsync(int id)
    {
        await using var db = factory.CreateDbContext();

        // 1) Remove discovered studies and request logs for this job
        await db.DiscoveredStudies.Where(s => s.DiscoveryJobId == id).ExecuteDeleteAsync();
        await db.DiscoveryRequests.Where(r => r.DiscoveryJobId == id).ExecuteDeleteAsync();

        // 2) Remove subdivided child partitions (those created adaptively on truncation).
        //    Only the original day-level partitions are kept and reset.
        await db.DiscoveryPartitions
            .Where(p => p.DiscoveryJobId == id && p.PartitionType != "Day")
            .ExecuteDeleteAsync();

        // 3) Reset the remaining (day) partitions to their initial Pending state
        await db.DiscoveryPartitions
            .Where(p => p.DiscoveryJobId == id)
            .ExecuteUpdateAsync(u => u
                .SetProperty(p => p.Status, "Pending")
                .SetProperty(p => p.AttemptCount, 0)
                .SetProperty(p => p.StudiesFound, 0)
                .SetProperty(p => p.StudiesInserted, 0)
                .SetProperty(p => p.StudiesUpdated, 0)
                .SetProperty(p => p.StartedAt, (DateTime?)null)
                .SetProperty(p => p.FinishedAt, (DateTime?)null)
                .SetProperty(p => p.DurationMs, (double?)null)
                .SetProperty(p => p.LastError, (string?)null)
                .SetProperty(p => p.LockedByWorker, (string?)null)
                .SetProperty(p => p.LockDate, (DateTime?)null));

        // 4) Reset the job to Draft as if never started — config preserved
        await db.DiscoveryJobs
            .Where(j => j.Id == id)
            .ExecuteUpdateAsync(u => u
                .SetProperty(j => j.Status, "Draft")
                .SetProperty(j => j.StartedDate, (DateTime?)null)
                .SetProperty(j => j.FinishedDate, (DateTime?)null));
    }

    // ── Partitions ──────────────────────────────────────────────────────────
    public async Task<List<DiscoveryPartition>> GetPartitionsAsync(int jobId)
    {
        await using var db = factory.CreateDbContext();
        return await db.DiscoveryPartitions
            .Where(p => p.DiscoveryJobId == jobId)
            .OrderBy(p => p.StartDate).ThenBy(p => p.Modality)
            .ToListAsync();
    }

    public async Task<(List<DiscoveryPartition> items, int total)> GetPartitionsPagedAsync(
        int jobId, int pageNumber, int pageSize)
    {
        await using var db = factory.CreateDbContext();
        var baseQuery = db.DiscoveryPartitions.Where(p => p.DiscoveryJobId == jobId);

        var total = await baseQuery.CountAsync();
        if (pageNumber < 1) pageNumber = 1;

        var items = await baseQuery
            .OrderBy(p => p.StartDate).ThenBy(p => p.Modality)
            .Skip((pageNumber - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();

        return (items, total);
    }

    public async Task AddPartitionsAsync(IEnumerable<DiscoveryPartition> partitions)
    {
        await using var db = factory.CreateDbContext();
        db.DiscoveryPartitions.AddRange(partitions);
        await db.SaveChangesAsync();
    }

    public async Task DeletePartitionsAsync(int jobId)
    {
        await using var db = factory.CreateDbContext();
        await db.DiscoveryPartitions
            .Where(p => p.DiscoveryJobId == jobId)
            .ExecuteDeleteAsync();
    }

    public async Task<DiscoveryPartition?> AcquireNextPendingPartitionAsync(int jobId, string workerId)
    {
        await using var db = factory.CreateDbContext();
        var next = await db.DiscoveryPartitions
            .Where(p => p.DiscoveryJobId == jobId && p.LockedByWorker == null
                     && p.Status == "Pending")   // only Pending — PossiblyTruncated is terminal
            .OrderBy(p => p.StartDate).ThenBy(p => p.Modality)
            .FirstOrDefaultAsync();
        if (next is null) return null;

        var locked = await db.DiscoveryPartitions
            .Where(p => p.Id == next.Id && p.LockedByWorker == null)
            .ExecuteUpdateAsync(u => u
                .SetProperty(p => p.Status, "Running")
                .SetProperty(p => p.LockedByWorker, workerId)
                .SetProperty(p => p.LockDate, DateTime.UtcNow)
                .SetProperty(p => p.StartedAt, DateTime.UtcNow));
        return locked > 0 ? next : null;
    }

    public async Task UpdatePartitionAsync(DiscoveryPartition partition)
    {
        await using var db = factory.CreateDbContext();
        db.DiscoveryPartitions.Update(partition);
        await db.SaveChangesAsync();
    }

    // ── Stats & logging ─────────────────────────────────────────────────────
    public async Task<DiscoveryStats> GetStatsAsync(int jobId)
    {
        await using var db = factory.CreateDbContext();
        var stats = new DiscoveryStats { JobId = jobId };

        var partGroups = await db.DiscoveryPartitions
            .Where(p => p.DiscoveryJobId == jobId)
            .GroupBy(p => p.Status)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToListAsync();

        foreach (var g in partGroups)
        {
            stats.TotalPartitions += g.Count;
            switch (g.Status)
            {
                case "Completed":         stats.CompletedPartitions  = g.Count; break;
                case "Subdivided":        stats.SubdividedPartitions = g.Count; break;
                case "Pending":           stats.PendingPartitions    = g.Count; break;
                case "Running":           stats.RunningPartitions    = g.Count; break;
                case "Failed":            stats.FailedPartitions     = g.Count; break;
                case "PossiblyTruncated": stats.TruncatedPartitions  = g.Count; break;
            }
        }

        // Study counts from the actual inventory.
        // DiscoveryJobId is always updated on upsert so this correctly reflects
        // all studies found by this job (including those already in the inventory).
        stats.StudiesDiscovered = await db.DiscoveredStudies
            .CountAsync(s => s.DiscoveryJobId == jobId);

        stats.StudiesNew = await db.DiscoveryPartitions
            .Where(p => p.DiscoveryJobId == jobId)
            .SumAsync(p => p.StudiesInserted);

        stats.StudiesExisting = stats.StudiesDiscovered - stats.StudiesNew;
        if (stats.StudiesExisting < 0) stats.StudiesExisting = 0;

        var reqGroups = await db.DiscoveryRequests
            .Where(r => r.DiscoveryJobId == jobId)
            .GroupBy(r => r.Result)
            .Select(g => new { Result = g.Key, Count = g.Count() })
            .ToListAsync();

        foreach (var g in reqGroups)
        {
            stats.TotalRequests += g.Count;
            if (g.Result == "OK") stats.OkRequests = g.Count;
            else                  stats.FailedRequests += g.Count;
        }

        // Elapsed + estimate
        var job = await db.DiscoveryJobs.FindAsync(jobId);
        if (job?.StartedDate is not null)
        {
            var end = job.FinishedDate ?? DateTime.UtcNow;
            stats.ElapsedSeconds = (end - job.StartedDate.Value).TotalSeconds;

            if (stats.CompletedPartitions > 0 && stats.PendingPartitions > 0)
            {
                var perPartition = stats.ElapsedSeconds / stats.CompletedPartitions;
                stats.EstimatedRemainingSeconds = perPartition * stats.PendingPartitions;
            }
        }

        return stats;
    }

    public async Task AddRequestLogAsync(DiscoveryRequest request)
    {
        await using var db = factory.CreateDbContext();
        db.DiscoveryRequests.Add(request);
        await db.SaveChangesAsync();
    }
}

public class DiscoveredStudyRepository(IDbContextFactory<AppDbContext> factory) : IDiscoveredStudyRepository
{
    public async Task<Dictionary<int, (int Series, int Instances)>> GetPartitionUidStatsAsync(int jobId)
    {
        await using var db = factory.CreateDbContext();

        // Suma de series (conteo declarado en descubrimiento) por partición.
        var series = await db.DiscoveredStudies
            .Where(s => s.DiscoveryJobId == jobId && s.PartitionId != null)
            .GroupBy(s => s.PartitionId!.Value)
            .Select(g => new { Pid = g.Key, Series = g.Sum(x => x.NumberOfStudyRelatedSeries ?? 0) })
            .ToListAsync();

        // Nº de UIDs (SOPInstanceUID) capturados por partición (tras enumerar).
        var instances = await db.DiscoveredInstances
            .Where(i => i.Study != null && i.Study.DiscoveryJobId == jobId && i.Study.PartitionId != null)
            .GroupBy(i => i.Study!.PartitionId!.Value)
            .Select(g => new { Pid = g.Key, Count = g.Count() })
            .ToListAsync();

        var dict = new Dictionary<int, (int Series, int Instances)>();
        foreach (var x in series) dict[x.Pid] = (x.Series, 0);
        foreach (var x in instances)
        {
            var s = dict.TryGetValue(x.Pid, out var v) ? v.Series : 0;
            dict[x.Pid] = (s, x.Count);
        }
        return dict;
    }

    public async Task<List<DiscoveredStudy>> GetPagedAsync(DiscoveredStudyFilter f)
    {
        await using var db = factory.CreateDbContext();
        var q = Filtered(db, f);
        return await q
            .OrderByDescending(s => s.DiscoveryDate)
            .Skip((f.PageNumber - 1) * f.PageSize)
            .Take(f.PageSize)
            .ToListAsync();
    }

    public async Task<int> CountAsync(DiscoveredStudyFilter f)
    {
        await using var db = factory.CreateDbContext();
        return await Filtered(db, f).CountAsync();
    }

    public async Task<long> CountBySourceAsync(int sourcePacsId)
    {
        await using var db = factory.CreateDbContext();
        return await db.DiscoveredStudies.Where(s => s.SourcePacsId == sourcePacsId).LongCountAsync();
    }

    public async Task<List<DiscoveredStudy>> GetAllForExportAsync(DiscoveredStudyFilter f)
    {
        await using var db = factory.CreateDbContext();
        return await Filtered(db, f)
            .OrderBy(s => s.StudyDate).ThenBy(s => s.PatientId)
            .ToListAsync();
    }

    public async IAsyncEnumerable<DiscoveredStudy> StreamForExportAsync(
        DiscoveredStudyFilter f,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        // Page through the result set so the full inventory is never in memory at once.
        // Each call opens a fresh DbContext to avoid long-lived contexts in case the
        // streaming consumer is slow.
        const int PageSize = 1000;
        int offset = 0;

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            List<DiscoveredStudy> page;
            await using (var db = factory.CreateDbContext())
            {
                page = await Filtered(db, f)
                    .OrderBy(s => s.StudyDate).ThenBy(s => s.PatientId).ThenBy(s => s.Id)
                    .Skip(offset).Take(PageSize)
                    .AsNoTracking()
                    .ToListAsync(ct);
            }

            if (page.Count == 0) yield break;
            foreach (var s in page) yield return s;
            if (page.Count < PageSize) yield break;
            offset += PageSize;
        }
    }

    public async Task<(string? min, string? max)> GetStudyDateRangeAsync(int discoveryJobId)
    {
        await using var db = factory.CreateDbContext();
        var dates = await db.DiscoveredStudies
            .Where(s => s.DiscoveryJobId == discoveryJobId && s.StudyDate != null)
            .Select(s => s.StudyDate!)
            .ToListAsync();
        if (dates.Count == 0) return (null, null);
        return (dates.Min(), dates.Max());
    }

    public async Task<(int inserted, int updated)> UpsertAsync(IEnumerable<DiscoveredStudy> studies)
    {
        await using var db = factory.CreateDbContext();
        int inserted = 0, updated = 0;

        foreach (var s in studies)
        {
            if (string.IsNullOrEmpty(s.StudyInstanceUid)) continue;

            var existing = await db.DiscoveredStudies
                .Where(x => x.StudyInstanceUid == s.StudyInstanceUid)
                .OrderBy(x => x.Id)
                .FirstOrDefaultAsync();

            if (existing is null)
            {
                db.DiscoveredStudies.Add(s);
                inserted++;
            }
            else
            {
                // Update metadata fields that were empty — keep traceability
                existing.PatientId         ??= s.PatientId;
                existing.PatientName       ??= s.PatientName;
                existing.AccessionNumber   ??= s.AccessionNumber;
                existing.StudyDate         ??= s.StudyDate;
                existing.StudyTime         ??= s.StudyTime;
                existing.StudyDescription  ??= s.StudyDescription;
                existing.ModalitiesInStudy ??= s.ModalitiesInStudy;
                existing.NumberOfStudyRelatedSeries    ??= s.NumberOfStudyRelatedSeries;
                existing.NumberOfStudyRelatedInstances ??= s.NumberOfStudyRelatedInstances;
                existing.InstitutionName   ??= s.InstitutionName;
                // Always update job reference so stats count correctly for the current job
                existing.DiscoveryJobId  = s.DiscoveryJobId;
                // Atribuir a la partición que lo (re)descubrió; rellena inventario antiguo.
                existing.PartitionId     = s.PartitionId ?? existing.PartitionId;
                existing.LastUpdatedDate = DateTime.UtcNow;
                updated++;
            }
        }

        await db.SaveChangesAsync();
        return (inserted, updated);
    }

    private static IQueryable<DiscoveredStudy> Filtered(AppDbContext db, DiscoveredStudyFilter f)
    {
        var q = db.DiscoveredStudies.AsQueryable();
        if (f.SourcePacsId.HasValue)   q = q.Where(s => s.SourcePacsId == f.SourcePacsId);
        if (f.DiscoveryJobId.HasValue) q = q.Where(s => s.DiscoveryJobId == f.DiscoveryJobId);
        if (!string.IsNullOrWhiteSpace(f.StudyInstanceUid)) q = q.Where(s => s.StudyInstanceUid.Contains(f.StudyInstanceUid));
        if (!string.IsNullOrWhiteSpace(f.PatientId))        q = q.Where(s => s.PatientId!.Contains(f.PatientId));
        if (!string.IsNullOrWhiteSpace(f.AccessionNumber))  q = q.Where(s => s.AccessionNumber!.Contains(f.AccessionNumber));
        if (!string.IsNullOrWhiteSpace(f.StudyDateFrom))    q = q.Where(s => string.Compare(s.StudyDate, f.StudyDateFrom) >= 0);
        if (!string.IsNullOrWhiteSpace(f.StudyDateTo))      q = q.Where(s => string.Compare(s.StudyDate, f.StudyDateTo) <= 0);
        if (!string.IsNullOrWhiteSpace(f.Modality))         q = q.Where(s => s.ModalitiesInStudy!.Contains(f.Modality));
        return q;
    }
}
