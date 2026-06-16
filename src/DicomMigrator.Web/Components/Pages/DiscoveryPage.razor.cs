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
            async _ => await RefreshDetailAsync(),
            null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
    }

    private void StopAutoRefresh()
    {
        _timer?.Dispose();
        _timer = null;
    }

    public void Dispose() => StopAutoRefresh();
}
