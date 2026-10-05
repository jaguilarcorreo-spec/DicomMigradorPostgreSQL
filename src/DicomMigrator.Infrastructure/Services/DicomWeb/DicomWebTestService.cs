// ═══════════════════════════════════════════════════════════════════════════
//  COPIADO LITERALMENTE DE DicomPacsTester — solo cambia el namespace.
// ═══════════════════════════════════════════════════════════════════════════
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace DicomMigrator.Infrastructure.Services.DicomWeb;

// ── Internal DTOs ────────────────────────────────────────────────────────────

public class TesterDicomWebConfig
{
    public string  BaseUrl            { get; set; } = string.Empty;
    public string  QidoPath           { get; set; } = "/qido-rs";
    public string  WadoPath           { get; set; } = "/wado-rs";
    public string  StowPath           { get; set; } = "/stow-rs";
    public string  AuthType           { get; set; } = "None";
    public string? Username           { get; set; }
    public string? EncryptedSecret    { get; set; }
    public bool    ValidateTls        { get; set; } = true;
    public int     HttpTimeoutSeconds { get; set; } = 30;
}

public class TesterQidoQuery
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
    public string? StudyTime         { get; set; }
    public int     Limit             { get; set; } = 100;
    public int     Offset            { get; set; } = 0;
    public string? IncludeField      { get; set; }
}

public class TesterQidoResult
{
    public bool   Success      { get; set; }
    public int    HttpStatus   { get; set; }
    public long   DurationMs   { get; set; }
    /// <summary>Elementos que traía la respuesta (incluidos los descartados). Es el
    /// número que sirve para saber si una página vino llena (paginación).</summary>
    public int    ResultCount  { get; set; }
    /// <summary>Elementos de la respuesta que no se pudieron leer (sin StudyInstanceUID,
    /// o con una estructura inesperada). No se pierden en silencio: se cuentan.</summary>
    public int    SkippedCount { get; set; }
    public string? RawJson     { get; set; }
    public List<TesterStudyDtoWeb>        Studies         { get; set; } = [];
    public Dictionary<string, string>     ResponseHeaders { get; set; } = [];
    public string? ErrorMessage { get; set; }
    public string  RequestUrl   { get; set; } = string.Empty;
}

public class TesterStudyDtoWeb
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

// ── DicomWebTestService (copiado del Tester) ─────────────────────────────────

public class TesterWebInstance
{
    public string? SeriesInstanceUid { get; set; }
    public string? SopInstanceUid    { get; set; }
}

public class TesterWebInstancesResult
{
    public bool    Success      { get; set; }
    public int?    HttpStatus   { get; set; }
    public long    DurationMs   { get; set; }
    public string? ErrorMessage { get; set; }
    public List<TesterWebInstance> Instances { get; set; } = [];
}

public class DicomWebTestService
{
    private readonly ILogger<DicomWebTestService> logger;
    private readonly IHttpClientFactory? _httpFactory;

    public DicomWebTestService(ILogger<DicomWebTestService> logger,
                               IHttpClientFactory? httpFactory = null)
    {
        this.logger = logger;
        _httpFactory = httpFactory;
    }

