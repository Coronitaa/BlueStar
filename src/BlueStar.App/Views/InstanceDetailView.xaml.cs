using System.ComponentModel;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
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
        if (_viewModel != null) _viewModel.PropertyChanged -= OnViewModelPropertyChanged;

        _viewModel = e.NewValue as InstanceDetailViewModel;

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

    private void OnModsTabDragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.Copy;
            e.Handled = true;
        }
        else
        {
            e.Effects = DragDropEffects.None;
        }
    }

    private async void OnModsTabDrop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;

        if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
        {
            if (DataContext is InstanceDetailViewModel vm)
            {
                await vm.ModsTab.InstallDroppedFilesAsync(files);
            }
            else if ((sender as FrameworkElement)?.DataContext is ModsTabViewModel modsTab)
            {
                await modsTab.InstallDroppedFilesAsync(files);
            }
        }
    }
}
