namespace AirPDF.Services;

/// <summary>
/// OCR facade: prefers local Tesseract (rus+eng), falls back to Windows OCR.
/// </summary>
public static class DocumentOcrService
{
    public static IReadOnlyList<OcrLanguageOption> AvailableLanguages()
    {
        if (TesseractOcrService.IsAvailable())
            return TesseractOcrService.AvailableLanguages();

        return WindowsOcrService.AvailableLanguages();
    }

    public static async Task<OcrPageResult> RecognizePageAsync(
        byte[] bgra,
        int width,
        int height,
        string? languageTag = null,
        CancellationToken token = default)
    {
        if (TesseractOcrService.IsAvailable())
        {
            var tess = await TesseractOcrService.RecognizePageAsync(
                bgra, width, height, languageTag, token);
            if (tess.Lines.Count > 0 || !string.IsNullOrWhiteSpace(tess.Text))
                return tess;
        }

        return await WindowsOcrService.RecognizePageAsync(
            bgra, width, height, languageTag, token);
    }
}
