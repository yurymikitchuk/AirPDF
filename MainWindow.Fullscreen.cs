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
    private void RotatePage(double delta)
    {
        if (!_renderer.IsOpen) return;
        var next = (_pageRotate.Angle + delta) % 360;
        if (next < 0) next += 360;
        _pageRotate.Angle = next;
        ClearTextSelection();
    }

    private async void ToggleFullscreen()
    {
        if (_fullscreenBusy) return;
        _fullscreenBusy = true;
        var refitVersion = ++_fullscreenRefitVersion;
        try
        {
            // Drain in-flight page/thumb/prefetch before resizing the window.
            await PauseBackgroundRasterAsync();

            if (_placement.IsFullscreen)
                ExitFullscreenUi();
            else
                EnterFullscreenUi();

            await RefitPageAfterChromeChangeAsync(refitVersion);
        }
        catch (Exception ex)
        {
            try
            {
                if (_placement.IsFullscreen)
                    _placement.ExitFullscreen();
            }
            catch { /* ignore */ }

            try { Toolbar.Visibility = Visibility.Visible; } catch { /* ignore */ }
            MessageBox.Show(this, ex.Message, "Полноэкранный режим",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            ResumeBackgroundRaster();
            _fullscreenBusy = false;
        }
    }

    private void EnterFullscreenUi()
    {
        if (_placement.IsFullscreen) return;

        _toolbarPrevVisibility = Toolbar.Visibility;
        Toolbar.Visibility = Visibility.Collapsed;
        _sidebarOpenBeforeFullscreen = _sidebarPreferredOpen;
        ApplySidebarVisibility(false, persist: false);
        _placement.EnterFullscreen();
        UpdateMaximizeCaption();
    }

    private void ExitFullscreenUi()
    {
        if (!_placement.IsFullscreen) return;

        Toolbar.Visibility = _toolbarPrevVisibility;
        if (_renderer.IsOpen)
            ApplySidebarVisibility(_sidebarOpenBeforeFullscreen, persist: false);
        _placement.ExitFullscreen();
        UpdateMaximizeCaption();
    }

    private async Task RefitPageAfterChromeChangeAsync(int refitVersion = 0)
    {
        if (!_renderer.IsOpen)
            return;

        // Wait for ActualWidth/Height after chrome/size change, then fit whole page.
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Loaded);
        if (refitVersion != 0 && refitVersion != _fullscreenRefitVersion)
            return;
        if (!IsLoaded || !IsVisible)
            return;

        UpdateLayout();
        await FitToViewportAsync(PageFitMode.Page);
    }
}
