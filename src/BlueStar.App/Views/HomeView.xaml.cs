using System;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;

namespace BlueStar.App.Views;

public partial class HomeView : UserControl
{
    private bool _webViewReady;
    private ViewModels.HomeViewModel? _viewModel;

    public HomeView()
    {
        InitializeComponent();

        DataContextChanged += OnDataContextChanged;
        Unloaded += OnUnloaded;
    }

    // ── Embedded store page (the same panel Explore opens) ───────────────

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_viewModel != null) _viewModel.PropertyChanged -= OnViewModelPropertyChanged;

        _viewModel = e.NewValue as ViewModels.HomeViewModel;

        if (_viewModel != null) _viewModel.PropertyChanged += OnViewModelPropertyChanged;
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_viewModel != null) _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _viewModel = null;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.InvokeAsync(() => OnViewModelPropertyChanged(sender, e));
            return;
        }

        if (e.PropertyName != nameof(ViewModels.HomeViewModel.DetailUrl)) return;

        var url = _viewModel?.DetailUrl;
        if (string.IsNullOrWhiteSpace(url)) return;

        _ = LoadStorePageAsync(url);
    }

    private async System.Threading.Tasks.Task LoadStorePageAsync(string url)
    {
        try
        {
            await EnsureWebViewAsync().ConfigureAwait(true);
            StorePageView.Source = new Uri(url);
        }
        catch (Exception)
        {
            // A missing WebView2 runtime must not take the dashboard down: the SteamDB button
            // still opens the page in the person's own browser.
        }
    }

    /// <summary>
    /// Creates the WebView2 environment against the same profile folder Explore uses, so a Steam
    /// sign-in made in either panel is remembered by both.
    /// </summary>
    private async System.Threading.Tasks.Task EnsureWebViewAsync()
    {
        if (_webViewReady) return;

        var profileFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "BlueStar", "webview");

        Directory.CreateDirectory(profileFolder);

        var environment = await CoreWebView2Environment
            .CreateAsync(userDataFolder: profileFolder)
            .ConfigureAwait(true);

        await StorePageView.EnsureCoreWebView2Async(environment).ConfigureAwait(true);

        StorePageView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
        StorePageView.CoreWebView2.Settings.IsStatusBarEnabled = false;
        StorePageView.CoreWebView2.NewWindowRequested += (s, args) =>
        {
            args.Handled = true;
            if (!string.IsNullOrWhiteSpace(args.Uri))
            {
                StorePageView.CoreWebView2.Navigate(args.Uri);
            }
        };

        _ = BlueStar.App.Services.SteamWebSessionHelper.TrySyncSteamCookiesAsync(StorePageView.CoreWebView2);

        _webViewReady = true;
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
