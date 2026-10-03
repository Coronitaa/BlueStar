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

    private void OnFolderDragEnter(object sender, DragEventArgs e)
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

    private void OnFolderDragOver(object sender, DragEventArgs e)
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

    private void OnFolderDragLeave(object sender, DragEventArgs e)
    {
        SetFolderDropHighlight(sender as Border, false);
    }

    private async void OnFolderDrop(object sender, DragEventArgs e)
    {
        SetFolderDropHighlight(sender as Border, false);
        if (IsDirectoryDrop(e))
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

    private static bool IsDirectoryDrop(DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            var files = (string[])e.Data.GetData(DataFormats.FileDrop);
            return files != null && files.Length > 0 && Directory.Exists(files[0]);
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
