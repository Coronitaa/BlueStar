using System;
using System.Collections;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using BlueStar.Core.Models;

namespace BlueStar.App.Controls;

/// <summary>
/// A horizontal tag bar that displays as many tag buttons as fit within the available width,
/// and automatically appends a "+X más" / "+X more" badge when tags overflow.
/// Clicking the overflow badge opens a context menu allowing the user to select any of the hidden tags.
/// </summary>
public class OverflowTagBar : Panel
{
    public static readonly DependencyProperty ItemsSourceProperty =
        DependencyProperty.Register(
            nameof(ItemsSource),
            typeof(IEnumerable),
            typeof(OverflowTagBar),
            new PropertyMetadata(null, OnItemsSourceChanged));

    public static readonly DependencyProperty TagCommandProperty =
        DependencyProperty.Register(
            nameof(TagCommand),
            typeof(ICommand),
            typeof(OverflowTagBar),
            new PropertyMetadata(null, OnTagCommandChanged));

    public static readonly DependencyProperty TagStyleProperty =
        DependencyProperty.Register(
            nameof(TagStyle),
            typeof(Style),
            typeof(OverflowTagBar),
            new PropertyMetadata(null, OnTagStyleChanged));

    public static readonly DependencyProperty MoreTextFormatProperty =
        DependencyProperty.Register(
            nameof(MoreTextFormat),
            typeof(string),
            typeof(OverflowTagBar),
            new PropertyMetadata("+{0} más", OnMoreTextFormatChanged));

    public IEnumerable? ItemsSource
    {
        get => (IEnumerable?)GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

    public ICommand? TagCommand
    {
        get => (ICommand?)GetValue(TagCommandProperty);
        set => SetValue(TagCommandProperty, value);
    }

    public Style? TagStyle
    {
        get => (Style?)GetValue(TagStyleProperty);
        set => SetValue(TagStyleProperty, value);
    }

    public string? MoreTextFormat
    {
        get => (string?)GetValue(MoreTextFormatProperty);
        set => SetValue(MoreTextFormatProperty, value);
    }

    private Border? _overflowBadge;
    private TextBlock? _overflowText;
    private double _lastFittedWidth;
    private readonly List<double> _cachedTagWidths = new();

    public OverflowTagBar()
    {
        ClipToBounds = true;
        Loaded += OnLoaded;
        SizeChanged += OnSizeChanged;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        EnsureStylesApplied();
        InvalidateMeasure();
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.WidthChanged && e.NewSize.Width > 0)
        {
            if (Math.Abs(e.NewSize.Width - _lastFittedWidth) > 1.0)
            {
                InvalidateMeasure();
            }
        }
    }

