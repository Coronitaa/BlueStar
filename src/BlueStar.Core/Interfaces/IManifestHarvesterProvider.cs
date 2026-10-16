using System.Threading;
using System.Threading.Tasks;
using BlueStar.Core.Models;

namespace BlueStar.Core.Interfaces;

/// <summary>
/// Defines a provider capable of acquiring/harvesting a Steam manifest file (.manifest)
/// using local or specialized mechanisms (Steam Client local console, DepotDownloaderMod, SteamManifestDownloaderAdapter).
/// </summary>
public interface IManifestHarvesterProvider
{
    /// <summary>
    /// Gets the unique identifier/name of this harvester provider.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Priority of this provider (higher priority tried first).
    /// </summary>
    int Priority { get; }

    /// <summary>
    /// Checks whether this provider is currently available on the local machine.
    /// </summary>
    Task<bool> IsAvailableAsync(CancellationToken ct = default);

    /// <summary>
    /// Harvests the specified manifest revision and returns the local file path on success.
    /// </summary>
    Task<HarvestResult> HarvestManifestAsync(uint appId, uint depotId, ulong manifestId, CancellationToken ct = default);
}
