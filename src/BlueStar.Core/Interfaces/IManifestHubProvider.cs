using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BlueStar.Core.Models;

namespace BlueStar.Core.Interfaces;

/// <summary>
/// Defines extended capabilities for community Manifest Hub interactions,
/// including remote catalog coverage querying and build snapshot retrieval.
/// </summary>
public interface IManifestHubProvider : IManifestProvider
{
    /// <summary>
    /// Fetches remote manifest coverage from Manifest Hub for the specified Steam Application IDs.
    /// </summary>
    Task<IReadOnlyList<ManifestArtifact>> GetCoverageAsync(IEnumerable<uint> appIds, CancellationToken ct = default);

    /// <summary>
    /// Fetches community build snapshots for the specified Steam Application ID.
    /// </summary>
    Task<IReadOnlyList<BuildSnapshot>> GetAppSnapshotsAsync(uint appId, CancellationToken ct = default);

    /// <summary>
    /// Fetches historical revisions for the specified Steam Application ID.
    /// </summary>
    Task<IReadOnlyList<HubRevisionItem>> GetRevisionsForAppAsync(uint appId, CancellationToken ct = default);

    /// <summary>
    /// Fetches the active community manifest demand index (indices/demand.json).
    /// </summary>
    Task<DemandIndex?> GetDemandAsync(CancellationToken ct = default);
}
