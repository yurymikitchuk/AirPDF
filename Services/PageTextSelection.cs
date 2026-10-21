using System.Text;
using System.Windows;
using Docnet.Core.Models;

namespace AirPDF.Services;

/// <summary>Text-layer hit-testing and reading-order reconstruction for selection/copy.</summary>
public static class PageTextSelection
{
    public static Rect Normalize(Rect nativeRect)
    {
        var left = Math.Min(nativeRect.Left, nativeRect.Right);
        var top = Math.Min(nativeRect.Top, nativeRect.Bottom);
        var right = Math.Max(nativeRect.Left, nativeRect.Right);
        var bottom = Math.Max(nativeRect.Top, nativeRect.Bottom);
        return new Rect(left, top, right - left, bottom - top);
    }

    public static bool IsTiny(Rect normalized) =>
        normalized.Width < 1 && normalized.Height < 1;

    public static List<Character> SelectIntersecting(IReadOnlyList<Character> chars, Rect normalized)
    {
        var selected = new List<Character>();
        var left = normalized.Left;
        var top = normalized.Top;
        var right = normalized.Right;
        var bottom = normalized.Bottom;

        foreach (var ch in chars)
        {
            var box = ch.Box;
            if (box.Right < left || box.Left > right || box.Bottom < top || box.Top > bottom)
                continue;
            selected.Add(ch);
        }

        return selected;
    }

    public static string BuildText(IReadOnlyList<Character> characters)
    {
        if (characters.Count == 0)
            return "";

        var ordered = characters
            .OrderBy(c => c.Box.Top)
            .ThenBy(c => c.Box.Left)
            .ToList();

        var heights = ordered
            .Select(c => Math.Max(1, c.Box.Bottom - c.Box.Top))
            .OrderBy(h => h)
            .ToList();
        var medianH = heights[heights.Count / 2];
        var lineThreshold = Math.Max(4, medianH * 0.6);
        var spaceThreshold = Math.Max(3, medianH * 0.35);

        var sb = new StringBuilder(ordered.Count + 16);
        int? prevBottom = null;
        int? prevRight = null;

        foreach (var ch in ordered)
        {
            var box = ch.Box;
            if (prevBottom is not null && box.Top > prevBottom.Value + lineThreshold)
            {
                sb.AppendLine();
                prevRight = null;
            }
            else if (prevRight is not null && box.Left > prevRight.Value + spaceThreshold)
            {
                sb.Append(' ');
            }

            sb.Append(ch.Char);
            prevBottom = box.Bottom;
            prevRight = box.Right;
        }

        return sb.ToString();
    }
}
