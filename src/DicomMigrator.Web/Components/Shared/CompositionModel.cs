using System.Globalization;
using DicomMigrator.Core.Models;

namespace DicomMigrator.Web.Components.Shared;

// ══════════════════════════════════════════════════════════════════════════════
// Datos y geometría de la pestaña Gráficas de la ficha de migración.
//
// Todo el cálculo vive aquí, en C# puro, y CompositionCharts.razor solo pinta lo que
// sale: el SVG se genera en el servidor (sin librerías JS ni llamadas a JavaScript, salvo
// desplazar la vista por meses hasta lo más reciente). Parte de las filas guardadas en
// MigrationComposition (mes × modalidad), así que nunca recorre los estudios.
// ══════════════════════════════════════════════════════════════════════════════

public enum DateGrouping { Month, Quarter, Year }

/// <summary>Año (y trimestre, opcional) cuyos meses se muestran al pulsar una barra.</summary>
public sealed record DrillKey(int Year, int? Quarter);

public sealed record ModalitySlice(string Key, string Label, int Studies, long Images, string Color);

public enum BucketKind { Month, Quarter, Year, Before, After, NoDate }

public sealed record DateBucket(
    BucketKind Kind, string Label, string? Label2, string Title,
    IReadOnlyDictionary<string, int> ByModality, int Total, DrillKey? Drill,
    int Year = 0, int Month = 0, int Quarter = 0)
{
    public bool IsOutlier => Kind is BucketKind.Before or BucketKind.After or BucketKind.NoDate;
}

public sealed class CompositionModel
{
    public static readonly CultureInfo Es = CultureInfo.GetCultureInfo("es-ES");
    public static readonly string[] MonthNames = ["ene", "feb", "mar", "abr", "may", "jun", "jul", "ago", "sep", "oct", "nov", "dic"];

    /// <summary>Modalidades con color propio (paleta categórica de 8 tonos); el resto se
    /// agrupa en «Otras», en gris. Nunca se generan tonos nuevos.</summary>
    public const int MaxOwnColors = 8;
    public const string OthersKey = "+";

    // Orden en que se reparten los 8 tonos entre las modalidades presentes: así una
    // modalidad conserva su color de una migración a otra en los casos habituales.
    private static readonly string[] Canonical =
        ["CT", "MR", "CR", "DX", "US", "MG", "RF", "XA", "NM", "PT", "OT", "SC", "ES", "IO", "PX", "SR", "PR", "KO"];

