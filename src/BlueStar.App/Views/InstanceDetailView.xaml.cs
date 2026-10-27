using System.ComponentModel;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using BlueStar.App.ViewModels;
using Microsoft.Web.WebView2.Core;

namespace BlueStar.App.Views;

/// <summary>
/// Code-behind for InstanceDetailView.
/// </summary>
public partial class InstanceDetailView : UserControl
{
    private bool _webViewReady;
    private InstanceDetailViewModel? _viewModel;

    public InstanceDetailView()
    {
        InitializeComponent();

        DataContextChanged += OnDataContextChanged;
        Unloaded += OnUnloaded;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_viewModel != null)
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            if (_viewModel.ModsTab != null)
            {
                _viewModel.ModsTab.OnNavigateModPage -= OnNavigateModPageRequested;
            }
        }

        _viewModel = e.NewValue as InstanceDetailViewModel;

        if (_viewModel != null)
        {
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
            if (_viewModel.ModsTab != null)
            {
                _viewModel.ModsTab.OnNavigateModPage += OnNavigateModPageRequested;
            }
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_viewModel != null)
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            if (_viewModel.ModsTab != null)
            {
                _viewModel.ModsTab.OnNavigateModPage -= OnNavigateModPageRequested;
            }
        }
        _viewModel = null;
    }

    private void OnNavigateModPageRequested(string url)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.InvokeAsync(() => OnNavigateModPageRequested(url));
            return;
        }

        _ = LoadModPageAsync(url);
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.InvokeAsync(() => OnViewModelPropertyChanged(sender, e));
            return;
        }

        if (e.PropertyName != nameof(InstanceDetailViewModel.StorePageUrl)) return;

        var url = _viewModel?.StorePageUrl;
        if (string.IsNullOrWhiteSpace(url)) return;

        _ = LoadStorePageAsync(url);
    }

    private string? _pendingStoreUrl;

    private async Task LoadStorePageAsync(string url)
    {
        _pendingStoreUrl = url;
        try
        {
            if (!Services.WebView2AvailabilityService.IsAvailable())
            {
                ShowWebViewFallback();
                return;
            }

            await EnsureWebViewAsync().ConfigureAwait(true);
            WebViewFallbackOverlay.Visibility = Visibility.Collapsed;
            StorePageView.Visibility = Visibility.Visible;

            if (StorePageView.CoreWebView2 != null)
            {
                StorePageView.CoreWebView2.Navigate(url);
            }
        }
        catch (Exception)
        {
            ShowWebViewFallback();
        }
    }

    private void ShowWebViewFallback()
    {
        StorePageView.Visibility = Visibility.Collapsed;
        WebViewFallbackOverlay.Visibility = Visibility.Visible;
    }

    private void OnInstallWebView2Clicked(object sender, RoutedEventArgs e)
    {
        Services.WebView2AvailabilityService.OpenOfficialDownloadPage();
    }

    private void OnOpenInBrowserClicked(object sender, RoutedEventArgs e)
    {
        var target = _pendingStoreUrl ?? (_viewModel?.Instance != null && _viewModel.Instance.AppId > 0 ? $"https://store.steampowered.com/app/{_viewModel.Instance.AppId}" : null);
        if (!string.IsNullOrWhiteSpace(target))
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(target) { UseShellExecute = true });
            }
            catch { }
        }
    }

    private async Task EnsureWebViewAsync()
    {
        if (_webViewReady) return;

        var profileFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "BlueStar", "webview");

        Directory.CreateDirectory(profileFolder);

        var environment = await BlueStar.App.Services.WebView2AvailabilityService
            .CreateEnvironmentAsync(profileFolder)
            .ConfigureAwait(true);

        await StorePageView.EnsureCoreWebView2Async(environment).ConfigureAwait(true);

        StorePageView.DefaultBackgroundColor = System.Drawing.Color.FromArgb(255, 27, 40, 56);
        if (StorePageView.CoreWebView2 != null)
        {
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

            _ = Services.SteamWebSessionHelper.TrySyncSteamCookiesAsync(StorePageView.CoreWebView2);
        }

        _webViewReady = true;
    }

    private bool _modWebViewReady;

    private async Task LoadModPageAsync(string url)
    {
        try
        {
            if (!Services.WebView2AvailabilityService.IsAvailable())
            {
                ShowModWebViewFallback();
                return;
            }

            await EnsureModWebViewAsync().ConfigureAwait(true);
            ModWebViewFallbackOverlay.Visibility = Visibility.Collapsed;
            ModPageView.Visibility = Visibility.Visible;

            if (ModPageView.CoreWebView2 != null)
            {
                ModPageView.CoreWebView2.Navigate(url);
            }
        }
        catch (Exception)
        {
            ShowModWebViewFallback();
        }
    }

    private void ShowModWebViewFallback()
    {
        ModPageView.Visibility = Visibility.Collapsed;
        ModWebViewFallbackOverlay.Visibility = Visibility.Visible;
    }

    private async Task EnsureModWebViewAsync()
    {
        if (_modWebViewReady) return;

        var profileFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "BlueStar", "webview_mods");

        Directory.CreateDirectory(profileFolder);

        var environment = await BlueStar.App.Services.WebView2AvailabilityService
            .CreateEnvironmentAsync(profileFolder)
            .ConfigureAwait(true);

        await ModPageView.EnsureCoreWebView2Async(environment).ConfigureAwait(true);

        ModPageView.DefaultBackgroundColor = System.Drawing.Color.FromArgb(255, 18, 24, 36);
        if (ModPageView.CoreWebView2 != null)
        {
            ModPageView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            ModPageView.CoreWebView2.Settings.IsStatusBarEnabled = false;
            ModPageView.CoreWebView2.NewWindowRequested += (s, args) =>
            {
                args.Handled = true;
                if (!string.IsNullOrWhiteSpace(args.Uri))
                {
                    ModPageView.CoreWebView2.Navigate(args.Uri);
                }
            };
        }

        _modWebViewReady = true;
    }

    private async void DepotDropZone_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            var files = (string[])e.Data.GetData(DataFormats.FileDrop);
            if (files != null && files.Length > 0 && DataContext is InstanceDetailViewModel vm)
            {
                var file = files[0];
                if (file.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ||
                    file.EndsWith(".manifest", StringComparison.OrdinalIgnoreCase))
                {
                    await vm.ImportDepotZipPackageAsync(file);
                }
            }
        }
    }

    private void DepotDropZone_DragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.Copy;
            e.Handled = true;
        }
    }

    private static ModsTabViewModel? ResolveModsTabVm(object sender, object? dataContext)
    {
        if (dataContext is InstanceDetailViewModel vm) return vm.ModsTab;
        if ((sender as FrameworkElement)?.DataContext is ModsTabViewModel tab) return tab;
        return null;
    }

    private void OnModsTabDragEnter(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.Copy;
            var tab = ResolveModsTabVm(sender, DataContext);
            if (tab != null) tab.IsDraggingFilesOver = true;
            e.Handled = true;
        }
        else
        {
            e.Effects = DragDropEffects.None;
        }
    }

    private void OnModsTabDragLeave(object sender, DragEventArgs e)
    {
        var tab = ResolveModsTabVm(sender, DataContext);
        if (tab != null) tab.IsDraggingFilesOver = false;
        e.Handled = true;
    }

    private void OnModsTabDragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.Copy;
            var tab = ResolveModsTabVm(sender, DataContext);
            if (tab != null) tab.IsDraggingFilesOver = true;
            e.Handled = true;
        }
        else
        {
            e.Effects = DragDropEffects.None;
        }
    }

    private async void OnModsTabDrop(object sender, DragEventArgs e)
    {
        var tab = ResolveModsTabVm(sender, DataContext);
        if (tab != null) tab.IsDraggingFilesOver = false;

        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;

        if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
        {
            if (tab != null)
            {
                await tab.InstallDroppedFilesAsync(files);
            }
        }
    }

    private void OnModpackShelfPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is ScrollViewer sv)
        {
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
            {
                sv.ScrollToHorizontalOffset(sv.HorizontalOffset - e.Delta);
                e.Handled = true;
            }
            else
            {
                // Seamlessly bubble vertical mouse wheel to parent vertical scroll viewer
                e.Handled = true;
                var parent = FindVisualParent<ScrollViewer>(sv);
                parent?.ScrollToVerticalOffset(parent.VerticalOffset - (e.Delta * 0.75));
            }
        }
    }

    private static T? FindVisualParent<T>(DependencyObject child) where T : DependencyObject
    {
        var current = VisualTreeHelper.GetParent(child);
        while (current != null && current is not T)
        {
            current = VisualTreeHelper.GetParent(current);
        }
        return current as T;
    }
}