    public async Task<TesterQidoResult> QidoAsync(TesterDicomWebConfig config, TesterQidoQuery query, CancellationToken ct = default)
    {
        var result = new TesterQidoResult();
        var sw = Stopwatch.StartNew();
        try
        {
            if (string.IsNullOrWhiteSpace(config.BaseUrl) || !Uri.TryCreate(config.BaseUrl, UriKind.Absolute, out _))
            {
                result.ErrorMessage = $"BaseUrl inválida: '{config.BaseUrl}'"; return result;
            }
            var url = BuildQidoUrl(config, query);
            result.RequestUrl = url;
            logger.LogInformation("QIDO-RS GET {Url}", url);

            // Build HTTP request — uses pooled HttpClient when factory is available
            // (avoids socket exhaustion under high partition counts)
            HttpClient client;
            HttpClientHandler? ownedHandler = null;
            if (_httpFactory is not null)
            {
                client = _httpFactory.CreateClient(config.ValidateTls ? "dicomweb-tls" : "dicomweb-relaxed");
                client.Timeout = TimeSpan.FromSeconds(config.HttpTimeoutSeconds);
            }
            else
            {
                // Fallback for callers without DI (e.g. direct instantiation)
                (client, ownedHandler) = BuildPooledHttpClientFallback(config);
            }
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                ApplyAuthHeader(request, config);
                ApplyAcceptHeader(request);
                var response = await client.SendAsync(request, ct);
                sw.Stop();
                result.DurationMs  = sw.ElapsedMilliseconds;
                result.HttpStatus  = (int)response.StatusCode;
                result.Success     = response.IsSuccessStatusCode;
                foreach (var h in response.Headers) result.ResponseHeaders[h.Key] = string.Join(", ", h.Value);

                if (response.IsSuccessStatusCode)
                {
                    // Antes bastaba el HTTP 200 para dar la consulta por buena, y el análisis
                    // tragaba cualquier error devolviendo lo leído hasta ese punto: una página
                    // HTML de un proxy, o un valor con un tipo inesperado, dejaban la partición
                    // "Completed" con estudios de menos y sin ningún log (DISC-2).
                    result.RawJson = await response.Content.ReadAsStringAsync(ct);
                    var (studies, total, skipped, error) = ParseQido(result.RawJson);
                    result.Studies      = studies;
                    result.ResultCount  = total;
                    result.SkippedCount = skipped;
                    if (error is not null)
                    {
                        result.Success      = false;
                        result.ErrorMessage = error;
                        logger.LogWarning("QIDO-RS: respuesta no válida de {Url}: {Error}", url, error);
                    }
                    else if (skipped > 0)
                        logger.LogWarning("QIDO-RS: {Skipped} de {Total} estudios de la respuesta no se pudieron leer ({Url})",
                            skipped, total, url);
                }
                else
                { result.ErrorMessage = $"HTTP {result.HttpStatus}: {response.ReasonPhrase}"; }
            }
            finally
            {
                // Only dispose if we created a one-off client ourselves
                if (ownedHandler is not null) { client.Dispose(); ownedHandler.Dispose(); }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Cancelación real (pausa/parada del usuario), no el timeout de HttpClient:
            // propagarla para que el llamador la trate como cancelación, no como fallo
            // de conexión (ver VerifyStudyAsync/CMoveService.MoveAsync).
            throw;
        }
        catch (TaskCanceledException)
        { sw.Stop(); result.DurationMs = sw.ElapsedMilliseconds; result.Success = false; result.ErrorMessage = $"Timeout tras {config.HttpTimeoutSeconds}s"; }
        catch (Exception ex)
        { sw.Stop(); result.DurationMs = sw.ElapsedMilliseconds; result.Success = false; result.ErrorMessage = ex.Message; }
        return result;
    }

