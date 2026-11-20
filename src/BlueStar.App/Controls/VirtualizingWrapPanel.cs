using System;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using BlueStar.App.ViewModels;

namespace BlueStar.App.Controls;

/// <summary>
/// High-performance virtualizing wrap panel for catalog item displays.
/// Derives from VirtualizingPanel and implements IScrollInfo to support smooth,
/// on-demand container realization for large collections of game cards.
/// Automatically synchronizes view mode (Grid vs List/Horizontal) from its parent ItemsControl.
/// </summary>
public class VirtualizingWrapPanel : VirtualizingPanel, IScrollInfo
{
    private const int BufferRows = 3;

    public static readonly DependencyProperty ItemWidthProperty =
        DependencyProperty.Register(
            nameof(ItemWidth),
            typeof(double),
            typeof(VirtualizingWrapPanel),
            new FrameworkPropertyMetadata(300.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty ItemHeightProperty =
        DependencyProperty.Register(
            nameof(ItemHeight),
            typeof(double),
            typeof(VirtualizingWrapPanel),
            new FrameworkPropertyMetadata(350.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty OrientationProperty =
        DependencyProperty.Register(
            nameof(Orientation),
            typeof(Orientation),
            typeof(VirtualizingWrapPanel),
            new FrameworkPropertyMetadata(Orientation.Horizontal, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty IsGridViewProperty =
        DependencyProperty.Register(
            nameof(IsGridView),
            typeof(bool),
            typeof(VirtualizingWrapPanel),
            new FrameworkPropertyMetadata(true, OnIsGridViewChanged));

    public double ItemWidth
    {
        get => (double)GetValue(ItemWidthProperty);
        set => SetValue(ItemWidthProperty, value);
    }

    public double ItemHeight
    {
        get => (double)GetValue(ItemHeightProperty);
        set => SetValue(ItemHeightProperty, value);
    }

    public Orientation Orientation
    {
        get => (Orientation)GetValue(OrientationProperty);
        set => SetValue(OrientationProperty, value);
    }

    public bool IsGridView
    {
        get => (bool)GetValue(IsGridViewProperty);
        set => SetValue(IsGridViewProperty, value);
    }

    private static void OnIsGridViewChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is VirtualizingWrapPanel panel && e.NewValue is bool isGrid)
        {
            panel.ItemWidth = isGrid ? 300.0 : 0.0;
            panel.ItemHeight = isGrid ? 350.0 : 136.0;
            panel.ResetContainers();
        }
    }

    public void ResetContainers()
    {
        try
        {
            ItemContainerGenerator?.RemoveAll();
        }
        catch { }

        if (InternalChildren.Count > 0)
        {
            RemoveInternalChildRange(0, InternalChildren.Count);
        }
        InvalidateMeasure();
        InvalidateArrange();
        _scrollOwner?.InvalidateScrollInfo();
    }

    private ScrollViewer? _ancestorScroller;
    private ScrollViewer? _scrollOwner;
    private bool _canVerticallyScroll;
    private bool _canHorizontallyScroll;
    private Size _extent = new(0, 0);
    private Size _viewport = new(0, 0);
    private Point _offset = new(0, 0);

    private ItemContainerGenerator? _generator;
    private INotifyPropertyChanged? _observedDataContext;
    private ItemsControl? _hookedItemsOwner;

    public VirtualizingWrapPanel()
    {
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        DataContextChanged += OnPanelDataContextChanged;
        IsVisibleChanged += (s, e) =>
        {
            if (IsVisible)
            {
                UpdateFromDataContext(DataContext ?? ItemsControl.GetItemsOwner(this)?.DataContext);
                ResetContainers();
            }
        };
    }

    private void OnPanelDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        UpdateFromDataContext(e.NewValue ?? ItemsControl.GetItemsOwner(this)?.DataContext);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        SyncViewModeFromItemsOwner();
        HookAncestorScroller();
        HookGenerator();
        InvalidateMeasure();
        InvalidateArrange();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        UnhookAncestorScroller();
        if (_generator != null)
        {
            _generator.StatusChanged -= OnGeneratorStatusChanged;
            _generator = null;
        }
        if (_observedDataContext != null)
        {
            _observedDataContext.PropertyChanged -= OnObservedDataContextPropertyChanged;
            _observedDataContext = null;
        }
        if (_hookedItemsOwner != null)
        {
            _hookedItemsOwner.DataContextChanged -= OnItemsOwnerDataContextChanged;
            _hookedItemsOwner = null;
        }
        ResetContainers();
    }

    private void SyncViewModeFromItemsOwner()
    {
        var itemsOwner = ItemsControl.GetItemsOwner(this);
        if (itemsOwner == null) return;

        if (!ReferenceEquals(_hookedItemsOwner, itemsOwner))
        {
            if (_hookedItemsOwner != null)
            {
                _hookedItemsOwner.DataContextChanged -= OnItemsOwnerDataContextChanged;
            }
            _hookedItemsOwner = itemsOwner;
            _hookedItemsOwner.DataContextChanged += OnItemsOwnerDataContextChanged;
        }

        UpdateFromDataContext(itemsOwner.DataContext);
    }

    private void OnItemsOwnerDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        UpdateFromDataContext(e.NewValue);
    }

    private void UpdateFromDataContext(object? dataContext)
    {
        if (!ReferenceEquals(_observedDataContext, dataContext))
        {
            if (_observedDataContext != null)
            {
                _observedDataContext.PropertyChanged -= OnObservedDataContextPropertyChanged;
            }
            _observedDataContext = dataContext as INotifyPropertyChanged;
            if (_observedDataContext != null)
            {
                _observedDataContext.PropertyChanged += OnObservedDataContextPropertyChanged;
            }
        }

        if (dataContext is BrowseViewModel bvm)
        {
            ApplyIsGridView(bvm.IsGridView);
        }
        else if (dataContext != null)
        {
            var prop = dataContext.GetType().GetProperty("IsGridView");
            if (prop != null && prop.PropertyType == typeof(bool))
            {
                bool val = (bool)prop.GetValue(dataContext)!;
                ApplyIsGridView(val);
            }
        }
    }

    private void OnObservedDataContextPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == "IsGridView" && sender != null)
        {
            var prop = sender.GetType().GetProperty("IsGridView");
            if (prop != null && prop.PropertyType == typeof(bool))
            {
                bool val = (bool)prop.GetValue(sender)!;
                ApplyIsGridView(val);
            }
        }
    }

