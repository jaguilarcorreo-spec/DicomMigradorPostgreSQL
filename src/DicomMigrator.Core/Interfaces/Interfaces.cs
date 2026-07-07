using DicomMigrator.Core.Models;

namespace DicomMigrator.Core.Interfaces;

// ══════════════════════════════════════════════════════════════════════════════
// REPOSITORIOS
// ══════════════════════════════════════════════════════════════════════════════

public interface INodeRepository
{
    Task<List<DicomNode>> GetAllAsync();
    Task<DicomNode?> GetByIdAsync(int id);
    Task<DicomNode> CreateAsync(DicomNode node);
    Task<DicomNode> UpdateAsync(DicomNode node);
    Task DeleteAsync(int id);
}

public interface IMigrationRepository
{
    Task<List<Migration>> GetAllAsync();
    Task<Migration?> GetByIdAsync(int id);
    Task<Migration> CreateAsync(Migration migration);
    Task<Migration> UpdateAsync(Migration migration);
    Task<bool> UpdateStatusAsync(int id, string status);
    /// <summary>Update VerificationStatus (Idle|Running|Paused|Completed), independent of Status.</summary>
    Task<bool> UpdateVerificationStatusAsync(int id, string verificationStatus);
    /// <summary>Set/clear the flag marking the MIGRATION as auto-paused by connection errors.</summary>
    Task SetMigrationAutoPausedAsync(int id, bool autoPaused);
    /// <summary>Set/clear the flag marking the VERIFICATION as auto-paused by connection errors.</summary>
    Task SetVerificationAutoPausedAsync(int id, bool autoPaused);
    /// <summary>All migrations currently auto-paused (migration or verification) due to
    /// connection errors, with origin/dest nodes loaded — for the auto-resume watcher.</summary>
    Task<List<Migration>> GetAutoPausedAsync();
    Task DeleteAsync(int id);
}

/// <summary>Servicio de captura de UIDs de ORIGEN (Nivel 2 de verificación).</summary>
public interface IInstanceCaptureService
{
    /// <summary>Captura los UIDs de origen de todos los estudios de un job de
    /// descubrimiento (salta los ya capturados salvo forceRecapture).</summary>
    Task<InstanceCaptureResult> CaptureForJobAsync(int jobId, bool forceRecapture = false, CancellationToken ct = default);

    /// <summary>Captura (enumera + persiste) los UIDs de origen de un estudio descubierto.</summary>
    Task<int> CaptureForStudyAsync(DicomNode originNode, long discoveredStudyId, string studyInstanceUid, CancellationToken ct = default);

    // ── Proceso gobernado (Start/Pause/Resume/Stop), por job ──────────────────
    Task StartCaptureAsync(int jobId, CancellationToken ct = default);
    Task PauseCaptureAsync(int jobId);
    Task ResumeCaptureAsync(int jobId, CancellationToken ct = default);
    Task StopCaptureAsync(int jobId);
}

/// <summary>Repositorio de instancias (UIDs) de ORIGEN capturadas en el descubrimiento (Nivel 2).</summary>
public interface IDiscoveredInstanceRepository
{
    Task<int> AddRangeAsync(long discoveredStudyId,
        IEnumerable<(string SeriesInstanceUid, string SopInstanceUid)> instances);
    Task<int> CountForStudyAsync(long discoveredStudyId);
    /// <summary>Nº de estudios de un job que ya tienen UIDs capturados (para la barra de progreso de enumeración).</summary>
    Task<int> CountCapturedStudiesForJobAsync(int jobId);
    /// <summary>Nº total de instancias (UIDs) capturadas de un job (acumulado).</summary>
    Task<int> CountInstancesForJobAsync(int jobId);
    Task DeleteForStudyAsync(long discoveredStudyId);
}

/// <summary>Repositorio de instancias (UIDs) de ORIGEN para verificación Nivel 2.</summary>
public interface IInstanceRepository
{
    /// <summary>Inserta el conjunto de instancias (Series/SOP UID) de un estudio.</summary>
    Task<int> AddRangeAsync(long migrationStudyId,
        IEnumerable<(string SeriesInstanceUid, string SopInstanceUid)> instances);

