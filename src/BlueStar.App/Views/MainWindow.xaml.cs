using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Extensions.DependencyInjection;

namespace BlueStar.App.Views;

/// <summary>
/// Main application window with custom chrome, taskbar-aware maximization, and sidebar navigation.
/// </summary>
public partial class MainWindow : Window
{
    private const int WM_GETMINMAXINFO = 0x0024;
    private const uint MONITOR_DEFAULTTONEAREST = 0x00000002;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MINMAXINFO
    {
        public POINT ptReserved;
        public POINT ptMaxSize;
        public POINT ptMaxPosition;
        public POINT ptMinTrackSize;
        public POINT ptMaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private class MONITORINFO
    {
        public int cbSize = Marshal.SizeOf(typeof(MONITORINFO));
        public RECT rcMonitor = new RECT();
        public RECT rcWork = new RECT();
        public int dwFlags = 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, [In, Out] MONITORINFO lpmi);

    public MainWindow()
    {
        InitializeComponent();
        if (App.Services != null)
        {
            DataContext = App.Services.GetRequiredService<ViewModels.MainViewModel>();
        }
    }


    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var handle = new WindowInteropHelper(this).Handle;
        var hwndSource = HwndSource.FromHwnd(handle);
        hwndSource?.AddHook(WndProc);

        ApplyAdaptiveWindowSize(handle);
    }

    private void ApplyAdaptiveWindowSize(IntPtr hwnd)
    {
        try
        {
            var monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
            if (monitor != IntPtr.Zero)
            {
                var monitorInfo = new MONITORINFO();
                GetMonitorInfo(monitor, monitorInfo);
                RECT rcWork = monitorInfo.rcWork;

                var source = HwndSource.FromHwnd(hwnd);
                double dpiX = source?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
                double dpiY = source?.CompositionTarget?.TransformToDevice.M22 ?? 1.0;

                double workAreaWidth = (rcWork.Right - rcWork.Left) / dpiX;
                double workAreaHeight = (rcWork.Bottom - rcWork.Top) / dpiY;
                double workAreaLeft = rcWork.Left / dpiX;
                double workAreaTop = rcWork.Top / dpiY;

                // Compute adaptive dimensions (ideal 82% width, 85% height, bounded within MinWidth/MinHeight and max limits)
                double targetWidth = Math.Clamp(workAreaWidth * 0.82, MinWidth, 1440);
                double targetHeight = Math.Clamp(workAreaHeight * 0.85, MinHeight, 920);

                if (targetWidth > workAreaWidth * 0.95) targetWidth = Math.Max(MinWidth, workAreaWidth * 0.95);
                if (targetHeight > workAreaHeight * 0.95) targetHeight = Math.Max(MinHeight, workAreaHeight * 0.95);

                Width = targetWidth;
                Height = targetHeight;
                Left = workAreaLeft + (workAreaWidth - targetWidth) / 2.0;
                Top = workAreaTop + (workAreaHeight - targetHeight) / 2.0;
            }
        }
        catch { }
    }

    protected override void OnPreviewMouseDown(System.Windows.Input.MouseButtonEventArgs e)
    {
        base.OnPreviewMouseDown(e);

        if (e.ChangedButton == System.Windows.Input.MouseButton.XButton1)
        {
            if (DataContext is ViewModels.MainViewModel vm && vm.CanGoBack)
            {
                vm.GoBack();
                e.Handled = true;
            }
        }
        else if (e.ChangedButton == System.Windows.Input.MouseButton.XButton2)
        {
            if (DataContext is ViewModels.MainViewModel vm && vm.CanGoForward)
            {
                vm.GoForward();
                e.Handled = true;
            }
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_GETMINMAXINFO)
        {
            WmGetMinMaxInfo(hwnd, lParam);
            handled = true;
        }
        return IntPtr.Zero;
    }

    private void WmGetMinMaxInfo(IntPtr hwnd, IntPtr lParam)
    {
        var mmi = (MINMAXINFO)Marshal.PtrToStructure(lParam, typeof(MINMAXINFO))!;
        var monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        if (monitor != IntPtr.Zero)
        {
            var monitorInfo = new MONITORINFO();
            GetMonitorInfo(monitor, monitorInfo);
            RECT rcWork = monitorInfo.rcWork;
            RECT rcMonitor = monitorInfo.rcMonitor;

            mmi.ptMaxPosition.X = Math.Abs(rcWork.Left - rcMonitor.Left);
            mmi.ptMaxPosition.Y = Math.Abs(rcWork.Top - rcMonitor.Top);
            mmi.ptMaxSize.X = Math.Abs(rcWork.Right - rcWork.Left);
            mmi.ptMaxSize.Y = Math.Abs(rcWork.Bottom - rcWork.Top);
        }

        var source = HwndSource.FromHwnd(hwnd);
        double dpiX = source?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
        double dpiY = source?.CompositionTarget?.TransformToDevice.M22 ?? 1.0;

        int minWidthPx = (int)(MinWidth * dpiX);
        int minHeightPx = (int)(MinHeight * dpiY);

        if (minWidthPx > 0) mmi.ptMinTrackSize.X = minWidthPx;
        if (minHeightPx > 0) mmi.ptMinTrackSize.Y = minHeightPx;

        Marshal.StructureToPtr(mmi, lParam, true);
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e)
        => WindowState = WindowState.Minimized;

    private void MaximizeButton_Click(object sender, RoutedEventArgs e)
        => WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;

    private void CloseButton_Click(object sender, RoutedEventArgs e)
        => Close();
}
