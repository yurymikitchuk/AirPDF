using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using A = DocumentFormat.OpenXml.Drawing;
using DW = DocumentFormat.OpenXml.Drawing.Wordprocessing;
using PIC = DocumentFormat.OpenXml.Drawing.Pictures;

namespace AirPDF.Services;

public enum WordExportMode
{
    EditableText,
    PreserveAppearance
}

public sealed class WordExportOptions
{
    public required string OutputPath { get; init; }
    public WordExportMode Mode { get; init; } = WordExportMode.EditableText;
    public string OcrLanguage { get; init; } = "auto";
    /// <summary>0-based PDF page indices. Null/empty = all pages.</summary>
    public IReadOnlyList<int>? Pages { get; init; }
}

public sealed class WordExportService
{
    private const double PreferredRenderScale = 2.0;
    private const double MaximumRenderedDimension = 2400.0;
    private const uint PageMarginTwips = 72;
    private const long EmusPerTwip = 635;

    private readonly PdfRenderer _renderer;

    public WordExportService(PdfRenderer renderer)
    {
        _renderer = renderer;
    }

    public async Task ExportAsync(
        WordExportOptions options,
        IProgress<(int Page, int Total, string Status)>? progress = null,
        CancellationToken token = default)
    {
        if (!_renderer.IsOpen)
            throw new InvalidOperationException("Документ не открыт.");

        var directory = Path.GetDirectoryName(options.OutputPath);
        if (string.IsNullOrWhiteSpace(directory))
            directory = Environment.CurrentDirectory;
        Directory.CreateDirectory(directory);

        var temporaryPath = Path.Combine(directory, $"~AirPDF-{Guid.NewGuid():N}.docx");

        try
        {
            await Task.Run(
                () => WriteDocumentAsync(temporaryPath, options, progress, token),
                token);

            token.ThrowIfCancellationRequested();
            await MoveWithRetryAsync(temporaryPath, options.OutputPath, token);
        }
        finally
        {
            TryDelete(temporaryPath);
        }
    }

    private async Task WriteDocumentAsync(
        string path,
        WordExportOptions options,
        IProgress<(int Page, int Total, string Status)>? progress,
        CancellationToken token)
    {
        var pages = ResolveExportPages(options);
        var total = pages.Count;
        if (total == 0)
            throw new InvalidOperationException("Не выбрано ни одной страницы для экспорта.");

        using var document = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document);
        var main = document.AddMainDocumentPart();
        var body = new Body();
        main.Document = new Document(body);
        document.PackageProperties.Title =
            Path.GetFileNameWithoutExtension(_renderer.FilePath) ?? "AirPDF export";
        document.PackageProperties.Creator = "AirPDF";

