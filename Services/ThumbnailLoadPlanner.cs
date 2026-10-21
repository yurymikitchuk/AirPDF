namespace AirPDF.Services;

/// <summary>Orders thumbnail page indices: current vicinity, then visible band, then the rest.</summary>
public static class ThumbnailLoadPlanner
{
    public const double DefaultItemHeight = 230;

    public static List<int> Plan(
        int count,
        int currentPage,
        double scrollOffset,
        double viewportHeight,
        double itemHeight = DefaultItemHeight)
    {
        var ordered = new List<int>(Math.Max(0, count));
        if (count <= 0)
            return ordered;

        var seen = new bool[count];

        void Add(int index)
        {
            if (index < 0 || index >= count || seen[index])
                return;
            seen[index] = true;
            ordered.Add(index);
        }

        Add(currentPage);
        Add(currentPage - 1);
        Add(currentPage + 1);

        var viewport = Math.Max(1, viewportHeight);
        var first = Math.Max(0, (int)(scrollOffset / itemHeight) - 1);
        var last = Math.Min(count - 1, (int)((scrollOffset + viewport) / itemHeight) + 2);
        for (var i = first; i <= last; i++)
            Add(i);

        for (var i = 0; i < count; i++)
            Add(i);

        return ordered;
    }
}
