using System.Text.Json;
using DicomMigrator.Core.Models;

namespace DicomMigrator.Web.Components.Shared;

// ══════════════════════════════════════════════════════════════════════════════
// Datos y geometría de la pestaña «Rendimiento» de la ficha de migración.
//
// Parte de MigrationPerformanceData (agregados de los estudios y eventos de la auditoría) y
// lo pasa a hora local: tramos de trabajo, pausas por tipo, jornadas, horas, tipos de
// estudio e incidencias. PerformanceTab.razor solo pinta lo que sale de aquí.
//
// Jornadas: el eje horario se adapta a cuándo se trabajó. Se busca el hueco horario más
// largo sin actividad en todo el lote (06:00-22:00 con una ventana nocturna, 15:00-08:00 con
// una de mañana, ninguno con 24 h) y cada fila empieza donde acaba ese hueco: una noche de
// 22:00 a 06:00 es una sola fila, no dos mitades de días distintos.
// ══════════════════════════════════════════════════════════════════════════════

public enum PauseKind { Window, Auto, Manual, Config, License }

public sealed record PauseSpan(DateTime StartUtc, DateTime EndUtc, PauseKind Kind, bool NoSend);

/// <summary>Una celda de tiempo (tramo de 15 min o una hora) de una jornada.</summary>
public sealed class PerfCell
{
    public long Images;
    public long Studies;
    public double ActiveSeconds;
    public double AutoSeconds;     // auto-pausa (origen sin envío, conexión…)
    public double PauseSeconds;    // ventana, manual, configuración, licencia
    public void Add(PerfCell o) { Images += o.Images; Studies += o.Studies; ActiveSeconds += o.ActiveSeconds; AutoSeconds += o.AutoSeconds; PauseSeconds += o.PauseSeconds; }
}

public sealed class PerfRow
{
    public DateTime StartLocal;              // inicio de la jornada (fecha + hora de inicio del eje)
    public string Label = "";
    public string LongLabel = "";
    public PerfCell[] Bins = [];             // tramos de 15 min del eje
    public PerfCell[] Hours = [];            // horas del eje
    public PerfCell Total = new();
}

/// <summary>Fila de la tabla por tipo. Studies/Images: migrados; LotStudies/LotImages: del lote
/// entero según la composición (pestaña Gráficas), null si no hay composición.</summary>
public sealed record PerfTypeView(string Key, string Code, string Name, string Color, long Studies, long Images,
                                  double Avg, double Median, double P95, double ThreadSeconds, double Share,
                                  long? LotStudies, long? LotImages);

public sealed class PerformanceModel
{
    public static readonly string[] Dow = ["dom", "lun", "mar", "mié", "jue", "vie", "sáb"];
    public static readonly string[] Mon = ["ene", "feb", "mar", "abr", "may", "jun", "jul", "ago", "sep", "oct", "nov", "dic"];
    public static readonly string[] DurationLabels = ["< 2 s", "2–5 s", "5–15 s", "15–60 s", "1–5 min", "5–15 min", "> 15 min"];
    /// <summary>Más jornadas que esto: barras por jornada + mapa de calor + detalle de la pulsada.</summary>
    public const int MaxRowsAsDetail = 10;

    public MigrationPerformanceData D { get; }
    public TimeZoneInfo Tz { get; }
    public double ActiveSeconds { get; }
    public double CalendarSeconds { get; }
    /// <summary>Parte del tiempo trabajado que cae dentro de auto-pausas: los huecos de menos de 5 min
    /// unen las islas, así que una auto-pausa corta queda contada como trabajo.</summary>
    public double AutoInsideSeconds { get; }
    public double NetSeconds => Math.Max(0, ActiveSeconds - AutoInsideSeconds);
    public double VerifyActiveSeconds { get; }
    public int AxisStartHour { get; }
    public int AxisHours { get; }
    public bool CrossesMidnight => AxisStartHour + AxisHours > 24;
    public List<PerfRow> Rows { get; } = [];
    public PerfCell[] PerHour { get; }
    public List<PerfTypeView> Types { get; } = [];
    /// <summary>Estudios e imágenes del lote entero (composición); null si no la hay.</summary>
    public long? LotStudies { get; }
    public long? LotImages  { get; }
    public List<PauseSpan> Pauses { get; } = [];
    public bool AuditPurged { get; }
    public bool UseHeatmap => Rows.Count > MaxRowsAsDetail;
    public double SecondsPerStudy => D.Migrated == 0 ? 0 : ActiveSeconds / D.Migrated;
    public double SecondsPerImage => D.Images == 0 ? 0 : ActiveSeconds / D.Images;

