using System;
using System.ComponentModel;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using BlueStar.App.ViewModels;

namespace BlueStar.App.Views;

/// <summary>
/// Embedded Steam Workshop browser. It shares the WebView2 profile of the Explore store panel, so signing in
/// to Steam once is remembered here too, and reports every page it lands on to <see cref="WorkshopBrowserViewModel"/>.
/// </summary>
public partial class WorkshopBrowserPanel : UserControl
{
    private WorkshopBrowserViewModel? _viewModel;
    private Task? _webViewInit;

    public WorkshopBrowserPanel()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        IsVisibleChanged += OnIsVisibleChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_viewModel != null) _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _viewModel = e.NewValue as WorkshopBrowserViewModel;
        if (_viewModel != null) _viewModel.PropertyChanged += OnViewModelPropertyChanged;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(WorkshopBrowserViewModel.StartUrl) && IsVisible)
        {
            _ = NavigateAsync(_viewModel?.StartUrl);
        }
    }

    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is true)
        {
            _ = NavigateAsync(_viewModel?.StartUrl);
        }
    }

    private async Task NavigateAsync(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;

        try
        {
            if (!BlueStar.App.Services.WebView2AvailabilityService.IsAvailable())
            {
                ShowFallback();
                return;
            }

            _webViewInit ??= InitializeWebViewAsync();
            await _webViewInit.ConfigureAwait(true);

            if (WorkshopView.CoreWebView2 != null &&
                !string.Equals(WorkshopView.CoreWebView2.Source, url, StringComparison.OrdinalIgnoreCase))
            {
                WorkshopView.CoreWebView2.Navigate(url);
            }
        }
        catch (Exception)
        {
            _webViewInit = null;
            ShowFallback();
        }
    }

    private async Task InitializeWebViewAsync()
    {
        var profileFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "BlueStar", "webview");
        Directory.CreateDirectory(profileFolder);

        var environment = await BlueStar.App.Services.WebView2AvailabilityService
            .CreateEnvironmentAsync(profileFolder)
            .ConfigureAwait(true);
        await WorkshopView.EnsureCoreWebView2Async(environment).ConfigureAwait(true);

        WorkshopView.DefaultBackgroundColor = System.Drawing.Color.FromArgb(255, 27, 40, 56);
        var core = WorkshopView.CoreWebView2;
        core.Settings.IsStatusBarEnabled = false;
        core.NewWindowRequested += (_, args) =>
        {
            // Workshop links that open a new tab stay in this panel.
            args.Handled = true;
            if (!string.IsNullOrWhiteSpace(args.Uri)) core.Navigate(args.Uri);
        };
        core.SourceChanged += (_, _) => _viewModel?.OnNavigated(core.Source);

        _ = BlueStar.App.Services.SteamWebSessionHelper.TrySyncSteamCookiesAsync(core);
    }

    private void ShowFallback()
    {
        WorkshopView.Visibility = Visibility.Collapsed;
        WebViewFallback.Visibility = Visibility.Visible;
    }

    private void OnBackClicked(object sender, RoutedEventArgs e)
    {
        if (WorkshopView.CoreWebView2?.CanGoBack == true) WorkshopView.CoreWebView2.GoBack();
    }

    private void OnForwardClicked(object sender, RoutedEventArgs e)
    {
        if (WorkshopView.CoreWebView2?.CanGoForward == true) WorkshopView.CoreWebView2.GoForward();
    }

    private void OnHomeClicked(object sender, RoutedEventArgs e)
    {
        var home = _viewModel?.StartUrl;
        if (!string.IsNullOrWhiteSpace(home)) WorkshopView.CoreWebView2?.Navigate(home);
    }

    private void OnInstallWebViewClicked(object sender, RoutedEventArgs e)
    {
        BlueStar.App.Services.WebView2AvailabilityService.OpenOfficialDownloadPage();
    }
}
