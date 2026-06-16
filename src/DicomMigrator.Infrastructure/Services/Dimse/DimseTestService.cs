// ═══════════════════════════════════════════════════════════════════════════
//  COPIADO LITERALMENTE DE DicomPacsTester.Infrastructure.Services.Dimse
//  Solo se cambia el namespace.
// ═══════════════════════════════════════════════════════════════════════════
using FellowOakDicom;
using FellowOakDicom.Network;
using FellowOakDicom.Network.Client;
using Microsoft.Extensions.Logging;
using System.Diagnostics;

namespace DicomMigrator.Infrastructure.Services.Dimse;

// ── Internal DTOs (mirrors of Tester's DTOs to avoid Core dependency here) ──

public class TesterDimseConfiguration
{
    public string RemoteAet                  { get; set; } = string.Empty;
    public string RemoteHost                 { get; set; } = string.Empty;
    public int    RemotePort                 { get; set; } = 104;
    public string LocalAet                   { get; set; } = "MIGRATOR_SCU";
    public int    LocalPort                  { get; set; } = 11113;
    public bool   UseTls                     { get; set; } = false;
    public int    AssociationTimeoutSeconds  { get; set; } = 30;
    public int    ResponseTimeoutSeconds     { get; set; } = 120;
}

public class TesterCFindQueryInternal
{
    public string  Level             { get; set; } = "STUDY";
    public string? PatientId         { get; set; }
    public string? PatientName       { get; set; }
    public string? StudyDate         { get; set; }
    public string? AccessionNumber   { get; set; }
    public string? StudyInstanceUid  { get; set; }
    public string? SeriesInstanceUid { get; set; }
    public string? SopInstanceUid    { get; set; }
    public string? Modality          { get; set; }
    public string? ModalitiesInStudy { get; set; }
}

public class TesterCMoveRequestInternal
{
    public string  Level             { get; set; } = "STUDY";
    public string  StudyInstanceUid  { get; set; } = string.Empty;
    public string? SeriesInstanceUid { get; set; }
    public string? SopInstanceUid    { get; set; }
    public string  DestinationAet    { get; set; } = string.Empty;
}

public class TesterEchoResult
{
    public bool    Success      { get; set; }
    public int?    DicomStatus  { get; set; }
    public long    DurationMs   { get; set; }
    public string? ErrorMessage { get; set; }
    public List<string> Logs    { get; set; } = [];
}

public class TesterCFindResult
{
    public bool    Success      { get; set; }
    public int?    DicomStatus  { get; set; }
    public long    DurationMs   { get; set; }
    public List<TesterStudyDto> Studies { get; set; } = [];
    public string? ErrorMessage { get; set; }
    public List<string> Logs    { get; set; } = [];
}

public class TesterStudyDto
{
    public string? PatientId         { get; set; }
    public string? PatientName       { get; set; }
    public string? StudyDate         { get; set; }
    public string? AccessionNumber   { get; set; }
    public string? StudyInstanceUid  { get; set; }
    public string? ModalitiesInStudy { get; set; }
    public string? StudyDescription  { get; set; }
    public int?    NumberOfInstances { get; set; }
    public int?    NumberOfSeries    { get; set; }
}

public class TesterCMoveResult
{
    public bool    Success           { get; set; }
    public int?    DicomStatus       { get; set; }
    public long    DurationMs        { get; set; }
    public int     Completed         { get; set; }
    public int     Failed            { get; set; }
    public int     Warning           { get; set; }
    public int     Remaining         { get; set; }
    public int     ReceivedCount     { get; set; }   // instancias realmente recibidas en el SCP
    public string  DownloadDirectory { get; set; } = string.Empty;
    public string? ErrorMessage      { get; set; }
    public List<string> Logs         { get; set; } = [];
}

// ── DimseTestService (copiado del Tester) ────────────────────────────────────

