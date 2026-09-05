using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BlueStar.Core.Models;

namespace BlueStar.Core.Interfaces;

/// <summary>
/// Provides curated or community recommendations for games, advising on the
/// most stable build, tested depot manifest revisions, and recommended emulators.
/// </summary>
public interface IRecommendationProvider : IProvider
{
    /// <summary>
    /// Retrieves a recommendation for a specific game AppID.
    /// Returns null if no curated advice exists (allowing automatic fallback to latest public build).
    /// </summary>
    Task<CurationRecommendation?> GetRecommendationAsync(uint appId, CancellationToken ct = default);

    /// <summary>
    /// Batch-retrieves recommendations for multiple games.
    /// </summary>
    Task<IReadOnlyDictionary<uint, CurationRecommendation>> BatchGetRecommendationsAsync(IEnumerable<uint> appIds, CancellationToken ct = default);
}
