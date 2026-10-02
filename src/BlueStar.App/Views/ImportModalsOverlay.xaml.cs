using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using BlueStar.App.ViewModels;

namespace BlueStar.App.Views;

/// <summary>
/// Centralized modal overlays for importing instances from Steam, ZIP depot archives, or existing folders.
/// Hosted at the window root to allow seamless import from the sidebar "+" button from any tab or from Home.
/// </summary>
public partial class ImportModalsOverlay : UserControl
{
    public ImportModalsOverlay()
    {
        InitializeComponent();
    }

    private async void OnZipDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            var files = (string[])e.Data.GetData(DataFormats.FileDrop);
            if (files != null && files.Length > 0 && files[0].EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                if (DataContext is HomeViewModel vm)
                {
                    await vm.LoadZipFileAsync(files[0]);
                }
            }
        }
    }

    private async void OnFolderDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            var files = (string[])e.Data.GetData(DataFormats.FileDrop);
            if (files != null && files.Length > 0 && Directory.Exists(files[0]))
            {
                if (DataContext is HomeViewModel vm)
                {
                    await vm.LoadFolderAsync(files[0]);
                }
            }
        }
    }
}
