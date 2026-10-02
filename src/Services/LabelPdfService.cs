using PdfSharp.Drawing;
using PdfSharp.Pdf;
using ZXing;
using ZXing.Common;
using ZXing.Rendering;

namespace Rim.Services;

// Label-sheet PDF generation via PDFsharp — the same PDF library Content
// Manager 24.3 bundles (CM24.3_ThirdPartyIP.pdf lists PDFsharp 1.5; this is
// the current 6.x line of that library, the release compatible with .NET 8).
// Barcodes are real, scannable Code 128 symbols rendered by ZXing and drawn
// as vector rectangles — never images — so they stay sharp at any print DPI.
public sealed record LabelItem(string Title, string Line2, string Barcode);

public sealed class LabelPdfService
{
    // 4" x 2" inventory label page, in points. Matches common direct-thermal
    // label stock; one label per page so it feeds label printers 1:1.
    public const double LabelWidthPt = 288;
    public const double LabelHeightPt = 144;

    // Font family used for label text. "Arial" on Windows (production);
    // override in tests where Arial is unavailable.
    public string FontFamily { get; set; } = "Arial";

    public byte[] RenderLabels(IReadOnlyList<LabelItem> items, string printedBy,
        DateTime? printedAt = null)
    {
        ArgumentNullException.ThrowIfNull(items);
        var at = printedAt ?? DateTime.Now;
        using var doc = new PdfDocument();
        doc.Info.Title = "RIM Inventory Labels";
        doc.Info.Creator = "Records Inventory Manager";

        foreach (var item in items)
            RenderOne(doc, item, printedBy, at);

        using var ms = new MemoryStream();
        doc.Save(ms);
        return ms.ToArray();
    }

    private void RenderOne(PdfDocument doc, LabelItem item, string printedBy, DateTime at)
    {
        var page = doc.AddPage();
        page.Width = XUnit.FromPoint(LabelWidthPt);
        page.Height = XUnit.FromPoint(LabelHeightPt);
        using var gfx = XGraphics.FromPdfPage(page);

        const double margin = 10;
        double y = margin;
        var usable = LabelWidthPt - 2 * margin;

        // Title (bold) + Line2, truncated with ellipsis to fit.
        var titleFont = new XFont(FontFamily, 13, XFontStyleEx.Bold);
        gfx.DrawString(Truncate(gfx, item.Title, titleFont, usable),
            titleFont, XBrushes.Black, new XRect(margin, y, usable, 18),
            XStringFormats.TopLeft);
        y += 19;

        var line2Font = new XFont(FontFamily, 10, XFontStyleEx.Regular);
        gfx.DrawString(Truncate(gfx, item.Line2, line2Font, usable),
            line2Font, XBrushes.Black, new XRect(margin, y, usable, 14),
            XStringFormats.TopLeft);
        y += 18;

        // Barcode symbol: ZXing-encoded, drawn as vector bars.
        var matrix = EncodeBarcode(item.Barcode);
        const double barHeight = 52;
        if (matrix is { Width: > 0 })
        {
            double scale = usable / matrix.Width;
            double barW = matrix.Width * scale;
            double x0 = margin + (usable - barW) / 2;
            double y0 = y;
            int h = matrix.Height;
            for (int x = 0; x < matrix.Width; x++)
            {
                if (!IsDark(matrix, x, h / 2)) continue;
                int xEnd = x;
                while (xEnd + 1 < matrix.Width && IsDark(matrix, xEnd + 1, h / 2)) xEnd++;
                gfx.DrawRectangle(XBrushes.Black,
                    x0 + x * scale, y0, (xEnd - x + 1) * scale, barHeight);
                x = xEnd;
            }
            y += barHeight + 3;
        }

        // Human-readable barcode text under the symbol.
        var codeFont = new XFont(FontFamily, 9, XFontStyleEx.Regular);
        gfx.DrawString(item.Barcode, codeFont, XBrushes.Black,
            new XRect(margin, y, usable, 12), XStringFormats.TopCenter);
        y += 14;

        // Footer.
        var footFont = new XFont(FontFamily, 7, XFontStyleEx.Regular);
        gfx.DrawString($"Printed {at:yyyy-MM-dd HH:mm} by {printedBy}",
            footFont, XBrushes.Gray,
            new XRect(margin, LabelHeightPt - margin - 10, usable, 10),
            XStringFormats.BottomLeft);
    }

    private static string Truncate(XGraphics gfx, string text, XFont font, double maxWidth)
    {
        if (string.IsNullOrEmpty(text)) return "";
        if (gfx.MeasureString(text, font).Width <= maxWidth) return text;
        const string ell = "...";
        int lo = 0, hi = text.Length;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (gfx.MeasureString(text[..mid] + ell, font).Width <= maxWidth) lo = mid + 1;
            else hi = mid;
        }
        return text[..Math.Max(0, lo - 1)] + ell;
    }

    private static bool IsDark(PixelData pd, int x, int y)
    {
        int o = (y * pd.Width + x) * 4;
        return pd.Pixels[o] < 128 && pd.Pixels[o + 1] < 128 && pd.Pixels[o + 2] < 128;
    }

    // Encode to Code 128 (full ASCII). Falls back to Code 39 for restricted
    // charsets; returns null only when no bars can be produced, in which case
    // the label still prints the human-readable text.
    internal static PixelData? EncodeBarcode(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        try { return WriteBars(text, BarcodeFormat.CODE_128); }
        catch { /* fall through to Code 39 */ }
        try
        {
            var sanitized = new string(text.ToUpperInvariant()
                .Where(c => (c >= '0' && c <= '9') || (c >= 'A' && c <= 'Z') || " -. $/+%".Contains(c))
                .ToArray());
            return sanitized.Length > 0 ? WriteBars(sanitized, BarcodeFormat.CODE_39) : null;
        }
        catch { return null; }
    }

    private static PixelData WriteBars(string text, BarcodeFormat format)
    {
        var writer = new BarcodeWriterPixelData
        {
            Format = format,
            Options = new EncodingOptions { Width = 600, Height = 120, Margin = 0, PureBarcode = true }
        };
        return writer.Write(text);
    }

    // Same ZXing encoding as the PDF, as inline SVG for the on-screen
    // preview in LabelDialog. No image encoding involved.
    public static string BarcodeSvg(string text, int barHeightPx = 44)
    {
        var matrix = EncodeBarcode(text);
        if (matrix is not { Width: > 0 }) return "";
        var sb = new System.Text.StringBuilder();
        sb.Append($"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{matrix.Width}\" height=\"{barHeightPx}\" viewBox=\"0 0 {matrix.Width} {barHeightPx}\" preserveAspectRatio=\"none\" role=\"img\" aria-label=\"barcode\">");
        int h = matrix.Height;
        for (int x = 0; x < matrix.Width; x++)
        {
            if (!IsDark(matrix, x, h / 2)) continue;
            int xEnd = x;
            while (xEnd + 1 < matrix.Width && IsDark(matrix, xEnd + 1, h / 2)) xEnd++;
            sb.Append($"<rect x=\"{x}\" y=\"0\" width=\"{xEnd - x + 1}\" height=\"{barHeightPx}\"/>");
            x = xEnd;
        }
        sb.Append("</svg>");
        return sb.ToString();
    }
}
