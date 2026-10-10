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
    public string? StudyTime         { get; set; }
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
    /// <summary>True si el PACS remoto rechazó la asociación (AE Title no autorizado,
    /// etc.) — problema de configuración permanente, no una caída transitoria.</summary>
    public bool    AssociationRejected { get; set; }
    /// <summary>True si el rechazo de la asociación es TRANSITORIO (PACS ocupado o al límite
    /// de conexiones): se reintenta, no es error de configuración (CONC-7).</summary>
    public bool    RejectionTransient { get; set; }
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

    // Claves de retorno adicionales (v207) — opcionales, pueden venir vacías.
    public string? StudyTime         { get; set; }
    public string? InstitutionName   { get; set; }
    public string? RetrieveAETitle   { get; set; }
    public string? PatientBirthDate  { get; set; }
    public string? PatientSex        { get; set; }
    public string? IssuerOfPatientId { get; set; }
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
    /// <summary>True si llegó la respuesta FINAL (no Pending) del C-MOVE. Sin ella, el
    /// C-MOVE se cortó (inactividad, red, aborto) y lo contado es parcial.</summary>
    public bool    FinalResponseReceived { get; set; }
    /// <summary>Comentario de error (0000,0902) que el PACS adjunta a la respuesta, si lo hay.</summary>
    public string? ErrorComment      { get; set; }
    public int     ReceivedCount     { get; set; }   // instancias realmente recibidas en el SCP
    public string  DownloadDirectory { get; set; } = string.Empty;
    public string? ErrorMessage      { get; set; }
    public List<string> Logs         { get; set; } = [];
    /// <summary>Ver TesterCFindResult.AssociationRejected.</summary>
    public bool    AssociationRejected { get; set; }
    /// <summary>True si el rechazo de la asociación es TRANSITORIO (PACS ocupado o al límite
    /// de conexiones): se reintenta, no es error de configuración (CONC-7).</summary>
    public bool    RejectionTransient { get; set; }
}

// ── DimseTestService (copiado del Tester) ────────────────────────────────────

public class TesterInstanceDto
{
    public string? SeriesInstanceUid { get; set; }
    public string? SopInstanceUid    { get; set; }
}

public class TesterCFindInstancesResult
{
    public bool    Success      { get; set; }
    public int?    DicomStatus  { get; set; }
    public long    DurationMs   { get; set; }
    public string? ErrorMessage { get; set; }
    public List<TesterInstanceDto> Instances { get; set; } = [];
    public List<string> Logs    { get; set; } = [];
    /// <summary>Ver TesterCFindResult.AssociationRejected.</summary>
    public bool    AssociationRejected { get; set; }
    /// <summary>True si el rechazo de la asociación es TRANSITORIO (PACS ocupado o al límite
    /// de conexiones): se reintenta, no es error de configuración (CONC-7).</summary>
    public bool    RejectionTransient { get; set; }
}

public class DimseTestService(ILogger<DimseTestService> logger)
{
    // ── Tiempos de espera de una operación DIMSE (DCM-3) ──────────────────────
    /// <summary>
    /// Vigilante de tiempos de C-ECHO / C-FIND, con el mismo esquema que el C-MOVE:
    ///   · Timeout asociación del nodo → tiempo máximo para CONECTAR y que el PACS acepte la
    ///     asociación (se configura además en fo-dicom, que antes usaba su valor por defecto).
    ///   · Timeout operación del nodo → tiempo máximo SIN NINGUNA RESPUESTA del PACS; cada
    ///     respuesta (cada resultado de una C-FIND) lo rearma.
    /// Antes el "timeout de asociación" (30 s) cortaba la consulta ENTERA aunque el PACS no
    /// parase de enviar resultados: un día con miles de estudios o un estudio con miles de
    /// imágenes nunca terminaba, y el "timeout de operación" casi nunca actuaba.
    /// fo-dicom NO lanza excepción al cancelar: SendAsync vuelve con normalidad, así que tras
    /// él hay que mirar si saltó el vigilante (Fired) o una cancelación real. Se cancela
    /// ABORTANDO la asociación: liberarla de forma ordenada esperaba hasta 10 s más la
    /// respuesta de un PACS que precisamente se ha quedado colgado.
    /// </summary>
    private sealed class DimseWatchdog : IDisposable
    {
        private readonly TimeSpan _assoc, _idle;
        public CancellationTokenSource Cts { get; }
        public bool Associated { get; private set; }

