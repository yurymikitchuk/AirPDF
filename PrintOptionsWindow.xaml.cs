using System.Printing;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AirPDF.Services;

namespace AirPDF;

public partial class PrintOptionsWindow : Window
{
    private const double PreviewRenderScale = 0.45;
    private const int MaxPreviewPages = 50;
    private const double PreviewPaperShort = 120;
    private const double PreviewPaperLong = 170;

    private readonly PdfRenderer _renderer;
    private readonly int _pageCount;
    private readonly int _currentPageIndex;
    private readonly DispatcherTimer _previewDebounce;

    private CancellationTokenSource? _previewCts;
    private int _previewVersion;
    private string _lastPreviewKey = "";

    public AirPrintSettings? Result { get; private set; }

    public PrintOptionsWindow(PdfRenderer renderer, int currentPageIndex)
    {
        InitializeComponent();
        _renderer = renderer;
        _pageCount = renderer.PageCount;
        _currentPageIndex = currentPageIndex;
        InfoText.Text = $"В документе страниц: {_pageCount}. Сейчас открыта: {currentPageIndex + 1}.";
        RangeBox.Text = _pageCount <= 1 ? "1" : $"1-{_pageCount}";

        PopulatePrinters();
        PopulateOrientation();
        PopulateScale();

        _previewDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        _previewDebounce.Tick += (_, _) =>
        {
            _previewDebounce.Stop();
            _ = RefreshPreviewAsync();
        };

        Loaded += (_, _) => QueuePreviewRefresh(force: true);
    }

    private void PopulatePrinters()
    {
        try
        {
            using var server = new LocalPrintServer();
            var queues = server.GetPrintQueues(
                [EnumeratedPrintQueueTypes.Local, EnumeratedPrintQueueTypes.Connections]);

            var list = queues
                .OrderBy(q => q.FullName, StringComparer.CurrentCultureIgnoreCase)
                .ToList();

            PrinterCombo.ItemsSource = list;

            PrintQueue? preferred = null;
            try { preferred = LocalPrintServer.GetDefaultPrintQueue(); }
            catch { /* no default printer */ }

            if (preferred is not null)
            {
                var match = list.FirstOrDefault(q =>
                    string.Equals(q.FullName, preferred.FullName, StringComparison.OrdinalIgnoreCase));
                PrinterCombo.SelectedItem = match ?? list.FirstOrDefault();
            }
            else
            {
                PrinterCombo.SelectedItem = list.FirstOrDefault();
            }
        }
        catch
        {
            PrinterCombo.ItemsSource = Array.Empty<PrintQueue>();
        }
    }

    private void PopulateOrientation()
    {
        OrientationCombo.Items.Clear();
        OrientationCombo.Items.Add(new ComboBoxItem { Content = "Авто", Tag = PrintOrientationMode.Auto });
        OrientationCombo.Items.Add(new ComboBoxItem { Content = "Книжная", Tag = PrintOrientationMode.Portrait });
        OrientationCombo.Items.Add(new ComboBoxItem { Content = "Альбомная", Tag = PrintOrientationMode.Landscape });
        OrientationCombo.SelectedIndex = 0;
    }

    private void PopulateScale()
    {
        ScaleCombo.Items.Clear();
        ScaleCombo.Items.Add(new ComboBoxItem { Content = "Вписать в страницу", Tag = PrintScaleMode.FitPage });
        ScaleCombo.Items.Add(new ComboBoxItem { Content = "По ширине", Tag = PrintScaleMode.FitWidth });
        ScaleCombo.Items.Add(new ComboBoxItem { Content = "Реальный размер (100%)", Tag = PrintScaleMode.ActualSize });
        ScaleCombo.Items.Add(new ComboBoxItem { Content = "Заполнить страницу", Tag = PrintScaleMode.FillPage });
        ScaleCombo.SelectedIndex = 0;
    }

    private void Mode_Changed(object sender, RoutedEventArgs e)
    {
        if (RangeBox is null) return;
        RangeBox.IsEnabled = RangeRadio.IsChecked == true;
        if (RangeBox.IsEnabled)
            RangeBox.Focus();
        QueuePreviewRefresh(force: true);
    }

    private void RangeBox_TextChanged(object sender, TextChangedEventArgs e) =>
        QueuePreviewRefresh(force: true);

    private void PrintOption_Changed(object sender, SelectionChangedEventArgs e) =>
        QueuePreviewRefresh(force: true);

    private void QueuePreviewRefresh(bool force = false)
    {
        if (!IsLoaded) return;
        if (force)
            _lastPreviewKey = "";
        _previewDebounce.Stop();
        _previewDebounce.Start();
    }