public class DimseTestService(ILogger<DimseTestService> logger)
{
    // ── C-ECHO ────────────────────────────────────────────────────────────────
    public async Task<TesterEchoResult> EchoAsync(TesterDimseConfiguration config, CancellationToken ct = default)
    {
        var result = new TesterEchoResult();
        var sw = Stopwatch.StartNew();
        try
        {
            logger.LogInformation("C-ECHO → {Aet} @ {Host}:{Port}", config.RemoteAet, config.RemoteHost, config.RemotePort);
            result.Logs.Add($"[INFO] Iniciando C-ECHO → {config.RemoteAet} @ {config.RemoteHost}:{config.RemotePort}");
            result.Logs.Add($"[DEBUG] Calling AET: {config.LocalAet}, Called AET: {config.RemoteAet}");

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(config.AssociationTimeoutSeconds));

            var client = DicomClientFactory.Create(config.RemoteHost, config.RemotePort, config.UseTls, config.LocalAet, config.RemoteAet);
            client.ServiceOptions.RequestTimeout = TimeSpan.FromSeconds(config.ResponseTimeoutSeconds);

            var echoRequest = new DicomCEchoRequest();
            DicomStatus? status = null;
            echoRequest.OnResponseReceived += (req, resp) =>
            {
                status = resp.Status;
                result.Logs.Add($"[INFO] Respuesta recibida. Status: {resp.Status}");
            };

            await client.AddRequestAsync(echoRequest);
            await client.SendAsync(cts.Token);

            sw.Stop();
            result.DurationMs = sw.ElapsedMilliseconds;
            result.DicomStatus = status?.Code;
            result.Success = status == DicomStatus.Success;
            result.Logs.Add(result.Success
                ? $"[INFO] C-ECHO completado. Status: 0x0000 (Success). RTT: {result.DurationMs}ms"
                : $"[WARN] C-ECHO status inesperado: {status}");
        }
        catch (DicomAssociationRejectedException ex)
        { sw.Stop(); result.DurationMs = sw.ElapsedMilliseconds; result.Success = false; result.ErrorMessage = $"Asociación rechazada: {ex.Message}"; result.Logs.Add($"[ERROR] {result.ErrorMessage}"); }
        catch (OperationCanceledException)
        { sw.Stop(); result.DurationMs = sw.ElapsedMilliseconds; result.Success = false; result.ErrorMessage = $"Timeout tras {config.AssociationTimeoutSeconds}s"; result.Logs.Add($"[WARN] C-ECHO timeout"); }
        catch (Exception ex)
        { sw.Stop(); result.DurationMs = sw.ElapsedMilliseconds; result.Success = false; result.ErrorMessage = ex.Message; result.Logs.Add($"[ERROR] {ex.Message}"); }
        return result;
    }

    // ── C-FIND ────────────────────────────────────────────────────────────────
    public async Task<TesterCFindResult> FindAsync(TesterDimseConfiguration config, TesterCFindQueryInternal query, CancellationToken ct = default)
    {
        var result = new TesterCFindResult();
        var sw = Stopwatch.StartNew();
        try
        {
            logger.LogInformation("C-FIND {Level} → {Aet}", query.Level, config.RemoteAet);
            result.Logs.Add($"[INFO] C-FIND {query.Level} → {config.RemoteAet} @ {config.RemoteHost}:{config.RemotePort}");

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(config.AssociationTimeoutSeconds));

            var client = DicomClientFactory.Create(config.RemoteHost, config.RemotePort, config.UseTls, config.LocalAet, config.RemoteAet);
            client.ServiceOptions.RequestTimeout = TimeSpan.FromSeconds(config.ResponseTimeoutSeconds);

            var request = BuildFindRequest(query);
            int pending = 0;
            request.OnResponseReceived += (req, resp) =>
            {
                if (resp.Status == DicomStatus.Pending && resp.Dataset is not null)
                {
                    pending++;
                    var study = ParseStudy(resp.Dataset);
                    result.Studies.Add(study);
                }
                else
                {
                    result.DicomStatus = resp.Status.Code;
                    result.Success = resp.Status == DicomStatus.Success;
                    result.Logs.Add($"[INFO] C-FIND completado. Status: {resp.Status}. {pending} resultados.");
                }
            };

            await client.AddRequestAsync(request);
            await client.SendAsync(cts.Token);
            sw.Stop();
            result.DurationMs = sw.ElapsedMilliseconds;
        }
        catch (Exception ex)
        { sw.Stop(); result.DurationMs = sw.ElapsedMilliseconds; result.Success = false; result.ErrorMessage = ex.Message; result.Logs.Add($"[ERROR] {ex.Message}"); }
        return result;
    }

    // ── C-MOVE ────────────────────────────────────────────────────────────────
    public async Task<TesterCMoveResult> MoveAsync(TesterDimseConfiguration config, TesterCMoveRequestInternal request, string downloadDir, int waitTimeout = 30, CancellationToken ct = default)
    {
        var svc = new CMoveService(Microsoft.Extensions.Logging.Abstractions.NullLogger<CMoveService>.Instance);
        var r = await svc.MoveAsync(config, request, downloadDir, waitTimeout, ct);
        return r;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────
    private static DicomCFindRequest BuildFindRequest(TesterCFindQueryInternal query)
    {
        var level = query.Level.ToUpperInvariant() switch
        {
            "PATIENT" => DicomQueryRetrieveLevel.Patient,
            "SERIES"  => DicomQueryRetrieveLevel.Series,
            "IMAGE"   => DicomQueryRetrieveLevel.Image,
            _         => DicomQueryRetrieveLevel.Study,
        };
        var req = new DicomCFindRequest(level);
        var ds  = req.Dataset;
        ds.AddOrUpdate(DicomTag.PatientID,         query.PatientId ?? "");
        ds.AddOrUpdate(DicomTag.PatientName,       query.PatientName ?? "");
        ds.AddOrUpdate(DicomTag.StudyDate,         query.StudyDate ?? "");
        ds.AddOrUpdate(DicomTag.AccessionNumber,   query.AccessionNumber ?? "");
        ds.AddOrUpdate(DicomTag.StudyInstanceUID,  query.StudyInstanceUid ?? "");
        ds.AddOrUpdate(DicomTag.StudyDescription,  "");
        ds.AddOrUpdate(DicomTag.ModalitiesInStudy, query.ModalitiesInStudy ?? "");
        ds.AddOrUpdate(DicomTag.NumberOfStudyRelatedInstances, "");
        ds.AddOrUpdate(DicomTag.NumberOfStudyRelatedSeries, "");
        return req;
    }

    private static TesterStudyDto ParseStudy(DicomDataset ds) => new()
    {
        PatientId         = ds.GetSingleValueOrDefault(DicomTag.PatientID,        string.Empty),
        PatientName       = ds.GetSingleValueOrDefault(DicomTag.PatientName,      string.Empty),
        StudyDate         = ds.GetSingleValueOrDefault(DicomTag.StudyDate,        string.Empty),
        StudyInstanceUid  = ds.GetSingleValueOrDefault(DicomTag.StudyInstanceUID, string.Empty),
        AccessionNumber   = ds.GetSingleValueOrDefault(DicomTag.AccessionNumber,  string.Empty),
        ModalitiesInStudy = ds.GetSingleValueOrDefault(DicomTag.ModalitiesInStudy, string.Empty),
        StudyDescription  = ds.GetSingleValueOrDefault(DicomTag.StudyDescription, string.Empty),
        NumberOfInstances = ds.TryGetValue(DicomTag.NumberOfStudyRelatedInstances, 0, out int n) ? n : null,
        NumberOfSeries    = ds.TryGetValue(DicomTag.NumberOfStudyRelatedSeries,    0, out int s) ? s : null,
    };
}
