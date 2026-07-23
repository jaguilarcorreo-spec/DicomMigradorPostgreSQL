namespace DicomMigrator.Core.Models;

// ══════════════════════════════════════════════════════════════════════════════
// NODOS DICOM  (equivalente a Pacs + DimseConfiguration + DicomWebConfiguration
//               del DicomPacsTester, renombrado y con campos de migración)
// ══════════════════════════════════════════════════════════════════════════════

public class DicomNode
{
    public int      Id          { get; set; }
    public string   Alias       { get; set; } = string.Empty;
    public string   Description { get; set; } = string.Empty;

    /// <summary>origin | destination | both</summary>
    public string   NodeType    { get; set; } = "origin";

    public bool     IsActive    { get; set; } = true;
    public DateTime CreatedAt   { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt   { get; set; } = DateTime.UtcNow;

    // ── DIMSE (reutilizado de DimseConfiguration del Tester) ─────────────────
    public string   LocalAet                  { get; set; } = "MIGRATOR_SCU";
    public string   RemoteAet                 { get; set; } = string.Empty;
    public string   RemoteHost                { get; set; } = string.Empty;
    public int      RemotePort                { get; set; } = 11112;
    public bool     UseTls                    { get; set; } = false;
    public int      AssociationTimeoutSeconds { get; set; } = 30;
    public int      OperationTimeoutSeconds   { get; set; } = 120;
    public int      MaxConcurrentAssociations { get; set; } = 10;

    // ── DICOMweb (reutilizado de DicomWebConfiguration del Tester) ───────────
    public bool     HasDicomWeb        { get; set; } = false;
    public string?  QidoBaseUrl        { get; set; }  // kept for backward compat
    public string?  WadoBaseUrl        { get; set; }  // kept for backward compat
    public string?  StowBaseUrl        { get; set; }  // kept for backward compat
    // Separate fields (same pattern as DicomPacsTester)
    public string?  WebBaseUrl         { get; set; }  // e.g. http://host:8042/dicom-web
    public string?  WebQidoPath        { get; set; }  // e.g. /studies
    public string?  WebWadoPath        { get; set; }  // e.g. /studies
    public string?  WebStowPath        { get; set; }  // e.g. /studies

    /// <summary>None | Basic | Bearer | ApiKey</summary>
    public string   AuthType           { get; set; } = "None";
    public string?  AuthUsername       { get; set; }
    public string?  EncryptedSecret    { get; set; }
    public bool     ValidateTls        { get; set; } = true;
    public int      HttpTimeoutSeconds { get; set; } = 30;

    // ── Navigation ───────────────────────────────────────────────────────────
    public ICollection<Migration> MigrationsAsOrigin { get; set; } = [];
    public ICollection<Migration> MigrationsAsDest   { get; set; } = [];
}

// ══════════════════════════════════════════════════════════════════════════════
// MIGRACIÓN
// ══════════════════════════════════════════════════════════════════════════════

public class Migration
{
    public int      Id          { get; set; }
    public string   Name        { get; set; } = string.Empty;
    public string   Description { get; set; } = string.Empty;

    public int      OriginNodeId { get; set; }
    public int      DestNodeId   { get; set; }

    /// <summary>Draft | Ready | Running | Paused | Completed | Failed | Cancelled</summary>
    public string   Status       { get; set; } = "Draft";

    /// <summary>Estado del proceso de verificación, INDEPENDIENTE de Status (migración).
    /// Idle | Running | Paused | Completed. Permite verificar y migrar a la vez.</summary>
    public string   VerificationStatus { get; set; } = "Idle";

    /// <summary>Nivel de verificación de estudios: 1 = por conteos (series/instancias),
    /// 2 = comparación de conjuntos de UIDs (requiere capturar los SOPInstanceUID de
    /// ORIGEN en el descubrimiento). Se fija al crear la migración.</summary>
    public int      VerificationLevel  { get; set; } = 1;

    /// <summary>CSV | C-FIND | QIDO-RS</summary>
    public string   DiscoveryMethod  { get; set; } = "C-FIND";

    /// <summary>C-MOVE | C-GET | WADO-RS+STOW-RS</summary>
    public string   TransferMethod   { get; set; } = "C-MOVE";

    public int      WorkerThreads    { get; set; } = 4;

    /// <summary>Comma-separated modality priority list, e.g. "MG,CT,MR,CR,OT"</summary>
    public string   ModalityPriority { get; set; } = "CT,MR,CR,DX,US,MG,NM,PT,XA,RF,OT,SC,PR,SR,KO";

    public int      MaxRetries       { get; set; } = 3;

