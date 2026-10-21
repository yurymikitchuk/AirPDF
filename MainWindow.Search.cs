using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using AirPDF.Services;
using Docnet.Core.Models;
using Microsoft.Win32;

namespace AirPDF;

public partial class MainWindow
{
    private async Task RunSearchAsync(string query, bool jumpToFirst)
    {
        if (!_renderer.IsOpen)
            return;

        query = query.Trim();
        if (query.Length == 0)
        {
            ClearSearch();
            return;
        }

        if (string.Equals(query, _lastQuery, StringComparison.Ordinal) && _searchHits.Count > 0)
        {
            if (jumpToFirst)
                await GoToSearchIndexAsync(0);
            return;
        }

        _searchCts?.Cancel();
        _searchCts = new CancellationTokenSource();
        var token = _searchCts.Token;
        var pageCount = _renderer.PageCount;
        var showBanner = pageCount >= 15;

        try
        {
            SearchStatusText.Text = "…";
            if (showBanner)
            {
                SearchBanner.Visibility = Visibility.Visible;
                SearchProgressText.Text = $"Текстовый слой: 0 / {pageCount}…";
            }

            var progress = new Progress<(int Page, int Total)>(p =>
            {
                SearchStatusText.Text = $"{p.Page}/{p.Total}";
                if (showBanner)
                    SearchProgressText.Text = $"Текстовый слой: {p.Page} / {p.Total}…";
            });

            var hits = await Task.Run(() => _renderer.Search(query, token, progress), token);
            if (token.IsCancellationRequested)
                return;

            // No text hits → OCR only pages with little/no extractable text (not the whole book).
            if (hits.Count == 0)
            {
                var sparse = await Task.Run(() => _renderer.FindTextSparsePages(), token);
                if (token.IsCancellationRequested)
                    return;

                if (sparse.Count > 0)
                {
                    SearchStatusText.Text = "…";
                    if (showBanner)
                        SearchProgressText.Text = $"OCR: 0 / {sparse.Count}…";
                    hits = await SearchWithOcrAsync(query, sparse, token, showBanner);
                    if (token.IsCancellationRequested)
                        return;
                }
            }

            _lastQuery = query;
            _searchHits = hits;
            _searchIndex = hits.Count > 0 ? 0 : -1;
            UpdateSearchUi();

            if (hits.Count == 0)
            {
                DrawHighlightsForCurrentPage();
                SearchStatusText.Text = "0";
                return;
            }

            if (jumpToFirst)
                await GoToSearchIndexAsync(0);
            else
                DrawHighlightsForCurrentPage();
        }
        catch (OperationCanceledException)
        {
            if (showBanner)
                SearchProgressText.Text = "Поиск отменён.";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Поиск", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            SearchBanner.Visibility = Visibility.Collapsed;
            SearchProgressText.Text = "Подготовка…";
        }
    }

    private async Task<List<PdfSearchHit>> SearchWithOcrAsync(
        string query,
        IReadOnlyList<int> pages,
        CancellationToken token,
        bool showBanner)
    {
        var hits = new List<PdfSearchHit>();
        var needle = query.Trim();
        if (needle.Length == 0 || !_renderer.IsOpen || pages.Count == 0)
            return hits;

        var language = AppSettingsStore.Load().OcrLanguage;
        const double ocrScale = 1.5;
        var total = pages.Count;

        for (var i = 0; i < pages.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            var page = pages[i];
            SearchStatusText.Text = $"{i + 1}/{total}";
            if (showBanner)
                SearchProgressText.Text = $"OCR: {i + 1} / {total}…";

            byte[] bgra;
            int width;
            int height;
            Size native;
            try
            {
                (bgra, width, height) = await Task.Run(() => _renderer.RenderPageRaw(page, ocrScale), token);
                native = _renderer.GetNativePageSize(page);
            }
            catch
            {
                continue;
            }

            if (width <= 0 || height <= 0 || bgra.Length == 0)
                continue;

            var ocr = await DocumentOcrService.RecognizePageAsync(bgra, width, height, language, token);
            if (ocr.Lines.Count == 0 && string.IsNullOrWhiteSpace(ocr.Text))
                continue;

            var scaleX = native.Width / width;
            var scaleY = native.Height / height;

            foreach (var line in ocr.Lines)
            {
                token.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(line.Text))
                    continue;

                var text = line.Text;
                var start = 0;
                while (start <= text.Length - needle.Length)
                {
                    var index = text.IndexOf(needle, start, StringComparison.OrdinalIgnoreCase);
                    if (index < 0)
                        break;

                    var frac0 = text.Length == 0 ? 0 : (double)index / text.Length;
                    var frac1 = text.Length == 0 ? 1 : (double)(index + needle.Length) / text.Length;
                    var left = (line.Left + line.Width * frac0) * scaleX;
                    var top = line.Top * scaleY;
                    var widthHit = Math.Max(2, line.Width * (frac1 - frac0) * scaleX);
                    var heightHit = Math.Max(2, line.Height * scaleY);
                    hits.Add(new PdfSearchHit(page, new Rect(left, top, widthHit, heightHit)));

                    start = index + Math.Max(1, needle.Length);
                }
            }
        }

