using System.Globalization;
using System.Text.RegularExpressions;

namespace AirPDF.Services;

public static partial class PageRangeParser
{
    public static bool TryParse(string input, int pageCount, out List<int> pages, out string? error)
    {
        pages = [];
        error = null;

        if (pageCount <= 0)
        {
            error = "Нет страниц для печати.";
            return false;
        }

        var raw = input.Trim();
        if (string.IsNullOrWhiteSpace(raw))
        {
            error = "Укажите страницы.";
            return false;
        }

        var seen = new HashSet<int>();
        foreach (var part in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (RangeRegex().IsMatch(part))
            {
                var bits = part.Split('-', StringSplitOptions.TrimEntries);
                if (!int.TryParse(bits[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var start) ||
                    !int.TryParse(bits[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var end))
                {
                    error = $"Неверный диапазон: {part}";
                    return false;
                }

                if (start > end)
                    (start, end) = (end, start);

                for (var p = start; p <= end; p++)
                {
                    if (p < 1 || p > pageCount)
                    {
                        error = $"Страница {p} вне диапазона 1–{pageCount}.";
                        return false;
                    }

                    if (seen.Add(p))
                        pages.Add(p - 1);
                }
            }
            else if (int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out var page))
            {
                if (page < 1 || page > pageCount)
                {
                    error = $"Страница {page} вне диапазона 1–{pageCount}.";
                    return false;
                }

                if (seen.Add(page))
                    pages.Add(page - 1);
            }
            else
            {
                error = $"Не удалось распознать: {part}";
                return false;
            }
        }

        if (pages.Count == 0)
        {
            error = "Не выбрано ни одной страницы.";
            return false;
        }

        return true;
    }

    [GeneratedRegex(@"^\d+\s*-\s*\d+$")]
    private static partial Regex RangeRegex();
}