    /// <summary>Nº de instancias capturadas para un estudio.</summary>
    Task<int> CountForStudyAsync(long migrationStudyId);

    /// <summary>Nº total de UIDs (instancias) copiados a una migración (indica si es Nivel 2).</summary>
    Task<int> CountForMigrationAsync(int migrationId);

    /// <summary>Conjunto de SOPInstanceUID capturados de un estudio (para comparar en verificación).</summary>
    Task<HashSet<string>> GetSopUidsForStudyAsync(long migrationStudyId);

    /// <summary>Borra las instancias capturadas de un estudio (para re-captura).</summary>
    Task DeleteForStudyAsync(long migrationStudyId);
}

public interface IStudyRepository
{
    Task<List<MigrationStudy>> GetPagedAsync(int migrationId, StudyFilter filter);
    /// <summary>
    /// Keyset (cursor) pagination — scales to millions of rows without the OFFSET
    /// penalty. Returns the next PageSize rows after the cursor in the filter.
    /// Order is DiscoveryDate DESC, Id DESC (Id breaks ties for a stable cursor).
    /// </summary>
    Task<List<MigrationStudy>> GetPageKeysetAsync(int migrationId, StudyFilter filter);
    Task<int> CountAsync(int migrationId, StudyFilter filter);
    Task<MigrationStudy?> GetByIdAsync(long id);
    Task<MigrationStudy?> GetByUidAsync(int migrationId, string studyInstanceUid);
    /// <summary>Stream studies matching the filter in pages without loading the full set into memory.</summary>
    IAsyncEnumerable<MigrationStudy> StreamForExportAsync(int migrationId, StudyFilter filter, CancellationToken ct = default);

    /// <summary>Bulk-insert discovered studies (ignores existing UIDs).</summary>
    Task<int> BulkInsertAsync(int migrationId, IEnumerable<MigrationStudy> studies);
    /// <summary>Import studies from the Discovery inventory into a migration, applying optional filter.</summary>
    Task<int> ImportFromInventoryAsync(int migrationId, DiscoveredStudyFilter filter);

    /// <summary>Pick next pending study for a worker (atomic lock).</summary>
    Task<MigrationStudy?> AcquireNextPendingAsync(int migrationId, string workerId,
        IEnumerable<string> modalityPriority, int retryDelaySeconds = 60,
        DateOnly? startFromDate = null, bool oldestFirst = false);

    /// <summary>Atomically acquire the next study to verify ('Migrated' or due
    /// 'VerifyRetryPending'), using the SEPARATE verification lock so it never
    /// collides with migration workers. Sets VerificationPending + verify lock.</summary>
    Task<MigrationStudy?> AcquireNextForVerificationAsync(int migrationId, string workerId,
        int retryDelaySeconds = 60);

    /// <summary>Finalize a verification attempt with retry logic mirroring migration:
    /// Verified on success; VerifyRetryPending if retries remain; Failed if exhausted.</summary>
    Task CompleteVerificationAsync(long id, bool success, int maxRetries,
        int? targetSeries, int? targetInstances, string? error = null);

    /// <summary>Release the verification lock and return the study to 'Migrated'
    /// WITHOUT consuming a retry — used when the destination couldn't be reached
    /// (connection error), so the study is verified again later, not failed.</summary>
    Task ReleaseVerificationLockAsync(long id);

    /// <summary>True if the migration still has studies to verify: any in 'Migrated',
    /// 'VerificationPending' (in flight) or 'VerifyRetryPending' (awaiting retry).</summary>
    Task<bool> HasVerificationWorkPendingAsync(int migrationId);

