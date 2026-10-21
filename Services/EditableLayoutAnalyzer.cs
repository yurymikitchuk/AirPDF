using System.Text.RegularExpressions;

namespace AirPDF.Services;

public abstract record ExportBlock;

public sealed record ParagraphBlock(LayoutLine Line) : ExportBlock;

public sealed record TableCellContent(string Text, double FontSize, bool Bold);

public sealed record TableBlock(
    IReadOnlyList<IReadOnlyList<TableCellContent>> Rows,
    IReadOnlyList<double>? ColumnWidthRatios = null) : ExportBlock;

/// <summary>
/// Groups layout lines into paragraphs and tables (grid-first, heuristic fallback).
/// </summary>
public static class EditableLayoutAnalyzer
{
    public static IReadOnlyList<ExportBlock> Analyze(
        IReadOnlyList<LayoutLine> lines,
        IReadOnlyList<DetectedTableGrid>? grids = null)
    {
        if (lines.Count == 0)
            return [];

        if (grids is { Count: > 0 })
        {
            var fromGrid = AnalyzeWithGrids(lines, grids);
            if (fromGrid.Any(b => b is TableBlock))
                return fromGrid;
        }

        return AnalyzeHeuristic(lines);
    }

    private static IReadOnlyList<ExportBlock> AnalyzeWithGrids(
        IReadOnlyList<LayoutLine> lines,
        IReadOnlyList<DetectedTableGrid> grids)
    {
        var remaining = lines.ToList();
        var tableBlocks = new List<(double Top, TableBlock Table)>();

        foreach (var grid in grids)
        {
            if (grid.RowCount < 2 || grid.ColumnCount < 2)
                continue;

            var matrix = new TableCellContent[grid.RowCount, grid.ColumnCount];
            var lastTop = new double[grid.RowCount, grid.ColumnCount];
            for (var r = 0; r < grid.RowCount; r++)
            for (var c = 0; c < grid.ColumnCount; c++)
            {
                matrix[r, c] = new TableCellContent(string.Empty, 0, false);
                lastTop[r, c] = -1;
            }

            var used = new List<LayoutLine>();
            foreach (var line in remaining.OrderBy(l => l.TopRatio).ThenBy(l => l.LeftRatio))
            {
                var cx = line.LeftRatio + line.WidthRatio * 0.5;
                var cy = line.TopRatio + line.HeightRatio * 0.5;
                if (!grid.TryGetCell(cx, cy, out var row, out var col))
                    continue;

                var prev = matrix[row, col];
                var stacked = lastTop[row, col] >= 0
                              && line.TopRatio - lastTop[row, col] > Math.Max(line.HeightRatio, 0.008) * 0.55;
                var joined = JoinCellText(prev.Text, line.Text.Trim(), stacked);
                var font = prev.FontSize > 0 && line.FontSize > 0
                    ? (prev.FontSize + line.FontSize) * 0.5
                    : Math.Max(prev.FontSize, line.FontSize);
                matrix[row, col] = new TableCellContent(joined, font, prev.Bold || line.Bold);
                lastTop[row, col] = line.TopRatio;
                used.Add(line);
            }

            if (used.Count < 3)
                continue;

            var filled = 0;
            var rows = new List<IReadOnlyList<TableCellContent>>(grid.RowCount);
            for (var r = 0; r < grid.RowCount; r++)
            {
                var row = new TableCellContent[grid.ColumnCount];
                for (var c = 0; c < grid.ColumnCount; c++)
                {
                    row[c] = matrix[r, c];
                    if (!string.IsNullOrWhiteSpace(row[c].Text))
                        filled++;
                }

                rows.Add(row);
            }

            var capacity = grid.RowCount * grid.ColumnCount;
            if (filled < Math.Max(3, (int)Math.Ceiling(capacity * 0.3)))
                continue;

            var widths = new double[grid.ColumnCount];
            for (var c = 0; c < grid.ColumnCount; c++)
                widths[c] = Math.Max(0.02, grid.VerticalEdges[c + 1] - grid.VerticalEdges[c]);

            tableBlocks.Add((grid.HorizontalEdges[0], new TableBlock(rows, widths)));
            foreach (var line in used)
                remaining.Remove(line);
        }

        if (tableBlocks.Count == 0)
            return AnalyzeHeuristic(lines);

        var blocks = new List<ExportBlock>();
        var paragraphLines = remaining
            .OrderBy(l => l.TopRatio)
            .ThenBy(l => l.LeftRatio)
            .ToList();
        var tables = tableBlocks.OrderBy(t => t.Top).ToList();
        var pi = 0;
        var ti = 0;

        while (pi < paragraphLines.Count || ti < tables.Count)
        {
            var nextParaTop = pi < paragraphLines.Count ? paragraphLines[pi].TopRatio : double.MaxValue;
            var nextTableTop = ti < tables.Count ? tables[ti].Top : double.MaxValue;

            if (nextTableTop <= nextParaTop)
            {
                blocks.Add(tables[ti].Table);
                ti++;
            }
            else
            {
                blocks.Add(new ParagraphBlock(paragraphLines[pi]));
                pi++;
            }
        }

        return blocks;
    }

