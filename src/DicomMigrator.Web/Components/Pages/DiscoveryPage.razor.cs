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
            null, DetailRefreshEvery, DetailRefreshEvery);
    }

    // ── Carga del refresco (BD-5) ────────────────────────────────────────────
    // Antes: cada 2 s, sin esperar al tick anterior, y cada tick contaba TODAS las instancias
    // capturadas del job (la tabla más grande de la base) tres veces. Con 1 millón de
    // estudios y 2 millones de UIDs, ~3,7 s de base por tick: los ticks se solapaban y una
    // pestaña abierta durante una captura ocupaba ~2 núcleos de PostgreSQL. Ahora: un tick
    // cada 5 s que se salta si el anterior sigue en curso, y los conteos de UIDs (lo caro)
    // solo cada 30 s. Un refresco pedido por el usuario (botones, cambiar de página) es
    // completo como siempre.
    private static readonly TimeSpan DetailRefreshEvery = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan UidCountsEvery     = TimeSpan.FromSeconds(30);
    private int _detailRefreshing;                      // 1 mientras hay un tick en curso
    private DateTime _lastUidCounts = DateTime.MinValue;

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
        // InvokeAsync serializa con los manejadores, pero no impide que un tick empiece
        // mientras el anterior espera a la base: sin esta guarda se solapaban (BD-5).
        if (Interlocked.Exchange(ref _detailRefreshing, 1) == 1) return;
        try
        {
            var withUidCounts = DateTime.UtcNow - _lastUidCounts >= UidCountsEvery;
            await InvokeAsync(() => RefreshDetailAsync(withUidCounts));
        }
        catch (ObjectDisposedException)
        {
            // El componente ya se desechó (navegación). Nada que hacer.
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "Auto-refresco del detalle de descubrimiento falló (tick transitorio, se ignora).");
        }
        finally
        {
            Interlocked.Exchange(ref _detailRefreshing, 0);
        }
    }

    private void StopAutoRefresh()
    {
        _timer?.Dispose();
        _timer = null;
    }

    public void Dispose() => StopAutoRefresh();
}