        return hits;
    }

    private async Task MoveSearchAsync(int delta)
    {
        if (_searchHits.Count == 0)
        {
            await RunSearchAsync(SearchBox.Text, jumpToFirst: true);
            return;
        }

        var next = (_searchIndex + delta) % _searchHits.Count;
        if (next < 0)
            next += _searchHits.Count;
        await GoToSearchIndexAsync(next);
    }

    private async Task GoToSearchIndexAsync(int index)
    {
        if (index < 0 || index >= _searchHits.Count)
            return;

        _searchIndex = index;
        UpdateSearchUi();

        var hit = _searchHits[index];
        if (hit.PageIndex != _pageIndex)
            await GoToPageAsync(hit.PageIndex, immediate: true);
        else
            DrawHighlightsForCurrentPage();

        ScrollToHit(hit);
    }

    private void ScrollToHit(PdfSearchHit hit)
    {
        ViewerScroll.UpdateLayout();
        var scale = _renderedZoom * _previewZoom.ScaleY;
        var y = hit.Bounds.Top * scale;
        var target = Math.Max(0, y - 40);
        ViewerScroll.ScrollToVerticalOffset(target);
    }

    private void DrawHighlightsForCurrentPage()
    {
        HighlightLayer.Children.Clear();
        if (_searchHits.Count == 0 || PageImage.Source is not BitmapSource)
            return;

        var scale = _renderedZoom;
        for (var i = 0; i < _searchHits.Count; i++)
        {
            var hit = _searchHits[i];
            if (hit.PageIndex != _pageIndex)
                continue;

            var rect = new Rectangle
            {
                Width = Math.Max(2, hit.Bounds.Width * scale),
                Height = Math.Max(2, hit.Bounds.Height * scale),
                Fill = i == _searchIndex
                    ? (Brush)FindResource("HighlightActive")
                    : (Brush)FindResource("HighlightFill"),
                IsHitTestVisible = false
            };

            Canvas.SetLeft(rect, hit.Bounds.Left * scale);
            Canvas.SetTop(rect, hit.Bounds.Top * scale);
            HighlightLayer.Children.Add(rect);
        }
    }

    private void UpdateHighlightLayerSize()
    {
        if (PageImage.Source is BitmapSource bmp)
        {
            // Use DIP size (DPI-aware), not PixelWidth — otherwise oversampled /
            // high-DPI bitmaps make the white PageFrame larger than the image.
            HighlightLayer.Width = bmp.Width;
            HighlightLayer.Height = bmp.Height;
            SelectionLayer.Width = bmp.Width;
            SelectionLayer.Height = bmp.Height;
        }
    }

    private void ClearSearch()
    {
        _searchCts?.Cancel();
        _searchHits = [];
        _searchIndex = -1;
        _lastQuery = "";
        SearchStatusText.Text = "";
        HighlightLayer.Children.Clear();
        UpdateSearchUi();
    }

    private void UpdateSearchUi()
    {
        var hasQuery = _renderer.IsOpen && !string.IsNullOrWhiteSpace(SearchBox.Text);
        var hasHits = _searchHits.Count > 0;
        // Keep nav buttons clickable while typing so they can run/retry search.
        SearchPrevButton.IsEnabled = hasQuery;
        SearchNextButton.IsEnabled = hasQuery;
        if (hasHits)
            SearchStatusText.Text = $"{_searchIndex + 1}/{_searchHits.Count}";
        else if (!hasQuery)
            SearchStatusText.Text = "";
        else if (SearchStatusText.Text is "…" || SearchStatusText.Text.Contains('/'))
        {
            // keep in-progress page counter
        }
        else
            SearchStatusText.Text = "0";
    }
}
