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
    private async Task FitToViewportAsync(PageFitMode mode)
    {
        if (!_renderer.IsOpen) return;

        try
        {
            var zoom = ComputeFitZoom(mode);
            if (zoom is null) return;
            await SetZoomAsync(zoom.Value);
            AppSettingsStore.Update(s =>
            {
                s.LastFitMode = mode.ToString();
                s.LastZoom = _zoom;
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Не удалось подогнать страницу", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private double? ComputeFitZoom(PageFitMode mode)
    {
        if (!_renderer.IsOpen) return null;

        ViewerScroll.UpdateLayout();
        var pad = ViewerScroll.Padding;
        var border = PageFrame.BorderThickness;
        var client = new Size(
            ViewerScroll.ActualWidth - pad.Left - pad.Right,
            ViewerScroll.ActualHeight - pad.Top - pad.Bottom);
        var native = _renderer.GetNativePageSize(_pageIndex);

        return ViewportFitCalculator.ComputeZoom(
            mode,
            client,
            border,
            native,
            SystemParameters.VerticalScrollBarWidth,
            SystemParameters.HorizontalScrollBarHeight);
    }

    private async Task SetZoomAsync(double zoom)
    {
        if (!_renderer.IsOpen) return;

        _zoom = Math.Clamp(Math.Round(zoom, 2), 0.25, 4.0);
        UpdateZoomText();

        var preview = _zoom / Math.Max(_renderedZoom, 0.01);
        _previewZoom.ScaleX = preview;
        _previewZoom.ScaleY = preview;
        RenderOptions.SetBitmapScalingMode(PageImage, BitmapScalingMode.HighQuality);

        await RenderCurrentPageAsync(showLoading: false);
    }

    private async Task PrintDocumentAsync()
    {
        if (!_renderer.IsOpen) return;

        // Drain page/thumb/prefetch GetImage before print preview shares the same renderer.
        await PauseBackgroundRasterAsync();
        try
        {
            var options = new PrintOptionsWindow(_renderer, _pageIndex) { Owner = this };
            if (options.ShowDialog() != true || options.Result is null || options.Result.Pages.Count == 0)
                return;

            try
            {
                var settings = options.Result;
                var dialog = new PrintDialog();
                if (settings.Printer is not null)
                    dialog.PrintQueue = settings.Printer;

                var ticket = dialog.PrintTicket ?? new System.Printing.PrintTicket();
                var orientation = ResolvePrintOrientation(settings.Orientation, settings.Pages);
                ticket.PageOrientation = orientation;
                dialog.PrintTicket = ticket;

                var pageSize = new Size(dialog.PrintableAreaWidth, dialog.PrintableAreaHeight);
                pageSize = NormalizePrintPageSize(pageSize, orientation);

                var paginator = new PdfPrintPaginator(
                    _renderer,
                    pageSize,
                    settings.Pages,
                    settings.ScaleMode);
                var name = System.IO.Path.GetFileNameWithoutExtension(_renderer.FilePath) ?? "AirPDF";
                dialog.PrintDocument(paginator, name);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "\u041e\u0448\u0438\u0431\u043a\u0430 \u043f\u0435\u0447\u0430\u0442\u0438", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        finally
        {
            ResumeBackgroundRaster();
        }
    }

    /// <summary>Cancel prefetch/thumbs and wait until PDFium is idle (option A).</summary>
    private async Task PauseBackgroundRasterAsync()
    {
        _backgroundRasterPaused = true;
        _prefetchCts?.Cancel();
        _thumbCts?.Cancel();
        await Task.Run(() => _renderer.WaitForRasterIdle(2000));
    }

    private void ResumeBackgroundRaster()
    {
        _backgroundRasterPaused = false;
        if (!_renderer.IsOpen)
            return;

        if (_wantPage != _shownPage ||
            double.IsNaN(_shownZoom) ||
            PageImage.Source is null ||
            Math.Abs(_zoom - _shownZoom) >= 0.001)
        {
            _ = EnsurePagePumpAsync();
        }

        if (_sidebarOpen)
            ScheduleThumbnailLoad();
    }

    private System.Printing.PageOrientation ResolvePrintOrientation(
        PrintOrientationMode mode,
        IReadOnlyList<int> pages)
    {
        if (mode == PrintOrientationMode.Portrait)
            return System.Printing.PageOrientation.Portrait;
        if (mode == PrintOrientationMode.Landscape)
            return System.Printing.PageOrientation.Landscape;

        try
        {
            var page = pages.Count > 0 ? pages[0] : _pageIndex;
            var size = _renderer.GetNativePageSize(page);
            return size.Width > size.Height
                ? System.Printing.PageOrientation.Landscape
                : System.Printing.PageOrientation.Portrait;
        }
        catch
        {
            return System.Printing.PageOrientation.Portrait;
        }
    }

    private static Size NormalizePrintPageSize(Size printable, System.Printing.PageOrientation orientation)
    {
        var landscape = orientation == System.Printing.PageOrientation.Landscape;
        if (landscape && printable.Width < printable.Height)
            return new Size(printable.Height, printable.Width);
        if (!landscape && printable.Width > printable.Height)
            return new Size(printable.Height, printable.Width);
        return printable;
    }

    private async Task ExportToWordAsync()
    {
        if (!_renderer.IsOpen || _isExporting) return;

        var suggestedName = System.IO.Path.GetFileNameWithoutExtension(_renderer.FilePath) ?? "AirPDF";
        var suggestedDir = System.IO.Path.GetDirectoryName(_renderer.FilePath) ?? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var suggestedPath = System.IO.Path.Combine(suggestedDir, suggestedName + ".docx");

        var optionsWindow = new ExportOptionsWindow(suggestedPath, _renderer.PageCount, _pageIndex)
        {
            Owner = this
        };
        if (optionsWindow.ShowDialog() != true || optionsWindow.Result is null)
            return;

        var options = optionsWindow.Result;
        _isExporting = true;
        _exportCts = new CancellationTokenSource();
        ExportBanner.Visibility = Visibility.Visible;
        ExportStatusText.Text = "Подготовка…";
        UpdateNavState();

        PdfRenderer? exportRenderer = null;
        var progress = new Progress<(int Page, int Total, string Status)>(p =>
        {
            ExportStatusText.Text = p.Status;
        });

        try
        {
            exportRenderer = _renderer.CreateIsolatedCopy();
            var exporter = new WordExportService(exportRenderer);
            await exporter.ExportAsync(options, progress, _exportCts.Token);

            MessageBox.Show(
                this,
                "Документ Word сохранён.\n" + options.OutputPath,
                "Экспорт в Microsoft Word",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (OperationCanceledException)
        {
            ExportStatusText.Text = "Экспорт отменён.";
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                this,
                ex.Message,
                "Ошибка экспорта",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        finally
        {
            exportRenderer?.Dispose();
            _exportCts?.Dispose();
            _exportCts = null;
            _isExporting = false;
            ExportBanner.Visibility = Visibility.Collapsed;
            ExportStatusText.Text = "Подготовка…";
            UpdateNavState();
        }
    }

    private void CancelExport_Click(object sender, RoutedEventArgs e) => _exportCts?.Cancel();

    private void CancelSearch_Click(object sender, RoutedEventArgs e) => _searchCts?.Cancel();

    private void ChangePage(int delta)
    {
        if (!_renderer.IsOpen) return;
        var next = _pageIndex + delta;
        if (next < 0 || next >= _renderer.PageCount) return;
        RequestPage(next);
    }

    private Task ChangePageAsync(int delta)
    {
        ChangePage(delta);
        return Task.CompletedTask;
    }

    private Task GoToFirstPageAsync()
    {
        if (_renderer.IsOpen)
            RequestPage(0);
        return Task.CompletedTask;
    }

    private Task GoToLastPageAsync()
    {
        if (_renderer.IsOpen)
            RequestPage(_renderer.PageCount - 1);
        return Task.CompletedTask;
    }

    private Task GoToPageAsync(int pageIndex, bool immediate = false)
    {
        _ = immediate;
        RequestPage(pageIndex);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Updates the page number immediately and renders via a single pump that always
    /// targets the latest requested page (skips intermediate pages instead of queuing them).
    /// </summary>
    private void RequestPage(int pageIndex)
    {
        if (!_renderer.IsOpen) return;
        if (pageIndex < 0 || pageIndex >= _renderer.PageCount) return;

        _pageIndex = pageIndex;
        _wantPage = pageIndex;
        UpdateNavState();
        SyncThumbnailSelection();
        ViewerScroll.ScrollToVerticalOffset(0);
        _prefetchCts?.Cancel();

        var renderScale = ToRenderScale(_zoom);
        if (_renderer.TryGetCached(pageIndex, renderScale, out var cached))
            ApplyPageBitmap(cached, pageIndex, _zoom);

        if (!_backgroundRasterPaused)
            _ = EnsurePagePumpAsync();
    }

    private void ApplyPageBitmap(BitmapSource bitmap, int page, double logicalZoom)
    {
        var pageChanged = page != _shownPage;
        _renderedZoom = logicalZoom;
        _shownPage = page;
        _shownZoom = logicalZoom;
        ResetPreviewTransform();
        PageImage.Source = bitmap;
        UpdateHighlightLayerSize();
        if (pageChanged)
        {
            ClearTextSelection();
            InvalidatePageLinks();
        }
        else
            DrawSelectionOverlay();
        DrawHighlightsForCurrentPage();
        EnsurePageLinks();
    }

    private double GetDpiScale()
    {
        try
        {
            return Math.Max(1.0, VisualTreeHelper.GetDpi(this).DpiScaleX);
        }
        catch
        {
            return 1.0;
        }
    }

    private double ToRenderScale(double logicalZoom) =>
        RenderScaleHelper.ToRenderScale(logicalZoom, GetDpiScale());

    private double GetBitmapDpi() => RenderScaleHelper.ToBitmapDpi(GetDpiScale());

    private Task EnsurePagePumpAsync()
    {
        lock (_pagePumpGate)
        {
            if (_pagePumpTask is { IsCompleted: false })
                return _pagePumpTask;

            _pagePumpTask = PumpPagesAsync();
            return _pagePumpTask;
        }
    }

    private async Task PumpPagesAsync()
    {
        try
        {
            while (_renderer.IsOpen && !_backgroundRasterPaused)
            {
                var page = _wantPage;
                var zoom = _zoom;

                if (page == _shownPage &&
                    PageImage.Source is not null &&
                    !double.IsNaN(_shownZoom) &&
                    Math.Abs(zoom - _shownZoom) < 0.001)
                {
                    break;
                }

                try
                {
                    var renderScale = ToRenderScale(zoom);
                    var dpi = GetBitmapDpi();
                    BitmapSource bitmap;
                    if (_renderer.TryGetCached(page, renderScale, out var cached))
                    {
                        bitmap = cached;
                    }
                    else
                    {
                        // Single full-quality pass (no low-res preview stage).
                        bitmap = await Task.Run(() => _renderer.RenderPage(page, renderScale, useCache: true, dpi));
                    }

                    if (!_renderer.IsOpen || _backgroundRasterPaused)
                        break;
                    if (page != _wantPage || Math.Abs(zoom - _zoom) >= 0.001)
                        continue;

                    ApplyPageBitmap(bitmap, page, zoom);
                    PrefetchNeighbors();
                }
                catch (Exception ex)
                {
                    if (_renderer.IsOpen && !_backgroundRasterPaused && page == _wantPage)
                    {
                        MessageBox.Show(
                            this,
                            ex.Message,
                            "\u041e\u0448\u0438\u0431\u043a\u0430 \u043e\u0442\u0440\u0438\u0441\u043e\u0432\u043a\u0438",
                            MessageBoxButton.OK,
                            MessageBoxImage.Warning);
                    }

                    break;
                }

                if (page == _wantPage && Math.Abs(zoom - _zoom) < 0.001)
                    break;
            }
        }
        finally
        {
            lock (_pagePumpGate)
                _pagePumpTask = null;

            // A RequestPage may have arrived while we were leaving — restart if still behind.
            if (!_backgroundRasterPaused &&
                _renderer.IsOpen &&
                (_wantPage != _shownPage ||
                 double.IsNaN(_shownZoom) ||
                 Math.Abs(_zoom - _shownZoom) >= 0.001))
            {
                _ = EnsurePagePumpAsync();
            }
        }
    }

    private void ChangeZoom(double delta)
    {
        if (!_renderer.IsOpen) return;

        _zoom = Math.Clamp(Math.Round(_zoom + delta, 2), 0.4, 3.5);
        UpdateZoomText();

        var preview = _zoom / Math.Max(_renderedZoom, 0.01);
        _previewZoom.ScaleX = preview;
        _previewZoom.ScaleY = preview;
        // HighQuality while waiting for a re-render — sharper than LowQuality stretch.
        RenderOptions.SetBitmapScalingMode(PageImage, BitmapScalingMode.HighQuality);

        _zoomDebounce.Stop();
        _zoomDebounce.Start();
    }

    private void ResetPreviewTransform()
    {
        _previewZoom.ScaleX = 1;
        _previewZoom.ScaleY = 1;
        // Oversampled bitmap → Fant downsample (crisper than Nearest at fractional ratios).
        RenderOptions.SetBitmapScalingMode(PageImage, BitmapScalingMode.HighQuality);
    }

    private async Task RenderCurrentPageAsync(bool showLoading)
    {
        if (!_renderer.IsOpen) return;

        _wantPage = _pageIndex;
        if (showLoading)
            BeginLoading();

        try
        {
            await EnsurePagePumpAsync();
        }
        finally
        {
            if (showLoading)
                EndLoading();
        }
    }

    private void PrefetchNeighbors()
    {
        if (_backgroundRasterPaused)
            return;

        _prefetchCts?.Cancel();
        _prefetchCts = new CancellationTokenSource();
        var token = _prefetchCts.Token;
        var page = _pageIndex;
        var renderScale = ToRenderScale(_zoom);
        var dpi = GetBitmapDpi();
        var count = _renderer.PageCount;
        var want = _wantPage;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(180, token);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            // Still on the same page — safe to warm neighbors.
            if (want != _wantPage || _backgroundRasterPaused)
                return;

            foreach (var offset in (int[])[1, -1, 2])
            {
                if (token.IsCancellationRequested || want != _wantPage || _backgroundRasterPaused)
                    return;

                var target = page + offset;
                if (target < 0 || target >= count)
                    continue;

                try
                {
                    _renderer.Prefetch(target, renderScale, dpi);
                }
                catch
                {
                    // Prefetch must never surface errors.
                }
            }
        }, token);
    }
}
