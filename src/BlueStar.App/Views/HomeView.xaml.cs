using System;
using System.Windows;
using System.Windows.Controls;

namespace BlueStar.App.Views;

public partial class HomeView : UserControl
{
    public HomeView()
    {
        InitializeComponent();
    }

    private System.Windows.Threading.DispatcherTimer? _scrollTimer;
    private double _scrollStart;
    private double _scrollTarget;
    private DateTime _scrollStartTime;
    private const double ScrollDurationMs = 300.0;

    private void SmoothScrollTo(ScrollViewer scrollViewer, double targetOffset)
    {
        _scrollTimer?.Stop();

        _scrollStart = scrollViewer.HorizontalOffset;
        _scrollTarget = Math.Max(0, Math.Min(scrollViewer.ScrollableWidth, targetOffset));
        _scrollStartTime = DateTime.UtcNow;

        if (Math.Abs(_scrollTarget - _scrollStart) < 1.0)
        {
            scrollViewer.ScrollToHorizontalOffset(_scrollTarget);
            return;
        }

        _scrollTimer = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(16)
        };

        _scrollTimer.Tick += (s, args) =>
        {
            var elapsed = (DateTime.UtcNow - _scrollStartTime).TotalMilliseconds;
            var progress = Math.Min(1.0, elapsed / ScrollDurationMs);

            // Cubic Ease-Out curve for natural, modern inertia feel
            var eased = 1.0 - Math.Pow(1.0 - progress, 3);
            var current = _scrollStart + (_scrollTarget - _scrollStart) * eased;

            scrollViewer.ScrollToHorizontalOffset(current);

            if (progress >= 1.0)
            {
                _scrollTimer?.Stop();
                _scrollTimer = null;
                scrollViewer.ScrollToHorizontalOffset(_scrollTarget);
            }
        };

        _scrollTimer.Start();
    }

    private void OnScrollLeftClick(object sender, RoutedEventArgs e)
    {
        if (RecentInstancesScrollViewer != null)
        {
            var target = RecentInstancesScrollViewer.HorizontalOffset - 330;
            SmoothScrollTo(RecentInstancesScrollViewer, target);
        }
    }

    private void OnScrollRightClick(object sender, RoutedEventArgs e)
    {
        if (RecentInstancesScrollViewer != null)
        {
            var target = RecentInstancesScrollViewer.HorizontalOffset + 330;
            SmoothScrollTo(RecentInstancesScrollViewer, target);
        }
    }

    private async void OnZipDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            var files = (string[])e.Data.GetData(DataFormats.FileDrop);
            if (files != null && files.Length > 0 && files[0].EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                if (DataContext is ViewModels.HomeViewModel vm)
                {
                    await vm.LoadZipFileAsync(files[0]);
                }
            }
        }
    }

    private async void OnFolderDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            var files = (string[])e.Data.GetData(DataFormats.FileDrop);
            if (files != null && files.Length > 0 && System.IO.Directory.Exists(files[0]))
            {
                if (DataContext is ViewModels.HomeViewModel vm)
                {
                    await vm.LoadFolderAsync(files[0]);
                }
            }
        }
    }
}
