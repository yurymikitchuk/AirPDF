using System.Runtime.InteropServices;
using System.Text;

namespace AirPDF.Services;

/// <summary>
/// Reads PDF bookmark/outline tree via PDFium (same native library Docnet uses).
/// </summary>
public static class PdfOutlineReader
{
    private const uint PdfActionGoto = 1;
    private const uint PdfActionRemoteGoto = 2;

    public static IReadOnlyList<PdfOutlineNode> Load(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        PdfRenderer.Warmup();

        lock (PdfRenderer.PdfiumLock)
        {
            var doc = Native.FPDF_LoadDocument(filePath, null);
            if (doc == IntPtr.Zero)
                return [];

            try
            {
                var roots = new List<PdfOutlineNode>();
                var child = Native.FPDFBookmark_GetFirstChild(doc, IntPtr.Zero);
                while (child != IntPtr.Zero)
                {
                    roots.Add(ReadNode(doc, child));
                    child = Native.FPDFBookmark_GetNextSibling(doc, child);
                }

                return roots;
            }
            finally
            {
                Native.FPDF_CloseDocument(doc);
            }
        }
    }

    private static PdfOutlineNode ReadNode(IntPtr doc, IntPtr bookmark)
    {
        var title = GetTitle(bookmark);
        var page = ResolvePageIndex(doc, bookmark);

        var node = new PdfOutlineNode
        {
            Title = string.IsNullOrWhiteSpace(title) ? "(без названия)" : title.Trim(),
            PageIndex = page
        };

        var child = Native.FPDFBookmark_GetFirstChild(doc, bookmark);
        while (child != IntPtr.Zero)
        {
            node.Children.Add(ReadNode(doc, child));
            child = Native.FPDFBookmark_GetNextSibling(doc, child);
        }

        return node;
    }

    private static string GetTitle(IntPtr bookmark)
    {
        var bytes = Native.FPDFBookmark_GetTitle(bookmark, null, 0);
        if (bytes == 0)
            return string.Empty;

        var buffer = new byte[bytes];
        Native.FPDFBookmark_GetTitle(bookmark, buffer, bytes);
        // PDFium returns UTF-16LE including a trailing null.
        var chars = Math.Max(0, (int)bytes / 2 - 1);
        return chars <= 0 ? string.Empty : Encoding.Unicode.GetString(buffer, 0, chars * 2);
    }

    private static int ResolvePageIndex(IntPtr doc, IntPtr bookmark)
    {
        var dest = Native.FPDFBookmark_GetDest(doc, bookmark);
        if (dest == IntPtr.Zero)
        {
            var action = Native.FPDFBookmark_GetAction(bookmark);
            if (action != IntPtr.Zero)
            {
                var type = Native.FPDFAction_GetType(action);
                if (type is PdfActionGoto or PdfActionRemoteGoto)
                    dest = Native.FPDFAction_GetDest(doc, action);
            }
        }

        if (dest == IntPtr.Zero)
            return -1;

        return Native.FPDFDest_GetDestPageIndex(doc, dest);
    }

    private static class Native
    {
        private const string Dll = "pdfium";

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        public static extern IntPtr FPDF_LoadDocument(string file_path, string? password);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern void FPDF_CloseDocument(IntPtr document);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr FPDFBookmark_GetFirstChild(IntPtr document, IntPtr bookmark);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr FPDFBookmark_GetNextSibling(IntPtr document, IntPtr bookmark);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern uint FPDFBookmark_GetTitle(IntPtr bookmark, byte[]? buffer, uint buflen);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr FPDFBookmark_GetDest(IntPtr document, IntPtr bookmark);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr FPDFBookmark_GetAction(IntPtr bookmark);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern uint FPDFAction_GetType(IntPtr action);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr FPDFAction_GetDest(IntPtr document, IntPtr action);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern int FPDFDest_GetDestPageIndex(IntPtr document, IntPtr dest);
    }
}
