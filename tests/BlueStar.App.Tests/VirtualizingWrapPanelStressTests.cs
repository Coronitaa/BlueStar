using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using BlueStar.App.Controls;
using BlueStar.App.ViewModels;
using Xunit;

namespace BlueStar.App.Tests;

public class VirtualizingWrapPanelStressTests
{
    private static void RunOnStaThread(Action action) => StaTestHelper.Run(action);

    public class MockItem
    {
        public string Title { get; set; } = "";
    }

    [Fact]
    public void StressTest_ScrollingAndViewToggling_DoesNotThrowOrCollapse()
    {
        RunOnStaThread(() =>
        {
            var items = new ObservableCollection<MockItem>();
            for (int i = 0; i < 100; i++)
            {
                items.Add(new MockItem { Title = $"Game {i}" });
            }

            var panel = new VirtualizingWrapPanel();
            var itemsControl = new ItemsControl
            {
                ItemsSource = items
            };

            var scroller = new ScrollViewer
            {
                Width = 1000,
                Height = 800,
                Content = itemsControl
            };

            // Set ItemsPanel to VirtualizingWrapPanel
            var template = new ItemsPanelTemplate(new FrameworkElementFactory(typeof(VirtualizingWrapPanel)));
            itemsControl.ItemsPanel = template;
            itemsControl.ApplyTemplate();

            // Measure & Arrange window-like tree
            scroller.Measure(new Size(1000, 800));
            scroller.Arrange(new Rect(0, 0, 1000, 800));

            // Find panel
            var actualPanel = FindChild<VirtualizingWrapPanel>(itemsControl);
            Assert.NotNull(actualPanel);

            // 1. Initial list view
            actualPanel.IsGridView = false;
            actualPanel.Measure(new Size(1000, 800));
            actualPanel.Arrange(new Rect(0, 0, 1000, 800));

            Assert.True(actualPanel.DesiredSize.Height > 0, "Panel height must be > 0 in list view");
            Assert.True(actualPanel.Children.Count > 0, "Panel should have realized containers");

            // 2. Scroll through list view
            for (double scrollY = 0; scrollY <= 2000; scrollY += 150)
            {
                actualPanel.SetVerticalOffset(scrollY);
                actualPanel.Measure(new Size(1000, 800));
                actualPanel.Arrange(new Rect(0, 0, 1000, 800));
                Assert.True(actualPanel.Children.Count > 0, $"Containers vanished at scroll {scrollY}");
            }

            // 3. Toggle to Grid view
            actualPanel.IsGridView = true;
            actualPanel.Measure(new Size(1000, 800));
            actualPanel.Arrange(new Rect(0, 0, 1000, 800));

            Assert.True(actualPanel.Children.Count > 0, "Containers vanished after toggle to Grid");

            // 4. Scroll through grid view
            for (double scrollY = 0; scrollY <= 2000; scrollY += 200)
            {
                actualPanel.SetVerticalOffset(scrollY);
                actualPanel.Measure(new Size(1000, 800));
                actualPanel.Arrange(new Rect(0, 0, 1000, 800));
                Assert.True(actualPanel.Children.Count > 0, $"Containers vanished at scroll {scrollY} in grid");
            }

            // 5. Toggle back to List view
            actualPanel.IsGridView = false;
            actualPanel.Measure(new Size(1000, 800));
            actualPanel.Arrange(new Rect(0, 0, 1000, 800));

            Assert.True(actualPanel.Children.Count > 0, "Containers vanished after toggle back to List");

            // 6. Scroll through list view again
            for (double scrollY = 2000; scrollY >= 0; scrollY -= 150)
            {
                actualPanel.SetVerticalOffset(scrollY);
                actualPanel.Measure(new Size(1000, 800));
                actualPanel.Arrange(new Rect(0, 0, 1000, 800));
                Assert.True(actualPanel.Children.Count > 0, $"Containers vanished at scroll {scrollY} scrolling up");
            }
        });
    }

    private static T? FindChild<T>(DependencyObject parent) where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T typed) return typed;
            var sub = FindChild<T>(child);
            if (sub != null) return sub;
        }
        return null;
    }
}
