using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BlueStar.Core.Models;

namespace BlueStar.Core.Interfaces;

/// <summary>
/// Defines extended capabilities for community Manifest Hub interactions,
/// including remote catalog coverage querying, build snapshot retrieval, and differential comparisons.
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
    /// Fetches paginated build history for the specified Steam Application ID and branch.
    /// </summary>
    Task<BuildHistoryPage> GetBuildHistoryPagedAsync(uint appId, string? branch = "public", int page = 1, int pageSize = 20, CancellationToken ct = default);

    /// <summary>
    /// Fetches a specific build snapshot by its BuildId.
    /// </summary>
    Task<BuildSnapshot?> GetSnapshotByIdAsync(uint appId, ulong buildId, CancellationToken ct = default);

    /// <summary>
    /// Compares two build snapshots and computes the differential depot/manifest plan.
    /// </summary>
    Task<SnapshotDiffResult?> CompareSnapshotsAsync(uint appId, ulong? baseBuildId, ulong targetBuildId, CancellationToken ct = default);

    /// <summary>
    /// Fetches the active community manifest demand index (indices/demand.json).
    /// </summary>
    Task<DemandIndex?> GetDemandAsync(CancellationToken ct = default);
}
