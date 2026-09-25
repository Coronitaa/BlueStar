using System;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using BlueStar.App.ViewModels;
using BlueStar.Core.Models;
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
    private const double LoadMoreThreshold = 250d;

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

        UpdateLoadingOverlay(_viewModel?.IsFirstLoad ?? false, immediate: false);
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

        if (_viewModel != null)
        {
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
            UpdateLoadingOverlay(_viewModel.IsFirstLoad, immediate: false);
        }
    }

    // ── Results scrolling ────────────────────────────────────────────────

    private void OnResultsScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (_viewModel is null) return;

        // Only trigger when user is scrolling downward and there is actual scrollable content
        if (e.VerticalChange <= 0 || ResultsScroller.ScrollableHeight <= 0) return;

        var remaining = ResultsScroller.ScrollableHeight - ResultsScroller.VerticalOffset;
        if (remaining > LoadMoreThreshold) return;

        if (!_viewModel.HasMoreResults || _viewModel.IsLoadingMore || _viewModel.IsSearching) return;

        var command = _viewModel.LoadMoreCommand;
        if (command.CanExecute(null)) command.Execute(null);
    }

    // ── Embedded store page ──────────────────────────────────────────────

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        => Attach(e.NewValue as BrowseViewModel);

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.InvokeAsync(() => OnViewModelPropertyChanged(sender, e));
            return;
        }

        if (e.PropertyName == nameof(BrowseViewModel.IsFirstLoad))
        {
            UpdateLoadingOverlay(_viewModel?.IsFirstLoad ?? false);
            return;
        }

        if ((e.PropertyName == nameof(BrowseViewModel.SearchState) && _viewModel?.SearchState == SearchState.LoadingInitial)
            || (e.PropertyName == nameof(BrowseViewModel.Results) && _viewModel?.CurrentPage == 1))
        {
            try
            {
                ResultsScroller.ScrollToTop();
            }
            catch { }
            return;
        }

        if (e.PropertyName != nameof(BrowseViewModel.DetailUrl)) return;

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

        var profileFolder = System.IO.Path.Combine(
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

    // ── Explore Loading Overlay Control ──────────────────────────────────

    private void UpdateLoadingOverlay(bool isLoading, bool immediate = false)
    {
        if (ExploreLoadingOverlay == null) return;

        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.InvokeAsync(() => UpdateLoadingOverlay(isLoading, immediate));
            return;
        }

        if (isLoading)
        {
            ExploreLoadingOverlay.BeginAnimation(UIElement.OpacityProperty, null);
            ExploreLoadingOverlay.Opacity = 1.0;
            ExploreLoadingOverlay.Visibility = Visibility.Visible;
            ExploreLoadingOverlay.IsHitTestVisible = true;
        }
        else
        {
            if (immediate || ExploreLoadingOverlay.Visibility != Visibility.Visible)
            {
                ExploreLoadingOverlay.BeginAnimation(UIElement.OpacityProperty, null);
                ExploreLoadingOverlay.Opacity = 0.0;
                ExploreLoadingOverlay.Visibility = Visibility.Collapsed;
                ExploreLoadingOverlay.IsHitTestVisible = false;
                return;
            }

            var fadeOut = new DoubleAnimation(1.0, 0.0, new Duration(TimeSpan.FromMilliseconds(350)))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            fadeOut.Completed += (s, e) =>
            {
                ExploreLoadingOverlay.Visibility = Visibility.Collapsed;
                ExploreLoadingOverlay.IsHitTestVisible = false;
            };
            ExploreLoadingOverlay.BeginAnimation(UIElement.OpacityProperty, fadeOut);
        }
    }

    // ── Random Pick Dice Animations ──────────────────────────────────────

    private async void OnDiceButtonClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn) return;

        var diceJump = btn.Template?.FindName("DiceJump", btn) as TranslateTransform;
        var diceSpin = btn.Template?.FindName("DiceSpin", btn) as RotateTransform;
        var diceRing = btn.Template?.FindName("DiceRing", btn) as Ellipse;
        var diceRingScale = btn.Template?.FindName("DiceRingScale", btn) as ScaleTransform;

        // 1. Jump bounce: 0 -> -13px -> 0
        if (diceJump != null)
        {
            var jumpAnim = new DoubleAnimationUsingKeyFrames();
            jumpAnim.KeyFrames.Add(new SplineDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            jumpAnim.KeyFrames.Add(new SplineDoubleKeyFrame(-13, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(120)), new KeySpline(0.2, 0.8, 0.4, 1.0)));
            jumpAnim.KeyFrames.Add(new SplineDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(350)), new KeySpline(0.4, 0.0, 0.6, 1.0)));
            jumpAnim.FillBehavior = FillBehavior.Stop;
            diceJump.BeginAnimation(TranslateTransform.YProperty, jumpAnim, HandoffBehavior.SnapshotAndReplace);
        }

        // 2. 360-degree spin
        if (diceSpin != null)
        {
            var spinAnim = new DoubleAnimation(0, 360, new Duration(TimeSpan.FromMilliseconds(420)))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut },
                FillBehavior = FillBehavior.Stop
            };
            diceSpin.BeginAnimation(RotateTransform.AngleProperty, spinAnim, HandoffBehavior.SnapshotAndReplace);
        }

        // 3. Subtle ring expansion and fade
        if (diceRing != null && diceRingScale != null)
        {
            var ringOpacity = new DoubleAnimation(0.65, 0.0, new Duration(TimeSpan.FromMilliseconds(400)))
            {
                FillBehavior = FillBehavior.Stop
            };
            var ringScale = new DoubleAnimation(0.6, 2.0, new Duration(TimeSpan.FromMilliseconds(400)))
            {
                FillBehavior = FillBehavior.Stop
            };
            diceRing.BeginAnimation(UIElement.OpacityProperty, ringOpacity, HandoffBehavior.SnapshotAndReplace);
            diceRingScale.BeginAnimation(ScaleTransform.ScaleXProperty, ringScale, HandoffBehavior.SnapshotAndReplace);
            diceRingScale.BeginAnimation(ScaleTransform.ScaleYProperty, ringScale, HandoffBehavior.SnapshotAndReplace);
        }

        // 4. Subtle pastel particle dots
        AnimateParticleDot(btn, "Dot1", "Dot1Trans", -7, -8);
        AnimateParticleDot(btn, "Dot2", "Dot2Trans", 8, -7);
        AnimateParticleDot(btn, "Dot3", "Dot3Trans", 7, 7);
        AnimateParticleDot(btn, "Dot4", "Dot4Trans", -7, 6);

        // 5. Trigger random selection smoothly at the apex of the jump (180ms)
        // so UI layout and search dispatch do not starve the initial animation frames
        if (btn.DataContext is FilterGroupViewModel groupVm)
        {
            try
            {
                await System.Threading.Tasks.Task.Delay(180);
                groupVm.PickRandom();
            }
            catch { }
        }
    }

    private static void AnimateParticleDot(Button btn, string dotName, string transName, double targetX, double targetY)
    {
        if (btn.Template?.FindName(dotName, btn) is Ellipse dot &&
            btn.Template?.FindName(transName, btn) is TranslateTransform trans)
        {
            var dotFade = new DoubleAnimation(0.9, 0.0, new Duration(TimeSpan.FromMilliseconds(380)))
            {
                FillBehavior = FillBehavior.Stop
            };
            var dotX = new DoubleAnimation(0, targetX, new Duration(TimeSpan.FromMilliseconds(380)))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                FillBehavior = FillBehavior.Stop
            };
            var dotY = new DoubleAnimation(0, targetY, new Duration(TimeSpan.FromMilliseconds(380)))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                FillBehavior = FillBehavior.Stop
            };

            dot.BeginAnimation(UIElement.OpacityProperty, dotFade, HandoffBehavior.SnapshotAndReplace);
            trans.BeginAnimation(TranslateTransform.XProperty, dotX, HandoffBehavior.SnapshotAndReplace);
            trans.BeginAnimation(TranslateTransform.YProperty, dotY, HandoffBehavior.SnapshotAndReplace);
        }
    }

    private void OnDiceButtonMouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is Button btn && btn.Template?.FindName("DiceTilt", btn) is RotateTransform tilt)
        {
            var anim = new DoubleAnimation(-14, new Duration(TimeSpan.FromMilliseconds(180)))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                FillBehavior = FillBehavior.HoldEnd
            };
            tilt.BeginAnimation(RotateTransform.AngleProperty, anim, HandoffBehavior.SnapshotAndReplace);
        }
    }

    private void OnDiceButtonMouseLeave(object sender, MouseEventArgs e)
    {
        if (sender is Button btn && btn.Template?.FindName("DiceTilt", btn) is RotateTransform tilt)
        {
            var anim = new DoubleAnimation(0, new Duration(TimeSpan.FromMilliseconds(180)))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                FillBehavior = FillBehavior.HoldEnd
            };
            tilt.BeginAnimation(RotateTransform.AngleProperty, anim, HandoffBehavior.SnapshotAndReplace);
        }
    }
}
