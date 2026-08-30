using System;
using System.Windows;
using System.Windows.Controls;
using BlueStar.App.ViewModels;

namespace BlueStar.App.Views;

/// <summary>
/// Code-behind for InstanceDetailView.
/// </summary>
public partial class InstanceDetailView : UserControl
{
    public InstanceDetailView()
    {
        InitializeComponent();
    }

    private async void DepotDropZone_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            var files = (string[])e.Data.GetData(DataFormats.FileDrop);
            if (files != null && files.Length > 0 && DataContext is InstanceDetailViewModel vm)
            {
                var file = files[0];
                if (file.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ||
                    file.EndsWith(".manifest", StringComparison.OrdinalIgnoreCase))
                {
                    await vm.ImportDepotZipPackageAsync(file);
                }
            }
        }
    }

    private void DepotDropZone_DragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.Copy;
            e.Handled = true;
        }
    }
}