    Task UpdateStatusAsync(long id, string status, string? error = null);
    Task UpdateVerificationAsync(long id, string status, int? targetSeries, int? targetInstances);
    Task UpdateVerificationStartAsync(long id);
    /// <summary>Return up to 'limit' studies currently in VerificationPending state.
    /// Always reads from the start (no offset): callers drain the set by changing
    /// each study's status as they process it, so re-querying brings the remainder.</summary>
    Task<List<MigrationStudy>> GetNextVerificationBatchAsync(int migrationId, int limit, CancellationToken ct = default);
    /// <summary>Bulk-transition all Migrated studies of a migration to VerificationPending
    /// in a single UPDATE. Returns the number of studies affected.</summary>
    Task<int> EnqueueAllMigratedForVerificationAsync(int migrationId);
    /// <summary>Mark study as VerificationPending without setting VerificationStartDate — timer starts when worker picks it up.</summary>
    Task EnqueueForVerificationAsync(long id);
    Task ReleaseLocksAsync(string workerId);
    /// <summary>Libera TODOS los locks huérfanos de una migración (de cualquier worker),
    /// devolviendo a 'Pending'/'Migrated' los estudios que quedaron en estado intermedio
    /// ('Queued' o en verificación) tras una caída del proceso. Pensado para el arranque
    /// en instancia única, donde un proceso recién iniciado no tiene workers vivos, por lo
    /// que cualquier lock existente es necesariamente huérfano. No espera el timeout de 10 min.</summary>
    Task ReleaseOrphanLocksAsync(int migrationId);
    /// <summary>Return a single study to 'Pending' and clear its migration lock without
    /// consuming a retry — used on a SOURCE connection error (transient).</summary>
    Task ReleaseMigrationLockAsync(long id);
    Task RetryFailedAsync(int migrationId);
    /// <summary>Requeue studies that FAILED VERIFICATION (VerifyFailed) back to
    /// 'Migrated' so the verification workers retry them, resetting VerifyRetryCount.</summary>
    Task RetryVerifyFailedAsync(int migrationId);
    Task CancelStudyAsync(long id);
    Task<MigrationStats> GetStatsAsync(int migrationId);
    Task ExportToCsvAsync(int migrationId, StudyFilter filter, Stream output);

    /// <summary>Delete all studies of a migration (reset to pre-discovery state).</summary>
    Task DeleteAllAsync(int migrationId);
}

public interface IAuditLogRepository
{
    Task<List<MigrationAuditLog>> GetAsync(int migrationId, int limit = 200);
    /// <summary>Fetch the most recent N audit log entries across ALL migrations in a single query.</summary>
    Task<List<MigrationAuditLog>> GetRecentAsync(int limit = 200);
    Task AddAsync(MigrationAuditLog log);
}

public interface ILocalConfigRepository
{
    Task<LocalConfiguration> GetAsync();
    Task<LocalConfiguration> SaveAsync(LocalConfiguration config);
}

// ── Discovery Engine (RF-020) ───────────────────────────────────────────────
public interface IDiscoveryJobRepository
{
    Task<List<DiscoveryJob>> GetAllAsync();
    Task<DiscoveryJob?> GetByIdAsync(int id);
    Task<DiscoveryJob> CreateAsync(DiscoveryJob job);
    Task<DiscoveryJob> UpdateAsync(DiscoveryJob job);
    Task<bool> UpdateStatusAsync(int id, string status);
    /// <summary>Update CaptureStatus (Idle|Running|Paused|Completed|Failed) for Nivel 2 UID capture.</summary>
    Task<bool> UpdateCaptureStatusAsync(int id, string captureStatus);
    /// <summary>Marca la enumeración como Running. Si resetTimer, reinicia el cronómetro
    /// (inicio=ahora, fin=null); si no (reanudación), conserva las marcas.</summary>
    Task<bool> SetCaptureRunningAsync(int id, bool resetTimer);
    /// <summary>Fija el estado final de la enumeración (Completed|Failed) y la marca de fin.</summary>
    Task<bool> FinishCaptureAsync(int id, string captureStatus);
    /// <summary>Reset a job to as-if-never-started: partitions back to Pending, counters zeroed, discovered studies and request logs removed. Config preserved.</summary>
    Task ResetJobAsync(int id);
    /// <summary>Reset all Failed partitions back to Pending so workers pick them up on the next run. Returns number of partitions reset.</summary>
    Task<int> RetryFailedPartitionsAsync(int jobId);
    /// <summary>Parse a CSV stream and upsert studies into the discovery inventory. Returns (inserted, updated, skipped, errors).</summary>
    Task<CsvImportStats> ImportFromCsvAsync(int jobId, Stream csvStream, CancellationToken ct = default);
    Task DeleteAsync(int id);

