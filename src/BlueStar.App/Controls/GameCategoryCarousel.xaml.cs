using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using BlueStar.Core.Models;

namespace BlueStar.App.Controls;

/// <summary>
/// Custom Game Category Carousel Control:
/// - Smooth horizontal auto-scrolling
/// - Pause on mouse hover & side arrow navigation with 3-second resume delay
/// - Automatic pausing when off-screen to save CPU/GPU resources
/// - In-place vertical expansion grid mode
/// </summary>
public partial class GameCategoryCarousel : UserControl
{
    public static readonly DependencyProperty CategoryProperty =
        DependencyProperty.Register(nameof(Category), typeof(CatalogCategory), typeof(GameCategoryCarousel),
            new PropertyMetadata(null, OnCategoryChanged));

    public static readonly DependencyProperty AddToLibraryCommandProperty =
        DependencyProperty.Register(nameof(AddToLibraryCommand), typeof(ICommand), typeof(GameCategoryCarousel),
            new PropertyMetadata(null));

    public static readonly DependencyProperty OpenSteamDbCommandProperty =
        DependencyProperty.Register(nameof(OpenSteamDbCommand), typeof(ICommand), typeof(GameCategoryCarousel),
            new PropertyMetadata(null));

    /// <summary>Raised when a card itself is clicked: opens the game's store page panel.</summary>
    public static readonly DependencyProperty OpenDetailCommandProperty =
        DependencyProperty.Register(nameof(OpenDetailCommand), typeof(ICommand), typeof(GameCategoryCarousel),
            new PropertyMetadata(null));

    public static readonly DependencyProperty ViewMoreCommandProperty =
        DependencyProperty.Register(nameof(ViewMoreCommand), typeof(ICommand), typeof(GameCategoryCarousel),
            new PropertyMetadata(null));

    public static readonly DependencyProperty LoadMoreCommandProperty =
        DependencyProperty.Register(nameof(LoadMoreCommand), typeof(ICommand), typeof(GameCategoryCarousel),
            new PropertyMetadata(null));

    public static readonly DependencyProperty IsExploreTabProperty =
        DependencyProperty.Register(nameof(IsExploreTab), typeof(bool), typeof(GameCategoryCarousel),
            new PropertyMetadata(false));

    public CatalogCategory? Category
    {
        get => (CatalogCategory?)GetValue(CategoryProperty);
        set => SetValue(CategoryProperty, value);
    }

    public ICommand? AddToLibraryCommand
    {
        get => (ICommand?)GetValue(AddToLibraryCommandProperty);
        set => SetValue(AddToLibraryCommandProperty, value);
    }

    public ICommand? OpenSteamDbCommand
    {
        get => (ICommand?)GetValue(OpenSteamDbCommandProperty);
        set => SetValue(OpenSteamDbCommandProperty, value);
    }

    public ICommand? OpenDetailCommand
    {
        get => (ICommand?)GetValue(OpenDetailCommandProperty);
        set => SetValue(OpenDetailCommandProperty, value);
    }

    public ICommand? ViewMoreCommand
    {
        get => (ICommand?)GetValue(ViewMoreCommandProperty);
        set => SetValue(ViewMoreCommandProperty, value);
    }

    public ICommand? LoadMoreCommand
    {
        get => (ICommand?)GetValue(LoadMoreCommandProperty);
        set => SetValue(LoadMoreCommandProperty, value);
    }

    public bool IsExploreTab
    {
        get => (bool)GetValue(IsExploreTabProperty);
        set => SetValue(IsExploreTabProperty, value);
    }

    private readonly DispatcherTimer _scrollTimer;
    private readonly DispatcherTimer _resumeTimer;
    private bool _isPaused;
    private bool _isInsideViewport = true;
    private const double ScrollStep = 0.85; // Slow smooth ticker speed

    public GameCategoryCarousel()
    {
        InitializeComponent();

        _scrollTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(25)
        };
        _scrollTimer.Tick += OnScrollTick;

