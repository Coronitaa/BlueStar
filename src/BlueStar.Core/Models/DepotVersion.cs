using System.Collections.Generic;

namespace BlueStar.Core.Models;

/// <summary>
/// Represents a specific version of a depot within a game build, binding the depot
/// to its manifest revision, decryption key, and available multi-provider routes.
/// </summary>
public record DepotVersion
{
    /// <summary>
    /// Gets the Steam Depot ID.
    /// </summary>
    public uint DepotId { get; init; }

    /// <summary>
    /// Gets the specific Manifest ID for this version of the depot.
    /// </summary>
    public ulong ManifestId { get; init; }

    /// <summary>
    /// Gets the depot friendly name.
    /// </summary>
    public string? Name { get; init; }

    /// <summary>
    /// Gets the depot size in bytes.
    /// </summary>
    public long SizeBytes { get; init; }

    /// <summary>
    /// Gets the 64-character hexadecimal decryption key for this depot, if known.
    /// </summary>
    public string? DepotKey { get; init; }

    /// <summary>
    /// Gets the depot category ("Base Game" or "DLC").
    /// </summary>
    public string Category { get; init; } = "Base Game";

    /// <summary>
    /// Gets the target platform ("Windows", "Linux", "macOS", "Universal").
    /// </summary>
    public string Platform { get; init; } = "Windows";

    /// <summary>
    /// Gets the target architecture ("64-bit", "32-bit", or null).
    /// </summary>
    public string? Architecture { get; init; }

    /// <summary>
    /// Gets a value indicating whether this depot is part of the core/recommended game install.
    /// </summary>
    public bool IsRecommended { get; init; } = true;


    /// <summary>
    /// Creates a compatible <see cref="DepotInfo"/> from this depot version.
    /// </summary>
    public DepotInfo ToDepotInfo(bool isDownloaded = false) => new()
    {
        DepotId = DepotId,
        ManifestId = ManifestId,
        Name = Name,
        SizeBytes = SizeBytes,
        DepotKey = DepotKey,
        Category = Category,
        Platform = Platform,
        Architecture = Architecture,
        IsRecommended = IsRecommended,
        IsDownloaded = isDownloaded
    };
}