    // Partitions
    Task<List<DiscoveryPartition>> GetPartitionsAsync(int jobId);
    /// <summary>Paged partitions. Returns (items, totalCount).</summary>
    Task<(List<DiscoveryPartition> items, int total)> GetPartitionsPagedAsync(int jobId, int pageNumber, int pageSize);
    Task AddPartitionsAsync(IEnumerable<DiscoveryPartition> partitions);
    /// <summary>Delete all partitions of a job. Used when the job's date range changes,
    /// so stale partitions from a previous configuration don't linger.</summary>
    Task DeletePartitionsAsync(int jobId);
    Task<DiscoveryPartition?> AcquireNextPendingPartitionAsync(int jobId, string workerId);
    Task UpdatePartitionAsync(DiscoveryPartition partition);

    // Stats
    Task<DiscoveryStats> GetStatsAsync(int jobId);
    Task AddRequestLogAsync(DiscoveryRequest request);
}

public interface IDiscoveredStudyRepository
{
    Task<List<DiscoveredStudy>> GetPagedAsync(DiscoveredStudyFilter filter);
    /// <summary>Agregados por partición (columnas SERIE/INSTANCE UIDs): suma de series y nº de UIDs capturados, por PartitionId.</summary>
    Task<Dictionary<int, (int Series, int Instances)>> GetPartitionUidStatsAsync(int jobId);
    Task<int> CountAsync(DiscoveredStudyFilter filter);
    /// <summary>Upsert by StudyInstanceUID. Returns (inserted, updated) counts.</summary>
    Task<(int inserted, int updated)> UpsertAsync(IEnumerable<DiscoveredStudy> studies);
    Task<long> CountBySourceAsync(int sourcePacsId);
    /// <summary>Return all studies matching the filter (no paging) for export.</summary>
    Task<List<DiscoveredStudy>> GetAllForExportAsync(DiscoveredStudyFilter filter);
    /// <summary>Stream studies in pages without loading the entire result set into memory.</summary>
    IAsyncEnumerable<DiscoveredStudy> StreamForExportAsync(DiscoveredStudyFilter filter, CancellationToken ct = default);
    /// <summary>Return (minStudyDate, maxStudyDate) for a discovery job's inventory. Nulls if no studies or no dates.</summary>
    Task<(string? min, string? max)> GetStudyDateRangeAsync(int discoveryJobId);
}

// ══════════════════════════════════════════════════════════════════════════════
// SERVICIOS DICOM  (los mismos del Tester, reutilizados directamente)
// ══════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Wrapper de los servicios DIMSE del DicomPacsTester.
/// Reutiliza DimseTestService y CMoveService sin modificación.
/// </summary>
public interface IDimseService
{
    Task<EchoResult>  EchoAsync(DicomNode node, CancellationToken ct = default);
    Task<CFindResult> FindAsync(DicomNode node, CFindQuery query, CancellationToken ct = default);
    /// <summary>Enumera las instancias (SOPInstanceUID + SeriesInstanceUID) de un
    /// estudio vía C-FIND a nivel IMAGE. Base del Nivel 2 de verificación.</summary>
    Task<CFindInstancesResult> EnumerateInstancesAsync(DicomNode node, string studyInstanceUid, CancellationToken ct = default);
    Task<CMoveResult> MoveAsync(DicomNode node, CMoveRequest request, CancellationToken ct = default);
}

/// <summary>Live connectivity status of a migration's origin and destination nodes,
/// probed with C-ECHO and cached briefly so the UI can show health without hammering
/// the PACS on every refresh.</summary>
public interface IConnectionHealthService
{
    /// <summary>Get cached health for a migration's nodes, probing if the cache is
    /// stale (older than the cache window). Returns origin and destination status.</summary>
    Task<ConnectionHealth> GetHealthAsync(int migrationId, CancellationToken ct = default);

    /// <summary>Force a fresh probe now, ignoring the cache.</summary>
    Task<ConnectionHealth> ProbeNowAsync(int migrationId, CancellationToken ct = default);