    private static void OnItemsSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is OverflowTagBar bar)
        {
            bar.RebuildChildren();
            bar.InvalidateMeasure();
        }
    }

    private static void OnTagCommandChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is OverflowTagBar bar && e.NewValue is ICommand cmd)
        {
            int tagCount = bar._overflowBadge != null ? bar.Children.Count - 1 : bar.Children.Count;
            for (int i = 0; i < tagCount; i++)
            {
                if (bar.Children[i] is Button b)
                    b.Command = cmd;
            }
        }
    }

    private static void OnTagStyleChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is OverflowTagBar bar && e.NewValue is Style st)
        {
            int tagCount = bar._overflowBadge != null ? bar.Children.Count - 1 : bar.Children.Count;
            for (int i = 0; i < tagCount; i++)
            {
                if (bar.Children[i] is Button b)
                    b.Style = st;
            }
        }
    }

    private static void OnMoreTextFormatChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is OverflowTagBar bar)
        {
            bar.InvalidateMeasure();
        }
    }

    private Style? ResolveTagStyle()
    {
        return TagStyle
            ?? (TryFindResource("CardTagBubble") as Style)
            ?? (Application.Current?.TryFindResource("CardTagBubble") as Style);
    }

    private void EnsureStylesApplied()
    {
        var style = ResolveTagStyle();
        if (style != null)
        {
            int tagCount = _overflowBadge != null ? Children.Count - 1 : Children.Count;
            for (int i = 0; i < tagCount; i++)
            {
                if (Children[i] is Button b && (b.Style == null || !ReferenceEquals(b.Style, style)))
                {
                    b.Style = style;
                }
            }
        }

        UpdateBadgeBrushes();
    }

    private void RebuildChildren()
    {
        _cachedTagWidths.Clear();
        Children.Clear();
        if (ItemsSource == null) return;

        var style = ResolveTagStyle();

        foreach (var item in ItemsSource)
        {
            if (item == null) continue;
            var tagRef = item as StoreTagRef;
            var btn = new Button
            {
                Style = style,
                DataContext = item,
                Content = tagRef?.DisplayName ?? tagRef?.Name ?? item.ToString(),
                Margin = new Thickness(0, 0, 4, 4),
                Command = TagCommand,
                CommandParameter = tagRef?.Name ?? (item as string) ?? item.ToString()
            };
            Children.Add(btn);
        }

        if (_overflowBadge == null)
        {
            CreateOverflowBadge();
        }
        Children.Add(_overflowBadge!);
    }

    private void CreateOverflowBadge()
    {
        _overflowText = new TextBlock
        {
            FontSize = 10.5,
            Foreground = (TryFindResource("SecondaryTextBrush") as Brush)
                         ?? (Application.Current?.TryFindResource("SecondaryTextBrush") as Brush)
                         ?? Brushes.Gray,
            VerticalAlignment = VerticalAlignment.Center,
            FontWeight = FontWeights.SemiBold
        };

        _overflowBadge = new Border
        {
            Background = (TryFindResource("SurfaceInputBrush") as Brush)
                         ?? (Application.Current?.TryFindResource("SurfaceInputBrush") as Brush)
                         ?? new SolidColorBrush(Color.FromRgb(30, 32, 40)),
            BorderBrush = (TryFindResource("SubtleBorderBrush") as Brush)
                          ?? (Application.Current?.TryFindResource("SubtleBorderBrush") as Brush)
                          ?? new SolidColorBrush(Color.FromRgb(50, 55, 65)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(9),
            Padding = new Thickness(7, 2, 7, 2),
            Margin = new Thickness(0, 0, 4, 4),
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed,
            Cursor = Cursors.Hand,
            Child = _overflowText
        };

        _overflowBadge.MouseEnter += (s, e) =>
        {
            if (TryFindResource("SurfaceCardHoverBrush") is Brush hoverBg)
                _overflowBadge.Background = hoverBg;
            if (TryFindResource("StrongBorderBrush") is Brush hoverBorder)
                _overflowBadge.BorderBrush = hoverBorder;
            if (_overflowText != null && TryFindResource("PrimaryTextBrush") is Brush primaryText)
                _overflowText.Foreground = primaryText;
        };

        _overflowBadge.MouseLeave += (s, e) =>
        {
            UpdateBadgeBrushes();
        };

        _overflowBadge.MouseLeftButtonUp += OnOverflowBadgeClicked;
    }

    private void UpdateBadgeBrushes()
    {
        if (_overflowBadge == null || _overflowText == null) return;

        var bg = (TryFindResource("SurfaceInputBrush") as Brush)
                 ?? (Application.Current?.TryFindResource("SurfaceInputBrush") as Brush)
                 ?? new SolidColorBrush(Color.FromRgb(30, 32, 40));
        var border = (TryFindResource("SubtleBorderBrush") as Brush)
                     ?? (Application.Current?.TryFindResource("SubtleBorderBrush") as Brush)
                     ?? new SolidColorBrush(Color.FromRgb(50, 55, 65));
        var fg = (TryFindResource("SecondaryTextBrush") as Brush)
                 ?? (Application.Current?.TryFindResource("SecondaryTextBrush") as Brush)
                 ?? Brushes.Gray;

        _overflowBadge.Background = bg;
        _overflowBadge.BorderBrush = border;
        _overflowText.Foreground = fg;
    }

    private void OnOverflowBadgeClicked(object sender, MouseButtonEventArgs e)
    {
        var hiddenTags = GetHiddenTags();
        if (hiddenTags.Count == 0 || _overflowBadge == null) return;

        var menu = new ContextMenu
        {
            PlacementTarget = _overflowBadge,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom,
            Background = (TryFindResource("SurfaceFloatingBrush") as Brush)
                         ?? (TryFindResource("SurfaceCardBrush") as Brush)
                         ?? (Application.Current?.TryFindResource("SurfaceCardBrush") as Brush)
                         ?? new SolidColorBrush(Color.FromRgb(24, 26, 32)),
            Foreground = (TryFindResource("PrimaryTextBrush") as Brush)
                         ?? (Application.Current?.TryFindResource("PrimaryTextBrush") as Brush)
                         ?? Brushes.White,
            BorderBrush = (TryFindResource("SubtleBorderBrush") as Brush)
                          ?? (Application.Current?.TryFindResource("SubtleBorderBrush") as Brush)
                          ?? new SolidColorBrush(Color.FromRgb(50, 55, 65)),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(4)
        };

        foreach (var tag in hiddenTags)
        {
            var menuItem = new MenuItem
            {
                Header = tag.DisplayName ?? tag.Name,
                Command = TagCommand,
                CommandParameter = tag.Name,
                Foreground = (TryFindResource("PrimaryTextBrush") as Brush)
                             ?? (Application.Current?.TryFindResource("PrimaryTextBrush") as Brush)
                             ?? Brushes.White,
                Cursor = Cursors.Hand
            };
            menu.Items.Add(menuItem);
        }

        menu.IsOpen = true;
        e.Handled = true;
    }

    private List<StoreTagRef> GetHiddenTags()
    {
        var list = new List<StoreTagRef>();
        int tagCount = _overflowBadge != null ? Children.Count - 1 : Children.Count;
        for (int i = 0; i < tagCount; i++)
        {
            if (Children[i].Visibility != Visibility.Visible &&
                Children[i] is FrameworkElement fe &&
                fe.DataContext is StoreTagRef tag)
            {
                list.Add(tag);
            }
        }
        return list;
    }

    private Size ApplyTagFit(double limitWidth, double availableHeight)
    {
        EnsureStylesApplied();

        int totalChildren = Children.Count;
        if (totalChildren == 0) return new Size(0, 0);

        int tagCount = _overflowBadge != null ? totalChildren - 1 : totalChildren;
        if (tagCount <= 0) return new Size(0, 0);

        double maxChildHeight = 0;
        double measureHeight = (double.IsInfinity(availableHeight) || double.IsNaN(availableHeight) || availableHeight <= 0) ? 40 : availableHeight;

        while (_cachedTagWidths.Count < tagCount)
            _cachedTagWidths.Add(0);

        // 1. Measure each tag child unconstrained to obtain its desired size.
        // In WPF, elements with Visibility.Collapsed return (0, 0) DesiredSize on Measure.
        // Temporarily switching them to Visibility.Hidden ensures WPF measures their real size.
        for (int i = 0; i < tagCount; i++)
        {
            var child = Children[i];
            if (child.Visibility == Visibility.Collapsed)
            {
                child.Visibility = Visibility.Hidden;
            }
            child.Measure(new Size(double.PositiveInfinity, measureHeight));
            double w = child.DesiredSize.Width;
            if (w > 0)
            {
                _cachedTagWidths[i] = w;
            }
            if (child.DesiredSize.Height > maxChildHeight)
                maxChildHeight = child.DesiredSize.Height;
        }

        // 2. Ensure badge is created and ready for measurement
        if (_overflowBadge == null)
        {
            CreateOverflowBadge();
            Children.Add(_overflowBadge!);
        }

        if (_overflowBadge!.Visibility == Visibility.Collapsed)
        {
            _overflowBadge.Visibility = Visibility.Hidden;
        }

        _lastFittedWidth = limitWidth;

        // If available width is unbounded, show all tags without badge
        if (double.IsInfinity(limitWidth) || double.IsNaN(limitWidth) || limitWidth <= 0)
        {
            double sum = 0;
            for (int i = 0; i < tagCount; i++)
            {
                Children[i].Visibility = Visibility.Visible;
                sum += _cachedTagWidths[i];
            }
            if (_overflowBadge != null)
                _overflowBadge.Visibility = Visibility.Collapsed;
            return new Size(sum, maxChildHeight);
        }

        // 3. Check if all tags fit without needing the overflow badge
        double totalTagsWidth = 0;
        for (int i = 0; i < tagCount; i++)
        {
            totalTagsWidth += _cachedTagWidths[i];
        }

        if (totalTagsWidth <= limitWidth)
        {
            for (int i = 0; i < tagCount; i++)
                Children[i].Visibility = Visibility.Visible;
            if (_overflowBadge != null)
                _overflowBadge.Visibility = Visibility.Collapsed;
            return new Size(totalTagsWidth, maxChildHeight);
        }

        // 4. Tags don't all fit. We MUST display the overflow badge alongside as many tags as fit.
        // Make the overflow badge Visible so MeasureCore does not return (0, 0).
        _overflowBadge!.Visibility = Visibility.Visible;

        // Precompute prefix sums of tag desired widths
        double[] prefixSums = new double[tagCount + 1];
        prefixSums[0] = 0;
        for (int i = 0; i < tagCount; i++)
        {
            prefixSums[i + 1] = prefixSums[i] + _cachedTagWidths[i];
        }

        // Find the maximum visible count k (from tagCount - 1 down to 0) such that
        // prefixSums[k] + badgeWidth <= limitWidth
        int bestVisibleCount = 0;
        double bestBadgeWidth = 0;

        for (int k = tagCount - 1; k >= 0; k--)
        {
            int hidden = tagCount - k;
            _overflowText!.Text = string.Format(MoreTextFormat ?? "+{0} más", hidden);
            _overflowBadge.Measure(new Size(double.PositiveInfinity, measureHeight));
            double badgeW = _overflowBadge.DesiredSize.Width;

            if (prefixSums[k] + badgeW <= limitWidth)
            {
                bestVisibleCount = k;
                bestBadgeWidth = badgeW;
                if (_overflowBadge.DesiredSize.Height > maxChildHeight)
                    maxChildHeight = _overflowBadge.DesiredSize.Height;
                break;
            }

            if (k == 0)
            {
                // Even with 0 tags, show the badge
                bestVisibleCount = 0;
                bestBadgeWidth = badgeW;
                if (_overflowBadge.DesiredSize.Height > maxChildHeight)
                    maxChildHeight = _overflowBadge.DesiredSize.Height;
            }
        }

        // Apply visibility
        for (int i = 0; i < tagCount; i++)
        {
            Children[i].Visibility = (i < bestVisibleCount) ? Visibility.Visible : Visibility.Collapsed;
        }

        int hiddenCount = tagCount - bestVisibleCount;
        _overflowBadge.Visibility = Visibility.Visible;
        _overflowText!.Text = string.Format(MoreTextFormat ?? "+{0} más", hiddenCount);
        _overflowBadge.Measure(new Size(double.PositiveInfinity, measureHeight));

        var hiddenNames = new List<string>();
        for (int i = bestVisibleCount; i < tagCount; i++)
        {
            if (Children[i] is FrameworkElement fe && fe.DataContext is StoreTagRef tag)
                hiddenNames.Add(tag.DisplayName ?? tag.Name);
        }
        _overflowBadge.ToolTip = hiddenNames.Count > 0 ? string.Join(", ", hiddenNames) : null;

        double finalWidth = prefixSums[bestVisibleCount] + _overflowBadge.DesiredSize.Width;
        return new Size(finalWidth, maxChildHeight);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        double limitWidth = availableSize.Width;
        if (double.IsInfinity(limitWidth) || double.IsNaN(limitWidth) || limitWidth <= 0)
        {
            if (ActualWidth > 0)
                limitWidth = ActualWidth;
        }
        else if (ActualWidth > 0 && ActualWidth < limitWidth - 1.0)
        {
            limitWidth = ActualWidth;
        }

        return ApplyTagFit(limitWidth, availableSize.Height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (finalSize.Width > 0 && !double.IsInfinity(finalSize.Width))
        {
            if (Math.Abs(finalSize.Width - _lastFittedWidth) > 1.0)
            {
                ApplyTagFit(finalSize.Width, finalSize.Height);
            }
        }

        double x = 0;
        foreach (UIElement child in Children)
        {
            if (child.Visibility != Visibility.Visible)
            {
                child.Arrange(new Rect(0, 0, 0, 0));
                continue;
            }

            double w = child.DesiredSize.Width;
            double h = child.DesiredSize.Height;
            if (h > finalSize.Height) h = finalSize.Height;
            double y = (finalSize.Height - h) / 2.0;
            if (y < 0) y = 0;

            child.Arrange(new Rect(x, y, w, h));
            x += w;
        }
        return finalSize;
    }
}