    public PerformanceModel(MigrationPerformanceData d, TimeZoneInfo tz, CompositionModel? comp)
    {
        D = d; Tz = tz;
        ActiveSeconds = d.Islands.Sum(i => (i.EndUtc - i.StartUtc).TotalSeconds);
        VerifyActiveSeconds = d.VerifyIslands.Sum(i => (i.EndUtc - i.StartUtc).TotalSeconds);
        CalendarSeconds = d.FirstStartUtc is { } f && d.LastEndUtc is { } l ? (l - f).TotalSeconds : 0;
        // Sin eventos desde el principio: la auditoría se purgó antes de que se guardaran.
        AuditPurged = d.FirstStartUtc is { } fs && (d.Events.Count == 0 || d.Events[0].Utc > fs.AddHours(1));
        BuildPauses();
        AutoInsideSeconds = Pauses.Where(p => p.Kind == PauseKind.Auto)
            .Sum(p => d.Islands.Sum(i => Math.Max(0, ((p.EndUtc < i.EndUtc ? p.EndUtc : i.EndUtc) - (p.StartUtc > i.StartUtc ? p.StartUtc : i.StartUtc)).TotalSeconds)));

        // ── Celdas de 15 min en UTC ───────────────────────────────────────────
        var cells = new Dictionary<DateTime, PerfCell>();
        PerfCell Cell(DateTime utc) { var k = Floor15(utc); return cells.TryGetValue(k, out var c) ? c : cells[k] = new PerfCell(); }
        foreach (var b in d.Bins) { var c = Cell(b.StartUtc); c.Images += b.Images; c.Studies += b.Studies; }
        foreach (var i in d.Islands) Spread(i.StartUtc, i.EndUtc, (k, s) => Cell(k).ActiveSeconds += s);
        var first = d.FirstStartUtc; var last = d.LastEndUtc;
        foreach (var p in Pauses)
        {
            // Solo dentro del periodo de la migración (una pausa final sin reanudar no cuenta)
            if (first is null || last is null) break;
            var a = p.StartUtc < first ? first.Value : p.StartUtc; var b = p.EndUtc > last ? last.Value : p.EndUtc;
            if (b <= a) continue;
            Spread(a, b, (k, s) => { if (p.Kind == PauseKind.Auto) Cell(k).AutoSeconds += s; else Cell(k).PauseSeconds += s; });
        }

        // ── Eje horario: empieza tras el hueco más largo sin actividad ────────
        var activeByHour = new double[24];
        foreach (var (k, c) in cells) activeByHour[ToLocal(k).Hour] += c.ActiveSeconds + c.Images;
        (AxisStartHour, AxisHours) = Axis(activeByHour);

        // ── Jornadas ─────────────────────────────────────────────────────────
        if (cells.Count > 0 && first is not null && last is not null)
        {
            DateTime RowKey(DateTime local) => local.AddHours(-AxisStartHour).Date;
            var firstRow = RowKey(ToLocal(first.Value)); var lastRow = RowKey(ToLocal(last.Value));
            var rows = new Dictionary<DateTime, PerfRow>();
            for (var r = firstRow; r <= lastRow; r = r.AddDays(1))
            {
                var start = r.AddHours(AxisStartHour);
                var end = start.AddHours(AxisHours);
                var row = new PerfRow
                {
                    StartLocal = start,
                    Bins = Enumerable.Range(0, AxisHours * 4).Select(_ => new PerfCell()).ToArray(),
                    Hours = Enumerable.Range(0, AxisHours).Select(_ => new PerfCell()).ToArray(),
                    Label = CrossesMidnight ? $"{Dow[(int)start.DayOfWeek]} {start.Day} → {Dow[(int)end.DayOfWeek]} {end.Day}" : $"{Dow[(int)start.DayOfWeek]} {start.Day}",
                    LongLabel = CrossesMidnight
                        ? $"Noche del {Dow[(int)start.DayOfWeek]} {start.Day} {Mon[start.Month - 1]} al {Dow[(int)end.DayOfWeek]} {end.Day}"
                        : $"{Dow[(int)start.DayOfWeek]} {start.Day} {Mon[start.Month - 1]}",
                };
                rows[r] = row; Rows.Add(row);
            }
            foreach (var (k, c) in cells)
            {
                var local = ToLocal(k);
                if (!rows.TryGetValue(RowKey(local), out var row)) continue;
                var idx = (int)Math.Floor((local - row.StartLocal).TotalMinutes / 15);
                if (idx < 0) continue;
                if (idx >= row.Bins.Length) idx = row.Bins.Length - 1;   // actividad suelta fuera del eje: al último tramo
                row.Bins[idx].Add(c); row.Hours[idx / 4].Add(c); row.Total.Add(c);
            }
        }

        PerHour = Enumerable.Range(0, AxisHours).Select(_ => new PerfCell()).ToArray();
        foreach (var row in Rows) for (var h = 0; h < AxisHours; h++) PerHour[h].Add(row.Hours[h]);

        // ── Tipos de estudio (colores y «Otras» como en la pestaña Gráficas) ──
        // También los tipos del lote que aún no tienen ningún estudio migrado (0 / total).
        var thread = d.Types.Sum(t => t.ThreadSeconds);
        var byKey = d.Types.ToDictionary(t => t.Key);
        var lot = comp?.Modalities.ToDictionary(m => m.Key) ?? new Dictionary<string, ModalitySlice>();
        var keys = byKey.Keys.Union(lot.Keys)
            .OrderBy(k => k == CompositionModel.OthersKey ? 1 : 0)
            .ThenByDescending(k => byKey.TryGetValue(k, out var bt) ? bt.Studies : 0)
            .ThenByDescending(k => lot.TryGetValue(k, out var ls) ? ls.Studies : 0);
        foreach (var k in keys)
        {
            var others = k == CompositionModel.OthersKey;
            var t = byKey.GetValueOrDefault(k);
            var lotRow = lot.GetValueOrDefault(k);
            var label = others ? (lotRow?.Label ?? "Otras") : k;
            Types.Add(new PerfTypeView(k, label, others ? "Resto de modalidades" : CompositionModel.NameOf(k),
                comp?.ColorOf(k) ?? "var(--series-other)",
                t?.Studies ?? 0, t?.Images ?? 0, t?.AvgSeconds ?? 0, t?.MedianSeconds ?? 0, t?.P95Seconds ?? 0, t?.ThreadSeconds ?? 0,
                thread == 0 || t is null ? 0 : t.ThreadSeconds / thread,
                comp is null ? null : lotRow?.Studies ?? 0, comp is null ? null : lotRow?.Images ?? 0));
        }
        LotStudies = comp?.TotalStudies;
        LotImages  = comp?.TotalImages;
    }

