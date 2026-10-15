using System;
using System.Collections.Generic;
using System.Linq;
using BlueStar.Core.Interfaces;
using BlueStar.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BlueStar.Infrastructure.Services;

/// <summary>
/// Implements <see cref="ILocalCoverageComparator"/>, performing 100% local in-memory
/// coverage analysis between the user's private library/manifests and the public Hub catalog index.
/// 
/// ZERO-KNOWLEDGE PRIVACY GUARANTEE:
/// This service never transmits, logs, or exports the user's game library, license list,
/// or discovered local manifests to any remote host or external telemetry collector.
/// </summary>
public sealed class LocalCoverageComparator : ILocalCoverageComparator
{
    private readonly ILogger<LocalCoverageComparator> _logger;

    public LocalCoverageComparator(ILogger<LocalCoverageComparator>? logger = null)
    {
        _logger = logger ?? NullLogger<LocalCoverageComparator>.Instance;
    }

    /// <inheritdoc />
    public CoverageComparisonResult CompareAppCoverage(
        IEnumerable<uint> localAppIds,
        IEnumerable<uint> hubCoveredAppIds)
    {
        ArgumentNullException.ThrowIfNull(localAppIds);
        ArgumentNullException.ThrowIfNull(hubCoveredAppIds);

        // Entire comparison executed strictly in volatile local process memory
        var localSet = new HashSet<uint>(localAppIds);
        var hubSet = new HashSet<uint>(hubCoveredAppIds);

        var covered = new List<uint>();
        var missingInHub = new List<uint>();

        foreach (var appId in localSet)
        {
            if (hubSet.Contains(appId))
            {
                covered.Add(appId);
            }
            else
            {
                missingInHub.Add(appId);
            }
        }

        covered.Sort();
        missingInHub.Sort();

        _logger.LogDebug(
            "Local in-memory coverage computed: {CoveredCount} covered, {MissingCount} missing in Hub. ZERO user data transmitted.",
            covered.Count, missingInHub.Count);

        return new CoverageComparisonResult
        {
            CoveredAppIds = covered.AsReadOnly(),
            MissingInHubAppIds = missingInHub.AsReadOnly(),
            TotalPrivacyGuaranteed = true
        };
    }

    /// <inheritdoc />
    public CoverageComparisonResult CompareManifestCoverage(
        IEnumerable<ManifestArtifact> localManifests,
        IEnumerable<ManifestArtifact> hubManifests)
    {
        ArgumentNullException.ThrowIfNull(localManifests);
        ArgumentNullException.ThrowIfNull(hubManifests);

        // Map hub manifests by composite key (DepotId, ManifestId)
        var hubKeySet = new HashSet<(uint DepotId, ulong ManifestId)>(
            hubManifests.Select(m => (m.DepotId, m.ManifestId)));

        var coveredList = new List<ManifestArtifact>();
        var missingInHubList = new List<ManifestArtifact>();

        foreach (var local in localManifests)
        {
            if (hubKeySet.Contains((local.DepotId, local.ManifestId)))
            {
                coveredList.Add(local);
            }
            else
            {
                missingInHubList.Add(local);
            }
        }

        _logger.LogDebug(
            "Local in-memory manifest coverage computed: {CoveredCount} covered, {MissingCount} candidate contributions. Zero data transmitted.",
            coveredList.Count, missingInHubList.Count);

        return new CoverageComparisonResult
        {
            CoveredManifests = coveredList.AsReadOnly(),
            MissingInHubManifests = missingInHubList.AsReadOnly(),
            TotalPrivacyGuaranteed = true
        };
    }
}
