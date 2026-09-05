using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BlueStar.Core.Models;

namespace BlueStar.Core.Interfaces;

/// <summary>
/// Coordinates multiple manifest providers, managing discovery aggregation,
/// failover, and artifact resolution across all registered sources.
/// </summary>
public interface IManifestRegistry
{
    /// <summary>
    /// Gets all currently registered manifest providers ordered by priority.
    /// </summary>
    IReadOnlyList<IManifestProvider> Providers { get; }

    /// <summary>
    /// Registers a new manifest provider.
    /// </summary>
    void RegisterProvider(IManifestProvider provider);

    /// <summary>
    /// Aggregates and deduplicates manifest discovery across all available providers for an AppID.
    /// </summary>
    Task<IReadOnlyList<ManifestArtifact>> DiscoverManifestsAsync(uint appId, CancellationToken ct = default);

    /// <summary>
    /// Acquires a manifest artifact file by trying local cache first, followed by available providers
    /// in priority order, with automatic failover if a provider fails.
    /// When appId is known, pass it to enable branch-scoped provider lookups.
    /// </summary>
    Task<string?> AcquireManifestAsync(uint depotId, ulong manifestId, uint appId = 0, string? preferredProviderId = null, CancellationToken ct = default);
}