        for (var exportIndex = 0; exportIndex < total; exportIndex++)
        {
            var pageIndex = pages[exportIndex];
            token.ThrowIfCancellationRequested();
            progress?.Report((
                exportIndex + 1,
                total,
                $"Страница {exportIndex + 1} / {total} (PDF {pageIndex + 1}): подготовка…"));

            var pageSize = _renderer.GetNativePageSize(pageIndex);
            var (pageWidth, pageHeight) = ToPageSize(pageSize);
            var nativeLines = ExtractEditableLines(pageIndex, pageSize);
            var lines = nativeLines;
            (byte[] Bgra, int Width, int Height)? raw = null;

            // Hybrid: keep a good PDF text layer; OCR when sparse/garbage; pick denser result.
            if (NeedsOcr(nativeLines))
            {
                progress?.Report((
                    exportIndex + 1,
                    total,
                    $"Страница {exportIndex + 1} / {total} (PDF {pageIndex + 1}): OCR…"));

                var renderScale = GetRenderScale(pageSize);
                raw = _renderer.RenderPageRaw(pageIndex, renderScale);
                token.ThrowIfCancellationRequested();
                var ocr = await DocumentOcrService.RecognizePageAsync(
                    raw.Value.Bgra,
                    raw.Value.Width,
                    raw.Value.Height,
                    options.OcrLanguage,
                    token);
                var ocrLines = TextLayoutBuilder.BuildLinesFromOcr(
                        ocr.Lines,
                        raw.Value.Width,
                        raw.Value.Height)
                    .Select(l => l with { Text = NormalizeText(l.Text) })
                    .Where(l => l.Text.Length > 0)
                    .ToList();
                if (ocrLines.Count == 0 && !string.IsNullOrWhiteSpace(ocr.Text))
                    ocrLines = CreatePlainLayoutLines(NormalizeText(ocr.Text));

                lines = PickBetterLines(nativeLines, ocrLines);
            }

            var hiddenText = string.Join('\n', lines.Select(l => l.Text));

            progress?.Report((
                exportIndex + 1,
                total,
                $"Страница {exportIndex + 1} / {total} (PDF {pageIndex + 1}): Word…"));

            if (options.Mode == WordExportMode.PreserveAppearance)
            {
                token.ThrowIfCancellationRequested();
                raw ??= _renderer.RenderPageRaw(pageIndex, GetRenderScale(pageSize));
                token.ThrowIfCancellationRequested();
                AppendAppearancePage(
                    main,
                    body,
                    raw.Value,
                    hiddenText,
                    exportIndex,
                    total,
                    pageIndex,
                    pageWidth,
                    pageHeight,
                    token);
            }
            else
            {
                token.ThrowIfCancellationRequested();
                raw ??= _renderer.RenderPageRaw(pageIndex, GetRenderScale(pageSize));
                token.ThrowIfCancellationRequested();
                var ruled = TableGridDetector.Detect(raw.Value.Bgra, raw.Value.Width, raw.Value.Height);
                var borderless = BorderlessTableDetector.Detect(lines);
                var grids = MergeTableGrids(ruled, borderless);
                AppendEditablePage(body, lines, grids, exportIndex, total, pageWidth, pageHeight);
            }

            if (exportIndex == total - 1)
            {
                body.Append(CreateSection(
                    pageWidth,
                    pageHeight,
                    nextPage: false,
                    editable: options.Mode == WordExportMode.EditableText));
            }
        }

