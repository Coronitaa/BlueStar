using BlueStar.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;

namespace BlueStar.App.ViewModels;

/// <summary>
/// One game fix offered in the install modal, with its tick box.
/// </summary>
/// <remarks>
/// Only online fixes are exclusive: they replace the emulator, so they live in the emulator radio
/// group. Bypasses, hypervisor fixes and everything else are layers — they sit on top of whatever
/// emulator was chosen, ReFix included, and several can apply at once. Those are the ones this
/// wraps, so the modal can offer them as a set of tick boxes rather than one more radio.
/// </remarks>
public partial class InstallModalFixItem : ObservableObject
{
    /// <summary>The fix this row stands for.</summary>
    public GameFixInfo Fix { get; }

    [ObservableProperty]
    private bool _isSelected;

    /// <summary>
    /// Initializes a new instance of the <see cref="InstallModalFixItem"/> class.
    /// </summary>
    public InstallModalFixItem(GameFixInfo fix, bool isSelected = false)
    {
        Fix = fix;
        _isSelected = isSelected;
    }

    /// <summary>Name shown on the row.</summary>
    public string Name => Fix.Name;

    /// <summary>Short label saying what kind of fix it is.</summary>
    public string Kind => Fix.IsBypass ? "BYPASS" : Fix.IsHypervisor ? "HYPERVISOR" : "FIX";

    /// <summary>Size and tags, for the secondary line.</summary>
    public string Detail => string.IsNullOrWhiteSpace(Fix.Description)
        ? Fix.FormattedSize
        : Fix.Description;
}