        public DimseWatchdog(TesterDimseConfiguration config, IDicomClient client, CancellationToken ct)
        {
            _assoc = TimeSpan.FromSeconds(Math.Max(1, config.AssociationTimeoutSeconds));
            _idle  = TimeSpan.FromSeconds(Math.Max(1, config.ResponseTimeoutSeconds));
            Cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            client.ClientOptions.AssociationRequestTimeoutInMs = (int)_assoc.TotalMilliseconds;
            client.ClientOptions.MaximumNumberOfConsecutiveTimedOutAssociationRequests = 1;   // sin reintentos encadenados
            client.ServiceOptions.RequestTimeout = _idle;
            // Conexión TCP + aceptación de la asociación, con un pequeño margen para que el
            // propio timeout de fo-dicom actúe antes si el PACS no contesta a la petición.
            Cts.CancelAfter(_assoc + TimeSpan.FromSeconds(5));
            client.AssociationAccepted += (_, _) => { Associated = true; Activity(); };
        }

        /// <summary>El PACS ha respondido: rearmar la espera por inactividad.</summary>
        public void Activity() { try { Cts.CancelAfter(_idle); } catch (ObjectDisposedException) { } }

        public bool Fired => Cts.IsCancellationRequested;

        public string TimeoutMessage(int received, string unit) => Associated
            ? $"El PACS dejó de responder durante {_idle.TotalSeconds:0} s (timeout de operación del nodo); " +
              $"{received} {unit} recibido(s) antes del corte."
            : $"No se pudo establecer la asociación con el PACS en {_assoc.TotalSeconds:0} s (timeout de asociación del nodo).";

        public void Dispose() => Cts.Dispose();
    }

    // ── C-ECHO ────────────────────────────────────────────────────────────────
    public async Task<TesterEchoResult> EchoAsync(TesterDimseConfiguration config, CancellationToken ct = default)
    {
        var result = new TesterEchoResult();
        var sw = Stopwatch.StartNew();
        DimseWatchdog? echoWatchdog = null;
        try
        {
            logger.LogInformation("C-ECHO → {Aet} @ {Host}:{Port}", config.RemoteAet, config.RemoteHost, config.RemotePort);
            result.Logs.Add($"[INFO] Iniciando C-ECHO → {config.RemoteAet} @ {config.RemoteHost}:{config.RemotePort}");
            result.Logs.Add($"[DEBUG] Calling AET: {config.LocalAet}, Called AET: {config.RemoteAet}");

            var client = DicomClientFactory.Create(config.RemoteHost, config.RemotePort, config.UseTls, config.LocalAet, config.RemoteAet);
            using var watchdog = new DimseWatchdog(config, client, ct);
            echoWatchdog = watchdog;

            var echoRequest = new DicomCEchoRequest();
            DicomStatus? status = null;
            echoRequest.OnResponseReceived += (req, resp) =>
            {
                status = resp.Status;
                result.Logs.Add($"[INFO] Respuesta recibida. Status: {resp.Status}");
            };

            await client.AddRequestAsync(echoRequest);
            await client.SendAsync(watchdog.Cts.Token, DicomClientCancellationMode.ImmediatelyAbortAssociation);
            ct.ThrowIfCancellationRequested();

            sw.Stop();
            result.DurationMs = sw.ElapsedMilliseconds;
            result.DicomStatus = status?.Code;
            result.Success = status == DicomStatus.Success;
            if (status is null && watchdog.Fired) result.ErrorMessage = watchdog.TimeoutMessage(0, "respuesta(s)");
            result.Logs.Add(result.Success
                ? $"[INFO] C-ECHO completado. Status: 0x0000 (Success). RTT: {result.DurationMs}ms"
                : $"[WARN] C-ECHO sin éxito: {result.ErrorMessage ?? status?.ToString()}");
        }
        catch (DicomAssociationRejectedException ex)
        { sw.Stop(); result.DurationMs = sw.ElapsedMilliseconds; result.Success = false; result.ErrorMessage = DescribeRejection(ex, config).Message; result.Logs.Add($"[ERROR] {result.ErrorMessage}"); }
        catch (Exception ex) when ((ex is OperationCanceledException && !ct.IsCancellationRequested) || ex is DicomAssociationRequestTimedOutException)
        { sw.Stop(); result.DurationMs = sw.ElapsedMilliseconds; result.Success = false; result.ErrorMessage = echoWatchdog?.TimeoutMessage(0, "respuesta(s)") ?? "Tiempo de espera agotado"; result.Logs.Add($"[WARN] {result.ErrorMessage}"); }
        catch (Exception ex)
        { sw.Stop(); result.DurationMs = sw.ElapsedMilliseconds; result.Success = false; result.ErrorMessage = ex.Message; result.Logs.Add($"[ERROR] {ex.Message}"); }
        return result;
    }

