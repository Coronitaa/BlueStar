using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BlueStar.Core.Interfaces;
using BlueStar.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BlueStar.Infrastructure.Services;

/// <summary>
/// Implements <see cref="IVoluntaryManifestContributionCoordinator"/>, managing the full lifecycle of
/// voluntary manifest contribution:
/// 1. Local & web Steam license discovery (including appcache/packageinfo.vdf / appinfo.vdf).
/// 2. Demand matching against indices/demand.json.
/// 3. Multi-mechanism harvesting (Steam client console, DepotDownloaderMod, script adapter).
/// 4. Protobuf magic header verification (0x71F617D0).
/// 5. Privacy sanitization & remote publication via ContributionTransportService.
/// 6. Stardust balance crediting and notifications via IStardustService.
/// </summary>
public sealed class VoluntaryManifestContributionCoordinator : IVoluntaryManifestContributionCoordinator
{
    private readonly ISteamLicenseDiscoveryService _licenseService;
    private readonly IManifestHubProvider _hubProvider;
    private readonly IVoluntaryContributionService _contributionService;
    private readonly IManifestHarvestCoordinator _harvester;
    private readonly IContributionTransportService _transportService;
    private readonly IStardustService _stardustService;
    private readonly ILogger<VoluntaryManifestContributionCoordinator> _logger;

    public VoluntaryManifestContributionCoordinator(
        ISteamLicenseDiscoveryService licenseService,
        IManifestHubProvider hubProvider,
        IVoluntaryContributionService contributionService,
        IManifestHarvestCoordinator harvester,
        IContributionTransportService transportService,
        IStardustService stardustService,
        ILogger<VoluntaryManifestContributionCoordinator>? logger = null)
    {
        _licenseService = licenseService ?? throw new ArgumentNullException(nameof(licenseService));
        _hubProvider = hubProvider ?? throw new ArgumentNullException(nameof(hubProvider));
        _contributionService = contributionService ?? throw new ArgumentNullException(nameof(contributionService));
        _harvester = harvester ?? throw new ArgumentNullException(nameof(harvester));
        _transportService = transportService ?? throw new ArgumentNullException(nameof(transportService));
        _stardustService = stardustService ?? throw new ArgumentNullException(nameof(stardustService));
        _logger = logger ?? NullLogger<VoluntaryManifestContributionCoordinator>.Instance;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<DemandRecord>> DiscoverContributableDemandsAsync(CancellationToken ct = default)
    {
        // 1. Fetch community demand index from ManifestHub
        var demandIndex = await _hubProvider.GetDemandAsync(ct).ConfigureAwait(false);
        if (demandIndex == null || demandIndex.Records.Count == 0)
        {
            _logger.LogInformation("No active manifest demand records reported by ManifestHub.");
            return Array.Empty<DemandRecord>();
        }

        // 2. Discover user's legitimate licenses (inspecting local cache, appcache, and web session)
        var options = new SteamLicenseDiscoveryOptions
        {
            IncludeLocalClientCache = true,
            IncludeWebSession = true,
            EnrichMetadata = false
        };

        var licenseResult = await _licenseService.DiscoverLicensesAsync(options, ct).ConfigureAwait(false);
        if (!licenseResult.IsSuccess || licenseResult.LicensedApps.Count == 0)
        {
            _logger.LogInformation("No user licenses discovered from local Steam client or web session.");
            return Array.Empty<DemandRecord>();
        }

        var ownedAppIds = licenseResult.LicensedApps.Select(a => a.AppId).Distinct();

        // 3. Cross owned app licenses with demand index
        var matched = _contributionService.MatchDemandWithLicenses(ownedAppIds, demandIndex);
        _logger.LogInformation(
            "Discovered {Count} contributable manifest(s) matching user licenses out of {DemandCount} demanded records",
            matched.Count, demandIndex.Records.Count);

        return matched;
    }

    /// <inheritdoc />
    public async Task<ContributionHarvestReceipt> HarvestAndContributeAsync(
        DemandRecord demand,
        bool userConsentGranted,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(demand);

        // 1. Enforce mandatory user consent
        if (!userConsentGranted)
        {
            return new ContributionHarvestReceipt
            {
                IsSuccess = false,
                ErrorMessage = "Explicit user consent is mandatory for community manifest contributions."
            };
        }

        // 2. Harvest manifest file via coordinated harvester providers
        _logger.LogInformation("Harvesting manifest for App {AppId}, Depot {DepotId}, Manifest {ManifestId}",
            demand.AppId, demand.DepotId, demand.ManifestId);

        var harvestResult = await _harvester.HarvestManifestAsync(demand.AppId, demand.DepotId, demand.ManifestId, ct).ConfigureAwait(false);
        if (!harvestResult.IsSuccess || string.IsNullOrWhiteSpace(harvestResult.ManifestFilePath))
        {
            _logger.LogWarning("Harvesting failed for Depot {DepotId}: {Error}", demand.DepotId, harvestResult.ErrorMessage);
            return new ContributionHarvestReceipt
            {
                IsSuccess = false,
                ErrorMessage = harvestResult.ErrorMessage ?? "Manifest harvesting failed across all providers.",
                HarvestResult = harvestResult
            };
        }

        // 3. Validate binary manifest protobuf magic header (0x71F617D0)
        if (!SteamClientManifestHarvesterProvider.ValidateManifestHeader(harvestResult.ManifestFilePath))
        {
            _logger.LogWarning("Harvested manifest file {Path} failed protobuf magic header validation (0x71F617D0)", harvestResult.ManifestFilePath);
            return new ContributionHarvestReceipt
            {
                IsSuccess = false,
                ErrorMessage = "Captured manifest failed Steam Protobuf magic header verification (0x71F617D0).",
                HarvestResult = harvestResult
            };
        }

        // 4. Create preview and package sanitized submission (Zero-Knowledge privacy)
        var preview = await _contributionService.CreatePreviewAsync(
            demand.AppId,
            demand.DepotId,
            demand.ManifestId,
            harvestResult.ManifestFilePath,
            ct: ct).ConfigureAwait(false);

        var submission = await _contributionService.PrepareSubmissionAsync(
            preview,
            userConsentGranted: true,
            ct: ct).ConfigureAwait(false);

        // 5. Transmit contribution to ManifestHub
        PublicationReceipt receipt;
        try
        {
            receipt = await _transportService.TransmitContributionAsync(submission, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to transmit contribution for Depot {DepotId}", demand.DepotId);
            return new ContributionHarvestReceipt
            {
                IsSuccess = false,
                ErrorMessage = $"Contribution transmission failed: {ex.Message}",
                HarvestResult = harvestResult
            };
        }

        // 6. Credit Stardust rewards upon confirmed publication
        if (receipt.StardustCredited > 0)
        {
            _stardustService.NotifyRewardConfirmed(receipt.StardustCredited);
            try
            {
                await _stardustService.RefreshBalanceAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not immediately refresh Stardust balance after crediting");
            }

            _logger.LogInformation("Successfully published manifest for Depot {DepotId} and credited {Points} Stardust",
                demand.DepotId, receipt.StardustCredited);
        }

        return new ContributionHarvestReceipt
        {
            IsSuccess = receipt.IsSuccess,
            ErrorMessage = receipt.IsSuccess ? null : $"Publication ended with status {receipt.Status}",
            PublicationReceipt = receipt,
            HarvestResult = harvestResult
        };
    }
}