    // ── Helpers (idénticos al Tester) ────────────────────────────────────────
    // ── QIDO-RS: enumeración de instancias de un estudio (Nivel 2, vía 4A) ────
    public async Task<TesterWebInstancesResult> EnumerateInstancesWebAsync(
        TesterDicomWebConfig config, string studyInstanceUid, CancellationToken ct = default)
    {
        var result = new TesterWebInstancesResult();
        var sw = Stopwatch.StartNew();
        try
        {
            if (string.IsNullOrWhiteSpace(config.BaseUrl) || !Uri.TryCreate(config.BaseUrl, UriKind.Absolute, out _))
            { result.ErrorMessage = $"BaseUrl inválida: '{config.BaseUrl}'"; return result; }

            // {base}{qidoPath}/studies/{uid}/instances?includefield=SOP,Series&limit=…
            var url = $"{config.BaseUrl.TrimEnd('/')}{config.QidoPath}/studies/{Uri.EscapeDataString(studyInstanceUid)}/instances" +
                      $"?includefield={Uri.EscapeDataString("00080018,0020000E")}&limit=1000000&offset=0";
            logger.LogInformation("QIDO-RS instances GET {Url}", url);

            HttpClient client;
            HttpClientHandler? ownedHandler = null;
            if (_httpFactory is not null)
            {
                client = _httpFactory.CreateClient(config.ValidateTls ? "dicomweb-tls" : "dicomweb-relaxed");
                client.Timeout = TimeSpan.FromSeconds(config.HttpTimeoutSeconds);
            }
            else
            {
                (client, ownedHandler) = BuildPooledHttpClientFallback(config);
            }
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                ApplyAuthHeader(request, config);
                ApplyAcceptHeader(request);
                var response = await client.SendAsync(request, ct);
                sw.Stop();
                result.DurationMs = sw.ElapsedMilliseconds;
                result.HttpStatus = (int)response.StatusCode;
                if (response.IsSuccessStatusCode)
                {
                    var json = await response.Content.ReadAsStringAsync(ct);
                    var (instances, skipped, error) = ParseInstancesWeb(json);
                    result.Instances = instances;
                    // Para la verificación Nivel 2 un conjunto de UIDs incompleto no vale: una
                    // respuesta ilegible o con instancias sin SOPInstanceUID es un fallo de la
                    // consulta (se reintenta), no "estas son todas las instancias".
                    if (error is not null)
                        result.ErrorMessage = error;
                    else if (skipped > 0)
                        result.ErrorMessage = $"{skipped} instancia(s) de la respuesta sin SOPInstanceUID legible: conjunto de UIDs incompleto.";
                    result.Success = result.ErrorMessage is null;
                    if (!result.Success)
                        logger.LogWarning("QIDO-RS instances: {Error} ({Url})", result.ErrorMessage, url);
                }
                else
                {
                    result.Success = false;
                    result.ErrorMessage = $"HTTP {result.HttpStatus}: {response.ReasonPhrase}";
                }
            }
            finally
            {
                if (ownedHandler is not null) { client.Dispose(); ownedHandler.Dispose(); }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (TaskCanceledException)
        { sw.Stop(); result.DurationMs = sw.ElapsedMilliseconds; result.Success = false; result.ErrorMessage = $"Timeout tras {config.HttpTimeoutSeconds}s"; }
        catch (Exception ex)
        { sw.Stop(); result.DurationMs = sw.ElapsedMilliseconds; result.Success = false; result.ErrorMessage = ex.Message; }
        return result;
    }

    private static (List<TesterWebInstance> instances, int skipped, string? error) ParseInstancesWeb(string json)
    {
        var list = new List<TesterWebInstance>();
        int skipped = 0;
        if (string.IsNullOrWhiteSpace(json)) return (list, 0, null);   // 204 / sin resultados
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return (list, 0, $"Respuesta QIDO-RS no válida: se esperaba una lista JSON y llegó {doc.RootElement.ValueKind}. {Snippet(json)}");
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                var sop = GetVal(item, "00080018");
                if (string.IsNullOrEmpty(sop)) { skipped++; continue; }
                list.Add(new TesterWebInstance { SopInstanceUid = sop, SeriesInstanceUid = GetVal(item, "0020000E") });
            }
        }
        catch (JsonException ex)
        {
            return (list, skipped, $"Respuesta QIDO-RS no es JSON válido ({ex.Message}). {Snippet(json)}");
        }
        return (list, skipped, null);
    }

    private static (HttpClient client, HttpClientHandler handler) BuildPooledHttpClientFallback(TesterDicomWebConfig config)
    {
        var handler = new HttpClientHandler();
        if (!config.ValidateTls)
            handler.ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(config.HttpTimeoutSeconds) };
        return (client, handler);
    }

    private static void ApplyAuthHeader(HttpRequestMessage request, TesterDicomWebConfig config)
    {
        switch (config.AuthType)
        {
            case "Basic":
                request.Headers.Authorization = new AuthenticationHeaderValue("Basic",
                    Convert.ToBase64String(Encoding.UTF8.GetBytes($"{config.Username}:{config.EncryptedSecret}")));
                break;
            case "Bearer":
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config.EncryptedSecret);
                break;
            case "ApiKey":
                request.Headers.Add("X-Api-Key", config.EncryptedSecret);
                break;
        }
    }

    /// <summary>QIDO-RS responde en application/dicom+json; se acepta también
    /// application/json por servidores que lo etiquetan así.</summary>
    private static void ApplyAcceptHeader(HttpRequestMessage request)
    {
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/dicom+json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json", 0.9));
    }

    private static string BuildQidoUrl(TesterDicomWebConfig config, TesterQidoQuery query)
    {
        // Identical to DicomPacsTester: BaseUrl + QidoPath + "/" + Level
        // e.g. http://localhost:8042/dicom-web + /studies + /studies → wrong
        // So QidoPath must NOT include the level name.
        // Correct config for Orthanc: BaseUrl=http://localhost:8042/dicom-web  QidoPath=/studies
        // → http://localhost:8042/dicom-web/studies?...  (level "studies" NOT appended again)
        // Wait — Tester DOES append level. So QidoPath should be empty or just /qido-rs:
        // Orthanc DICOMweb: BaseUrl=http://localhost:8042/dicom-web  QidoPath=/studies
        //   → http://localhost:8042/dicom-web/studies/studies ← WRONG
        // Orthanc DICOMweb: BaseUrl=http://localhost:8042/dicom-web  QidoPath= (empty)
        //   → http://localhost:8042/dicom-web/studies ← CORRECT
        // So for Orthanc the user should leave QidoPath empty or set BaseUrl to include /studies already.

        var sb  = new StringBuilder($"{config.BaseUrl.TrimEnd('/')}{config.QidoPath}/{query.Level}");
        var qs  = new List<string>();
        if (!string.IsNullOrWhiteSpace(query.PatientId))         qs.Add($"PatientID={Uri.EscapeDataString(query.PatientId)}");
        if (!string.IsNullOrWhiteSpace(query.StudyDate))         qs.Add($"StudyDate={Uri.EscapeDataString(query.StudyDate)}");
        if (!string.IsNullOrWhiteSpace(query.StudyTime))         qs.Add($"StudyTime={Uri.EscapeDataString(query.StudyTime)}");
        if (!string.IsNullOrWhiteSpace(query.AccessionNumber))   qs.Add($"AccessionNumber={Uri.EscapeDataString(query.AccessionNumber)}");
        if (!string.IsNullOrWhiteSpace(query.StudyInstanceUid))  qs.Add($"StudyInstanceUID={Uri.EscapeDataString(query.StudyInstanceUid)}");
        if (!string.IsNullOrWhiteSpace(query.Modality))          qs.Add($"Modality={Uri.EscapeDataString(query.Modality)}");
        if (!string.IsNullOrWhiteSpace(query.ModalitiesInStudy)) qs.Add($"ModalitiesInStudy={Uri.EscapeDataString(query.ModalitiesInStudy)}");
        if (!string.IsNullOrWhiteSpace(query.IncludeField))      qs.Add($"includefield={Uri.EscapeDataString(query.IncludeField)}");
        qs.Add($"limit={query.Limit}"); qs.Add($"offset={query.Offset}");
        if (qs.Count > 0) sb.Append('?').Append(string.Join('&', qs));
        return sb.ToString();
    }

    /// <summary>Analiza una respuesta QIDO-RS de estudios. Devuelve los estudios leídos,
    /// cuántos elementos traía, cuántos se descartaron y, si el documento entero no es
    /// válido (HTML de un proxy, JSON roto, objeto en vez de lista), el motivo del error.
    /// Cada estudio se lee por separado: uno raro ya no corta la lectura de los demás.</summary>
    private (List<TesterStudyDtoWeb> studies, int total, int skipped, string? error) ParseQido(string json)
    {
        var studies = new List<TesterStudyDtoWeb>();
        int total = 0, skipped = 0;
        if (string.IsNullOrWhiteSpace(json)) return (studies, 0, 0, null);   // 204 / sin resultados
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return (studies, 0, 0, $"Respuesta QIDO-RS no válida: se esperaba una lista JSON y llegó {doc.RootElement.ValueKind}. {Snippet(json)}");

            foreach (var item in doc.RootElement.EnumerateArray())
            {
                total++;
                try
                {
                    var uid = GetVal(item, "0020000D");
                    if (string.IsNullOrEmpty(uid)) { skipped++; continue; }   // sin UID no se puede inventariar
                    studies.Add(new TesterStudyDtoWeb
                    {
                        PatientId         = GetVal(item, "00100020"),
                        PatientName       = GetName(item, "00100010"),
                        StudyDate         = GetVal(item, "00080020"),
                        AccessionNumber   = GetVal(item, "00080050"),
                        StudyInstanceUid  = uid,
                        ModalitiesInStudy = GetMultiVal(item, "00080061"),
                        StudyDescription  = GetVal(item, "00081030"),
                        NumberOfInstances = GetInt(item, "00201208"),  // NumberOfStudyRelatedInstances
                        NumberOfSeries    = GetInt(item, "00201206"),  // NumberOfStudyRelatedSeries
                        StudyTime         = GetVal(item, "00080030"),  // StudyTime
                        InstitutionName   = GetVal(item, "00080080"),  // InstitutionName
                        RetrieveAETitle   = GetVal(item, "00080054"),  // RetrieveAETitle
                        PatientBirthDate  = GetVal(item, "00100030"),  // PatientBirthDate
                        PatientSex        = GetVal(item, "00100040"),  // PatientSex
                        IssuerOfPatientId = GetVal(item, "00100021"),  // IssuerOfPatientID
                    });
                }
                catch (Exception ex)
                {
                    // Los lectores de abajo ya no lanzan por tipos inesperados; esto es
                    // defensa adicional para no perder el resto de la página.
                    skipped++;
                    logger.LogWarning(ex, "QIDO-RS: estudio {N} de la respuesta ilegible, se descarta", total);
                }
            }
        }
        catch (JsonException ex)
        {
            return (studies, total, skipped, $"Respuesta QIDO-RS no es JSON válido ({ex.Message}). {Snippet(json)}");
        }
        return (studies, total, skipped, null);
    }

    /// <summary>Comienzo del cuerpo para el mensaje de error (p. ej. "&lt;html&gt;…").</summary>
    private static string Snippet(string body)
    {
        var t = body.Trim().Replace('\r', ' ').Replace('\n', ' ');
        return t.Length <= 80 ? $"Inicio: «{t}»" : $"Inicio: «{t[..80]}…»";
    }

    /// <summary>Primer valor de un atributo como texto, sea cual sea su tipo JSON (un
    /// servidor puede mandar como número lo que el estándar define como cadena). Nunca
    /// lanza: antes GetString() sobre un número abortaba la lectura de toda la página.</summary>
    private static string? GetVal(JsonElement r, string tag)
    {
        if (!TryFirstValue(r, tag, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.GetRawText(),
            _                    => null,
        };
    }

    private static bool TryFirstValue(JsonElement r, string tag, out JsonElement first)
    {
        first = default;
        if (r.ValueKind != JsonValueKind.Object || !r.TryGetProperty(tag, out var el)
            || el.ValueKind != JsonValueKind.Object || !el.TryGetProperty("Value", out var v)
            || v.ValueKind != JsonValueKind.Array || v.GetArrayLength() == 0)
            return false;
        first = v[0];
        return true;
    }

    /// <summary>Atributo multivalor (p. ej. ModalitiesInStudy): todos los valores unidos
    /// con '\', igual que en DIMSE. GetVal solo devolvería el primero ("CT" de "CT\SR").</summary>
    private static string? GetMultiVal(JsonElement r, string tag)
    {
        if (r.ValueKind != JsonValueKind.Object || !r.TryGetProperty(tag, out var el)
            || el.ValueKind != JsonValueKind.Object || !el.TryGetProperty("Value", out var v)) return null;
        if (v.ValueKind != JsonValueKind.Array || v.GetArrayLength() == 0) return null;
        return string.Join('\\', v.EnumerateArray()
            .Where(x => x.ValueKind == JsonValueKind.String)
            .Select(x => x.GetString()));
    }

    /// <summary>Entero tolerante: número JSON o texto ("12"). Antes TryGetInt32 sobre un
    /// texto lanzaba (no es un "try" para el tipo), y la alternativa nunca se ejecutaba.</summary>
    private static int? GetInt(JsonElement r, string tag)
    {
        if (!TryFirstValue(r, tag, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i)) return i;
        if (v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), out var si)) return si;
        return null;
    }

    /// <summary>Nombre de persona: objeto PN {"Alphabetic": …} según el estándar, o una
    /// cadena simple, que algunos servidores envían.</summary>
    private static string? GetName(JsonElement r, string tag)
    {
        if (!TryFirstValue(r, tag, out var v)) return null;
        if (v.ValueKind == JsonValueKind.String) return v.GetString();
        if (v.ValueKind == JsonValueKind.Object && v.TryGetProperty("Alphabetic", out var a)
            && a.ValueKind == JsonValueKind.String) return a.GetString();
        return null;
    }
}
