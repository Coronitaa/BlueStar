using System;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace BlueStar.App.Controls;

/// <summary>
/// High-performance virtualizing wrap panel that arranges items in a responsive grid
/// while recycling UI containers to eliminate lag and RAM bloat on large result sets.
/// </summary>
public class VirtualizingWrapPanel : VirtualizingPanel, IScrollInfo
{
    public static readonly DependencyProperty ItemWidthProperty =
        DependencyProperty.Register(nameof(ItemWidth), typeof(double), typeof(VirtualizingWrapPanel),
            new FrameworkPropertyMetadata(300.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty ItemHeightProperty =
        DependencyProperty.Register(nameof(ItemHeight), typeof(double), typeof(VirtualizingWrapPanel),
            new FrameworkPropertyMetadata(340.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

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

    private TranslateTransform _trans = new();
    private Size _extent = new(0, 0);
    private Size _viewport = new(0, 0);
    private Point _offset = new(0, 0);

    public VirtualizingWrapPanel()
    {
        RenderTransform = _trans;
    }

    // --- Layout and Virtualization ---

    protected override Size MeasureOverride(Size availableSize)
    {
        var itemsCount = ItemsOwner?.Items.Count ?? 0;
        if (itemsCount == 0)
        {
            UpdateScrollInfo(availableSize, new Size(0, 0));
            return new Size(0, 0);
        }

        var itemWidth = ItemWidth > 0 ? ItemWidth : 300.0;
        var itemHeight = ItemHeight > 0 ? ItemHeight : 340.0;

        var availableWidth = double.IsInfinity(availableSize.Width) ? availableSize.Width : Math.Max(availableSize.Width, itemWidth);
        var columns = Math.Max(1, (int)Math.Floor(availableWidth / itemWidth));
        var rows = (int)Math.Ceiling((double)itemsCount / columns);

        var totalWidth = columns * itemWidth;
        var totalHeight = rows * itemHeight;
        var extentSize = new Size(totalWidth, totalHeight);

        UpdateScrollInfo(availableSize, extentSize);

        // Determine visible row range with 1 row buffer
        var firstVisibleRow = Math.Max(0, (int)Math.Floor(_offset.Y / itemHeight) - 1);
        var lastVisibleRow = Math.Min(rows - 1, (int)Math.Ceiling((_offset.Y + _viewport.Height) / itemHeight) + 1);

        var firstVisibleIndex = firstVisibleRow * columns;
        var lastVisibleIndex = Math.Min(itemsCount - 1, (lastVisibleRow + 1) * columns - 1);

        var generator = ItemContainerGenerator;
        var startPos = generator.GeneratorPositionFromIndex(firstVisibleIndex);
        var childIndex = (startPos.Offset == 0) ? startPos.Index : startPos.Index + 1;

        using (generator.StartAt(startPos, GeneratorDirection.Forward, true))
        {
            for (var itemIndex = firstVisibleIndex; itemIndex <= lastVisibleIndex; itemIndex++)
            {
                var child = generator.GenerateNext(out var isNewlyRealized) as UIElement;
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

                child.Measure(new Size(itemWidth, itemHeight));
                childIndex++;
            }
        }

        // Clean up / recycle offscreen containers
        CleanUpContainers(firstVisibleIndex, lastVisibleIndex);

        var resultWidth = double.IsInfinity(availableSize.Width) ? totalWidth : availableSize.Width;
        var resultHeight = double.IsInfinity(availableSize.Height) ? totalHeight : availableSize.Height;
        return new Size(resultWidth, resultHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var itemWidth = ItemWidth > 0 ? ItemWidth : 300.0;
        var itemHeight = ItemHeight > 0 ? ItemHeight : 340.0;
        var columns = Math.Max(1, (int)Math.Floor(finalSize.Width / itemWidth));

        for (var i = 0; i < InternalChildren.Count; i++)
        {
            var child = InternalChildren[i];
            var itemIndex = ItemContainerGenerator.IndexFromGeneratorPosition(new GeneratorPosition(i, 0));
            if (itemIndex < 0) continue;

            var col = itemIndex % columns;
            var row = itemIndex / columns;

            var x = col * itemWidth;
            var y = row * itemHeight;

            child.Arrange(new Rect(x, y, itemWidth, itemHeight));
        }

        return finalSize;
    }

    private void CleanUpContainers(int firstVisibleIndex, int lastVisibleIndex)
    {
        var generator = ItemContainerGenerator;
        if (generator == null) return;

        for (var i = InternalChildren.Count - 1; i >= 0; i--)
        {
            var pos = new GeneratorPosition(i, 0);
            var itemIndex = generator.IndexFromGeneratorPosition(pos);
            if (itemIndex < firstVisibleIndex || itemIndex > lastVisibleIndex)
            {
                try
                {
                    generator.Remove(pos, 1);
                    RemoveInternalChildRange(i, 1);
                }
                catch
                {
                    // Defensive guard: prevent collection mutation/reset exceptions from crashing the dispatcher
                }
            }
        }
    }

    private ItemsControl? ItemsOwner => ItemsControl.GetItemsOwner(this);

    // --- IScrollInfo implementation for smooth scrolling ---

    public bool CanVerticallyScroll { get; set; } = true;
    public bool CanHorizontallyScroll { get; set; } = false;

    public double ExtentWidth => _extent.Width;
    public double ExtentHeight => _extent.Height;
    public double ViewportWidth => _viewport.Width;
    public double ViewportHeight => _viewport.Height;
    public double HorizontalOffset => _offset.X;
    public double VerticalOffset => _offset.Y;
    public ScrollViewer? ScrollOwner { get; set; }

    public void LineUp() => SetVerticalOffset(_offset.Y - 50);
    public void LineDown() => SetVerticalOffset(_offset.Y + 50);
    public void LineLeft() { }
    public void LineRight() { }
    public void PageUp() => SetVerticalOffset(_offset.Y - _viewport.Height);
    public void PageDown() => SetVerticalOffset(_offset.Y + _viewport.Height);
    public void PageLeft() { }
    public void PageRight() { }
    public void MouseWheelUp() => SetVerticalOffset(_offset.Y - 100);
    public void MouseWheelDown() => SetVerticalOffset(_offset.Y + 100);
    public void MouseWheelLeft() { }
    public void MouseWheelRight() { }
    public void SetHorizontalOffset(double offset) { }

    public void SetVerticalOffset(double offset)
    {
        if (offset < 0 || _viewport.Height >= _extent.Height)
        {
            offset = 0;
        }
        else if (offset + _viewport.Height > _extent.Height)
        {
            offset = _extent.Height - _viewport.Height;
        }

        _offset.Y = offset;
        _trans.Y = -offset;
        ScrollOwner?.InvalidateScrollInfo();
        InvalidateMeasure();
    }

    public Rect MakeVisible(Visual visual, Rect rectangle) => rectangle;

    private void UpdateScrollInfo(Size availableSize, Size extentSize)
    {
        var viewport = availableSize;
        if (double.IsInfinity(viewport.Width)) viewport.Width = extentSize.Width;
        if (double.IsInfinity(viewport.Height)) viewport.Height = extentSize.Height;

        _extent = extentSize;
        _viewport = viewport;
        ScrollOwner?.InvalidateScrollInfo();
    }
}