    private static readonly Dictionary<string, string> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        ["CT"] = "Tomografía", ["MR"] = "Resonancia", ["CR"] = "Radiografía CR", ["DX"] = "Radiografía DX",
        ["US"] = "Ecografía", ["MG"] = "Mamografía", ["RF"] = "Radioscopia", ["XA"] = "Angiografía",
        ["NM"] = "Medicina nuclear", ["PT"] = "PET", ["OT"] = "Otros", ["SC"] = "Captura secundaria",
        ["ES"] = "Endoscopia", ["IO"] = "Intraoral", ["PX"] = "Panorámica dental", ["SR"] = "Informe estructurado",
        ["PR"] = "Estado de presentación", ["KO"] = "Selección de imágenes", ["—"] = "Sin modalidad",
    };

    public IReadOnlyList<ModalitySlice> Modalities { get; }
    public int TotalStudies { get; }
    public long TotalImages { get; }
    public DateTime? ComputedAt { get; }

    /// <summary>Meses del rango del lote, continuos (también los que no tienen estudios).</summary>
    public IReadOnlyList<DateBucket> Months { get; }
    /// <summary>Fuera de rango: Anteriores, Posteriores y Sin fecha (solo los que tienen estudios).</summary>
    public IReadOnlyList<DateBucket> Outliers { get; }
    public string RangeLabel { get; }
    public DateGrouping DefaultGrouping { get; }

    /// <summary>Orden de apilado (abajo → arriba): de más a menos estudios; «Otras» arriba.</summary>
    public IReadOnlyList<string> StackOrder => Modalities.Select(m => m.Key).ToList();

    private readonly Dictionary<string, string> _keyOf;   // modalidad real → clave mostrada (o «Otras»)

    /// <summary>Clave mostrada de una modalidad real: ella misma si tiene color propio, o «Otras».</summary>
    public string KeyOf(string modality) => _keyOf.TryGetValue(modality, out var k) ? k : OthersKey;
    /// <summary>Modalidades con color propio (sin «Otras»).</summary>
    public IReadOnlyList<string> OwnKeys => Modalities.Where(m => m.Key != OthersKey).Select(m => m.Key).ToList();

    public string ColorOf(string key) => Modalities.FirstOrDefault(m => m.Key == key)?.Color ?? "var(--series-other)";
    public static string NameOf(string key) =>
        key == OthersKey ? "Resto de modalidades" : Names.TryGetValue(key, out var n) ? n : key;

    private CompositionModel(List<MigrationCompositionRow> rows, DateOnly today)
    {
        ComputedAt   = rows.Count > 0 ? rows.Max(r => r.ComputedAt) : null;
        TotalStudies = rows.Sum(r => r.Studies);
        TotalImages  = rows.Sum(r => r.Images);

        // ── Modalidades: 8 con color propio, el resto a «Otras» ────────────────
        var byMod = rows.GroupBy(r => r.Modality)
            .Select(g => (Key: g.Key, Studies: g.Sum(r => r.Studies), Images: g.Sum(r => r.Images)))
            .OrderByDescending(m => m.Studies).ThenByDescending(m => m.Images).ThenBy(m => CanonicalRank(m.Key))
            .ToList();
        var own = byMod.Count <= MaxOwnColors ? byMod : byMod.Take(MaxOwnColors - 1).ToList();
        var rest = byMod.Skip(own.Count).ToList();
        var slot = own.OrderBy(m => CanonicalRank(m.Key)).ThenBy(m => m.Key, StringComparer.Ordinal)
            .Select((m, i) => (m.Key, Slot: i + 1)).ToDictionary(x => x.Key, x => x.Slot);
        var slices = own.Select(m => new ModalitySlice(m.Key, m.Key, m.Studies, m.Images, $"var(--series-{slot[m.Key]})")).ToList();
        if (rest.Count > 0)
            slices.Add(new ModalitySlice(OthersKey, $"Otras ({rest.Count})", rest.Sum(m => m.Studies), rest.Sum(m => m.Images), "var(--series-other)"));
        Modalities = slices;
        _keyOf = byMod.ToDictionary(m => m.Key, m => own.Any(o => o.Key == m.Key) ? m.Key : OthersKey);

        // ── Fechas: rango del lote y fuera de rango ────────────────────────────
        var todayKey = today.Year * 12 + today.Month - 1;
        var perMonth = rows.Where(r => r.Month.Length == 6)
            .GroupBy(r => MonthIndex(r.Month))
            .ToDictionary(g => g.Key, g => g.ToList());
        var future = perMonth.Keys.Where(k => k > todayKey).ToHashSet();
        var keys = perMonth.Keys.Where(k => k <= todayKey).OrderBy(k => k).ToList();
        int Mass(IEnumerable<int> ks) => ks.Sum(k => perMonth[k].Sum(r => r.Studies));

        // Grupos de meses separados por huecos de más de 2 años. El grupo con más estudios
        // es el lote; lo que queda antes o después solo se aparta si suma menos del 1 %
        // (fechas imposibles como 1900, relojes de modalidad mal puestos…). Si pesa más,
        // es un periodo real y entra en el rango.
        var before = new List<int>(); var after = new List<int>(); var inRange = keys;
        if (keys.Count > 0)
        {
            var clusters = new List<List<int>> { new() { keys[0] } };
            for (var i = 1; i < keys.Count; i++)
            {
                if (keys[i] - keys[i - 1] > 24) clusters.Add([]);
                clusters[^1].Add(keys[i]);
            }
            var main = clusters.IndexOf(clusters.MaxBy(c => Mass(c))!);
            var total = Mass(keys);
            var pre = clusters.Take(main).SelectMany(c => c).ToList();
            var post = clusters.Skip(main + 1).SelectMany(c => c).ToList();
            if (pre.Count > 0 && Mass(pre) < total * 0.01) before = pre;
            if (post.Count > 0 && Mass(post) < total * 0.01) after = post;
            inRange = keys.Except(before).Except(after).ToList();
        }
        after.AddRange(future);

        var months = new List<DateBucket>();
        if (inRange.Count > 0)
        {
            for (var k = inRange[0]; k <= inRange[^1]; k++)
            {
                var (y, m) = (k / 12, k % 12);
                var dict = Collapse(perMonth.TryGetValue(k, out var rs) ? rs : []);
                months.Add(new DateBucket(BucketKind.Month, MonthNames[m], null, $"{MonthNames[m]} {y}",
                    dict, dict.Values.Sum(), null, y, m, m / 3));
            }
            RangeLabel = $"{MonthNames[inRange[0] % 12]} {inRange[0] / 12} – {MonthNames[inRange[^1] % 12]} {inRange[^1] / 12}";
        }
        else RangeLabel = "sin fechas válidas";
        Months = months;

        var outs = new List<DateBucket>();
        void AddOut(BucketKind kind, string label, string sub, IEnumerable<MigrationCompositionRow> rs)
        {
            var d = Collapse(rs);
            var t = d.Values.Sum();
            if (t > 0) outs.Add(new DateBucket(kind, label, sub, $"{label} ({sub})", d, t, null));
        }
        var first = inRange.Count > 0 ? $"{MonthNames[inRange[0] % 12]} {inRange[0] / 12}" : "";
        AddOut(BucketKind.Before, "Anteriores", $"antes de {first}".Trim(), before.SelectMany(k => perMonth[k]));
        AddOut(BucketKind.After, "Posteriores", future.Count > 0 && after.Count == future.Count ? "fechas futuras" : "fechas posteriores o futuras",
               after.SelectMany(k => perMonth[k]));
        AddOut(BucketKind.NoDate, "Sin fecha", "vacía o no válida", rows.Where(r => r.Month.Length != 6));
        Outliers = outs;

        // Agrupación por defecto según el rango: hasta 2 años, meses; hasta 6, trimestres; más, años
        DefaultGrouping = months.Count <= 24 ? DateGrouping.Month : months.Count <= 72 ? DateGrouping.Quarter : DateGrouping.Year;
    }

    public static CompositionModel Build(IEnumerable<MigrationCompositionRow> rows, DateOnly today) => new(rows.ToList(), today);

    private static int CanonicalRank(string key)
    {
        var i = Array.IndexOf(Canonical, key);
        return i < 0 ? Canonical.Length : i;
    }

    private static int MonthIndex(string yyyymm) =>
        int.Parse(yyyymm[..4], CultureInfo.InvariantCulture) * 12 + int.Parse(yyyymm[4..], CultureInfo.InvariantCulture) - 1;

    private Dictionary<string, int> Collapse(IEnumerable<MigrationCompositionRow> rs)
    {
        var d = new Dictionary<string, int>();
        foreach (var r in rs)
        {
            var k = _keyOf.TryGetValue(r.Modality, out var mapped) ? mapped : OthersKey;
            d[k] = d.GetValueOrDefault(k) + r.Studies;
        }
        return d;
    }

    private static Dictionary<string, int> Sum(IEnumerable<DateBucket> bs)
    {
        var d = new Dictionary<string, int>();
        foreach (var b in bs) foreach (var (k, v) in b.ByModality) d[k] = d.GetValueOrDefault(k) + v;
        return d;
    }

    /// <summary>Barras de la vista pedida. Con <paramref name="drill"/>, los meses de ese año o trimestre.</summary>
    public IReadOnlyList<DateBucket> Buckets(DateGrouping g, DrillKey? drill)
    {
        if (drill is not null)
            return Months.Where(b => b.Year == drill.Year && (drill.Quarter is null || b.Quarter == drill.Quarter)).ToList();
        if (g == DateGrouping.Month) return Months;
        return Months.GroupBy(b => g == DateGrouping.Year ? (b.Year, 0) : (b.Year, b.Quarter + 1))
            .Select(grp =>
            {
                var list = grp.ToList();
                var d = Sum(list);
                var (f, l) = (list[0], list[^1]);
                if (g == DateGrouping.Year)
                {
                    var partial = list.Count < 12 ? $"{MonthNames[f.Month]}–{MonthNames[l.Month]}" : null;
                    return new DateBucket(BucketKind.Year, $"{f.Year}", partial, partial is null ? $"{f.Year}" : $"{f.Year} ({partial})",
                        d, d.Values.Sum(), new DrillKey(f.Year, null), f.Year);
                }
                return new DateBucket(BucketKind.Quarter, $"T{f.Quarter + 1}", null, $"T{f.Quarter + 1} {f.Year}",
                    d, d.Values.Sum(), new DrillKey(f.Year, f.Quarter), f.Year, f.Month, f.Quarter);
            }).ToList();
    }

    public static string N(long n) => n.ToString("N0", Es);
    public static string Pct(long v, long total) => total == 0 ? "0,0 %" : (100.0 * v / total).ToString("0.0", Es) + " %";
}

