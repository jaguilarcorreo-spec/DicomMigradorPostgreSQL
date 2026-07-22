// ┌─────────────────────────────────────────────────────────────────────────┐
// │  DimseService.cs                                                        │
// │                                                                         │
// │  Adaptador delgado sobre los servicios DIMSE del DicomPacsTester.       │
// │  DimseTestService y CMoveService se copian SIN MODIFICACIÓN desde       │
// │  DicomPacsTester.Infrastructure.Services.Dimse y se referencian aquí.  │
// │                                                                         │
// │  Solo se adaptan los tipos DicomNode → DimseConfiguration para que      │
// │  el contrato IDimseService acepte el modelo propio del migrador.        │
// └─────────────────────────────────────────────────────────────────────────┘
using DicomMigrator.Core.Interfaces;
using DicomMigrator.Core.Models;
using Microsoft.Extensions.Logging;

namespace DicomMigrator.Infrastructure.Services.Dimse;

/// <summary>
/// Implementa IDimseService adaptando DicomNode al formato que espera
/// el DimseTestService del Tester (copiado en la subcarpeta /Tester/).
/// </summary>
public class DimseService(ILogger<DimseService> logger) : IDimseService
{
    // Reutiliza el DimseTestService del Tester directamente
    private readonly TesterDimseTestService _inner = new(
        Microsoft.Extensions.Logging.Abstractions.NullLogger<TesterDimseTestService>.Instance);

    public async Task<EchoResult> EchoAsync(DicomNode node, CancellationToken ct = default)
    {
        logger.LogInformation("C-ECHO → {Alias} ({Aet}@{Host}:{Port})", node.Alias, node.RemoteAet, node.RemoteHost, node.RemotePort);
        var result = await _inner.EchoAsync(ToTesterConfig(node), ct);
        return new EchoResult
        {
            Success      = result.Success,
            DicomStatus  = result.DicomStatus,
            DurationMs   = result.DurationMs,
            ErrorMessage = result.ErrorMessage,
            Logs         = result.Logs,
        };
    }

    public async Task<CFindResult> FindAsync(DicomNode node, CFindQuery query, CancellationToken ct = default)
    {
        // Log the actual search criterion so identical-looking C-FINDs can be told
        // apart. Verification queries by StudyInstanceUid; discovery queries by date.
        var criterion =
            !string.IsNullOrEmpty(query.StudyInstanceUid) ? $"StudyUID={query.StudyInstanceUid}" :
            !string.IsNullOrEmpty(query.StudyDate)         ? $"StudyDate={query.StudyDate}" :
            !string.IsNullOrEmpty(query.PatientId)         ? $"PatientId={query.PatientId}" :
            "sin filtro";
        logger.LogInformation("C-FIND {Level} {Criterion} → {Alias}", query.Level, criterion, node.Alias);
        var testerQuery = ToTesterQuery(query);
        var result = await _inner.FindAsync(ToTesterConfig(node), testerQuery, ct);
        return new CFindResult
        {
            Success      = result.Success,
            DicomStatus  = result.DicomStatus,
            DurationMs   = result.DurationMs,
            ErrorMessage = result.ErrorMessage,
            Logs         = result.Logs,
            Studies      = result.Studies.Select(s => new DicomStudyDto
            {
                PatientId         = s.PatientId,
                PatientName       = s.PatientName,
                StudyDate         = s.StudyDate,
                AccessionNumber   = s.AccessionNumber,
                StudyInstanceUid  = s.StudyInstanceUid,
                ModalitiesInStudy = s.ModalitiesInStudy,
                StudyDescription  = s.StudyDescription,
                NumberOfInstances = s.NumberOfInstances,
                NumberOfSeries    = s.NumberOfSeries,
                StudyTime         = s.StudyTime,
                InstitutionName   = s.InstitutionName,
                RetrieveAETitle   = s.RetrieveAETitle,
                PatientBirthDate  = s.PatientBirthDate,
                PatientSex        = s.PatientSex,
                IssuerOfPatientId = s.IssuerOfPatientId,
            }).ToList(),
        };
    }

