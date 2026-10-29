using ClosedXML.Excel;
using DicomMigrator.Core.Models;
using DicomMigrator.Web.Components.Shared;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using A = DocumentFormat.OpenXml.Drawing;
using C = DocumentFormat.OpenXml.Drawing.Charts;
using Xdr = DocumentFormat.OpenXml.Drawing.Spreadsheet;

namespace DicomMigrator.Web.Services;

/// <summary>
/// Excel de la composición del lote (pestaña Gráficas de la ficha de migración):
///   · «Composición»: estudios e imágenes por modalidad principal, como la vista Tabla, con
///     las dos tartas como gráficos NATIVOS de Excel (editables y enlazados a las celdas), con
///     los mismos colores que en pantalla.
///   · «Estudios por mes» e «Imágenes por mes»: mes × modalidad, con las mismas agrupaciones.
///   · «Modalidades»: todas las modalidades reales, también las agrupadas en «Otras».
/// Sale de la composición guardada (MigrationComposition): no recorre los estudios.
/// ClosedXML no sabe crear gráficos, así que las tartas se añaden después con Open XML SDK
/// (la dependencia que ClosedXML ya trae).
/// </summary>
public static class CompositionExcel
{
    private const string SheetMain = "Composición";
    private const int HeaderRow = 5;          // fila de cabecera de la tabla principal

    /// <summary>Colores de wwwroot/css/app.css (--series-1..8 y --series-other).</summary>
    private static readonly Dictionary<string, string> SeriesHex = new()
    {
        ["var(--series-1)"] = "3987E5", ["var(--series-2)"] = "D95926", ["var(--series-3)"] = "199E70",
        ["var(--series-4)"] = "C98500", ["var(--series-5)"] = "D55181", ["var(--series-6)"] = "008300",
        ["var(--series-7)"] = "9085E9", ["var(--series-8)"] = "E66767", ["var(--series-other)"] = "5A7394",
    };
    private static string Hex(string cssColor) => SeriesHex.TryGetValue(cssColor, out var h) ? h : "5A7394";

    private static string Label(ModalitySlice s) => s.Key == CompositionModel.OthersKey ? s.Label : s.Key;

