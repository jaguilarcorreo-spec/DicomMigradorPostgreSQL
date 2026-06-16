using FellowOakDicom;
using FellowOakDicom.Network;
using FellowOakDicom.Network.Client;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;

namespace DicomMigrator.Infrastructure.Services.Dimse;

// ══════════════════════════════════════════════════════════════════════════════
//  StorageScpServer  —  Singleton SCP compartido por todos los workers
//
//  Ciclo de vida:
//    • Se arranca UNA VEZ al inicio de la primera C-MOVE.
//    • Permanece activo mientras haya al menos un C-MOVE en curso.
//    • Se detiene automáticamente cuando todos los workers terminan.
//
//  Correlación worker↔instancias:
//    • Antes de enviar el C-MOVE, el worker registra su StudyInstanceUID
//      junto con la carpeta de destino y una callback.
//    • Cuando llega un C-STORE, se extrae el StudyInstanceUID y se invoca
//      la callback del worker correspondiente.
// ══════════════════════════════════════════════════════════════════════════════

public sealed class StorageScpServer : IDisposable
{
    // ── Singleton ─────────────────────────────────────────────────────────────
    private static StorageScpServer? _instance;
    private static readonly object   _lock = new();

    public static StorageScpServer GetOrCreate(int port, ILogger logger)
    {
        lock (_lock)
        {
            if (_instance is null || _instance._disposed)
                _instance = new StorageScpServer(port, logger);
            return _instance;
        }
    }

    // ── Per-study registration ─────────────────────────────────────────────
    // Key: StudyInstanceUID  Value: (downloadDir, onFileReceived callback)
    private readonly ConcurrentDictionary<string, (string Dir, Action<string> Callback)> _registrations = new();

    // ── Server state ───────────────────────────────────────────────────────
    private IDicomServer? _server;
    private int           _port;
    private bool          _disposed;
    private readonly ILogger _logger;
    private int _activeWorkers;

    private StorageScpServer(int port, ILogger logger)
    {
        _port   = port;
        _logger = logger;
        // Share static ref with handler
        SharedScpHandler.Server = this;
    }

    // Called by CMoveService before issuing C-MOVE
    public void RegisterStudy(string studyUid, string downloadDir, Action<string> onFileReceived)
    {
        Directory.CreateDirectory(downloadDir);
        _registrations[studyUid] = (downloadDir, onFileReceived);
        EnsureStarted();
        Interlocked.Increment(ref _activeWorkers);
    }

    // Called by CMoveService after C-MOVE completes (success or fail)
    public void UnregisterStudy(string studyUid)
    {
        _registrations.TryRemove(studyUid, out _);
        if (Interlocked.Decrement(ref _activeWorkers) <= 0)
            StopServer();
    }

    // Called from handler when a C-STORE arrives
    internal void OnInstanceReceived(string studyUid, string sopUid, DicomFile file)
    {
        if (!_registrations.TryGetValue(studyUid, out var reg))
        {
            // Study not registered — store in a fallback folder
            _logger.LogWarning("SCP recibió instancia de estudio no registrado: {Uid}", studyUid);
            reg = (Path.Combine(Path.GetTempPath(), "dicommigrator", "unregistered"), _ => { });
            Directory.CreateDirectory(reg.Dir);
        }
        try
        {
            var path = Path.Combine(reg.Dir, $"{sopUid}.dcm");
            file.Save(path);
            reg.Callback(path);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error almacenando instancia {Sop}", sopUid);
        }
    }

    private void EnsureStarted()
    {
        lock (_lock)
        {
            if (_server is not null) return;
            try
            {
                _server = DicomServerFactory.Create<SharedScpHandler>(_port);
                _logger.LogInformation("SCP Storage iniciado en :{Port}", _port);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "No se pudo iniciar el SCP Storage en :{Port}", _port);
                throw;
            }
        }
    }

    private void StopServer()
    {
        lock (_lock)
        {
            if (_server is null) return;
            _server.Dispose();
            _server = null;
            _logger.LogInformation("SCP Storage detenido (sin workers activos)");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopServer();
    }
}

