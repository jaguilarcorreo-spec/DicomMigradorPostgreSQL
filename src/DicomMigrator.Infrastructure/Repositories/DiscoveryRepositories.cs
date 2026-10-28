using DicomMigrator.Core.Interfaces;
using DicomMigrator.Core.Models;
using DicomMigrator.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;

namespace DicomMigrator.Infrastructure.Repositories;

file static class StringExtensions
{
    internal static string? NullIfEmpty(this string? s) =>
        string.IsNullOrWhiteSpace(s) ? null : s;
}

// ═══════════════════════════════════════════════════════════════════════════
// RF-020 — Discovery Engine repositories
// ═══════════════════════════════════════════════════════════════════════════

public class DiscoveryJobRepository(IDbContextFactory<AppDbContext> factory, DeferredVacuum vacuum) : IDiscoveryJobRepository
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

        // Cargar en UNA sola consulta todas las filas que puedan coincidir con el lote
        // (mismo job + mismo UID), en vez de un FirstOrDefaultAsync por estudio. Con
        // inventarios de millones de filas, el N+1 anterior convertía una importación
        // de horas en días.
        var jobIds = batch.Select(s => s.DiscoveryJobId).Distinct().ToList();
        var uids   = batch.Select(s => s.StudyInstanceUid).Distinct().ToList();
        var existing = await db.DiscoveredStudies
            .Where(x => jobIds.Contains(x.DiscoveryJobId) && uids.Contains(x.StudyInstanceUid))
            .ToListAsync(ct);
        var byKey = existing
            .GroupBy(x => (x.DiscoveryJobId, x.StudyInstanceUid))
            .ToDictionary(g => g.Key, g => g.OrderBy(x => x.Id).First());

        foreach (var s in batch)
        {
            // Inventario POR JOB: buscar solo dentro de este job; el mismo UID en otro
            // job es una fila independiente y NO se reasigna.
            if (byKey.TryGetValue((s.DiscoveryJobId, s.StudyInstanceUid), out var match))
            {
                match.PatientId        ??= s.PatientId;
                match.PatientName      ??= s.PatientName;
                match.AccessionNumber  ??= s.AccessionNumber;
                match.StudyDate        ??= s.StudyDate;
                match.StudyTime        ??= s.StudyTime;
                match.ModalitiesInStudy ??= s.ModalitiesInStudy;
                match.StudyDescription ??= s.StudyDescription;
                // Ya NO se reasigna DiscoveryJobId (la fila es de este mismo job).
                match.LastUpdatedDate  = s.LastUpdatedDate;
                upd++;
            }
            else
            {
                db.DiscoveredStudies.Add(s);
                ins++;
            }
        }
        await db.SaveChangesAsync(ct);
        return (ins, upd);
    }

    public async Task DeleteAsync(int id, IProgress<long>? progress = null)
    {
        // Por lotes (DISC-13): primero el inventario (cada estudio arrastra sus instancias),
        // luego las peticiones y por último particiones y job. El job se borra AL FINAL: si
        // algo falla a mitad, sigue existiendo y volver a borrarlo continúa donde se quedó.
        await BatchDelete.RunAsync(factory, "DiscoveredStudies", "DiscoveryJobId", id, BatchDelete.StudiesPerBatch, progress);
        await BatchDelete.RunAsync(factory, "DiscoveryRequests", "DiscoveryJobId", id, BatchDelete.RowsPerBatch);
        await using var db = factory.CreateDbContext();
        await db.DiscoveryPartitions.Where(p => p.DiscoveryJobId == id).ExecuteDeleteAsync();
        await db.DiscoveryJobs.Where(j => j.Id == id).ExecuteDeleteAsync();
        vacuum.Request("DiscoveredStudies", "DiscoveredInstances", "DiscoveryRequests", "DiscoveryPartitions");
    }

    public async Task ResetJobAsync(int id, IProgress<long>? progress = null)
    {
        // 1) Remove discovered studies and request logs for this job, por lotes (DISC-13)
        await BatchDelete.RunAsync(factory, "DiscoveredStudies", "DiscoveryJobId", id, BatchDelete.StudiesPerBatch, progress);
        await BatchDelete.RunAsync(factory, "DiscoveryRequests", "DiscoveryJobId", id, BatchDelete.RowsPerBatch);

        await using var db = factory.CreateDbContext();

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
                .SetProperty(j => j.FinishedDate, (DateTime?)null)
                // El inventario (y con él sus UIDs) se ha vaciado.
                .SetProperty(j => j.CapturedStudies, 0)
                .SetProperty(j => j.CapturedInstances, 0));

        // El reset vacía el inventario del job para volver a descubrirlo: VACUUM en
        // segundo plano para que el nuevo descubrimiento reutilice las páginas de índice.
        vacuum.Request("DiscoveredStudies", "DiscoveredInstances", "DiscoveryRequests", "DiscoveryPartitions");
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
        // Adquisición ATÓMICA (DISC-3), como la cola de migración (CONC-9). Antes era leer la
        // primera pendiente y bloquearla en otro paso: todos los workers leían la MISMA, uno
        // ganaba y los demás recibían «nada» y salían como si no quedara trabajo; un job con
        // 8 hilos trabajaba con 3-4, y al final con uno. Ahora una sola sentencia bloquea la
        // primera partición que nadie tenga (FOR UPDATE SKIP LOCKED): cada worker obtiene una
        // distinta. Solo 'Pending' (PossiblyTruncated es terminal). Mismo orden que antes.
        await using var db = factory.CreateDbContext();
        var now = DateTime.UtcNow;
        var claimed = await db.DiscoveryPartitions.FromSqlRaw(@"
UPDATE ""DiscoveryPartitions"" AS p
SET ""Status"" = 'Running', ""LockedByWorker"" = @worker, ""LockDate"" = @now, ""StartedAt"" = @now
WHERE p.""Id"" = (
    SELECT s.""Id"" FROM ""DiscoveryPartitions"" AS s
    WHERE s.""DiscoveryJobId"" = @job AND s.""LockedByWorker"" IS NULL AND s.""Status"" = 'Pending'
    ORDER BY s.""StartDate"", s.""Modality"", s.""Id""
    LIMIT 1
    FOR UPDATE SKIP LOCKED)
RETURNING p.*",
                new NpgsqlParameter("job", jobId), new NpgsqlParameter("worker", workerId), new NpgsqlParameter("now", now))
            .AsNoTracking().ToListAsync();
        return claimed.FirstOrDefault();
    }

    public async Task<int> ReleaseOrphanPartitionsAsync(int jobId)
    {
        // Particiones atrapadas en 'Running' (lock de un worker que ya no existe: caída
        // abrupta del proceso, o fallo de BD al cerrarlas) → 'Pending', liberando el lock.
        // AttemptCount NO se toca: el worker solo lo persiste al cerrar la partición, así
        // que el valor en BD es el de antes del intento interrumpido (no se penaliza).
        // Solo es seguro llamarlo cuando no hay workers vivos de ese job.
        await using var db = factory.CreateDbContext();
        return await db.DiscoveryPartitions
            .Where(p => p.DiscoveryJobId == jobId && p.Status == "Running")
            .ExecuteUpdateAsync(u => u
                .SetProperty(p => p.Status,         "Pending")
                .SetProperty(p => p.LockedByWorker, (string?)null)
                .SetProperty(p => p.LockDate,       (DateTime?)null)
                .SetProperty(p => p.StartedAt,      (DateTime?)null));
    }

    public async Task ReleasePartitionAsync(long partitionId)
    {
        await using var db = factory.CreateDbContext();
        await db.DiscoveryPartitions
            .Where(p => p.Id == partitionId && p.Status == "Running")
            .ExecuteUpdateAsync(u => u
                .SetProperty(p => p.Status,         "Pending")
                .SetProperty(p => p.LockedByWorker, (string?)null)
                .SetProperty(p => p.LockDate,       (DateTime?)null)
                .SetProperty(p => p.StartedAt,      (DateTime?)null));
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

        // Study counts from the actual inventory. Inventario POR JOB: cada job tiene sus
        // propias filas de DiscoveredStudy, así que contar por DiscoveryJobId es exacto y no
        // se solapa con otros jobs (aunque compartan StudyInstanceUID).
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
    public async Task<Dictionary<int, (int Series, int Instances)>> GetPartitionUidStatsAsync(int jobId,
        IReadOnlyCollection<int> partitionIds)
    {
        if (partitionIds.Count == 0) return new();
        var pids = partitionIds.ToList();
        await using var db = factory.CreateDbContext();

        // Suma de series (conteo declarado en descubrimiento) por partición.
        var series = await db.DiscoveredStudies
            .Where(s => s.DiscoveryJobId == jobId && s.PartitionId != null && pids.Contains(s.PartitionId.Value))
            .GroupBy(s => s.PartitionId!.Value)
            .Select(g => new { Pid = g.Key, Series = g.Sum(x => x.NumberOfStudyRelatedSeries ?? 0) })
            .ToListAsync();

        // Nº de UIDs (SOPInstanceUID) capturados por partición (tras enumerar).
        var instances = await db.DiscoveredInstances
            .Where(i => i.Study != null && i.Study.DiscoveryJobId == jobId && i.Study.PartitionId != null
                     && pids.Contains(i.Study.PartitionId.Value))
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
        // Por clave, en orden de Id (= orden de descubrimiento), DISC-14. Antes: OFFSET
        // ordenando por fecha, paciente e Id, sin índice para ese orden; cada página ordenaba
        // el job entero (4 s por página con 1 millón de estudios: más de 1 hora en total).
        // Ahora cada página es un rango del índice IX_DiscoveredStudies_Job_Id.
        const int PageSize = 1000;
        long lastId = 0;

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            List<DiscoveredStudy> page;
            await using (var db = factory.CreateDbContext())
            {
                var after = lastId;
                page = await Filtered(db, f)
                    .Where(s => s.Id > after)
                    .OrderBy(s => s.Id)
                    .Take(PageSize)
                    .AsNoTracking()
                    .ToListAsync(ct);
            }

            if (page.Count == 0) yield break;
            foreach (var s in page) yield return s;
            if (page.Count < PageSize) yield break;
            lastId = page[^1].Id;
        }
    }

    public async IAsyncEnumerable<(long Id, string Uid)> StreamCaptureCandidatesAsync(int jobId, bool includeCaptured,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        // DISC-14: antes la captura recorría el inventario entero con la exportación lenta,
        // leyendo los estudios completos, y luego hacía una consulta por estudio para saltarse
        // los ya capturados. Ahora: tramos por Id, solo Id y UID, y los ya capturados se
        // descartan en la propia consulta (NOT EXISTS sobre el índice de instancias).
        const int PageSize = 5000;
        long lastId = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            List<(long, string)> page;
            await using (var db = factory.CreateDbContext())
            {
                var after = lastId;
                var q = db.DiscoveredStudies.AsNoTracking()
                    .Where(s => s.DiscoveryJobId == jobId && s.Id > after && s.StudyInstanceUid != "");
                if (!includeCaptured)
                    q = q.Where(s => !db.DiscoveredInstances.Any(i => i.DiscoveredStudyId == s.Id));
                page = (await q.OrderBy(s => s.Id).Take(PageSize)
                        .Select(s => new { s.Id, s.StudyInstanceUid })
                        .ToListAsync(ct))
                    .Select(x => (x.Id, x.StudyInstanceUid)).ToList();
            }
            if (page.Count == 0) yield break;
            foreach (var p in page) yield return p;
            if (page.Count < PageSize) yield break;
            lastId = page[^1].Item1;
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
        // ── DISC-5 ─────────────────────────────────────────────────────────────
        // Antes: leer los existentes, decidir en memoria e insertar con SaveChanges. Fallaba
        // con clave duplicada (23505) y la partición quedaba «Failed» para siempre si (a) el
        // PACS repetía un UID en la misma respuesta (se añadían dos filas iguales) o (b) dos
        // workers guardaban a la vez el mismo estudio (p. ej. a las 06:00:00, en el límite
        // de dos franjas horarias): los dos lo veían «nuevo». DISC-3 lo ocultaba al trabajar
        // con un solo worker.
        // Ahora: (1) se funden los repetidos del lote (el primero manda; los siguientes solo
        // rellenan huecos) y (2) un único INSERT … ON CONFLICT DO UPDATE resuelve en la base,
        // de forma atómica, si cada estudio es nuevo o ya existía en el job. Al actualizar se
        // conserva la regla de siempre: solo se rellenan los campos vacíos, la partición pasa
        // a ser la que lo ha (re)descubierto y se anota la fecha.
        var merged = new Dictionary<(int?, string), DiscoveredStudy>();
        foreach (var s in studies)
        {
            if (string.IsNullOrEmpty(s.StudyInstanceUid)) continue;
            var key = (s.DiscoveryJobId, s.StudyInstanceUid);
            if (!merged.TryGetValue(key, out var m)) { merged[key] = s; continue; }
            m.PatientId         ??= s.PatientId;
            m.PatientName       ??= s.PatientName;
            m.AccessionNumber   ??= s.AccessionNumber;
            m.StudyDate         ??= s.StudyDate;
            m.StudyTime         ??= s.StudyTime;
            m.StudyDescription  ??= s.StudyDescription;
            m.ModalitiesInStudy ??= s.ModalitiesInStudy;
            m.NumberOfStudyRelatedSeries    ??= s.NumberOfStudyRelatedSeries;
            m.NumberOfStudyRelatedInstances ??= s.NumberOfStudyRelatedInstances;
            m.InstitutionName   ??= s.InstitutionName;
            m.RetrieveAETitle   ??= s.RetrieveAETitle;
            m.PatientBirthDate  ??= s.PatientBirthDate;
            m.PatientSex        ??= s.PatientSex;
            m.IssuerOfPatientId ??= s.IssuerOfPatientId;
            m.PartitionId       ??= s.PartitionId;
        }
        if (merged.Count == 0) return (0, 0);
        var list = merged.Values.ToList();

        static NpgsqlParameter Arr<T>(string name, NpgsqlDbType type, IEnumerable<T> values) =>
            new(name, NpgsqlDbType.Array | type) { Value = values.ToArray() };

        await using var db = factory.CreateDbContext();
        // Una fila por estudio con «Value» = true si se insertó y false si ya existía.
        var results = await db.Database.SqlQueryRaw<bool>(@"
INSERT INTO ""DiscoveredStudies"" AS t
    (""DiscoveryJobId"", ""PartitionId"", ""SourcePacsId"", ""StudyInstanceUid"", ""PatientId"", ""IssuerOfPatientId"",
     ""PatientName"", ""PatientBirthDate"", ""PatientSex"", ""AccessionNumber"", ""StudyDate"", ""StudyTime"",
     ""StudyDescription"", ""ModalitiesInStudy"", ""NumberOfStudyRelatedSeries"", ""NumberOfStudyRelatedInstances"",
     ""InstitutionName"", ""RetrieveAETitle"", ""DiscoveryDate"")
SELECT * FROM unnest(@job, @part, @pacs, @uid, @pid, @issuer, @pname, @pbirth, @psex, @acc, @sdate, @stime,
                     @sdesc, @mods, @nser, @ninst, @inst, @aet, @disc)
ON CONFLICT (""DiscoveryJobId"", ""StudyInstanceUid"") DO UPDATE SET
    ""PatientId""         = COALESCE(t.""PatientId"",         EXCLUDED.""PatientId""),
    ""IssuerOfPatientId"" = COALESCE(t.""IssuerOfPatientId"", EXCLUDED.""IssuerOfPatientId""),
    ""PatientName""       = COALESCE(t.""PatientName"",       EXCLUDED.""PatientName""),
    ""PatientBirthDate""  = COALESCE(t.""PatientBirthDate"",  EXCLUDED.""PatientBirthDate""),
    ""PatientSex""        = COALESCE(t.""PatientSex"",        EXCLUDED.""PatientSex""),
    ""AccessionNumber""   = COALESCE(t.""AccessionNumber"",   EXCLUDED.""AccessionNumber""),
    ""StudyDate""         = COALESCE(t.""StudyDate"",         EXCLUDED.""StudyDate""),
    ""StudyTime""         = COALESCE(t.""StudyTime"",         EXCLUDED.""StudyTime""),
    ""StudyDescription""  = COALESCE(t.""StudyDescription"",  EXCLUDED.""StudyDescription""),
    ""ModalitiesInStudy"" = COALESCE(t.""ModalitiesInStudy"", EXCLUDED.""ModalitiesInStudy""),
    ""NumberOfStudyRelatedSeries""    = COALESCE(t.""NumberOfStudyRelatedSeries"",    EXCLUDED.""NumberOfStudyRelatedSeries""),
    ""NumberOfStudyRelatedInstances"" = COALESCE(t.""NumberOfStudyRelatedInstances"", EXCLUDED.""NumberOfStudyRelatedInstances""),
    ""InstitutionName""   = COALESCE(t.""InstitutionName"",   EXCLUDED.""InstitutionName""),
    ""RetrieveAETitle""   = COALESCE(t.""RetrieveAETitle"",   EXCLUDED.""RetrieveAETitle""),
    ""PartitionId""       = COALESCE(EXCLUDED.""PartitionId"", t.""PartitionId""),
    ""LastUpdatedDate""   = now()
RETURNING (xmax = 0) AS ""Value""",
            Arr("job",    NpgsqlDbType.Integer,     list.Select(s => s.DiscoveryJobId)),
            Arr("part",   NpgsqlDbType.Integer,     list.Select(s => s.PartitionId)),
            Arr("pacs",   NpgsqlDbType.Integer,     list.Select(s => s.SourcePacsId)),
            Arr("uid",    NpgsqlDbType.Varchar,     list.Select(s => s.StudyInstanceUid)),
            Arr("pid",    NpgsqlDbType.Text,        list.Select(s => s.PatientId)),
            Arr("issuer", NpgsqlDbType.Text,        list.Select(s => s.IssuerOfPatientId)),
            Arr("pname",  NpgsqlDbType.Text,        list.Select(s => s.PatientName)),
            Arr("pbirth", NpgsqlDbType.Text,        list.Select(s => s.PatientBirthDate)),
            Arr("psex",   NpgsqlDbType.Text,        list.Select(s => s.PatientSex)),
            Arr("acc",    NpgsqlDbType.Text,        list.Select(s => s.AccessionNumber)),
            Arr("sdate",  NpgsqlDbType.Text,        list.Select(s => s.StudyDate)),
            Arr("stime",  NpgsqlDbType.Text,        list.Select(s => s.StudyTime)),
            Arr("sdesc",  NpgsqlDbType.Text,        list.Select(s => s.StudyDescription)),
            Arr("mods",   NpgsqlDbType.Text,        list.Select(s => s.ModalitiesInStudy)),
            Arr("nser",   NpgsqlDbType.Integer,     list.Select(s => s.NumberOfStudyRelatedSeries)),
            Arr("ninst",  NpgsqlDbType.Integer,     list.Select(s => s.NumberOfStudyRelatedInstances)),
            Arr("inst",   NpgsqlDbType.Text,        list.Select(s => s.InstitutionName)),
            Arr("aet",    NpgsqlDbType.Text,        list.Select(s => s.RetrieveAETitle)),
            Arr("disc",   NpgsqlDbType.TimestampTz, list.Select(s => DateTime.SpecifyKind(s.DiscoveryDate, DateTimeKind.Utc))))
            .ToListAsync();

        var inserted = results.Count(r => r);
        return (inserted, results.Count - inserted);
    }

    private static IQueryable<DiscoveredStudy> Filtered(AppDbContext db, DiscoveredStudyFilter f)
    {
        var q = db.DiscoveredStudies.AsQueryable();
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
