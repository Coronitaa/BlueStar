using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BlueStar.Core.Models;

namespace BlueStar.Core.Interfaces;

/// <summary>
/// Coordinates multiple manifest harvester providers, trying available mechanisms in priority order
/// (e.g. SteamClient local console -> DepotDownloaderMod -> SteamManifestDownloaderAdapter).
/// </summary>
public interface IManifestHarvestCoordinator
{
    /// <summary>
    /// Gets the registered harvester providers ordered by priority.
    /// </summary>
    IReadOnlyList<IManifestHarvesterProvider> Providers { get; }

    /// <summary>
    /// Attempts to harvest the requested manifest by iterating through available providers.
    /// </summary>
    Task<HarvestResult> HarvestManifestAsync(uint appId, uint depotId, ulong manifestId, CancellationToken ct = default);
}
