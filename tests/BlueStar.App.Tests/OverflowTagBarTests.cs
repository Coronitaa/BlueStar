using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using BlueStar.App.Controls;
using BlueStar.Core.Models;
using Xunit;

namespace BlueStar.App.Tests;

public class OverflowTagBarTests
{
    private static void RunOnStaThread(Action action) => StaTestHelper.Run(action);

    [Fact]
    public void Defaults_AreCorrect()
    {
        RunOnStaThread(() =>
        {
            var bar = new OverflowTagBar();
            Assert.True(bar.ClipToBounds);
            Assert.Equal("+{0} más", bar.MoreTextFormat);
            Assert.Null(bar.ItemsSource);
            Assert.Null(bar.TagCommand);
        });
    }

    [Fact]
    public void ItemsSource_CreatesChildrenButtons()
    {
        RunOnStaThread(() =>
        {
            var bar = new OverflowTagBar();
            var tags = new List<StoreTagRef>
            {
                new("Action", false),
                new("Adventure", true),
                new("Co-op", false)
            };

            bar.ItemsSource = tags;

            // Children should have 3 tag buttons + 1 overflow badge = 4
            Assert.Equal(4, bar.Children.Count);
            Assert.IsType<Button>(bar.Children[0]);
            Assert.IsType<Button>(bar.Children[1]);
            Assert.IsType<Button>(bar.Children[2]);
            Assert.IsType<Border>(bar.Children[3]);
        });
    }

    [Fact]
    public void Measure_WithSufficientWidth_ShowsAllTagsAndHidesBadge()
    {
        RunOnStaThread(() =>
        {
            var bar = new OverflowTagBar();
            var tags = new List<StoreTagRef>
            {
                new("Tag 1", false),
                new("Tag 2", false)
            };

            bar.ItemsSource = tags;

            // Measure with large width (1000px)
            bar.Measure(new Size(1000, 30));

            // Tag 1 and Tag 2 should be visible
            Assert.Equal(Visibility.Visible, bar.Children[0].Visibility);
            Assert.Equal(Visibility.Visible, bar.Children[1].Visibility);

            // Overflow badge (last child) should be collapsed
            Assert.Equal(Visibility.Collapsed, bar.Children[2].Visibility);
        });
    }

    [Fact]
    public void Measure_WithRestrictedWidth_ShowsOnlyFittingTagsAndShowsOverflowBadge()
    {
        RunOnStaThread(() =>
        {
            var bar = new OverflowTagBar
            {
                MoreTextFormat = "+{0} more"
            };

            var tags = new List<StoreTagRef>
            {
                new("Very Long Tag Name 1", false),
                new("Very Long Tag Name 2", false),
                new("Very Long Tag Name 3", false),
                new("Very Long Tag Name 4", false),
                new("Very Long Tag Name 5", false)
            };

            bar.ItemsSource = tags;

            // Measure each child to see dimensions
            foreach (UIElement child in bar.Children)
            {
                child.Measure(new Size(double.PositiveInfinity, 30));
            }

            // Force a restricted measurement (e.g. 150px)
            bar.Measure(new Size(150, 30));

            // The overflow badge (child 5) must be visible
            var badge = bar.Children[5] as Border;
            Assert.NotNull(badge);
            Assert.Equal(Visibility.Visible, badge.Visibility);

            var textBlock = badge.Child as TextBlock;
            Assert.NotNull(textBlock);
            Assert.StartsWith("+", textBlock.Text);
            Assert.EndsWith("more", textBlock.Text);

            // DesiredSize width must NEVER exceed available width (150px)
            Assert.True(bar.DesiredSize.Width <= 150.01,
                $"DesiredSize.Width ({bar.DesiredSize.Width}) must be <= available width (150)");
        });
    }

