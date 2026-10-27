using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using FluentAssertions;
using Xunit;

namespace BlueStar.App.Tests;

public class ScrollBarInspectionTests
{
    [Theory]
    [InlineData(600)]
    [InlineData(300)]
    [InlineData(150)]
    [InlineData(80)]
    [InlineData(50)]
    public void ScrollBar_Thumb_NeverOverflowsTrack_AtAnyHeight(double height)
    {
        StaTestHelper.Run(() =>
        {
            var scrollViewer = new ScrollViewer
            {
                Width = 200,
                Height = height,
                VerticalScrollBarVisibility = ScrollBarVisibility.Visible
            };
            var content = new StackPanel { Height = 10000 };
            scrollViewer.Content = content;

            var window = new Window { Content = scrollViewer, Width = 300, Height = height + 100 };
            window.Show();

            scrollViewer.UpdateLayout();
            scrollViewer.ScrollToVerticalOffset(10000);
            scrollViewer.UpdateLayout();

            var scrollBar = (ScrollBar)scrollViewer.Template.FindName("PART_VerticalScrollBar", scrollViewer);
            var track = (Track)scrollBar.Template.FindName("PART_Track", scrollBar);
            var thumb = track.Thumb;

            if (thumb != null && track.ActualHeight > 0)
            {
                var thumbTrans = thumb.TranslatePoint(new Point(0, 0), scrollBar);
                var trackTrans = track.TranslatePoint(new Point(0, 0), scrollBar);

                double trackBottomInScrollBar = trackTrans.Y + track.ActualHeight;
                double thumbBottomInScrollBar = thumbTrans.Y + thumb.ActualHeight;

                // Thumb should never extend beyond track bottom (allow 0.5px rounding error)
                (thumbBottomInScrollBar - trackBottomInScrollBar).Should().BeLessThanOrEqualTo(0.5);
                (thumbTrans.Y - trackTrans.Y).Should().BeGreaterThanOrEqualTo(-0.5);

                // ScrollBar overall bottom should not be exceeded
                (thumbBottomInScrollBar - scrollBar.ActualHeight).Should().BeLessThanOrEqualTo(0.5);
            }

            window.Close();
        });
    }

    [Fact]
    public void ScrollBar_Thumb_HasMinimumSize_AndDoesNotBecomeMinusculeOnLargeExtent()
    {
        StaTestHelper.Run(() =>
        {
            var scrollViewer = new ScrollViewer
            {
                Width = 200,
                Height = 400,
                VerticalScrollBarVisibility = ScrollBarVisibility.Visible
            };
            // 200,000px content simulates thousands of items in Explore or Thunderstore
            var content = new StackPanel { Height = 200000 };
            scrollViewer.Content = content;

            var window = new Window { Content = scrollViewer, Width = 300, Height = 500 };
            window.Show();

            scrollViewer.UpdateLayout();

            var scrollBar = (ScrollBar)scrollViewer.Template.FindName("PART_VerticalScrollBar", scrollViewer);
            var track = (Track)scrollBar.Template.FindName("PART_Track", scrollBar);
            var thumb = track.Thumb;

            thumb.Should().NotBeNull();
            // Should be bounded to minimum thumb size (40px) rather than 0.5px
            thumb.ActualHeight.Should().BeInRange(36.0, 44.0);

            // Scroll to middle and bottom, verify thumb stays within track
            scrollViewer.ScrollToVerticalOffset(100000);
            scrollViewer.UpdateLayout();

            var thumbTrans = thumb.TranslatePoint(new Point(0, 0), scrollBar);
            var trackTrans = track.TranslatePoint(new Point(0, 0), scrollBar);
            (thumbTrans.Y + thumb.ActualHeight).Should().BeLessThanOrEqualTo(trackTrans.Y + track.ActualHeight + 0.5);

            scrollViewer.ScrollToVerticalOffset(200000);
            scrollViewer.UpdateLayout();

            thumbTrans = thumb.TranslatePoint(new Point(0, 0), scrollBar);
            (thumbTrans.Y + thumb.ActualHeight).Should().BeLessThanOrEqualTo(trackTrans.Y + track.ActualHeight + 0.5);

            window.Close();
        });
    }

    [Fact]
    public void ScrollBar_And_ScrollViewer_HaveNoContextMenu_OnRightClick()
    {
        StaTestHelper.Run(() =>
        {
            var scrollBar = new ScrollBar { Orientation = Orientation.Vertical };
            scrollBar.ContextMenu.Should().BeNull();

            var scrollViewer = new ScrollViewer();
            scrollViewer.ContextMenu.Should().BeNull();
        });
    }
}
