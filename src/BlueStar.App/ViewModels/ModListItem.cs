namespace BlueStar.App.ViewModels;

/// <summary>
/// One row in the Mods tab. Managed rows come from the mod ledger (installed by BlueStar's pipeline);
/// unmanaged rows are mods found on disk that BlueStar did not install, handled by the legacy managers.
/// </summary>
public sealed record ModListItem
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string? Version { get; init; }
    public required string SourceLabel { get; init; }
    public string? Detail { get; init; }
    public bool IsEnabled { get; init; } = true;
    public bool IsManaged { get; init; }

    /// <summary>File or folder of an unmanaged mod.</summary>
    public string? LegacyPath { get; init; }

    public bool HasVersion => !string.IsNullOrWhiteSpace(Version);

    /// <summary>Author or creator subtitle if known.</summary>
    public string? Author { get; init; }

    /// <summary>Size of the mod in bytes.</summary>
    public long SizeBytes { get; init; }

    /// <summary>Formatted file size or count summary (e.g. "4.2 MB · 12 files").</summary>
    public string? FormattedSize { get; init; }

    /// <summary>Geometry resource key for the icon (IconSteam, IconBolt, IconMod, etc.).</summary>
    public string IconGeometryKey => SourceLabel.Contains("Workshop", System.StringComparison.OrdinalIgnoreCase)
        ? "IconSteam"
        : SourceLabel.Contains("Thunderstore", System.StringComparison.OrdinalIgnoreCase)
            ? "IconBolt"
            : "IconMod";
}
