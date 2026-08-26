using DicomMigrator.Core.Interfaces;
using DicomMigrator.Core.Models;
using DicomMigrator.Infrastructure.Services.Licensing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

// Alias explícito para evitar ambigüedad entre el tipo Migration y el namespace
// DicomMigrator.Infrastructure.Services.Migration en el que vive este fichero.
using MigrationEntity = DicomMigrator.Core.Models.Migration;

namespace DicomMigrator.Infrastructure.Services.Migration;

// ══════════════════════════════════════════════════════════════════════════════
// DISCOVERY SERVICE
// ══════════════════════════════════════════════════════════════════════════════

public class DiscoveryService(
    IMigrationRepository migrationRepo,
    IStudyRepository studyRepo,
    IAuditLogRepository auditRepo,
    IDimseService dimse,
    IDicomWebService dicomWeb,
    ILogger<DiscoveryService> logger) : IDiscoveryService
{
    public async Task<CsvImportResult> DiscoverViaCFindAsync(
        int migrationId, CFindQuery query, CancellationToken ct = default)
    {
        var result = new CsvImportResult();
        var migration = await migrationRepo.GetByIdAsync(migrationId)
            ?? throw new InvalidOperationException($"Migración {migrationId} no encontrada");

        // Split modalities — DICOM C-FIND only supports one modality per request
        // "CT,MR,MG" → three separate C-FIND calls merged into one result set
        var modalities = string.IsNullOrWhiteSpace(query.ModalitiesInStudy)
            ? new[] { (string?)null }   // no filter → one call without modality constraint
            : query.ModalitiesInStudy
                .Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(m => (string?)m.Trim())
                .Where(m => !string.IsNullOrEmpty(m))
                .ToArray();

        var filtersDesc = string.Join(" | ",
            new[]
            {
                string.IsNullOrWhiteSpace(query.StudyDate)       ? null : $"Fecha={query.StudyDate}",
                string.IsNullOrWhiteSpace(query.ModalitiesInStudy) ? null : $"Modalidad={query.ModalitiesInStudy}",
                string.IsNullOrWhiteSpace(query.PatientId)       ? null : $"PatientID={query.PatientId}",
                string.IsNullOrWhiteSpace(query.AccessionNumber) ? null : $"AccessionNumber={query.AccessionNumber}",
            }.Where(f => f is not null));

        logger.LogInformation("C-FIND discovery for migration {Id}. Filtros: {Filters}. Modalidades: {Count} petición(es)",
            migrationId, filtersDesc, modalities.Length);

        await auditRepo.AddAsync(new MigrationAuditLog
        {
            MigrationId   = migrationId,
            Action        = "DISCOVERY",
            Level         = "INFO",
            UserOrProcess = "DISCOVERY",
            TechnicalMessage = $"Iniciando C-FIND discovery. Filtros: {(string.IsNullOrWhiteSpace(filtersDesc) ? "ninguno (todos los estudios)" : filtersDesc)}. {modalities.Length} petición(es) C-FIND.",
        });

        var allStudies = new List<MigrationStudy>();

        foreach (var modality in modalities)
        {
            if (ct.IsCancellationRequested) break;

            // CFindQuery is a class, not a record — clone manually
            var singleQuery = new CFindQuery
            {
                Level             = query.Level,
                PatientId         = query.PatientId,
                PatientName       = query.PatientName,
                StudyDate         = query.StudyDate,
                AccessionNumber   = query.AccessionNumber,
                StudyInstanceUid  = query.StudyInstanceUid,
                SeriesInstanceUid = query.SeriesInstanceUid,
                SopInstanceUid    = query.SopInstanceUid,
                Modality          = query.Modality,
                ModalitiesInStudy = modality,   // one modality per request
                MaxResults        = query.MaxResults,
            };

            try
            {
                var findResult = await dimse.FindAsync(migration.OriginNode!, singleQuery, ct);

                if (!findResult.Success)
                {
                    var errMsg = findResult.ErrorMessage ?? "C-FIND falló";
                    result.Errors.Add(modality is null ? errMsg : $"[{modality}] {errMsg}");
                    logger.LogWarning("C-FIND error for modality {Mod}: {Err}", modality ?? "*", errMsg);
                    continue;
                }

                var studies = findResult.Studies
                    .Where(s => !string.IsNullOrEmpty(s.StudyInstanceUid))
                    .Select(s => new MigrationStudy
                    {
                        StudyInstanceUid    = s.StudyInstanceUid!,
                        PatientId           = s.PatientId,
                        AccessionNumber     = s.AccessionNumber,
                        StudyDate           = s.StudyDate,
                        ModalitiesInStudy   = s.ModalitiesInStudy,
                        SourceSeriesCount   = s.NumberOfSeries,
                        SourceInstanceCount = s.NumberOfInstances,
                        MigrationStatus     = "Pending",
                    }).ToList();

                logger.LogInformation("C-FIND [{Mod}] → {Count} estudios encontrados",
                    modality ?? "*", studies.Count);

                allStudies.AddRange(studies);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "C-FIND error for modality {Mod}", modality ?? "*");
                result.Errors.Add($"[{modality ?? "*"}] {ex.Message}");
            }
        }

        // Deduplicate by StudyInstanceUID (different modality calls may return overlapping results)
        var unique = allStudies
            .GroupBy(s => s.StudyInstanceUid)
            .Select(g => g.First())
            .ToList();

        result.TotalRows    = allStudies.Count;
        result.ValidRows    = unique.Count;
        result.DuplicateRows = allStudies.Count - unique.Count;

        if (unique.Count > 0)
        {
            result.ImportedRows  = await studyRepo.BulkInsertAsync(migrationId, unique);
            result.DuplicateRows += result.ValidRows - result.ImportedRows; // also count DB duplicates
        }

        await auditRepo.AddAsync(new MigrationAuditLog
        {
            MigrationId   = migrationId,
            Action        = "DISCOVERY",
            Level         = result.Errors.Count > 0 ? "WARN" : "INFO",
            Result        = result.Errors.Count > 0 && result.ImportedRows == 0 ? "ERROR" : "OK",
            UserOrProcess = "DISCOVERY",
            TechnicalMessage = $"C-FIND discovery completado. " +
                               $"Encontrados={result.TotalRows} Únicos={result.ValidRows} " +
                               $"Importados={result.ImportedRows} Duplicados={result.DuplicateRows}" +
                               (result.Errors.Count > 0 ? $" Errores={result.Errors.Count}" : ""),
        });

        return result;
    }

    public async Task<CsvImportResult> DiscoverViaQidoAsync(
        int migrationId, QidoQuery query, CancellationToken ct = default)
    {
        var result = new CsvImportResult();
        var migration = await migrationRepo.GetByIdAsync(migrationId)
            ?? throw new InvalidOperationException($"Migración {migrationId} no encontrada");

        logger.LogInformation("QIDO-RS discovery for migration {Id}", migrationId);

        try
        {
            // Paginate until no more results
            var allStudies = new List<MigrationStudy>();
            var offset = 0;
            const int pageSize = 200;

            while (true)
            {
                query.Offset = offset;
                query.Limit  = pageSize;
                var qidoResult = await dicomWeb.QidoAsync(migration.OriginNode!, query, ct);

                if (!qidoResult.Success || qidoResult.Studies.Count == 0) break;

                allStudies.AddRange(qidoResult.Studies.Select(s => new MigrationStudy
                {
                    StudyInstanceUid    = s.StudyInstanceUid ?? string.Empty,
                    PatientId           = s.PatientId,
                    AccessionNumber     = s.AccessionNumber,
                    StudyDate           = s.StudyDate,
                    ModalitiesInStudy   = s.ModalitiesInStudy,
                    SourceSeriesCount   = s.NumberOfSeries,
                    SourceInstanceCount = s.NumberOfInstances,
                    MigrationStatus     = "Pending",
                }).Where(s => !string.IsNullOrEmpty(s.StudyInstanceUid)));

                if (qidoResult.Studies.Count < pageSize) break;
                offset += pageSize;
            }

            result.TotalRows   = allStudies.Count;
            result.ValidRows   = allStudies.Count;
            result.ImportedRows = await studyRepo.BulkInsertAsync(migrationId, allStudies);
            result.DuplicateRows = result.ValidRows - result.ImportedRows;

            await auditRepo.AddAsync(new MigrationAuditLog
            {
                MigrationId = migrationId, Action = "DISCOVERY", Level = "INFO", Result = "OK",
                TechnicalMessage = $"QIDO-RS discovery OK. Encontrados={result.TotalRows} Importados={result.ImportedRows}"
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Discovery QIDO-RS error for migration {Id}", migrationId);
            result.Errors.Add(ex.Message);
        }
        return result;
    }

    public async Task<CsvImportResult> ImportFromCsvAsync(
        int migrationId, Stream csv, CancellationToken ct = default)
    {
        var result = new CsvImportResult();
        var studies = new List<MigrationStudy>();

        try
        {
            using var reader = new System.IO.StreamReader(csv);
            var header = await reader.ReadLineAsync(ct);
            if (header is null) { result.Errors.Add("CSV vacío"); return result; }

            // Parse header columns
            var cols = header.Split(',').Select(c => c.Trim('"', ' ').ToLowerInvariant()).ToArray();
            int uidCol = Array.IndexOf(cols, "studyinstanceuid");
            int pidCol = Array.IndexOf(cols, "patientid");
            int dateCol = Array.IndexOf(cols, "studydate");
            // Conteos de origen: si el CSV los trae, la verificación podrá comparar de
            // verdad en vez de limitarse a comprobar que el estudio existe en destino.
            // Se aceptan los nombres del propio export y los del estándar DICOM.
            int seriesCol = FirstIndexOf(cols, "series", "numberofstudyrelatedseries", "srcseries");
            int instCol   = FirstIndexOf(cols, "instances", "numberofstudyrelatedinstances", "srcinstances");

            if (uidCol < 0) { result.Errors.Add("Columna StudyInstanceUID requerida"); return result; }

            string? line;
            int row = 0;
            var seenUids = new HashSet<string>();

            while ((line = await reader.ReadLineAsync(ct)) is not null)
            {
                row++;
                result.TotalRows++;
                if (string.IsNullOrWhiteSpace(line)) continue;

                var parts = ParseCsvLine(line);
                if (parts.Length <= uidCol) { result.InvalidRows++; result.Errors.Add($"Línea {row}: columnas insuficientes"); continue; }

                var uid = parts[uidCol].Trim('"', ' ');
                if (string.IsNullOrWhiteSpace(uid)) { result.InvalidRows++; result.Errors.Add($"Línea {row}: StudyInstanceUID vacío"); continue; }
                if (seenUids.Contains(uid)) { result.DuplicateRows++; result.Warnings.Add($"Línea {row}: UID duplicado en CSV"); continue; }

                seenUids.Add(uid);
                result.ValidRows++;

                studies.Add(new MigrationStudy
                {
                    StudyInstanceUid = uid,
                    PatientId  = pidCol  >= 0 && parts.Length > pidCol  ? parts[pidCol].Trim('"', ' ')  : null,
                    StudyDate  = dateCol >= 0 && parts.Length > dateCol ? parts[dateCol].Trim('"', ' ') : null,
                    SourceSeriesCount   = ParseCount(parts, seriesCol),
                    SourceInstanceCount = ParseCount(parts, instCol),
                    MigrationStatus = "Pending",
                });
            }

            result.ImportedRows = await studyRepo.BulkInsertAsync(migrationId, studies);
            result.DuplicateRows += result.ValidRows - result.ImportedRows; // DB duplicates

            await auditRepo.AddAsync(new MigrationAuditLog
            {
                MigrationId = migrationId, Action = "IMPORT_CSV", Level = "INFO", Result = "OK",
                TechnicalMessage = $"CSV import OK. Total={result.TotalRows} Válidos={result.ValidRows} Importados={result.ImportedRows} Inválidos={result.InvalidRows} Duplicados={result.DuplicateRows}"
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "CSV import error for migration {Id}", migrationId);
            result.Errors.Add(ex.Message);
        }
        return result;
    }

    /// <summary>Primer índice que coincida con alguno de los nombres de columna dados
    /// (el CSV puede venir del propio export o de una herramienta externa).</summary>
    private static int FirstIndexOf(string[] cols, params string[] names)
    {
        foreach (var n in names)
        {
            var i = Array.IndexOf(cols, n);
            if (i >= 0) return i;
        }
        return -1;
    }

    /// <summary>Lee un conteo entero de la columna indicada. Devuelve null si la columna
    /// no existe, está fuera de rango o no es un número: null significa "desconocido",
    /// que es justo lo que la verificación necesita distinguir de un cero real.</summary>
    private static int? ParseCount(string[] parts, int col)
    {
        if (col < 0 || parts.Length <= col) return null;
        var raw = parts[col].Trim('"', ' ');
        return int.TryParse(raw, out var v) && v >= 0 ? v : null;
    }

    private static string[] ParseCsvLine(string line)
    {
        // Simple CSV parser — handles quoted fields with commas
        var parts = new List<string>();
        bool inQuote = false;
        var current = new System.Text.StringBuilder();
        foreach (var c in line)
        {
            if (c == '"') { inQuote = !inQuote; continue; }
            if (c == ',' && !inQuote) { parts.Add(current.ToString()); current.Clear(); continue; }
            current.Append(c);
        }
        parts.Add(current.ToString());
        return parts.ToArray();
    }
}
