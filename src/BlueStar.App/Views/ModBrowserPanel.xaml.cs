using System.Windows.Controls;
using BlueStar.App.ViewModels;

namespace BlueStar.App.Views;

/// <summary>
/// Interaction logic for ModBrowserPanel.xaml
/// </summary>
public partial class ModBrowserPanel : UserControl
{
    private const double LoadMoreThreshold = 250.0;

    public ModBrowserPanel()
    {
        InitializeComponent();
    }

    private void OnPackagesScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (DataContext is not ModBrowserViewModel vm || vm.Thunderstore == null) return;
        var tstore = vm.Thunderstore;

        // Only trigger when user is scrolling downward and there is actual scrollable content
        if (e.VerticalChange <= 0 || e.ExtentHeight <= 0) return;

        var remaining = e.ExtentHeight - (e.VerticalOffset + e.ViewportHeight);
        if (remaining > LoadMoreThreshold) return;

        if (!tstore.HasMorePackages || tstore.IsLoadingMore || tstore.IsLoading) return;

        var command = tstore.LoadMoreCommand;
        if (command.CanExecute(null))
        {
            command.Execute(null);
        }
    }
}
