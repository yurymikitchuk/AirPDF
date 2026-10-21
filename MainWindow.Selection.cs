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
    private void PageContent_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!_renderer.IsOpen || PageImage.Source is null)
            return;

        Focus();
        var native = ToNativePoint(e.GetPosition(PageContent));
        EnsurePageLinks();
        var link = PdfPageLinkReader.HitTest(_pageLinks, native);
        if (link is not null)
        {
            _pressedLink = link;
            _pressedLinkOrigin = e.GetPosition(PageContent);
            _isSelecting = false;
            PageContent.CaptureMouse();
            e.Handled = true;
            return;
        }

        _pressedLink = null;
        _isSelecting = true;
        _selAnchorNative = native;
        PageContent.CaptureMouse();
        UpdateSelectionFromNativeRect(new Rect(native, native));
        e.Handled = true;
    }

    private void PageContent_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!_renderer.IsOpen || PageImage.Source is null)
            return;

        var local = e.GetPosition(PageContent);
        var native = ToNativePoint(local);

        if (_pressedLink is not null)
        {
            var dx = local.X - _pressedLinkOrigin.X;
            var dy = local.Y - _pressedLinkOrigin.Y;
            if (dx * dx + dy * dy > 36)
            {
                // Treat as text selection instead of a link click.
                _isSelecting = true;
                _selAnchorNative = ToNativePoint(_pressedLinkOrigin);
                _pressedLink = null;
                UpdateSelectionFromNativeRect(new Rect(_selAnchorNative, native));
            }

            return;
        }

        if (_isSelecting)
        {
            UpdateSelectionFromNativeRect(new Rect(_selAnchorNative, native));
            return;
        }

        EnsurePageLinks();
        var hover = PdfPageLinkReader.HitTest(_pageLinks, native);
        PageFrame.Cursor = hover is null ? Cursors.IBeam : Cursors.Hand;
        if (hover is not null)
            PageFrame.ToolTip = hover.Kind == PdfLinkKind.Uri
                ? hover.Uri
                : $"Стр. {hover.TargetPageIndex + 1}";
        else
            PageFrame.ToolTip = null;
    }

    private void PageContent_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_pressedLink is not null)
        {
            var link = _pressedLink;
            _pressedLink = null;
            if (PageContent.IsMouseCaptured)
                PageContent.ReleaseMouseCapture();
            ActivatePdfLink(link);
            e.Handled = true;
            return;
        }

        if (!_isSelecting)
            return;

        _isSelecting = false;
        if (PageContent.IsMouseCaptured)
            PageContent.ReleaseMouseCapture();
    }

    private void PageContent_MouseLeave(object sender, MouseEventArgs e)
    {
        if (_isSelecting || _pressedLink is not null)
            return;
        PageFrame.Cursor = Cursors.IBeam;
        PageFrame.ToolTip = null;
    }

    private void InvalidatePageLinks()
    {
        _pageLinks = [];
        _pageLinksPage = int.MinValue;
        _pressedLink = null;
    }

    private void EnsurePageLinks()
    {
        if (!_renderer.IsOpen || _renderer.FilePath is null)
        {
            InvalidatePageLinks();
            return;
        }

        if (_pageLinksPage == _pageIndex)
            return;

        try
        {
            var size = _renderer.GetNativePageSize(_pageIndex);
            _pageLinks = PdfPageLinkReader.Load(_renderer.FilePath, _pageIndex, size);
            _pageLinksPage = _pageIndex;
        }
        catch
        {
            _pageLinks = [];
            _pageLinksPage = _pageIndex;
        }
    }

    private void ActivatePdfLink(PdfPageLink link)
    {
        if (link.Kind == PdfLinkKind.Internal)
        {
            if (link.TargetPageIndex >= 0 && link.TargetPageIndex < _renderer.PageCount)
                RequestPage(link.TargetPageIndex);
            return;
        }

        if (string.IsNullOrWhiteSpace(link.Uri))
            return;

        var uri = link.Uri.Trim();
        if (!uri.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !uri.StartsWith("https://", StringComparison.OrdinalIgnoreCase) &&
            !uri.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show(this, "Ссылка имеет неподдерживаемый формат:\n" + uri,
                "Ссылка", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var confirm = MessageBox.Show(
            this,
            "Открыть ссылку?\n\n" + uri,
            "Внешняя ссылка",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes)
            return;

        try
        {
            Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Ссылка", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private Point ToNativePoint(Point pageLocal)
    {
        var scale = Math.Max(_renderedZoom, 0.01);
        return new Point(pageLocal.X / scale, pageLocal.Y / scale);
    }

    private void EnsurePageCharacters()
    {
        if (!_renderer.IsOpen)
        {
            _pageCharacters = null;
            _pageCharactersPage = int.MinValue;
            return;
        }

        if (_pageCharacters is not null && _pageCharactersPage == _pageIndex)
            return;

        try
        {
            _pageCharacters = _renderer.GetPageCharacters(_pageIndex);
            _pageCharactersPage = _pageIndex;
        }
        catch
        {
            _pageCharacters = [];
            _pageCharactersPage = _pageIndex;
        }
    }

    private void UpdateSelectionFromNativeRect(Rect nativeRect)
    {
        EnsurePageCharacters();
        var chars = _pageCharacters;
        if (chars is null || chars.Count == 0)
        {
            _selectedText = "";
            _selectionNative = null;
            _selectionIsAll = false;
            SelectionLayer.Children.Clear();
            return;
        }

        var left = Math.Min(nativeRect.Left, nativeRect.Right);
        var top = Math.Min(nativeRect.Top, nativeRect.Bottom);
        var right = Math.Max(nativeRect.Left, nativeRect.Right);
        var bottom = Math.Max(nativeRect.Top, nativeRect.Bottom);
        // Tiny click → clear selection.
        if (right - left < 1 && bottom - top < 1)
        {
            _selectedText = "";
            _selectionNative = null;
            _selectionIsAll = false;
            SelectionLayer.Children.Clear();
            return;
        }

        _selectionNative = new Rect(left, top, right - left, bottom - top);
        _selectionIsAll = false;

        var selected = new List<Character>();
        foreach (var ch in chars)
        {
            var box = ch.Box;
            if (box.Right < left || box.Left > right || box.Bottom < top || box.Top > bottom)
                continue;
            selected.Add(ch);
        }

        _selectedText = BuildTextFromCharacters(selected);
        DrawSelectionOverlay(selected);
    }

    private void SelectAllPageText()
    {
        if (!_renderer.IsOpen) return;
        EnsurePageCharacters();
        var chars = _pageCharacters;
        if (chars is null || chars.Count == 0)
        {
            MessageBox.Show(this, "На странице нет текстового слоя для выделения.", "Выделение",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        _selectionIsAll = true;
        _selectionNative = null;
        _selectedText = BuildTextFromCharacters(chars);
        DrawSelectionOverlay(chars);
    }

    private void CopySelectionToClipboard()
    {
        if (string.IsNullOrEmpty(_selectedText))
            return;

        try
        {
            Clipboard.SetText(_selectedText);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Копирование", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void ClearTextSelection()
    {
        _isSelecting = false;
        _selectedText = "";
        _selectionNative = null;
        _selectionIsAll = false;
        _pageCharacters = null;
        _pageCharactersPage = int.MinValue;
        SelectionLayer.Children.Clear();
        if (PageContent.IsMouseCaptured)
            PageContent.ReleaseMouseCapture();
    }

    private void DrawSelectionOverlay()
    {
        if (_selectionIsAll)
        {
            EnsurePageCharacters();
            if (_pageCharacters is { Count: > 0 })
            {
                _selectedText = BuildTextFromCharacters(_pageCharacters);
                DrawSelectionOverlay(_pageCharacters);
                return;
            }
        }

        if (_selectionNative is Rect native)
        {
            UpdateSelectionFromNativeRect(native);
            return;
        }

        SelectionLayer.Children.Clear();
    }

    private void DrawSelectionOverlay(IReadOnlyList<Character> selected)
    {
        SelectionLayer.Children.Clear();
        if (selected.Count == 0 || PageImage.Source is not BitmapSource)
            return;

        SelectionLayer.Width = HighlightLayer.Width;
        SelectionLayer.Height = HighlightLayer.Height;
        var scale = _renderedZoom;
        var brush = new SolidColorBrush(Color.FromArgb(0x66, 0x2F, 0x6F, 0xED));
        brush.Freeze();

        foreach (var ch in selected)
        {
            var box = ch.Box;
            var w = Math.Max(1, (box.Right - box.Left) * scale);
            var h = Math.Max(1, (box.Bottom - box.Top) * scale);
            var rect = new Rectangle
            {
                Width = w,
                Height = h,
                Fill = brush,
                IsHitTestVisible = false
            };
            Canvas.SetLeft(rect, box.Left * scale);
            Canvas.SetTop(rect, box.Top * scale);
            SelectionLayer.Children.Add(rect);
        }
    }

    private static string BuildTextFromCharacters(IReadOnlyList<Character> characters)
    {
        if (characters.Count == 0)
            return "";

        var ordered = characters
            .OrderBy(c => c.Box.Top)
            .ThenBy(c => c.Box.Left)
            .ToList();

        var heights = ordered
            .Select(c => Math.Max(1, c.Box.Bottom - c.Box.Top))
            .OrderBy(h => h)
            .ToList();
        var medianH = heights[heights.Count / 2];
        var lineThreshold = Math.Max(4, medianH * 0.6);
        var spaceThreshold = Math.Max(3, medianH * 0.35);

        var sb = new StringBuilder(ordered.Count + 16);
        int? prevBottom = null;
        int? prevRight = null;

        foreach (var ch in ordered)
        {
            var box = ch.Box;
            if (prevBottom is not null && box.Top > prevBottom.Value + lineThreshold)
            {
                sb.AppendLine();
                prevRight = null;
            }
            else if (prevRight is not null && box.Left > prevRight.Value + spaceThreshold)
            {
                sb.Append(' ');
            }

            sb.Append(ch.Char);
            prevBottom = box.Bottom;
            prevRight = box.Right;
        }

        return sb.ToString();
    }
}