    private async Task RefreshPreviewAsync()
    {
        if (!TryResolvePages(out var pages, out var error, forPreview: true))
        {
            PreviewPanel.Children.Clear();
            PreviewStatusText.Text = "";
            _lastPreviewKey = "";
            if (!string.IsNullOrWhiteSpace(error))
            {
                ErrorText.Text = error;
                ErrorText.Visibility = Visibility.Visible;
            }
            else
            {
                ErrorText.Visibility = Visibility.Collapsed;
            }
            return;
        }

        ErrorText.Visibility = Visibility.Collapsed;

        var scaleMode = GetSelectedScale();
        var orientation = GetSelectedOrientation();
        var landscape = IsLandscapeForPages(pages, orientation);
        var previewKey =
            $"{string.Join(',', pages.Take(MaxPreviewPages))}|{scaleMode}|{orientation}|{landscape}";
        if (previewKey == _lastPreviewKey)
            return;
        _lastPreviewKey = previewKey;

        var showCount = Math.Min(pages.Count, MaxPreviewPages);
        PreviewStatusText.Text = pages.Count > MaxPreviewPages
            ? $"К печати: {pages.Count} стр. · {GetSelectedOrientationLabel()} · {GetSelectedScaleLabel()}\n(показаны первые {MaxPreviewPages})"
            : $"К печати: {pages.Count} стр. · {GetSelectedOrientationLabel()} · {GetSelectedScaleLabel()}";

        _previewCts?.Cancel();
        _previewCts?.Dispose();
        _previewCts = new CancellationTokenSource();
        var token = _previewCts.Token;
        var version = ++_previewVersion;

        PreviewPanel.Children.Clear();
        for (var i = 0; i < showCount; i++)
            PreviewPanel.Children.Add(CreatePlaceholder(pages[i] + 1, landscape));

        try
        {
            var paper = landscape
                ? new Size(PreviewPaperLong, PreviewPaperShort)
                : new Size(PreviewPaperShort, PreviewPaperLong);

            for (var i = 0; i < showCount; i++)
            {
                token.ThrowIfCancellationRequested();
                var pageIndex = pages[i];
                var slot = i;

                var pageData = await Task.Run(() =>
                {
                    token.ThrowIfCancellationRequested();
                    var bitmap = _renderer.RenderPage(pageIndex, PreviewRenderScale, useCache: false);
                    var native = _renderer.GetNativePageSize(pageIndex);
                    return (Bitmap: bitmap, Native: native);
                }, token);

                if (version != _previewVersion || token.IsCancellationRequested)
                    return;

                // RenderTargetBitmap must run on the UI thread.
                var preview = ComposePreviewFrame(pageData.Bitmap, pageData.Native, paper, scaleMode);

                if (slot < PreviewPanel.Children.Count && PreviewPanel.Children[slot] is Border card)
                    ApplyPreviewImage(card, preview, pageIndex + 1);
            }
        }
        catch (OperationCanceledException)
        {
            // superseded
        }
        catch (Exception ex)
        {
            PreviewStatusText.Text = ex.Message;
            _lastPreviewKey = "";
        }
    }

    private static BitmapSource ComposePreviewFrame(
        BitmapSource pageBitmap,
        Size nativePdfSize,
        Size paper,
        PrintScaleMode scaleMode)
    {
        var dest = PrintLayout.ComputeContentRect(pageBitmap, nativePdfSize, paper, scaleMode);

        var bounds = new Rect(0, 0, paper.Width, paper.Height);
        var contentBounds = Rect.Union(bounds, dest);
        contentBounds.Inflate(4, 4);

        var scale = Math.Min(
            (paper.Width + 24) / contentBounds.Width,
            (paper.Height + 24) / contentBounds.Height);
        scale = Math.Min(scale, 1.0);

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.PushTransform(new ScaleTransform(scale, scale));
            dc.PushTransform(new TranslateTransform(-contentBounds.X, -contentBounds.Y));

            dc.DrawRectangle(
                new SolidColorBrush(Color.FromRgb(0xEE, 0xEE, 0xEE)),
                null,
                contentBounds);
            dc.DrawRectangle(
                Brushes.White,
                new Pen(new SolidColorBrush(Color.FromRgb(0xC2, 0xC2, 0xC2)), 1),
                bounds);
            dc.PushClip(new RectangleGeometry(bounds));
            dc.DrawImage(pageBitmap, dest);
            dc.Pop();
            dc.Pop();
            dc.Pop();
        }

