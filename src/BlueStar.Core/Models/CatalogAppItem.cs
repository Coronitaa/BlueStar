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

    private readonly string? _priceText;
    public string? PriceText
    {
        get => !string.IsNullOrWhiteSpace(_priceText)
            ? _priceText
            : (PriceCents.HasValue && PriceCents.Value == 0 ? "Free" : null);
        init => _priceText = value;
    }

    public int? PriceCents { get; init; }
    public int DiscountPercent { get; init; }

    private readonly string? _releaseDateText;
    public string? ReleaseDateText
    {
        get => !string.IsNullOrWhiteSpace(_releaseDateText)
            ? _releaseDateText
            : (ReleaseDateUtc.HasValue && ReleaseDateUtc.Value > 0
                ? DateTimeOffset.FromUnixTimeSeconds(ReleaseDateUtc.Value).ToString("MMM d, yyyy", System.Globalization.CultureInfo.InvariantCulture)
                : null);
        init => _releaseDateText = value;
    }

    public long? ReleaseDateUtc { get; init; }
    public bool HasWindows { get; init; } = true;
    public bool HasMac { get; init; }
    public bool HasLinux { get; init; }
    public bool IsNsfw { get; init; }
    public bool HasDrm { get; init; }
    public string? DrmName { get; init; }
    public bool HasAntiCheat { get; init; }
    public string? AntiCheatName { get; init; }
    public bool HasExternalLauncher { get; init; }
    public string? LauncherName { get; init; }
    public bool HasAccount { get; init; }
    public string? AccountName { get; init; }
    public bool HasEula { get; init; }
    public string? EulaName { get; init; }
    public int? DlcCount { get; init; }
    public bool IsEnriched { get; init; }
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
    [System.Text.Json.Serialization.JsonPropertyName("version")]
    public int Version { get; init; }

    [System.Text.Json.Serialization.JsonPropertyName("created_at")]
    public DateTimeOffset GeneratedAt { get; init; }

    [System.Text.Json.Serialization.JsonPropertyName("app_count")]
    public int TotalApps { get; init; }

    [System.Text.Json.Serialization.JsonPropertyName("sha256")]
    public string Sha256 { get; init; } = string.Empty;

    [System.Text.Json.Serialization.JsonPropertyName("download_url")]
    public string DownloadUrl { get; init; } = string.Empty;

    [System.Text.Json.Serialization.JsonPropertyName("compressed_size_bytes")]
    public long CompressedSizeBytes { get; init; }

    [System.Text.Json.Serialization.JsonPropertyName("schema_version")]
    public string FormatVersion { get; init; } = "1.0";
}

/// <summary>
/// Container for batch metadata enrichment (tags, dates, prices, ratings, platforms).
/// </summary>
public sealed record AppMetadataEnrichment(
    uint AppId,
    string? TagIds,
    long? ReleaseDateUtc,
    int? PriceCents,
    int? ReviewPercent,
    int? ReviewCount,
    bool HasWindows,
    bool HasMac,
    bool HasLinux,
    bool IsNsfw,
    string? DrmName = null,
    string? LauncherName = null,
    int? DlcCount = null,
    string? ReleaseDateText = null,
    string? PriceText = null,
    string? AntiCheatName = null,
    string? AccountName = null,
    string? EulaName = null,
    bool IsEnriched = false
);

