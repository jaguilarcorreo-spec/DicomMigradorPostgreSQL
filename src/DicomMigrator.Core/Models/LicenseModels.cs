using System.Text.Json.Serialization;

namespace DicomMigrator.Core.Models;

// ══════════════════════════════════════════════════════════════════════════════
// LICENCIAS — modelos
//
// Un token de licencia tiene la forma  DMLIC1.<payload_b64url>.<firma_b64url>.
// Se firman/verifican los BYTES EXACTOS del payload que viajan en el token, así
// que el validador nunca vuelve a serializar el JSON (no hay que reproducir la
// serialización de Python en C#). El payload se decodifica SOLO para leerlo, y su
// forma la describe LicensePayload.
//
// Diseño alineado con el generador Python (licensing/*.py) y con la spec
// DicomMigrator_Licencias.md.
// ══════════════════════════════════════════════════════════════════════════════

/// <summary>Contenido (claims) de una licencia, tal como los emite el generador.</summary>
public sealed class LicensePayload
{
    [JsonPropertyName("lic_id")]         public string  LicId        { get; set; } = "";
    [JsonPropertyName("product")]        public string  Product      { get; set; } = "";
    [JsonPropertyName("edition")]        public string  Edition      { get; set; } = "";
    [JsonPropertyName("customer")]       public string  Customer     { get; set; } = "";
    [JsonPropertyName("issued_utc")]     public string? IssuedUtc    { get; set; }
    [JsonPropertyName("not_before_utc")] public string? NotBeforeUtc { get; set; }
    /// <summary>ISO-8601 UTC, o null para licencia perpetua.</summary>
    [JsonPropertyName("expires_utc")]    public string? ExpiresUtc   { get; set; }
    /// <summary>Fingerprint de máquina al que se liga la licencia, o null = sin ligar.</summary>
    [JsonPropertyName("machine_fp")]     public string? MachineFp    { get; set; }
    [JsonPropertyName("seats")]          public int     Seats        { get; set; }
    [JsonPropertyName("features")]       public string[] Features    { get; set; } = System.Array.Empty<string>();
    /// <summary>Entero monotónico para el anti-rollback. Ver LicenseState.HighestSerial.</summary>
    [JsonPropertyName("serial")]         public long    Serial       { get; set; }
    [JsonPropertyName("notes")]          public string? Notes        { get; set; }
}

/// <summary>
/// Estado persistente de la licencia en PostgreSQL — FILA ÚNICA (Id=1). Es el
/// ancla del anti-rollback: guarda el serial más alto jamás activado y una marca
/// de agua del reloj (para que retrasar la hora del sistema no reviva una licencia
/// caducada).
/// </summary>
public class LicenseState
{
    /// <summary>Siempre 1: hay una sola fila de estado de licencia.</summary>
    public int       Id              { get; set; } = 1;

    /// <summary>Token instalado actualmente (o null si no hay ninguno).</summary>
    public string?   Token           { get; set; }

    public string?   LicId           { get; set; }

    /// <summary>Serial más alto que se ha activado alguna vez. Anti-rollback: no se
    /// admite instalar un token con serial menor que este.</summary>
    public long      HighestSerial   { get; set; }

    /// <summary>Serial del token actualmente activo.</summary>
    public long      ActivatedSerial { get; set; }

    public System.DateTime? ExpiresUtc   { get; set; }
    public System.DateTime? ActivatedUtc { get; set; }

    /// <summary>Marca de agua del reloj: mayor instante que la app ha observado.
    /// Si "ahora" &lt; LastSeenUtc, el reloj se ha retrasado (posible evasión de
    /// caducidad) y se usa LastSeenUtc como tiempo efectivo para evaluar la ventana.</summary>
    public System.DateTime  LastSeenUtc  { get; set; } = System.DateTime.UtcNow;

    public System.DateTime  UpdatedUtc   { get; set; } = System.DateTime.UtcNow;
}

/// <summary>Veredicto de la evaluación de una licencia.</summary>
public enum LicenseVerdict
{
    Unknown = 0,
    Valid,
    Missing,           // no hay token instalado
    Malformed,         // no tiene la forma DMLIC1.<payload>.<firma> / no decodifica
    BadSignature,      // la firma Ed25519 no verifica con la clave embebida
    WrongProduct,      // product != "MOVE"
    NotYetValid,       // now < not_before
    Expired,           // now >= expires
    MachineMismatch,   // machine_fp no coincide con el fingerprint de esta máquina
    Rollback,          // serial menor que el mayor ya activado (licencia antigua)
    Error,             // fallo inesperado al evaluar
}

/// <summary>Foto inmutable del estado de licencia, cacheada para lecturas baratas
/// desde la UI y como verja de las migraciones.</summary>
public sealed class LicenseStatusSnapshot
{
    public LicenseVerdict Verdict { get; init; } = LicenseVerdict.Unknown;

    public bool   IsValid    => Verdict == LicenseVerdict.Valid;
    /// <summary>Puerta de las migraciones. Hoy = IsValid (sin periodo de gracia).</summary>
    public bool   CanMigrate => IsValid;

    public string Reason  { get; init; } = "Licencia no evaluada todavía.";

    public string?  Customer   { get; init; }
    public string?  Edition    { get; init; }
    public System.DateTime? ExpiresUtc { get; init; }
    public string?  MachineFp  { get; init; }   // fp exigido por la licencia (o null)
    public long     Serial     { get; init; }
    public string[] Features   { get; init; } = System.Array.Empty<string>();

    /// <summary>Fingerprint de ESTA máquina (para mostrarlo en la UI).</summary>
    public string?  ThisMachineFp { get; init; }

    public System.DateTime EvaluatedUtc { get; init; } = System.DateTime.UtcNow;

    public static LicenseStatusSnapshot Unknown() => new()
    {
        Verdict = LicenseVerdict.Unknown,
        Reason  = "Licencia no evaluada todavía.",
    };
}