    public static byte[] Build(string migrationName, IReadOnlyList<MigrationCompositionRow> rows)
    {
        var model  = CompositionModel.Build(rows, DateOnly.FromDateTime(DateTime.Today));
        // Mismo orden que en pantalla: de más a menos estudios y «Otras» al final.
        var slices = model.Modalities.ToList();

        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add(SheetMain);
        ws.Cell(1, 1).Value = $"Composición del lote · {migrationName}";
        ws.Cell(1, 1).Style.Font.Bold = true;
        ws.Cell(1, 1).Style.Font.FontSize = 14;
        ws.Cell(2, 1).Value = model.ComputedAt is { } at
            ? $"Calculada el {at.ToLocalTime():dd/MM/yyyy HH:mm} · Exportada el {DateTime.Now:dd/MM/yyyy HH:mm}"
            : $"Exportada el {DateTime.Now:dd/MM/yyyy HH:mm}";
        ws.Cell(3, 1).Value = "Cada estudio cuenta en su modalidad principal: SR, PR y KO acompañan a otras y solo cuentan si son lo único del estudio. " +
                              "Imágenes según el recuento del origen.";
        ws.Range(1, 1, 3, 1).Style.Font.FontColor = XLColor.FromArgb(0x33, 0x33, 0x33);
        ws.Range(2, 1, 3, 1).Style.Font.Italic = true;

        string[] headers = ["Código", "Modalidad", "Estudios", "% estudios", "Imágenes", "% imágenes", "Imágenes / estudio"];
        for (var c = 0; c < headers.Length; c++) ws.Cell(HeaderRow, c + 1).Value = headers[c];
        StyleHeader(ws.Range(HeaderRow, 1, HeaderRow, headers.Length));

        var r = HeaderRow + 1;
        foreach (var s in slices)
        {
            ws.Cell(r, 1).Value = Label(s);
            ws.Cell(r, 1).Style.Fill.BackgroundColor = XLColor.FromHtml("#" + Hex(s.Color));
            ws.Cell(r, 1).Style.Font.FontColor = XLColor.White;
            ws.Cell(r, 1).Style.Font.Bold = true;
            ws.Cell(r, 2).Value = CompositionModel.NameOf(s.Key);
            ws.Cell(r, 3).Value = s.Studies;
            ws.Cell(r, 4).Value = model.TotalStudies == 0 ? 0 : (double)s.Studies / model.TotalStudies;
            ws.Cell(r, 5).Value = s.Images;
            ws.Cell(r, 6).Value = model.TotalImages == 0 ? 0 : (double)s.Images / model.TotalImages;
            ws.Cell(r, 7).Value = s.Studies == 0 ? 0 : (double)s.Images / s.Studies;
            r++;
        }
        var firstData = HeaderRow + 1;
        var lastData  = r - 1;
        ws.Cell(r, 1).Value = "Total";
        ws.Cell(r, 3).Value = model.TotalStudies;
        ws.Cell(r, 4).Value = slices.Count == 0 ? 0 : 1;
        ws.Cell(r, 5).Value = model.TotalImages;
        ws.Cell(r, 6).Value = slices.Count == 0 ? 0 : 1;
        ws.Cell(r, 7).Value = model.TotalStudies == 0 ? 0 : (double)model.TotalImages / model.TotalStudies;
        StyleTotal(ws.Range(r, 1, r, headers.Length));
        ws.Range(firstData, 3, r, 3).Style.NumberFormat.Format = "#,##0";
        ws.Range(firstData, 5, r, 5).Style.NumberFormat.Format = "#,##0";
        ws.Range(firstData, 4, r, 4).Style.NumberFormat.Format = "0.0%";
        ws.Range(firstData, 6, r, 6).Style.NumberFormat.Format = "0.0%";
        ws.Range(firstData, 7, r, 7).Style.NumberFormat.Format = "#,##0.0";
        Borders(ws.Range(HeaderRow, 1, r, headers.Length));
        ws.Column(1).Width = 14; ws.Column(2).Width = 24;
        for (var c = 3; c <= 7; c++) ws.Column(c).Width = 13;

        // ── Por mes (estudios e imágenes), con las columnas de la tabla principal ──
        AddMonthSheet(wb, "Estudios por mes", model, slices, rows, x => x.Studies);
        AddMonthSheet(wb, "Imágenes por mes", model, slices, rows, x => x.Images);

        // ── Todas las modalidades reales ──
        var wm = wb.Worksheets.Add("Modalidades");
        string[] mh = ["Modalidad", "Nombre", "Estudios", "Imágenes", "En las gráficas como"];
        for (var c = 0; c < mh.Length; c++) wm.Cell(1, c + 1).Value = mh[c];
        StyleHeader(wm.Range(1, 1, 1, mh.Length));
        var mr = 2;
        foreach (var g in rows.GroupBy(x => x.Modality).OrderByDescending(g => g.Sum(x => x.Studies)))
        {
            var key = model.KeyOf(g.Key);
            wm.Cell(mr, 1).Value = g.Key;
            wm.Cell(mr, 2).Value = CompositionModel.NameOf(g.Key);
            wm.Cell(mr, 3).Value = g.Sum(x => x.Studies);
            wm.Cell(mr, 4).Value = g.Sum(x => x.Images);
            wm.Cell(mr, 5).Value = key == CompositionModel.OthersKey ? "Otras" : key;
            mr++;
        }
        wm.Range(2, 3, Math.Max(2, mr - 1), 4).Style.NumberFormat.Format = "#,##0";
        Borders(wm.Range(1, 1, Math.Max(1, mr - 1), mh.Length));
        wm.Column(1).Width = 12; wm.Column(2).Width = 26; wm.Columns(3, 4).Width = 13; wm.Column(5).Width = 22;
        wm.SheetView.FreezeRows(1);

        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        if (slices.Count > 0)
        {
            var colors = slices.Select(s => Hex(s.Color)).ToList();
            var cats   = slices.Select(Label).ToList();
            AddPieCharts(ms, SheetMain,
            [
                new PieSpec("Estudios por modalidad", 'C', firstData, lastData, cats, slices.Select(s => (double)s.Studies).ToList(), colors, 8, 2, 15, 22),
                new PieSpec("Imágenes por modalidad", 'E', firstData, lastData, cats, slices.Select(s => (double)s.Images).ToList(), colors, 15, 2, 22, 22),
            ]);
        }
        return ms.ToArray();
    }

