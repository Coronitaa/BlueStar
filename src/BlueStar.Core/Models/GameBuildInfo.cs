using System;
using System.Collections.Generic;

namespace BlueStar.Core.Models;

/// <summary>
/// Represents a specific game build version, its target branch, and associated depot manifests.
/// </summary>
public record GameBuildInfo
{
    /// <summary>
    /// Gets or sets the unique build identifier (e.g. "15283921").
    /// </summary>
    public string BuildId { get; init; } = string.Empty;

    /// <summary>
    /// Gets or sets the branch name (e.g. "public", "previous", "beta", "legacy").
    /// </summary>
    public string BranchName { get; init; } = "public";

    /// <summary>
    /// Gets or sets the friendly display name for the build in the UI.
    /// </summary>
    public string DisplayName { get; init; } = string.Empty;

    /// <summary>
    /// Gets or sets the date and time when this build was released or updated.
    /// </summary>
    public DateTimeOffset? UpdatedAt { get; init; }

    /// <summary>
    /// Gets or sets optional description or changelog notes for this build.
    /// </summary>
    public string? Description { get; init; }

    /// <summary>
    /// Gets or sets a value indicating whether this is the build currently configured on the instance.
    /// </summary>
    public bool IsCurrentBuild { get; init; }

    /// <summary>
    /// Gets or sets the source of the build metadata ("Steam", "DepotBox", "Local Archive").
    /// </summary>
    public string Source { get; init; } = "Steam";

    /// <summary>
    /// Gets or sets the dictionary mapping each DepotId to its corresponding ManifestId for this build.
    /// </summary>
    public IReadOnlyDictionary<uint, ulong> DepotManifests { get; init; } = new Dictionary<uint, ulong>();

    /// <summary>
    /// Gets or sets the total size in bytes for the depots in this build if known.
    /// </summary>
    public long TotalSizeBytes { get; init; }

    /// <summary>
    /// Formatted size string for display.
    /// </summary>
    public string FormattedSize => TotalSizeBytes > 0
        ? $"{TotalSizeBytes / (1024.0 * 1024.0 * 1024.0):F2} GB"
        : string.Empty;

    /// <summary>
    /// Formatted date string for display.
    /// </summary>
    public string FormattedDate => UpdatedAt.HasValue
        ? UpdatedAt.Value.ToString("d MMM yyyy")
        : "Unknown Date";
}
