using System;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace BlueStar.App.Controls;

/// <summary>
/// High-performance virtualizing wrap panel for catalog item displays.
/// Derives from VirtualizingPanel and implements IScrollInfo to support smooth,
/// on-demand container realization for large collections of game cards.
/// </summary>
public class VirtualizingWrapPanel : VirtualizingPanel, IScrollInfo
{
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

    private ScrollViewer? _ancestorScroller;
    private ScrollViewer? _scrollOwner;
    private bool _canVerticallyScroll;
    private bool _canHorizontallyScroll;
    private Size _extent = new(0, 0);
    private Size _viewport = new(0, 0);
    private Point _offset = new(0, 0);

    public VirtualizingWrapPanel()
    {
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        HookAncestorScroller();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        UnhookAncestorScroller();
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
                RemoveInternalChildRange(0, InternalChildren.Count);
                break;
            case NotifyCollectionChangedAction.Add:
                if (args.Position.Index >= 0 && args.Position.Index < InternalChildren.Count)
                {
                    RemoveInternalChildRange(0, InternalChildren.Count);
                }
                break;
        }

        InvalidateMeasure();
    }

    protected override void OnClearChildren()
    {
        base.OnClearChildren();
        RemoveInternalChildRange(0, InternalChildren.Count);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        try
        {
            var itemsOwner = ItemsControl.GetItemsOwner(this);
            var itemCount = itemsOwner?.Items.Count ?? InternalChildren.Count;

            if (itemCount == 0)
            {
                RemoveInternalChildRange(0, InternalChildren.Count);
                UpdateScrollDimensions(0, 0, availableSize.Width, availableSize.Height);
                return new Size(0, 0);
            }

            double itemWidth = ItemWidth > 0 ? ItemWidth : 300;
            double itemHeight = ItemHeight > 0 ? ItemHeight : 350;

            double availableWidth = double.IsInfinity(availableSize.Width)
                ? (_viewport.Width > 0 ? _viewport.Width : 1200)
                : availableSize.Width;

            int columns = Math.Max(1, (int)(availableWidth / itemWidth));
            int rows = (int)Math.Ceiling((double)itemCount / columns);

            double extentHeight = rows * itemHeight;
            double extentWidth = columns * itemWidth;

            UpdateScrollDimensions(extentWidth, extentHeight, availableWidth, double.IsInfinity(availableSize.Height) ? extentHeight : availableSize.Height);

            // Determine visible viewport range
            double offsetY = _canVerticallyScroll ? _offset.Y : 0;
            double viewportHeight = availableSize.Height;

            if (double.IsInfinity(viewportHeight) || viewportHeight <= 0)
            {
                if (_ancestorScroller != null)
                {
                    try
                    {
                        var transform = TransformToAncestor(_ancestorScroller);
                        var panelTopInScroller = transform.Transform(new Point(0, 0));
                        offsetY = Math.Max(0, -panelTopInScroller.Y);
                        viewportHeight = _ancestorScroller.ViewportHeight > 0 ? _ancestorScroller.ViewportHeight : _ancestorScroller.ActualHeight;
                    }
                    catch
                    {
                        viewportHeight = 1000;
                    }
                }
                else
                {
                    viewportHeight = 1000;
                }
            }

            int firstRow = Math.Max(0, (int)Math.Floor(offsetY / itemHeight) - 1);
            int lastRow = Math.Min(rows - 1, (int)Math.Ceiling((offsetY + viewportHeight) / itemHeight) + 1);

            if (firstRow > lastRow || firstRow * columns >= itemCount)
            {
                firstRow = 0;
                lastRow = Math.Min(rows - 1, (int)Math.Ceiling(viewportHeight / itemHeight) + 1);
            }

            int firstIndex = Math.Clamp(firstRow * columns, 0, Math.Max(0, itemCount - 1));
            int lastIndex = Math.Clamp(((lastRow + 1) * columns) - 1, firstIndex, Math.Max(0, itemCount - 1));

            var generator = ItemContainerGenerator;
            if (generator != null)
            {
                var startPos = generator.GeneratorPositionFromIndex(firstIndex);
                int childIndex = (startPos.Offset == 0) ? startPos.Index : startPos.Index + 1;

                using (generator.StartAt(startPos, GeneratorDirection.Forward, true))
                {
                    for (int i = firstIndex; i <= lastIndex; i++)
                    {
                        var child = generator.GenerateNext(out bool isNewlyRealized) as UIElement;
                        if (child == null) break;

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
                        child.Measure(new Size(itemWidth, itemHeight));
                        childIndex++;
                    }
                }

                CleanUpItems(firstIndex, lastIndex);
            }
            else
            {
                foreach (UIElement child in InternalChildren)
                {
                    child.Measure(new Size(itemWidth, itemHeight));
                }
            }

            double resultWidth = double.IsInfinity(availableSize.Width) ? extentWidth : availableSize.Width;
            double resultHeight = double.IsInfinity(availableSize.Height) ? extentHeight : Math.Min(availableSize.Height, extentHeight);

            return new Size(resultWidth, resultHeight);
        }
        catch (Exception)
        {
            try
            {
                RemoveInternalChildRange(0, InternalChildren.Count);
            }
            catch
            {
                // Ignore
            }
            return new Size(0, 0);
        }
    }

    private void CleanUpItems(int minIndex, int maxIndex)
    {
        var generator = ItemContainerGenerator;
        if (generator == null) return;

        for (int i = InternalChildren.Count - 1; i >= 0; i--)
        {
            try
            {
                var pos = new GeneratorPosition(i, 0);
                int itemIndex = generator.IndexFromGeneratorPosition(pos);
                if (itemIndex < 0 || itemIndex < minIndex || itemIndex > maxIndex)
                {
                    try
                    {
                        generator.Remove(pos, 1);
                    }
                    catch (Exception)
                    {
                        // Ignore generator desynchronization
                    }
                    RemoveInternalChildRange(i, 1);
                }
            }
            catch (Exception)
            {
                // In case of generator desynchronization during rapid UI collection updates,
                // safely remove visual child directly to prevent WPF layout crash
                try
                {
                    RemoveInternalChildRange(i, 1);
                }
                catch
                {
                    // Ignore
                }
            }
        }
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        try
        {
            var generator = ItemContainerGenerator;
            double itemWidth = ItemWidth > 0 ? ItemWidth : 300;
            double itemHeight = ItemHeight > 0 ? ItemHeight : 350;

            int columns = Math.Max(1, (int)(finalSize.Width / itemWidth));
            double verticalOffset = _canVerticallyScroll ? _offset.Y : 0;

            for (int i = 0; i < InternalChildren.Count; i++)
            {
                var child = InternalChildren[i];
                if (child == null) continue;

                int itemIndex = -1;
                try
                {
                    itemIndex = generator?.IndexFromGeneratorPosition(new GeneratorPosition(i, 0)) ?? i;
                }
                catch
                {
                    itemIndex = i;
                }

                if (itemIndex < 0) itemIndex = i;

                int row = itemIndex / columns;
                int col = itemIndex % columns;

                double x = col * itemWidth;
                double y = (row * itemHeight) - verticalOffset;

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