    private static void AddMonthSheet(XLWorkbook wb, string name, CompositionModel model, List<ModalitySlice> slices,
        IReadOnlyList<MigrationCompositionRow> rows, Func<MigrationCompositionRow, long> value)
    {
        var ws = wb.Worksheets.Add(name);
        ws.Cell(1, 1).Value = "Mes";
        for (var i = 0; i < slices.Count; i++) ws.Cell(1, i + 2).Value = Label(slices[i]);
        ws.Cell(1, slices.Count + 2).Value = "Total";
        StyleHeader(ws.Range(1, 1, 1, slices.Count + 2));
        // Cada modalidad con su color de las gráficas.
        for (var i = 0; i < slices.Count; i++) ws.Cell(1, i + 2).Style.Fill.BackgroundColor = XLColor.FromHtml("#" + Hex(slices[i].Color));

        var col = slices.Select((s, i) => (s.Key, i)).ToDictionary(x => x.Key, x => x.i + 2);
        // Meses con estudios, de más antiguo a más reciente; los que no traen fecha válida al final.
        var months = rows.Select(x => x.Month).Distinct()
            .OrderBy(m => m.Length == 6 ? 0 : 1).ThenBy(m => m, StringComparer.Ordinal).ToList();
        var r = 2;
        foreach (var month in months)
        {
            ws.Cell(r, 1).Value = month.Length == 6 ? $"{month[..4]}-{month[4..]}" : "Sin fecha";
            long total = 0;
            foreach (var g in rows.Where(x => x.Month == month).GroupBy(x => model.KeyOf(x.Modality)))
            {
                var v = g.Sum(value);
                ws.Cell(r, col[g.Key]).Value = v;
                total += v;
            }
            ws.Cell(r, slices.Count + 2).Value = total;
            r++;
        }
        ws.Cell(r, 1).Value = "Total";
        for (var c = 2; c <= slices.Count + 2; c++)
            ws.Cell(r, c).FormulaA1 = $"SUM({ws.Cell(2, c).Address}:{ws.Cell(r - 1, c).Address})";
        StyleTotal(ws.Range(r, 1, r, slices.Count + 2));
        ws.Range(2, 2, r, slices.Count + 2).Style.NumberFormat.Format = "#,##0";
        Borders(ws.Range(1, 1, r, slices.Count + 2));
        ws.Column(1).Width = 12;
        ws.Columns(2, slices.Count + 2).Width = 12;
        ws.SheetView.FreezeRows(1);
        ws.SheetView.FreezeColumns(1);
    }

    private static void StyleHeader(IXLRange r)
    {
        r.Style.Font.Bold = true;
        r.Style.Fill.BackgroundColor = XLColor.FromArgb(0x1F, 0x4E, 0x78);
        r.Style.Font.FontColor = XLColor.White;
        r.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        r.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
        r.Style.Alignment.WrapText = true;
    }

    private static void StyleTotal(IXLRange r)
    {
        r.Style.Font.Bold = true;
        r.Style.Fill.BackgroundColor = XLColor.FromArgb(0xD9, 0xEA, 0xD3);
        r.Style.Font.FontColor = XLColor.Black;
    }