// ── Geometría ────────────────────────────────────────────────────────────────

public sealed record PieSlice(string Key, string Path, string Color, string Title, double LabelX, double LabelY, string? Label);

public static class PieLayout
{
    /// <summary>Porciones de mayor a menor desde las 12 en punto; % dentro de las de 8 % o más.</summary>
    public static List<PieSlice> Build(IEnumerable<(string Key, long Value, string Color, string Title)> items,
        double cx = 100, double cy = 100, double r = 92)
    {
        var list = items.Where(i => i.Value > 0).OrderByDescending(i => i.Value).ToList();
        var total = (double)list.Sum(i => i.Value);
        var res = new List<PieSlice>();
        var a0 = -Math.PI / 2;
        foreach (var it in list)
        {
            var frac = it.Value / total;
            var a1 = a0 + 2 * Math.PI * frac;
            string path;
            if (frac >= 0.9999)   // una sola modalidad: círculo completo (un arco de 360° no se dibuja)
                path = F($"M{cx},{cy - r} A{r},{r} 0 1 1 {cx - 0.01},{cy - r} Z");
            else
            {
                var (x0, y0) = (cx + r * Math.Cos(a0), cy + r * Math.Sin(a0));
                var (x1, y1) = (cx + r * Math.Cos(a1), cy + r * Math.Sin(a1));
                path = F($"M{cx},{cy} L{x0:0.##},{y0:0.##} A{r},{r} 0 {(a1 - a0 > Math.PI ? 1 : 0)} 1 {x1:0.##},{y1:0.##} Z");
            }
            var mid = (a0 + a1) / 2;
            res.Add(new PieSlice(it.Key, path, it.Color, it.Title,
                cx + r * 0.62 * Math.Cos(mid), cy + r * 0.62 * Math.Sin(mid),
                frac >= 0.08 ? $"{Math.Round(frac * 100)}%" : null));
            a0 = a1;
        }
        return res;
    }