    /// <summary>If set, only studies with StudyDate >= this date are migrated.</summary>
    public DateOnly? StartFromDate   { get; set; }

    /// <summary>If true, studies are processed oldest-first (ascending StudyDate). Default is priority-order.</summary>
    public bool     OldestFirst      { get; set; }

    /// <summary>Oldest study date in the source inventory (populated when created from CSV/Discovery).</summary>
    public string?  InventoryDateFrom { get; set; }

    /// <summary>Newest study date in the source inventory (populated when created from CSV/Discovery).</summary>
    public string?  InventoryDateTo   { get; set; }
    public int      RetryDelaySeconds { get; set; } = 60;

    /// <summary>True when the migration was auto-paused due to repeated source
    /// connection errors (not a manual pause). Used to auto-resume when the node
    /// becomes reachable again. Cleared on any manual Start/Resume/Stop.</summary>
    public bool     MigrationAutoPaused   { get; set; }
    /// <summary>Same as MigrationAutoPaused but for the verification process
    /// (destination unreachable).</summary>
    public bool     VerificationAutoPaused { get; set; }

    public string   CreatedBy  { get; set; } = string.Empty;
    public DateTime CreatedAt  { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt  { get; set; } = DateTime.UtcNow;
    public DateTime? StartedAt  { get; set; }
    public DateTime? FinishedAt { get; set; }

    // ── Navigation ───────────────────────────────────────────────────────────
    public DicomNode?            OriginNode     { get; set; }
    public DicomNode?            DestNode       { get; set; }
    public ExecutionWindow?      Window         { get; set; }
    public ICollection<MigrationStudy>   Studies  { get; set; } = [];
    public ICollection<MigrationAuditLog> AuditLogs { get; set; } = [];
}

// ══════════════════════════════════════════════════════════════════════════════
// VENTANA DE EJECUCIÓN
// ══════════════════════════════════════════════════════════════════════════════

public class ExecutionWindow
{
    public int    Id          { get; set; }
    public int    MigrationId { get; set; }

    /// <summary>Comma-separated ISO day numbers: 1=Mon … 7=Sun. E.g. "1,2,3,4,5"</summary>
    public string EnabledDays   { get; set; } = "1,2,3,4,5";
    public TimeOnly StartTime   { get; set; } = new TimeOnly(23, 0);
    public TimeOnly EndTime     { get; set; } = new TimeOnly(6, 0);

    /// <summary>IANA timezone id, e.g. "Europe/Madrid"</summary>
    public string TimeZoneId    { get; set; } = "Europe/Madrid";

    /// <summary>True when StartTime > EndTime (crosses midnight)</summary>
    public bool   CrossesMidnight => StartTime > EndTime;

    public Migration? Migration  { get; set; }
}

// ══════════════════════════════════════════════════════════════════════════════
// ESTUDIO DE MIGRACIÓN  (tabla principal de estudios)
// ══════════════════════════════════════════════════════════════════════════════

public class MigrationStudy
{
    public long     Id               { get; set; }
    public int      MigrationId      { get; set; }

    // ── Identificadores DICOM ────────────────────────────────────────────────
    public string   StudyInstanceUid   { get; set; } = string.Empty;
    public string?  PatientId          { get; set; }
    public string?  IssuerOfPatientId  { get; set; }
    public string?  AccessionNumber    { get; set; }
    public string?  StudyDate          { get; set; }
    public string?  ModalitiesInStudy  { get; set; }

    // ── Contadores origen ────────────────────────────────────────────────────
    public int?     SourceSeriesCount    { get; set; }
    public int?     SourceInstanceCount  { get; set; }

    // ── Contadores destino (post-migración) ──────────────────────────────────
    public int?     TargetSeriesCount    { get; set; }
    public int?     TargetInstanceCount  { get; set; }

    /// <summary>
    /// Pending | Queued | Migrating | Migrated |
    /// VerificationPending | Verified | Failed | RetryPending | Cancelled
    /// </summary>
    public string   MigrationStatus    { get; set; } = "Pending";

    public int      RetryCount         { get; set; } = 0;
    public string?  LastError          { get; set; }

    /// <summary>Worker thread id holding this study for MIGRATION (optimistic lock)</summary>
    public string?  LockedByWorker     { get; set; }
    public DateTime? LockDate          { get; set; }

    /// <summary>Worker thread id holding this study for VERIFICATION (separate lock,
    /// so verification and migration can run concurrently without colliding).</summary>
    public string?  VerifyLockedByWorker { get; set; }
    public DateTime? VerifyLockDate       { get; set; }
    /// <summary>Verification retry counter, independent of migration RetryCount.</summary>
    public int      VerifyRetryCount     { get; set; } = 0;