    private static void Borders(IXLRange r)
    {
        r.Style.Border.InsideBorder = XLBorderStyleValues.Thin;
        r.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
    }

    // ══ Tartas nativas (Open XML) ═══════════════════════════════════════════════

    /// <summary>Una tarta: valores en <paramref name="ValueColumn"/>, filas FirstRow..LastRow
    /// (1-based) de la hoja; categorías en la columna A. Ancla en celdas 0-based.</summary>
    private sealed record PieSpec(string Title, char ValueColumn, int FirstRow, int LastRow,
        List<string> Categories, List<double> Values, List<string> Colors,
        int FromCol, int FromRow, int ToCol, int ToRow);

    private static void AddPieCharts(MemoryStream xlsx, string sheetName, IReadOnlyList<PieSpec> pies)
    {
        xlsx.Position = 0;
        using (var doc = SpreadsheetDocument.Open(xlsx, true))
        {
            var wbPart = doc.WorkbookPart!;
            var sheet  = wbPart.Workbook.Descendants<Sheet>().First(s => s.Name == sheetName);
            var wsPart = (WorksheetPart)wbPart.GetPartById(sheet.Id!);
            var drawingsPart = wsPart.DrawingsPart ?? wsPart.AddNewPart<DrawingsPart>();
            drawingsPart.WorksheetDrawing ??= new Xdr.WorksheetDrawing();

            uint id = 2;
            foreach (var p in pies)
            {
                var chartPart = drawingsPart.AddNewPart<ChartPart>();
                chartPart.ChartSpace = PieChartSpace(p, sheetName);
                chartPart.ChartSpace.Save();
                drawingsPart.WorksheetDrawing.Append(new Xdr.TwoCellAnchor(
                    new Xdr.FromMarker(new Xdr.ColumnId(p.FromCol.ToString()), new Xdr.ColumnOffset("0"),
                                       new Xdr.RowId(p.FromRow.ToString()), new Xdr.RowOffset("0")),
                    new Xdr.ToMarker(new Xdr.ColumnId(p.ToCol.ToString()), new Xdr.ColumnOffset("0"),
                                     new Xdr.RowId(p.ToRow.ToString()), new Xdr.RowOffset("0")),
                    new Xdr.GraphicFrame(
                        new Xdr.NonVisualGraphicFrameProperties(
                            new Xdr.NonVisualDrawingProperties { Id = id++, Name = p.Title },
                            new Xdr.NonVisualGraphicFrameDrawingProperties()),
                        new Xdr.Transform(new A.Offset { X = 0, Y = 0 }, new A.Extents { Cx = 0, Cy = 0 }),
                        new A.Graphic(new A.GraphicData(new C.ChartReference { Id = drawingsPart.GetIdOfPart(chartPart) })
                            { Uri = "http://schemas.openxmlformats.org/drawingml/2006/chart" }))
                    { Macro = "" },
                    new Xdr.ClientData()));
            }
            drawingsPart.WorksheetDrawing.Save();

            // <drawing> en su sitio dentro de la hoja: antes de legacyDrawing, tableParts, extLst…
            var ws = wsPart.Worksheet;
            if (!ws.Elements<Drawing>().Any())
            {
                var drawing = new Drawing { Id = wsPart.GetIdOfPart(drawingsPart) };
                string[] after = ["legacyDrawing", "legacyDrawingHF", "drawingHF", "picture", "oleObjects",
                                  "controls", "webPublishItems", "tableParts", "extLst"];
                var next = ws.ChildElements.FirstOrDefault(e => after.Contains(e.LocalName));
                if (next is null) ws.Append(drawing); else ws.InsertBefore(drawing, next);
            }
            ws.Save();
        }
        xlsx.Position = 0;
    }

