using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BlueStar.Core.Models;

namespace BlueStar.Core.Interfaces;

/// <summary>
/// Provides methods to discover downloadable content (DLC) for a game.
/// </summary>
public interface IDlcProvider
{
    /// <summary>
    /// Discovers the DLCs available for a specific base application.
    /// </summary>
    /// <param name="baseAppId">The application identifier of the base game.</param>
    /// <param name="ct">A token to monitor for cancellation requests.</param>
    /// <returns>A task that represents the asynchronous operation. The task result contains a read-only list of DLC information.</returns>
    Task<IReadOnlyList<DlcInfo>> DiscoverDlcsAsync(uint baseAppId, CancellationToken ct);
}
