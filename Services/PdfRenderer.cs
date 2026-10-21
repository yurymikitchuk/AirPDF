using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Docnet.Core;
using Docnet.Core.Models;
using Docnet.Core.Readers;

namespace AirPDF.Services;

public sealed class PdfRenderer : IDisposable
{
    private const int MaxCachedPages = 18;
    private const double MetaScale = 1.0;
    private const double ThumbScale = 0.2;

    /// <summary>
    /// Native PDFium AA + annotations. No LCD ClearType (softens when not pixel-aligned).
    /// </summary>
    private const RenderFlags ViewRenderFlags = RenderFlags.RenderAnnotations;

    private readonly object _stateGate = new();

    /// <summary>
    /// Process-wide lock for all Docnet/PDFium work. Separate per-reader gates are not enough —
    /// concurrent GetImage/GetText/FPDF_LoadDocument across threads crashes native pdfium.
    /// </summary>
    internal static readonly object PdfiumLock = new();

    /// <summary>
    /// Blocks until no PDFium call holds <see cref="PdfiumLock"/> (or timeout).
    /// Call from a worker thread before Print/F11 so they do not overlap an in-flight GetImage.
    /// </summary>
    public void WaitForRasterIdle(int timeoutMs = 2000)
    {
        if (!Monitor.TryEnter(PdfiumLock, Math.Max(0, timeoutMs)))
            return;
        Monitor.Exit(PdfiumLock);
    }

    private readonly ConcurrentDictionary<(int Page, int ZoomKey), CacheEntry> _cache = new();
    private readonly ConcurrentDictionary<int, BitmapSource> _thumbCache = new();

    private string? _filePath;
    private Size[]? _pageSizes;
    private IDocReader? _renderReader;
    private double _renderScale;
    /// <summary>
    /// DocReaders keyed by zoom. We must NOT Dispose a reader just to switch scale —
    /// Docnet AVs in FPDFDOC_ExitFormFillEnvironment (print preview / F11 fit).
    /// All pooled readers are released together on Close.
    /// </summary>
    private readonly Dictionary<int, IDocReader> _renderReadersByZoom = new();
    private IDocReader? _metaReader;
    private IDocReader? _thumbReader;
    private int _pageCount;
    private bool _disposed;
    private long _accessClock;
    private long _openGeneration;
    private List<int>? _sparsePagesCache;
    private int _sparseCachedMinLetters = int.MinValue;

    public bool IsOpen
    {
        get { lock (_stateGate) return _filePath is not null; }
    }

    public int PageCount
    {
        get { lock (_stateGate) return _pageCount; }
    }

    public string? FilePath
    {
        get { lock (_stateGate) return _filePath; }
    }

    public static void Warmup()
    {
        try
        {
            _ = DocLib.Instance;
        }
        catch
        {
            // Native warmup is best-effort.
        }
    }

    public void Open(string path)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException("PDF-файл не найден.", fullPath);

        long generation;
        lock (_stateGate)
            generation = ++_openGeneration;

        // Open by file path so large documents are not fully loaded into managed memory.
        int count;
        lock (PdfiumLock)
        {
            using var probe = DocLib.Instance.GetDocReader(fullPath, new PageDimensions(0.1));
            count = probe.GetPageCount();
        }
        if (count <= 0)
            throw new InvalidOperationException("PDF не содержит страниц.");

        DisposeAllReaders();

