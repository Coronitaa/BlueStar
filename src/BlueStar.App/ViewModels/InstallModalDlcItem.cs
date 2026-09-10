using System.Linq;
using BlueStar.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;

namespace BlueStar.App.ViewModels;

/// <summary>
/// Represents an optional DLC item in the One-Click Install modal.
/// </summary>
public partial class InstallModalDlcItem : ObservableObject
{
    public required DlcInfo Dlc { get; init; }

    [ObservableProperty]
    private bool _isSelected = true;

    public System.Action? OnSelectionChanged { get; set; }

    partial void OnIsSelectedChanged(bool value) => OnSelectionChanged?.Invoke();

    public string Name => Dlc.Name;

    public uint AppId => Dlc.AppId;

    public string FormattedSize
    {
        get
        {
            long bytes = Dlc.Depots != null ? Dlc.Depots.Sum(d => d.SizeBytes) : 0;
            return bytes > 0 ? $"{bytes / (1024.0 * 1024.0):F1} MB" : "Steam DLC";
        }
    }
}
