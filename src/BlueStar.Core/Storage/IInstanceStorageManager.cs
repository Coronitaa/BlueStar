using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace BlueStar.Core.Storage;

/// <summary>
/// Configuration options for zero-copy instance deployment.
/// </summary>
public record InstanceDeployOptions
{
    /// <summary>
    /// Whether to create directory junctions for heavy static asset subdirectories (e.g., Content/Paks, *_Data).
    /// </summary>
    public bool UseJunctionsForAssetFolders { get; init; } = true;

    /// <summary>
    /// List of relative folder names that must always be created as independent physical local folders
    /// rather than hardlinks or junctions (e.g., saves, logs, config, steam_settings).
    /// </summary>
    public IReadOnlyList<string> IsolatedFolderNames { get; init; } =
    [
        "steam_settings",
        "saves",
        "save",
        "logs",
        "log",
        "config",
        "configs",
        "BepInEx/config",
        "BepInEx/plugins",
        "mods",
        "Mods",
        "steamapps/workshop"
    ];

    /// <summary>
    /// Whether to automatically fall back to standard file copying if the file system is non-NTFS or cross-volume.
    /// </summary>
    public bool AllowNonNtfsFallback { get; init; } = true;
}

/// <summary>
/// Result of an instance deployment operation.
/// </summary>
public record InstanceDeployResult
{
    public required bool Success { get; init; }
    public long TotalFilesLinked { get; init; }
    public long TotalFilesCopied { get; init; }
    public long TotalJunctionsCreated { get; init; }
    public bool IsZeroCopy { get; init; }
    public string? FallbackReason { get; init; }
    public string? ErrorMessage { get; init; }
    public TimeSpan Duration { get; init; }
}

/// <summary>
/// Disk allocation statistics for a game instance.
/// </summary>
public record InstanceStorageStats
{
    public long SharedHardlinkedBytes { get; init; }
    public long UniqueAllocatedBytes { get; init; }
    public long TotalApparentBytes { get; init; }
    public int HardlinkedFileCount { get; init; }
    public int UniqueFileCount { get; init; }
    public int JunctionCount { get; init; }
}

/// <summary>
/// Manager for zero-copy game instance creation, Copy-on-Write (CoW) link breaking,
/// and safe instance unlinking.
/// </summary>
public interface IInstanceStorageManager
{
    /// <summary>
    /// Deploys a zero-copy instance of a game from an immutable base depot to a target instance path
    /// using NTFS hardlinks, directory junctions, and isolated mutable directories.
    /// </summary>
    Task<InstanceDeployResult> CreateInstanceAsync(
        string depotPath,
        string instancePath,
        InstanceDeployOptions? options = null,
        CancellationToken ct = default);

    /// <summary>
    /// Implements Copy-on-Write (CoW) protection on a single file within an instance.
    /// If the file is hardlinked (link count > 1), it breaks the link and replaces it
    /// with an independent physical copy before modification.
    /// </summary>
    Task<bool> BreakLinkAndCopyAsync(string filePath, CancellationToken ct = default);

    /// <summary>
    /// Ensures that a target file is safe for mutation by verifying link count and breaking the hardlink if linked.
    /// </summary>
    Task<bool> EnsureCoWFileAsync(string filePath, string? sourceDepotFilePath = null, CancellationToken ct = default);

    /// <summary>
    /// Safely deletes an instance directory, removing junctions and unlinking hardlinks
    /// without deleting or modifying files in the base depot.
    /// </summary>
    Task<bool> DeleteInstanceAsync(string instancePath, CancellationToken ct = default);

    /// <summary>
    /// Calculates disk allocation statistics for an instance, distinguishing shared hardlink size vs unique local space.
    /// </summary>
    Task<InstanceStorageStats> GetStorageStatsAsync(string instancePath, CancellationToken ct = default);
}
