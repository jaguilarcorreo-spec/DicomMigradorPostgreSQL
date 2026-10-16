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

public sealed record PerfTypeView(string Key, string Code, string Name, string Color, long Studies, long Images,
                                  double Avg, double Median, double P95, double ThreadSeconds, double Share);

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
    public List<PauseSpan> Pauses { get; } = [];
    public bool AuditPurged { get; }
    public bool UseHeatmap => Rows.Count > MaxRowsAsDetail;
    public double SecondsPerStudy => D.Migrated == 0 ? 0 : ActiveSeconds / D.Migrated;

    public PerformanceModel(MigrationPerformanceData d, TimeZoneInfo tz, CompositionModel? comp)
    {
        D = d; Tz = tz;
        ActiveSeconds = d.Islands.Sum(i => (i.EndUtc - i.StartUtc).TotalSeconds);
        VerifyActiveSeconds = d.VerifyIslands.Sum(i => (i.EndUtc - i.StartUtc).TotalSeconds);
        CalendarSeconds = d.FirstStartUtc is { } f && d.LastEndUtc is { } l ? (l - f).TotalSeconds : 0;
        AuditPurged = d.FirstStartUtc is { } fs && (d.FirstAuditUtc is null || d.FirstAuditUtc > fs.AddHours(1));
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
        var thread = d.Types.Sum(t => t.ThreadSeconds);
        foreach (var t in d.Types.OrderByDescending(t => t.Key == "+" ? -1 : t.Studies))
        {
            var others = t.Key == "+";
            var label = others ? (comp?.Modalities.FirstOrDefault(m => m.Key == CompositionModel.OthersKey)?.Label ?? "Otras") : t.Key;
            Types.Add(new PerfTypeView(t.Key, label, others ? "Resto de modalidades" : CompositionModel.NameOf(t.Key),
                comp?.ColorOf(others ? CompositionModel.OthersKey : t.Key) ?? "var(--series-other)",
                t.Studies, t.Images, t.AvgSeconds, t.MedianSeconds, t.P95Seconds, t.ThreadSeconds,
                thread == 0 ? 0 : t.ThreadSeconds / thread));
        }
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
            switch (e.Action)
            {
                case "START":
                    if (open is { } o) Pauses.Add(new PauseSpan(o.At, e.Utc, o.Kind, o.NoSend));
                    open = null;
                    break;
                case "WINDOW_CLOSE": open ??= (e.Utc, PauseKind.Window, false); break;
                case "LICENSE": open ??= (e.Utc, PauseKind.License, false); break;
                case "PAUSE":
                    var kind = e.Message.StartsWith("Auto-pausa", StringComparison.Ordinal) ? PauseKind.Auto
                             : e.Message.Contains("error de configuración", StringComparison.Ordinal) ? PauseKind.Config
                             : PauseKind.Manual;
                    // Una pausa manual mientras ya estaba en pausa por ventana no abre otra.
                    open ??= (e.Utc, kind, e.Message.Contains("no envía imágenes", StringComparison.Ordinal));
                    break;
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
