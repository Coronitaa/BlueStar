using System;

namespace BlueStar.Core.Models;

/// <summary>
/// Specifies the type of distribution route for acquiring a manifest artifact.
/// </summary>
public enum ManifestSourceType
{
    /// <summary>Direct byte stream or file download via HTTP/HTTPS.</summary>
    DirectHttp = 0,

    /// <summary>Raw file hosted in a GitHub repository branch or tree.</summary>
    GitHubRaw = 1,

    /// <summary>Extracted on-the-fly from a multi-depot zip archive.</summary>
    ArchiveZip = 2,

    /// <summary>Already present in local application data or cache directory.</summary>
    LocalCache = 3,

    /// <summary>User-imported local filesystem file.</summary>
    LocalFile = 4,

    /// <summary>Dynamically generated from known key and catalog state.</summary>
    Synthetic = 5
}

/// <summary>
/// Represents a concrete discovery route or download source for a manifest artifact.
/// </summary>
public record ManifestSourceRoute
{
    /// <summary>
    /// Gets the unique identifier of the provider that discovered this route (e.g. "depotbox", "manifesthub", "local").
    /// </summary>
    public string ProviderId { get; init; } = string.Empty;

    /// <summary>
    /// Gets the user-friendly name of the provider.
    /// </summary>
    public string ProviderName { get; init; } = string.Empty;

    /// <summary>
    /// Gets the download or acquisition URI/path for this manifest.
    /// </summary>
    public string? DownloadUrl { get; init; }

    /// <summary>
    /// Gets the type of route.
    /// </summary>
    public ManifestSourceType RouteType { get; init; } = ManifestSourceType.DirectHttp;

    /// <summary>
    /// Gets the provider priority (higher values are attempted first; local cache is highest).
    /// </summary>
    public int Priority { get; init; } = 100;

    /// <summary>
    /// Gets the timestamp when this route was discovered.
    /// </summary>
    public DateTimeOffset DiscoveredAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Gets the timestamp of the last successful download using this route.
    /// </summary>
    public DateTimeOffset? LastSuccessAt { get; init; }

    /// <summary>
    /// Gets a value indicating whether this route is currently healthy and not circuit-broken.
    /// </summary>
    public bool IsHealthy { get; init; } = true;

    /// <summary>
    /// Gets the last error message encountered when attempting this route, if any.
    /// </summary>
    public string? LastError { get; init; }
}
