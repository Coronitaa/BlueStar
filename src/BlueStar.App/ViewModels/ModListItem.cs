using CommunityToolkit.Mvvm.ComponentModel;

namespace BlueStar.App.ViewModels;

/// <summary>
/// One row in the Mods tab. Managed rows come from the mod ledger (installed by BlueStar's pipeline);
/// unmanaged rows are mods found on disk that BlueStar did not install, handled by the legacy managers.
/// </summary>
public sealed partial class ModListItem : ObservableObject
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string? Version { get; init; }
    public required string SourceLabel { get; init; }
    public string? Detail { get; init; }

    [ObservableProperty]
    private bool _isEnabled = true;

    [ObservableProperty]
    private bool _isSelected;

    public bool IsManaged { get; init; }

    /// <summary>Whether this mod resides in the BepInEx plugins folder or comes from Thunderstore.</summary>
    public bool IsBepInEx { get; init; }

    /// <summary>File or folder of an unmanaged mod.</summary>
    public string? LegacyPath { get; init; }

    public bool HasVersion => !string.IsNullOrWhiteSpace(Version);

    /// <summary>Author or creator subtitle if known.</summary>
    public string? Author { get; init; }

    /// <summary>Size of the mod in bytes.</summary>
    public long SizeBytes { get; init; }

    /// <summary>Formatted file size or count summary (e.g. "4.2 MB · 12 files").</summary>
    public string? FormattedSize { get; init; }

    /// <summary>Web page URL for the mod (Thunderstore page, Workshop item URL, etc.).</summary>
    public string? PageUrl { get; init; }

    /// <summary>Remote or local image icon URL for the mod if available.</summary>
    public string? IconUrl { get; init; }

    /// <summary>Geometry resource key for the icon (IconSteam, IconBolt, IconEngine, IconMod, etc.).</summary>
    public string IconGeometryKey => (SourceLabel.Contains("Workshop", System.StringComparison.OrdinalIgnoreCase) || Detail?.Contains("Workshop", System.StringComparison.OrdinalIgnoreCase) == true)
        ? "IconSteam"
        : (SourceLabel.Contains("Thunderstore", System.StringComparison.OrdinalIgnoreCase) || Detail?.Contains("Thunderstore", System.StringComparison.OrdinalIgnoreCase) == true)
            ? "IconBolt"
            : (IsBepInEx || SourceLabel.Contains("BepInEx", System.StringComparison.OrdinalIgnoreCase) || Detail?.Contains("BepInEx", System.StringComparison.OrdinalIgnoreCase) == true)
                ? "IconEngine"
                : "IconMod";
}