    private static C.ChartSpace PieChartSpace(PieSpec p, string sheetName)
    {
        var sheetRef = $"'{sheetName.Replace("'", "''")}'";
        var catRef   = $"{sheetRef}!$A${p.FirstRow}:$A${p.LastRow}";
        var valRef   = $"{sheetRef}!${p.ValueColumn}${p.FirstRow}:${p.ValueColumn}${p.LastRow}";
        var n = (uint)p.Values.Count;

        var ser = new C.PieChartSeries(
            new C.Index { Val = 0 },
            new C.Order { Val = 0 },
            new C.SeriesText(new C.StringReference(
                new C.Formula($"{sheetRef}!${p.ValueColumn}${HeaderRow}"),
                new C.StringCache(new C.PointCount { Val = 1 },
                    new C.StringPoint(new C.NumericValue(p.Title)) { Index = 0 }))));
        for (var i = 0; i < p.Values.Count; i++)
            ser.Append(new C.DataPoint(
                new C.Index { Val = (uint)i },
                new C.Bubble3D { Val = false },
                new C.ChartShapeProperties(
                    new A.SolidFill(new A.RgbColorModelHex { Val = p.Colors[i] }),
                    new A.Outline(new A.SolidFill(new A.RgbColorModelHex { Val = "FFFFFF" })) { Width = 12700 })));
        ser.Append(new C.DataLabels(
            new C.NumberingFormat { FormatCode = "0.0%", SourceLinked = false },
            new C.TextProperties(new A.BodyProperties(), new A.ListStyle(),
                new A.Paragraph(new A.ParagraphProperties(new A.DefaultRunProperties(
                    new A.SolidFill(new A.RgbColorModelHex { Val = "FFFFFF" })) { FontSize = 900, Bold = true }),
                    new A.EndParagraphRunProperties { Language = "es-ES" })),
            new C.DataLabelPosition { Val = C.DataLabelPositionValues.BestFit },
            new C.ShowLegendKey { Val = false }, new C.ShowValue { Val = false },
            new C.ShowCategoryName { Val = false }, new C.ShowSeriesName { Val = false },
            new C.ShowPercent { Val = true }, new C.ShowBubbleSize { Val = false },
            new C.ShowLeaderLines { Val = true }));

        var catCache = new C.StringCache(new C.PointCount { Val = n });
        for (var i = 0; i < p.Categories.Count; i++)
            catCache.Append(new C.StringPoint(new C.NumericValue(p.Categories[i])) { Index = (uint)i });
        ser.Append(new C.CategoryAxisData(new C.StringReference(new C.Formula(catRef), catCache)));

        var numCache = new C.NumberingCache(new C.FormatCode("General"), new C.PointCount { Val = n });
        for (var i = 0; i < p.Values.Count; i++)
            numCache.Append(new C.NumericPoint(new C.NumericValue(p.Values[i].ToString(System.Globalization.CultureInfo.InvariantCulture))) { Index = (uint)i });
        ser.Append(new C.Values(new C.NumberReference(new C.Formula(valRef), numCache)));

        var chart = new C.Chart(
            new C.Title(
                new C.ChartText(new C.RichText(new A.BodyProperties(), new A.ListStyle(),
                    new A.Paragraph(
                        new A.ParagraphProperties(new A.DefaultRunProperties { FontSize = 1200, Bold = true }),
                        new A.Run(new A.RunProperties { Language = "es-ES", FontSize = 1200, Bold = true }, new A.Text(p.Title))))),
                new C.Overlay { Val = false }),
            new C.AutoTitleDeleted { Val = false },
            new C.PlotArea(new C.Layout(),
                new C.PieChart(new C.VaryColors { Val = true }, ser, new C.FirstSliceAngle { Val = 0 })),
            new C.Legend(new C.LegendPosition { Val = C.LegendPositionValues.Right }, new C.Overlay { Val = false }),
            new C.PlotVisibleOnly { Val = true },
            new C.DisplayBlanksAs { Val = C.DisplayBlanksAsValues.Gap });

        return new C.ChartSpace(
            new C.EditingLanguage { Val = "es-ES" },
            new C.RoundedCorners { Val = false },
            chart);
    }
}
