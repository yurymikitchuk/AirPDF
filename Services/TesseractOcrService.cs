using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Tesseract;

namespace AirPDF.Services;

/// <summary>
/// Local Tesseract OCR (eng + rus tessdata_fast), offline.
/// </summary>
public static class TesseractOcrService
{
    private static readonly object Gate = new();
    private static TesseractEngine? _engine;
    private static string? _engineLanguages;
    private static string? _tessDataPath;

    public static bool IsAvailable()
    {
        try
        {
            return Directory.Exists(GetTessDataPath())
                   && File.Exists(Path.Combine(GetTessDataPath(), "eng.traineddata"))
                   && File.Exists(Path.Combine(GetTessDataPath(), "rus.traineddata"));
        }
        catch
        {
            return false;
        }
    }

    public static IReadOnlyList<OcrLanguageOption> AvailableLanguages() =>
    [
        new("auto", "Авто (RU+EN)"),
        new("rus+eng", "Русский + English"),
        new("rus", "Русский"),
        new("eng", "English")
    ];

    /// <summary>Release the shared Tesseract engine (e.g. when a document is closed).</summary>
    public static void Release()
    {
        lock (Gate)
        {
            _engine?.Dispose();
            _engine = null;
            _engineLanguages = null;
        }
    }

    public static Task<OcrPageResult> RecognizePageAsync(
        byte[] bgra,
        int width,
        int height,
        string? languageTag = null,
        CancellationToken token = default)
    {
        return Task.Run(() => RecognizePage(bgra, width, height, languageTag, token), token);
    }

    private static OcrPageResult RecognizePage(
        byte[] bgra,
        int width,
        int height,
        string? languageTag,
        CancellationToken token)
    {
        if (bgra.Length == 0 || width <= 0 || height <= 0 || !IsAvailable())
            return new OcrPageResult(string.Empty, []);

        token.ThrowIfCancellationRequested();

        try
        {
            var languages = MapLanguages(languageTag);
            var engine = GetEngine(languages);
            if (engine is null)
                return new OcrPageResult(string.Empty, []);

            // Preprocess (contrast + soft binarize), fall back to original if OCR is empty.
            var enhanced = ImagePreprocess.EnhanceForOcr(bgra, width, height);
            var result = RunEngine(engine, enhanced, width, height, token);
            if (result.Lines.Count > 0 || !string.IsNullOrWhiteSpace(result.Text))
                return result;

            if (ReferenceEquals(enhanced, bgra))
                return result;

            token.ThrowIfCancellationRequested();
            return RunEngine(engine, bgra, width, height, token);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return new OcrPageResult(string.Empty, []);
        }
    }

    private static OcrPageResult RunEngine(
        TesseractEngine engine,
        byte[] bgra,
        int width,
        int height,
        CancellationToken token)
    {
        var png = EncodePng(bgra, width, height);
        token.ThrowIfCancellationRequested();

        using var pix = Pix.LoadFromMemory(png);
        lock (Gate)
        {
            token.ThrowIfCancellationRequested();
            using var page = engine.Process(pix, PageSegMode.Auto);
            var fullText = page.GetText()?.Trim() ?? string.Empty;
            var lines = ExtractLines(page);
            if (lines.Count == 0 && fullText.Length > 0)
                return new OcrPageResult(fullText, []);

            var text = lines.Count > 0
                ? string.Join('\n', lines.Select(l => l.Text))
                : fullText;
            return new OcrPageResult(text, lines);
        }
    }

    private static List<OcrLineResult> ExtractLines(Page page)
    {
        var lines = new List<OcrLineResult>();
        using var iter = page.GetIterator();
        iter.Begin();

        do
        {
            if (!iter.TryGetBoundingBox(PageIteratorLevel.TextLine, out var bounds))
                continue;

            var text = iter.GetText(PageIteratorLevel.TextLine)?.Trim();
            if (string.IsNullOrWhiteSpace(text))
                continue;

            lines.Add(new OcrLineResult(
                text,
                bounds.X1,
                bounds.Y1,
                Math.Max(1, bounds.X2 - bounds.X1),
                Math.Max(1, bounds.Y2 - bounds.Y1)));
        }
        while (iter.Next(PageIteratorLevel.TextLine));

        return lines
            .OrderBy(l => l.Top)
            .ThenBy(l => l.Left)
            .ToList();
    }

    private static TesseractEngine? GetEngine(string languages)
    {
        lock (Gate)
        {
            if (_engine is not null &&
                string.Equals(_engineLanguages, languages, StringComparison.OrdinalIgnoreCase))
                return _engine;

            _engine?.Dispose();
            _engine = null;
            _engineLanguages = null;

            try
            {
                var path = GetTessDataPath();
                _engine = new TesseractEngine(path, languages, EngineMode.Default);
                _engine.SetVariable("user_defined_dpi", "150");
                _engineLanguages = languages;
                return _engine;
            }
            catch
            {
                _engine = null;
                return null;
            }
        }
    }

    private static string MapLanguages(string? languageTag)
    {
        if (string.IsNullOrWhiteSpace(languageTag) ||
            languageTag.Equals("auto", StringComparison.OrdinalIgnoreCase) ||
            languageTag.Contains('+', StringComparison.Ordinal))
        {
            if (string.Equals(languageTag, "eng+rus", StringComparison.OrdinalIgnoreCase))
                return "eng+rus";
            return "rus+eng";
        }

        var tag = languageTag.Trim().ToLowerInvariant();
        if (tag.StartsWith("ru"))
            return "rus";
        if (tag.StartsWith("en"))
            return "eng";
        return "rus+eng";
    }

    private static string GetTessDataPath()
    {
        if (_tessDataPath is not null)
            return _tessDataPath;

        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "tessdata"),
            Path.Combine(Directory.GetCurrentDirectory(), "tessdata"),
            Path.Combine(AppContext.BaseDirectory, "..", "tessdata")
        };

        foreach (var candidate in candidates)
        {
            var full = Path.GetFullPath(candidate);
            if (Directory.Exists(full) && File.Exists(Path.Combine(full, "eng.traineddata")))
            {
                _tessDataPath = full;
                return full;
            }
        }

        _tessDataPath = Path.Combine(AppContext.BaseDirectory, "tessdata");
        return _tessDataPath;
    }

    private static byte[] EncodePng(byte[] bgra, int width, int height)
    {
        var flattened = FlattenOntoWhite(bgra);
        var bitmap = BitmapSource.Create(
            width, height, 96, 96, PixelFormats.Bgra32, null, flattened, width * 4);
        bitmap.Freeze();

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    private static byte[] FlattenOntoWhite(byte[] bgra)
    {
        var result = new byte[bgra.Length];
        for (var i = 0; i < bgra.Length; i += 4)
        {
            var b = bgra[i];
            var g = bgra[i + 1];
            var r = bgra[i + 2];
            var a = bgra[i + 3];
            if (a >= 250)
            {
                result[i] = b;
                result[i + 1] = g;
                result[i + 2] = r;
                result[i + 3] = 255;
                continue;
            }

            if (a == 0)
            {
                result[i] = 255;
                result[i + 1] = 255;
                result[i + 2] = 255;
                result[i + 3] = 255;
                continue;
            }

            var alpha = a / 255.0;
            result[i] = (byte)Math.Round(b * alpha + 255 * (1 - alpha));
            result[i + 1] = (byte)Math.Round(g * alpha + 255 * (1 - alpha));
            result[i + 2] = (byte)Math.Round(r * alpha + 255 * (1 - alpha));
            result[i + 3] = 255;
        }

        return result;
    }
}
