namespace AirPDF.Services;

/// <summary>
/// Detects bordered table grids from a rendered page bitmap (dark H/V rules).
/// </summary>
public sealed record DetectedTableGrid(
    IReadOnlyList<double> VerticalEdges,
    IReadOnlyList<double> HorizontalEdges)
{
    public int ColumnCount => Math.Max(0, VerticalEdges.Count - 1);
    public int RowCount => Math.Max(0, HorizontalEdges.Count - 1);

    public bool Contains(double xRatio, double yRatio) =>
        VerticalEdges.Count >= 2
        && HorizontalEdges.Count >= 2
        && xRatio >= VerticalEdges[0] - 0.01
        && xRatio <= VerticalEdges[^1] + 0.01
        && yRatio >= HorizontalEdges[0] - 0.01
        && yRatio <= HorizontalEdges[^1] + 0.01;

    public bool TryGetCell(double xRatio, double yRatio, out int row, out int col)
    {
        row = -1;
        col = -1;
        if (!Contains(xRatio, yRatio))
            return false;

        col = FindBucket(VerticalEdges, xRatio);
        row = FindBucket(HorizontalEdges, yRatio);
        return row >= 0 && col >= 0;
    }

    private static int FindBucket(IReadOnlyList<double> edges, double value)
    {
        for (var i = 0; i < edges.Count - 1; i++)
        {
            if (value >= edges[i] - 0.002 && value <= edges[i + 1] + 0.002)
                return i;
        }

        return -1;
    }
}

public static class TableGridDetector
{
    private const int DarkLuma = 150;
    private const double MinLineCoverage = 0.18;
    private const int MinRunPixels = 28;

    public static IReadOnlyList<DetectedTableGrid> Detect(byte[] bgra, int width, int height)
    {
        if (bgra.Length < width * height * 4 || width < 40 || height < 40)
            return [];

        var dark = BuildDarkMask(bgra, width, height);
        var horizontal = DetectLinePositions(dark, width, height, horizontal: true);
        var vertical = DetectLinePositions(dark, width, height, horizontal: false);

        if (horizontal.Count < 3 || vertical.Count < 3)
            return [];

        return BuildGrids(horizontal, vertical, width, height);
    }

    private static bool[] BuildDarkMask(byte[] bgra, int width, int height)
    {
        var mask = new bool[width * height];
        for (var i = 0; i < mask.Length; i++)
        {
            var o = i * 4;
            var b = bgra[o];
            var g = bgra[o + 1];
            var r = bgra[o + 2];
            var a = bgra[o + 3];
            if (a < 20)
                continue;

            // Near-black ink / rules on light background.
            var luma = (r * 30 + g * 59 + b * 11) / 100;
            var chroma = Math.Max(r, Math.Max(g, b)) - Math.Min(r, Math.Min(g, b));
            mask[i] = luma <= DarkLuma && chroma < 70;
        }

        return mask;
    }

    private static List<int> DetectLinePositions(bool[] dark, int width, int height, bool horizontal)
    {
        var primary = horizontal ? height : width;
        var secondary = horizontal ? width : height;
        var minCoverage = Math.Max(MinRunPixels, (int)(secondary * MinLineCoverage));
        var scores = new int[primary];

        for (var p = 0; p < primary; p++)
        {
            var bestRun = 0;
            var run = 0;
            var darkCount = 0;
            for (var s = 0; s < secondary; s++)
            {
                var idx = horizontal ? p * width + s : s * width + p;
                if (dark[idx])
                {
                    run++;
                    darkCount++;
                    if (run > bestRun)
                        bestRun = run;
                }
                else
                {
                    run = 0;
                }
            }

            // Prefer continuous rules over speckles.
            if (bestRun >= minCoverage || darkCount >= secondary * 0.55)
                scores[p] = Math.Max(bestRun, darkCount / 2);
        }

        return ExtractPeaks(scores, mergeDistance: Math.Max(2, primary / 400));
    }

    private static List<int> ExtractPeaks(int[] scores, int mergeDistance)
    {
        var threshold = scores.Max();
        if (threshold < MinRunPixels)
            return [];

        threshold = Math.Max(MinRunPixels, (int)(threshold * 0.35));
        var peaks = new List<int>();
        for (var i = 1; i < scores.Length - 1; i++)
        {
            if (scores[i] < threshold)
                continue;
            if (scores[i] >= scores[i - 1] && scores[i] >= scores[i + 1])
                peaks.Add(i);
        }

        if (peaks.Count == 0)
            return [];

        // Merge nearby peaks into one edge.
        var merged = new List<int>();
        var cluster = new List<int> { peaks[0] };
        for (var i = 1; i < peaks.Count; i++)
        {
            if (peaks[i] - cluster[^1] <= mergeDistance)
            {
                cluster.Add(peaks[i]);
            }
            else
            {
                merged.Add((int)Math.Round(cluster.Average()));
                cluster = [peaks[i]];
            }
        }

        merged.Add((int)Math.Round(cluster.Average()));
        return merged;
    }

    private static IReadOnlyList<DetectedTableGrid> BuildGrids(
        IReadOnlyList<int> horizontalPx,
        IReadOnlyList<int> verticalPx,
        int width,
        int height)
    {
        // Split into local grids by large gaps between consecutive rules.
        var hGroups = SplitByGap(horizontalPx, Math.Max(24, height / 12));
        var vGroups = SplitByGap(verticalPx, Math.Max(24, width / 12));
        var grids = new List<DetectedTableGrid>();

        foreach (var hs in hGroups)
        foreach (var vs in vGroups)
        {
            if (hs.Count < 3 || vs.Count < 3)
                continue;

            // Keep only line sets that form a reasonably compact rectangle.
            var hSpan = hs[^1] - hs[0];
            var vSpan = vs[^1] - vs[0];
            if (hSpan < height * 0.04 || vSpan < width * 0.08)
                continue;

            var avgHGap = hSpan / (double)(hs.Count - 1);
            var avgVGap = vSpan / (double)(vs.Count - 1);
            if (avgHGap < 8 || avgVGap < 12)
                continue;

            grids.Add(new DetectedTableGrid(
                vs.Select(x => x / (double)Math.Max(1, width - 1)).ToArray(),
                hs.Select(y => y / (double)Math.Max(1, height - 1)).ToArray()));
        }

        // Prefer larger grids first when assigning text.
        return grids
            .OrderByDescending(g => (g.VerticalEdges[^1] - g.VerticalEdges[0]) *
                                    (g.HorizontalEdges[^1] - g.HorizontalEdges[0]))
            .ToList();
    }

    private static List<List<int>> SplitByGap(IReadOnlyList<int> values, int maxGap)
    {
        if (values.Count == 0)
            return [];

        var groups = new List<List<int>>();
        var current = new List<int> { values[0] };
        for (var i = 1; i < values.Count; i++)
        {
            if (values[i] - current[^1] > maxGap)
            {
                groups.Add(current);
                current = [values[i]];
            }
            else
            {
                current.Add(values[i]);
            }
        }

        groups.Add(current);
        return groups;
    }
}