    private static IReadOnlyList<ExportBlock> AnalyzeHeuristic(IReadOnlyList<LayoutLine> lines)
    {
        var rows = BuildVisualRows(lines);
        if (rows.Count == 0)
            return lines.Select(l => (ExportBlock)new ParagraphBlock(l)).ToList();

        var blocks = new List<ExportBlock>();
        var index = 0;
        while (index < rows.Count)
        {
            if (TryTakeTable(rows, index, out var table, out var consumed) && table is not null)
            {
                blocks.Add(table);
                index += consumed;
                continue;
            }

            foreach (var cell in rows[index])
                blocks.Add(new ParagraphBlock(cell));
            index++;
        }

        return blocks;
    }

    private static List<List<LayoutLine>> BuildVisualRows(IReadOnlyList<LayoutLine> lines)
    {
        var ordered = lines
            .OrderBy(l => l.TopRatio)
            .ThenBy(l => l.LeftRatio)
            .ToList();

        var heights = ordered
            .Select(l => l.HeightRatio)
            .Where(h => h > 0)
            .Order()
            .DefaultIfEmpty(0.02)
            .ToArray();
        var typicalHeight = heights[heights.Length / 2];
        var rowThreshold = Math.Max(typicalHeight * 0.6, 0.007);

        var rows = new List<List<LayoutLine>>();
        foreach (var line in ordered)
        {
            var expanded = ExpandRowCells(line);
            if (rows.Count == 0)
            {
                rows.Add(expanded);
                continue;
            }

            var current = rows[^1];
            var rowTop = current.Average(c => c.TopRatio);
            if (Math.Abs(line.TopRatio - rowTop) <= rowThreshold)
                current.AddRange(expanded);
            else
                rows.Add(expanded);
        }

        foreach (var row in rows)
            row.Sort(static (a, b) => a.LeftRatio.CompareTo(b.LeftRatio));

        return rows;
    }

    private static List<LayoutLine> ExpandRowCells(LayoutLine line)
    {
        var parts = Regex.Split(line.Text.Trim(), @"\s{2,}")
            .Select(p => p.Trim())
            .Where(p => p.Length > 0)
            .ToArray();

        if (parts.Length <= 1)
            return [line];

        var width = Math.Max(0.01, line.WidthRatio);
        var cellWidth = width / parts.Length;
        var result = new List<LayoutLine>(parts.Length);
        for (var i = 0; i < parts.Length; i++)
        {
            result.Add(line with
            {
                Text = parts[i],
                LeftRatio = Math.Clamp(line.LeftRatio + i * cellWidth, 0, 1),
                WidthRatio = Math.Clamp(cellWidth * 0.95, 0.01, 1)
            });
        }

        return result;
    }

