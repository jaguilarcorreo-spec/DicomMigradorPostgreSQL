namespace DicomMigrator.Core.Models;

// ══════════════════════════════════════════════════════════════════════════════
// REUTILIZADOS DEL DICOMPACSTESTER (sin cambios)
// ══════════════════════════════════════════════════════════════════════════════

/// <summary>Resultado de C-ECHO. Idéntico al del Tester.</summary>
public class EchoResult
{
    public bool    Success      { get; set; }
    public int?    DicomStatus  { get; set; }
    public long    DurationMs   { get; set; }
    public string? ErrorMessage { get; set; }
    public List<string> Logs    { get; set; } = [];
}

/// <summary>Health of a single DICOM node as last probed by C-ECHO.</summary>
public class NodeHealth
{
    public string   Alias      { get; set; } = "";
    public bool     Reachable  { get; set; }
    public long     DurationMs { get; set; }
    public string?  Error      { get; set; }
    public DateTime CheckedAt  { get; set; }
}

/// <summary>Combined origin + destination health for a migration.</summary>
public class ConnectionHealth
{
    public int        MigrationId { get; set; }
    public NodeHealth? Origin     { get; set; }
    public NodeHealth? Destination{ get; set; }
    public DateTime    CheckedAt  { get; set; }
}

/// <summary>Parámetros de C-FIND. Idéntico al del Tester.</summary>
public class CFindQuery
{
    public string  Level              { get; set; } = "STUDY";
    public string? PatientId          { get; set; }
    public string? PatientName        { get; set; }
    public string? StudyDate          { get; set; }
    public string? AccessionNumber    { get; set; }
    public string? StudyInstanceUid   { get; set; }
    public string? SeriesInstanceUid  { get; set; }
    public string? SopInstanceUid     { get; set; }
    public string? Modality           { get; set; }
    public string? ModalitiesInStudy  { get; set; }
    public int?    MaxResults         { get; set; }
}

/// <summary>Resultado de C-FIND. Idéntico al del Tester.</summary>
public class CFindResult
{
    public bool    Success      { get; set; }
    public int?    DicomStatus  { get; set; }
    public long    DurationMs   { get; set; }
    public List<DicomStudyDto> Studies { get; set; } = [];
    public string? ErrorMessage { get; set; }
    public List<string> Logs    { get; set; } = [];
}

/// <summary>Referencia a una instancia DICOM (Nivel 2 de verificación).</summary>
public class DicomInstanceRef
{
    public string SeriesInstanceUid { get; set; } = string.Empty;
    public string SopInstanceUid    { get; set; } = string.Empty;
}

/// <summary>Resultado de enumerar las instancias (SOP UIDs) de un estudio
/// mediante C-FIND a nivel IMAGE. Base del Nivel 2 de verificación.</summary>
public class CFindInstancesResult
{
    public bool    Success      { get; set; }
    public int?    DicomStatus  { get; set; }
    public long    DurationMs   { get; set; }
    public string? ErrorMessage { get; set; }
    public List<DicomInstanceRef> Instances { get; set; } = [];
}

/// <summary>Resumen de una captura de UIDs de origen (Nivel 2, Fase 1b).</summary>
public class InstanceCaptureResult
{
    public int StudiesCaptured   { get; set; }
    public int StudiesSkipped    { get; set; }
    public int StudiesFailed     { get; set; }
    public int InstancesCaptured { get; set; }
    public string? ErrorMessage  { get; set; }
    public List<string> Failures { get; set; } = [];
}

/// <summary>DTO de estudio DICOM. Idéntico al del Tester.</summary>
public class DicomStudyDto
{
    public string? PatientId         { get; set; }
    public string? PatientName       { get; set; }
    public string? StudyDate         { get; set; }
    public string? AccessionNumber   { get; set; }
    public string? StudyInstanceUid  { get; set; }
    public string? ModalitiesInStudy { get; set; }
    public string? StudyDescription  { get; set; }
    public string? SeriesInstanceUid { get; set; }
    public string? Modality          { get; set; }
    public string? SopInstanceUid    { get; set; }
    public int?    NumberOfInstances { get; set; }
    public int?    NumberOfSeries    { get; set; }
}

/// <summary>Parámetros de QIDO-RS. Idéntico al del Tester con Offset añadido.</summary>
public class QidoQuery
{
    public string  Level             { get; set; } = "studies";
    public string? StudyInstanceUid  { get; set; }
    public string? SeriesInstanceUid { get; set; }
    public string? PatientId         { get; set; }
    public string? PatientName       { get; set; }
    public string? StudyDate         { get; set; }
    public string? AccessionNumber   { get; set; }
    public string? Modality          { get; set; }
    public string? ModalitiesInStudy { get; set; }
    public int     Limit             { get; set; } = 100;
    public int     Offset            { get; set; } = 0;
    public string? IncludeField      { get; set; }
}

