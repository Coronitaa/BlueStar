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

    private void FolderDropZone_DragEnter(object sender, DragEventArgs e)
    {
        if (IsDirectoryDrop(e))
        {
            e.Effects = DragDropEffects.Copy;
            SetFolderDropHighlight(sender as Border, true);
            e.Handled = true;
        }
        else
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
        }
    }

    private void FolderDropZone_DragOver(object sender, DragEventArgs e)
    {
        if (IsDirectoryDrop(e))
        {
            e.Effects = DragDropEffects.Copy;
            SetFolderDropHighlight(sender as Border, true);
            e.Handled = true;
        }
        else
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
        }
    }

    private void FolderDropZone_DragLeave(object sender, DragEventArgs e)
    {
        SetFolderDropHighlight(sender as Border, false);
    }

    private async void FolderDropZone_Drop(object sender, DragEventArgs e)
    {
        SetFolderDropHighlight(sender as Border, false);
        if (IsDirectoryDrop(e))
        {
            var files = (string[])e.Data.GetData(DataFormats.FileDrop);
            if (files != null && files.Length > 0 && System.IO.Directory.Exists(files[0]))
            {
                if (DataContext is LibraryViewModel vm)
                {
                    await vm.LoadFolderAsync(files[0]);
                }
            }
        }
    }

    private static bool IsDirectoryDrop(DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            var files = (string[])e.Data.GetData(DataFormats.FileDrop);
            return files != null && files.Length > 0 && System.IO.Directory.Exists(files[0]);
        }
        return false;
    }

    private void SetFolderDropHighlight(Border? border, bool isHighlighted)
    {
        if (border == null) return;
        if (isHighlighted)
        {
            if (Application.Current.TryFindResource("SuccessBrush") is System.Windows.Media.Brush successBrush)
            {
                border.BorderBrush = successBrush;
            }
            if (Application.Current.TryFindResource("SuccessBgBrush") is System.Windows.Media.Brush successBgBrush)
            {
                border.Background = successBgBrush;
            }
        }
        else
        {
            if (Application.Current.TryFindResource("SubtleBorderBrush") is System.Windows.Media.Brush subtleBrush)
            {
                border.BorderBrush = subtleBrush;
            }
            if (Application.Current.TryFindResource("SurfaceCardBrush") is System.Windows.Media.Brush cardBrush)
            {
                border.Background = cardBrush;
            }
        }
    }
}
