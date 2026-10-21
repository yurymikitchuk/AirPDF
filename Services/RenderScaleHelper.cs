namespace AirPDF.Services;

/// <summary>Maps logical zoom + screen DPI to Docnet scale and bitmap DPI metadata.</summary>
public static class RenderScaleHelper
{
    /// <summary>
    /// Extra raster density beyond device pixels. WPF Fant downsample yields sharper stems
    /// than native PDFium AA at 1×.
    /// </summary>
    public const double Oversample = 1.35;

    public static double ToRenderScale(double logicalZoom, double dpiScale) =>
        logicalZoom * dpiScale * Oversample;

    public static double ToBitmapDpi(double dpiScale) =>
        96.0 * dpiScale * Oversample;
}
