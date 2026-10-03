using FastReport;
using FastReport.Barcode;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using System.Data;

namespace Rim.Services;

// Template-driven label PDF generation.
//
// Division of labor:
//   - FastReport Open Source (MIT) owns layout: the .frx template in
//     Reports/ positions every object, binds the Labels table, and evaluates
//     expressions. Edit the template with FastReport Designer Community
//     Edition — no code changes needed for layout tweaks.
//   - PDFsharp owns the PDF bytes: this service walks the prepared report
//     pages and redraws each object as vector output. Barcodes are drawn as
//     rectangles via ZXing (shared with LabelPdfService), never rasterized —
//     the open-source FastReport PDF export renders pages as bitmaps, which
//     would soften barcodes on direct-thermal printers.
//
// Report units are 1/96 inch; PDF points are 1/72 inch.
//
// System.Drawing note: FastReport's object model exposes System.Drawing
// types (Font, Color). CA1416 flags those APIs as Windows-only, but the
// production target is win-x64 and the test environment provides libgdiplus,
// so they work on both. The suppression below is scoped to this file.
#pragma warning disable CA1416
public sealed class FastReportLabelService
{
    public const string TemplateFileName = "Label4x2.frx";

    private readonly string _templatePath;

    public FastReportLabelService()
        : this(Path.Combine(AppContext.BaseDirectory, "Reports", TemplateFileName))
    {
    }

    internal FastReportLabelService(string templatePath) => _templatePath = templatePath;

    public byte[] RenderLabels(IReadOnlyList<LabelItem> items, string printedBy,
        DateTime? printedAt = null)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (!File.Exists(_templatePath))
            throw new FileNotFoundException(
                $"Label template not found: {_templatePath}", _templatePath);
        var at = printedAt ?? DateTime.Now;

        using var report = new Report();
        report.Load(_templatePath);
        report.RegisterData(BuildTable(items), "Labels");
        report.SetParameterValue("PrintedBy", printedBy);
        report.SetParameterValue("PrintedAt", at.ToString("yyyy-MM-dd HH:mm"));
        if (!report.Prepare())
            throw new InvalidOperationException(
                "FastReport could not prepare the label template.");

        using var doc = new PdfDocument();
        doc.Info.Title = "RIM Inventory Labels";
        doc.Info.Creator = "Records Inventory Manager";
        for (int p = 0; p < report.PreparedPages.Count; p++)
            RenderPage(doc, report.PreparedPages.GetPage(p));

        using var ms = new MemoryStream();
        doc.Save(ms);
        return ms.ToArray();
    }

    private static DataTable BuildTable(IReadOnlyList<LabelItem> items)
    {
        var table = new DataTable("Labels");
        table.Columns.Add("Title", typeof(string));
        table.Columns.Add("Line2", typeof(string));
        table.Columns.Add("Barcode", typeof(string));
        foreach (var i in items)
            // The template clips (WordWrap=false); truncate with ellipsis up
            // front so long titles degrade gracefully, as the old renderer did.
            table.Rows.Add(Truncate(i.Title, 60), Truncate(i.Line2, 80), i.Barcode);
        return table;
    }

    private static string Truncate(string text, int max)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= max) return text ?? "";
        return text[..Math.Max(0, max - 3)] + "...";
    }

    private static void RenderPage(PdfDocument doc, ReportPage page)
    {
        var pdfPage = doc.AddPage();
        pdfPage.Width = XUnit.FromPoint(ToPt(page.PaperWidth));
        pdfPage.Height = XUnit.FromPoint(ToPt(page.PaperHeight));
        using var gfx = XGraphics.FromPdfPage(pdfPage);
        foreach (var obj in page.AllObjects)
        {
            switch (obj)
            {
                case TextObject t: DrawText(gfx, t); break;
                case BarcodeObject b: DrawBarcode(gfx, b); break;
            }
        }
    }

    private static double ToPt(float units) => units * 72.0 / 96.0;

    private static void DrawText(XGraphics gfx, TextObject t)
    {
        if (string.IsNullOrEmpty(t.Text)) return;
        var f = t.Font;
        var style = XFontStyleEx.Regular;
        if (f.Bold) style |= XFontStyleEx.Bold;
        if (f.Italic) style |= XFontStyleEx.Italic;
        var xfont = new XFont(f.FontFamily.Name, f.Size, style);
        var fmt = t.HorzAlign switch
        {
            HorzAlign.Center => XStringFormats.TopCenter,
            HorzAlign.Right => XStringFormats.TopRight,
            _ => XStringFormats.TopLeft,
        };
        var brush = new XSolidBrush(XColor.FromArgb(t.TextColor.ToArgb()));
        gfx.DrawString(t.Text, xfont, brush,
            new XRect(ToPt(t.AbsLeft), ToPt(t.AbsTop), ToPt(t.Width), ToPt(t.Height)), fmt);
    }

    private static void DrawBarcode(XGraphics gfx, BarcodeObject b)
    {
        if (string.IsNullOrEmpty(b.Text)) return;
        LabelPdfService.DrawBarcodeBars(gfx, b.Text,
            ToPt(b.AbsLeft), ToPt(b.AbsTop), ToPt(b.Width), ToPt(b.Height));
    }
}
#pragma warning restore CA1416
