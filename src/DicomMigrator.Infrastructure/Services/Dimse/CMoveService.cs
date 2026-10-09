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
            else if (_instance._port != port)
            {
                // El singleton nunca se desecha (StopServer solo cierra el listener), así que
                // el puerto de la primera C-MOVE quedaba fijo hasta reiniciar el servicio aunque
                // se cambiara en LocalConfigPage. Con el listener parado se adopta el nuevo;
                // si está en uso por otros C-MOVE, se aplicará cuando éstos terminen.
                if (_instance._server is null)
                    _instance._port = port;
                else
                    logger.LogWarning(
                        "SCP Storage sigue escuchando en :{Old}; el nuevo puerto :{New} se aplicará cuando terminen los C-MOVE activos",
                        _instance._port, port);
            }
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

            // Vigilante de INACTIVIDAD, no tope total: se rearma con cada respuesta del PACS.
            // Antes era un tope fijo (ResponseTimeout + 60 = 180 s por defecto) que cortaba
            // cualquier estudio grande que tardara más, aunque el PACS siguiera enviando y
            // respondiendo con Pending; y el corte se daba por bueno (CONC-2).
            var inactivity = TimeSpan.FromSeconds(config.ResponseTimeoutSeconds + 60);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(inactivity);

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

            bool countersReported = false;
            moveReq.OnResponseReceived += (req, resp) =>
            {
                result.DicomStatus = resp.Status.Code;
                var isFinal = resp.Status.State != DicomState.Pending;

                // Los contadores son OPCIONALES en la respuesta final (PS3.4 C.4.2.1.4):
                // solo se actualizan si vienen. Antes un final sin contadores los ponía a 0
                // y un estudio ya migrado se reenviaba hasta acabar "Failed" (DCM-6).
                if (TryCount(resp.Command, DicomTag.NumberOfCompletedSuboperations, out var c)) { result.Completed = c; countersReported = true; }
                if (TryCount(resp.Command, DicomTag.NumberOfFailedSuboperations,    out var f)) { result.Failed    = f; countersReported = true; }
                if (TryCount(resp.Command, DicomTag.NumberOfWarningSuboperations,   out var w)) { result.Warning   = w; countersReported = true; }
                // Remaining solo tiene sentido en las Pending; en la final, si no viene, es 0.
                result.Remaining = TryCount(resp.Command, DicomTag.NumberOfRemainingSuboperations, out var r) ? r
                                 : isFinal ? 0 : result.Remaining;
                if (isFinal) result.FinalResponseReceived = true;
                // Comentario de error del PACS (0000,0902), p. ej. GE: "VNAPRE is a unknown
                // move destination". Dice la causa exacta; se muestra en el mensaje (DCM-4).
                var comment = resp.Command.GetSingleValueOrDefault(DicomTag.ErrorComment, string.Empty);
                if (!string.IsNullOrWhiteSpace(comment)) result.ErrorComment = comment.Trim();

                result.Logs.Add($"[INFO] Status={resp.Status} Completed={result.Completed} " +
                                $"Failed={result.Failed} Remaining={result.Remaining}");

                // Hay actividad: rearmar el vigilante de inactividad.
                try { cts.CancelAfter(inactivity); } catch (ObjectDisposedException) { }
            };

            await client.AddRequestAsync(moveReq);
            await client.SendAsync(cts.Token);

            // fo-dicom NO lanza excepción al cancelar (modo ImmediatelyReleaseAssociation):
            // SendAsync vuelve con normalidad y lo recibido hasta entonces son respuestas
            // Pending con una parte de las imágenes. Antes eso se daba por migrado.
            if (ct.IsCancellationRequested)
                throw new OperationCanceledException(ct);   // pausa/parada/ventana: ver catch
            if (cts.IsCancellationRequested && !result.FinalResponseReceived)
            {
                result.ErrorMessage = $"Sin respuesta del PACS origen durante {inactivity.TotalSeconds:0} s " +
                                      $"(C-MOVE cortado sin respuesta final; Completed={result.Completed}, Remaining={result.Remaining}).";
                result.Logs.Add($"[WARN] {result.ErrorMessage}");
            }

            // C-MOVE hacia otro PACS: el origen envía los C-STORE directamente al destino,
            // NO pasan por el SCP local del migrador. ReceivedCount siempre será 0.
            //
            // Éxito SOLO con la respuesta FINAL del PACS (CONC-2):
            //   · 0x0000 (todas las sub-operaciones OK), o 0xB000 (terminado con avisos) sin
            //     fallos: los avisos no son pérdida de imágenes (CONC-17);
            //   · sin fallos ni sub-operaciones pendientes;
            //   · al menos una completada, salvo que el PACS no informe contadores (son
            //     opcionales) y la final sea 0x0000.
            // Una 0xFF00/0xFF01 (Pending) como último estado significa que el C-MOVE no
            // terminó: tope, corte de red o aborto del PACS. Ya no cuenta como migrado.
            result.ReceivedCount = received.Count; // para diagnóstico, siempre 0 en C-MOVE normal
            result.Success = result.FinalResponseReceived
                          && (result.DicomStatus == 0x0000 || result.DicomStatus == 0xB000)
                          && result.Failed == 0
                          && result.Remaining == 0
                          && (result.Completed > 0 || (!countersReported && result.DicomStatus == 0x0000));
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
            // El PACS origen rechazó la asociación: permanente (configuración) o transitorio
            // (ocupado / al límite de conexiones). Ver DescribeRejection (CONC-7).
            result.Success = false;
            result.AssociationRejected = true;
            (result.RejectionTransient, result.ErrorMessage) = DimseTestService.DescribeRejection(ex, config);
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

    /// <summary>Lee un contador de sub-operaciones solo si el PACS lo incluyó.</summary>
    private static bool TryCount(DicomDataset command, DicomTag tag, out int value)
    {
        value = 0;
        if (!command.Contains(tag)) return false;
        value = command.GetValueOrDefault(tag, 0, 0);
        return true;
    }
}
