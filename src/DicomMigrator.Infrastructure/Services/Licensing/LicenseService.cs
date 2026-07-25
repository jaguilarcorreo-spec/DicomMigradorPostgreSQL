using System.Globalization;
using DicomMigrator.Core.Interfaces;
using DicomMigrator.Core.Models;
using Microsoft.Extensions.Logging;

namespace DicomMigrator.Infrastructure.Services.Licensing;

/// <summary>
/// Orquesta la licencia: verificación de firma (LicenseTokenVerifier) + ventana
/// temporal + binding de máquina + anti-rollback contra PostgreSQL (LicenseState).
///
/// Anti-rollback en dos frentes:
///   - Serial: no se admite un token con serial menor que el mayor ya activado
///     (HighestSerial), para que no se pueda reinstalar una licencia antigua.
///   - Reloj: LastSeenUtc es una marca de agua del tiempo observado; el tiempo
///     efectivo para evaluar la caducidad nunca baja de ella, así que retrasar el
///     reloj del sistema no revive una licencia caducada.
/// </summary>
public sealed class LicenseService(
    ILicenseStateRepository stateRepo,
    IMachineFingerprintProvider fpProvider,
    LicenseStatusCache cache,
    LicenseOptions options,
    ILogger<LicenseService> logger) : ILicenseService
{
    /// <summary>Holgura antes de avisar de un retraso de reloj.</summary>
    private static readonly TimeSpan ClockSkew = TimeSpan.FromHours(48);

    public LicenseStatusSnapshot Current => cache.Current;

    public async Task<LicenseStatusSnapshot> EvaluateAsync(CancellationToken ct = default)
    {
        try
        {
            var state = await stateRepo.GetAsync();

            // Bootstrap: sin token en BD → importar de fichero configurado (License:Path).
            if (string.IsNullOrWhiteSpace(state.Token)
                && !string.IsNullOrWhiteSpace(options.FilePath)
                && File.Exists(options.FilePath))
            {
                try
                {
                    var fileToken = (await File.ReadAllTextAsync(options.FilePath, ct)).Trim();
                    logger.LogInformation("Importando licencia del fichero {Path}.", options.FilePath);
                    var (ok, err, snap) = await InstallAsync(fileToken, "bootstrap-file", ct);
                    if (!ok) logger.LogWarning("La licencia del fichero no se pudo instalar: {Err}", err);
                    return snap;
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "No se pudo leer la licencia del fichero {Path}.", options.FilePath);
                }
            }

            var snapshot = EvaluateToken(state.Token, state, out var effectiveNow, out var payload);

            // Persistir marca de agua del reloj y datos derivados si es válida.
            state.LastSeenUtc = effectiveNow > state.LastSeenUtc ? effectiveNow : state.LastSeenUtc;
            if (snapshot.IsValid && payload is not null)
            {
                state.LicId           = payload.LicId;
                state.ActivatedSerial = payload.Serial;
                if (payload.Serial > state.HighestSerial) state.HighestSerial = payload.Serial;
                state.ExpiresUtc      = ParseUtc(payload.ExpiresUtc);
            }
            await stateRepo.SaveAsync(state);

            cache.Set(snapshot);
            return snapshot;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error evaluando la licencia.");
            var snap = new LicenseStatusSnapshot
            {
                Verdict       = LicenseVerdict.Error,
                Reason        = ex.Message,
                ThisMachineFp = SafeFp(),
            };
            cache.Set(snap);
            return snap;
        }
    }

    public async Task<(bool Ok, string? Error, LicenseStatusSnapshot Snapshot)> InstallAsync(
        string token, string? actor = null, CancellationToken ct = default)
    {
        var state    = await stateRepo.GetAsync();
        var snapshot = EvaluateToken(token, state, out var effectiveNow, out var payload);

        // Solo se instala si es utilizable AHORA (firma+producto+ventana+binding) y no es
        // un rollback. Un token válido pero caducado o de otra máquina no se activa.
        if (!snapshot.IsValid || payload is null)
            return (false, snapshot.Reason, snapshot);

        state.Token           = token.Trim();
        state.LicId           = payload.LicId;
        state.ActivatedSerial = payload.Serial;
        if (payload.Serial > state.HighestSerial) state.HighestSerial = payload.Serial;
        state.ExpiresUtc      = ParseUtc(payload.ExpiresUtc);
        state.ActivatedUtc    = DateTime.UtcNow;
        state.LastSeenUtc     = effectiveNow > state.LastSeenUtc ? effectiveNow : state.LastSeenUtc;
        await stateRepo.SaveAsync(state);

        logger.LogInformation("Licencia instalada por {Actor}: cliente '{C}', serie {S}, caduca {Exp}.",
            actor ?? "desconocido", payload.Customer, payload.Serial,
            state.ExpiresUtc?.ToString("yyyy-MM-dd") ?? "nunca");

        cache.Set(snapshot);
        return (true, null, snapshot);
    }

    // ── Núcleo de evaluación (sin efectos secundarios de BD) ──────────────────
    private LicenseStatusSnapshot EvaluateToken(
        string? token, LicenseState state, out DateTime effectiveNow, out LicensePayload? payload)
    {
        var thisFp = SafeFp();
        var now    = DateTime.UtcNow;

        // Tiempo efectivo: nunca por debajo de la marca de agua (anti clock-rollback).
        effectiveNow = now >= state.LastSeenUtc ? now : state.LastSeenUtc;
        if (now < state.LastSeenUtc - ClockSkew)
            logger.LogWarning("Reloj retrasado respecto a la última evaluación ({Now:o} < {Seen:o}). " +
                "Se usa la marca de agua para evaluar la caducidad.", now, state.LastSeenUtc);

        if (!LicenseTokenVerifier.TryVerify(token, out payload, out var verdict, out var error))
            return Snap(verdict, error, payload, thisFp);

        var p = payload!;

        var notBefore = ParseUtc(p.NotBeforeUtc);
        if (notBefore is { } nb && effectiveNow < nb)
            return Snap(LicenseVerdict.NotYetValid,
                $"La licencia aún no es válida (desde {nb:yyyy-MM-dd}).", p, thisFp);

        var expires = ParseUtc(p.ExpiresUtc);
        if (expires is { } ex && effectiveNow >= ex)
            return Snap(LicenseVerdict.Expired, $"La licencia caducó el {ex:yyyy-MM-dd}.", p, thisFp);

        if (!string.IsNullOrWhiteSpace(p.MachineFp)
            && !string.Equals(p.MachineFp.Trim(), thisFp, StringComparison.OrdinalIgnoreCase))
            return Snap(LicenseVerdict.MachineMismatch,
                "La licencia está ligada a otra máquina (el fingerprint no coincide).", p, thisFp);

        if (p.Serial < state.HighestSerial)
            return Snap(LicenseVerdict.Rollback,
                $"Licencia más antigua que la ya activada (serie {p.Serial} < {state.HighestSerial}).", p, thisFp);

        return Snap(LicenseVerdict.Valid, $"Licencia válida para '{p.Customer}'.", p, thisFp);
    }

    private static LicenseStatusSnapshot Snap(LicenseVerdict v, string reason, LicensePayload? p, string? thisFp) => new()
    {
        Verdict       = v,
        Reason        = reason,
        Customer      = p?.Customer,
        Edition       = p?.Edition,
        ExpiresUtc    = ParseUtc(p?.ExpiresUtc),
        MachineFp     = p?.MachineFp,
        Serial        = p?.Serial ?? 0,
        Features      = p?.Features ?? Array.Empty<string>(),
        ThisMachineFp = thisFp,
        EvaluatedUtc  = DateTime.UtcNow,
    };

    private static DateTime? ParseUtc(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        return DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture,
                   DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dto)
            ? dto.UtcDateTime
            : null;
    }

    private string? SafeFp()
    {
        try { return fpProvider.GetFingerprint(); }
        catch { return null; }
    }
}
