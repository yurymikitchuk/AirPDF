using System.Text;
using Docnet.Core.Models;

namespace AirPDF.Services;

public sealed record LayoutLine(
    string Text,
    double LeftRatio,
    double TopRatio,
    double WidthRatio,
    double HeightRatio,
    double FontSize = 0,
    bool Bold = false);

public static class TextLayoutBuilder
{
    private readonly record struct Glyph(char Char, int Left, int Top, int Right, int Bottom, double FontSize)
    {
        public int Height => Math.Max(1, Bottom - Top);
    }

    public static IReadOnlyList<LayoutLine> BuildLinesFromCharacters(
        IReadOnlyList<Character> characters,
        double pageWidth,
        double pageHeight)
    {
        if (characters.Count == 0 || pageWidth <= 0 || pageHeight <= 0)
            return [];

        var items = new List<Glyph>(characters.Count);
        foreach (var ch in characters)
        {
            if (ch.Char == '\0')
                continue;
            items.Add(new Glyph(
                ch.Char,
                ch.Box.Left,
                ch.Box.Top,
                ch.Box.Right,
                ch.Box.Bottom,
                ch.FontSize > 0 ? ch.FontSize : 0));
        }

        if (items.Count == 0)
            return [];

        items.Sort(static (a, b) =>
        {
            var byTop = a.Top.CompareTo(b.Top);
            return byTop != 0 ? byTop : a.Left.CompareTo(b.Left);
        });

        var avgHeight = items.Average(c => c.Height);
        var pageFontMedian = Median(items
            .Where(g => g.FontSize > 0)
            .Select(g => g.FontSize)
            .DefaultIfEmpty(0)
            .ToArray());
        var lineThreshold = Math.Max(3, avgHeight * 0.55);
        var lines = new List<List<Glyph>>();

        foreach (var item in items)
        {
            if (lines.Count == 0)
            {
                lines.Add([item]);
                continue;
            }

            var current = lines[^1];
            var lineTop = current.Average(c => c.Top);
            if (Math.Abs(item.Top - lineTop) <= lineThreshold)
                current.Add(item);
            else
                lines.Add([item]);
        }

        var result = new List<LayoutLine>(lines.Count);
        foreach (var line in lines)
        {
            line.Sort(static (a, b) => a.Left.CompareTo(b.Left));
            foreach (var segment in SplitIntoSegments(line, avgHeight))
                AddLayoutLine(result, segment, pageWidth, pageHeight, avgHeight, pageFontMedian);
        }

        return OrderForReading(result);
    }

    public static IReadOnlyList<LayoutLine> BuildLinesFromOcr(
        IReadOnlyList<OcrLineResult> lines,
        double pageWidth,
        double pageHeight)
    {
        var layout = lines
            .Where(l => !string.IsNullOrWhiteSpace(l.Text))
            .OrderBy(l => l.Top)
            .ThenBy(l => l.Left)
            .Select(l => new LayoutLine(
                l.Text.Trim(),
                ClampRatio(l.Left / Math.Max(1, pageWidth)),
                ClampRatio(l.Top / Math.Max(1, pageHeight)),
                ClampRatio(l.Width / Math.Max(1, pageWidth)),
                ClampRatio(l.Height / Math.Max(1, pageHeight)),
                FontSize: EstimateOcrFontSize(l.Height, pageHeight)))
            .ToList();
        return OrderForReading(layout);
    }

    private static double EstimateOcrFontSize(double lineHeightPx, double pageHeightPx)
    {
        if (lineHeightPx <= 0 || pageHeightPx <= 0)
            return 0;
        // Map image line height to approximate PDF points (letter page ~792pt).
        return Math.Clamp(lineHeightPx / pageHeightPx * 720.0, 8, 36);
    }

    private static IEnumerable<List<Glyph>> SplitIntoSegments(List<Glyph> line, double averageHeight)
    {
        if (line.Count <= 1)
        {
            yield return line;
            yield break;
        }

        var widths = line
            .Where(g => !char.IsWhiteSpace(g.Char))
            .Select(g => Math.Max(1, g.Right - g.Left))
            .Order()
            .ToArray();
        var medianWidth = widths.Length == 0 ? averageHeight * 0.5 : widths[widths.Length / 2];
        // Tighter gap so dense table columns stay split into cells.
        var columnGap = Math.Max(averageHeight * 1.6, medianWidth * 3.2);

        var segment = new List<Glyph>();
        int? previousRight = null;
        foreach (var glyph in line)
        {
            if (previousRight is not null
                && glyph.Left - previousRight > columnGap
                && segment.Any(g => !char.IsWhiteSpace(g.Char)))
            {
                yield return segment;
                segment = [];
            }

            segment.Add(glyph);
            previousRight = Math.Max(previousRight ?? glyph.Right, glyph.Right);
        }

        if (segment.Count > 0)
            yield return segment;
    }

