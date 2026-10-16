using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BlueStar.Core.Models;

namespace BlueStar.Core.Interfaces;

/// <summary>
/// Orchestrates the discovery of contributable manifests from user licenses crossed with community demand,
/// multi-mechanism manifest harvesting, integrity verification, publication, and Stardust rewarding.
/// </summary>
public interface IVoluntaryManifestContributionCoordinator
{
    /// <summary>
    /// Discovers user licenses and matches them against ManifestHub demand (indices/demand.json),
    /// returning candidate manifests that the user is legitimately authorized to contribute.
    /// </summary>
    Task<IReadOnlyList<DemandRecord>> DiscoverContributableDemandsAsync(CancellationToken ct = default);

    /// <summary>
    /// Harvests the requested manifest using available providers, verifies protobuf magic header (0x71F617D0),
    /// packages a privacy-safe submission, transmits it to ManifestHub, and credits the user's Stardust balance.
    /// </summary>
    Task<ContributionHarvestReceipt> HarvestAndContributeAsync(
        DemandRecord demand,
        bool userConsentGranted,
        CancellationToken ct = default);
}