    private static bool TryTakeTable(
        IReadOnlyList<List<LayoutLine>> rows,
        int start,
        out TableBlock? table,
        out int consumed)
    {
        table = null;
        consumed = 0;

        if (start >= rows.Count || CountCells(rows[start]) < 2)
            return false;

        var end = start;
        while (end + 1 < rows.Count && CanBelongToTable(rows, start, end + 1))
            end++;

        var rowCount = end - start + 1;
        if (rowCount < 2)
            return false;

        var slice = rows.Skip(start).Take(rowCount).ToList();
        var columns = InferColumns(slice);
        if (columns.Count < 2)
            return false;

        var multiCellRows = slice.Count(r => CountCells(r) >= 2);
        if (multiCellRows < 2)
            return false;

        var matrix = new List<IReadOnlyList<TableCellContent>>(rowCount);
        foreach (var row in slice)
        {
            var cells = new TableCellContent[columns.Count];
            for (var i = 0; i < cells.Length; i++)
                cells[i] = new TableCellContent(string.Empty, 0, false);

            foreach (var cell in row)
            {
                var col = NearestColumn(columns, cell.LeftRatio);
                if (col < 0 || col >= cells.Length)
                    continue;

                var prev = cells[col];
                cells[col] = new TableCellContent(
                    JoinCellText(prev.Text, cell.Text.Trim(), stacked: false),
                    prev.FontSize > 0 && cell.FontSize > 0
                        ? (prev.FontSize + cell.FontSize) * 0.5
                        : Math.Max(prev.FontSize, cell.FontSize),
                    prev.Bold || cell.Bold);
            }

            matrix.Add(cells);
        }

        var filled = matrix.Sum(r => r.Count(c => !string.IsNullOrWhiteSpace(c.Text)));
        var capacity = matrix.Count * columns.Count;
        if (filled < Math.Max(4, (int)Math.Ceiling(capacity * 0.4)))
            return false;

        var widths = EstimateColumnWidths(columns, slice);
        table = new TableBlock(matrix, widths);
        consumed = rowCount;
        return true;
    }

    private static double[] EstimateColumnWidths(IReadOnlyList<double> columns, List<List<LayoutLine>> rows)
    {
        var widths = new double[columns.Count];
        for (var c = 0; c < columns.Count; c++)
        {
            var right = c + 1 < columns.Count ? columns[c + 1] : 1.0;
            widths[c] = Math.Max(0.04, right - columns[c]);
        }

        var maxRight = rows.SelectMany(r => r).Select(l => l.LeftRatio + l.WidthRatio).DefaultIfEmpty(1).Max();
        if (columns.Count > 0)
            widths[^1] = Math.Max(widths[^1], Math.Max(0.04, maxRight - columns[^1]));

        var sum = widths.Sum();
        if (sum > 0)
        {
            for (var i = 0; i < widths.Length; i++)
                widths[i] /= sum;
        }

        return widths;
    }

    private static bool CanBelongToTable(
        IReadOnlyList<List<LayoutLine>> rows,
        int tableStart,
        int candidate)
    {
        var candidateRow = rows[candidate];
        var cellCount = CountCells(candidateRow);
        if (cellCount >= 2)
            return true;

        if (cellCount == 1)
        {
            var line = candidateRow[0];
            if (line.WidthRatio >= 0.72)
                return false;

            var prefix = rows.Skip(tableStart).Take(candidate - tableStart).ToList();
            if (prefix.Count == 0)
                return false;

            var multi = prefix.Where(r => CountCells(r) >= 2).ToList();
            if (multi.Count == 0)
                multi = [prefix[0]];

            var columns = InferColumns(multi);
            if (columns.Count < 2)
                return false;

            var nearest = columns.MinBy(c => Math.Abs(c - line.LeftRatio));
            return Math.Abs(nearest - line.LeftRatio) <= 0.055;
        }

        return false;
    }

    private static List<double> InferColumns(IReadOnlyList<List<LayoutLine>> rows)
    {
        var lefts = rows
            .SelectMany(r => r)
            .Select(l => l.LeftRatio)
            .Order()
            .ToList();

        if (lefts.Count == 0)
            return [];

        var clusters = new List<List<double>>();
        clusters.Add([lefts[0]]);
        foreach (var left in lefts.Skip(1))
        {
            var current = clusters[^1];
            var center = current.Average();
            if (Math.Abs(left - center) <= 0.04)
                current.Add(left);
            else
                clusters.Add([left]);
        }

        return clusters.Select(c => c.Average()).Order().ToList();
    }

    private static int NearestColumn(IReadOnlyList<double> columns, double leftRatio)
    {
        var best = 0;
        var bestDist = double.MaxValue;
        for (var i = 0; i < columns.Count; i++)
        {
            var dist = Math.Abs(columns[i] - leftRatio);
            if (dist < bestDist)
            {
                bestDist = dist;
                best = i;
            }
        }

        return best;
    }

    private static int CountCells(List<LayoutLine> row) => row.Count;

    private static string JoinCellText(string previous, string next, bool stacked)
    {
        next = next.Trim();
        if (next.Length == 0)
            return previous ?? string.Empty;
        if (string.IsNullOrWhiteSpace(previous))
            return next;

        return previous + (stacked ? "\n" : " ") + next;
    }
}
