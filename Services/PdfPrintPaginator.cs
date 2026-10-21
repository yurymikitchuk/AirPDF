using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace AirPDF.Services;

public sealed class PdfPrintPaginator : DocumentPaginator
{
    private const double PrintRenderScale = 2.2;

    private readonly PdfRenderer _renderer;
    private readonly Size _pageSize;
    private readonly IReadOnlyList<int> _pageIndexes;
    private readonly PrintScaleMode _scaleMode;

    public PdfPrintPaginator(
        PdfRenderer renderer,
        Size pageSize,
        IReadOnlyList<int> pageIndexes,
        PrintScaleMode scaleMode)
    {
        _renderer = renderer;
        _pageSize = pageSize;
        _pageIndexes = pageIndexes;
        _scaleMode = scaleMode;
    }

    public override bool IsPageCountValid => true;
    public override int PageCount => _pageIndexes.Count;

    public override Size PageSize
    {
        get => _pageSize;
        set { }
    }

    public override IDocumentPaginatorSource Source => null!;

    public override DocumentPage GetPage(int pageNumber)
    {
        var pdfPage = _pageIndexes[pageNumber];
        var bitmap = _renderer.RenderPage(pdfPage, PrintRenderScale, useCache: false);
        var native = _renderer.GetNativePageSize(pdfPage);

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            var dest = PrintLayout.ComputeContentRect(bitmap, native, _pageSize, _scaleMode);
            dc.DrawRectangle(Brushes.White, null, new Rect(_pageSize));
            dc.PushClip(new RectangleGeometry(new Rect(_pageSize)));
            dc.DrawImage(bitmap, dest);
            dc.Pop();
        }

        return new DocumentPage(visual, _pageSize, new Rect(_pageSize), new Rect(_pageSize));
    }
}