    /// <summary>Verificación Nivel 2: nº de UIDs faltantes y sobrantes en destino, y la
    /// lista de SOPInstanceUID faltantes (informe; puede venir truncada si son muchos).</summary>
    public int      VerifyMissingCount   { get; set; } = 0;
    public int      VerifyExtraCount     { get; set; } = 0;
    public string?  VerifyMissingUids    { get; set; }

    /// <summary>Qué comprobación se aplicó REALMENTE al verificar este estudio:
    /// <c>UidSet</c> (comparación de conjuntos de SOPInstanceUID · la más fuerte),
    /// <c>Counts</c> (se comparó al menos un conteo de origen contra el destino),
    /// <c>ExistenceOnly</c> (no se conocía ningún conteo de origen: solo se confirmó
    /// que el estudio existe en destino).
    /// Sin esto, "Verified" significa dos cosas distintas y la auditoría no puede
    /// distinguirlas. No es un estado: convive con MigrationStatus sin alterarlo.</summary>
    public string?  VerifiedBy           { get; set; }

    public DateTime  DiscoveryDate     { get; set; } = DateTime.UtcNow;
    public DateTime? MigrationDate       { get; set; }
    public DateTime? MigrationStartDate  { get; set; }  // cuando el worker adquirió el estudio
    public DateTime? VerificationDate    { get; set; }
    public DateTime? VerificationStartDate { get; set; }  // cuando empezó la verificación
    public DateTime  LastUpdateDate    { get; set; } = DateTime.UtcNow;

    // ── Navigation ───────────────────────────────────────────────────────────
    public Migration? Migration { get; set; }

    /// <summary>Instancias (UIDs) de ORIGEN capturadas para verificación Nivel 2.</summary>
    public ICollection<MigrationInstance> Instances { get; set; } = [];
}

// ══════════════════════════════════════════════════════════════════════════════
// INSTANCIA DE ESTUDIO  (Nivel 2 de verificación — conjunto de UIDs de ORIGEN)
// ══════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Una instancia (imagen) del ORIGEN, capturada en el descubrimiento cuando la
/// migración usa verificación Nivel 2. Permite comparar conjuntos de UIDs
/// origen↔destino (no solo cardinales) al verificar, y saber exactamente qué
/// SOPInstanceUID falta en destino.
/// </summary>
public class MigrationInstance
{
    public long     Id                { get; set; }
    public long     MigrationStudyId  { get; set; }

    public string   SeriesInstanceUid { get; set; } = string.Empty;
    public string   SopInstanceUid    { get; set; } = string.Empty;

    // ── Navigation ───────────────────────────────────────────────────────────
    public MigrationStudy? Study      { get; set; }
}

// ══════════════════════════════════════════════════════════════════════════════
// AUDIT LOG  (extiende TestExecutionLog del Tester con campos de migración)
// ══════════════════════════════════════════════════════════════════════════════

public class MigrationAuditLog
{
    public long     Id            { get; set; }
    public int      MigrationId   { get; set; }

    /// <summary>INFO | DEBUG | WARN | ERROR</summary>
    public string   Level         { get; set; } = "INFO";

    /// <summary>
    /// C-ECHO | C-FIND | QIDO-RS | C-MOVE | C-GET | WADO-RS | STOW-RS |
    /// VERIFY | START | PAUSE | RESUME | CANCEL | WINDOW_OPEN | WINDOW_CLOSE |
    /// DISCOVERY | IMPORT_CSV | RETRY | STATUS_CHANGE
    /// </summary>
    public string   Action        { get; set; } = string.Empty;

    /// <summary>OK | ERROR | WARN</summary>
    public string   Result        { get; set; } = "OK";

    public string?  StudyInstanceUid { get; set; }
    public string?  UserOrProcess    { get; set; }
    public string?  TechnicalMessage { get; set; }
    public DateTime Timestamp        { get; set; } = DateTime.UtcNow;

    public Migration? Migration { get; set; }
}

// ══════════════════════════════════════════════════════════════════════════════
// CONFIGURACIÓN SCU LOCAL  (idéntica a la del Tester)
// ══════════════════════════════════════════════════════════════════════════════

public class LocalConfiguration
{
    public int      Id            { get; set; }
    public string   LocalAet      { get; set; } = "MIGRATOR_SCU";
    public int      LocalPort     { get; set; } = 11113;
    public string   LocalHostname { get; set; } = string.Empty;
    public string   Description   { get; set; } = string.Empty;

    /// <summary>Global max simultaneous migrations running at the same time</summary>
    public int      MaxConcurrentMigrations { get; set; } = 3;

    public DateTime UpdatedAt     { get; set; } = DateTime.UtcNow;
}
