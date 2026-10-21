using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using AirPDF.Services;
using Microsoft.Win32;

namespace AirPDF;

public partial class ExportOptionsWindow : Window
{
    private readonly int _pageCount;
    private readonly int _currentPageIndex;

    public WordExportOptions? Result { get; private set; }

    public ExportOptionsWindow(string suggestedPath, int pageCount, int currentPageIndex)
    {
        InitializeComponent();

        _pageCount = Math.Max(0, pageCount);
        _currentPageIndex = Math.Clamp(currentPageIndex, 0, Math.Max(0, _pageCount - 1));

        ModeCombo.Items.Add(new ComboBoxItem
        {
            Content = "Редактируемый текст (OCR / слой PDF)",
            Tag = WordExportMode.EditableText
        });
        ModeCombo.Items.Add(new ComboBoxItem
        {
            Content = "Точная копия страниц (изображение + скрытый текст)",
            Tag = WordExportMode.PreserveAppearance
        });

        var settings = AppSettingsStore.Load();
        ModeCombo.SelectedIndex = settings.ExportMode == nameof(WordExportMode.PreserveAppearance) ? 1 : 0;

        InfoText.Text =
            $"OCR выполняется автоматически (RU+EN). В документе страниц: {_pageCount}. " +
            $"Сейчас открыта: {_currentPageIndex + 1}. Просмотр PDF во время экспорта останется доступен.";
        RangeBox.Text = _pageCount <= 1 ? "1" : $"1-{_pageCount}";

        PathBox.IsReadOnly = false;
        PathBox.Text = suggestedPath ?? "";
        ContentRendered += (_, _) => AlignPathToStart();
        Mode_Changed(this, new RoutedEventArgs());
    }

    private void Mode_Changed(object sender, RoutedEventArgs e)
    {
        if (RangeBox is null)
            return;
        RangeBox.IsEnabled = RangeRadio.IsChecked == true;
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var current = PathBox.Text.Trim();
        var fileName = !string.IsNullOrWhiteSpace(current)
            ? Path.GetFileName(current)
            : "export.docx";
        var directory = TryGetDirectory(current);

        var save = new SaveFileDialog
        {
            Title = "Экспорт в Microsoft Word",
            Filter = "Word (*.docx)|*.docx",
            FileName = string.IsNullOrWhiteSpace(fileName) ? "export.docx" : fileName,
            InitialDirectory = directory,
            AddExtension = true,
            DefaultExt = ".docx"
        };

        if (save.ShowDialog(this) == true)
        {
            PathBox.Text = save.FileName;
            AlignPathToStart();
        }
    }

    private void AlignPathToStart()
    {
        Dispatcher.BeginInvoke(() =>
        {
            PathBox.CaretIndex = 0;
            PathBox.SelectionLength = 0;
            PathBox.ScrollToHome();

            if (PathBox.Template?.FindName("PART_ContentHost", PathBox) is ScrollViewer host)
                host.ScrollToHorizontalOffset(0);
        }, DispatcherPriority.Loaded);
    }

    private static string? TryGetDirectory(string path)
    {
        try
        {
            var dir = Path.GetDirectoryName(path);
            return !string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir) ? dir : null;
        }
        catch
        {
            return null;
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        ErrorText.Visibility = Visibility.Collapsed;

        if (string.IsNullOrWhiteSpace(PathBox.Text))
        {
            ErrorText.Text = "Укажите файл для сохранения.";
            ErrorText.Visibility = Visibility.Visible;
            return;
        }

        if (!TryResolvePages(out var pages, out var pageError))
        {
            ErrorText.Text = pageError ?? "Некорректный диапазон страниц.";
            ErrorText.Visibility = Visibility.Visible;
            return;
        }

        var mode = ModeCombo.SelectedItem is ComboBoxItem { Tag: WordExportMode selected }
            ? selected
            : WordExportMode.EditableText;

        AppSettingsStore.Update(settings =>
        {
            settings.ExportMode = mode.ToString();
            settings.OcrLanguage = "auto";
        });

        Result = new WordExportOptions
        {
            OutputPath = PathBox.Text.Trim(),
            Mode = mode,
            OcrLanguage = "auto",
            Pages = pages
        };

        DialogResult = true;
        Close();
    }

    private bool TryResolvePages(out List<int> pages, out string? error)
    {
        pages = [];
        error = null;

        if (_pageCount <= 0)
        {
            error = "Нет страниц для экспорта.";
            return false;
        }

        if (AllPagesRadio.IsChecked == true)
        {
            pages = Enumerable.Range(0, _pageCount).ToList();
            return true;
        }

        if (CurrentPageRadio.IsChecked == true)
        {
            pages = [_currentPageIndex];
            return true;
        }

        return PageRangeParser.TryParse(RangeBox.Text, _pageCount, out pages, out error);
    }
}
