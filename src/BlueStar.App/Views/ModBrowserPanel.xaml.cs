using System;
using System.Windows;
using System.Windows.Controls;
using BlueStar.App.ViewModels;

namespace BlueStar.App.Views;

/// <summary>
/// Interaction logic for ModBrowserPanel.xaml
/// </summary>
public partial class ModBrowserPanel : UserControl
{
    private ThunderstoreBrowserViewModel? _boundThunderstore;

    public ModBrowserPanel()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        UnhookThunderstore();
        HookThunderstore();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        HookThunderstore();
        if (_boundThunderstore != null && PackagesScroller != null && PackagesScroller.ViewportHeight > 0)
        {
            _boundThunderstore.UpdateScrollWindow(PackagesScroller.VerticalOffset, PackagesScroller.ViewportHeight);
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        UnhookThunderstore();
    }

    private void HookThunderstore()
    {
        if (DataContext is ModBrowserViewModel vm && vm.Thunderstore != null)
        {
            if (!ReferenceEquals(_boundThunderstore, vm.Thunderstore))
            {
                UnhookThunderstore();
                _boundThunderstore = vm.Thunderstore;
                _boundThunderstore.RequestScrollToTop += OnRequestScrollToTop;
            }
        }
    }

    private void UnhookThunderstore()
    {
        if (_boundThunderstore != null)
        {
            _boundThunderstore.RequestScrollToTop -= OnRequestScrollToTop;
            _boundThunderstore = null;
        }
    }

    private void OnRequestScrollToTop()
    {
        PackagesScroller?.ScrollToTop();
    }

    private void OnPackagesScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (DataContext is not ModBrowserViewModel vm || vm.Thunderstore == null) return;
        var tstore = vm.Thunderstore;

        tstore.UpdateScrollWindow(e.VerticalOffset, e.ViewportHeight);
    }
}
