using System.Collections.Generic;

namespace BlueStar.Core.Models;

/// <summary>
/// Represents information about downloadable content (DLC).
/// </summary>
public record DlcInfo
{
    /// <summary>
    /// Gets or sets the application identifier for the DLC.
    /// </summary>
    public uint AppId { get; init; }

    /// <summary>
    /// Gets or sets the name of the DLC.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Gets or sets the list of depots required by this DLC.
    /// </summary>
    public IReadOnlyList<DepotInfo> Depots { get; init; } = [];

    /// <summary>
    /// Gets or sets a value indicating whether the DLC is currently installed.
    /// </summary>
    public bool IsInstalled { get; init; }

    /// <summary>
    /// Gets or sets the category of the DLC ("DLC").
    /// </summary>
    public string Category { get; init; } = "DLC";

    /// <summary>
    /// Gets or sets the target platform ("Windows", "Linux", "macOS").
    /// </summary>
    public string Platform { get; init; } = "Windows";

    /// <summary>
    /// Gets total size in bytes for this DLC's depots.
    /// </summary>
    public long TotalSizeBytes => System.Linq.Enumerable.Sum(Depots, d => d.SizeBytes);

    /// <summary>
    /// Gets the Steam store header banner image URL for this DLC.
    /// </summary>
    public string HeaderImageUrl => $"https://cdn.cloudflare.steamstatic.com/steam/apps/{AppId}/header.jpg";
}
