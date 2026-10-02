using PdfSharp.Fonts;

namespace Rim.Services;

// PDFsharp 6.x ships with NO default font resolver: GlobalFontSettings.FontResolver
// is null, so `new XFont(...)` throws InvalidOperationException on every machine
// (Windows included) unless the host installs one. v0.14.0 missed this — the
// sandbox test masked it with a test-only resolver — and the label PDF button
// crashed the Blazor circuit on first click. This resolver serves real faces
// from the OS font directories (Windows %WINDIR%\Fonts first), so production
// gets Arial and the same code path runs in Linux test/dev containers via
// DejaVu/Liberation/Noto fallbacks.
public sealed class PdfSharpFontResolver : IFontResolver
{
    public string DefaultFontName => "Arial";

    private readonly string[] _dirs;
    private readonly Dictionary<string, string> _facePaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();

    public PdfSharpFontResolver()
    {
        var dirs = new List<string>();
        var windir = Environment.GetEnvironmentVariable("WINDIR");
        if (!string.IsNullOrEmpty(windir)) dirs.Add(Path.Combine(windir, "Fonts"));
        // Same code path on Linux/macOS (tests, dev containers).
        dirs.Add("/usr/share/fonts");
        dirs.Add("/usr/local/share/fonts");
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(home)) dirs.Add(Path.Combine(home, ".fonts"));
        _dirs = dirs.Where(Directory.Exists).ToArray();
    }

    public FontResolverInfo? ResolveTypeface(string familyName, bool isBold, bool isItalic)
    {
        // Requested family first (degrades to its regular face), then Arial,
        // then the common Linux sans families — each degrading to regular.
        var found = FindFace(familyName, isBold, isItalic)
            ?? (familyName.Equals("Arial", StringComparison.OrdinalIgnoreCase)
                ? null : FindFace("Arial", isBold, isItalic))
            ?? FindFace("DejaVu Sans", isBold, isItalic)
            ?? FindFace("Liberation Sans", isBold, isItalic)
            ?? FindFace("Noto Sans", isBold, isItalic);
        if (found is null) return null; // XFont then throws its documented guidance.
        var faceKey = Path.GetFileName(found.Value.Path);
        lock (_lock) _facePaths[faceKey] = found.Value.Path;
        // PDFsharp does not implement bold simulation (must stay false); italic
        // may be simulated when only a regular face exists.
        return new FontResolverInfo(faceKey, false, isItalic && !found.Value.IsItalic);
    }

    public byte[]? GetFont(string faceName)
    {
        lock (_lock)
            return _facePaths.TryGetValue(faceName, out var path) ? File.ReadAllBytes(path) : null;
    }

    private (string Path, bool IsItalic)? FindFace(string family, bool bold, bool italic)
    {
        var key = Normalize(family);
        string? regular = null;
        foreach (var file in EnumerateFontFiles())
        {
            var name = Normalize(Path.GetFileNameWithoutExtension(file));
            if (!name.StartsWith(key, StringComparison.Ordinal)) continue;
            var style = name.Substring(key.Length);
            bool isB = IsBoldStyle(style);
            bool isI = IsItalicStyle(style);
            if (!isB && !isI) regular ??= file;
            if (isB == bold && isI == italic) return (file, isI);
        }
        return regular is null ? null : (regular, false);
    }

    private IEnumerable<string> EnumerateFontFiles()
    {
        foreach (var dir in _dirs)
        {
            IEnumerable<string> files = Enumerable.Empty<string>();
            try
            {
                files = Directory.EnumerateFiles(dir, "*.ttf", SearchOption.AllDirectories)
                    .Concat(Directory.EnumerateFiles(dir, "*.otf", SearchOption.AllDirectories));
            }
            catch { /* unreadable dir — skip */ }
            foreach (var f in files) yield return f;
        }
    }

    private static string Normalize(string s) =>
        new string(s.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();

    // Windows uses terse suffixes (arialbd.ttf, ariali.ttf, arialbi.ttf);
    // cross-platform families use -Bold/-Italic/-Oblique.
    private static bool IsBoldStyle(string style) =>
        style.Contains("bold") || style.Contains("black") || style.Contains("heavy") ||
        style is "bd" or "bi";

    private static bool IsItalicStyle(string style) =>
        style.Contains("italic") || style.Contains("oblique") ||
        style is "i" or "bi";
}