/// <summary>Resultado de QIDO-RS. Idéntico al del Tester.</summary>
public class QidoResult
{
    public bool    Success      { get; set; }
    public int     HttpStatus   { get; set; }
    public long    DurationMs   { get; set; }
    public int     ResultCount  { get; set; }
    public string? RawJson      { get; set; }
    public List<DicomStudyDto>          Studies         { get; set; } = [];
    public Dictionary<string, string>   ResponseHeaders { get; set; } = [];
    public string? ErrorMessage { get; set; }
    public string  RequestUrl   { get; set; } = string.Empty;
}

/// <summary>Petición de C-MOVE. Idéntico al del Tester.</summary>
public class CMoveRequest
{
    public string  Level            { get; set; } = "STUDY";
    public string  StudyInstanceUid { get; set; } = string.Empty;
    public string? SeriesInstanceUid { get; set; }
    public string? SopInstanceUid   { get; set; }
    public string  DestinationAet   { get; set; } = string.Empty;
}

/// <summary>Resultado de C-MOVE. Idéntico al del Tester.</summary>
public class CMoveResult
{
    public bool    Success           { get; set; }
    public int?    DicomStatus       { get; set; }
    public long    DurationMs        { get; set; }
    public int     Completed         { get; set; }
    public int     Failed            { get; set; }
    public int     Warning           { get; set; }
    public int     Remaining         { get; set; }
    public int     ReceivedCount     { get; set; }   // instancias realmente llegadas al SCP
    public string  DownloadDirectory { get; set; } = string.Empty;
    public string? ErrorMessage      { get; set; }
    /// <summary>True if the C-MOVE failed because the SOURCE PACS could not be reached
    /// (association rejected, connection lost, timeout) rather than because the study
    /// is genuinely missing. Such studies should be retried as a transient error, not
    /// counted as a permanent migration failure.</summary>
    public bool    ConnectionError   { get; set; }
    public List<string> Logs         { get; set; } = [];
}

/// <summary>Resultado de WADO-RS + STOW-RS (transferencia DICOMweb).</summary>
public class WadoStowResult
{
    public bool    Success       { get; set; }
    public int     HttpStatus    { get; set; }
    public long    DurationMs    { get; set; }
    public long    TotalBytes    { get; set; }
    public int     ObjectCount   { get; set; }
    public string? ErrorMessage  { get; set; }
    public List<string> Logs     { get; set; } = [];
}

/// <summary>Progreso de descarga WADO-RS. Idéntico al del Tester.</summary>
public class WadoProgress
{
    public string Phase          { get; set; } = "";
    public int    ObjectsWritten { get; set; }
    public long   BytesReceived  { get; set; }
    public long   TotalBytes     { get; set; }
    public int    Percent        => TotalBytes > 0 ? (int)(BytesReceived * 100 / TotalBytes) : -1;
}

// ══════════════════════════════════════════════════════════════════════════════
// NUEVOS DTOs ESPECÍFICOS DE MIGRACIÓN
// ══════════════════════════════════════════════════════════════════════════════

/// <summary>Resultado de un C-FIND o QIDO-RS usado para verificar un estudio en destino.</summary>
public class VerificationResult
{
    public bool    StudyFoundInDest      { get; set; }
    public bool    SeriesCountMatch      { get; set; }
    public bool    InstanceCountMatch    { get; set; }
    public int?    DestSeriesCount       { get; set; }
    public int?    DestInstanceCount     { get; set; }
    public long    DurationMs            { get; set; }
    public string? ErrorMessage          { get; set; }
    /// <summary>True if the verification query itself could not complete (PACS down,
    /// timeout, network/transport error) — as opposed to the study being genuinely
    /// absent or mismatched. In this case the study should NOT be marked VerifyFailed;
    /// it stays verifiable and is retried (the destination couldn't be reached).</summary>
    public bool    ConnectionError       { get; set; }
    public List<string> Logs            { get; set; } = [];
}

/// <summary>Resultado de importación de CSV de estudios.</summary>
public class CsvImportResult
{
    public int     TotalRows       { get; set; }
    public int     ValidRows       { get; set; }
    public int     InvalidRows     { get; set; }
    public int     DuplicateRows   { get; set; }
    public int     ImportedRows    { get; set; }
    public List<string> Errors     { get; set; } = [];
    public List<string> Warnings   { get; set; } = [];
}

/// <summary>Estadísticas resumen de una migración para el dashboard.</summary>
public class MigrationStats
{
    public int  MigrationId          { get; set; }
    public long Total                { get; set; }
    public long Pending              { get; set; }
    public long Queued               { get; set; }
    public long Migrating            { get; set; }
    public long Migrated             { get; set; }
    public long VerificationPending  { get; set; }
    public long Verified             { get; set; }
    public long Failed               { get; set; }
    public long RetryPending         { get; set; }
    public long VerifyFailed         { get; set; }
    public long VerifyRetryPending   { get; set; }
    public long Cancelled            { get; set; }

