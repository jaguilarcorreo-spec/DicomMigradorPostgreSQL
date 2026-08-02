// ══════════════════════════════════════════════════════════════════════════════
//  NotificationService.cs  (v230)
//  Encola eventos como correos (si están activados en la config de BD), los renderiza
//  a HTML y los envía por SMTP (MailKit) desde el outbox con reintentos y backoff.
//  Los correos NO incluyen datos de paciente: solo agregados e identificadores del
//  proceso (nombre de migración/job, conteos, estado).
//
//  Estilo (v230): cabecera con DEGRADADO por evento (con color sólido de reserva para
//  Outlook de escritorio), ICONO real de la app (la nube MOVE) incrustado como imagen
//  vía Content-ID (cid:movemark) — no SVG ni data-URI, porque Gmail los elimina/bloquea —,
//  pastilla de estado y tarjetas de KPIs con los conteos del proceso.
// ══════════════════════════════════════════════════════════════════════════════
using System.Net;
using DicomMigrator.Core.Interfaces;
using DicomMigrator.Core.Models;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using MimeKit;

namespace DicomMigrator.Infrastructure.Services.Notifications;

public class NotificationService(
    INotificationRepository repo,
    ILogger<NotificationService> logger) : INotificationService
{
    private const int MaxAttempts = 5;

    // Icono de la app (nube MOVE, blanco) en PNG, incrustado por CID en cada correo.
    // Generado desde el mismo trazado SVG de MoveLogo.razor. ~1 KB.
    private const string MoveMarkPngB64 = "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";
    private const string LogoCid = "movemark";

    // ── Encolar ────────────────────────────────────────────────────────────────
    public async Task RaiseAsync(string eventKind, string entityName,
        IReadOnlyList<(string Label, string Value)> facts,
        int? refId = null, string? refType = null,
        IReadOnlyList<(string Label, string Value)>? kpis = null,
        CancellationToken ct = default)
    {
        try
        {
            var s = await repo.GetSettingsAsync();
            if (!s.Enabled || !s.IsEventEnabled(eventKind)) return;

            var recipients = s.RecipientList();
            if (recipients.Count == 0) return;

            var (subject, body) = Render(eventKind, entityName, facts, kpis, s.BaseUrl, refType, refId);

            await repo.AddToOutboxAsync(new NotificationOutbox
            {
                EventKind  = eventKind,
                Recipients = string.Join(",", recipients),
                Subject    = subject,
                BodyHtml   = body,
            });
        }
        catch (Exception ex)
        {
            // Una notificación NUNCA debe tumbar el proceso que la origina.
            logger.LogWarning(ex, "No se pudo encolar la notificación {Kind} de '{Name}'.", eventKind, entityName);
        }
    }

    // ── Prueba manual (botón de la UI) ──────────────────────────────────────────
    public async Task<(bool ok, string? error)> SendTestAsync(CancellationToken ct = default)
    {
        var s = await repo.GetSettingsAsync();
        if (!s.IsSmtpUsable())
            return (false, "Configura al menos host SMTP, remitente y un destinatario.");

        var body = Wrap(
            headline: "Prueba de configuración",
            lead: "Si recibes este correo, las notificaciones de MOVE están bien configuradas.",
            gradient: "linear-gradient(135deg,#0077b6,#00b4d8)", solid: "#00b4d8",
            pill: "Prueba", link: null, cta: null, ctaTextColor: "#fff",
            kpis: [], facts: new (string, string)[] { ("Servidor", s.SmtpHost ?? "—"), ("Remitente", s.FromAddress ?? "—") });

        return await SendAsync(s, s.RecipientList(), "[MOVE] Correo de prueba", body, ct);
    }

    // ── Procesado del outbox (servicio de fondo) ────────────────────────────────
    public async Task ProcessOutboxAsync(CancellationToken ct = default)
    {
        var s = await repo.GetSettingsAsync();
        if (!s.Enabled || !s.IsSmtpUsable()) return;

        var pending = await repo.GetPendingAsync(MaxAttempts);
        var now = DateTime.UtcNow;

        foreach (var item in pending)
        {
            if (ct.IsCancellationRequested) break;

            // Backoff exponencial suave: no reintentar un fallido antes de tiempo.
            if (item.LastAttemptUtc is DateTime last)
            {
                var wait = TimeSpan.FromMinutes(Math.Min(item.Attempts, 10));
                if (now - last < wait) continue;
            }

            var to = item.Recipients.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var (ok, error) = await SendAsync(s, to, item.Subject, item.BodyHtml, ct);

            if (ok)
                await repo.MarkSentAsync(item.Id);
            else
            {
                var attempts = item.Attempts + 1;
                await repo.MarkAttemptAsync(item.Id, attempts, error ?? "Error desconocido", giveUp: attempts >= MaxAttempts);
            }
        }
    }

    // ── Envío SMTP (MailKit) ────────────────────────────────────────────────────
    private async Task<(bool ok, string? error)> SendAsync(
        NotificationSettings s, IEnumerable<string> to, string subject, string bodyHtml, CancellationToken ct)
    {
        try
        {
            var msg = new MimeMessage();
            msg.From.Add(MailboxAddress.Parse(s.FromAddress!));   // no-null garantizado por IsSmtpUsable()
            foreach (var addr in to)
                msg.To.Add(MailboxAddress.Parse(addr));
            msg.Subject = subject;

            // El icono de la cabecera viaja como recurso incrustado (Content-ID); el HTML
            // lo referencia con src="cid:movemark". Fiable en Gmail/Outlook/Apple Mail.
            var builder = new BodyBuilder { HtmlBody = bodyHtml };
            try
            {
                var logo = builder.LinkedResources.Add("movemark.png", Convert.FromBase64String(MoveMarkPngB64));
                logo.ContentId = LogoCid;
            }
            catch (Exception lex) { logger.LogWarning(lex, "No se pudo incrustar el icono en el correo (se envía sin él)."); }
            msg.Body = builder.ToMessageBody();

            using var client = new SmtpClient();
            var socketOption = s.SmtpUseStartTls ? SecureSocketOptions.StartTls : SecureSocketOptions.Auto;
            await client.ConnectAsync(s.SmtpHost, s.SmtpPort, socketOption, ct);
            if (!string.IsNullOrWhiteSpace(s.SmtpUser))
                await client.AuthenticateAsync(s.SmtpUser, s.SmtpPassword ?? string.Empty, ct);
            await client.SendAsync(msg, ct);
            await client.DisconnectAsync(true, ct);
            return (true, null);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Fallo al enviar correo de notificación a través de {Host}.", s.SmtpHost);
            return (false, ex.Message);
        }
    }

    // ── Render ──────────────────────────────────────────────────────────────────
    private static (string subject, string body) Render(
        string kind, string entityName,
        IReadOnlyList<(string Label, string Value)> facts,
        IReadOnlyList<(string Label, string Value)>? kpis,
        string? baseUrl, string? refType, int? refId)
    {
        // El primer elemento (emoji) se descarta: los asuntos van sin icono, por
        // petición. Se conserva en las ramas para no reordenar la tabla.
        var (_, g1, g2, solid, headline, pill, cta) = kind switch
        {
            NotificationEvents.MigrationCompleted    => ("✅", "#0077b6", "#22c55e", "#22c55e", "Migración completada",             "Migración",     "Ver la migración"),
            NotificationEvents.MigrationFailed       => ("⛔", "#7f1d1d", "#ef4444", "#ef4444", "Migración finalizada con fallos",   "Con fallos",    "Revisar los fallos"),
            NotificationEvents.VerificationCompleted => ("🔎", "#0077b6", "#22c55e", "#22c55e", "Verificación completada",           "Verificación",  "Ver la migración"),
            NotificationEvents.VerificationFailed    => ("⛔", "#7f1d1d", "#ef4444", "#ef4444", "Verificación finalizada con fallos","Con fallos",    "Revisar los fallos"),
            NotificationEvents.AutoPaused            => ("⏸", "#92400e", "#f59e0b", "#f59e0b", "Proceso auto-pausado",              "Auto-pausado",  "Ver estado"),
            NotificationEvents.DiscoveryCompleted    => ("🔍", "#0077b6", "#00b4d8", "#00b4d8", "Descubrimiento completado",         "Descubrimiento","Abrir el descubrimiento"),
            NotificationEvents.DiscoveryFailed       => ("⛔", "#7f1d1d", "#ef4444", "#ef4444", "Descubrimiento con error",          "Con error",     "Abrir el descubrimiento"),
            NotificationEvents.CaptureFinished       => ("🧬", "#5b21b6", "#a78bfa", "#a78bfa", "Enumeración de UIDs terminada",     "Enumeración",   "Abrir el descubrimiento"),
            NotificationEvents.PopulateFinished      => ("📥", "#5b21b6", "#a78bfa", "#a78bfa", "Poblado desde inventario terminado","Poblado",       "Ver la migración"),
            _                                        => ("•", "#0077b6", "#00b4d8", "#0077b6", "Aviso",                             "Aviso",         "Abrir MOVE"),
        };

        var subject = $"[MOVE] {headline} — «{entityName}»";

        string? link = null;
        if (!string.IsNullOrWhiteSpace(baseUrl))
        {
            var b = baseUrl!.TrimEnd('/');
            link = refType switch
            {
                "migration" => $"{b}/migraciones",
                "discovery" => $"{b}/discovery",
                _           => b,
            };
        }

        var gradient = $"linear-gradient(135deg,{g1},{g2})";
        var ctaTextColor = solid == "#f59e0b" ? "#3a2a06" : "#fff";

        var body = Wrap(headline, $"«{entityName}»", gradient, solid, pill,
            link, link is null ? null : cta, ctaTextColor,
            kpis ?? [], facts);
        return (subject, body);
    }

    /// <summary>Plantilla HTML común de los correos (tema claro, estilo del mock: cabecera en
    /// degradado con el icono de la app, pastilla de estado, tarjetas de KPIs y tabla de detalle).</summary>
    private static string Wrap(string headline, string lead, string gradient, string solid, string pill,
        string? link, string? cta, string ctaTextColor,
        IReadOnlyList<(string Label, string Value)> kpis,
        IReadOnlyList<(string Label, string Value)> facts)
    {
        const string mono = "'IBM Plex Mono',ui-monospace,Consolas,monospace";

        // Tarjetas de KPIs (una fila de celdas iguales; tabla para que Outlook la respete).
        var kpiBlock = "";
        if (kpis.Count > 0)
        {
            var cells = string.Concat(kpis.Select(k =>
                "<td valign=\"top\" style=\"padding:0 4px\">" +
                "<div style=\"background:#f4f7fb;border:1px solid #e2e8f0;border-radius:8px;padding:10px 6px;text-align:center\">" +
                $"<div style=\"font-size:10px;color:#64748b;text-transform:uppercase;letter-spacing:.05em\">{Html(k.Label)}</div>" +
                $"<div style=\"font-family:{mono};font-size:18px;font-weight:700;color:#1e2d42;margin-top:2px\">{Html(k.Value)}</div>" +
                "</div></td>"));
            kpiBlock = $"<table width=\"100%\" cellpadding=\"0\" cellspacing=\"0\" style=\"margin-bottom:16px\"><tr>{cells}</tr></table>";
        }

        // Tabla de detalle (label → valor).
        var rowsBlock = "";
        if (facts.Count > 0)
        {
            var rows = string.Concat(facts.Select(f =>
                $"<tr><td style=\"padding:5px 0;border-bottom:1px solid #eef2f7;color:#64748b;width:42%\">{Html(f.Label)}</td>" +
                $"<td style=\"padding:5px 0;border-bottom:1px solid #eef2f7;color:#334155\">{Html(f.Value)}</td></tr>"));
            rowsBlock = $"<table style=\"width:100%;border-collapse:collapse;font-size:12px;margin-bottom:16px\">{rows}</table>";
        }

        var button = (link is null || cta is null) ? "" :
            $"<a href=\"{Html(link)}\" style=\"display:inline-block;background:{solid};color:{ctaTextColor};" +
            $"text-decoration:none;font-weight:600;font-size:12px;padding:9px 16px;border-radius:7px\">{Html(cta)} →</a>";

        return $@"<!doctype html><html><body style=""margin:0;background:#eef2f7;font-family:Segoe UI,Arial,sans-serif"">
<div style=""max-width:600px;margin:0 auto;border:1px solid #cbd5e1;border-radius:12px;overflow:hidden"">
  <div style=""background:{solid};background:{gradient};padding:16px 20px"">
    <table width=""100%"" cellpadding=""0"" cellspacing=""0""><tr>
      <td width=""34"" valign=""middle"">
        <div style=""width:34px;height:34px;border-radius:8px;background:rgba(255,255,255,.16);text-align:center;line-height:34px"">
          <img src=""cid:{LogoCid}"" width=""22"" height=""22"" alt=""MOVE"" style=""vertical-align:middle;display:inline-block"" />
        </div>
      </td>
      <td valign=""middle"" style=""padding-left:12px"">
        <div style=""color:#fff;font-weight:700;font-size:15px;letter-spacing:.04em"">MOVE</div>
        <div style=""color:#fff;opacity:.85;font-size:10px;font-family:{mono}"">DICOM data migrator</div>
      </td>
      <td align=""right"" valign=""middle"">
        <span style=""display:inline-block;padding:3px 10px;border-radius:99px;font-size:11px;font-weight:600;background:rgba(255,255,255,.2);color:#fff"">{Html(pill)}</span>
      </td>
    </tr></table>
  </div>
  <div style=""background:#fff;color:#1e2d42;padding:20px"">
    <h3 style=""margin:0 0 4px;font-size:16px"">{Html(headline)}</h3>
    <p style=""color:#5a6b82;font-size:12.5px;margin:0 0 16px"">{Html(lead)}</p>
    {kpiBlock}
    {rowsBlock}
    {button}
  </div>
  <div style=""background:#f4f7fb;color:#94a3b8;font-size:10px;padding:12px 20px;border-top:1px solid #e2e8f0"">
    Aviso automático de MOVE. No contiene datos de paciente. Configúralos o desactívalos en Configuración → Notificaciones.
  </div>
</div></body></html>";
    }

    private static string Html(string s) => WebUtility.HtmlEncode(s ?? string.Empty);
}