        _resumeTimer = new DispatcherTimer(DispatcherPriority.Normal)
        {
            Interval = TimeSpan.FromSeconds(3)
        };
        _resumeTimer.Tick += OnResumeTick;

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        LayoutUpdated += OnLayoutUpdated;
    }

    private static void OnCategoryChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is GameCategoryCarousel carousel)
        {
            carousel.EvaluateTimerState();
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        EvaluateTimerState();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _scrollTimer.Stop();
        _resumeTimer.Stop();
    }

    private void OnLayoutUpdated(object? sender, EventArgs e)
    {
        CheckViewportVisibility();
    }

    private void CheckViewportVisibility()
    {
        if (!IsLoaded || HorizontalScrollViewer == null) return;

        try
        {
            // Find parent window or scrollviewer to check relative visibility bounds
            var window = Window.GetWindow(this);
            if (window == null) return;

            var transform = TransformToAncestor(window);
            var bounds = transform.TransformBounds(new Rect(0, 0, RenderSize.Width, RenderSize.Height));

            var windowHeight = window.ActualHeight;
            bool isVisible = bounds.Bottom > 0 && bounds.Top < windowHeight;

            if (_isInsideViewport != isVisible)
            {
                _isInsideViewport = isVisible;
                EvaluateTimerState();
            }
        }
        catch
        {
            // Ignore transform errors during visual tree transitions
        }
    }

    private void EvaluateTimerState()
    {
        if (_scrollTimer == null) return;

        bool shouldRun = _isInsideViewport &&
                         !_isPaused &&
                         Category != null &&
                         !Category.IsExpanded &&
                         !Category.IsLoading &&
                         Category.Items.Count > 1;

        if (shouldRun && !_scrollTimer.IsEnabled)
        {
            _scrollTimer.Start();
        }
        else if (!shouldRun && _scrollTimer.IsEnabled)
        {
            _scrollTimer.Stop();
        }
    }

    private void OnScrollTick(object? sender, EventArgs e)
    {
        if (HorizontalScrollViewer == null) return;

        var sv = HorizontalScrollViewer;
        if (sv.ScrollableWidth <= 0) return;

        double next = sv.HorizontalOffset + ScrollStep;
        if (next >= sv.ScrollableWidth)
        {
            sv.ScrollToHorizontalOffset(0);
        }
        else
        {
            sv.ScrollToHorizontalOffset(next);
        }
    }

    private void OnResumeTick(object? sender, EventArgs e)
    {
        _resumeTimer.Stop();
        _isPaused = false;
        EvaluateTimerState();
    }

    private void OnCarouselMouseEnter(object sender, MouseEventArgs e)
    {
        _isPaused = true;
        _resumeTimer.Stop();
        EvaluateTimerState();
    }

    private void OnCarouselMouseLeave(object sender, MouseEventArgs e)
    {
        _resumeTimer.Stop();
        _resumeTimer.Start(); // Resume after 3 seconds
    }

    private void OnPrevClick(object sender, RoutedEventArgs e)
    {
        if (HorizontalScrollViewer == null) return;
        _isPaused = true;
        _resumeTimer.Stop();
        _resumeTimer.Start();

        var target = Math.Max(0, HorizontalScrollViewer.HorizontalOffset - 336);
        HorizontalScrollViewer.ScrollToHorizontalOffset(target);
    }

    private void OnNextClick(object sender, RoutedEventArgs e)
    {
        if (HorizontalScrollViewer == null) return;
        _isPaused = true;
        _resumeTimer.Stop();
        _resumeTimer.Start();

        var target = Math.Min(HorizontalScrollViewer.ScrollableWidth, HorizontalScrollViewer.HorizontalOffset + 336);
        HorizontalScrollViewer.ScrollToHorizontalOffset(target);
    }

    private void OnViewMoreClick(object sender, RoutedEventArgs e)
    {
        if (Category == null) return;

        if (IsExploreTab)
        {
            // In Explore tab: toggle expansion in place
            Category.IsExpanded = !Category.IsExpanded;
            EvaluateTimerState();
        }
        else
        {
            // In Home tab: navigate to Explore view and expand the selected category
            if (ViewMoreCommand != null && ViewMoreCommand.CanExecute(Category))
            {
                ViewMoreCommand.Execute(Category);
            }
            else
            {
                Category.IsExpanded = !Category.IsExpanded;
                EvaluateTimerState();
            }
        }
    }

    private void OnScrollViewerPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (!e.Handled)
        {
            e.Handled = true;
            var parentScrollViewer = FindParentScrollViewer(this);
            if (parentScrollViewer != null)
            {
                parentScrollViewer.ScrollToVerticalOffset(parentScrollViewer.VerticalOffset - (e.Delta * 0.75));
            }
            else
            {
                var eventArg = new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
                {
                    RoutedEvent = UIElement.MouseWheelEvent,
                    Source = sender
                };
                var parent = VisualTreeHelper.GetParent(this) as UIElement;
                parent?.RaiseEvent(eventArg);
            }
        }
    }

    private static ScrollViewer? FindParentScrollViewer(DependencyObject child)
    {
        var current = VisualTreeHelper.GetParent(child);
        while (current != null)
        {
            if (current is ScrollViewer sv && sv.VerticalScrollBarVisibility != ScrollBarVisibility.Disabled)
            {
                return sv;
            }
            current = VisualTreeHelper.GetParent(current);
        }
        return null;
    }
}

