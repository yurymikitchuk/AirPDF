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
    private void SidebarToggle_Click(object sender, RoutedEventArgs e) => ToggleSidebar();

    private void ToggleSidebar()
    {
        if (_placement.IsFullscreen || !_renderer.IsOpen)
            return;

        _sidebarPreferredOpen = !_sidebarPreferredOpen;
        ApplySidebarVisibility(_sidebarPreferredOpen, persist: true);
        if (_sidebarOpen)
            ScheduleThumbnailLoad();
    }

    private void RestoreSidebarFromSettings()
    {
        var settings = AppSettingsStore.Load();
        _sidebarWidth = Math.Clamp(settings.SidebarWidth, 160, 360);
        _sidebarPreferredOpen = settings.SidebarOpen;
        // No document yet — keep the nav panel hidden until a file is opened.
        ApplySidebarVisibility(false, persist: false);

        if (string.Equals(settings.SidebarTab, "Outline", StringComparison.OrdinalIgnoreCase))
            SidebarTabs.SelectedItem = OutlineTab;
        else
            SidebarTabs.SelectedItem = PagesTab;
    }

    private void RestoreSidebarForOpenDocument()
    {
        if (_placement.IsFullscreen || !_renderer.IsOpen)
            return;

        ApplySidebarVisibility(_sidebarPreferredOpen, persist: false);
        if (_sidebarOpen)
            ScheduleThumbnailLoad();
    }

    private void HideSidebarWhileNoDocument()
    {
        ApplySidebarVisibility(false, persist: false);
    }

    private void PersistSidebarSettings()
    {
        var tab = ReferenceEquals(SidebarTabs.SelectedItem, OutlineTab) ? "Outline" : "Pages";
        if (_sidebarOpen && SidebarColumn.ActualWidth > 80)
            _sidebarWidth = SidebarColumn.ActualWidth;

        AppSettingsStore.Update(s =>
        {
            s.SidebarOpen = _sidebarPreferredOpen;
            s.SidebarWidth = _sidebarWidth;
            s.SidebarTab = tab;
        });
    }

    private void ApplySidebarVisibility(bool open, bool persist)
    {
        if (open && !_renderer.IsOpen)
            open = false;

        _sidebarOpen = open;
        if (open)
        {
            SidebarColumn.MinWidth = 160;
            SidebarColumn.Width = new GridLength(_sidebarWidth);
            SidebarSplitterColumn.Width = new GridLength(4);
            SidebarPanel.Visibility = Visibility.Visible;
            SidebarSplitter.Visibility = Visibility.Visible;
        }
        else
        {
            if (SidebarColumn.ActualWidth > 80)
                _sidebarWidth = SidebarColumn.ActualWidth;
            SidebarColumn.MinWidth = 0;
            SidebarColumn.Width = new GridLength(0);
            SidebarSplitterColumn.Width = new GridLength(0);
            SidebarPanel.Visibility = Visibility.Collapsed;
            SidebarSplitter.Visibility = Visibility.Collapsed;
        }

        if (persist)
            PersistSidebarSettings();
    }

    private void SidebarSplitter_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        if (SidebarColumn.ActualWidth > 80)
            _sidebarWidth = Math.Clamp(SidebarColumn.ActualWidth, 160, 360);
        PersistSidebarSettings();
    }

    private void SidebarTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        PersistSidebarSettings();
        if (ReferenceEquals(SidebarTabs.SelectedItem, PagesTab) && _renderer.IsOpen)
            ScheduleThumbnailLoad();
    }

    private void ClearSidebarContent()
    {
        _thumbCts?.Cancel();
        _thumbnails.Clear();
        OutlineTree.ItemsSource = null;
        OutlineEmptyText.Visibility = Visibility.Collapsed;
    }

    private void RebuildSidebarForDocument(string path)
    {
        _thumbCts?.Cancel();
        _thumbnails.Clear();

        var count = _renderer.PageCount;
        for (var i = 0; i < count; i++)
            _thumbnails.Add(new ThumbnailItem(i));

        SyncThumbnailSelection();
        ScheduleThumbnailLoad();

        try
        {
            var outline = PdfOutlineReader.Load(path);
            OutlineTree.ItemsSource = outline;
            var hasOutline = outline.Count > 0;
            OutlineTree.Visibility = hasOutline ? Visibility.Visible : Visibility.Collapsed;
            OutlineEmptyText.Visibility = hasOutline ? Visibility.Collapsed : Visibility.Visible;
            if (hasOutline)
                ExpandOutlineRoot(outline);
        }
        catch
        {
            OutlineTree.ItemsSource = null;
            OutlineTree.Visibility = Visibility.Collapsed;
            OutlineEmptyText.Visibility = Visibility.Visible;
        }
    }

    private void ExpandOutlineRoot(IReadOnlyList<PdfOutlineNode> roots)
    {
        // Expand first level after containers are generated.
        Dispatcher.BeginInvoke(() =>
        {
            foreach (var item in OutlineTree.Items)
            {
                if (OutlineTree.ItemContainerGenerator.ContainerFromItem(item) is TreeViewItem tvi)
                    tvi.IsExpanded = true;
            }
        }, DispatcherPriority.Loaded);
    }

    private void SyncThumbnailSelection()
    {
        if (_thumbnails.Count == 0)
            return;

        _syncingThumbnailSelection = true;
        try
        {
            foreach (var item in _thumbnails)
                item.IsCurrent = item.PageIndex == _pageIndex;

            if (_pageIndex >= 0 && _pageIndex < _thumbnails.Count)
            {
                var current = _thumbnails[_pageIndex];
                ThumbnailList.SelectedItem = current;
                ThumbnailList.ScrollIntoView(current);
            }
        }
        finally
        {
            _syncingThumbnailSelection = false;
        }

        ScheduleThumbnailLoad();
    }

    private void ThumbnailList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingThumbnailSelection || !_renderer.IsOpen)
            return;
        if (ThumbnailList.SelectedItem is not ThumbnailItem item)
            return;
        if (item.PageIndex == _pageIndex)
            return;

        RequestPage(item.PageIndex);
    }

    private void ThumbnailList_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.VerticalChange == 0 && e.ExtentHeightChange == 0)
            return;
        ScheduleThumbnailLoad();
    }

    private void ScheduleThumbnailLoad()
    {
        if (!_renderer.IsOpen || !_sidebarOpen || _backgroundRasterPaused || _thumbnails.Count == 0)
            return;

        _thumbCts?.Cancel();
        _thumbCts = new CancellationTokenSource();
        var token = _thumbCts.Token;
        _ = LoadThumbnailsAsync(token);
    }

    private async Task LoadThumbnailsAsync(CancellationToken token)
    {
        try
        {
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
            if (token.IsCancellationRequested || !_renderer.IsOpen)
                return;

            var priorities = GetThumbnailLoadOrder();
            foreach (var page in priorities)
            {
                if (token.IsCancellationRequested || !_renderer.IsOpen || _backgroundRasterPaused)
                    return;

                if (page < 0 || page >= _thumbnails.Count)
                    continue;
                if (_thumbnails[page].Image is not null)
                    continue;

                BitmapSource? bmp = null;
                try
                {
                    bmp = await Task.Run(() => _renderer.RenderThumbnail(page), token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch
                {
                    continue;
                }

                if (token.IsCancellationRequested || !_renderer.IsOpen)
                    return;

                var target = page;
                var image = bmp;
                await Dispatcher.InvokeAsync(() =>
                {
                    if (!_renderer.IsOpen || target >= _thumbnails.Count)
                        return;
                    _thumbnails[target].Image = image;
                });
            }
        }
        catch (OperationCanceledException)
        {
            // superseded
        }
    }

    private List<int> GetThumbnailLoadOrder()
    {
        double offset = 0;
        double viewport = 1;
        if (FindVisualChild<ScrollViewer>(ThumbnailList) is { } sv)
        {
            offset = sv.VerticalOffset;
            viewport = sv.ViewportHeight;
        }

        return ThumbnailLoadPlanner.Plan(
            _thumbnails.Count,
            _pageIndex,
            offset,
            viewport);
    }

    private void OutlineTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (!_renderer.IsOpen)
            return;
        if (e.NewValue is not PdfOutlineNode node)
            return;
        if (node.PageIndex < 0 || node.PageIndex >= _renderer.PageCount)
            return;

        RequestPage(node.PageIndex);
    }
}
