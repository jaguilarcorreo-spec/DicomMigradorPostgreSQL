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
    public int     Limit             { get; set; } = 100;
    public int     Offset            { get; set; } = 0;
    public string? IncludeField      { get; set; }
}

public class TesterQidoResult
{
    public bool   Success      { get; set; }
    public int    HttpStatus   { get; set; }
    public long   DurationMs   { get; set; }
    public int    ResultCount  { get; set; }
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
                var response = await client.SendAsync(request, ct);
                sw.Stop();
                result.DurationMs  = sw.ElapsedMilliseconds;
                result.HttpStatus  = (int)response.StatusCode;
                result.Success     = response.IsSuccessStatusCode;
                foreach (var h in response.Headers) result.ResponseHeaders[h.Key] = string.Join(", ", h.Value);

                if (response.IsSuccessStatusCode)
                {
                    result.RawJson    = await response.Content.ReadAsStringAsync(ct);
                    result.Studies    = ParseQido(result.RawJson);
                    result.ResultCount = result.Studies.Count;
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
                var response = await client.SendAsync(request, ct);
                sw.Stop();
                result.DurationMs = sw.ElapsedMilliseconds;
                result.HttpStatus = (int)response.StatusCode;
                if (response.IsSuccessStatusCode)
                {
                    var json = await response.Content.ReadAsStringAsync(ct);
                    result.Instances = ParseInstancesWeb(json);
                    result.Success = true;
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
        catch (TaskCanceledException)
        { sw.Stop(); result.DurationMs = sw.ElapsedMilliseconds; result.Success = false; result.ErrorMessage = $"Timeout tras {config.HttpTimeoutSeconds}s"; }
        catch (Exception ex)
        { sw.Stop(); result.DurationMs = sw.ElapsedMilliseconds; result.Success = false; result.ErrorMessage = ex.Message; }
        return result;
    }

    private static List<TesterWebInstance> ParseInstancesWeb(string json)
    {
        var list = new List<TesterWebInstance>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                list.Add(new TesterWebInstance
                {
                    SopInstanceUid    = GetVal(item, "00080018"),
                    SeriesInstanceUid = GetVal(item, "0020000E"),
                });
            }
        }
        catch { }
        return list;
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
        if (!string.IsNullOrWhiteSpace(query.AccessionNumber))   qs.Add($"AccessionNumber={Uri.EscapeDataString(query.AccessionNumber)}");
        if (!string.IsNullOrWhiteSpace(query.StudyInstanceUid))  qs.Add($"StudyInstanceUID={Uri.EscapeDataString(query.StudyInstanceUid)}");
        if (!string.IsNullOrWhiteSpace(query.Modality))          qs.Add($"Modality={Uri.EscapeDataString(query.Modality)}");
        if (!string.IsNullOrWhiteSpace(query.ModalitiesInStudy)) qs.Add($"ModalitiesInStudy={Uri.EscapeDataString(query.ModalitiesInStudy)}");
        if (!string.IsNullOrWhiteSpace(query.IncludeField))      qs.Add($"includefield={Uri.EscapeDataString(query.IncludeField)}");
        qs.Add($"limit={query.Limit}"); qs.Add($"offset={query.Offset}");
        if (qs.Count > 0) sb.Append('?').Append(string.Join('&', qs));
        return sb.ToString();
    }

    private static List<TesterStudyDtoWeb> ParseQido(string json)
    {
        var studies = new List<TesterStudyDtoWeb>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                studies.Add(new TesterStudyDtoWeb
                {
                    PatientId         = GetVal(item, "00100020"),
                    PatientName       = GetName(item, "00100010"),
                    StudyDate         = GetVal(item, "00080020"),
                    AccessionNumber   = GetVal(item, "00080050"),
                    StudyInstanceUid  = GetVal(item, "0020000D"),
                    ModalitiesInStudy = GetVal(item, "00080061"),
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
        }
        catch { }
        return studies;
    }

    private static string? GetVal(JsonElement r, string tag) =>
        r.TryGetProperty(tag, out var el) && el.TryGetProperty("Value", out var v) &&
        v.ValueKind == JsonValueKind.Array && v.GetArrayLength() > 0 ? v[0].GetString() : null;

    private static int? GetInt(JsonElement r, string tag)
    {
        if (!r.TryGetProperty(tag, out var el) || !el.TryGetProperty("Value", out var v)) return null;
        if (v.ValueKind != JsonValueKind.Array || v.GetArrayLength() == 0) return null;
        if (v[0].TryGetInt32(out var i)) return i;
        if (v[0].ValueKind == JsonValueKind.String && int.TryParse(v[0].GetString(), out var si)) return si;
        return null;
    }

    private static string? GetName(JsonElement r, string tag) =>
        r.TryGetProperty(tag, out var el) && el.TryGetProperty("Value", out var v) &&
        v.ValueKind == JsonValueKind.Array && v.GetArrayLength() > 0 &&
        v[0].TryGetProperty("Alphabetic", out var a) ? a.GetString() : null;
}