    public async Task<CFindInstancesResult> EnumerateInstancesAsync(DicomNode node, string studyInstanceUid, CancellationToken ct = default)
    {
        logger.LogInformation("C-FIND IMAGE (enumeración) StudyUID={Uid} → {Alias}", studyInstanceUid, node.Alias);
        var r = await _inner.EnumerateInstancesAsync(ToTesterConfig(node), studyInstanceUid, ct);
        return new CFindInstancesResult
        {
            Success      = r.Success,
            DicomStatus  = r.DicomStatus,
            DurationMs   = r.DurationMs,
            ErrorMessage = r.ErrorMessage,
            Instances    = r.Instances.Select(i => new DicomInstanceRef
            {
                SeriesInstanceUid = i.SeriesInstanceUid ?? string.Empty,
                SopInstanceUid    = i.SopInstanceUid ?? string.Empty,
            }).ToList(),
        };
    }

    public async Task<CMoveResult> MoveAsync(DicomNode node, CMoveRequest request, CancellationToken ct = default)
    {
        logger.LogInformation("C-MOVE {Level} StudyUID={Uid} → {DestAet}", request.Level, request.StudyInstanceUid, request.DestinationAet);
        var downloadDir = Path.Combine(Path.GetTempPath(), "dicommigrator", Guid.NewGuid().ToString("N"));
        var testerRequest = new TesterCMoveRequest
        {
            Level             = request.Level,
            StudyInstanceUid  = request.StudyInstanceUid,
            SeriesInstanceUid = request.SeriesInstanceUid,
            SopInstanceUid    = request.SopInstanceUid,
            DestinationAet    = request.DestinationAet,
        };
        var result = await _inner.MoveAsync(ToTesterConfig(node), testerRequest, downloadDir, 60, ct);

        // Distinguir un fallo de CONEXIÓN del origen (asociación rechazada, conexión
        // perdida, timeout) de un fallo real del estudio. Señales de conexión:
        //  - No hubo respuesta DICOM (DicomStatus == null): fue un fallo de transporte.
        //  - El mensaje de error menciona conexión/asociación/timeout.
        var msg = result.ErrorMessage ?? string.Empty;
        var looksLikeConnection =
            msg.Contains("connection", StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("conexión", StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("association", StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("asociación", StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("timed out", StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("timeout", StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("denegó", StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("refused", StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("unreachable", StringComparison.OrdinalIgnoreCase);
        var connectionError = !result.Success
            && result.Completed == 0
            && (result.DicomStatus is null || looksLikeConnection);

        return new CMoveResult
        {
            Success    = result.Success,
            DicomStatus = result.DicomStatus,
            DurationMs = result.DurationMs,
            Completed  = result.Completed,
            Failed     = result.Failed,
            Warning    = result.Warning,
            Remaining  = result.Remaining,
            ReceivedCount     = result.ReceivedCount,
            DownloadDirectory = result.DownloadDirectory,
            ErrorMessage = result.ErrorMessage,
            ConnectionError = connectionError,
            Logs         = result.Logs,
        };
    }

    // ── Adapters ──────────────────────────────────────────────────────────────

    private static TesterDimseConfig ToTesterConfig(DicomNode n) => new()
    {
        RemoteAet                = n.RemoteAet,
        RemoteHost               = n.RemoteHost,
        RemotePort               = n.RemotePort,
        LocalAet                 = n.LocalAet,
        LocalPort                = 11113,   // from LocalConfiguration; injected in real impl
        UseTls                   = n.UseTls,
        AssociationTimeoutSeconds = n.AssociationTimeoutSeconds,
        ResponseTimeoutSeconds   = n.OperationTimeoutSeconds,
    };

    private static TesterCFindQuery ToTesterQuery(CFindQuery q) => new()
    {
        Level             = q.Level,
        PatientId         = q.PatientId,
        PatientName       = q.PatientName,
        StudyDate         = q.StudyDate,
        AccessionNumber   = q.AccessionNumber,
        StudyInstanceUid  = q.StudyInstanceUid,
        SeriesInstanceUid = q.SeriesInstanceUid,
        SopInstanceUid    = q.SopInstanceUid,
        Modality          = q.Modality,
        ModalitiesInStudy = q.ModalitiesInStudy,
    };
}
