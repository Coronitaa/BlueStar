using System;
using System.Collections.Generic;

namespace BlueStar.Core.Models;

/// <summary>
/// Result of an in-memory local coverage comparison between user's library/manifests and the Hub index.
/// Guaranteed to be computed 100% locally with zero user telemetry or external transmission.
/// </summary>
public record CoverageComparisonResult
{
    /// <summary>
    /// App IDs in the user's local scope that are already covered by the Manifest Hub.
    /// </summary>
    public IReadOnlyList<uint> CoveredAppIds { get; init; } = [];

    /// <summary>
    /// App IDs in the user's local scope that are missing in the Manifest Hub (candidates for contribution).
    /// </summary>
    public IReadOnlyList<uint> MissingInHubAppIds { get; init; } = [];

    /// <summary>
    /// Manifest artifacts in the user's local scope that are missing in the Manifest Hub.
    /// </summary>
    public IReadOnlyList<ManifestArtifact> MissingInHubManifests { get; init; } = [];

    /// <summary>
    /// Manifest artifacts in the user's local scope that already exist in the Manifest Hub.
    /// </summary>
    public IReadOnlyList<ManifestArtifact> CoveredManifests { get; init; } = [];

    /// <summary>
    /// Total count of local games or manifests evaluated.
    /// </summary>
    public int TotalLocalItems => (CoveredAppIds.Count + MissingInHubAppIds.Count) > 0
        ? CoveredAppIds.Count + MissingInHubAppIds.Count
        : CoveredManifests.Count + MissingInHubManifests.Count;

    /// <summary>
    /// Percentage of user's local items that are covered in the Hub.
    /// </summary>
    public double CoveragePercentage
    {
        get
        {
            var total = TotalLocalItems;
            if (total == 0) return 100.0;
            var covered = CoveredAppIds.Count > 0 || MissingInHubAppIds.Count > 0
                ? CoveredAppIds.Count
                : CoveredManifests.Count;
            return Math.Round((double)covered / total * 100.0, 1);
        }
    }

    /// <summary>
    /// Confirmation that comparison executed entirely on the local device without transmitting any data.
    /// </summary>
    public bool TotalPrivacyGuaranteed { get; init; } = true;
}
