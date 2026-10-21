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

public partial class MainWindow : Window
{
    private readonly PdfRenderer _renderer = new();
    private readonly ScaleTransform _previewZoom = new(1, 1);
    private readonly RotateTransform _pageRotate = new(0);
    private readonly TransformGroup _pageTransform = new();
    private readonly DispatcherTimer _zoomDebounce;
    private readonly DispatcherTimer _loadingDelay;
    private readonly DispatcherTimer _searchDebounce;

    private int _pageIndex;
    private int _wantPage;
    private int _shownPage = int.MinValue;
    private double _shownZoom = double.NaN;
    private double _zoom = 1.15;
    private double _renderedZoom = 1.15;
    private int _renderVersion;
    private CancellationTokenSource? _renderCts;
    private CancellationTokenSource? _searchCts;
    private CancellationTokenSource? _exportCts;
    private CancellationTokenSource? _prefetchCts;
    private readonly object _pagePumpGate = new();
    private Task? _pagePumpTask;
    private bool _loadingArmed;
    private bool _isExporting;

    private List<PdfSearchHit> _searchHits = [];
    private int _searchIndex = -1;
    private string _lastQuery = "";
    private readonly WindowPlacementController _placement;
    private Point? _toolbarDragOrigin;
    private bool _fullscreenBusy;
    /// <summary>Stops pump/prefetch/thumbs from starting new PDFium work (Print / F11).</summary>
    private bool _backgroundRasterPaused;
    private Visibility _toolbarPrevVisibility = Visibility.Visible;
    private int _fullscreenRefitVersion;

    private bool _isSelecting;
    private Point _selAnchorNative;
    private Rect? _selectionNative;
    private bool _selectionIsAll;
    private string _selectedText = "";
    private List<Character>? _pageCharacters;
    private int _pageCharactersPage = int.MinValue;

    private IReadOnlyList<PdfPageLink> _pageLinks = [];
    private int _pageLinksPage = int.MinValue;
    private PdfPageLink? _pressedLink;
    private Point _pressedLinkOrigin;

    private readonly ObservableCollection<ThumbnailItem> _thumbnails = [];
    private CancellationTokenSource? _thumbCts;
    private bool _sidebarOpen;
    private bool _sidebarPreferredOpen;
    private double _sidebarWidth = 230;
    private bool _sidebarOpenBeforeFullscreen;
    private bool _syncingThumbnailSelection;

    public MainWindow()
    {
        InitializeComponent();
        _placement = new WindowPlacementController(this);
        _placement.Changed += (_, _) => UpdateMaximizeCaption();
        _pageTransform.Children.Add(_previewZoom);
        _pageTransform.Children.Add(_pageRotate);
        PageContent.LayoutTransform = _pageTransform;
        ThumbnailList.ItemsSource = _thumbnails;
        RestoreSidebarFromSettings();

        _zoomDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(70) };
        _zoomDebounce.Tick += async (_, _) =>
        {
            _zoomDebounce.Stop();
            await RenderCurrentPageAsync(showLoading: false);
        };

        _loadingDelay = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        _loadingDelay.Tick += (_, _) =>
        {
            _loadingDelay.Stop();
            if (_loadingArmed)
                LoadingOverlay.Visibility = Visibility.Visible;
        };

