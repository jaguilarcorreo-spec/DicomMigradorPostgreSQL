// ══════════════════════════════════════════════════════════════════════════════
//  NotificationModels.cs  (v227)
//  Notificaciones por correo. TODA la configuración vive en base de datos y se edita
//  desde la UI (/notificaciones) — nada en appsettings. Un evento de proceso (fin de
//  migración, auto-pausa, etc.) encola un correo en un OUTBOX que un servicio de fondo
//  vacía por SMTP con reintentos.
// ══════════════════════════════════════════════════════════════════════════════
namespace DicomMigrator.Core.Models;

/// <summary>Identificadores de los eventos que pueden disparar un correo.</summary>
public static class NotificationEvents
{
    public const string MigrationCompleted     = "MigrationCompleted";
    public const string MigrationFailed        = "MigrationFailed";
    public const string VerificationCompleted  = "VerificationCompleted";  // verificación OK
    public const string VerificationFailed     = "VerificationFailed";     // verificación con fallos
    public const string AutoPaused             = "AutoPaused";        // migración o verificación
    public const string DiscoveryCompleted     = "DiscoveryCompleted";
    public const string DiscoveryFailed    = "DiscoveryFailed";
    public const string CaptureFinished    = "CaptureFinished";   // enumeración Nivel 2
    public const string PopulateFinished   = "PopulateFinished";  // poblado desde inventario
}

/// <summary>Configuración de notificaciones. Fila ÚNICA (Id=1), como LicenseState.</summary>
public class NotificationSettings
{
    public int    Id { get; set; } = 1;

    public bool   Enabled { get; set; }

    // ── SMTP ──
    public string?  SmtpHost        { get; set; }
    public int      SmtpPort        { get; set; } = 587;
    public string?  SmtpUser        { get; set; }
    public string?  SmtpPassword    { get; set; }
    public bool     SmtpUseStartTls { get; set; } = true;
    public string?  FromAddress     { get; set; }

    /// <summary>Destinatarios separados por coma, punto y coma, espacio o salto de línea.</summary>
    public string?  Recipients      { get; set; }

    /// <summary>URL base de la app para los enlaces de los correos (p. ej. https://move:5200).</summary>
    public string?  BaseUrl         { get; set; }

    // ── Eventos activados ──
    public bool NotifyMigrationCompleted    { get; set; } = true;
    public bool NotifyMigrationFailed       { get; set; } = true;
    public bool NotifyVerificationCompleted { get; set; } = true;
    public bool NotifyVerificationFailed    { get; set; } = true;
    public bool NotifyAutoPaused            { get; set; } = true;
    public bool NotifyDiscoveryCompleted { get; set; } = true;
    public bool NotifyDiscoveryFailed    { get; set; } = true;
    public bool NotifyCaptureFinished    { get; set; }
    public bool NotifyPopulateFinished   { get; set; }

    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;

    /// <summary>¿Está activado este tipo de evento?</summary>
    public bool IsEventEnabled(string kind) => kind switch
    {
        NotificationEvents.MigrationCompleted    => NotifyMigrationCompleted,
        NotificationEvents.MigrationFailed       => NotifyMigrationFailed,
        NotificationEvents.VerificationCompleted => NotifyVerificationCompleted,
        NotificationEvents.VerificationFailed    => NotifyVerificationFailed,
        NotificationEvents.AutoPaused            => NotifyAutoPaused,
        NotificationEvents.DiscoveryCompleted => NotifyDiscoveryCompleted,
        NotificationEvents.DiscoveryFailed    => NotifyDiscoveryFailed,
        NotificationEvents.CaptureFinished    => NotifyCaptureFinished,
        NotificationEvents.PopulateFinished   => NotifyPopulateFinished,
        _ => false,
    };

    /// <summary>Lista de destinatarios normalizada.</summary>
    public IReadOnlyList<string> RecipientList() =>
        (Recipients ?? string.Empty)
            .Split(new[] { ',', ';', '\n', '\r', ' ', '\t' },
                   StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>¿Hay lo mínimo para poder enviar (host, remitente y algún destinatario)?</summary>
    public bool IsSmtpUsable() =>
        !string.IsNullOrWhiteSpace(SmtpHost)
        && !string.IsNullOrWhiteSpace(FromAddress)
        && RecipientList().Count > 0;
}

/// <summary>Correo encolado pendiente de envío (outbox persistido).</summary>
public class NotificationOutbox
{
    public long      Id             { get; set; }
    public DateTime  CreatedUtc     { get; set; } = DateTime.UtcNow;
    public string    EventKind      { get; set; } = string.Empty;
    public string    Recipients     { get; set; } = string.Empty;   // copia congelada al encolar
    public string    Subject        { get; set; } = string.Empty;
    public string    BodyHtml       { get; set; } = string.Empty;
    /// <summary>Pending | Sent | Failed.</summary>
    public string    Status         { get; set; } = "Pending";
    public int       Attempts       { get; set; }
    public string?   LastError      { get; set; }
    public DateTime? LastAttemptUtc { get; set; }
    public DateTime? SentUtc        { get; set; }
}
