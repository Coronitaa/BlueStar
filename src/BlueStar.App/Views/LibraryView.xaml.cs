using System;
using System.Windows;
using System.Windows.Controls;
using BlueStar.App.ViewModels;

namespace BlueStar.App.Views;

/// <summary>
/// Library view showing managed game instances.
/// </summary>
public partial class LibraryView : UserControl
{
    /// <summary>
    /// Initializes the library view.
    /// </summary>
    public LibraryView()
    {
        InitializeComponent();
    }

    private async void ZipDropZone_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            var files = (string[])e.Data.GetData(DataFormats.FileDrop);
            if (files != null && files.Length > 0 && files[0].EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                if (DataContext is LibraryViewModel vm)
                {
                    await vm.LoadZipFileAsync(files[0]);
                }
            }
        }
    }

    private void ZipDropZone_DragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.Copy;
            e.Handled = true;
        }
    }
}
