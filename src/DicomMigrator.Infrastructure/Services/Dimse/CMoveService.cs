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
    // Key: token opaco por-llamada (NO el StudyInstanceUID). La arquitectura permite
    // que el mismo StudyInstanceUID esté siendo migrado por dos jobs en paralelo; si la
    // clave fuera el UID, el Unregister de un job borraría silenciosamente el registro
    // todavía activo del otro (y un Register posterior lo pisaría). Las instancias que
    // llegan se correlacionan por StudyUid contra TODOS los registros activos: si hay
    // más de uno para el mismo estudio, se entregan a todos — la asociación DICOM no
    // trae ninguna identidad de job con la que desambiguar más.
    private sealed record Registration(string StudyUid, string LocalAet, string Dir, Action<string> Callback);
    private readonly ConcurrentDictionary<string, Registration> _registrations = new();

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

    // Called by CMoveService before issuing C-MOVE. Returns a token to pass to UnregisterStudy.
    public string RegisterStudy(string studyUid, string localAet, string downloadDir, Action<string> onFileReceived)
    {
        Directory.CreateDirectory(downloadDir);
        var token = Guid.NewGuid().ToString("N");
        _registrations[token] = new Registration(studyUid, localAet, downloadDir, onFileReceived);
        EnsureStarted();
        Interlocked.Increment(ref _activeWorkers);
        return token;
    }

    // Called by SharedScpHandler on every incoming association: solo se acepta si el AE
    // Title llamado coincide con el configurado en alguno de los C-MOVE activos ahora
    // mismo. Antes se aceptaba CUALQUIER asociación entrante sin comprobar nada, así que
    // cualquier host que alcanzara el puerto podía empujar ficheros arbitrarios al SCP.
    internal bool IsCalledAetAllowed(string calledAe) =>
        _registrations.Values.Any(r => string.Equals(r.LocalAet, calledAe, StringComparison.OrdinalIgnoreCase));

    // Called by CMoveService after C-MOVE completes (success or fail)
    public void UnregisterStudy(string token)
    {
        _registrations.TryRemove(token, out _);
        if (Interlocked.Decrement(ref _activeWorkers) <= 0)
            StopServer();
    }

    // Called from handler when a C-STORE arrives
    internal void OnInstanceReceived(string studyUid, string sopUid, DicomFile file)
    {
        var matches = _registrations.Values.Where(r => r.StudyUid == studyUid).ToList();
        if (matches.Count == 0)
        {
            // Study not registered — store in a fallback folder
            _logger.LogWarning("SCP recibió instancia de estudio no registrado: {Uid}", studyUid);
            var fallbackDir = Path.Combine(Path.GetTempPath(), "dicommigrator", "unregistered");
            Directory.CreateDirectory(fallbackDir);
            matches.Add(new Registration(studyUid, string.Empty, fallbackDir, _ => { }));
        }
        else if (matches.Count > 1)
        {
            _logger.LogWarning(
                "SCP recibió instancia de estudio {Uid} con {Count} registros activos simultáneos (migrado por varios jobs a la vez); se entrega a todos",
                studyUid, matches.Count);
        }

        foreach (var reg in matches)
        {
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

    private readonly ILogger _log;

    public SharedScpHandler(INetworkStream stream, Encoding fallbackEncoding,
        ILogger log, DicomServiceDependencies deps)
        : base(stream, fallbackEncoding, log, deps) => _log = log;

    public Task OnReceiveAssociationRequestAsync(DicomAssociation association)
    {
        // Solo se acepta si el AE Title llamado coincide con el configurado en algún
        // C-MOVE activo ahora mismo — evita que cualquier host que alcance el puerto
        // pueda empujar ficheros arbitrarios haciéndose pasar por el origen esperado.
        if (Server is null || !Server.IsCalledAetAllowed(association.CalledAE))
        {
            _log.LogWarning(
                "SCP rechazó asociación: CalledAE={Called} CallingAE={Calling} Host={Host} (no coincide con ningún C-MOVE activo)",
                association.CalledAE, association.CallingAE, association.RemoteHost);
            return SendAssociationRejectAsync(DicomRejectResult.Permanent,
                DicomRejectSource.ServiceUser, DicomRejectReason.CalledAENotRecognized);
        }

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
        catch (Exception ex)
        {
            // Antes se ignoraba en silencio: un dataset corrupto o cualquier fallo aquí
            // quedaba invisible y el SCU seguía recibiendo Success como si la instancia
            // se hubiera guardado correctamente.
            _log.LogError(ex, "Error procesando C-STORE entrante");
        }
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
        string? registrationToken = null;

        try
        {
            // RegisterStudy puede fallar (CreateDirectory, EnsureStarted con puerto ocupado).
            // El token solo se asigna si efectivamente se hizo el Register — evita
            // Decrement spurios sobre _activeWorkers en el finally.
            registrationToken = scp.RegisterStudy(request.StudyInstanceUid, config.LocalAet, downloadDir, path =>
            {
                received.Add(path);
                result.Logs.Add($"[INFO] Recibido: {Path.GetFileName(path)}");
            });

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
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Cancelación real (pausa/parada del usuario), no el timeout interno de
            // 'cts' (que también deriva de OperationCanceledException): propagarla tal
            // cual para que el llamador la trate como cancelación — no como fallo de
            // conexión — y no penalice el estudio ni dispare una auto-pausa espuria.
            throw;
        }
        catch (OperationCanceledException)
        {
            result.ErrorMessage = "Timeout esperando respuesta C-MOVE.";
            result.Logs.Add($"[WARN] {result.ErrorMessage}");
        }
        catch (DicomAssociationRejectedException ex)
        {
            // El PACS origen rechazó la asociación (AE Title no autorizado, etc.):
            // problema de CONFIGURACIÓN permanente, no una caída de red transitoria.
            result.Success = false;
            result.AssociationRejected = true;
            result.ErrorMessage = $"Asociación rechazada: {ex.Message}";
            result.Logs.Add($"[ERROR] {result.ErrorMessage}");
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
            if (registrationToken is not null) scp.UnregisterStudy(registrationToken);
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