    private void ApplyIsGridView(bool isGrid)
    {
        if (IsGridView != isGrid)
        {
            IsGridView = isGrid;
        }
    }

    private void HookGenerator()
    {
        if (_generator != null)
        {
            _generator.StatusChanged -= OnGeneratorStatusChanged;
        }
        _generator = ItemContainerGenerator as ItemContainerGenerator;
        if (_generator != null)
        {
            _generator.StatusChanged += OnGeneratorStatusChanged;
        }
    }

    private void OnGeneratorStatusChanged(object? sender, EventArgs e)
    {
        if (_generator?.Status == GeneratorStatus.ContainersGenerated)
        {
            var itemsOwner = ItemsControl.GetItemsOwner(this);
            var itemCount = itemsOwner?.Items.Count ?? 0;
            if (itemCount > 0 && InternalChildren.Count == 0)
            {
                InvalidateMeasure();
                InvalidateArrange();
            }
        }
    }

    private void HookAncestorScroller()
    {
        UnhookAncestorScroller();
        _ancestorScroller = FindAncestor<ScrollViewer>(this);
        if (_ancestorScroller != null)
        {
            _ancestorScroller.ScrollChanged += OnAncestorScrollChanged;
        }
    }

    private void UnhookAncestorScroller()
    {
        if (_ancestorScroller != null)
        {
            _ancestorScroller.ScrollChanged -= OnAncestorScrollChanged;
            _ancestorScroller = null;
        }
    }

