using System.Windows;

namespace AirPDF.Services;

/// <summary>Pure fit-zoom math for the viewer ScrollViewer (no UI dependencies).</summary>
public static class ViewportFitCalculator
{
    private const double Safety = 1;
    private const double MinClient = 40;
    private const double MinZoom = 0.25;
    private const double MaxZoom = 4.0;

    public static double? ComputeZoom(
        PageFitMode mode,
        Size clientSize,
        Thickness pageBorder,
        Size nativePage,
        double verticalScrollBarWidth,
        double horizontalScrollBarHeight)
    {
        var clientW = clientSize.Width;
        var clientH = clientSize.Height;
        if (double.IsNaN(clientW) || double.IsNaN(clientH) || clientW <= MinClient || clientH <= MinClient)
            return null;
        if (nativePage.Width <= 0 || nativePage.Height <= 0)
            return null;

        var availW = clientW - pageBorder.Left - pageBorder.Right - Safety;
        var availH = clientH - pageBorder.Top - pageBorder.Bottom - Safety;

        double zoom = 1;
        for (var i = 0; i < 3; i++)
        {
            availW = Math.Max(MinClient, availW);
            availH = Math.Max(MinClient, availH);

            var zoomW = availW / nativePage.Width;
            var zoomH = availH / nativePage.Height;
            zoom = mode switch
            {
                PageFitMode.Width => zoomW,
                PageFitMode.Height => zoomH,
                _ => Math.Min(zoomW, zoomH)
            };

            // Floor to 0.01 so display percent never rounds up into scrollbars.
            zoom = Math.Clamp(Math.Floor(zoom * 100.0) / 100.0, MinZoom, MaxZoom);

            var contentW = nativePage.Width * zoom + pageBorder.Left + pageBorder.Right;
            var contentH = nativePage.Height * zoom + pageBorder.Top + pageBorder.Bottom;
            var needV = contentH > clientH + 0.5;
            var needH = contentW > clientW + 0.5;

            var nextW = clientW - pageBorder.Left - pageBorder.Right - Safety
                        - (mode == PageFitMode.Width && needV ? verticalScrollBarWidth : 0);
            var nextH = clientH - pageBorder.Top - pageBorder.Bottom - Safety
                        - (mode == PageFitMode.Height && needH ? horizontalScrollBarHeight : 0);

            if (Math.Abs(nextW - availW) < 0.5 && Math.Abs(nextH - availH) < 0.5)
                break;

            availW = nextW;
            availH = nextH;
        }

        return zoom;
    }
}
