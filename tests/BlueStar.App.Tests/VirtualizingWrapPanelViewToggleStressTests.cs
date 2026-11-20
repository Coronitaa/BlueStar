using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using BlueStar.App.Controls;
using Xunit;

namespace BlueStar.App.Tests;

public class VirtualizingWrapPanelViewToggleStressTests
{
    private static void RunOnStaThread(Action action) => StaTestHelper.Run(action);

    public class TestItem
    {
        public string Title { get; set; } = "";
    }

    public class TestContext : System.ComponentModel.INotifyPropertyChanged
    {
        private bool _isGridView = false;
        public bool IsGridView
        {
            get => _isGridView;
            set
            {
                if (_isGridView != value)
                {
                    _isGridView = value;
                    PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(IsGridView)));
                }
            }
        }
        public ObservableCollection<TestItem> Items { get; set; } = new();

        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    }

    private static (ScrollViewer scroller, VirtualizingWrapPanel panel, TestContext ctx) CreateTestSetup(int itemCount = 300)
    {
        var ctx = new TestContext();
        for (int i = 0; i < itemCount; i++)
        {
            ctx.Items.Add(new TestItem { Title = $"Item {i}" });
        }

        var cardTemplate = new DataTemplate();
        var cardFactory = new FrameworkElementFactory(typeof(Border));
        cardFactory.SetValue(Border.HeightProperty, 350.0);
        cardFactory.SetValue(Border.WidthProperty, 300.0);
        cardTemplate.VisualTree = cardFactory;

        var rowTemplate = new DataTemplate();
        var rowFactory = new FrameworkElementFactory(typeof(Border));
        rowFactory.SetValue(Border.HeightProperty, 128.0);
        rowTemplate.VisualTree = rowFactory;

        var itemsControl = new ItemsControl
        {
            ItemsSource = ctx.Items
        };

        var style = new Style(typeof(ItemsControl));
        style.Setters.Add(new Setter(ItemsControl.ItemTemplateProperty, cardTemplate));

        var trigger = new DataTrigger
        {
            Binding = new System.Windows.Data.Binding("IsGridView"),
            Value = false
        };
        trigger.Setters.Add(new Setter(ItemsControl.ItemTemplateProperty, rowTemplate));
        style.Triggers.Add(trigger);

        itemsControl.Style = style;

        var panelFactory = new FrameworkElementFactory(typeof(VirtualizingWrapPanel));
        panelFactory.SetBinding(VirtualizingWrapPanel.IsGridViewProperty,
            new System.Windows.Data.Binding("DataContext.IsGridView")
            {
                RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.FindAncestor, typeof(ItemsControl), 1)
            });
        itemsControl.ItemsPanel = new ItemsPanelTemplate(panelFactory);

        var stack = new StackPanel();
        stack.Children.Add(itemsControl);

        var scroller = new ScrollViewer
        {
            Width = 1000,
            Height = 800,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = stack
        };

        scroller.DataContext = ctx;
        scroller.ApplyTemplate();

        scroller.Measure(new Size(1000, 800));
        scroller.Arrange(new Rect(0, 0, 1000, 800));
        scroller.UpdateLayout();

        var panel = FindChild<VirtualizingWrapPanel>(itemsControl);
        Assert.NotNull(panel);
        panel.UpdateLayout();

        return (scroller, panel, ctx);
    }

    [Fact]
    public void StressTest_ScrollAndToggleGridList_NeverDropsContainersOrHangs()
    {
        RunOnStaThread(() =>
        {
            var (scroller, panel, ctx) = CreateTestSetup(300);

            // Initially List view
            Assert.False(panel.IsGridView);
            int childCount = VisualTreeHelper.GetChildrenCount(panel);
            Assert.True(childCount > 0, $"Initial list view must have realized children. Children.Count={panel.Children.Count}, VisualChildren={childCount}");

            var sw = Stopwatch.StartNew();

            void RefreshLayout()
            {
                scroller.InvalidateMeasure();
                scroller.UpdateLayout();
            }

            // Perform multiple cycles of scrolling and toggling
            for (int cycle = 0; cycle < 5; cycle++)
            {
                // 1. Scroll down in List view
                for (double offset = 0; offset <= 3000; offset += 300)
                {
                    scroller.ScrollToVerticalOffset(offset);
                    RefreshLayout();
                    Assert.True(panel.Children.Count > 0, $"Containers vanished at list scroll {offset} in cycle {cycle}");
                }

                // 2. Toggle to Grid view while scrolled
                ctx.IsGridView = true;
                RefreshLayout();
                int visChildren = VisualTreeHelper.GetChildrenCount(panel);
                Assert.True(panel.IsGridView, "Panel must be in Grid view");
                Assert.True(panel.Children.Count > 0,
                    $"Containers vanished after switching to Grid in cycle {cycle}. Children.Count={panel.Children.Count}, VisualChildren={visChildren}");

                // 3. Scroll in Grid view
                for (double offset = 3000; offset >= 500; offset -= 400)
                {
                    scroller.ScrollToVerticalOffset(offset);
                    RefreshLayout();
                    Assert.True(panel.Children.Count > 0, $"Containers vanished at grid scroll {offset} in cycle {cycle}");
                }

                // 4. Toggle back to List view
                ctx.IsGridView = false;
                RefreshLayout();
                Assert.False(panel.IsGridView, "Panel must be in List view");
                Assert.True(panel.Children.Count > 0, $"Containers vanished after switching back to List in cycle {cycle}");
            }

            sw.Stop();
            // Entire stress test (5 cycles of 20 layout passes = 100 passes + 10 view switches) should finish well under 2 seconds!
            Assert.True(sw.ElapsedMilliseconds < 2500, $"Stress test took too long: {sw.ElapsedMilliseconds}ms");
        });
    }

    [Fact]
    public void RapidViewToggle_AtVariousOffsets_RemainsCompletelyStable()
    {
        RunOnStaThread(() =>
        {
            var (scroller, panel, ctx) = CreateTestSetup(300);

            double[] testOffsets = { 0, 500, 1500, 4000, 10000 };

            void RefreshLayout()
            {
                scroller.InvalidateMeasure();
                scroller.UpdateLayout();
            }

            foreach (var offset in testOffsets)
            {
                scroller.ScrollToVerticalOffset(offset);
                RefreshLayout();

                for (int t = 0; t < 6; t++)
                {
                    ctx.IsGridView = !ctx.IsGridView;
                    RefreshLayout();

                    Assert.True(panel.Children.Count > 0,
                        $"Containers vanished during rapid toggle {t} at offset {offset}. Panel.IsGridView={panel.IsGridView}");
                    Assert.True(panel.DesiredSize.Height > 0,
                        $"DesiredSize collapsed to 0 during toggle {t} at offset {offset}");
                }
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