// ══════════════════════════════════════════════════════════════════════════════
//  SharedScpHandler  — un handler de conexión; enruta por StudyInstanceUID
// ══════════════════════════════════════════════════════════════════════════════

public class SharedScpHandler : DicomService, IDicomServiceProvider, IDicomCStoreProvider
{
    internal static StorageScpServer? Server;

    public SharedScpHandler(INetworkStream stream, Encoding fallbackEncoding,
        ILogger log, DicomServiceDependencies deps)
        : base(stream, fallbackEncoding, log, deps) { }

    public Task OnReceiveAssociationRequestAsync(DicomAssociation association)
    {
        foreach (var pc in association.PresentationContexts)
            pc.SetResult(DicomPresentationContextResult.Accept);
        return SendAssociationAcceptAsync(association);
    }
    public Task OnReceiveAssociationReleaseRequestAsync() => SendAssociationReleaseResponseAsync();
    public void OnReceiveAbort(DicomAbortSource source, DicomAbortReason reason) { }
    public void OnConnectionClosed(Exception? exception) { }

    public async Task<DicomCStoreResponse> OnCStoreRequestAsync(DicomCStoreRequest request)
    {
        try
        {
            var studyUid = request.Dataset.GetSingleValueOrDefault(DicomTag.StudyInstanceUID, "");
            var sopUid   = request.Dataset.GetSingleValueOrDefault(DicomTag.SOPInstanceUID,   Guid.NewGuid().ToString());
            Server?.OnInstanceReceived(studyUid, sopUid, request.File);
        }
        catch { }
        return new DicomCStoreResponse(request, DicomStatus.Success);
    }
    public Task OnCStoreRequestExceptionAsync(string tempFileName, Exception e) => Task.CompletedTask;
}

// ══════════════════════════════════════════════════════════════════════════════
//  CMoveService  — usa el SCP compartido
// ══════════════════════════════════════════════════════════════════════════════

