using System;
using System.Collections.Generic;
using System.Linq;

namespace BlueStar.Core.Models;

/// <summary>
/// Represents an individual depot item scheduled for download or reuse in an installation plan.
/// </summary>
public record PlanDepotItem
{
    /// <summary>
    /// Gets the Steam Depot ID.
    /// </summary>
    public uint DepotId { get; init; }

    /// <summary>
    /// Gets the target Manifest ID to be installed.
    /// </summary>
    public ulong ManifestId { get; init; }

    /// <summary>
    /// Gets the previous installed Manifest ID, if this is an update.
    /// </summary>
    public ulong? PreviousManifestId { get; init; }

    /// <summary>
    /// Gets the depot friendly name.
    /// </summary>
    public string? Name { get; init; }

    /// <summary>
    /// Gets the size in bytes of the depot payload.
    /// </summary>
    public long SizeBytes { get; init; }

    /// <summary>
    /// Gets the 64-character hexadecimal decryption key.
    /// </summary>
    public string? DepotKey { get; init; }

    /// <summary>
    /// Gets the resolved absolute path to the .manifest file on local disk.
    /// </summary>
    public string? ManifestFilePath { get; init; }

    /// <summary>
    /// Gets the source route selected for obtaining this depot's manifest.
    /// </summary>
    public ManifestSourceRoute? SelectedRoute { get; init; }

    /// <summary>
    /// Gets a value indicating whether this depot is already present and matches the target manifest.
    /// </summary>
    public bool IsReused { get; init; }
}

/// <summary>
/// Represents a fully resolved, executable installation or update plan.
/// Encapsulates exactly what must be downloaded, what can be reused,
/// which manifests are required, and which post-install component layers to apply.
/// </summary>
public record InstallationPlan
{
    /// <summary>
    /// Gets the ID of the target game instance.
    /// </summary>
    public Guid InstanceId { get; init; }

    /// <summary>
    /// Gets the Steam AppID of the game.
    /// </summary>
    public uint AppId { get; init; }

    /// <summary>
    /// Gets the target build ID.
    /// </summary>
    public string TargetBuildId { get; init; } = string.Empty;

    /// <summary>
    /// Gets the target branch name.
    /// </summary>
    public string TargetBranch { get; init; } = "public";

    /// <summary>
    /// Gets the target installation directory.
    /// </summary>
    public string InstallPath { get; init; } = string.Empty;

    /// <summary>
    /// Gets the list of depots that must be downloaded.
    /// </summary>
    public IReadOnlyList<PlanDepotItem> DepotsToDownload { get; init; } = [];

    /// <summary>
    /// Gets the list of existing depots that are already up-to-date and will be reused without downloading.
    /// </summary>
    public IReadOnlyList<PlanDepotItem> ReusedDepots { get; init; } = [];

    /// <summary>
    /// Gets the list of manifest artifacts required on disk before starting the download engine.
    /// </summary>
    public IReadOnlyList<ManifestArtifact> RequiredManifests { get; init; } = [];

    /// <summary>
    /// Gets optional component or fix layers to apply after download (e.g. "ReFix", "OnlineFix", "SmokeAPI").
    /// </summary>
    public IReadOnlyList<string> FixLayersToApply { get; init; } = [];

    /// <summary>
    /// Gets a value indicating whether this is a differential update that reuses unchanged depots.
    /// </summary>
    public bool IsDifferential => ReusedDepots.Count > 0;

    /// <summary>
    /// Gets the total bytes that must be downloaded for this plan.
    /// </summary>
    public long TotalDownloadSizeBytes => DepotsToDownload.Sum(d => d.SizeBytes);

    /// <summary>
    /// Formatted download size string for UI presentation.
    /// </summary>
    public string FormattedDownloadSize => TotalDownloadSizeBytes > 0
        ? $"{TotalDownloadSizeBytes / (1024.0 * 1024.0 * 1024.0):F2} GB"
        : "0 B";
}
