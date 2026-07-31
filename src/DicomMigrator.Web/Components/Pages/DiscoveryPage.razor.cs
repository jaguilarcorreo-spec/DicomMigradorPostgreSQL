using DicomMigrator.Core.Models;

namespace DicomMigrator.Web.Components.Pages;

/// <summary>
/// Code-behind for DiscoveryPage.razor.
/// Static and helper methods are here to avoid Razor compiler issues
/// with static methods and comparison operators inside @code blocks.
/// </summary>
public partial class DiscoveryPage
{
    // ── Time formatting ──────────────────────────────────────────────────────
    private static string FormatElapsed(double totalSeconds)
    {
        var h   = (int)(totalSeconds / 3600);
        var m   = (int)((totalSeconds % 3600) / 60);
        var s   = (int)(totalSeconds % 60);
        if (h > 0)   return $"{h}h {m}m";
        if (m > 0)   return $"{m}m {s}s";
        return $"{s}s";
    }

    // ── Markup helpers (avoid Razor parser issues with lambdas/comparisons) ──
    private IEnumerable<DicomNode> DestCandidates()
    {
        if (_detail is null) return _nodes;
        var sourceId = _detail.SourcePacsId;
        return _nodes.Where(n => n.Id != sourceId);
    }

    private bool ShowDaysPreview()
    {
        if (_fStart == default || _fEnd == default) return false;
        return _fEnd.CompareTo(_fStart) >= 0;
    }

    private int DaysCount() => (_fEnd.DayNumber - _fStart.DayNumber) + 1;

    // ── Pagination button states (helpers avoid inline comparisons in markup) ──
    private bool FirstPageDisabled() => _partPage <= 1;
    private bool LastPageDisabled()  => _partPage >= _partTotalPages;

    // ── Auto-refresh timer ───────────────────────────────────────────────────
    private void StartAutoRefresh()
    {
        StopAutoRefresh();
        _timer = new System.Threading.Timer(
            _state => _ = OnTimerTickAsync(),
            null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
    }

    // El Timer dispara en un hilo del pool cada 2 s. Antes el callback era
    // 'async _ => await RefreshDetailAsync()', es decir async void sin protección:
    //   · si el usuario volvía a la lista o se borraba el job, _detail pasaba a null
    //     mientras un tick ya estaba en vuelo → NullReferenceException;
    //   · al ser un hilo suelto, esa excepción NO la capturaba Blazor y tumbaba el
    //     proceso (rompía en el depurador).
    // Ahora se ejecuta a través de InvokeAsync, con lo que corre en el dispatcher del
    // componente y queda SERIALIZADO con el resto de manejadores (volver a la lista,
    // borrar, iniciar…): ya no puede solaparse con ellos. Y va envuelto en try/catch:
    // un tick puntual que falle se registra y se ignora, el siguiente lo reintenta.
    private async Task OnTimerTickAsync()
    {
        try
        {
            await InvokeAsync(RefreshDetailAsync);
        }
        catch (ObjectDisposedException)
        {
            // El componente ya se desechó (navegación). Nada que hacer.
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "Auto-refresco del detalle de descubrimiento falló (tick transitorio, se ignora).");
        }
    }

    private void StopAutoRefresh()
    {
        _timer?.Dispose();
        _timer = null;
    }

    public void Dispose() => StopAutoRefresh();
}