        token.ThrowIfCancellationRequested();
        progress?.Report((total, total, "Сохранение Word…"));
        main.Document.Save();
    }

    private List<int> ResolveExportPages(WordExportOptions options)
    {
        var count = _renderer.PageCount;
        if (options.Pages is null || options.Pages.Count == 0)
            return Enumerable.Range(0, count).ToList();

        return options.Pages
            .Where(p => p >= 0 && p < count)
            .Distinct()
            .ToList();
    }

    private List<LayoutLine> ExtractEditableLines(int pageIndex, Size pageSize)
    {
        try
        {
            var characters = _renderer.GetPageCharacters(pageIndex);
            var lines = TextLayoutBuilder.BuildLinesFromCharacters(
                    characters,
                    pageSize.Width,
                    pageSize.Height)
                .Select(l => l with { Text = NormalizeText(l.Text) })
                .Where(l => l.Text.Length > 0)
                .ToList();
            if (lines.Count > 0)
                return lines;
        }
        catch
        {
            // Fall back to plain text.
        }

        try
        {
            var plain = NormalizeText(_renderer.GetPageText(pageIndex));
            return string.IsNullOrWhiteSpace(plain)
                ? []
                : CreatePlainLayoutLines(plain);
        }
        catch
        {
            // A malformed text layer must not prevent image/OCR export.
            return [];
        }
    }

    private static List<LayoutLine> CreatePlainLayoutLines(string text)
    {
        var source = text.Split(
            '\n',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var count = Math.Max(1, source.Length);
        return source
            .Select((line, index) => new LayoutLine(
                NormalizeText(line),
                0,
                index / (double)count,
                1,
                1.0 / count))
            .Where(l => l.Text.Length > 0)
            .ToList();
    }

    private static bool HasUsefulLines(IReadOnlyList<LayoutLine> lines)
    {
        var text = string.Join(' ', lines.Select(l => l.Text));
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var visible = text.Count(c => !char.IsWhiteSpace(c));
        var lettersAndDigits = text.Count(char.IsLetterOrDigit);
        var invalid = text.Count(c => c is '\uFFFD' or '\0');
        return lettersAndDigits >= 12
               && lettersAndDigits >= visible * 0.35
               && invalid <= Math.Max(1, visible / 100);
    }

    private static bool NeedsOcr(IReadOnlyList<LayoutLine> native)
    {
        if (!HasUsefulLines(native))
            return true;

        var letters = native.Sum(l => l.Text.Count(char.IsLetterOrDigit));
        if (letters < 48)
            return true;

        // Sparse text layer (likely scan with a few embedded labels).
        if (native.Count < 4 && letters < 120)
            return true;

        return false;
    }

    private static List<LayoutLine> PickBetterLines(
        IReadOnlyList<LayoutLine> native,
        IReadOnlyList<LayoutLine> ocr)
    {
        var nativeScore = ScoreLines(native);
        var ocrScore = ScoreLines(ocr);
        if (ocrScore == 0 && nativeScore == 0)
            return ocr.Count > 0 ? ocr.ToList() : native.ToList();
        if (ocrScore >= nativeScore * 1.12)
            return ocr.ToList();
        if (nativeScore > 0)
            return native.ToList();
        return ocr.ToList();
    }

    private static int ScoreLines(IReadOnlyList<LayoutLine> lines)
    {
        if (lines.Count == 0)
            return 0;

        var text = string.Join(' ', lines.Select(l => l.Text));
        var letters = text.Count(char.IsLetterOrDigit);
        var invalid = text.Count(c => c is '\uFFFD' or '\0');
        if (letters < 8)
            return 0;

        var multiCellRows = 0;
        var ordered = lines.OrderBy(l => l.TopRatio).ToList();
        var rowBuckets = new List<List<LayoutLine>>();
        foreach (var line in ordered)
        {
            if (rowBuckets.Count == 0
                || Math.Abs(line.TopRatio - rowBuckets[^1][0].TopRatio) > 0.012)
                rowBuckets.Add([line]);
            else
                rowBuckets[^1].Add(line);
        }

        multiCellRows = rowBuckets.Count(r => r.Count >= 2);
        return letters * 2 + lines.Count + multiCellRows * 8 - invalid * 20;
    }

    private static IReadOnlyList<DetectedTableGrid> MergeTableGrids(
        IReadOnlyList<DetectedTableGrid> ruled,
        IReadOnlyList<DetectedTableGrid> borderless)
    {
        if (ruled.Count == 0)
            return borderless;
        if (borderless.Count == 0)
            return ruled;

        var merged = ruled.ToList();
        foreach (var candidate in borderless)
        {
            var overlaps = merged.Any(existing => GridsOverlap(existing, candidate));
            if (!overlaps)
                merged.Add(candidate);
        }

        return merged
            .OrderByDescending(g =>
                (g.VerticalEdges[^1] - g.VerticalEdges[0]) *
                (g.HorizontalEdges[^1] - g.HorizontalEdges[0]))
            .ToList();
    }

    private static bool GridsOverlap(DetectedTableGrid a, DetectedTableGrid b)
    {
        var ax0 = a.VerticalEdges[0];
        var ax1 = a.VerticalEdges[^1];
        var ay0 = a.HorizontalEdges[0];
        var ay1 = a.HorizontalEdges[^1];
        var bx0 = b.VerticalEdges[0];
        var bx1 = b.VerticalEdges[^1];
        var by0 = b.HorizontalEdges[0];
        var by1 = b.HorizontalEdges[^1];

        var overlapX = Math.Max(0, Math.Min(ax1, bx1) - Math.Max(ax0, bx0));
        var overlapY = Math.Max(0, Math.Min(ay1, by1) - Math.Max(ay0, by0));
        var overlap = overlapX * overlapY;
        var areaA = Math.Max(0.0001, (ax1 - ax0) * (ay1 - ay0));
        var areaB = Math.Max(0.0001, (bx1 - bx0) * (by1 - by0));
        return overlap >= Math.Min(areaA, areaB) * 0.45;
    }

    private static void AppendAppearancePage(
        MainDocumentPart main,
        Body body,
        (byte[] Bgra, int Width, int Height) raw,
        string text,
        int exportIndex,
        int exportTotal,
        int pdfPageIndex,
        uint pageWidth,
        uint pageHeight,
        CancellationToken token)
    {
        using var image = EncodePageImage(raw.Bgra, raw.Width, raw.Height, token);
        token.ThrowIfCancellationRequested();
        var imagePart = image.IsPng
            ? main.AddImagePart(ImagePartType.Png)
            : main.AddImagePart(ImagePartType.Jpeg);
        imagePart.FeedData(image.Stream);
        token.ThrowIfCancellationRequested();
        var relationshipId = main.GetIdOfPart(imagePart);

        var availableWidth = pageWidth - PageMarginTwips * 2;
        var availableHeight = pageHeight - PageMarginTwips * 2 - 20;
        var (imageWidth, imageHeight) = Fit(raw.Width, raw.Height, availableWidth, availableHeight);

        var paragraph = CreatePageParagraph(
            relationshipId,
            pdfPageIndex + 1,
            imageWidth * EmusPerTwip,
            imageHeight * EmusPerTwip,
            text,
            imageName: $"page-{pdfPageIndex + 1}.{image.Extension}");

        if (exportIndex < exportTotal - 1)
        {
            paragraph.ParagraphProperties!.Append(CreateSection(
                pageWidth, pageHeight, nextPage: true, editable: false));
        }

        body.Append(paragraph);
    }

    private static void AppendEditablePage(
        Body body,
        IReadOnlyList<LayoutLine> lines,
        IReadOnlyList<DetectedTableGrid> grids,
        int exportIndex,
        int exportTotal,
        uint pageWidth,
        uint pageHeight)
    {
        OpenXmlElement? last = null;
        var source = lines.Count == 0
            ? [new LayoutLine("(текст не распознан)", 0, 0, 1, 0.02)]
            : lines;

        var minimumLeft = source.Min(l => l.LeftRatio);
        var medianHeight = source
            .Select(l => l.HeightRatio)
            .Where(h => h > 0)
            .Order()
            .DefaultIfEmpty(0.02)
            .ToArray();
        var typicalHeight = medianHeight[medianHeight.Length / 2];
        var pageFontMedian = source
            .Where(l => l.FontSize > 0)
            .Select(l => l.FontSize)
            .Order()
            .DefaultIfEmpty(0)
            .ToArray();
        var typicalFont = pageFontMedian[pageFontMedian.Length / 2];
        var blocks = EditableLayoutAnalyzer.Analyze(source, grids);

        for (var i = 0; i < blocks.Count; i++)
        {
            switch (blocks[i])
            {
                case TableBlock table:
                    last = CreateEditableTable(table, pageWidth);
                    body.Append(last);
                    break;

                case ParagraphBlock paragraph:
                {
                    LayoutLine? nextLine = i + 1 < blocks.Count && blocks[i + 1] is ParagraphBlock nextParagraph
                        ? nextParagraph.Line
                        : null;

                    last = CreateEditableParagraph(
                        paragraph.Line,
                        nextLine,
                        minimumLeft,
                        typicalHeight,
                        typicalFont,
                        pageWidth,
                        isMissingText: lines.Count == 0);
                    body.Append(last);
                    break;
                }
            }
        }

        if (exportIndex < exportTotal - 1 && last is not null)
        {
            var section = CreateSection(pageWidth, pageHeight, nextPage: true, editable: true);
            if (last is Paragraph paragraph)
            {
                paragraph.ParagraphProperties ??= new ParagraphProperties();
                paragraph.ParagraphProperties.Append(section);
            }
            else
            {
                // Section break after a table must live in a trailing paragraph.
                var breaker = new Paragraph(new ParagraphProperties(section));
                body.Append(breaker);
                last = breaker;
            }
        }
    }

    private static Table CreateEditableTable(TableBlock table, uint pageWidth)
    {
        var columnCount = table.Rows.Count == 0
            ? 0
            : table.Rows.Max(r => r.Count);
        columnCount = Math.Max(columnCount, 1);

        var contentWidth = Math.Max(1, (int)pageWidth - (int)PageMarginTwips * 2);
        var colWidths = new int[columnCount];
        if (table.ColumnWidthRatios is { Count: > 0 } ratios && ratios.Count == columnCount)
        {
            var allocated = 0;
            for (var c = 0; c < columnCount; c++)
            {
                colWidths[c] = Math.Max(120, (int)Math.Round(ratios[c] * contentWidth));
                allocated += colWidths[c];
            }

            if (allocated != contentWidth && columnCount > 0)
                colWidths[^1] = Math.Max(120, colWidths[^1] + (contentWidth - allocated));
        }
        else
        {
            var even = Math.Max(200, contentWidth / columnCount);
            Array.Fill(colWidths, even);
        }

        var grid = new TableGrid();
        for (var c = 0; c < columnCount; c++)
            grid.Append(new GridColumn { Width = colWidths[c].ToString() });

        var wordTable = new Table(
            new TableProperties(
                new TableWidth
                {
                    Width = contentWidth.ToString(),
                    Type = TableWidthUnitValues.Dxa
                },
                new TableLayout { Type = TableLayoutValues.Fixed },
                new TableBorders(
                    new TopBorder { Val = BorderValues.Single, Size = 4U, Color = "666666", Space = 0U },
                    new LeftBorder { Val = BorderValues.Single, Size = 4U, Color = "666666", Space = 0U },
                    new BottomBorder { Val = BorderValues.Single, Size = 4U, Color = "666666", Space = 0U },
                    new RightBorder { Val = BorderValues.Single, Size = 4U, Color = "666666", Space = 0U },
                    new InsideHorizontalBorder { Val = BorderValues.Single, Size = 4U, Color = "AAAAAA", Space = 0U },
                    new InsideVerticalBorder { Val = BorderValues.Single, Size = 4U, Color = "AAAAAA", Space = 0U }),
                new TableLook
                {
                    Val = "04A0",
                    FirstRow = true,
                    LastRow = false,
                    FirstColumn = false,
                    LastColumn = false,
                    NoHorizontalBand = false,
                    NoVerticalBand = true
                }),
            grid);

        for (var r = 0; r < table.Rows.Count; r++)
        {
            var row = table.Rows[r];
            var tableRow = new TableRow();
            for (var c = 0; c < columnCount; c++)
            {
                var cell = c < row.Count
                    ? row[c]
                    : new TableCellContent(string.Empty, 0, false);
                var isHeader = r == 0;
                tableRow.Append(CreateTableCell(cell, colWidths[c], isHeader));
            }

            wordTable.Append(tableRow);
        }

        return wordTable;
    }

    private static TableCell CreateTableCell(TableCellContent cell, int widthDxa, bool header)
    {
        var halfPoints = ToHalfPoints(cell.FontSize, fallback: header ? 20 : 18);
        var cellProperties = new TableCellProperties(
            new TableCellWidth { Width = widthDxa.ToString(), Type = TableWidthUnitValues.Dxa },
            new TableCellVerticalAlignment { Val = TableVerticalAlignmentValues.Center });
        if (header)
        {
            cellProperties.Append(new Shading
            {
                Val = ShadingPatternValues.Clear,
                Color = "auto",
                Fill = "F0F0F0"
            });
        }

        var parts = (cell.Text ?? string.Empty)
            .Replace("\r\n", "\n")
            .Split('\n');
        if (parts.Length == 0)
            parts = [""];

        var openXmlCell = new TableCell(cellProperties);
        foreach (var part in parts)
        {
            var runProperties = new RunProperties(
                new RunFonts { Ascii = "Calibri", HighAnsi = "Calibri", ComplexScript = "Calibri" },
                new FontSize { Val = halfPoints.ToString() });
            if (header || cell.Bold)
                runProperties.Append(new Bold());

            openXmlCell.Append(new Paragraph(
                new ParagraphProperties(
                    new SpacingBetweenLines
                    {
                        Before = "20",
                        After = "20",
                        Line = "240",
                        LineRule = LineSpacingRuleValues.Auto
                    }),
                new Run(runProperties, new Text(part)
                {
                    Space = SpaceProcessingModeValues.Preserve
                })));
        }

        return openXmlCell;
    }

    private static Paragraph CreateEditableParagraph(
        LayoutLine line,
        LayoutLine? next,
        double minimumLeft,
        double typicalHeight,
        double typicalFont,
        uint pageWidth,
        bool isMissingText)
    {
        var trimmed = line.Text.Trim();
        var looksLikeHeading = line.Bold
                               || (trimmed.Length is > 0 and <= 80
                                   && trimmed.Any(char.IsLetter)
                                   && (line.HeightRatio > typicalHeight * 1.2
                                       || (typicalFont > 0 && line.FontSize >= typicalFont * 1.2)
                                       || trimmed.Count(char.IsLower)
                                       <= Math.Max(1, trimmed.Count(char.IsLetter) / 5)));

        var contentWidth = Math.Max(1, pageWidth - 1440);
        var indentRatio = Math.Max(0, line.LeftRatio - minimumLeft);
        var leftIndent = (int)Math.Round(Math.Min(0.72, indentRatio) * contentWidth);
        var verticalGap = next is null
            ? 0
            : next.TopRatio - (line.TopRatio + line.HeightRatio);
        var spacingAfter = verticalGap > Math.Max(typicalHeight * 0.7, 0.008)
            ? 100
            : 12;

        var relativeHeight = typicalHeight <= 0 ? 1 : line.HeightRatio / typicalHeight;
        var fontHalfPoints = line.FontSize > 0
            ? ToHalfPoints(line.FontSize, fallback: 21)
            : looksLikeHeading
                ? Math.Clamp((int)Math.Round(22 * Math.Max(1.05, relativeHeight)), 23, 34)
                : Math.Clamp((int)Math.Round(21 * relativeHeight), 18, 25);

        var runProperties = new RunProperties(
            new RunFonts { Ascii = "Calibri", HighAnsi = "Calibri", ComplexScript = "Calibri" },
            new FontSize { Val = fontHalfPoints.ToString() });

        if (looksLikeHeading || line.Bold)
            runProperties.Append(new Bold());
        if (isMissingText)
            runProperties.Append(new Italic(), new DocumentFormat.OpenXml.Wordprocessing.Color { Val = "777777" });

        return new Paragraph(
            new ParagraphProperties(
                new Indentation { Left = leftIndent.ToString() },
                new SpacingBetweenLines
                {
                    Before = "0",
                    After = spacingAfter.ToString(),
                    Line = "240",
                    LineRule = LineSpacingRuleValues.Auto
                }),
            new Run(runProperties, new Text(line.Text) { Space = SpaceProcessingModeValues.Preserve }));
    }

    private static int ToHalfPoints(double fontSize, int fallback)
    {
        if (fontSize <= 0)
            return fallback;

        // Docnet FontSize is typically in PDF points.
        var half = (int)Math.Round(fontSize * 2.0);
        return Math.Clamp(half, 14, 72);
    }

    private static string NormalizeText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        var builder = new System.Text.StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c is '\r')
            {
                if (i + 1 < text.Length && text[i + 1] == '\n')
                    i++;
                builder.Append('\n');
                continue;
            }

            if (char.IsHighSurrogate(c))
            {
                if (i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                {
                    builder.Append(c);
                    builder.Append(text[++i]);
                }
                continue;
            }

            if (char.IsLowSurrogate(c))
                continue;

            if (c is '\t' or '\n'
                || (c >= 0x20 && c <= 0xD7FF)
                || (c >= 0xE000 && c <= 0xFFFD))
            {
                builder.Append(c);
            }
        }

        var normalized = builder.ToString();
        normalized = Regex.Replace(normalized, "[ \t]+\n", "\n");
        normalized = Regex.Replace(normalized, "\n{3,}", "\n\n");
        return normalized.Trim();
    }

    private static EncodedPageImage EncodePageImage(
        byte[] bgra,
        int width,
        int height,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();

        // PDFium leaves transparent page areas as BGRA(0,0,0,0). The viewer
        // shows them as white because of the page frame, but JPEG/PNG export
        // otherwise keeps a solid black background.
        var flattened = FlattenOntoWhite(bgra);
        var bitmap = BitmapSource.Create(
            width, height, 96, 96, PixelFormats.Bgra32, null, flattened, width * 4);
        bitmap.Freeze();

        var usePng = ShouldUsePng(flattened, width, height);
        var stream = new MemoryStream();
        try
        {
            if (usePng)
            {
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                encoder.Save(stream);
            }
            else
            {
                var encoder = new JpegBitmapEncoder { QualityLevel = 92 };
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                encoder.Save(stream);
            }

            token.ThrowIfCancellationRequested();
            stream.Position = 0;
            var selected = new EncodedPageImage(
                stream,
                isPng: usePng,
                usePng ? "png" : "jpg");
            stream = null!;
            return selected;
        }
        finally
        {
            stream?.Dispose();
        }
    }

    private static byte[] FlattenOntoWhite(byte[] bgra)
    {
        var result = new byte[bgra.Length];
        for (var i = 0; i + 3 < bgra.Length; i += 4)
        {
            var blue = bgra[i];
            var green = bgra[i + 1];
            var red = bgra[i + 2];
            var alpha = bgra[i + 3];

            if (alpha == 255)
            {
                result[i] = blue;
                result[i + 1] = green;
                result[i + 2] = red;
                result[i + 3] = 255;
                continue;
            }

            if (alpha == 0)
            {
                result[i] = 255;
                result[i + 1] = 255;
                result[i + 2] = 255;
                result[i + 3] = 255;
                continue;
            }

            // Blend against white: out = src * a + 255 * (1 - a)
            var inv = 255 - alpha;
            result[i] = (byte)((blue * alpha + 255 * inv) / 255);
            result[i + 1] = (byte)((green * alpha + 255 * inv) / 255);
            result[i + 2] = (byte)((red * alpha + 255 * inv) / 255);
            result[i + 3] = 255;
        }

        return result;
    }

    private static bool ShouldUsePng(byte[] bgra, int width, int height)
    {
        var pixelCount = Math.Max(1, width * height);
        var step = Math.Max(1, pixelCount / 20_000);
        var sampled = 0;
        var white = 0;
        var colors = new HashSet<int>();

        for (var pixel = 0; pixel < pixelCount; pixel += step)
        {
            var offset = pixel * 4;
            if (offset + 2 >= bgra.Length)
                break;

            sampled++;
            var blue = bgra[offset];
            var green = bgra[offset + 1];
            var red = bgra[offset + 2];
            if (blue >= 235 && green >= 235 && red >= 235)
            {
                white++;
            }

            // Quantized palette is sufficient to distinguish line-art pages
            // from scans/photos without encoding the page twice.
            colors.Add(((red >> 4) << 8) | ((green >> 4) << 4) | (blue >> 4));
        }

        var whiteRatio = sampled == 0 ? 0 : white / (double)sampled;
        return whiteRatio >= 0.82 && colors.Count <= 64;
    }

    private static Paragraph CreatePageParagraph(
        string relationshipId,
        int pageNumber,
        long widthEmus,
        long heightEmus,
        string recognizedText,
        string imageName)
    {
        var drawing = new DocumentFormat.OpenXml.Wordprocessing.Drawing(
            new DW.Inline(
                new DW.Extent { Cx = widthEmus, Cy = heightEmus },
                new DW.EffectExtent { LeftEdge = 0, TopEdge = 0, RightEdge = 0, BottomEdge = 0 },
                new DW.DocProperties
                {
                    Id = (uint)pageNumber,
                    Name = $"Страница {pageNumber}",
                    Description = $"Страница PDF {pageNumber}"
                },
                new DW.NonVisualGraphicFrameDrawingProperties(new A.GraphicFrameLocks { NoChangeAspect = true }),
                new A.Graphic(
                    new A.GraphicData(
                        new PIC.Picture(
                            new PIC.NonVisualPictureProperties(
                                new PIC.NonVisualDrawingProperties { Id = 0, Name = imageName },
                                new PIC.NonVisualPictureDrawingProperties()),
                            new PIC.BlipFill(
                                new A.Blip
                                {
                                    Embed = relationshipId,
                                    CompressionState = A.BlipCompressionValues.Print
                                },
                                new A.Stretch(new A.FillRectangle())),
                            new PIC.ShapeProperties(
                                new A.Transform2D(
                                    new A.Offset { X = 0, Y = 0 },
                                    new A.Extents { Cx = widthEmus, Cy = heightEmus }),
                                new A.PresetGeometry(new A.AdjustValueList())
                                {
                                    Preset = A.ShapeTypeValues.Rectangle
                                })))
                    {
                        Uri = "http://schemas.openxmlformats.org/drawingml/2006/picture"
                    }))
            {
                DistanceFromTop = 0,
                DistanceFromBottom = 0,
                DistanceFromLeft = 0,
                DistanceFromRight = 0
            });

        var paragraph = new Paragraph(
            new ParagraphProperties(
                new SpacingBetweenLines { Before = "0", After = "0", Line = "1" },
                new Justification { Val = JustificationValues.Center }),
            new Run(drawing));

        if (!string.IsNullOrWhiteSpace(recognizedText))
        {
            paragraph.Append(new Run(
                new RunProperties(new Vanish(), new NoProof(), new FontSize { Val = "2" }),
                new Text("\n" + recognizedText) { Space = SpaceProcessingModeValues.Preserve }));
        }

        return paragraph;
    }

    private static SectionProperties CreateSection(uint width, uint height, bool nextPage, bool editable)
    {
        var margin = editable ? 720U : PageMarginTwips;
        var section = new SectionProperties(
            new PageSize
            {
                Width = width,
                Height = height,
                Orient = width > height ? PageOrientationValues.Landscape : PageOrientationValues.Portrait
            },
            new PageMargin
            {
                Top = (int)margin,
                Right = margin,
                Bottom = (int)margin,
                Left = margin,
                Header = 0,
                Footer = 0,
                Gutter = 0
            });

        if (nextPage)
            section.PrependChild(new SectionType { Val = SectionMarkValues.NextPage });

        return section;
    }

    private static (uint Width, uint Height) ToPageSize(Size nativeSize)
    {
        const double twipsPerPdfPoint = 20.0;
        const double maxTwips = 31680.0;
        const double minTwips = 1440.0;

        var width = Math.Max(1, nativeSize.Width * twipsPerPdfPoint);
        var height = Math.Max(1, nativeSize.Height * twipsPerPdfPoint);
        var downScale = Math.Min(1.0, maxTwips / Math.Max(width, height));
        width *= downScale;
        height *= downScale;
        // Extreme receipts/banners cannot preserve their physical aspect ratio
        // within Word's page limits. Clamp the paper and fit the image/text inside.
        width = Math.Clamp(width, minTwips, maxTwips);
        height = Math.Clamp(height, minTwips, maxTwips);
        return ((uint)Math.Round(width), (uint)Math.Round(height));
    }

    private static double GetRenderScale(Size nativeSize)
    {
        var longestSide = Math.Max(nativeSize.Width, nativeSize.Height);
        if (longestSide <= 0)
            return 1.0;
        return Math.Clamp(MaximumRenderedDimension / longestSide, 0.1, PreferredRenderScale);
    }

    private static (long Width, long Height) Fit(int sourceWidth, int sourceHeight, uint availableWidth, uint availableHeight)
    {
        var scale = Math.Min(availableWidth / (double)sourceWidth, availableHeight / (double)sourceHeight);
        return (Math.Max(1, (long)Math.Round(sourceWidth * scale)), Math.Max(1, (long)Math.Round(sourceHeight * scale)));
    }

    private static async Task MoveWithRetryAsync(string source, string destination, CancellationToken token)
    {
        const int attempts = 8;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(source, destination, overwrite: true);
                return;
            }
            catch (IOException) when (attempt < attempts)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(150 * attempt), token);
            }
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private sealed class EncodedPageImage(
        MemoryStream stream,
        bool isPng,
        string extension) : IDisposable
    {
        public MemoryStream Stream { get; } = stream;
        public bool IsPng { get; } = isPng;
        public string Extension { get; } = extension;

        public void Dispose() => Stream.Dispose();
    }
}
