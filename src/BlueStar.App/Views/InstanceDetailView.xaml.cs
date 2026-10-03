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

    private async Task LoadStorePageAsync(string url)
    {
        try
        {
            await EnsureWebViewAsync().ConfigureAwait(true);
            StorePageView.CoreWebView2.Navigate(url);
        }
        catch (Exception)
        {
            // A missing WebView2 runtime must not crash the instance detail view
        }
    }

    private async Task EnsureWebViewAsync()
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

        StorePageView.DefaultBackgroundColor = System.Drawing.Color.FromArgb(255, 27, 40, 56);
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
}