    /// <summary>Probe a single node directly (used by auto-resume to check recovery).</summary>
    Task<NodeHealth> ProbeNodeAsync(DicomNode node, CancellationToken ct = default);
}

/// <summary>
/// Wrapper de los servicios DICOMweb del DicomPacsTester.
/// Reutiliza DicomWebTestService sin modificación.
/// </summary>
public interface IDicomWebService
{
    Task<QidoResult>     QidoAsync(DicomNode node, QidoQuery query, CancellationToken ct = default);
    Task<WadoStowResult> WadoStowAsync(DicomNode origin, DicomNode dest,
                                       string studyUid, CancellationToken ct = default);
}

// ══════════════════════════════════════════════════════════════════════════════
// SERVICIOS DE MIGRACIÓN  (nuevos, específicos de este proyecto)
// ══════════════════════════════════════════════════════════════════════════════

public interface IDiscoveryService
{
    /// <summary>Discover studies via C-FIND and insert into study table.</summary>
    Task<CsvImportResult> DiscoverViaCFindAsync(int migrationId, CFindQuery query, CancellationToken ct = default);

    /// <summary>Discover studies via QIDO-RS and insert into study table.</summary>
    Task<CsvImportResult> DiscoverViaQidoAsync(int migrationId, QidoQuery query, CancellationToken ct = default);

    /// <summary>Import studies from a CSV stream and insert into study table.</summary>
    Task<CsvImportResult> ImportFromCsvAsync(int migrationId, Stream csv, CancellationToken ct = default);
}

public interface IVerificationService
{
    /// <summary>Cancela todas las verificaciones activas al apagar el proceso (sin tocar BD).</summary>
    void CancelAllForShutdown();

    /// <summary>Verify a single migrated study against the destination node.</summary>
    Task<VerificationResult> VerifyStudyAsync(DicomNode destNode, MigrationStudy study,
                                               CancellationToken ct = default);

    /// <summary>Start the verification process: launches worker threads that acquire
    /// 'Migrated' studies one by one (atomic verify-lock), verify them, and retry on
    /// mismatch up to MaxRetries. Runs independently of migration. Idempotent.</summary>
    Task StartVerificationAsync(int migrationId, CancellationToken ct = default);

    /// <summary>Pause verification: stops acquiring new studies. Studies not yet taken
    /// remain 'Migrated' so verification can resume later.</summary>
    Task PauseVerificationAsync(int migrationId);

    /// <summary>Resume a paused verification.</summary>
    Task ResumeVerificationAsync(int migrationId, CancellationToken ct = default);

    /// <summary>Stop verification entirely (cancel workers, set status Idle).</summary>
    Task StopVerificationAsync(int migrationId);
}

public interface IMigrationWorker
{
    /// <summary>Start worker threads for a migration.</summary>
    Task StartAsync(int migrationId, CancellationToken ct = default);

    /// <summary>Cancela todos los workers activos al apagar el proceso (sin tocar BD).</summary>
    void CancelAllForShutdown();

    /// <summary>Pause all workers for a migration gracefully.</summary>
    Task PauseAsync(int migrationId);

    /// <summary>Resume workers for a paused migration.</summary>
    Task ResumeAsync(int migrationId, CancellationToken ct = default);
}

public interface IWindowScheduler
{
    /// <summary>Returns true if the migration window is currently open.</summary>
    bool IsWindowOpen(ExecutionWindow window);

    /// <summary>Background service that starts/pauses migrations based on their windows.</summary>
    Task RunAsync(CancellationToken ct = default);
}

// ── Discovery Engine (RF-020) ───────────────────────────────────────────────
public interface IDiscoveryEngine
{
    /// <summary>Generate daily partitions for a Temporal job based on its date range.</summary>
    Task GeneratePartitionsAsync(int jobId, CancellationToken ct = default);

    /// <summary>Start the discovery worker(s) for a job. Runs in background.</summary>
    Task StartAsync(int jobId, CancellationToken ct = default);

    /// <summary>Pause the discovery job gracefully.</summary>
    Task PauseAsync(int jobId);

    /// <summary>Resume a paused job from the next pending partition.</summary>
    Task ResumeAsync(int jobId, CancellationToken ct = default);
}
