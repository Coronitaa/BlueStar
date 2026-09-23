using System;
using System.Collections.Generic;

namespace BlueStar.Core.Models;

/// <summary>
/// Represents an entry in the local offline-first Steam catalog database.
/// Contains indexed metadata, platform compatibility, and cached review stats.
/// </summary>
public sealed record CatalogAppItem
{
    public uint AppId { get; init; }
    public string Name { get; init; } = string.Empty;
    public string NormalizedName { get; init; } = string.Empty;
    public string CompactName { get; init; } = string.Empty;
    public string AppType { get; init; } = "game";
    public long LastModified { get; init; }
    public uint PriceChangeNumber { get; init; }
    public int? ReviewPercent { get; init; }
    public int? ReviewCount { get; init; }
    public int? PositiveReviews { get; init; }
    public int? NegativeReviews { get; init; }
    public DateTimeOffset? RatingUpdatedAt { get; init; }
    public string? HeaderImageUrl { get; init; }
    public string? PriceText { get; init; }
    public int DiscountPercent { get; init; }
    public string? ReleaseDateText { get; init; }
    public bool HasWindows { get; init; } = true;
    public bool HasMac { get; init; }
    public bool HasLinux { get; init; }
    public bool IsNsfw { get; init; }
    public bool HasDrm { get; init; }
    public bool HasExternalLauncher { get; init; }
    public IReadOnlyList<int> TagIds { get; init; } = [];

    /// <summary>
    /// Computes the Wilson score lower bound of positive review proportion at 95% confidence interval.
    /// Provides a robust ranking metric that prevents games with 1 review (100%) from beating games with 50,000 reviews (98%).
    /// </summary>
    public double WilsonRatingScore
    {
        get
        {
            var pos = PositiveReviews ?? 0;
            var neg = NegativeReviews ?? 0;
            var total = pos + neg;
            if (total == 0)
            {
                // Fall back to review percent if explicit counts are missing
                return ReviewPercent.HasValue ? ReviewPercent.Value / 100.0 * 0.5 : 0.0;
            }

            // 95% confidence interval -> z = 1.96
            const double z = 1.96;
            var phat = (double)pos / total;
            var z2 = z * z;
            var denominator = 1.0 + z2 / total;
            var numerator = phat + z2 / (2.0 * total) - z * Math.Sqrt((phat * (1.0 - phat) + z2 / (4.0 * total)) / total);
            return numerator / denominator;
        }
    }
}

/// <summary>
/// Metadata manifest describing a versioned catalog snapshot for client synchronization.
/// </summary>
public sealed record CatalogManifest
{
    public int Version { get; init; }
    public DateTimeOffset GeneratedAt { get; init; }
    public int TotalApps { get; init; }
    public string Sha256 { get; init; } = string.Empty;
    public string DownloadUrl { get; init; } = string.Empty;
    public long CompressedSizeBytes { get; init; }
    public string FormatVersion { get; init; } = "1.0";
}
