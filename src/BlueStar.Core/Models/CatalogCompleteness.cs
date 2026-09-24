using System;

namespace BlueStar.Core.Models;

/// <summary>
/// Tracks the completeness and metadata coverage of the local catalog index across multiple dimensions.
/// Prevents treating partial or ad-hoc discovered entries as an authoritative full catalog.
/// </summary>
public sealed record CatalogCompleteness
{
    public bool IdentityComplete { get; init; }
    public bool TagsComplete { get; init; }
    public bool GenresComplete { get; init; }
    public double ReleaseDateCoverage { get; init; }
    public double ReviewsCoverage { get; init; }
    public double PricingCoverage { get; init; }
    public int TotalIndexedApps { get; init; }
    public DateTimeOffset? LastSnapshotSync { get; init; }

    /// <summary>
    /// Whether the local catalog is complete enough to act as the authoritative source
    /// for identity and explore searches without falling back to Steam Store Search.
    /// </summary>
    public bool IsAuthoritative => IdentityComplete && TotalIndexedApps >= 100;

    public static CatalogCompleteness Empty => new()
    {
        IdentityComplete = false,
        TagsComplete = false,
        GenresComplete = false,
        ReleaseDateCoverage = 0.0,
        ReviewsCoverage = 0.0,
        PricingCoverage = 0.0,
        TotalIndexedApps = 0
    };
}
