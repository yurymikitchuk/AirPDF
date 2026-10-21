using System.Windows;

namespace AirPDF.Services;

public enum PdfLinkKind
{
    Internal,
    Uri
}

public sealed class PdfPageLink
{
    public required PdfLinkKind Kind { get; init; }
    /// <summary>Bounds in top-left page space (same as Docnet glyphs / rendered image).</summary>
    public required Rect Bounds { get; init; }
    public int TargetPageIndex { get; init; } = -1;
    public string? Uri { get; init; }
}
