using System;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using BlueStar.App.ViewModels;
using Microsoft.Web.WebView2.Core;

namespace BlueStar.App.Views;

/// <summary>
/// Explore: faceted search over the Steam catalog.
/// </summary>
/// <remarks>
/// Two things need code rather than markup: the results list asks for the next page as its
/// bottom comes into view, and the detail panel hosts a WebView2 whose profile lives in the
/// BlueStar data folder so a Steam sign-in made inside the app is remembered next time.
/// </remarks>
public partial class BrowseView : UserControl
{
    /// <summary>How close to the bottom the list gets before the next page is asked for.</summary>
    private const double LoadMoreThreshold = 600d;

    private bool _webViewReady;
    private BrowseViewModel? _viewModel;

    /// <summary>
    /// Initializes a new instance of the <see cref="BrowseView"/> class.
    /// </summary>
    public BrowseView()
    {
        InitializeComponent();

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        DataContextChanged += OnDataContextChanged;
    }

    /// <remarks>
    /// This page is built once and shown again on every visit to Explore, so loading has to be
    /// idempotent: the handlers are detached before being attached, and the ViewModel is picked
    /// up here as well as from <see cref="OnDataContextChanged"/>, which only fires the first
    /// time round.
    /// </remarks>
    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ResultsScroller.ScrollChanged -= OnResultsScrollChanged;
        ResultsScroller.ScrollChanged += OnResultsScrollChanged;

        Attach(DataContext as BrowseViewModel);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        ResultsScroller.ScrollChanged -= OnResultsScrollChanged;

        // The ViewModel outlives the page, so its subscription is left in place: dropping it
        // here would mean the store panel stopped opening after the first visit.
    }

    private void Attach(BrowseViewModel? viewModel)
    {
        if (ReferenceEquals(_viewModel, viewModel)) return;

        if (_viewModel != null) _viewModel.PropertyChanged -= OnViewModelPropertyChanged;

        _viewModel = viewModel;

        if (_viewModel != null) _viewModel.PropertyChanged += OnViewModelPropertyChanged;
    }

    // ── Results scrolling ────────────────────────────────────────────────

    private void OnResultsScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (_viewModel is null) return;

        var remaining = ResultsScroller.ScrollableHeight - ResultsScroller.VerticalOffset;
        if (remaining > LoadMoreThreshold) return;

        var command = _viewModel.LoadMoreCommand;
        if (command.CanExecute(null)) command.Execute(null);
    }

    // ── Embedded store page ──────────────────────────────────────────────

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        => Attach(e.NewValue as BrowseViewModel);

    private async void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(BrowseViewModel.DetailUrl)) return;

        var url = _viewModel?.DetailUrl;
        if (string.IsNullOrWhiteSpace(url)) return;

        try
        {
            await EnsureWebViewAsync().ConfigureAwait(true);
            StorePageView.Source = new Uri(url);
        }
        catch (Exception)
        {
            // A missing WebView2 runtime must not take the view down. The person can still use
            // the SteamDB button, which opens their own browser.
        }
    }

    /// <summary>
    /// Creates the WebView2 environment against a profile folder of our own.
    /// </summary>
    /// <remarks>
    /// The Steam desktop client keeps its cookies in its own store, which WebView2 cannot read,
    /// so the session the person has open in Steam cannot be inherited. What this does give
    /// them is a profile that persists: sign in to Steam once inside this panel and it is
    /// remembered from then on. Without signing in, the page loads anonymously.
    /// </remarks>
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

        _webViewReady = true;
    }
}