    private void OnAncestorScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.VerticalChange != 0 || e.HorizontalChange != 0 || e.ViewportHeightChange != 0 || e.ViewportWidthChange != 0)
        {
            InvalidateMeasure();
            InvalidateArrange();
        }
    }

    protected override void OnItemsChanged(object sender, ItemsChangedEventArgs args)
    {
        base.OnItemsChanged(sender, args);

        switch (args.Action)
        {
            case NotifyCollectionChangedAction.Reset:
            case NotifyCollectionChangedAction.Remove:
            case NotifyCollectionChangedAction.Replace:
            case NotifyCollectionChangedAction.Move:
                ResetContainers();
                break;
            case NotifyCollectionChangedAction.Add:
                break;
        }

        HookGenerator();
        InvalidateMeasure();
        InvalidateArrange();
    }

    protected override void OnClearChildren()
    {
        base.OnClearChildren();
        ResetContainers();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        try
        {
            if (_observedDataContext == null)
            {
                var dc = DataContext ?? ItemsControl.GetItemsOwner(this)?.DataContext;
                if (dc != null) UpdateFromDataContext(dc);
            }

            var itemsOwner = ItemsControl.GetItemsOwner(this);
            var itemCount = itemsOwner?.Items.Count ?? InternalChildren.Count;

            if (itemCount == 0)
            {
                ResetContainers();
                UpdateScrollDimensions(0, 0, availableSize.Width, availableSize.Height);
                return new Size(0, 0);
            }

            if (_ancestorScroller == null)
            {
                HookAncestorScroller();
            }

            if (_generator == null)
            {
                HookGenerator();
            }

            double availableWidth = availableSize.Width;
            if (double.IsInfinity(availableWidth) || double.IsNaN(availableWidth) || availableWidth <= 0)
            {
                availableWidth = _ancestorScroller?.ViewportWidth > 0
                    ? _ancestorScroller.ViewportWidth
                    : (_ancestorScroller?.ActualWidth > 0
                        ? _ancestorScroller.ActualWidth
                        : (_viewport.Width > 0 ? _viewport.Width : 1200));
            }

            if (availableWidth <= 0) availableWidth = 1200;

            int columns = ItemWidth > 0 ? Math.Max(1, (int)(availableWidth / ItemWidth)) : 1;
            double itemWidth = columns == 1 && ItemWidth <= 0 ? availableWidth : (ItemWidth > 0 ? ItemWidth : 300);
            double itemHeight = ItemHeight > 0 ? ItemHeight : 350;

            int rows = (int)Math.Ceiling((double)itemCount / columns);

            double extentHeight = rows * itemHeight;
            double extentWidth = columns * itemWidth;

            UpdateScrollDimensions(extentWidth, extentHeight, availableWidth, double.IsInfinity(availableSize.Height) ? extentHeight : availableSize.Height);

            double relativeScrollY = _offset.Y;
            double viewHeight = _viewport.Height;

            if (_ancestorScroller != null)
            {
                viewHeight = _ancestorScroller.ViewportHeight > 0
                    ? _ancestorScroller.ViewportHeight
                    : (_ancestorScroller?.ActualHeight > 0 ? _ancestorScroller.ActualHeight : 800);
                try
                {
                    var transform = TransformToAncestor(_ancestorScroller);
                    var pt = transform.Transform(new Point(0, 0));
                    relativeScrollY = Math.Max(0, -pt.Y);
                }
                catch
                {
                    relativeScrollY = _ancestorScroller?.VerticalOffset ?? 0;
                }
            }

            if (viewHeight <= 0) viewHeight = 800;

            int firstRow = Math.Max(0, (int)(relativeScrollY / itemHeight) - BufferRows);
            int lastRow = Math.Min(rows - 1, (int)((relativeScrollY + viewHeight) / itemHeight) + BufferRows);

            int firstItemIndex = firstRow * columns;
            int lastItemIndex = Math.Min(itemCount - 1, (lastRow + 1) * columns - 1);

            var generator = ItemContainerGenerator;
            var realGen = generator as ItemContainerGenerator;
            var recyclingGen = generator as IRecyclingItemContainerGenerator;

            if (generator != null && firstItemIndex <= lastItemIndex)
            {
                var startPos = generator.GeneratorPositionFromIndex(firstItemIndex);
                int childIndex = (startPos.Offset == 0) ? startPos.Index : startPos.Index + 1;
                if (childIndex < 0) childIndex = 0;

                using (generator.StartAt(startPos, GeneratorDirection.Forward, true))
                {
                    for (int i = firstItemIndex; i <= lastItemIndex; i++)
                    {
                        var child = generator.GenerateNext(out bool isNewlyRealized) as UIElement;
                        if (child == null) continue;

                        if (isNewlyRealized)
                        {
                            if (childIndex >= InternalChildren.Count)
                            {
                                AddInternalChild(child);
                            }
                            else
                            {
                                InsertInternalChild(childIndex, child);
                            }
                            generator.PrepareItemContainer(child);
                        }
                        else
                        {
                            // Container is already realized and in InternalChildren
                            if (childIndex >= InternalChildren.Count || !ReferenceEquals(InternalChildren[childIndex], child))
                            {
                                int existingIndex = InternalChildren.IndexOf(child);
                                if (existingIndex >= 0)
                                {
                                    RemoveInternalChildRange(existingIndex, 1);
                                }
                                if (childIndex >= InternalChildren.Count)
                                {
                                    AddInternalChild(child);
                                }
                                else
                                {
                                    InsertInternalChild(childIndex, child);
                                }
                            }
                        }

                        // Measure child only if newly realized or measure invalid or size changed
                        if (isNewlyRealized || !child.IsMeasureValid || Math.Abs(child.DesiredSize.Width - itemWidth) > 1.0)
                        {
                            child.Measure(new Size(itemWidth, itemHeight));
                        }

                        childIndex++;
                    }
                }

                // Clean up containers that have scrolled out of view (above or below)
                for (int i = InternalChildren.Count - 1; i >= 0; i--)
                {
                    var child = InternalChildren[i];
                    int itemIndex = realGen != null ? realGen.IndexFromContainer(child) : -1;
                    if (itemIndex < 0 || itemIndex < firstItemIndex || itemIndex > lastItemIndex)
                    {
                        if (itemIndex >= 0)
                        {
                            var genPos = generator.GeneratorPositionFromIndex(itemIndex);
                            if (genPos.Index >= 0)
                            {
                                if (recyclingGen != null)
                                {
                                    recyclingGen.Recycle(genPos, 1);
                                }
                                else
                                {
                                    generator.Remove(genPos, 1);
                                }
                            }
                        }
                        RemoveInternalChildRange(i, 1);
                    }
                }
            }

            double resultWidth = double.IsInfinity(availableSize.Width) ? extentWidth : availableSize.Width;
            double resultHeight = double.IsInfinity(availableSize.Height) ? extentHeight : Math.Min(availableSize.Height, extentHeight);

            return new Size(resultWidth, resultHeight);
        }
        catch
        {
            ResetContainers();
            return new Size(0, 0);
        }
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        try
        {
            int columns = ItemWidth > 0 ? Math.Max(1, (int)(finalSize.Width / ItemWidth)) : 1;
            double itemWidth = columns == 1 && ItemWidth <= 0 ? finalSize.Width : (ItemWidth > 0 ? ItemWidth : 300);
            double itemHeight = ItemHeight > 0 ? ItemHeight : 350;

            var realGen = ItemContainerGenerator as ItemContainerGenerator;

            for (int i = 0; i < InternalChildren.Count; i++)
            {
                var child = InternalChildren[i];
                if (child == null) continue;

                int itemIndex = realGen != null ? realGen.IndexFromContainer(child) : -1;
                if (itemIndex < 0) itemIndex = i;

                int row = itemIndex / columns;
                int col = itemIndex % columns;

                double x = col * itemWidth;
                double y = row * itemHeight;

                child.Arrange(new Rect(x, y, itemWidth, itemHeight));
            }

            return finalSize;
        }
        catch (Exception)
        {
            return finalSize;
        }
    }

    private void UpdateScrollDimensions(double extentW, double extentH, double viewW, double viewH)
    {
        bool changed = false;
        if (_extent.Width != extentW || _extent.Height != extentH)
        {
            _extent = new Size(extentW, extentH);
            changed = true;
        }

        if (_viewport.Width != viewW || _viewport.Height != viewH)
        {
            _viewport = new Size(viewW, viewH);
            changed = true;
        }

        if (changed)
        {
            _scrollOwner?.InvalidateScrollInfo();
        }
    }

    private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current != null)
        {
            if (current is T match) return match;
            current = VisualTreeHelper.GetParent(current);
        }
        return null;
    }

    // ── IScrollInfo Implementation ──────────────────────────────────────

    public bool CanVerticallyScroll
    {
        get => _canVerticallyScroll;
        set => _canVerticallyScroll = value;
    }

    public bool CanHorizontallyScroll
    {
        get => _canHorizontallyScroll;
        set => _canHorizontallyScroll = value;
    }

    public double ExtentWidth => _extent.Width;
    public double ExtentHeight => _extent.Height;
    public double ViewportWidth => _viewport.Width;
    public double ViewportHeight => _viewport.Height;
    public double HorizontalOffset => _offset.X;
    public double VerticalOffset => _offset.Y;
    public ScrollViewer? ScrollOwner
    {
        get => _scrollOwner;
        set => _scrollOwner = value;
    }

    public void LineUp() => SetVerticalOffset(_offset.Y - 40);
    public void LineDown() => SetVerticalOffset(_offset.Y + 40);
    public void LineLeft() => SetHorizontalOffset(_offset.X - 40);
    public void LineRight() => SetHorizontalOffset(_offset.X + 40);

    public void PageUp() => SetVerticalOffset(_offset.Y - _viewport.Height);
    public void PageDown() => SetVerticalOffset(_offset.Y + _viewport.Height);
    public void PageLeft() => SetHorizontalOffset(_offset.X - _viewport.Width);
    public void PageRight() => SetHorizontalOffset(_offset.X + _viewport.Width);

    public void MouseWheelUp() => SetVerticalOffset(_offset.Y - 120);
    public void MouseWheelDown() => SetVerticalOffset(_offset.Y + 120);
    public void MouseWheelLeft() => SetHorizontalOffset(_offset.X - 120);
    public void MouseWheelRight() => SetHorizontalOffset(_offset.X + 120);

    public void SetHorizontalOffset(double offset)
    {
        offset = Math.Clamp(offset, 0, Math.Max(0, _extent.Width - _viewport.Width));
        if (Math.Abs(_offset.X - offset) > 0.001)
        {
            _offset.X = offset;
            InvalidateMeasure();
            _scrollOwner?.InvalidateScrollInfo();
        }
    }

    public void SetVerticalOffset(double offset)
    {
        offset = Math.Clamp(offset, 0, Math.Max(0, _extent.Height - _viewport.Height));
        if (Math.Abs(_offset.Y - offset) > 0.001)
        {
            _offset.Y = offset;
            InvalidateMeasure();
            _scrollOwner?.InvalidateScrollInfo();
        }
    }

    public Rect MakeVisible(Visual visual, Rect rectangle)
    {
        return rectangle;
    }
}
