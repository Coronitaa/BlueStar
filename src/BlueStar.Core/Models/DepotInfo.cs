namespace BlueStar.Core.Models;

/// <summary>
/// Represents information about a game depot.
/// </summary>
public record DepotInfo
{
    /// <summary>
    /// Gets or sets the unique identifier for the depot.
    /// </summary>
    public uint DepotId { get; init; }

    /// <summary>
    /// Gets or sets the manifest identifier associated with this depot.
    /// </summary>
    public ulong ManifestId { get; init; }

    /// <summary>
    /// Gets or sets the size of the depot in bytes.
    /// </summary>
    public long SizeBytes { get; init; }

    /// <summary>
    /// Gets or sets the 64-character hexadecimal decryption key for the depot, if available.
    /// </summary>
    public string? DepotKey { get; init; }

    /// <summary>
    /// Gets or sets the name of the depot.
    /// </summary>
    public string? Name { get; init; }

    /// <summary>
    /// Gets or sets a value indicating whether this is a shared depot.
    /// </summary>
    public bool IsSharedDepot { get; init; }

    /// <summary>
    /// Gets or sets the category of the depot ("Base Game" or "DLC").
    /// </summary>
    public string Category { get; init; } = "Base Game";

    /// <summary>
    /// Gets or sets the target platform ("Windows", "Linux", "macOS", "Universal").
    /// </summary>
    public string Platform { get; init; } = "Windows";

    /// <summary>
    /// Gets or sets the target architecture ("64-bit", "32-bit", or null).
    /// </summary>
    public string? Architecture { get; init; }

    /// <summary>
    /// Gets or sets a value indicating whether this depot is currently downloaded/installed.
    /// </summary>
    public bool IsDownloaded { get; init; } = false;
}

