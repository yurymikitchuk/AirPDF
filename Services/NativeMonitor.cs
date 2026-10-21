using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace AirPDF.Services;

/// <summary>
/// Monitor work/full bounds and WM_GETMINMAXINFO so borderless Maximized
/// stays on the work area (taskbar remains visible).
/// </summary>
internal static class NativeMonitor
{
    private const int WmGetMinMaxInfo = 0x0024;
    private const int MonitorDefaultToNearest = 0x00000002;

    public static void HookWorkAreaMaximize(Window window)
    {
        window.SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            var source = HwndSource.FromHwnd(hwnd);
            source?.AddHook(WndProc);
        };
    }

    /// <summary>Current monitor bounds in WPF DIPs (work area or full monitor).</summary>
    public static Rect GetBoundsDip(Window window, bool workArea)
    {
        var hwnd = new WindowInteropHelper(window).EnsureHandle();
        var monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
        if (monitor == IntPtr.Zero)
            return workArea ? SystemParameters.WorkArea : new Rect(0, 0, SystemParameters.PrimaryScreenWidth, SystemParameters.PrimaryScreenHeight);

        var info = new MonitorInfo { cbSize = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info))
            return workArea ? SystemParameters.WorkArea : new Rect(0, 0, SystemParameters.PrimaryScreenWidth, SystemParameters.PrimaryScreenHeight);

        var native = workArea ? info.rcWork : info.rcMonitor;
        var source = HwndSource.FromHwnd(hwnd);
        if (source?.CompositionTarget is null)
        {
            return new Rect(native.Left, native.Top,
                native.Right - native.Left, native.Bottom - native.Top);
        }

        var toDip = source.CompositionTarget.TransformFromDevice;
        var topLeft = toDip.Transform(new Point(native.Left, native.Top));
        var bottomRight = toDip.Transform(new Point(native.Right, native.Bottom));
        return new Rect(topLeft, bottomRight);
    }

    private static IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WmGetMinMaxInfo)
            return IntPtr.Zero;

        var mmi = Marshal.PtrToStructure<MinMaxInfo>(lParam);
        var monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
        if (monitor != IntPtr.Zero)
        {
            var info = new MonitorInfo { cbSize = Marshal.SizeOf<MonitorInfo>() };
            if (GetMonitorInfo(monitor, ref info))
            {
                var work = info.rcWork;
                var monitorRect = info.rcMonitor;
                var workW = Math.Abs(work.Right - work.Left);
                var workH = Math.Abs(work.Bottom - work.Top);
                var monW = Math.Abs(monitorRect.Right - monitorRect.Left);
                var monH = Math.Abs(monitorRect.Bottom - monitorRect.Top);

                mmi.ptMaxPosition.X = Math.Abs(work.Left - monitorRect.Left);
                mmi.ptMaxPosition.Y = Math.Abs(work.Top - monitorRect.Top);
                // Maximized → exact work area (flush with the taskbar, no gap).
                mmi.ptMaxSize.X = Math.Max(1, workW);
                mmi.ptMaxSize.Y = Math.Max(1, workH);
                // Manual / F11 sizing may cover the full monitor — must not be capped to work area
                // or WPF can throw / tear down the HWND when assigning full-screen Height.
                mmi.ptMaxTrackSize.X = Math.Max(mmi.ptMaxTrackSize.X, monW);
                mmi.ptMaxTrackSize.Y = Math.Max(mmi.ptMaxTrackSize.Y, monH);
                mmi.ptMinTrackSize.X = Math.Max(0, mmi.ptMinTrackSize.X);
                mmi.ptMinTrackSize.Y = Math.Max(0, mmi.ptMinTrackSize.Y);
            }
        }

        Marshal.StructureToPtr(mmi, lParam, fDeleteOld: false);
        handled = true;
        return IntPtr.Zero;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, int flags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MonitorInfo lpmi);

    [StructLayout(LayoutKind.Sequential)]
    private struct PointApi
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public PointApi ptReserved;
        public PointApi ptMaxSize;
        public PointApi ptMaxPosition;
        public PointApi ptMinTrackSize;
        public PointApi ptMaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RectApi
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MonitorInfo
    {
        public int cbSize;
        public RectApi rcMonitor;
        public RectApi rcWork;
        public int dwFlags;
    }
}
