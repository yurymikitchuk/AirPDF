using System.Windows;
using System.Windows.Media;

namespace AirPDF.Services;

public static class ThemeService
{
    private static readonly string[] BrushKeys =
    [
        "BgApp", "BgPanel", "BgElevated", "BgInput", "BgHover", "BgActive",
        "SidebarBg", "SidebarTabBarBg", "SidebarTabActive",
        "BorderSubtle", "BorderStrong",
        "TextPrimary", "TextMuted", "TextBright",
        "Accent", "AccentHover", "AccentMuted",
        "GroupBg", "GroupBorder",
        "Danger", "HighlightFill", "HighlightActive",
        "LoadingOverlayBg",
        "ScrollBarTrack", "ScrollBarThumb", "ScrollBarThumbHover"
    ];

    public static AppTheme Current { get; private set; } = AppTheme.Light;

    public static void Initialize(AppTheme theme) => Apply(theme);

    public static void Apply(AppTheme theme)
    {
        Current = theme;
        var colors = theme == AppTheme.Dark ? Dark : Light;
        var resources = Application.Current.Resources;

        foreach (var key in BrushKeys)
        {
            if (!colors.TryGetValue(key, out var hex))
                continue;

            // ResourceDictionary freezes Freezables on insert — never mutate in place.
            var color = (Color)ColorConverter.ConvertFromString(hex)!;
            resources[key] = new SolidColorBrush(color);
        }
    }

    public static AppTheme Parse(string? value) =>
        string.Equals(value, nameof(AppTheme.Dark), StringComparison.OrdinalIgnoreCase)
            ? AppTheme.Dark
            : AppTheme.Light;

    private static readonly Dictionary<string, string> Light = new()
    {
        ["BgApp"] = "#E8E8E8",
        ["BgPanel"] = "#F3F3F3",
        ["BgElevated"] = "#FAFAFA",
        ["BgInput"] = "#FFFFFF",
        ["BgHover"] = "#E4E4E4",
        ["BgActive"] = "#D8D8D8",
        ["SidebarBg"] = "#DEDEDE",
        ["SidebarTabBarBg"] = "#DEDEDE",
        ["SidebarTabActive"] = "#F3F3F3",
        ["BorderSubtle"] = "#D4D4D4",
        ["BorderStrong"] = "#C2C2C2",
        ["TextPrimary"] = "#333333",
        ["TextMuted"] = "#6B6B6B",
        ["TextBright"] = "#1E1E1E",
        ["Accent"] = "#3A3A3A",
        ["AccentHover"] = "#2A2A2A",
        ["AccentMuted"] = "#D0D0D0",
        ["GroupBg"] = "#E6E6E6",
        ["GroupBorder"] = "#BEBEBE",
        ["Danger"] = "#C50F1F",
        ["HighlightFill"] = "#66FFEB3B",
        ["HighlightActive"] = "#99FF9800",
        ["LoadingOverlayBg"] = "#88E8E8E8",
        ["ScrollBarTrack"] = "#E0E0E0",
        ["ScrollBarThumb"] = "#B0B0B0",
        ["ScrollBarThumbHover"] = "#909090"
    };

    private static readonly Dictionary<string, string> Dark = new()
    {
        ["BgApp"] = "#2B2B2B",
        ["BgPanel"] = "#323232",
        ["BgElevated"] = "#3A3A3A",
        ["BgInput"] = "#2F2F2F",
        ["BgHover"] = "#454545",
        ["BgActive"] = "#505050",
        ["SidebarBg"] = "#333333",
        ["SidebarTabBarBg"] = "#333333",
        ["SidebarTabActive"] = "#454545",
        ["BorderSubtle"] = "#3F3F3F",
        ["BorderStrong"] = "#555555",
        ["TextPrimary"] = "#E0E0E0",
        ["TextMuted"] = "#A0A0A0",
        ["TextBright"] = "#F2F2F2",
        ["Accent"] = "#6A6A6A",
        ["AccentHover"] = "#5A5A5A",
        ["AccentMuted"] = "#4A4A4A",
        ["GroupBg"] = "#383838",
        ["GroupBorder"] = "#4A4A4A",
        ["Danger"] = "#E74C3C",
        ["HighlightFill"] = "#66FFEB3B",
        ["HighlightActive"] = "#99FF9800",
        ["LoadingOverlayBg"] = "#882B2B2B",
        ["ScrollBarTrack"] = "#2A2A2A",
        ["ScrollBarThumb"] = "#5A5A5A",
        ["ScrollBarThumbHover"] = "#7A7A7A"
    };
}