    [Fact]
    public void Arrange_WithSmallerWidthThanMeasured_ShowsOverflowBadgeAndHidesExcess()
    {
        RunOnStaThread(() =>
        {
            var bar = new OverflowTagBar
            {
                MoreTextFormat = "+{0} más"
            };

            var tags = new List<StoreTagRef>
            {
                new("Very Long Tag Name 1", false),
                new("Very Long Tag Name 2", false),
                new("Very Long Tag Name 3", false),
                new("Very Long Tag Name 4", false),
                new("Very Long Tag Name 5", false)
            };

            bar.ItemsSource = tags;

            // Initially measure with large width (1000px) so all tags fit
            bar.Measure(new Size(1000, 30));
            Assert.Equal(Visibility.Collapsed, bar.Children[5].Visibility);

            // Now measure and arrange with restricted width (150px)
            bar.Measure(new Size(150, 30));
            bar.Arrange(new Rect(0, 0, 150, 30));

            // Overflow badge (child 5) must now be visible because 5 long tags exceed 150px
            Assert.Equal(Visibility.Visible, bar.Children[5].Visibility);
            // Some tags must now be collapsed
            Assert.Equal(Visibility.Collapsed, bar.Children[4].Visibility);
        });
    }

    [Fact]
    public void Measure_ConsecutivePasses_RemainsStableAndDoesNotOscillate()
    {
        RunOnStaThread(() =>
        {
            var bar = new OverflowTagBar { MoreTextFormat = "+{0} más" };
            var tags = new List<StoreTagRef>
            {
                new("Fabricación", false),
                new("Diseño e ilustración", false),
                new("Acogedores", false),
                new("Simulador de trabajo", false),
                new("Simulación", false),
                new("Relajantes", false),
                new("Un jugador", false)
            };
            bar.ItemsSource = tags;

            // Pass 1: restricted width
            bar.Measure(new Size(250, 30));
            var badge = bar.Children[7] as Border;
            Assert.NotNull(badge);
            Assert.Equal(Visibility.Visible, badge.Visibility); // badge visible

            // Pass 2: same restricted width (like scroll or layout pass)
            bar.Measure(new Size(250, 30));
            Assert.Equal(Visibility.Visible, badge.Visibility); // badge MUST STILL BE VISIBLE!

            // Pass 3: arrange pass
            bar.Arrange(new Rect(0, 0, 250, 30));
            Assert.Equal(Visibility.Visible, badge.Visibility); // badge MUST STILL BE VISIBLE!
        });
    }

    [Fact]
    public void ItemsControl_WithIsGridViewFalse_InitializesPanelCorrectly()
    {
        RunOnStaThread(() =>
        {
            var xaml = @"
                <ItemsControl xmlns=""http://schemas.microsoft.com/winfx/2006/xaml/presentation""
                              xmlns:ctrl=""clr-namespace:BlueStar.App.Controls;assembly=BlueStar"">
                    <ItemsControl.ItemsPanel>
                        <ItemsPanelTemplate>
                            <ctrl:VirtualizingWrapPanel/>
                        </ItemsPanelTemplate>
                    </ItemsControl.ItemsPanel>
                </ItemsControl>";

            var itemsControl = (ItemsControl)System.Windows.Markup.XamlReader.Parse(xaml);
            itemsControl.DataContext = new TestContext { IsGridView = false };

            itemsControl.ApplyTemplate();
            itemsControl.Measure(new Size(1000, 1000));

            var panel = FindChild<VirtualizingWrapPanel>(itemsControl);
            Assert.NotNull(panel);
            Assert.False(panel.IsGridView);
            Assert.Equal(0.0, panel.ItemWidth);
            Assert.Equal(136.0, panel.ItemHeight);
        });
    }

    private class TestContext
    {
        public bool IsGridView { get; set; }
    }

    private static T? FindChild<T>(DependencyObject parent) where T : DependencyObject
    {
        int count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
            if (child is T typed) return typed;
            var sub = FindChild<T>(child);
            if (sub != null) return sub;
        }
        return null;
    }
}
