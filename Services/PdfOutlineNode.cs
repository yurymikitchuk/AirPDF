namespace AirPDF.Services;

public sealed class PdfOutlineNode
{
    public required string Title { get; init; }
    public int PageIndex { get; init; } = -1;
    public List<PdfOutlineNode> Children { get; init; } = [];
}
