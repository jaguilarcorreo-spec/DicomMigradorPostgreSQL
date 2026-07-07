namespace DicomMigrator.Core.Models;

// ═══════════════════════════════════════════════════════════════════════════
// RF-020 — Discovery Engine
// Capa independiente de descubrimiento de estudios DICOM en PACS origen.
// El inventario generado es reutilizable por múltiples migraciones.
// ═══════════════════════════════════════════════════════════════════════════

/// <summary>
/// Un Discovery Job representa una ejecución de descubrimiento sobre un PACS origen.
/// Puede ser por CSV, temporal (por particiones de fecha) o incremental.
/// </summary>
public class DiscoveryJob
{
    public int      Id           { get; set; }
    public string   Name         { get; set; } = string.Empty;
    public string?  Description  { get; set; }

    /// <summary>Nodo PACS origen sobre el que se descubre.</summary>
    public int      SourcePacsId { get; set; }
    public DicomNode? SourcePacs { get; set; }

    /// <summary>CSV | Temporal | Incremental</summary>
    public string   DiscoveryType { get; set; } = "Temporal";

    /// <summary>Método de consulta al PACS: CFIND | QIDO. (No aplica a CSV.)</summary>
    public string   QueryMethod   { get; set; } = "CFIND";

    /// <summary>Rango temporal global del descubrimiento (modo Temporal).</summary>
    public DateOnly? StartDate    { get; set; }
    public DateOnly? EndDate      { get; set; }

    /// <summary>Modalidades a incluir, separadas por coma. Vacío = todas.</summary>
    public string?  Modalities    { get; set; }

    /// <summary>Límite de resultados que el PACS devuelve por consulta (para detección de truncamiento).</summary>
    public int      PacsResultLimit { get; set; } = 500;

    /// <summary>Número máximo de workers concurrentes para este job.</summary>
    public int      WorkerThreads   { get; set; } = 4;

    /// <summary>Draft | Running | Paused | Completed | Cancelled | Failed</summary>
    public string   Status        { get; set; } = "Draft";

    /// <summary>Estado de la captura de UIDs de origen (verificación Nivel 2):
    /// Idle | Running | Paused | Completed | Failed.</summary>
    public string   CaptureStatus { get; set; } = "Idle";
    /// <summary>Inicio/fin del proceso de enumeración (Nivel 2), para medir el tiempo transcurrido.</summary>
    public DateTime? CaptureStartedDate  { get; set; }
    public DateTime? CaptureFinishedDate { get; set; }

    public DateTime CreatedDate   { get; set; } = DateTime.UtcNow;
    public DateTime? StartedDate  { get; set; }
    public DateTime? FinishedDate { get; set; }
    public string?  CreatedBy     { get; set; }

    public List<DiscoveryPartition> Partitions { get; set; } = [];
}

/// <summary>
/// Unidad mínima de trabajo del descubrimiento. Por defecto 1 día = 1 partición.
/// Puede subdividirse por modalidad o rango horario si se detecta truncamiento.
/// </summary>
public class DiscoveryPartition
{
    public int      Id             { get; set; }
    public int      DiscoveryJobId { get; set; }
    public DiscoveryJob? DiscoveryJob { get; set; }

    /// <summary>Day | DayModality | DayModalityTime</summary>
    public string   PartitionType  { get; set; } = "Day";

    public DateOnly? StartDate     { get; set; }
    public DateOnly? EndDate       { get; set; }
    public string?  Modality       { get; set; }
    public string?  StudyTimeFrom  { get; set; }  // HHmmss
    public string?  StudyTimeTo    { get; set; }  // HHmmss

    /// <summary>Pending | Running | Completed | PossiblyTruncated | Subdivided | Failed</summary>
    public string   Status         { get; set; } = "Pending";

    public int      AttemptCount   { get; set; }
    public int      StudiesFound   { get; set; }
    public int      StudiesInserted { get; set; }
    public int      StudiesUpdated { get; set; }

    public DateTime? StartedAt     { get; set; }
    public DateTime? FinishedAt    { get; set; }
    public double?  DurationMs     { get; set; }
    public string?  LastError      { get; set; }

    /// <summary>Worker que la tiene bloqueada (para procesamiento concurrente).</summary>
    public string?  LockedByWorker { get; set; }
    public DateTime? LockDate      { get; set; }
}

/// <summary>
/// Inventario persistente de estudios descubiertos. Clave única: StudyInstanceUID.
/// Independiente de MigrationStudies — reutilizable por múltiples migraciones.
/// </summary>
public class DiscoveredStudy
{
    public long     Id               { get; set; }

    public string   StudyInstanceUid { get; set; } = string.Empty;
    public string?  PatientId        { get; set; }
    public string?  IssuerOfPatientId { get; set; }
    public string?  PatientName      { get; set; }
    public string?  PatientBirthDate { get; set; }
    public string?  PatientSex       { get; set; }
    public string?  AccessionNumber  { get; set; }
    public string?  StudyDate        { get; set; }
    public string?  StudyTime        { get; set; }
    public string?  StudyDescription { get; set; }
    public string?  ModalitiesInStudy { get; set; }
    public int?     NumberOfStudyRelatedSeries    { get; set; }
    public int?     NumberOfStudyRelatedInstances { get; set; }
    public string?  InstitutionName  { get; set; }
    public string?  RetrieveAETitle  { get; set; }

    public DateTime DiscoveryDate    { get; set; } = DateTime.UtcNow;
    public DateTime? LastUpdatedDate { get; set; }

    /// <summary>Nodo PACS donde se descubrió.</summary>
    public int      SourcePacsId     { get; set; }

    /// <summary>Job de descubrimiento que lo encontró por primera vez.</summary>
    public int?     DiscoveryJobId   { get; set; }

    /// <summary>Partición que descubrió el estudio (para agregados por partición).</summary>
    public int?     PartitionId      { get; set; }

    /// <summary>Instancias (UIDs) de origen capturadas para verificación Nivel 2.</summary>
    public ICollection<DiscoveredInstance> Instances { get; set; } = [];
}

/// <summary>
/// Una instancia (imagen) de un estudio descubierto en el ORIGEN, capturada por
/// C-FIND IMAGE para la verificación Nivel 2. Al crear una migración desde el
/// inventario, estos UIDs se copian a MigrationInstance.
/// </summary>
public class DiscoveredInstance
{
    public long     Id                { get; set; }
    public long     DiscoveredStudyId { get; set; }

    public string   SeriesInstanceUid { get; set; } = string.Empty;
    public string   SopInstanceUid    { get; set; } = string.Empty;

    public DiscoveredStudy? Study      { get; set; }
}

/// <summary>
/// Log detallado de cada petición individual realizada al PACS durante el descubrimiento.
/// Permite auditar rendimiento y diagnosticar problemas.
/// </summary>
public class DiscoveryRequest
{
    public long     Id             { get; set; }
    public int      DiscoveryJobId { get; set; }
    public int?     PartitionId    { get; set; }

    public DateTime RequestDate    { get; set; } = DateTime.UtcNow;
    public int      SourcePacsId   { get; set; }

    /// <summary>CFIND | QIDO | CSV</summary>
    public string   QueryType      { get; set; } = string.Empty;

    /// <summary>Descripción legible de los filtros usados.</summary>
    public string?  Filters        { get; set; }

    public double   DurationMs     { get; set; }

    /// <summary>OK | ERROR | TIMEOUT | TRUNCATED</summary>
    public string   Result         { get; set; } = string.Empty;

    public int      StudiesReturned { get; set; }
    public int      Attempt        { get; set; } = 1;
    public string?  Error          { get; set; }
}
