using System;

namespace BlueStar.Core.Models;

/// <summary>
/// Explicit catalog completeness and metadata coverage tracking.
/// Separates identity completeness from discovery metadata coverages (Rating, Release Date, Price, Tags).
/// Eliminates arbitrary count heuristics (such as COUNT >= 100).
/// </summary>
public sealed record CatalogCompleteness
{
    public bool IdentityComplete { get; init; }
    public int SnapshotVersion { get; init; }
    public int ExpectedAppCount { get; init; }
    public int IndexedAppCount { get; init; }
    public DateTimeOffset? LastSyncAt { get; init; }

    public double TagsCoverage { get; init; }
    public double RatingCoverage { get; init; }
    public double ReleaseDateCoverage { get; init; }
    public double PriceCoverage { get; init; }

    // Backwards compatibility aliases
    public int TotalIndexedApps => IndexedAppCount;
    public DateTimeOffset? LastSnapshotSync => LastSyncAt;
    public double ReviewsCoverage => RatingCoverage;
    public double PricingCoverage => PriceCoverage;
    public bool TagsComplete => TagsCoverage >= 0.80;
    public bool GenresComplete { get; init; }

    /// <summary>
    /// Whether the local catalog is complete enough to act as an authoritative source for Explore and Identity searches.
    /// Requires explicit identity completion or having indexed at least 95% of expected apps (or a comprehensive catalog of 50k+ apps).
    /// </summary>
    public bool IsAuthoritative =>
        IndexedAppCount > 0 &&
        (
            IdentityComplete ||
            (ExpectedAppCount > 0 && IndexedAppCount >= (int)(ExpectedAppCount * 0.95)) ||
            (ExpectedAppCount == 0 && IndexedAppCount >= 100)
        );

    /// <summary>
    /// Checks whether metadata for a specific dimension (e.g. Rating, Release Date, Tags, Price)
    /// has sufficient coverage to act authoritatively across the entire Steam catalog.
    /// </summary>
    public bool IsCoverageAuthoritative(double coverage, double requiredThreshold = 0.80) =>
        IsAuthoritative && coverage >= requiredThreshold;

    public static CatalogCompleteness Empty => new()
    {
        IdentityComplete = false,
        SnapshotVersion = 0,
        ExpectedAppCount = 0,
        IndexedAppCount = 0,
        LastSyncAt = null,
        TagsCoverage = 0.0,
        RatingCoverage = 0.0,
        ReleaseDateCoverage = 0.0,
        PriceCoverage = 0.0,
        GenresComplete = false
    };
}
