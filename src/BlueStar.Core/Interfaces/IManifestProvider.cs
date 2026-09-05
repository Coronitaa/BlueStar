using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BlueStar.Core.Models;

namespace BlueStar.Core.Interfaces;

/// <summary>
/// Defines a provider capable of discovering and downloading Steam depot manifests.
/// </summary>
public interface IManifestProvider : IProvider
{
    /// <summary>
    /// Checks whether this provider has manifests available for the specified AppID.
    /// </summary>
    Task<bool> IsAvailableAsync(uint appId, CancellationToken ct = default);

    /// <summary>
    /// Discovers all manifest artifacts known to this provider for the specified AppID.
    /// </summary>
    Task<IReadOnlyList<ManifestArtifact>> DiscoverManifestsAsync(uint appId, CancellationToken ct = default);

    /// <summary>
    /// Downloads or acquires a specific manifest artifact file to the specified target directory.
    /// Returns the absolute path to the downloaded .manifest file.
    /// When appId is known, it should be provided to allow branch-scoped lookup.
    /// </summary>
    Task<string> DownloadManifestAsync(uint depotId, ulong manifestId, string targetDirectory, uint appId = 0, CancellationToken ct = default);

    /// <summary>
    /// Legacy compatibility method for existing callers.
    /// </summary>
    Task<IReadOnlyList<ManifestInfo>> GetManifestsAsync(uint appId, CancellationToken ct = default);
}
