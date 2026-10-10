using DicomMigrator.Core.Interfaces;
using DicomMigrator.Core.Models;
using DicomMigrator.Infrastructure.Services.Licensing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

// Alias explícito para evitar ambigüedad entre el tipo Migration y el namespace
// DicomMigrator.Infrastructure.Services.Migration en el que vive este fichero.
using MigrationEntity = DicomMigrator.Core.Models.Migration;

namespace DicomMigrator.Infrastructure.Services.Migration;

// ══════════════════════════════════════════════════════════════════════════════
// BACKOFF HELPER
// ══════════════════════════════════════════════════════════════════════════════

internal static class ConnBackoff
{
    /// <summary>Consecutive connection errors before a process auto-pauses. Kept low
    /// so the pause fires within ~1 minute of the PACS going down, not 20.</summary>
    public const int AutoPauseThreshold = 5;

    /// <summary>Short fixed wait between connection errors while counting toward the
    /// auto-pause threshold. Fixed (not exponential) so reaching the threshold is fast.</summary>
    public static readonly TimeSpan PrePauseWait = TimeSpan.FromSeconds(10);

    // El contador de errores es POR WORKER, así que varios pueden cruzar el umbral
    // en el mismo instante y cada uno anunciaría la pausa: en el log aparecía seis
    // veces algo que ocurrió una. Este guardián deja pasar solo al primero.
    // TryAdd es atómico, así que no hay carrera aunque lleguen simultáneamente.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _pauseAnnounced = new();

    /// <summary>Devuelve true solo para el primer worker que alcanza el umbral.
    /// Los demás cancelan y salen en silencio.</summary>
    public static bool TryAnnouncePause(string process, int migrationId)
        => _pauseAnnounced.TryAdd($"{process}:{migrationId}", 0);

    /// <summary>Rearma el aviso. Se llama al (re)arrancar el proceso, para que una
    /// pausa posterior vuelva a registrarse.</summary>
    public static void ResetPauseAnnouncement(string process, int migrationId)
        => _pauseAnnounced.TryRemove($"{process}:{migrationId}", out _);

    /// <summary>Exponential backoff for consecutive connection errors: 10s, 20s,
    /// 40s, 80s... capped at 5 minutes. consecutiveErrors starts at 1.</summary>
    public static TimeSpan ForAttempt(int consecutiveErrors)
    {
        var n = Math.Max(1, consecutiveErrors);
        // 10s * 2^(n-1), capped at 300s
        var seconds = Math.Min(300d, 10d * Math.Pow(2, n - 1));
        return TimeSpan.FromSeconds(seconds);
    }

    /// <summary>True si es un fallo PASAJERO de la base de datos propia, que se arregla
    /// esperando: a diferencia de IsDatabaseError, excluye los errores de datos.</summary>
    public static bool IsTransientDatabaseError(Exception ex)
    {
        // Solo lo que se arregla solo: conexión perdida o rechazada, PostgreSQL reiniciándose
        // (Npgsql lo marca IsTransient) o reintentos de EF agotados por eso mismo. NO los
        // errores de datos (clave duplicada, valor demasiado largo…): reintentarlos no los
        // arregla y una partición así se repetiría para siempre (DISC-4).
        for (var e = ex; e is not null; e = e.InnerException)
        {
            if (e is Npgsql.NpgsqlException { IsTransient: true }
                || e is Microsoft.EntityFrameworkCore.Storage.RetryLimitExceededException)
                return true;
        }
        return false;
    }

    /// <summary>True si la excepción (o alguna interna) viene de la base de datos propia:
    /// conexión con PostgreSQL perdida, reintentos de Npgsql agotados, fallo al guardar.
    /// Estas NO son errores del PACS ni del estudio: los bloques que clasifican fallos de
    /// DICOM deben dejarlas pasar al bucle del worker, que espera con backoff y reintenta
    /// sin contar hacia la auto-pausa ni gastar reintentos del estudio.</summary>
    public static bool IsDatabaseError(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            if (e is System.Data.Common.DbException
                || e is Microsoft.EntityFrameworkCore.Storage.RetryLimitExceededException
                || e is Microsoft.EntityFrameworkCore.DbUpdateException)
                return true;
        }
        return false;
    }
}