    private static void AddLayoutLine(
        ICollection<LayoutLine> result,
        List<Glyph> glyphs,
        double pageWidth,
        double pageHeight,
        double averageHeight,
        double pageFontMedian)
    {
        var builder = new StringBuilder(glyphs.Count + 8);
        var visibleWidths = glyphs
            .Where(g => !char.IsWhiteSpace(g.Char))
            .Select(g => Math.Max(1, g.Right - g.Left))
            .ToArray();
        var averageWidth = visibleWidths.Length == 0 ? averageHeight * 0.5 : visibleWidths.Average();
        var wordGap = Math.Max(2, averageWidth * 0.75);

        int? previousRight = null;
        foreach (var glyph in glyphs)
        {
            if (previousRight is not null
                && glyph.Left - previousRight > wordGap
                && builder.Length > 0
                && !char.IsWhiteSpace(builder[^1]))
            {
                builder.Append(' ');
            }

            if (!(char.IsWhiteSpace(glyph.Char)
                  && (builder.Length == 0 || char.IsWhiteSpace(builder[^1]))))
            {
                builder.Append(glyph.Char);
            }

            previousRight = Math.Max(previousRight ?? glyph.Right, glyph.Right);
        }

        var text = builder.ToString().Trim();
        if (text.Length == 0)
            return;

        var left = glyphs.Min(g => g.Left);
        var top = glyphs.Min(g => g.Top);
        var right = glyphs.Max(g => g.Right);
        var bottom = glyphs.Max(g => g.Bottom);
        var fontSizes = glyphs.Where(g => g.FontSize > 0).Select(g => g.FontSize).ToArray();
        var fontSize = fontSizes.Length == 0 ? 0 : Median(fontSizes);
        var bold = fontSize > 0
                   && pageFontMedian > 0
                   && fontSize >= pageFontMedian * 1.16
                   && text.Length <= 64;

        result.Add(new LayoutLine(
            text,
            ClampRatio(left / pageWidth),
            ClampRatio(top / pageHeight),
            ClampRatio((right - left) / pageWidth),
            ClampRatio((bottom - top) / pageHeight),
            fontSize,
            bold));
    }

    private static IReadOnlyList<LayoutLine> OrderForReading(List<LayoutLine> lines)
    {
        if (lines.Count < 4)
            return lines.OrderBy(l => l.TopRatio).ThenBy(l => l.LeftRatio).ToList();

        var ordered = lines.OrderBy(l => l.TopRatio).ThenBy(l => l.LeftRatio).ToList();
        var separators = ordered
            .Where(IsWideSeparator)
            .OrderBy(l => l.TopRatio)
            .ToList();

        if (separators.Count == 0)
            return OrderBand(ordered);

        var result = new List<LayoutLine>(lines.Count);
        var bandStart = 0.0;
        foreach (var separator in separators)
        {
            var band = ordered
                .Where(l => !separators.Contains(l)
                            && l.TopRatio >= bandStart
                            && l.TopRatio < separator.TopRatio)
                .ToList();
            result.AddRange(OrderBand(band));
            result.Add(separator);
            bandStart = separator.TopRatio + Math.Max(separator.HeightRatio, 0.001);
        }

        result.AddRange(OrderBand(ordered
            .Where(l => !separators.Contains(l) && l.TopRatio >= bandStart)
            .ToList()));
        return result;
    }

    private static IReadOnlyList<LayoutLine> OrderBand(List<LayoutLine> lines)
    {
        if (lines.Count < 4 || LooksTabular(lines))
            return lines.OrderBy(l => l.TopRatio).ThenBy(l => l.LeftRatio).ToList();

        var left = lines
            .Where(l => l.LeftRatio + l.WidthRatio <= 0.62)
            .OrderBy(l => l.TopRatio)
            .ToList();
        var right = lines
            .Except(left)
            .Where(l => l.LeftRatio >= 0.38)
            .OrderBy(l => l.TopRatio)
            .ToList();

        if (left.Count < 2 || right.Count < 2)
            return lines.OrderBy(l => l.TopRatio).ThenBy(l => l.LeftRatio).ToList();

        var remainder = lines.Except(left).Except(right)
            .OrderBy(l => l.TopRatio)
            .ThenBy(l => l.LeftRatio);
        return left.Concat(right).Concat(remainder).ToList();
    }

    private static bool LooksTabular(IReadOnlyList<LayoutLine> lines)
    {
        var heights = lines
            .Select(l => l.HeightRatio)
            .Where(h => h > 0)
            .Order()
            .DefaultIfEmpty(0.02)
            .ToArray();
        var typicalHeight = heights[heights.Length / 2];
        var rowThreshold = Math.Max(typicalHeight * 0.65, 0.008);

        var ordered = lines.OrderBy(l => l.TopRatio).ThenBy(l => l.LeftRatio).ToList();
        var rows = new List<List<LayoutLine>>();
        foreach (var line in ordered)
        {
            if (rows.Count == 0)
            {
                rows.Add([line]);
                continue;
            }

            var rowTop = rows[^1].Average(c => c.TopRatio);
            if (Math.Abs(line.TopRatio - rowTop) <= rowThreshold)
                rows[^1].Add(line);
            else
                rows.Add([line]);
        }

        return rows.Count(r => r.Count >= 2) >= 2;
    }

    private static bool IsWideSeparator(LayoutLine line) =>
        line.WidthRatio >= 0.62
        || (line.LeftRatio < 0.38 && line.LeftRatio + line.WidthRatio > 0.62);

    private static double Median(IReadOnlyList<double> values)
    {
        if (values.Count == 0)
            return 0;
        var ordered = values.Order().ToArray();
        return ordered[ordered.Length / 2];
    }

    private static double ClampRatio(double value) => Math.Clamp(value, 0, 1);
}
