using System.Collections.Generic;
using BlueStar.Core.Models;

namespace BlueStar.Core.Interfaces;

/// <summary>
/// Compares the user's local game library or discovered manifests against the Manifest Hub coverage index.
/// STRICT PRIVACY REQUIREMENT: All comparisons are performed 100% locally in-memory.
/// NEVER transmits the user's library, license list, or installed games to any external service.
/// </summary>
public interface ILocalCoverageComparator
{
    /// <summary>
    /// Compares locally known game App IDs against the Hub coverage index in-memory.
    /// </summary>
    /// <param name="localAppIds">App IDs present in the user's local library or installation history.</param>
    /// <param name="hubCoveredAppIds">App IDs indexed in the community Hub's public coverage catalog.</param>
    /// <returns>A breakdown of covered and missing apps computed locally without transmitting user data.</returns>
    CoverageComparisonResult CompareAppCoverage(
        IEnumerable<uint> localAppIds,
        IEnumerable<uint> hubCoveredAppIds);

    /// <summary>
    /// Compares locally discovered manifest artifacts against the Hub manifest catalog in-memory
    /// to identify rare or missing manifests eligible for voluntary community contribution.
    /// </summary>
    /// <param name="localManifests">Manifest artifacts found in the user's local cache or depot folders.</param>
    /// <param name="hubManifests">Manifest artifacts already present in the community Hub catalog.</param>
    /// <returns>A comparison result identifying coverage gaps computed entirely in-memory.</returns>
    CoverageComparisonResult CompareManifestCoverage(
        IEnumerable<ManifestArtifact> localManifests,
        IEnumerable<ManifestArtifact> hubManifests);
}