    // ── C-FIND ────────────────────────────────────────────────────────────────
    public async Task<TesterCFindResult> FindAsync(TesterDimseConfiguration config, TesterCFindQueryInternal query, CancellationToken ct = default)
    {
        var result = new TesterCFindResult();
        var sw = Stopwatch.StartNew();
        DimseWatchdog? findWatchdog = null;
        try
        {
            logger.LogInformation("C-FIND {Level} → {Aet}", query.Level, config.RemoteAet);
            result.Logs.Add($"[INFO] C-FIND {query.Level} → {config.RemoteAet} @ {config.RemoteHost}:{config.RemotePort}");

            var client = DicomClientFactory.Create(config.RemoteHost, config.RemotePort, config.UseTls, config.LocalAet, config.RemoteAet);
            using var watchdog = new DimseWatchdog(config, client, ct);
            findWatchdog = watchdog;

            var request = BuildFindRequest(query);
            int pending = 0;
            request.OnResponseReceived += (req, resp) =>
            {
                watchdog.Activity();   // cada resultado rearma la espera (DCM-3)
                // Por ESTADO, no por código (DCM-1): Pending agrupa 0xFF00 y 0xFF01
                // ("Pending, alguna clave opcional no soportada"). Comparando con
                // DicomStatus.Pending (0xFF00), cada 0xFF01 caía en la rama de respuesta
                // final y su estudio se perdía: un PACS que responde así devolvía 0
                // estudios y la final 0x0000 daba la consulta por buena.
                if (resp.Status.State == DicomState.Pending && resp.Dataset is not null)
                {
                    pending++;
                    var study = ParseStudy(resp.Dataset);
                    result.Studies.Add(study);
                }
                else if (resp.Status.State != DicomState.Pending)   // solo la respuesta final
                {
                    result.DicomStatus = resp.Status.Code;
                    result.Success = resp.Status == DicomStatus.Success;
                    result.Logs.Add($"[INFO] C-FIND completado. Status: {resp.Status}. {pending} resultados.");
                }
            };

            await client.AddRequestAsync(request);
            await client.SendAsync(watchdog.Cts.Token, DicomClientCancellationMode.ImmediatelyAbortAssociation);
            // fo-dicom no lanza al cancelar: una pausa/parada real se propaga aquí (antes se
            // devolvía como una C-FIND fallida más); si saltó el vigilante sin respuesta final,
            // se dice cuál de los dos tiempos se agotó y cuánto llegó.
            ct.ThrowIfCancellationRequested();
            sw.Stop();
            result.DurationMs = sw.ElapsedMilliseconds;
            if (result.DicomStatus is null && watchdog.Fired)
            {
                result.Success = false;
                result.ErrorMessage = watchdog.TimeoutMessage(result.Studies.Count, "resultado(s)");
                result.Logs.Add($"[WARN] C-FIND cortado: {result.ErrorMessage}");
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Cancelación real (pausa/parada del usuario), no el vigilante de tiempos:
            // propagarla para que el llamador la trate como cancelación, no como un
            // C-FIND fallido (ver VerifyStudyAsync).
            throw;
        }
        catch (Exception ex) when (ex is OperationCanceledException or DicomAssociationRequestTimedOutException)
        {
            // Vigilante de tiempos, o el PACS no aceptó la asociación a tiempo (fo-dicom lanza
            // su propia excepción, con un texto en inglés que no dice qué ajuste del nodo es).
            sw.Stop(); result.DurationMs = sw.ElapsedMilliseconds; result.Success = false;
            result.ErrorMessage = findWatchdog?.TimeoutMessage(result.Studies.Count, "resultado(s)") ?? "Tiempo de espera agotado";
            result.Logs.Add($"[WARN] C-FIND cortado: {result.ErrorMessage}");
        }
        catch (DicomAssociationRejectedException ex)
        {
            // El PACS rechazó la asociación. Puede ser PERMANENTE (AE no autorizado o mal
            // configurado: reintentar nunca lo arregla) o TRANSITORIO (PACS ocupado o al
            // límite de conexiones: basta con esperar). Ver DescribeRejection (CONC-7).
            sw.Stop(); result.DurationMs = sw.ElapsedMilliseconds; result.Success = false;
            result.AssociationRejected = true;
            (result.RejectionTransient, result.ErrorMessage) = DescribeRejection(ex, config);
            result.Logs.Add($"[ERROR] {result.ErrorMessage}");
        }
        catch (Exception ex)
        { sw.Stop(); result.DurationMs = sw.ElapsedMilliseconds; result.Success = false; result.ErrorMessage = ex.Message; result.Logs.Add($"[ERROR] {ex.Message}"); }
        return result;
    }

    // ── C-FIND IMAGE: enumeración de instancias (Nivel 2 de verificación) ──────
    public async Task<TesterCFindInstancesResult> EnumerateInstancesAsync(
        TesterDimseConfiguration config, string studyInstanceUid, CancellationToken ct = default)
    {
        var result = new TesterCFindInstancesResult();
        var sw = Stopwatch.StartNew();
        DimseWatchdog? enumWatchdog = null;
        try
        {
            logger.LogInformation("C-FIND IMAGE StudyUID={Uid} → {Aet}", studyInstanceUid, config.RemoteAet);
            result.Logs.Add($"[INFO] C-FIND IMAGE StudyUID={studyInstanceUid} → {config.RemoteAet} @ {config.RemoteHost}:{config.RemotePort}");

            var client = DicomClientFactory.Create(config.RemoteHost, config.RemotePort, config.UseTls, config.LocalAet, config.RemoteAet);
            using var watchdog = new DimseWatchdog(config, client, ct);
            enumWatchdog = watchdog;

            var request = new DicomCFindRequest(DicomQueryRetrieveLevel.Image);
            var ds = request.Dataset;
            ds.AddOrUpdate(DicomTag.StudyInstanceUID,  studyInstanceUid);
            ds.AddOrUpdate(DicomTag.SeriesInstanceUID, string.Empty);   // clave de retorno
            ds.AddOrUpdate(DicomTag.SOPInstanceUID,    string.Empty);   // clave de retorno

            request.OnResponseReceived += (req, resp) =>
            {
                watchdog.Activity();   // cada instancia rearma la espera (DCM-3)
                if (resp.Status.State == DicomState.Pending && resp.Dataset is not null)   // 0xFF00 y 0xFF01 (DCM-1)
                {
                    result.Instances.Add(new TesterInstanceDto
                    {
                        SeriesInstanceUid = resp.Dataset.GetSingleValueOrDefault(DicomTag.SeriesInstanceUID, string.Empty),
                        SopInstanceUid    = resp.Dataset.GetSingleValueOrDefault(DicomTag.SOPInstanceUID,    string.Empty),
                    });
                }
                else if (resp.Status.State != DicomState.Pending)   // solo la respuesta final
                {
                    result.DicomStatus = resp.Status.Code;
                    result.Success = resp.Status == DicomStatus.Success;
                    result.Logs.Add($"[INFO] C-FIND IMAGE completado. Status: {resp.Status}. {result.Instances.Count} instancias.");
                }
            };

            await client.AddRequestAsync(request);
            await client.SendAsync(watchdog.Cts.Token, DicomClientCancellationMode.ImmediatelyAbortAssociation);
            ct.ThrowIfCancellationRequested();   // ver FindAsync
            sw.Stop();
            result.DurationMs = sw.ElapsedMilliseconds;
            if (result.DicomStatus is null && watchdog.Fired)
            {
                result.Success = false;
                result.ErrorMessage = watchdog.TimeoutMessage(result.Instances.Count, "instancia(s)");
                result.Logs.Add($"[WARN] C-FIND IMAGE cortado: {result.ErrorMessage}");
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Cancelación real (pausa/parada del usuario), no el vigilante de tiempos:
            // propagarla para que el llamador la trate como cancelación, no como un
            // C-FIND IMAGE fallido (ver VerifyStudyAsync).
            throw;
        }
        catch (Exception ex) when (ex is OperationCanceledException or DicomAssociationRequestTimedOutException)
        {
            // Ver FindAsync.
            sw.Stop();
            result.DurationMs = sw.ElapsedMilliseconds;
            result.Success = false;
            result.ErrorMessage = enumWatchdog?.TimeoutMessage(result.Instances.Count, "instancia(s)") ?? "Tiempo de espera agotado";
            result.Logs.Add($"[WARN] C-FIND IMAGE cortado: {result.ErrorMessage}");
        }
        catch (DicomAssociationRejectedException ex)
        {
            // Ver comentario equivalente en FindAsync: permanente o transitorio (CONC-7).
            sw.Stop();
            result.DurationMs = sw.ElapsedMilliseconds;
            result.Success = false;
            result.AssociationRejected = true;
            (result.RejectionTransient, result.ErrorMessage) = DescribeRejection(ex, config);
            result.Logs.Add($"[ERROR] {result.ErrorMessage}");
        }
        catch (Exception ex)
        {
            sw.Stop();
            result.DurationMs = sw.ElapsedMilliseconds;
            result.Success = false;
            result.ErrorMessage = ex.Message;
            result.Logs.Add($"[ERROR] {ex.Message}");
        }
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
    /// <summary>Clasifica un rechazo de asociación (A-ASSOCIATE-RJ) y lo explica (CONC-7).
    /// Transitorio: resultado "transient" o motivo de congestión / límite de conexiones,
    /// que se arregla esperando. Permanente: el PACS no reconoce o no admite al migrador,
    /// y solo se arregla cambiando la configuración.</summary>
    internal static (bool Transient, string Message) DescribeRejection(
        DicomAssociationRejectedException ex, TesterDimseConfiguration config)
    {
        var who = $"El PACS {config.RemoteAet} ({config.RemoteHost}:{config.RemotePort})";
        var transient = ex.RejectResult == DicomRejectResult.Transient
                     || ex.RejectReason is DicomRejectReason.TemporaryCongestion or DicomRejectReason.LocalLimitExceeded;
        if (transient)
            return (true, $"{who} rechazó la conexión de forma temporal: está ocupado o al límite de conexiones " +
                          $"simultáneas ({(ex.RejectReason == DicomRejectReason.LocalLimitExceeded ? "límite de asociaciones superado" : "congestión temporal")}). Se reintentará. Si se repite a menudo, reduce los hilos " +
                          "de este proceso o amplía el límite de asociaciones del PACS.");
        var detail = ex.RejectReason switch
        {
            DicomRejectReason.CallingAENotRecognized =>
                $"no reconoce el AE llamante '{config.LocalAet}' (este migrador). Dalo de alta en el PACS " +
                $"(AE '{config.LocalAet}' y la IP de este servidor).",
            DicomRejectReason.CalledAENotRecognized =>
                $"no reconoce el AE llamado '{config.RemoteAet}'. Revisa el AE configurado para este nodo.",
            DicomRejectReason.ApplicationContextNotSupported =>
                "no admite el contexto de aplicación DICOM propuesto.",
            DicomRejectReason.ProtocolVersionNotSupported =>
                "no admite la versión del protocolo DICOM propuesta.",
            _ =>
                $"sin indicar el motivo. Revisa que el PACS tenga dado de alta el AE '{config.LocalAet}' " +
                $"(y la IP de este servidor) y que el AE '{config.RemoteAet}' del nodo sea correcto.",
        };
        return (false, $"{who} rechazó la conexión de forma permanente: {detail}");
    }
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
        // Claves de retorno adicionales (v207). Son atributos opcionales: un SCP
        // conforme que no los soporte simplemente los devuelve vacíos.
        // StudyTime: si viene un rango ("HHMMSS-HHMMSS") actúa como clave de MATCHING
        // (acota por hora en la subdivisión); si viene vacío, es solo clave de retorno.
        ds.AddOrUpdate(DicomTag.StudyTime,          query.StudyTime ?? "");
        ds.AddOrUpdate(DicomTag.InstitutionName,    "");
        ds.AddOrUpdate(DicomTag.RetrieveAETitle,    "");
        ds.AddOrUpdate(DicomTag.PatientBirthDate,   "");
        ds.AddOrUpdate(DicomTag.PatientSex,         "");
        ds.AddOrUpdate(DicomTag.IssuerOfPatientID,  "");
        return req;
    }

    private static TesterStudyDto ParseStudy(DicomDataset ds) => new()
    {
        PatientId         = ds.GetSingleValueOrDefault(DicomTag.PatientID,        string.Empty),
        PatientName       = ds.GetSingleValueOrDefault(DicomTag.PatientName,      string.Empty),
        StudyDate         = ds.GetSingleValueOrDefault(DicomTag.StudyDate,        string.Empty),
        StudyInstanceUid  = ds.GetSingleValueOrDefault(DicomTag.StudyInstanceUID, string.Empty),
        AccessionNumber   = ds.GetSingleValueOrDefault(DicomTag.AccessionNumber,  string.Empty),
        // Multivalor (VM 1-n): un estudio CT con informe llega como "CT\SR".
        // GetSingleValueOrDefault devuelve vacío si hay más de un valor; TryGetString
        // los devuelve todos separados por '\' y no lanza si el PACS omite el atributo.
        ModalitiesInStudy = ds.TryGetString(DicomTag.ModalitiesInStudy, out var mods) ? mods : string.Empty,
        StudyDescription  = ds.GetSingleValueOrDefault(DicomTag.StudyDescription, string.Empty),
        NumberOfInstances = ds.TryGetValue(DicomTag.NumberOfStudyRelatedInstances, 0, out int n) ? n : null,
        NumberOfSeries    = ds.TryGetValue(DicomTag.NumberOfStudyRelatedSeries,    0, out int s) ? s : null,
        StudyTime         = ds.GetSingleValueOrDefault(DicomTag.StudyTime,         string.Empty),
        InstitutionName   = ds.GetSingleValueOrDefault(DicomTag.InstitutionName,   string.Empty),
        RetrieveAETitle   = ds.GetSingleValueOrDefault(DicomTag.RetrieveAETitle,   string.Empty),
        PatientBirthDate  = ds.GetSingleValueOrDefault(DicomTag.PatientBirthDate,  string.Empty),
        PatientSex        = ds.GetSingleValueOrDefault(DicomTag.PatientSex,        string.Empty),
        IssuerOfPatientId = ds.GetSingleValueOrDefault(DicomTag.IssuerOfPatientID, string.Empty),
    };
}
