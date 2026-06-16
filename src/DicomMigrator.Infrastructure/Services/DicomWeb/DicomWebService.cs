// ═══════════════════════════════════════════════════════════════════════════
//  DicomWebService.cs
//  Adaptador delgado sobre DicomWebTestService del Tester (copiado en esta
//  misma carpeta con namespace cambiado).
// ═══════════════════════════════════════════════════════════════════════════
using DicomMigrator.Core.Interfaces;
using DicomMigrator.Core.Models;
using Microsoft.Extensions.Logging;

namespace DicomMigrator.Infrastructure.Services.DicomWeb;

public class DicomWebService(ILogger<DicomWebService> logger, IHttpClientFactory? httpFactory = null) : IDicomWebService
{
    private readonly DicomWebTestService _inner = new(
        Microsoft.Extensions.Logging.Abstractions.NullLogger<DicomWebTestService>.Instance,
        httpFactory);

    public async Task<QidoResult> QidoAsync(DicomNode node, QidoQuery query, CancellationToken ct = default)
    {
        var effectiveBase = !string.IsNullOrWhiteSpace(node.WebBaseUrl) ? node.WebBaseUrl : node.QidoBaseUrl;
        if (!node.HasDicomWeb || string.IsNullOrWhiteSpace(effectiveBase))
            return new QidoResult { Success = false, ErrorMessage = "El nodo no tiene DICOMweb / QIDO-RS configurado." };

        var config = ToDicomWebConfig(node);
        var testerQuery = new TesterQidoQuery
        {
            Level            = query.Level,
            StudyInstanceUid = query.StudyInstanceUid,
            PatientId        = query.PatientId,
            StudyDate        = query.StudyDate,
            AccessionNumber  = query.AccessionNumber,
            Modality         = query.Modality,
            ModalitiesInStudy = query.ModalitiesInStudy,
            Limit            = query.Limit,
            Offset           = query.Offset,
            IncludeField     = query.IncludeField,
        };
        var r = await _inner.QidoAsync(config, testerQuery, ct);
        // Log the REAL request URL (with /studies and query params), not the raw base
        logger.LogInformation("QIDO-RS {Level} → {Alias} · {Url} · HTTP {Status} · {Count} resultados",
            query.Level, node.Alias, r.RequestUrl, r.HttpStatus, r.ResultCount);
        return new QidoResult
        {
            Success      = r.Success,
            HttpStatus   = r.HttpStatus,
            DurationMs   = r.DurationMs,
            ResultCount  = r.ResultCount,
            RawJson      = r.RawJson,
            ErrorMessage = r.ErrorMessage,
            RequestUrl   = r.RequestUrl,
            ResponseHeaders = r.ResponseHeaders,
            Studies = r.Studies.Select(s => new DicomStudyDto
            {
                PatientId         = s.PatientId,
                PatientName       = s.PatientName,
                StudyDate         = s.StudyDate,
                AccessionNumber   = s.AccessionNumber,
                StudyInstanceUid  = s.StudyInstanceUid,
                ModalitiesInStudy = s.ModalitiesInStudy,
                NumberOfInstances = s.NumberOfInstances,
                NumberOfSeries    = s.NumberOfSeries,
            }).ToList(),
        };
    }

    public async Task<WadoStowResult> WadoStowAsync(DicomNode origin, DicomNode dest,
                                                     string studyUid, CancellationToken ct = default)
    {
        // TODO: implement full WADO-RS + STOW-RS pipeline
        // 1. WADO-RS retrieve from origin → temp dir
        // 2. STOW-RS push to dest
        logger.LogWarning("WADO-RS+STOW-RS not yet implemented for study {Uid}", studyUid);
        await Task.Delay(0, ct);
        return new WadoStowResult
        {
            Success = false,
            ErrorMessage = "WADO-RS+STOW-RS pipeline not yet implemented. Use C-MOVE instead.",
        };
    }

    // ── Adapter helpers ───────────────────────────────────────────────────────
    private static TesterDicomWebConfig ToDicomWebConfig(DicomNode n)
    {
        // Use the same field layout as DicomPacsTester:
        //   WebBaseUrl  = e.g. http://localhost:8042/dicom-web
        //   WebQidoPath = e.g. /studies   (or /qido-rs for Orthanc Ubuntu)
        // Final URL = BaseUrl.TrimEnd('/') + QidoPath + "/" + query.Level
        // e.g. http://localhost:8042/dicom-web/studies/studies  ← BuildQidoUrl adds /studies
        // So for Orthanc: BaseUrl=http://localhost:8042/dicom-web  QidoPath=/studies
        // BuildQidoUrl: http://localhost:8042/dicom-web + /studies + (level not added if already ends with it)

        var baseUrl   = n.WebBaseUrl  ?? n.QidoBaseUrl ?? "";
        var qidoPath  = n.WebQidoPath ?? "";
        var wadoPath  = n.WebWadoPath ?? qidoPath;
        var stowPath  = n.WebStowPath ?? qidoPath;

        // Basic auth: form stores "user:password" in EncryptedSecret
        string? username = n.AuthUsername;
        string? secret   = n.EncryptedSecret;
        if (n.AuthType == "Basic" && !string.IsNullOrWhiteSpace(secret)
            && string.IsNullOrWhiteSpace(username))
        {
            var colon = secret.IndexOf(':');
            if (colon > 0) { username = secret[..colon]; secret = secret[(colon + 1)..]; }
        }

        return new TesterDicomWebConfig
        {
            BaseUrl            = baseUrl,
            QidoPath           = qidoPath,
            WadoPath           = wadoPath,
            StowPath           = stowPath,
            AuthType           = n.AuthType,
            Username           = username,
            EncryptedSecret    = secret,
            ValidateTls        = n.ValidateTls,
            HttpTimeoutSeconds = n.HttpTimeoutSeconds,
        };
    }
}