    public static string F(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);
}

public sealed record BarSegment(string Key, double Y, double H, string Color, string? Path);
public sealed record BarShape(DateBucket Bucket, double X, double W, IReadOnlyList<BarSegment> Segments, double TopY, string Tooltip);
public sealed record AxisText(double X, double Y, string Text, string Css, string Anchor = "middle");

/// <summary>
/// Geometría de las barras apiladas, en unidades de dibujo. Sin desplazamiento, todo cabe
/// en 900 × 290 y el SVG se escala al ancho de la tarjeta, con el eje dentro. Con
/// desplazamiento (vista por meses que no cabe), cada mes ocupa <see cref="MonthMin"/>
/// unidades a escala 1:1 en píxeles, y el eje va en un SVG aparte fijo a la izquierda.
/// </summary>
public sealed class BarLayout
{
    public const double Base = 900, H = 290, AxisW = 52, Mr = 12, Mt = 16, Mb = 44, MonthMin = 16;

    public bool Scroll { get; }
    /// <summary>Ancho del dibujo de barras (sin el eje si hay desplazamiento).</summary>
    public double Width { get; }
    public double PlotLeft { get; }
    public double PlotRight { get; }
    public double Baseline => H - Mb;
    public List<(double Y, string Text)> Ticks { get; } = [];
    public List<BarShape> Bars { get; } = [];
    public List<AxisText> Texts { get; } = [];
    public List<double> YearSeparators { get; } = [];
    public double? OutlierSeparator { get; }

