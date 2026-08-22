namespace BlueStar.Core.Models;

/// <summary>
/// Represents information about a specific depot manifest.
/// </summary>
public record ManifestInfo
{
    /// <summary>
    /// Gets or sets the unique identifier for the depot.
    /// </summary>
    public uint DepotId { get; init; }

    /// <summary>
    /// Gets or sets the unique identifier for the manifest.
    /// </summary>
    public ulong ManifestId { get; init; }

    /// <summary>
    /// Gets or sets the file path to the .manifest file if it is stored locally.
    /// </summary>
    public string? FilePath { get; init; }

    /// <summary>
    /// Gets or sets the total size in bytes for the manifest contents.
    /// </summary>
    public long SizeBytes { get; init; }

    /// <summary>
    /// Gets or sets a value indicating whether the manifest file is downloaded locally.
    /// </summary>
    public bool IsDownloaded { get; init; }
}
