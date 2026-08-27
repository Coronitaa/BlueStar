using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using BlueStar.App.Controls;
using BlueStar.App.ViewModels;

namespace BlueStar.App.Views;

/// <summary>
/// Browse view for searching DepotBox games and exploring interactive category feeds.
/// </summary>
public partial class BrowseView : UserControl
{
    public BrowseView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is BrowseViewModel vm)
        {
            vm.OnScrollToCategoryRequested = ScrollToCategory;
        }
    }

    /// <summary>
    /// Smoothly scrolls the page down to the target expanded category carousel.
    /// </summary>
    public void ScrollToCategory(string categoryId)
    {
        if (string.IsNullOrWhiteSpace(categoryId)) return;

        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            try
            {
                var carousel = FindVisualChild<GameCategoryCarousel>(this, c =>
                    string.Equals(c.Category?.Id, categoryId, StringComparison.OrdinalIgnoreCase));

                if (carousel != null && BrowseScrollViewer != null)
                {
                    var transform = carousel.TransformToAncestor(BrowseScrollViewer);
                    var point = transform.Transform(new Point(0, 0));
                    var targetOffset = BrowseScrollViewer.VerticalOffset + point.Y - 16;
                    BrowseScrollViewer.ScrollToVerticalOffset(Math.Max(0, targetOffset));
                }
            }
            catch
            {
                // Fallback if visual tree transform not yet ready
            }
        }));
    }

    private static T? FindVisualChild<T>(DependencyObject parent, Func<T, bool> predicate) where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T typed && predicate(typed))
            {
                return typed;
            }

            var sub = FindVisualChild(child, predicate);
            if (sub != null) return sub;
        }
        return null;
    }
}