        lock (_stateGate)
        {
            if (generation != _openGeneration)
                return;

            _cache.Clear();
            _thumbCache.Clear();
            _sparsePagesCache = null;
            _sparseCachedMinLetters = int.MinValue;
            _filePath = fullPath;
            _pageSizes = new Size[count];
            _pageCount = count;
            _accessClock = 0;
        }
    }

    public void Close()
    {
        lock (_stateGate)
        {
            _openGeneration++;
            _cache.Clear();
            _thumbCache.Clear();
            _sparsePagesCache = null;
            _sparseCachedMinLetters = int.MinValue;
            _filePath = null;
            _pageSizes = null;
            _pageCount = 0;
            _accessClock = 0;
        }

        DisposeAllReaders();
    }

    public BitmapSource RenderThumbnail(int pageIndex, double maxWidth = 120)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_thumbCache.TryGetValue(pageIndex, out var cached))
            return cached;

        string path;
        long generation;
        lock (_stateGate)
        {
            if (_filePath is null)
                throw new InvalidOperationException("Документ не открыт.");
            if (pageIndex < 0 || pageIndex >= _pageCount)
                throw new ArgumentOutOfRangeException(nameof(pageIndex));
            path = _filePath;
            generation = _openGeneration;
        }

        // Dedicated thumb reader at a fixed low scale — never recreates the view render reader.
        _ = maxWidth;
        byte[] rawBytes;
        int width;
        int height;
        lock (PdfiumLock)
        {
            EnsureAlive(path, generation);
            EnsureThumbReader_NoLock(path);
            using var pageReader = _thumbReader!.GetPageReader(pageIndex);
            width = pageReader.GetPageWidth();
            height = pageReader.GetPageHeight();
            rawBytes = pageReader.GetImage();
        }

        EnsureAlive(path, generation);
        var bitmap = CreateFrozenBitmap(rawBytes, width, height, 96, 96);
        _thumbCache[pageIndex] = bitmap;

        if (_thumbCache.Count > 400)
        {
            foreach (var key in _thumbCache.Keys.OrderBy(k => k).Take(80).ToList())
                _thumbCache.TryRemove(key, out _);
        }

        return bitmap;
    }

    public bool TryGetCached(int pageIndex, double scale, out BitmapSource bitmap)
    {
        bitmap = null!;
        scale = ClampRenderScale(scale);
        var zoomKey = ToZoomKey(scale);
        if (!_cache.TryGetValue((pageIndex, zoomKey), out var entry))
            return false;

        entry.LastAccess = Interlocked.Increment(ref _accessClock);
        bitmap = entry.Bitmap;
        return true;
    }

    /// <param name="scale">Docnet page scale (logical zoom × DPI scale for crisp HiDPI output).</param>
    /// <param name="dpi">Bitmap metadata DPI so WPF lays out 1:1 with device pixels.</param>
    public BitmapSource RenderPage(int pageIndex, double scale, bool useCache = true, double dpi = 96)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        scale = ClampRenderScale(scale);
        dpi = Math.Clamp(dpi, 96, 384);
        var zoomKey = ToZoomKey(scale);

        if (useCache && TryGetCached(pageIndex, scale, out var cached))
            return cached;

        string path;
        long generation;
        lock (_stateGate)
        {
            if (_filePath is null)
                throw new InvalidOperationException("Документ не открыт.");
            if (pageIndex < 0 || pageIndex >= _pageCount)
                throw new ArgumentOutOfRangeException(nameof(pageIndex));
            path = _filePath;
            generation = _openGeneration;
        }

        byte[] rawBytes;
        int width;
        int height;
        lock (PdfiumLock)
        {
            EnsureAlive(path, generation);
            EnsureRenderReader_NoLock(path, scale);
            using var pageReader = _renderReader!.GetPageReader(pageIndex);
            width = pageReader.GetPageWidth();
            height = pageReader.GetPageHeight();
            rawBytes = pageReader.GetImage(ViewRenderFlags);
        }

        EnsureAlive(path, generation);
        var bitmap = CreateFrozenBitmap(rawBytes, width, height, dpi, dpi);
        if (useCache)
        {
            lock (_stateGate)
            {
                if (_filePath is not null &&
                    _openGeneration == generation &&
                    string.Equals(_filePath, path, StringComparison.OrdinalIgnoreCase))
                {
                    StoreCache(pageIndex, zoomKey, bitmap);
                }
            }
        }

        return bitmap;
    }

    public Size GetNativePageSize(int pageIndex)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (PdfiumLock)
        {
            lock (_stateGate)
            {
                if (_filePath is null || _pageSizes is null)
                    throw new InvalidOperationException("Документ не открыт.");
                if (pageIndex < 0 || pageIndex >= _pageCount)
                    throw new ArgumentOutOfRangeException(nameof(pageIndex));
            }

            return GetOrLoadPageSize_NoLock(pageIndex);
        }
    }

    public string GetPageText(int pageIndex)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        string path;
        lock (_stateGate)
        {
            if (_filePath is null)
                throw new InvalidOperationException("Документ не открыт.");
            if (pageIndex < 0 || pageIndex >= _pageCount)
                throw new ArgumentOutOfRangeException(nameof(pageIndex));
            path = _filePath;
        }

        lock (PdfiumLock)
        {
            EnsureMetaReader_NoLock(path);
            using var pageReader = _metaReader!.GetPageReader(pageIndex);
            return pageReader.GetText() ?? string.Empty;
        }
    }

    public (Size Size, string Text) GetPageExportInfo(int pageIndex)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        string path;
        lock (_stateGate)
        {
            if (_filePath is null || _pageSizes is null)
                throw new InvalidOperationException("Документ не открыт.");
            if (pageIndex < 0 || pageIndex >= _pageCount)
                throw new ArgumentOutOfRangeException(nameof(pageIndex));
            path = _filePath;
        }

        lock (PdfiumLock)
        {
            EnsureMetaReader_NoLock(path);
            var size = GetOrLoadPageSize_NoLock(pageIndex);
            using var pageReader = _metaReader!.GetPageReader(pageIndex);
            return (size, pageReader.GetText() ?? string.Empty);
        }
    }

    public List<Character> GetPageCharacters(int pageIndex)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        string path;
        lock (_stateGate)
        {
            if (_filePath is null)
                throw new InvalidOperationException("Документ не открыт.");
            if (pageIndex < 0 || pageIndex >= _pageCount)
                throw new ArgumentOutOfRangeException(nameof(pageIndex));
            path = _filePath;
        }

        lock (PdfiumLock)
        {
            EnsureMetaReader_NoLock(path);
            using var pageReader = _metaReader!.GetPageReader(pageIndex);
            return pageReader.GetCharacters().ToList();
        }
    }

    /// <summary>
    /// Shares the open file path with a separate renderer instance so export/print
    /// does not contend with the interactive viewer lock/readers.
    /// </summary>
    public PdfRenderer CreateIsolatedCopy()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_stateGate)
        {
            if (_filePath is null)
                throw new InvalidOperationException("Документ не открыт.");

            var copy = new PdfRenderer
            {
                _filePath = _filePath,
                _pageCount = _pageCount,
                _pageSizes = _pageSizes is null ? null : (Size[])_pageSizes.Clone()
            };
            return copy;
        }
    }

    public bool EstimateLikelyScanned(int samplePages = 3)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!IsOpen || PageCount <= 0)
            return false;

        var take = Math.Min(samplePages, PageCount);
        var usefulPages = 0;
        for (var i = 0; i < take; i++)
        {
            var text = GetPageText(i);
            var letters = text.Count(char.IsLetterOrDigit);
            if (letters >= 20)
                usefulPages++;
        }

        return usefulPages == 0;
    }

    public (byte[] Bgra, int Width, int Height) RenderPageRaw(int pageIndex, double scale)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        scale = ClampRenderScale(scale);

        string path;
        long generation;
        lock (_stateGate)
        {
            if (_filePath is null)
                throw new InvalidOperationException("Документ не открыт.");
            if (pageIndex < 0 || pageIndex >= _pageCount)
                throw new ArgumentOutOfRangeException(nameof(pageIndex));
            path = _filePath;
            generation = _openGeneration;
        }

        lock (PdfiumLock)
        {
            EnsureAlive(path, generation);
            EnsureRenderReader_NoLock(path, scale);
            using var pageReader = _renderReader!.GetPageReader(pageIndex);
            var width = pageReader.GetPageWidth();
            var height = pageReader.GetPageHeight();
            // OCR/export: default raster without LCD subpixel text.
            var raw = pageReader.GetImage();
            return (raw, width, height);
        }
    }

    public List<PdfSearchHit> Search(
        string query,
        CancellationToken token = default,
        IProgress<(int Page, int Total)>? progress = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (string.IsNullOrWhiteSpace(query))
            return [];

        string path;
        int pageCount;
        lock (_stateGate)
        {
            if (_filePath is null)
                throw new InvalidOperationException("Документ не открыт.");
            path = _filePath;
            pageCount = _pageCount;
        }

        var needle = query.Trim();
        var bag = new ConcurrentBag<PdfSearchHit>();
        var completed = 0;
        var dop = Math.Clamp(Environment.ProcessorCount / 2, 2, 4);

        // One DocReader per worker thread — not one per page.
        // All Docnet calls still serialize on PdfiumLock (library is process-global).
        using var localReaders = new ThreadLocal<IDocReader>(
            () =>
            {
                lock (PdfiumLock)
                    return DocLib.Instance.GetDocReader(path, new PageDimensions(MetaScale));
            },
            trackAllValues: true);

        try
        {
            Parallel.For(0, pageCount, new ParallelOptions
            {
                MaxDegreeOfParallelism = dop,
                CancellationToken = token
            }, page =>
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    var reader = localReaders.Value!;
                    List<Character> characters;
                    lock (PdfiumLock)
                    {
                        using var pageReader = reader.GetPageReader(page);
                        characters = pageReader.GetCharacters().ToList();
                    }
                    if (characters.Count == 0)
                        return;

                    var haystack = BuildHaystack(characters);
                    var start = 0;
                    while (start <= haystack.Length - needle.Length)
                    {
                        token.ThrowIfCancellationRequested();
                        var index = haystack.IndexOf(needle, start, StringComparison.OrdinalIgnoreCase);
                        if (index < 0)
                            break;

                        var end = index + needle.Length - 1;
                        var left = int.MaxValue;
                        var top = int.MaxValue;
                        var right = int.MinValue;
                        var bottom = int.MinValue;

                        for (var i = index; i <= end && i < characters.Count; i++)
                        {
                            var box = characters[i].Box;
                            left = Math.Min(left, box.Left);
                            top = Math.Min(top, box.Top);
                            right = Math.Max(right, box.Right);
                            bottom = Math.Max(bottom, box.Bottom);
                        }

                        if (left < right && top < bottom)
                            bag.Add(new PdfSearchHit(page, new Rect(left, top, right - left, bottom - top)));

                        start = index + Math.Max(1, needle.Length);
                    }
                }
                finally
                {
                    var done = Interlocked.Increment(ref completed);
                    progress?.Report((done, pageCount));
                }
            });
        }
        finally
        {
            lock (PdfiumLock)
            {
                foreach (var reader in localReaders.Values)
                    SafeDisposeDocReader(reader);
            }
        }

        return bag.OrderBy(h => h.PageIndex).ThenBy(h => h.Bounds.Top).ThenBy(h => h.Bounds.Left).ToList();
    }

    /// <summary>Pages with little/no extractable text — candidates for OCR search.</summary>
    public List<int> FindTextSparsePages(int minLetters = 24)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_stateGate)
        {
            if (_filePath is null)
                return [];
            if (_sparsePagesCache is not null && _sparseCachedMinLetters == minLetters)
                return _sparsePagesCache;
        }

        string path;
        int pageCount;
        lock (_stateGate)
        {
            if (_filePath is null)
                return [];
            path = _filePath;
            pageCount = _pageCount;
        }

        var sparse = new ConcurrentBag<int>();
        var dop = Math.Clamp(Environment.ProcessorCount / 2, 2, 4);
        using var localReaders = new ThreadLocal<IDocReader>(
            () =>
            {
                lock (PdfiumLock)
                    return DocLib.Instance.GetDocReader(path, new PageDimensions(MetaScale));
            },
            trackAllValues: true);

        try
        {
            Parallel.For(0, pageCount, new ParallelOptions { MaxDegreeOfParallelism = dop }, page =>
            {
                var reader = localReaders.Value!;
                string text;
                lock (PdfiumLock)
                {
                    using var pageReader = reader.GetPageReader(page);
                    text = pageReader.GetText() ?? string.Empty;
                }
                var letters = 0;
                foreach (var ch in text)
                {
                    if (char.IsLetterOrDigit(ch))
                        letters++;
                }

                if (letters < minLetters)
                    sparse.Add(page);
            });
        }
        finally
        {
            lock (PdfiumLock)
            {
                foreach (var reader in localReaders.Values)
                    SafeDisposeDocReader(reader);
            }
        }

        var result = sparse.OrderBy(p => p).ToList();
        lock (_stateGate)
        {
            _sparsePagesCache = result;
            _sparseCachedMinLetters = minLetters;
        }

        return result;
    }

    public void Prefetch(int pageIndex, double scale, double dpi = 96)
    {
        if (!IsOpen) return;
        int count;
        lock (_stateGate) count = _pageCount;
        if (pageIndex < 0 || pageIndex >= count) return;

        scale = ClampRenderScale(scale);
        if (TryGetCached(pageIndex, scale, out _))
            return;

        try
        {
            RenderPage(pageIndex, scale, useCache: true, dpi);
        }
        catch
        {
            // Prefetch must never surface errors.
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        Close();
        _disposed = true;
    }

    private void EnsureRenderReader_NoLock(string path, double scale)
    {
        if (_renderReader is not null && AlmostEqual(_renderScale, scale))
            return;

        var key = ToZoomKey(scale);
        if (_renderReadersByZoom.TryGetValue(key, out var pooled))
        {
            _renderReader = pooled;
            _renderScale = scale;
            return;
        }

        // Uniform scale — correct for every page size/orientation (no dimOne/dimTwo box).
        // Keep previous readers alive; disposing them mid-session crashes native pdfium.
        var reader = DocLib.Instance.GetDocReader(path, new PageDimensions(scale));
        _renderReadersByZoom[key] = reader;
        _renderReader = reader;
        _renderScale = scale;
    }

    private void EnsureMetaReader_NoLock(string path)
    {
        if (_metaReader is not null)
            return;

        _metaReader = DocLib.Instance.GetDocReader(path, new PageDimensions(MetaScale));
    }

    private void EnsureThumbReader_NoLock(string path)
    {
        if (_thumbReader is not null)
            return;

        _thumbReader = DocLib.Instance.GetDocReader(path, new PageDimensions(ThumbScale));
    }

    private Size GetOrLoadPageSize_NoLock(int pageIndex)
    {
        Size[] sizes;
        string path;
        lock (_stateGate)
        {
            if (_filePath is null || _pageSizes is null)
                throw new InvalidOperationException("Документ не открыт.");
            sizes = _pageSizes;
            path = _filePath;
        }

        var cached = sizes[pageIndex];
        if (cached.Width > 0 && cached.Height > 0)
            return cached;

        EnsureMetaReader_NoLock(path);
        using var pageReader = _metaReader!.GetPageReader(pageIndex);
        var size = new Size(pageReader.GetPageWidth(), pageReader.GetPageHeight());
        sizes[pageIndex] = size;
        return size;
    }

    private void DisposeAllReaders()
    {
        lock (PdfiumLock)
        {
            // Do NOT call IDocReader.Dispose().
            // After multi-scale use (print preview / F11 fit), Docnet native-crashes in
            // FPDFDOC_ExitFormFillEnvironment — AccessViolation cannot be caught in .NET.
            // Drop managed refs only; OS reclaims native memory on process exit.
            // Mid-session we already pool render readers for the same reason.
            _renderReadersByZoom.Clear();
            _renderReader = null;
            _renderScale = 0;
            _metaReader = null;
            _thumbReader = null;
        }
    }

    /// <summary>
    /// Best-effort dispose for short-lived search DocReaders only.
    /// Never use for the long-lived view/thumb/meta readers (see DisposeAllReaders).
    /// </summary>
    private static void SafeDisposeDocReader(IDocReader? reader)
    {
        if (reader is null) return;
        try
        {
            reader.Dispose();
        }
        catch
        {
            // ignore managed dispose failures
        }
    }

    private void EnsureAlive(string path, long generation)
    {
        lock (_stateGate)
        {
            if (_filePath is null ||
                _openGeneration != generation ||
                !string.Equals(_filePath, path, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Документ был закрыт.");
            }
        }
    }

    private void StoreCache(int pageIndex, int zoomKey, BitmapSource bitmap)
    {
        var entry = new CacheEntry(bitmap, Interlocked.Increment(ref _accessClock));
        _cache[(pageIndex, zoomKey)] = entry;
        TrimCache(pageIndex, zoomKey);
    }

    private void TrimCache(int keepPage, int keepZoomKey)
    {
        if (_cache.Count <= MaxCachedPages)
            return;

        foreach (var key in _cache.Keys)
        {
            if (key.ZoomKey != keepZoomKey)
                _cache.TryRemove(key, out _);
        }

        while (_cache.Count > MaxCachedPages)
        {
            var hasVictim = false;
            var victim = default((int Page, int ZoomKey));
            var oldest = long.MaxValue;

            foreach (var pair in _cache)
            {
                // Keep a small window around the current page.
                if (Math.Abs(pair.Key.Page - keepPage) <= 1)
                    continue;

                if (pair.Value.LastAccess < oldest)
                {
                    oldest = pair.Value.LastAccess;
                    victim = pair.Key;
                    hasVictim = true;
                }
            }

            if (!hasVictim)
            {
                using var enumerator = _cache.Keys.GetEnumerator();
                if (!enumerator.MoveNext())
                    break;
                victim = enumerator.Current;
            }

            _cache.TryRemove(victim, out _);
        }
    }

    private static string BuildHaystack(List<Character> characters)
    {
        var sb = new StringBuilder(characters.Count);
        foreach (var ch in characters)
            sb.Append(ch.Char);
        return sb.ToString();
    }

    private static BitmapSource CreateFrozenBitmap(byte[] rawBytes, int width, int height, double dpiX, double dpiY)
    {
        var wb = new WriteableBitmap(width, height, dpiX, dpiY, PixelFormats.Bgra32, null);
        wb.WritePixels(new Int32Rect(0, 0, width, height), rawBytes, width * 4, 0);
        wb.Freeze();
        return wb;
    }

    private static double ClampRenderScale(double scale) => Math.Clamp(scale, 0.15, 12.0);
    private static int ToZoomKey(double scale) => (int)Math.Round(scale * 100);
    private static bool AlmostEqual(double a, double b) => Math.Abs(a - b) < 0.001;

    private sealed class CacheEntry
    {
        public CacheEntry(BitmapSource bitmap, long lastAccess)
        {
            Bitmap = bitmap;
            LastAccess = lastAccess;
        }

        public BitmapSource Bitmap { get; }
        public long LastAccess;
    }
}
