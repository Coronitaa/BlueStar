using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace BlueStar.Core.Interfaces;

/// <summary>
/// Information about an available BepInEx release on GitHub.
/// </summary>
public record BepInExRelease(
    string TagName,
    string Version,
    string DownloadUrl,
    string Architecture,
    long SizeBytes,
    DateTimeOffset PublishedAt
);

/// <summary>
/// Service for discovering, downloading, installing, and uninstalling BepInEx mod loaders for Unity games.
/// </summary>
public interface IBepInExService
{
    /// <summary>
    /// Fetches all available stable and pre-release BepInEx versions from GitHub.
    /// </summary>
    Task<IReadOnlyList<BepInExRelease>> GetAvailableReleasesAsync(bool includePreReleases = false, CancellationToken ct = default);

    /// <summary>
    /// Checks if BepInEx is currently installed in the specified game directory, returning the installed version string or null.
    /// </summary>
    Task<string?> GetInstalledVersionAsync(string gameInstallPath, CancellationToken ct = default);

    /// <summary>
    /// Checks if BepInEx is installed in the specified game directory.
    /// </summary>
    bool IsInstalled(string gameInstallPath);

    /// <summary>
    /// Downloads and installs a specific BepInEx release into the game directory.
    /// </summary>
    Task<bool> InstallAsync(string gameInstallPath, BepInExRelease release, IProgress<double>? progress = null, CancellationToken ct = default);

    /// <summary>
    /// Uninstalls BepInEx and its loader libraries from the game directory.
    /// </summary>
    Task<bool> UninstallAsync(string gameInstallPath, bool keepPluginsFolder = true, CancellationToken ct = default);
}
