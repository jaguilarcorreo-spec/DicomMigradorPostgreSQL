using System.Collections.Concurrent;

namespace DicomMigrator.Infrastructure.Services.Migration;

/// <summary>
/// Incidentes de auto-pausa por conexión, por proceso ("MIGRATE" / "VERIFY") y migración
/// (CONC-6). Un incidente empieza con la primera auto-pausa y termina cuando el proceso
/// vuelve a funcionar de verdad (un estudio migrado o una verificación que llega al PACS).
///
/// Sirve para dos cosas:
///   · Un correo por incidente. Antes cada ciclo «auto-pausa → auto-reanudación → vuelve
///     a fallar» enviaba otro aviso: con un nodo caído toda la noche, decenas de correos.
///   · Espaciar las reanudaciones que vuelven a fallar. Si el sondeo dice que el nodo
///     responde pero el proceso sigue fallando (PACS saturado, servicio DICOMweb caído con
///     el puerto DICOM vivo…), cada nueva reanudación espera el doble: 1, 2, 4… hasta 30 min.
///
/// En memoria: un reinicio del servicio empieza de cero (como mucho, un correo más).
/// </summary>
public static class AutoPauseIncidents
{
    private sealed record State(int Pauses, DateTime PausedAtUtc);

    private static readonly ConcurrentDictionary<string, State> _open = new();

    private static readonly TimeSpan MaxResumeDelay = TimeSpan.FromMinutes(30);

    private static string Key(string process, int migrationId) => $"{process}:{migrationId}";

    /// <summary>Registra una auto-pausa. True si abre un incidente nuevo (hay que avisar);
    /// false si el incidente ya estaba abierto (reanudación que ha vuelto a fallar).</summary>
    public static bool RegisterPause(string process, int migrationId)
    {
        var isNew = true;
        _open.AddOrUpdate(Key(process, migrationId),
            _ => new State(1, DateTime.UtcNow),
            (_, s) => { isNew = false; return new State(s.Pauses + 1, DateTime.UtcNow); });
        return isNew;
    }

    /// <summary>El proceso ha vuelto a funcionar: cierra el incidente.</summary>
    public static void Resolve(string process, int migrationId)
        => _open.TryRemove(Key(process, migrationId), out _);

    /// <summary>Auto-pausas seguidas del incidente abierto (0 si no hay).</summary>
    public static int Pauses(string process, int migrationId)
        => _open.TryGetValue(Key(process, migrationId), out var s) ? s.Pauses : 0;

    /// <summary>True si ya se puede intentar la auto-reanudación. Tras la primera pausa, en
    /// cuanto el nodo responda; tras cada pausa más del mismo incidente, se espera el doble
    /// desde la última (1, 2, 4, 8, 16 y 30 min como máximo).</summary>
    public static bool ReadyToResume(string process, int migrationId, out TimeSpan wait)
    {
        wait = TimeSpan.Zero;
        if (!_open.TryGetValue(Key(process, migrationId), out var s) || s.Pauses <= 1) return true;

        var delay = TimeSpan.FromMinutes(Math.Min(MaxResumeDelay.TotalMinutes, Math.Pow(2, s.Pauses - 2)));
        wait = s.PausedAtUtc + delay - DateTime.UtcNow;
        return wait <= TimeSpan.Zero;
    }
}