        var pixelW = Math.Max(1, (int)Math.Ceiling(contentBounds.Width * scale));
        var pixelH = Math.Max(1, (int)Math.Ceiling(contentBounds.Height * scale));
        var rtb = new RenderTargetBitmap(pixelW, pixelH, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        rtb.Freeze();
        return rtb;
    }

    private bool TryResolvePages(out List<int> pages, out string? error, bool forPreview)
    {
        pages = [];
        error = null;

        if (AllPagesRadio?.IsChecked == true)
        {
            pages = Enumerable.Range(0, _pageCount).ToList();
            return true;
        }

        if (CurrentPageRadio?.IsChecked == true)
        {
            pages = [_currentPageIndex];
            return true;
        }

        if (RangeRadio?.IsChecked == true)
        {
            if (string.IsNullOrWhiteSpace(RangeBox?.Text))
            {
                if (!forPreview)
                    error = "Укажите диапазон страниц.";
                return false;
            }

            return PageRangeParser.TryParse(RangeBox.Text ?? "", _pageCount, out pages, out error);
        }

        return false;
    }

    private PrintOrientationMode GetSelectedOrientation()
    {
        if (OrientationCombo?.SelectedItem is ComboBoxItem { Tag: PrintOrientationMode mode })
            return mode;
        return PrintOrientationMode.Auto;
    }

    private PrintScaleMode GetSelectedScale()
    {
        if (ScaleCombo?.SelectedItem is ComboBoxItem { Tag: PrintScaleMode mode })
            return mode;
        return PrintScaleMode.FitPage;
    }

    private string GetSelectedOrientationLabel() =>
        OrientationCombo?.SelectedItem is ComboBoxItem item ? item.Content?.ToString() ?? "Авто" : "Авто";

    private string GetSelectedScaleLabel() =>
        ScaleCombo?.SelectedItem is ComboBoxItem item ? item.Content?.ToString() ?? "Вписать" : "Вписать";

    private bool IsLandscapeForPages(IReadOnlyList<int> pages, PrintOrientationMode mode)
    {
        if (mode == PrintOrientationMode.Landscape) return true;
        if (mode == PrintOrientationMode.Portrait) return false;
        if (pages.Count == 0) return false;

        try
        {
            var size = _renderer.GetNativePageSize(pages[0]);
            return size.Width > size.Height;
        }
        catch
        {
            return false;
        }
    }

    private static Border CreatePlaceholder(int pageNumber, bool landscape)
    {
        var width = landscape ? PreviewPaperLong + 16 : PreviewPaperShort + 16;
        var height = landscape ? PreviewPaperShort + 28 : PreviewPaperLong + 28;

        var image = new Image
        {
            Stretch = Stretch.Uniform,
            Margin = new Thickness(6, 6, 6, 22)
        };

        var label = new TextBlock
        {
            Text = pageNumber.ToString(),
            FontSize = 11,
            Foreground = (Brush)Application.Current.Resources["TextMuted"],
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 0, 4)
        };

        var grid = new Grid();
        grid.Children.Add(image);
        grid.Children.Add(label);

        return new Border
        {
            Width = width,
            Height = height,
            Margin = new Thickness(4),
            Background = (Brush)Application.Current.Resources["BgElevated"],
            BorderBrush = (Brush)Application.Current.Resources["BorderStrong"],
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Child = grid
        };
    }

    private static void ApplyPreviewImage(Border card, BitmapSource bitmap, int pageNumber)
    {
        if (card.Child is not Grid grid)
            return;

        foreach (var child in grid.Children)
        {
            if (child is Image image)
                image.Source = bitmap;
            else if (child is TextBlock label)
                label.Text = pageNumber.ToString();
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void Print_Click(object sender, RoutedEventArgs e)
    {
        if (!TryResolvePages(out var pages, out var error, forPreview: false))
        {
            ErrorText.Text = string.IsNullOrWhiteSpace(error) ? "Не удалось выбрать страницы." : error;
            ErrorText.Visibility = Visibility.Visible;
            return;
        }

        if (PrinterCombo.SelectedItem is not PrintQueue)
        {
            ErrorText.Text = "Выберите принтер.";
            ErrorText.Visibility = Visibility.Visible;
            return;
        }

        Result = new AirPrintSettings
        {
            Pages = pages,
            Printer = (PrintQueue)PrinterCombo.SelectedItem,
            Orientation = GetSelectedOrientation(),
            ScaleMode = GetSelectedScale()
        };

        DialogResult = true;
        Close();
    }

    private void Window_Closed(object sender, EventArgs e)
    {
        _previewDebounce.Stop();
        _previewCts?.Cancel();
        _previewCts?.Dispose();
        _previewCts = null;
    }
}
