namespace AirPDF.Services;

/// <summary>
/// Builds virtual table grids from aligned text columns (no ruling lines required).
/// </summary>
public static class BorderlessTableDetector
{
    public static IReadOnlyList<DetectedTableGrid> Detect(IReadOnlyList<LayoutLine> lines)
    {
        if (lines.Count < 4)
            return [];

        var rows = BuildRows(lines);
        if (rows.Count < 2)
            return [];

        var grids = new List<DetectedTableGrid>();
        var index = 0;
        while (index < rows.Count)
        {
            if (!TryTakeAlignedBlock(rows, index, out var end, out var grid) || grid is null)
            {
                index++;
                continue;
            }

            grids.Add(grid);
            index = end + 1;
        }

        return grids
            .OrderByDescending(g =>
                (g.VerticalEdges[^1] - g.VerticalEdges[0]) *
                (g.HorizontalEdges[^1] - g.HorizontalEdges[0]))
            .ToList();
    }

    private static List<List<LayoutLine>> BuildRows(IReadOnlyList<LayoutLine> lines)
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
        var typical = heights[heights.Length / 2];
        var threshold = Math.Max(typical * 0.65, 0.008);

        var rows = new List<List<LayoutLine>>();
        foreach (var line in ordered)
        {
            if (rows.Count == 0)
            {
                rows.Add([line]);
                continue;
            }

            var rowTop = rows[^1].Average(c => c.TopRatio);
            if (Math.Abs(line.TopRatio - rowTop) <= threshold)
                rows[^1].Add(line);
            else
                rows.Add([line]);
        }

        foreach (var row in rows)
            row.Sort(static (a, b) => a.LeftRatio.CompareTo(b.LeftRatio));

        return rows;
    }

    private static bool TryTakeAlignedBlock(
        IReadOnlyList<List<LayoutLine>> rows,
        int start,
        out int end,
        out DetectedTableGrid? grid)
    {
        end = start;
        grid = null;

        if (CountCells(rows[start]) < 2)
            return false;

        while (end + 1 < rows.Count && LooksLikeTableRow(rows, start, end + 1))
            end++;

        var rowCount = end - start + 1;
        if (rowCount < 2)
            return false;

        var slice = rows.Skip(start).Take(rowCount).ToList();
        var multi = slice.Count(r => CountCells(r) >= 2);
        if (multi < 2)
            return false;

        var columns = InferColumnCenters(slice);
        if (columns.Count < 2)
            return false;

        // Reject if too many cells don't land near a column (noise / prose).
        var hits = 0;
        var total = 0;
        foreach (var row in slice)
        foreach (var cell in row)
        {
            total++;
            if (columns.Min(c => Math.Abs(c - cell.LeftRatio)) <= 0.05)
                hits++;
        }

        if (total == 0 || hits < Math.Max(4, (int)(total * 0.55)))
            return false;

        var top = slice.Min(r => r.Min(l => l.TopRatio));
        var bottom = slice.Max(r => r.Max(l => l.TopRatio + l.HeightRatio));
        var left = Math.Max(0, columns[0] - 0.01);
        var right = Math.Min(1, slice.Max(r => r.Max(l => l.LeftRatio + l.WidthRatio)) + 0.01);

        var vertical = new List<double> { left };
        for (var i = 0; i < columns.Count - 1; i++)
            vertical.Add((columns[i] + columns[i + 1]) * 0.5);
        vertical.Add(Math.Max(vertical[^1] + 0.02, right));

        var horizontal = new List<double> { Math.Max(0, top - 0.004) };
        for (var i = 0; i < slice.Count - 1; i++)
        {
            var a = slice[i].Average(l => l.TopRatio + l.HeightRatio);
            var b = slice[i + 1].Average(l => l.TopRatio);
            horizontal.Add((a + b) * 0.5);
        }

        horizontal.Add(Math.Min(1, bottom + 0.004));

        if (vertical.Count < 3 || horizontal.Count < 3)
            return false;

        grid = new DetectedTableGrid(vertical, horizontal);
        return true;
    }

    private static bool LooksLikeTableRow(
        IReadOnlyList<List<LayoutLine>> rows,
        int tableStart,
        int candidate)
    {
        var row = rows[candidate];
        var cells = CountCells(row);
        if (cells >= 2)
            return true;

        if (cells != 1)
            return false;

        var line = row[0];
        if (line.WidthRatio >= 0.72)
            return false;

        var prefix = rows.Skip(tableStart).Take(candidate - tableStart)
            .Where(r => CountCells(r) >= 2)
            .ToList();
        if (prefix.Count == 0)
            return false;

        var columns = InferColumnCenters(prefix);
        if (columns.Count < 2)
            return false;

        return columns.Min(c => Math.Abs(c - line.LeftRatio)) <= 0.055;
    }

    private static List<double> InferColumnCenters(IReadOnlyList<List<LayoutLine>> rows)
    {
        // Prefer left edges; fall back to centers for short cells.
        var anchors = rows
            .SelectMany(r => r)
            .Select(l => l.WidthRatio < 0.12
                ? l.LeftRatio + l.WidthRatio * 0.5
                : l.LeftRatio)
            .Order()
            .ToList();

        if (anchors.Count == 0)
            return [];

        var clusters = new List<List<double>> { new() { anchors[0] } };
        foreach (var value in anchors.Skip(1))
        {
            var center = clusters[^1].Average();
            if (Math.Abs(value - center) <= 0.035)
                clusters[^1].Add(value);
            else
                clusters.Add(new List<double> { value });
        }

        return clusters
            .Where(c => c.Count >= 2 || clusters.Count <= 3)
            .Select(c => c.Average())
            .Order()
            .ToList();
    }

    private static int CountCells(List<LayoutLine> row) => row.Count;
}