public class CMoveService(ILogger<CMoveService> logger)
{
    public async Task<TesterCMoveResult> MoveAsync(
        TesterDimseConfiguration config,
        TesterCMoveRequestInternal request,
        string downloadDir,
        int waitTimeoutSeconds = 30,
        CancellationToken ct = default)
    {
        var result   = new TesterCMoveResult { DownloadDirectory = downloadDir };
        var sw       = Stopwatch.StartNew();
        var received = new ConcurrentBag<string>();

        result.Logs.Add($"[INFO] C-MOVE {request.Level} → {config.RemoteAet} @ {config.RemoteHost}:{config.RemotePort}");
        result.Logs.Add($"[INFO] AET destino: {request.DestinationAet} · SCP local :{config.LocalPort}");
        result.Logs.Add($"[INFO] StudyUID: {request.StudyInstanceUid}");

        // El SCP compartido se obtiene/arranca con el primer estudio activo.
        var scp = StorageScpServer.GetOrCreate(config.LocalPort, logger);
        bool registered = false;

        try
        {
            // RegisterStudy puede fallar (CreateDirectory, EnsureStarted con puerto ocupado).
            // El flag 'registered' asegura que UnregisterStudy solo se llama si efectivamente
            // se hizo el Register — evita Decrement spurios sobre _activeWorkers.
            scp.RegisterStudy(request.StudyInstanceUid, downloadDir, path =>
            {
                received.Add(path);
                result.Logs.Add($"[INFO] Recibido: {Path.GetFileName(path)}");
            });
            registered = true;

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(config.ResponseTimeoutSeconds + 60));

            var client = DicomClientFactory.Create(
                config.RemoteHost, config.RemotePort, config.UseTls,
                config.LocalAet, config.RemoteAet);
            client.ServiceOptions.RequestTimeout = TimeSpan.FromSeconds(config.ResponseTimeoutSeconds);

            DicomCMoveRequest moveReq = request.Level.ToUpperInvariant() switch
            {
                "IMAGE" when !string.IsNullOrEmpty(request.SeriesInstanceUid)
                          && !string.IsNullOrEmpty(request.SopInstanceUid) =>
                    new DicomCMoveRequest(request.DestinationAet, request.StudyInstanceUid,
                        request.SeriesInstanceUid, request.SopInstanceUid),
                "SERIES" when !string.IsNullOrEmpty(request.SeriesInstanceUid) =>
                    new DicomCMoveRequest(request.DestinationAet, request.StudyInstanceUid,
                        request.SeriesInstanceUid),
                _ =>
                    new DicomCMoveRequest(request.DestinationAet, request.StudyInstanceUid),
            };

            moveReq.OnResponseReceived += (req, resp) =>
            {
                result.DicomStatus = resp.Status.Code;
                result.Remaining   = resp.Command.GetValueOrDefault(DicomTag.NumberOfRemainingSuboperations,  0, 0);
                result.Completed   = resp.Command.GetValueOrDefault(DicomTag.NumberOfCompletedSuboperations, 0, 0);
                result.Failed      = resp.Command.GetValueOrDefault(DicomTag.NumberOfFailedSuboperations,    0, 0);
                result.Warning     = resp.Command.GetValueOrDefault(DicomTag.NumberOfWarningSuboperations,   0, 0);
                result.Logs.Add($"[INFO] Status={resp.Status} Completed={result.Completed} " +
                                $"Failed={result.Failed} Remaining={result.Remaining}");
            };

            await client.AddRequestAsync(moveReq);
            await client.SendAsync(cts.Token);

            // C-MOVE hacia otro PACS: el origen envía los C-STORE directamente al destino,
            // NO pasan por el SCP local del migrador. ReceivedCount siempre será 0.
            // Éxito = no hubo fallos Y el PACS reportó al menos 1 completada.
            // 0xFF00/0xFF01 son respuestas de progreso válidas (Pending); si la final
            // es 0x0000 o quedó en 0xFF00 con Completed>0 y Failed=0 → OK.
            result.ReceivedCount = received.Count; // para diagnóstico, siempre 0 en C-MOVE normal
            result.Success = result.Failed == 0
                          && result.Completed > 0
                          && (result.DicomStatus == 0x0000
                              || result.DicomStatus == 0xFF00
                              || result.DicomStatus == 0xFF01);
        }
        catch (OperationCanceledException)
        {
            result.ErrorMessage = "Timeout esperando respuesta C-MOVE.";
            result.Logs.Add($"[WARN] {result.ErrorMessage}");
        }
        catch (Exception ex)
        {
            result.Success       = false;
            result.ErrorMessage  = ex.Message;
            result.Logs.Add($"[ERROR] {ex.Message}");
            logger.LogError(ex, "C-MOVE error");
        }
        finally
        {
            if (registered) scp.UnregisterStudy(request.StudyInstanceUid);
            sw.Stop();
            result.DurationMs = sw.ElapsedMilliseconds;
            result.Logs.Add($"[INFO] Finalizado. Recibidos={received.Count} " +
                            $"Completados={result.Completed} Fallidos={result.Failed} " +
                            $"{result.DurationMs}ms");

            // Clean up any files downloaded to the local SCP directory.
            // In the normal PACS→PACS C-MOVE flow the origin sends directly to the
            // destination AET (not the local SCP), so received.Count is 0 and
            // nothing is downloaded. If files WERE received (local-destination mode),
            // they are deleted here to prevent unbounded disk growth.
            if (received.Count > 0 && Directory.Exists(downloadDir))
            {
                try { Directory.Delete(downloadDir, recursive: true); }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "No se pudo limpiar el directorio de descarga {Dir}", downloadDir);
                }
            }
        }
        return result;
    }
}
