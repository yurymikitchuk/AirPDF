using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace AirPDF.Services;

/// <summary>
/// Maximize (work area) + F11 (full monitor) placement for a borderless window.
/// UI chrome (toolbar/sidebar) stays in the window code-behind.
/// </summary>
internal sealed class WindowPlacementController
{
    private readonly Window _window;
    private Rect _restoreBounds;
    private Rect _fullscreenRestoreBounds;
    private bool _fullscreenWasFilled;

    public WindowPlacementController(Window window)
    {
        _window = window;
        NativeMonitor.HookWorkAreaMaximize(window);
        window.StateChanged += (_, _) =>
        {
            if (IsFullscreen) return;
            IsFilled = window.WindowState == WindowState.Maximized;
            Changed?.Invoke(this, EventArgs.Empty);
        };
    }

    public bool IsFilled { get; private set; }
    public bool IsFullscreen { get; private set; }

    public event EventHandler? Changed;

    public void ToggleMaximize()
    {
        if (IsFilled || _window.WindowState == WindowState.Maximized)
            RestoreFromWorkArea();
        else
            FillToWorkArea();
    }

    public void FillToWorkArea()
    {
        if (!IsFilled && _window.WindowState == WindowState.Normal)
            RememberNormalBounds();

        _window.WindowState = WindowState.Maximized;
        IsFilled = true;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void RestoreFromWorkArea()
    {
        var target = ClampToWorkArea(IsUsableNormalBounds(_restoreBounds)
            ? _restoreBounds
            : GetDefaultNormalBounds());

        IsFilled = false;
        _window.WindowState = WindowState.Normal;
        ApplyWindowBounds(target);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Apply full-monitor geometry. Caller collapses chrome before/after as needed.</summary>
    public void EnterFullscreen()
    {
        if (IsFullscreen) return;

        _fullscreenWasFilled = IsFilled || _window.WindowState == WindowState.Maximized;
        RememberNormalBounds();
        _fullscreenRestoreBounds = IsUsableNormalBounds(_restoreBounds)
            ? _restoreBounds
            : GetDefaultNormalBounds();

        IsFullscreen = true;
        IsFilled = false;

        if (_window.WindowState != WindowState.Normal)
            _window.WindowState = WindowState.Normal;

        var screen = NativeMonitor.GetBoundsDip(_window, workArea: false);
        if (screen.Width < 200 || screen.Height < 200 ||
            double.IsNaN(screen.Width) || double.IsNaN(screen.Height))
        {
            screen = new Rect(0, 0,
                SystemParameters.PrimaryScreenWidth,
                SystemParameters.PrimaryScreenHeight);
        }

        _window.Left = screen.Left;
        _window.Top = screen.Top;
        _window.Width = screen.Width;
        _window.Height = screen.Height;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Restore geometry after fullscreen. Caller restores chrome first.</summary>
    public void ExitFullscreen()
    {
        if (!IsFullscreen) return;

        IsFullscreen = false;

        if (_fullscreenWasFilled)
        {
            FillToWorkArea();
            return;
        }

        IsFilled = false;
        if (_window.WindowState != WindowState.Normal)
            _window.WindowState = WindowState.Normal;

        ApplyWindowBounds(ClampToWorkArea(
            IsUsableNormalBounds(_fullscreenRestoreBounds)
                ? _fullscreenRestoreBounds
                : GetDefaultNormalBounds()));
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void UpdateMaximizeCaption(Button maximizeButton)
    {
        // Segoe MDL2 Assets: ChromeRestore / ChromeMaximize
        maximizeButton.Content = IsFilled ? "\uE923" : "\uE922";
        maximizeButton.ToolTip = IsFilled
            ? "Свернуть в окно"
            : "Развернуть";
    }

    private void RememberNormalBounds()
    {
        if (TryGetNormalBounds(out var bounds))
            _restoreBounds = bounds;
        else if (!IsUsableNormalBounds(_restoreBounds))
            _restoreBounds = GetDefaultNormalBounds();
    }

    private bool TryGetNormalBounds(out Rect bounds)
    {
        var w = _window.ActualWidth > 0 ? _window.ActualWidth : _window.Width;
        var h = _window.ActualHeight > 0 ? _window.ActualHeight : _window.Height;
        bounds = new Rect(_window.Left, _window.Top, w, h);
        if (!IsUsableNormalBounds(bounds))
            return false;

        var work = NativeMonitor.GetBoundsDip(_window, workArea: true);
        if (bounds.Width >= work.Width - 8 || bounds.Height >= work.Height - 8)
            return false;

        return true;
    }

    private static bool IsUsableNormalBounds(Rect bounds) =>
        bounds.Width >= 200 && bounds.Height >= 200 &&
        !double.IsNaN(bounds.Left) && !double.IsNaN(bounds.Top) &&
        !double.IsInfinity(bounds.Width) && !double.IsInfinity(bounds.Height);

    private Rect GetDefaultNormalBounds()
    {
        var work = NativeMonitor.GetBoundsDip(_window, workArea: true);
        const double w = 1100;
        const double h = 760;
        return ClampToWorkArea(new Rect(
            work.Left + Math.Max(0, (work.Width - w) / 2),
            work.Top + Math.Max(0, (work.Height - h) / 2),
            w, h));
    }

    private Rect ClampToWorkArea(Rect bounds)
    {
        var work = NativeMonitor.GetBoundsDip(_window, workArea: true);
        if (work.Width < 100 || work.Height < 100 ||
            double.IsNaN(work.Width) || double.IsNaN(work.Height))
        {
            work = SystemParameters.WorkArea;
        }

        var minW = Math.Min(_window.MinWidth, work.Width);
        var minH = Math.Min(_window.MinHeight, work.Height);
        var w = Math.Clamp(bounds.Width, minW, work.Width);
        var h = Math.Clamp(bounds.Height, minH, work.Height);
        var maxLeft = work.Left + Math.Max(0, work.Width - w);
        var maxTop = work.Top + Math.Max(0, work.Height - h);
        var left = bounds.Left;
        var top = bounds.Top;
        if (left < work.Left) left = work.Left;
        if (left > maxLeft) left = maxLeft;
        if (top < work.Top) top = work.Top;
        if (top > maxTop) top = maxTop;
        return new Rect(left, top, w, h);
    }

    private void ApplyWindowBounds(Rect bounds)
    {
        if (!IsUsableNormalBounds(bounds))
            bounds = GetDefaultNormalBounds();

        try
        {
            _window.Left = bounds.Left;
            _window.Top = bounds.Top;
            _window.Width = bounds.Width;
            _window.Height = bounds.Height;
        }
        catch
        {
            // Ignore invalid placement; next layout pass may recover.
        }

        _window.Dispatcher.BeginInvoke(() =>
        {
            if (IsFullscreen || _window.WindowState == WindowState.Maximized)
                return;
            try
            {
                _window.Left = bounds.Left;
                _window.Top = bounds.Top;
                _window.Width = bounds.Width;
                _window.Height = bounds.Height;
            }
            catch
            {
                // ignore
            }
        }, DispatcherPriority.Loaded);
    }
}
