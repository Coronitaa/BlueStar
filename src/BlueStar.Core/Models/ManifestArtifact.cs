using System.Collections.Generic;

namespace BlueStar.Core.Models;

/// <summary>
/// Represents the intrinsic identity and integrity of a Steam depot manifest,
/// independent of which provider, mirror, or local path supplied it.
/// </summary>
public record ManifestArtifact
{
    /// <summary>
    /// Gets the Steam Depot ID this manifest belongs to.
    /// </summary>
    public uint DepotId { get; init; }

    /// <summary>
    /// Gets the Steam Manifest ID (GID / unique revision identifier).
    /// </summary>
    public ulong ManifestId { get; init; }

    /// <summary>
    /// Gets the total payload size of the manifest in bytes, if known.
    /// </summary>
    public long SizeBytes { get; init; }

    /// <summary>
    /// Gets the SHA-256 checksum of the manifest file if verified.
    /// </summary>
    public string? ChecksumSha256 { get; init; }

    /// <summary>
    /// Gets the local absolute path where the .manifest file is cached or stored, if available.
    /// </summary>
    public string? LocalCachePath { get; init; }

    /// <summary>
    /// Gets the list of available acquisition routes discovered across providers for this manifest.
    /// </summary>
    public IReadOnlyList<ManifestSourceRoute> Routes { get; init; } = [];

    /// <summary>
    /// Gets a value indicating whether the manifest artifact is fully verified and available on local disk.
    /// </summary>
    public bool IsDownloaded => !string.IsNullOrWhiteSpace(LocalCachePath) && System.IO.File.Exists(LocalCachePath);

    /// <summary>
    /// Gets the canonical filename convention for this manifest ("{depotId}_{manifestId}.manifest").
    /// </summary>
    public string CanonicalFileName => $"{DepotId}_{ManifestId}.manifest";

    /// <summary>
    /// Human-friendly representation of manifest identity.
    /// </summary>
    public override string ToString() => $"{DepotId}_{ManifestId}";
}