    public DateTime ToLocal(DateTime utc) => TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), Tz);

    private static DateTime Floor15(DateTime utc) =>
        new(utc.Ticks - utc.Ticks % TimeSpan.FromMinutes(15).Ticks, DateTimeKind.Utc);

    /// <summary>Reparte el intervalo [a, b) entre las celdas de 15 min que toca.</summary>
    private static void Spread(DateTime a, DateTime b, Action<DateTime, double> add)
    {
        var k = Floor15(a);
        while (k < b)
        {
            var next = k.AddMinutes(15);
            var s = ((b < next ? b : next) - (a > k ? a : k)).TotalSeconds;
            if (s > 0) add(k, s);
            k = next;
        }
    }

    /// <summary>Hueco circular más largo de horas sin actividad → (hora de inicio, horas de eje).</summary>
    private static (int Start, int Hours) Axis(double[] byHour)
    {
        if (byHour.All(v => v > 0) || byHour.All(v => v == 0)) return (0, 24);
        int bestLen = 0, bestEnd = 0;
        for (var s = 0; s < 24; s++)
        {
            if (byHour[s] > 0 || byHour[(s + 23) % 24] == 0) continue;   // empieza un hueco en s
            var len = 0;
            while (len < 24 && byHour[(s + len) % 24] == 0) len++;
            if (len > bestLen) { bestLen = len; bestEnd = (s + len) % 24; }
        }
        return (bestEnd, 24 - bestLen);
    }

    /// <summary>Pausas a partir de la auditoría: empiezan con un cierre de ventana, una pausa o
    /// la verja de licencia, y terminan con el siguiente arranque.</summary>
    private void BuildPauses()
    {
        (DateTime At, PauseKind Kind, bool NoSend)? open = null;
        foreach (var e in D.Events)
        {
            // Una pausa mientras ya estaba en pausa (p. ej. manual con la ventana cerrada) no abre otra.
            switch (e.Kind)
            {
                case "S":
                    if (open is { } o) Pauses.Add(new PauseSpan(o.At, e.Utc, o.Kind, o.NoSend));
                    open = null;
                    break;
                case "W": open ??= (e.Utc, PauseKind.Window, false); break;
                case "L": open ??= (e.Utc, PauseKind.License, false); break;
                case "PA": open ??= (e.Utc, PauseKind.Auto, false); break;
                case "PN": open ??= (e.Utc, PauseKind.Auto, true); break;
                case "PC": open ??= (e.Utc, PauseKind.Config, false); break;
                case "PM": open ??= (e.Utc, PauseKind.Manual, false); break;
            }
        }
    }

    // ── Incidencias ──────────────────────────────────────────────────────────
    public int Count(PauseKind k) => Pauses.Count(p => p.Kind == k);
    public double Seconds(PauseKind k) => Pauses.Where(p => p.Kind == k).Sum(p => (p.EndUtc - p.StartUtc).TotalSeconds);
    public int NoSendPauses => Pauses.Count(p => p.NoSend);

    // ── Formato ──────────────────────────────────────────────────────────────
    public static string N(double v, int dec = 0) => v.ToString(dec == 0 ? "N0" : "N" + dec, CompositionModel.Es);
    public static string Hm(double s)
    {
        if (s < 60) return $"{N(s)} s";
        var d = (int)(s / 86400); var h = (int)(s % 86400 / 3600); var m = (int)Math.Round(s % 3600 / 60);
        if (m == 60) { h++; m = 0; }
        return (d > 0 ? $"{d} d " : "") + (h > 0 || d > 0 ? $"{h} h " : "") + $"{m} min";
    }
    public static string Dur(double s) => s < 60 ? $"{N(s, s < 10 ? 1 : 0)} s" : $"{(int)(s / 60)} min {(int)Math.Round(s % 60):00} s";
    public string HourLabel(int axisIndex) => $"{(AxisStartHour + axisIndex) % 24:00}";

    /// <summary>JSON del detalle al pasar el ratón (lo pinta wwwroot/js/charts.js).</summary>
    public static string Tip(string title, string sub, IEnumerable<string>? lines = null) =>
        JsonSerializer.Serialize(new { t = title, s = sub, r = lines?.Select(l => new[] { l, "transparent", "", "" }) });
}