        _searchDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(280) };
        _searchDebounce.Tick += async (_, _) =>
        {
            _searchDebounce.Stop();
            await RunSearchAsync(SearchBox.Text, jumpToFirst: true);
        };

        UpdateSearchPlaceholder();
        UpdateMaximizeCaption();
        RefreshRecentFiles();

        Closed += (_, _) =>
        {
            _zoomDebounce.Stop();
            _loadingDelay.Stop();
            _searchDebounce.Stop();
            _renderCts?.Cancel();
            _searchCts?.Cancel();
            _exportCts?.Cancel();
            _prefetchCts?.Cancel();
            _thumbCts?.Cancel();
            PersistSidebarSettings();
            AppSettingsStore.Update(s => s.LastZoom = _zoom);
            _renderer.Dispose();
        };
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        FillToWorkArea();
        RefreshRecentFiles();
        UpdateToolbarCompactMode();

        if (!string.IsNullOrWhiteSpace(App.StartupFilePath))
            await OpenFileAsync(App.StartupFilePath);
    }

    private void Toolbar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Double-click must not toggle window size.
        if (e.ClickCount >= 2)
        {
            e.Handled = true;
            return;
        }

        if (e.OriginalSource is DependencyObject source && IsInteractive(source))
            return;

        if (_placement.IsFilled)
        {
            // Only restore+move after a real drag. A plain click must not shift the window.
            _toolbarDragOrigin = e.GetPosition(this);
            CaptureMouse();
            MouseMove += Toolbar_MouseMove;
            MouseLeftButtonUp += Toolbar_MouseLeftButtonUp;
            e.Handled = true;
            return;
        }

        try { DragMove(); } catch { /* ignore */ }
    }

    private void Toolbar_MouseMove(object sender, MouseEventArgs e)
    {
        if (_toolbarDragOrigin is null || e.LeftButton != MouseButtonState.Pressed)
            return;

        var current = e.GetPosition(this);
        var delta = current - _toolbarDragOrigin.Value;
        if (Math.Abs(delta.X) < 4 && Math.Abs(delta.Y) < 4)
            return;

        var screen = PointToScreen(current);
        EndToolbarDragTracking();
        RestoreFromWorkArea();
        Left = screen.X - Width / 2;
        Top = Math.Max(SystemParameters.WorkArea.Top, screen.Y - 20);

        try { DragMove(); } catch { /* ignore */ }
    }

    private void Toolbar_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        EndToolbarDragTracking();
    }

    private void EndToolbarDragTracking()
    {
        _toolbarDragOrigin = null;
        MouseMove -= Toolbar_MouseMove;
        MouseLeftButtonUp -= Toolbar_MouseLeftButtonUp;
        if (IsMouseCaptured)
            ReleaseMouseCapture();
    }

    private static bool IsInteractive(DependencyObject source)
    {
        while (source is not null)
        {
            if (source is Button or TextBox or ComboBox)
                return true;
            source = VisualTreeHelper.GetParent(source);
        }

        return false;
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Maximize_Click(object sender, RoutedEventArgs e) => ToggleMaximize();

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Exit_Click(object sender, RoutedEventArgs e) => Close();

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SettingsWindow { Owner = this };
        dialog.ShowDialog();
    }

    private void About_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new AboutWindow { Owner = this };
        dialog.ShowDialog();
    }

    private async void CloseDocument() => await CloseDocumentAsync();

    private async Task CloseDocumentAsync()
    {
        if (!_renderer.IsOpen)
            return;

        _renderVersion++;
        _wantPage = 0;
        _shownPage = int.MinValue;
        _shownZoom = double.NaN;
        _renderCts?.Cancel();
        _searchCts?.Cancel();
        _prefetchCts?.Cancel();
        _thumbCts?.Cancel();
        ClearSearch();
        SearchBox.Text = "";
        ClearPageView();
        ClearSidebarContent();

        // Drain in-flight GetImage before dropping DocReaders (same pause as Print/F11).
        await PauseBackgroundRasterAsync();
        try
        {
            _renderer.Close();
        }
        finally
        {
            _backgroundRasterPaused = false;
        }

        TesseractOcrService.Release();
        FileNameText.Text = "";
        Title = "AirPDF";
        _pageIndex = 0;
        ShowEmptyState();
        HideSidebarWhileNoDocument();
        UpdateNavState();
        EndLoading();
    }

    private void ClearPageView()
    {
        PageImage.Source = null;
        HighlightLayer.Children.Clear();
        HighlightLayer.Width = double.NaN;
        HighlightLayer.Height = double.NaN;
        ClearTextSelection();
        InvalidatePageLinks();
        ResetPreviewTransform();
        _pageRotate.Angle = 0;
        PageFrame.Cursor = Cursors.IBeam;
        PageFrame.Visibility = Visibility.Collapsed;
    }

    private void ToggleMaximize() => _placement.ToggleMaximize();

    private void FillToWorkArea() => _placement.FillToWorkArea();

    private void RestoreFromWorkArea() => _placement.RestoreFromWorkArea();

    private void UpdateMaximizeCaption() => _placement.UpdateMaximizeCaption(MaximizeButton);

    private async void Open_Click(object sender, RoutedEventArgs e) => await OpenWithDialogAsync();

    private void OpenRecentDrop_Click(object sender, RoutedEventArgs e)
    {
        RebuildOpenRecentMenu();
        OpenRecentMenu.PlacementTarget = OpenRecentDropButton;
        OpenRecentMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        OpenRecentMenu.IsOpen = true;
        e.Handled = true;
    }

    private async Task OpenWithDialogAsync()
    {
        var dialog = new OpenFileDialog
        {
            Filter = "PDF (*.pdf)|*.pdf",
            Title = "\u041e\u0442\u043a\u0440\u044b\u0442\u044c PDF"
        };

        if (dialog.ShowDialog(this) != true)
            return;

        await OpenFileAsync(dialog.FileName);
    }

    private async Task OpenFileAsync(string path)
    {
        var version = ++_renderVersion;
        _renderCts?.Cancel();
        _renderCts = new CancellationTokenSource();
        var token = _renderCts.Token;

        try
        {
            ClearSearch();
            ClearTextSelection();
            _pageRotate.Angle = 0;
            BeginLoading();
            EmptyState.Visibility = Visibility.Collapsed;
            PageFrame.Visibility = Visibility.Visible;
            FileNameText.Text = System.IO.Path.GetFileName(path);
            Title = System.IO.Path.GetFileName(path);

            await Task.Run(() =>
            {
                token.ThrowIfCancellationRequested();
                _renderer.Open(path);
            }, token);

            if (version != _renderVersion || token.IsCancellationRequested)
                return;

            _pageIndex = 0;
            UpdateNavState();

            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Loaded);
            if (version != _renderVersion)
                return;

            var zoom = ComputeFitZoom(PageFitMode.Page) ?? _zoom;
            _zoom = zoom;
            UpdateZoomText();

            var renderScale = ToRenderScale(zoom);
            var dpi = GetBitmapDpi();
            var bitmap = await Task.Run(() =>
            {
                token.ThrowIfCancellationRequested();
                return _renderer.RenderPage(0, renderScale, useCache: true, dpi);
            }, token);

            if (version != _renderVersion || token.IsCancellationRequested)
                return;

            _pageIndex = 0;
            _wantPage = 0;
            ApplyPageBitmap(bitmap, 0, zoom);
            PrefetchNeighbors();
            RebuildSidebarForDocument(path);
            RestoreSidebarForOpenDocument();
            AppSettingsStore.AddRecentFile(path);
            RefreshRecentFiles();
        }
        catch (OperationCanceledException)
        {
            // superseded
        }
        catch (Exception ex)
        {
            AppSettingsStore.RemoveRecentFile(path);
            ClearSidebarContent();
            ShowEmptyState();
            FileNameText.Text = "";
            Title = "AirPDF";
            MessageBox.Show(this, ex.Message, "\u041d\u0435 \u0443\u0434\u0430\u043b\u043e\u0441\u044c \u043e\u0442\u043a\u0440\u044b\u0442\u044c PDF", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            if (version == _renderVersion)
                EndLoading();
        }
    }

    private async void RecentFile_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string path } || string.IsNullOrWhiteSpace(path))
            return;

        if (!File.Exists(path))
        {
            AppSettingsStore.RemoveRecentFile(path);
            RefreshRecentFiles();
            MessageBox.Show(
                this,
                "\u0424\u0430\u0439\u043b \u0431\u043e\u043b\u044c\u0448\u0435 \u043d\u0435 \u043d\u0430\u0439\u0434\u0435\u043d:\n" + path,
                "AirPDF",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        await OpenFileAsync(path);
    }

    private void ClearRecent_Click(object sender, RoutedEventArgs e)
    {
        AppSettingsStore.ClearRecentFiles();
        RefreshRecentFiles();
    }

    private void ShowEmptyState()
    {
        ClearPageView();
        EmptyState.Visibility = Visibility.Visible;
        EmptyDropHighlight.Visibility = Visibility.Collapsed;
        HideSidebarWhileNoDocument();
        RefreshRecentFiles();
    }

    private void RefreshRecentFiles()
    {
        var recent = AppSettingsStore.GetRecentFiles()
            .Select(path => new RecentFileItem(
                path,
                System.IO.Path.GetFileName(path) ?? path))
            .ToList();

        RecentFilesList.ItemsSource = recent;
        RecentEmptyHint.Visibility = recent.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ClearRecentButton.IsEnabled = recent.Count > 0;
        RebuildOpenRecentMenu();
    }

    private void RebuildOpenRecentMenu()
    {
        OpenRecentMenu.Items.Clear();
        var recent = AppSettingsStore.GetRecentFiles().Take(8).ToList();
        if (recent.Count == 0)
        {
            OpenRecentMenu.Items.Add(new MenuItem
            {
                Header = "Нет недавних файлов",
                IsEnabled = false
            });
            OpenRecentDropButton.IsEnabled = false;
            return;
        }

        OpenRecentDropButton.IsEnabled = true;
        foreach (var path in recent)
        {
            var item = new MenuItem
            {
                Header = System.IO.Path.GetFileName(path) ?? path,
                Tag = path,
                ToolTip = path
            };
            item.Click += RecentFile_Click;
            OpenRecentMenu.Items.Add(item);
        }
    }

    private sealed record RecentFileItem(string FilePath, string DisplayName);

    private void Prev_Click(object sender, RoutedEventArgs e) => ChangePage(-1);
    private void Next_Click(object sender, RoutedEventArgs e) => ChangePage(1);

    private void ZoomIn_Click(object sender, RoutedEventArgs e) => ChangeZoom(0.15);
    private void ZoomOut_Click(object sender, RoutedEventArgs e) => ChangeZoom(-0.15);

    private async void FitWidth_Click(object sender, RoutedEventArgs e) => await FitToViewportAsync(PageFitMode.Width);
    private async void FitHeight_Click(object sender, RoutedEventArgs e) => await FitToViewportAsync(PageFitMode.Height);
    private async void FitPage_Click(object sender, RoutedEventArgs e) => await FitToViewportAsync(PageFitMode.Page);

    private async void Print_Click(object sender, RoutedEventArgs e) => await PrintDocumentAsync();

    private async void Export_Click(object sender, RoutedEventArgs e) => await ExportToWordAsync();

    private async void SearchPrev_Click(object sender, RoutedEventArgs e) => await MoveSearchAsync(-1);
    private async void SearchNext_Click(object sender, RoutedEventArgs e) => await MoveSearchAsync(1);

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateSearchPlaceholder();
        UpdateSearchUi();
        if (!_renderer.IsOpen) return;
        _searchDebounce.Stop();
        _searchDebounce.Start();
    }

    private void UpdateSearchPlaceholder()
    {
        var empty = string.IsNullOrEmpty(SearchBox.Text);
        SearchPlaceholder.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        SearchClearButton.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
    }

    private void SearchClear_Click(object sender, RoutedEventArgs e)
    {
        ClearSearch();
        SearchBox.Text = "";
        UpdateSearchPlaceholder();
        if (SearchBox.IsEnabled)
            SearchBox.Focus();
    }

    private async void SearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            if (_searchHits.Count == 0 || !string.Equals(_lastQuery, SearchBox.Text.Trim(), StringComparison.Ordinal))
                await RunSearchAsync(SearchBox.Text, jumpToFirst: true);
            else
                await MoveSearchAsync(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? -1 : 1);
        }
        else if (e.Key == Key.Escape)
        {
            ClearSearch();
            SearchBox.Text = "";
            UpdateSearchPlaceholder();
            e.Handled = true;
        }
    }

    private async void PageBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            await GoToTypedPageAsync();
        }
        else if (e.Key == Key.Escape)
        {
            SyncPageBox();
            ViewerScroll.Focus();
            e.Handled = true;
        }
    }

    private async void PageBox_LostFocus(object sender, RoutedEventArgs e) => await GoToTypedPageAsync();

    private async Task GoToTypedPageAsync()
    {
        if (!_renderer.IsOpen) return;

        if (!int.TryParse(PageBox.Text.Trim(), out var page) || page < 1 || page > _renderer.PageCount)
        {
            SyncPageBox();
            return;
        }

        var target = page - 1;
        if (target == _pageIndex)
        {
            SyncPageBox();
            return;
        }

        await GoToPageAsync(target, immediate: true);
    }

    private void SyncPageBox()
    {
        PageBox.Text = _renderer.IsOpen ? $"{_pageIndex + 1}" : "";
    }



    private void UpdateNavState()
    {
        var open = _renderer.IsOpen;
        PrintButton.IsEnabled = open && !_isExporting;
        ExportButton.IsEnabled = open && !_isExporting;
        PrevButton.IsEnabled = open && _pageIndex > 0;
        NextButton.IsEnabled = open && _pageIndex < _renderer.PageCount - 1;
        FitWidthButton.IsEnabled = open;
        FitHeightButton.IsEnabled = open;
        FitPageButton.IsEnabled = open;
        SearchBox.IsEnabled = open;
        PageBox.IsEnabled = open;
        if (open)
        {
            if (!PageBox.IsKeyboardFocusWithin)
                SyncPageBox();
            PageCountText.Text = $"/ {_renderer.PageCount}";
        }
        else
        {
            PageBox.Text = "";
            PageCountText.Text = "/ ?";
        }
        UpdateSearchUi();
        UpdateZoomText();
    }

    private void UpdateZoomText() => ZoomText.Text = $"{(int)Math.Round(_zoom * 100)}%";

    private void BeginLoading()
    {
        _loadingArmed = true;
        _loadingDelay.Stop();
        _loadingDelay.Start();
    }

    private void EndLoading()
    {
        _loadingArmed = false;
        _loadingDelay.Stop();
        LoadingOverlay.Visibility = Visibility.Collapsed;
    }

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        var ok = HasPdfFile(e);
        e.Effects = ok ? DragDropEffects.Copy : DragDropEffects.None;
        if (EmptyState.Visibility == Visibility.Visible)
            EmptyDropHighlight.Visibility = ok ? Visibility.Visible : Visibility.Collapsed;
        e.Handled = true;
    }

    private void Window_DragLeave(object sender, DragEventArgs e)
    {
        if (EmptyState.Visibility == Visibility.Visible)
            EmptyDropHighlight.Visibility = Visibility.Collapsed;
    }

    private async void Window_Drop(object sender, DragEventArgs e)
    {
        EmptyDropHighlight.Visibility = Visibility.Collapsed;
        if (!TryGetPdfPath(e, out var path))
            return;
        await OpenFileAsync(path);
    }

    private void Window_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateToolbarCompactMode();

    private void UpdateToolbarCompactMode()
    {
        // 1) Icon-only labels free space early.
        // 2) Then hide tool groups into ⋯ overflow.
        // Chrome min/max/close live in a fixed column and stay visible.
        var width = ActualWidth;
        var iconOnly = width < 1200;
        var showFit = width >= 1080;
        var showZoom = width >= 940;
        var showPages = width >= 820;
        var showSearch = width >= 680;
        var showFileName = width >= 520;
        var narrowSearch = width < 1000;

        var labelVis = iconOnly ? Visibility.Collapsed : Visibility.Visible;
        OpenLabel.Visibility = labelVis;
        PrintLabel.Visibility = labelVis;
        ExportLabel.Visibility = labelVis;

        var searchW = narrowSearch ? 160 : 200;
        SearchBoxHost.Width = searchW;
        SearchBoxHost.MinWidth = searchW;
        SearchBoxHost.MaxWidth = searchW;

        FitButtonsPanel.Visibility = showFit ? Visibility.Visible : Visibility.Collapsed;
        ZoomGroup.Visibility = showZoom ? Visibility.Visible : Visibility.Collapsed;
        PagesGroup.Visibility = showPages ? Visibility.Visible : Visibility.Collapsed;
        SearchGroup.Visibility = showSearch ? Visibility.Visible : Visibility.Collapsed;
        FileNameText.Visibility = showFileName ? Visibility.Visible : Visibility.Collapsed;

        var needsOverflow = !showFit || !showZoom || !showPages || !showSearch;
        OverflowButton.Visibility = needsOverflow ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OverflowButton_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu();
        if (SearchGroup.Visibility != Visibility.Visible)
            menu.Items.Add(CreateOverflowItem("Поиск", () =>
            {
                if (SearchBox.IsEnabled)
                {
                    SearchBox.Focus();
                    SearchBox.SelectAll();
                }
            }));
        if (PagesGroup.Visibility != Visibility.Visible)
        {
            menu.Items.Add(CreateOverflowItem("Предыдущая страница", () => _ = ChangePageAsync(-1)));
            menu.Items.Add(CreateOverflowItem("Следующая страница", () => _ = ChangePageAsync(1)));
        }
        if (ZoomGroup.Visibility != Visibility.Visible || FitButtonsPanel.Visibility != Visibility.Visible)
        {
            menu.Items.Add(CreateOverflowItem("По ширине", () => _ = FitToViewportAsync(PageFitMode.Width)));
            menu.Items.Add(CreateOverflowItem("По высоте", () => _ = FitToViewportAsync(PageFitMode.Height)));
            menu.Items.Add(CreateOverflowItem("Страница целиком", () => _ = FitToViewportAsync(PageFitMode.Page)));
        }
        if (menu.Items.Count > 0)
            menu.Items.Add(new Separator());
        menu.Items.Add(CreateOverflowItem("Печать", () => _ = PrintDocumentAsync()));
        menu.Items.Add(CreateOverflowItem("Экспорт в Microsoft Word", () => _ = ExportToWordAsync()));
        menu.PlacementTarget = OverflowButton;
        menu.IsOpen = true;
    }

    private static MenuItem CreateOverflowItem(string header, Action action)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => action();
        return item;
    }

    private async void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);

        if (e.Key == Key.F11)
        {
            ToggleFullscreen();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.F4)
        {
            ToggleSidebar();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape && _placement.IsFullscreen)
        {
            ToggleFullscreen();
            e.Handled = true;
            return;
        }

        if (ctrl && e.Key == Key.O)
        {
            await OpenWithDialogAsync();
            e.Handled = true;
            return;
        }

        if (ctrl && e.Key == Key.W)
        {
            _ = CloseDocumentAsync();
            e.Handled = true;
            return;
        }

        if (ctrl && e.Key == Key.P)
        {
            _ = PrintDocumentAsync();
            e.Handled = true;
            return;
        }

        if (ctrl && e.Key == Key.F)
        {
            if (SearchBox.IsEnabled)
            {
                SearchBox.Focus();
                SearchBox.SelectAll();
            }
            e.Handled = true;
            return;
        }

        if (ctrl && e.Key == Key.G)
        {
            if (PageBox.IsEnabled)
            {
                PageBox.Focus();
                PageBox.SelectAll();
            }
            e.Handled = true;
            return;
        }

        if (!_renderer.IsOpen) return;

        if (ctrl && e.Key == Key.C && Keyboard.FocusedElement is not TextBox)
        {
            CopySelectionToClipboard();
            e.Handled = true;
            return;
        }

        if (ctrl && e.Key == Key.A && Keyboard.FocusedElement is not TextBox)
        {
            SelectAllPageText();
            e.Handled = true;
            return;
        }

        if (Keyboard.FocusedElement is TextBox)
            return;

        if (ctrl && (e.Key is Key.OemPlus or Key.Add))
        {
            ChangeZoom(0.15);
            e.Handled = true;
            return;
        }

        if (ctrl && (e.Key is Key.OemMinus or Key.Subtract))
        {
            ChangeZoom(-0.15);
            e.Handled = true;
            return;
        }

        switch (e.Key)
        {
            case Key.Home:
                _ = GoToFirstPageAsync();
                e.Handled = true;
                break;
            case Key.End:
                _ = GoToLastPageAsync();
                e.Handled = true;
                break;
            case Key.Left:
            case Key.PageUp:
                ChangePage(-1);
                e.Handled = true;
                break;
            case Key.Right:
            case Key.PageDown:
                ChangePage(1);
                e.Handled = true;
                break;
            case Key.Space:
                if (ViewerScroll.ScrollableHeight > 0 &&
                    ViewerScroll.VerticalOffset < ViewerScroll.ScrollableHeight - 8)
                {
                    ViewerScroll.ScrollToVerticalOffset(
                        Math.Min(ViewerScroll.ScrollableHeight, ViewerScroll.VerticalOffset + ViewerScroll.ViewportHeight * 0.9));
                }
                else
                {
                    ChangePage(1);
                }
                e.Handled = true;
                break;
            case Key.F3:
                await MoveSearchAsync(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? -1 : 1);
                e.Handled = true;
                break;
        }
    }

    private void ViewerContextMenu_Opened(object sender, RoutedEventArgs e)
    {
        CopyMenuItem.IsEnabled = !string.IsNullOrEmpty(_selectedText);
        UpdateViewMenuItems();
    }

    private void AppMenu_SubmenuOpened(object sender, RoutedEventArgs e) => UpdateViewMenuItems();

    private void UpdateViewMenuItems()
    {
        var sidebarHeader = _sidebarOpen ? "Скрыть панель навигации" : "Панель навигации";
        var sidebarEnabled = _renderer.IsOpen && !_placement.IsFullscreen;
        var fullscreenHeader = _placement.IsFullscreen ? "Выйти из полноэкранного режима" : "Во весь экран";

        SidebarNavMenuItem.Header = sidebarHeader;
        SidebarNavMenuItem.IsEnabled = sidebarEnabled;
        FullscreenMenuItem.Header = fullscreenHeader;

        AppSidebarNavMenuItem.Header = sidebarHeader;
        AppSidebarNavMenuItem.IsEnabled = sidebarEnabled;
        AppFullscreenMenuItem.Header = fullscreenHeader;
    }

    private void CopySelection_Click(object sender, RoutedEventArgs e) => CopySelectionToClipboard();

    private void SelectAllText_Click(object sender, RoutedEventArgs e) => SelectAllPageText();

    private void CopyPageText_Click(object sender, RoutedEventArgs e)
    {
        if (!_renderer.IsOpen) return;
        try
        {
            var text = _renderer.GetPageText(_pageIndex);
            if (string.IsNullOrWhiteSpace(text))
            {
                MessageBox.Show(this, "На странице нет текстового слоя.", "Копирование",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            Clipboard.SetText(text);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Копирование", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void RotateClockwise_Click(object sender, RoutedEventArgs e) => RotatePage(90);

    private void RotateCounterClockwise_Click(object sender, RoutedEventArgs e) => RotatePage(-90);

    private void RotateReset_Click(object sender, RoutedEventArgs e)
    {
        _pageRotate.Angle = 0;
        ClearTextSelection();
    }

    private void Fullscreen_Click(object sender, RoutedEventArgs e) => ToggleFullscreen();



    private async void ViewerScroll_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (!_renderer.IsOpen)
            return;

        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            ChangeZoom(e.Delta > 0 ? 0.1 : -0.1);
            e.Handled = true;
            return;
        }

        // When the page fits the viewport, wheel flips pages; otherwise keep native scroll.
        ViewerScroll.UpdateLayout();
        const double fitTolerance = 2;
        var pageFits =
            ViewerScroll.ScrollableHeight <= fitTolerance &&
            ViewerScroll.ScrollableWidth <= fitTolerance;
        if (!pageFits)
            return;

        ChangePage(e.Delta > 0 ? -1 : 1);
        e.Handled = true;
    }


    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(parent);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match)
                return match;
            var nested = FindVisualChild<T>(child);
            if (nested is not null)
                return nested;
        }

        return null;
    }

    private static bool HasPdfFile(DragEventArgs e) => TryGetPdfPath(e, out _);

    private static bool TryGetPdfPath(DragEventArgs e, out string path)
    {
        path = string.Empty;
        if (!e.Data.GetDataPresent(DataFormats.FileDrop))
            return false;

        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files || files.Length == 0)
            return false;

        var candidate = files[0];
        if (!candidate.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) || !File.Exists(candidate))
            return false;

        path = candidate;
        return true;
    }
}
