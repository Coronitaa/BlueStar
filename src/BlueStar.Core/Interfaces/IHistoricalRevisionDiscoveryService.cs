using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BlueStar.Core.Models;

namespace BlueStar.Core.Interfaces;

/// <summary>
/// Discovers and unifies historical manifest revisions across multiple allowed community sources:
/// - BlueStar-ManifestHub (Coronitaa/BlueStar-ManifestHub)
/// - SSMGAlt/ManifestHub2
/// - K25 (qwe213312/k25FCdfEOoEJ42S6)
/// - DepotBox (bounded manifest inspection only; zero payload downloads)
/// 
/// INVARIANTS:
/// 1. Unifies candidates strictly by exact (AppId, DepotId, ManifestGid).
/// 2. Preserves authentic publication dates and never estimates or fabricates timestamps.
/// 3. Does NOT download game payloads; retrieves only individual manifest binaries when requested.
/// </summary>
public interface IHistoricalRevisionDiscoveryService
{
    /// <summary>
    /// Discovers all known historical revisions for a single Steam App ID across all allowed sources,
    /// unifying duplicates and ordering by authentic publication date.
    /// </summary>
    /// <param name="appId">The Steam Application identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A unified, chronologically transparent list of revision candidates.</returns>
    Task<IReadOnlyList<HubRevisionItem>> DiscoverRevisionsAsync(uint appId, CancellationToken ct = default);

    /// <summary>
    /// Discovers historical revisions for multiple Steam Application IDs concurrently.
    /// </summary>
    /// <param name="appIds">Collection of application IDs.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A dictionary mapping each App ID to its unified list of revision items.</returns>
    Task<IReadOnlyDictionary<uint, IReadOnlyList<HubRevisionItem>>> DiscoverRevisionsForAppsAsync(IEnumerable<uint> appIds, CancellationToken ct = default);

    /// <summary>
    /// Fetches a specific manifest binary file across the unified sources
    /// (trying ManifestHub, ManifestHub2, K25, or DepotBox in priority order)
    /// without downloading full game payload archives.
    /// </summary>
    /// <param name="appId">The Steam Application identifier.</param>
    /// <param name="depotId">The Depot identifier.</param>
    /// <param name="manifestId">The Manifest revision identifier (GID).</param>
    /// <param name="targetDirectory">Directory where the acquired .manifest file should be saved.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The local file path of the validated manifest file.</returns>
    Task<string> FetchManifestBinaryAsync(uint appId, uint depotId, ulong manifestId, string targetDirectory, CancellationToken ct = default);
}
