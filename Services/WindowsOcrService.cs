using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace AirPDF.Services;

public sealed record OcrLineResult(
    string Text,
    double Left,
    double Top,
    double Width,
    double Height);

public sealed record OcrPageResult(string Text, IReadOnlyList<OcrLineResult> Lines);

public sealed record OcrLanguageOption(string Id, string Label);

public static class WindowsOcrService
{
    private static readonly Dictionary<string, OcrEngine?> Engines = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object Gate = new();

    public static IReadOnlyList<OcrLanguageOption> AvailableLanguages()
    {
        var list = new List<OcrLanguageOption> { new("auto", "Авто") };
        try
        {
            foreach (var language in OcrEngine.AvailableRecognizerLanguages
                         .OrderBy(l => l.DisplayName, StringComparer.CurrentCultureIgnoreCase))
            {
                list.Add(new OcrLanguageOption(language.LanguageTag, language.DisplayName));
            }
        }
        catch
        {
            list.Add(new OcrLanguageOption("ru", "Русский"));
            list.Add(new OcrLanguageOption("en", "English"));
        }

        return list;
    }

    public static async Task<string> RecognizeAsync(
        byte[] bgra,
        int width,
        int height,
        string? languageTag = null,
        CancellationToken token = default)
    {
        var page = await RecognizePageAsync(bgra, width, height, languageTag, token);
        return page.Text;
    }

    public static async Task<OcrPageResult> RecognizePageAsync(
        byte[] bgra,
        int width,
        int height,
        string? languageTag = null,
        CancellationToken token = default)
    {
        if (bgra.Length == 0 || width <= 0 || height <= 0)
            return new OcrPageResult(string.Empty, []);

        var engine = GetEngine(languageTag);
        if (engine is null)
            return new OcrPageResult(string.Empty, []);

        try
        {
            using var bitmap = new SoftwareBitmap(BitmapPixelFormat.Bgra8, width, height, BitmapAlphaMode.Premultiplied);
            bitmap.CopyFromBuffer(bgra.AsBuffer());

            token.ThrowIfCancellationRequested();
            var result = await engine.RecognizeAsync(bitmap).AsTask(token);
            if (result is null)
                return new OcrPageResult(string.Empty, []);

            var lines = result.Lines
                .Select(line =>
                {
                    var text = line.Text?.Trim() ?? string.Empty;
                    if (line.Words.Count == 0)
                        return new OcrLineResult(text, 0, 0, 0, 0);

                    var left = line.Words.Min(w => w.BoundingRect.X);
                    var top = line.Words.Min(w => w.BoundingRect.Y);
                    var right = line.Words.Max(w => w.BoundingRect.X + w.BoundingRect.Width);
                    var bottom = line.Words.Max(w => w.BoundingRect.Y + w.BoundingRect.Height);
                    return new OcrLineResult(text, left, top, right - left, bottom - top);
                })
                .Where(l => !string.IsNullOrWhiteSpace(l.Text))
                .OrderBy(l => l.Top)
                .ToList();

            var text = lines.Count > 0
                ? string.Join('\n', lines.Select(l => l.Text))
                : (result.Text?.Trim() ?? string.Empty);

            return new OcrPageResult(text, lines);
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

    private static OcrEngine? GetEngine(string? languageTag)
    {
        var key = string.IsNullOrWhiteSpace(languageTag) || languageTag == "auto"
            ? "auto"
            : languageTag.Trim();

        lock (Gate)
        {
            if (Engines.TryGetValue(key, out var cached))
                return cached;

            OcrEngine? engine = null;
            try
            {
                if (key == "auto")
                {
                    engine = OcrEngine.TryCreateFromUserProfileLanguages()
                             ?? OcrEngine.TryCreateFromLanguage(new Language("ru"))
                             ?? OcrEngine.TryCreateFromLanguage(new Language("en"));
                }
                else
                {
                    engine = OcrEngine.TryCreateFromLanguage(new Language(key))
                             ?? OcrEngine.TryCreateFromUserProfileLanguages();
                }
            }
            catch
            {
                engine = null;
            }

            Engines[key] = engine;
            return engine;
        }
    }
}
