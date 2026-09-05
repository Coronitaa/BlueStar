using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BlueStar.Core.Models;

namespace BlueStar.Core.Interfaces;

/// <summary>
/// Provides catalog search, game discovery, and official store metadata
/// independently of manifest availability or download providers.
/// </summary>
public interface IGameCatalogProvider : IProvider
{
    /// <summary>
    /// Searches the catalog for games matching the specified query.
    /// </summary>
    Task<IReadOnlyList<SearchResult>> SearchGamesAsync(string query, CancellationToken ct = default);

    /// <summary>
    /// Retrieves full store metadata for an application by its Steam AppID.
    /// </summary>
    Task<GameMetadata?> GetGameDetailsAsync(uint appId, CancellationToken ct = default);

    /// <summary>
    /// Retrieves the list of known DLCs for an application.
    /// </summary>
    Task<IReadOnlyList<DlcInfo>> GetDlcListAsync(uint appId, CancellationToken ct = default);
}