    public BarLayout(CompositionModel model, IReadOnlyList<DateBucket> buckets, IReadOnlyList<DateBucket> outliers, bool monthView)
    {
        var all = buckets.Concat(outliers).ToList();
        var gapSlots = outliers.Count > 0 ? 1.2 : 0;
        var slots = all.Count + gapSlots;
        Scroll = monthView && (Base - AxisW - Mr) < slots * MonthMin;
        PlotLeft = Scroll ? 0 : AxisW;
        Width = Scroll ? Math.Ceiling(slots * MonthMin + Mr) : Base;
        PlotRight = Width - Mr;
        var band = (PlotRight - PlotLeft) / Math.Max(slots, 1);
        var dense = buckets.Count > 30;
        var bw = Math.Max(4, Math.Min(44, band * (dense ? 0.72 : 0.62)));

        var max = Math.Max(1, all.Count == 0 ? 1 : all.Max(b => b.Total));
        var step = NiceStep(max);
        var top = Math.Ceiling(max / step) * step;
        double Y(double v) => Mt + (H - Mt - Mb) * (1 - v / top);
        for (var v = 0.0; v <= top + 0.5; v += step) Ticks.Add((Y(v), CompositionModel.N((long)v)));

        var order = model.StackOrder;
        void AddBar(DateBucket b, double x0)
        {
            var x = x0 + (band - bw) / 2;
            var present = order.Where(k => b.ByModality.GetValueOrDefault(k) > 0).ToList();
            var segs = new List<BarSegment>();
            double acc = 0;
            var gap = bw > 6 ? 2 : 1;   // separación entre tramos
            foreach (var k in present)
            {
                var v = b.ByModality[k];
                var (y1, y0) = (Y(acc + v), Y(acc));
                var h = Math.Max(0, y0 - y1 - gap);
                var isTop = k == present[^1];
                segs.Add(new BarSegment(k, y1, h, model.ColorOf(k), isTop ? RoundTop(x, y1, bw, h, Math.Min(4, bw / 3)) : null));
                acc += v;
            }
            var lines = string.Join("\n", present.AsEnumerable().Reverse()
                .Select(k => $"{(k == CompositionModel.OthersKey ? "Otras" : k),-6} {CompositionModel.N(b.ByModality[k])}"));
            var tip = $"{b.Title} · {CompositionModel.N(b.Total)} estudios" + (lines.Length > 0 ? "\n" + lines : "")
                      + (b.Drill is not null ? "\nPulsa para ver sus meses" : "");
            Bars.Add(new BarShape(b, x, bw, segs, Y(b.Total), tip));
        }

        for (var i = 0; i < buckets.Count; i++)
        {
            var b = buckets[i];
            var x0 = PlotLeft + band * i;
            var cxm = x0 + band / 2;
            var firstOfYear = (b.Kind == BucketKind.Month && b.Month == 0) || (b.Kind == BucketKind.Quarter && b.Quarter == 0);
            if (firstOfYear && i > 0) YearSeparators.Add(x0);
            AddBar(b, x0);
            var yl = Baseline + 16;
            switch (b.Kind)
            {
                case BucketKind.Month when dense:
                    if (b.Month == 0) Texts.Add(new AxisText(x0, yl, $"{b.Year}", "ax-year"));
                    else if (i == 0) Texts.Add(new AxisText(x0, yl, $"{b.Year}", "ax-year", "start"));
                    break;
                case BucketKind.Month:
                    Texts.Add(new AxisText(cxm, yl, b.Label, "ax-lbl"));
                    if (b.Month == 0 || i == 0) Texts.Add(new AxisText(cxm, yl + 15, $"{b.Year}", "ax-year2"));
                    break;
                case BucketKind.Quarter:
                    Texts.Add(new AxisText(cxm, yl, b.Label, "ax-lbl"));
                    if (b.Quarter == 0 || i == 0) Texts.Add(new AxisText(cxm, yl + 15, $"{b.Year}", "ax-year2"));
                    break;
                case BucketKind.Year:
                    Texts.Add(new AxisText(cxm, yl, b.Label, "ax-year"));
                    if (b.Label2 is not null) Texts.Add(new AxisText(cxm, yl + 15, b.Label2, "ax-sub"));
                    break;
            }
        }
        if (outliers.Count > 0)
        {
            OutlierSeparator = PlotLeft + band * (buckets.Count + 0.6);
            for (var j = 0; j < outliers.Count; j++)
                AddBar(outliers[j], PlotLeft + band * (buckets.Count + gapSlots + j));
            Texts.Add(new AxisText(PlotRight, Baseline + 16, "Fuera de rango", "ax-out", "end"));
            Texts.Add(new AxisText(PlotRight, Baseline + 31,
                string.Join(" · ", outliers.Select(o => o.Kind switch { BucketKind.Before => "ant.", BucketKind.After => "post.", _ => "sin fecha" })),
                "ax-sub", "end"));
        }
    }

    private static double NiceStep(double max)
    {
        var raw = max / 5;
        var p = Math.Pow(10, Math.Floor(Math.Log10(raw)));
        var n = raw / p;
        return Math.Max(1, (n <= 1 ? 1 : n <= 2 ? 2 : n <= 5 ? 5 : 10) * p);
    }

    private static string RoundTop(double x, double yTop, double w, double h, double rad)
    {
        rad = Math.Max(0, Math.Min(rad, Math.Min(w / 2, h)));
        return PieLayout.F($"M{x:0.##},{yTop + h:0.##} L{x:0.##},{yTop + rad:0.##} Q{x:0.##},{yTop:0.##} {x + rad:0.##},{yTop:0.##} L{x + w - rad:0.##},{yTop:0.##} Q{x + w:0.##},{yTop:0.##} {x + w:0.##},{yTop + rad:0.##} L{x + w:0.##},{yTop + h:0.##} Z");
    }
}
