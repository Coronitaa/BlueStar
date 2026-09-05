using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BlueStar.Core.Models;

namespace BlueStar.Core.Interfaces;

/// <summary>
/// Resolves game versions and builds across local inventories, community curation,
/// SteamCMD branch data, and provider manifest availability.
/// </summary>
public interface IBuildResolver
{
    /// <summary>
    /// Enumerates all known available builds/versions for an application.
    /// </summary>
    Task<IReadOnlyList<GameVersion>> GetAvailableVersionsAsync(uint appId, CancellationToken ct = default);

    /// <summary>
    /// Resolves the recommended version for an application, falling back gracefully:
    /// Curated Recommendation → SteamCMD Public Branch → Local Inventory → Best Inferred.
    /// </summary>
    Task<GameVersion?> ResolveRecommendedVersionAsync(uint appId, CancellationToken ct = default);

    /// <summary>
    /// Resolves the latest official public release version for an application.
    /// </summary>
    Task<GameVersion?> ResolveLatestVersionAsync(uint appId, CancellationToken ct = default);

    /// <summary>
    /// Resolves a specific version by its Build ID or branch name.
    /// </summary>
    Task<GameVersion?> ResolveVersionAsync(uint appId, string buildIdOrBranch, CancellationToken ct = default);
}
