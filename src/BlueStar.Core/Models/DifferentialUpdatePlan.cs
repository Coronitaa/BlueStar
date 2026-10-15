using System.Collections.Generic;

namespace BlueStar.Core.Models;

/// <summary>
/// Represents the resolved plan for a differential update between installed/current depots and a target version,
/// optimizing bandwidth by skipping unchanged depots with identical ManifestIds.
/// </summary>
public record DifferentialUpdatePlan
{
    /// <summary>
    /// Gets the base game Steam App ID.
    /// </summary>
    public uint AppId { get; init; }

    /// <summary>
    /// Gets the target build or version identifier.
    /// </summary>
    public string TargetBuildId { get; init; } = string.Empty;

    /// <summary>
    /// Gets the depots that exist in both current and target versions with matching ManifestIds.
    /// These depots require no download, preserving user bandwidth and disk writes.
    /// </summary>
    public IReadOnlyList<DepotInfo> UnchangedDepots { get; init; } = [];

    /// <summary>
    /// Gets the depots that exist in both versions but have different ManifestIds, requiring updated manifests/chunks.
    /// </summary>
    public IReadOnlyList<DepotInfo> UpdatedDepots { get; init; } = [];

    /// <summary>
    /// Gets the depots present in the target version that were not previously installed.
    /// </summary>
    public IReadOnlyList<DepotInfo> NewDepots { get; init; } = [];

    /// <summary>
    /// Gets the depots that were installed in the current version but are no longer in the target version.
    /// </summary>
    public IReadOnlyList<DepotInfo> RemovedDepots { get; init; } = [];

    /// <summary>
    /// Gets the total bytes that must be downloaded (sum of UpdatedDepots and NewDepots sizes).
    /// </summary>
    public long TotalDownloadSizeBytes { get; init; }

    /// <summary>
    /// Gets the total bytes saved by skipping unchanged depots (sum of UnchangedDepots sizes).
    /// </summary>
    public long TotalSavedSizeBytes { get; init; }

    /// <summary>
    /// Gets a value indicating whether any depots are preserved without redundant download.
    /// </summary>
    public bool HasSavings => TotalSavedSizeBytes > 0 || UnchangedDepots.Count > 0;

    /// <summary>
    /// Gets the percentage of payload size saved compared to a full clean install.
    /// </summary>
    public double SavingsPercentage
    {
        get
        {
            var total = TotalDownloadSizeBytes + TotalSavedSizeBytes;
            return total > 0 ? System.Math.Round((double)TotalSavedSizeBytes / total * 100.0, 1) : 0.0;
        }
    }

    /// <summary>
    /// Formatted download size for display.
    /// </summary>
    public string FormattedDownloadSize => TotalDownloadSizeBytes switch
    {
        <= 0 => "0 B",
        < 1024 * 1024 => $"{TotalDownloadSizeBytes / 1024.0:F1} KB",
        < 1024 * 1024 * 1024 => $"{TotalDownloadSizeBytes / (1024.0 * 1024.0):F1} MB",
        _ => $"{TotalDownloadSizeBytes / (1024.0 * 1024.0 * 1024.0):F2} GB"
    };

    /// <summary>
    /// Formatted saved size for display.
    /// </summary>
    public string FormattedSavedSize => TotalSavedSizeBytes switch
    {
        <= 0 => "0 B",
        < 1024 * 1024 => $"{TotalSavedSizeBytes / 1024.0:F1} KB",
        < 1024 * 1024 * 1024 => $"{TotalSavedSizeBytes / (1024.0 * 1024.0):F1} MB",
        _ => $"{TotalSavedSizeBytes / (1024.0 * 1024.0 * 1024.0):F2} GB"
    };
}
