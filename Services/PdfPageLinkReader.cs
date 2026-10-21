using System.Runtime.InteropServices;
using System.Text;
using System.Windows;

namespace AirPDF.Services;

/// <summary>Loads PDF link annotations (internal + URI) via PDFium.</summary>
public static class PdfPageLinkReader
{
    private const uint PdfActionGoto = 1;
    private const uint PdfActionRemoteGoto = 2;
    private const uint PdfActionUri = 3;

    public static IReadOnlyList<PdfPageLink> Load(string filePath, int pageIndex, Size pageSize)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        if (pageIndex < 0 || pageSize.Width <= 0 || pageSize.Height <= 0)
            return [];

        PdfRenderer.Warmup();
        lock (PdfRenderer.PdfiumLock)
        {
            var doc = Native.FPDF_LoadDocument(filePath, null);
            if (doc == IntPtr.Zero)
                return [];

            try
            {
                var page = Native.FPDF_LoadPage(doc, pageIndex);
                if (page == IntPtr.Zero)
                    return [];

                try
                {
                    var links = new List<PdfPageLink>();
                    var pos = 0;
                    while (Native.FPDFLink_Enumerate(page, ref pos, out var link) != 0 && link != IntPtr.Zero)
                    {
                        if (Native.FPDFLink_GetAnnotRect(link, out var pdfRect) == 0)
                            continue;

                        var bounds = ToTopLeftBounds(pdfRect, pageSize.Height);
                        if (bounds.Width < 1 || bounds.Height < 1)
                            continue;

                        var action = Native.FPDFLink_GetAction(link);
                        var dest = Native.FPDFLink_GetDest(doc, link);

                        if (action != IntPtr.Zero)
                        {
                            var type = Native.FPDFAction_GetType(action);
                            if (type == PdfActionUri)
                            {
                                var uri = GetUri(doc, action);
                                if (!string.IsNullOrWhiteSpace(uri))
                                {
                                    links.Add(new PdfPageLink
                                    {
                                        Kind = PdfLinkKind.Uri,
                                        Bounds = bounds,
                                        Uri = uri.Trim()
                                    });
                                }

                                continue;
                            }

                            if (type is PdfActionGoto or PdfActionRemoteGoto)
                                dest = Native.FPDFAction_GetDest(doc, action);
                        }

                        if (dest != IntPtr.Zero)
                        {
                            var target = Native.FPDFDest_GetDestPageIndex(doc, dest);
                            if (target >= 0)
                            {
                                links.Add(new PdfPageLink
                                {
                                    Kind = PdfLinkKind.Internal,
                                    Bounds = bounds,
                                    TargetPageIndex = target
                                });
                            }
                        }
                    }

                    return links;
                }
                finally
                {
                    Native.FPDF_ClosePage(page);
                }
            }
            finally
            {
                Native.FPDF_CloseDocument(doc);
            }
        }
    }

    public static PdfPageLink? HitTest(IReadOnlyList<PdfPageLink> links, Point nativePoint)
    {
        for (var i = links.Count - 1; i >= 0; i--)
        {
            if (links[i].Bounds.Contains(nativePoint))
                return links[i];
        }

        return null;
    }

    private static Rect ToTopLeftBounds(FsRectF pdf, double pageHeight)
    {
        // PDFium page space: origin bottom-left; top > bottom.
        var left = Math.Min(pdf.left, pdf.right);
        var right = Math.Max(pdf.left, pdf.right);
        var pdfBottom = Math.Min(pdf.bottom, pdf.top);
        var pdfTop = Math.Max(pdf.bottom, pdf.top);
        var top = pageHeight - pdfTop;
        var bottom = pageHeight - pdfBottom;
        return new Rect(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top));
    }

    private static string GetUri(IntPtr doc, IntPtr action)
    {
        var bytes = Native.FPDFAction_GetURIPath(doc, action, null, 0);
        if (bytes == 0)
            return string.Empty;

        var buffer = new byte[bytes];
        Native.FPDFAction_GetURIPath(doc, action, buffer, bytes);
        // URI path is a byte string; trim trailing nulls.
        var len = buffer.Length;
        while (len > 0 && buffer[len - 1] == 0)
            len--;
        return len == 0 ? string.Empty : Encoding.UTF8.GetString(buffer, 0, len);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FsRectF
    {
        public float left;
        public float top;
        public float right;
        public float bottom;
    }

    private static class Native
    {
        private const string Dll = "pdfium";

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        public static extern IntPtr FPDF_LoadDocument(string file_path, string? password);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern void FPDF_CloseDocument(IntPtr document);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr FPDF_LoadPage(IntPtr document, int page_index);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern void FPDF_ClosePage(IntPtr page);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern int FPDFLink_Enumerate(IntPtr page, ref int start_pos, out IntPtr link_annot);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern int FPDFLink_GetAnnotRect(IntPtr link_annot, out FsRectF rect);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr FPDFLink_GetDest(IntPtr document, IntPtr link);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr FPDFLink_GetAction(IntPtr link);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern uint FPDFAction_GetType(IntPtr action);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr FPDFAction_GetDest(IntPtr document, IntPtr action);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern int FPDFDest_GetDestPageIndex(IntPtr document, IntPtr dest);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern uint FPDFAction_GetURIPath(IntPtr document, IntPtr action, byte[]? buffer, uint buflen);
    }
}