    /// <summary>Sum of individual study transfer times (accumulated CPU time), in seconds.</summary>
    public double TotalMigrationSeconds     { get; set; }

    /// <summary>Sum of individual study verification times (accumulated CPU time), in seconds.</summary>
    public double TotalVerificationSeconds  { get; set; }

    /// <summary>Wall-clock time: first MigrationStartDate to last MigrationDate, in seconds.</summary>
    public double WallMigrationSeconds      { get; set; }

    /// <summary>Wall-clock time: first VerificationStartDate to last VerificationDate, in seconds.</summary>
    public double WallVerificationSeconds   { get; set; }

    public double ProgressPercent =>
        Total > 0 ? Math.Round((Migrated + Verified + VerificationPending) * 100.0 / Total, 1) : 0;

    public double VerificationPercent =>
        Total > 0 ? Math.Round(Verified * 100.0 / Total, 1) : 0;

    public double MigrationOnlyPercent =>
        Total > 0 ? Math.Round((Migrated + VerificationPending) * 100.0 / Total, 1) : 0;

    public double? EstimatedDaysRemaining { get; set; }
    public double? AvgStudiesPerDay       { get; set; }
}

/// <summary>Filtro avanzado para consulta de estudios de una migración.</summary>
public class StudyFilter
{
    public string? StudyInstanceUid  { get; set; }
    public string? PatientId         { get; set; }
    public string? AccessionNumber   { get; set; }
    public string? StudyDateFrom     { get; set; }
    public string? StudyDateTo       { get; set; }
    public string? Modality          { get; set; }
    public string? Status            { get; set; }
    public bool?   HasError          { get; set; }
    public int?    MinRetries        { get; set; }
    public int?    MaxRetries        { get; set; }
    public int     PageSize          { get; set; } = 50;
    public int     PageNumber        { get; set; } = 1;

    // ── Paginación keyset (cursor) ──────────────────────────────────────────
    // Para grandes volúmenes, en vez de OFFSET (que recorre y descarta las filas
    // anteriores), se pagina con un cursor: "dame las siguientes N filas después
    // de esta". El cursor es el par (DiscoveryDate, Id) de la última fila vista.
    // Null = primera página.
    public DateTime? CursorDiscoveryDate { get; set; }
    public long?     CursorId            { get; set; }
}

// ── Discovery Engine (RF-020) ───────────────────────────────────────────────
public class DiscoveryStats
{
    public int    JobId                { get; set; }
    public int    TotalPartitions      { get; set; }
    public int    CompletedPartitions  { get; set; }
    public int    SubdividedPartitions { get; set; }
    public int    PendingPartitions    { get; set; }
    public int    RunningPartitions    { get; set; }
    public int    FailedPartitions     { get; set; }
    public int    TruncatedPartitions  { get; set; }

    public int    TotalRequests        { get; set; }
    public int    OkRequests           { get; set; }
    public int    FailedRequests       { get; set; }

    public int    StudiesDiscovered    { get; set; }
    public int    StudiesNew           { get; set; }
    public int    StudiesExisting      { get; set; }

    public double ElapsedSeconds       { get; set; }
    public double? EstimatedRemainingSeconds { get; set; }

    /// <summary>
    /// Partitions that have been fully processed — no more work pending on them.
    /// Includes: Completed, Subdivided (work delegated to children), Failed, PossiblyTruncated.
    /// Excludes: Pending, Running.
    /// </summary>
    public int ProcessedPartitions =>
        CompletedPartitions + SubdividedPartitions + FailedPartitions + TruncatedPartitions;

    public double ProgressPercent =>
        TotalPartitions > 0
            ? Math.Round(ProcessedPartitions * 100.0 / TotalPartitions, 1)
            : 0;
}

public class DiscoveredStudyFilter
{
    public int?    SourcePacsId   { get; set; }
    public int?    DiscoveryJobId { get; set; }
    public string? StudyInstanceUid { get; set; }
    public string? PatientId      { get; set; }
    public string? AccessionNumber { get; set; }
    public string? StudyDateFrom  { get; set; }
    public string? StudyDateTo    { get; set; }
    public string? Modality       { get; set; }
    public int     PageSize       { get; set; } = 50;
    public int     PageNumber     { get; set; } = 1;
}

public class CsvImportStats
{
    public int     TotalLines  { get; set; }
    public int     Inserted    { get; set; }
    public int     Updated     { get; set; }
    public int     Skipped     { get; set; }   // blank or duplicate UID in same file
    public int     Errors      { get; set; }   // lines with missing/invalid UID
    public List<string> ErrorDetails { get; set; } = [];
}
