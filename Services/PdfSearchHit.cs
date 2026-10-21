using System.Windows;

namespace AirPDF.Services;

public sealed record PdfSearchHit(int PageIndex, Rect Bounds);
