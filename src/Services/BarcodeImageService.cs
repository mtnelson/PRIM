using SkiaSharp;
using ZXing;
using ZXing.Common;

namespace Rim.Services;

// Decodes barcodes from uploaded images (photos of labels, screenshots)
// using ZXing. SkiaSharp plays a supporting role only: it decodes the
// JPEG/PNG bytes into pixels, which ZXing then reads. Used by the
// Tools -> Barcode Scanning page as an alternative to keyboard-wedge input.
public sealed class BarcodeImageService
{
    private const int MaxBytes = 10 * 1024 * 1024;

    public IReadOnlyList<string> DecodeBarcodes(byte[] imageBytes)
    {
        if (imageBytes is not { Length: > 0 } || imageBytes.Length > MaxBytes)
            return Array.Empty<string>();

        // SKBitmap.Decode throws (rather than returning null) on unrecognized
        // bytes; treat any undecodable input as "no barcodes".
        SKBitmap? bitmap;
        try { bitmap = SKBitmap.Decode(imageBytes); }
        catch { return Array.Empty<string>(); }
        using (bitmap)
        {
            if (bitmap is null) return Array.Empty<string>();

            var found = new HashSet<string>(StringComparer.Ordinal);
            TryDecode(bitmap, found);

            // Large phone photos decode more reliably at half resolution.
            if (found.Count == 0 && (bitmap.Width > 800 || bitmap.Height > 800))
            {
                using var small = bitmap.Resize(
                    new SKImageInfo(bitmap.Width / 2, bitmap.Height / 2),
                    SKSamplingOptions.Default);
                if (small is not null) TryDecode(small, found);
            }
            return found.ToList();
        }
    }

    private static void TryDecode(SKBitmap bitmap, HashSet<string> found)
    {
        // SKColor-based copy: agnostic to the bitmap's native color type.
        var rgb = new byte[bitmap.Width * bitmap.Height * 3];
        int i = 0;
        foreach (var c in bitmap.Pixels)
        {
            rgb[i++] = c.Red;
            rgb[i++] = c.Green;
            rgb[i++] = c.Blue;
        }
        var source = new RGBLuminanceSource(rgb, bitmap.Width, bitmap.Height,
            RGBLuminanceSource.BitmapFormat.RGB24);
        var reader = new BarcodeReaderGeneric
        {
            AutoRotate = true,
            Options = new DecodingOptions { TryHarder = true }
        };
        var results = reader.DecodeMultiple(source);
        if (results is null) return;
        foreach (var r in results)
            if (!string.IsNullOrWhiteSpace(r?.Text))
                found.Add(r.Text.Trim());
    }
}
