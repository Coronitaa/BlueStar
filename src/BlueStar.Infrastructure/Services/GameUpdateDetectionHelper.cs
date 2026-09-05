using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BlueStar.Core.Models;
using BlueStar.Infrastructure.Downloader;
using BlueStar.Infrastructure.Metadata;

namespace BlueStar.Infrastructure.Services;

/// <summary>
/// Provides unified game update detection for instances by comparing local installed manifests/dates
/// against official Steam branches and DepotBox metadata.
/// </summary>
public static class GameUpdateDetectionHelper
{
    /// <summary>
    /// Scans the instance install directory and application cache folders for local .manifest files and extracts the newest creation date.
    /// </summary>
    public static DateTimeOffset? GetInstalledManifestDate(GameInstance instance)
    {
        if (instance == null) return null;
        if (instance.InstalledVersionDate.HasValue) return instance.InstalledVersionDate.Value;
        if (instance.Depots.Count == 0) return null;

        var searchDirs = new List<string>();
        if (!string.IsNullOrWhiteSpace(instance.InstallPath) && Directory.Exists(instance.InstallPath))
        {
            searchDirs.Add(instance.InstallPath);
            searchDirs.Add(Path.Combine(instance.InstallPath, ".depots"));
            searchDirs.Add(Path.Combine(instance.InstallPath, "depots"));
            searchDirs.Add(Path.Combine(instance.InstallPath, ".DepotDownloader"));
        }

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (!string.IsNullOrWhiteSpace(appData))
        {
            searchDirs.Add(Path.Combine(appData, "BlueStar", "instances", instance.Id.ToString(), "manifests"));
            searchDirs.Add(Path.Combine(appData, "BlueStar", "instances", instance.Id.ToString()));
            searchDirs.Add(Path.Combine(appData, "BlueStar", "cache", "manifests"));
            searchDirs.Add(Path.Combine(appData, "BlueStar", "manifests"));
        }

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(localAppData))
        {
            searchDirs.Add(Path.Combine(localAppData, "BlueStar", "DepotWork", instance.Id.ToString()));
        }

        DateTimeOffset? latestDepotDate = null;

        // Check instance depots in search directories
        foreach (var dir in searchDirs)
        {
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) continue;

            foreach (var depot in instance.Depots)
            {
                var manifestFiles = Directory.GetFiles(dir, $"*{depot.DepotId}*.manifest", SearchOption.TopDirectoryOnly)
                    .Concat(Directory.GetFiles(dir, $"*{depot.ManifestId}*.manifest", SearchOption.TopDirectoryOnly))
                    .Distinct();

                foreach (var mf in manifestFiles)
                {
                    var date = SteamManifestDateHelper.GetManifestCreationDate(mf);
                    if (date.HasValue && (latestDepotDate == null || date.Value > latestDepotDate.Value))
                    {
                        latestDepotDate = date.Value;
                    }
                }
            }

            // Also check depot subdirectories inside global cache (e.g. cache/manifests/{depotId}/*.manifest)
            if (dir.EndsWith("manifests", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var depot in instance.Depots)
                {
                    var subDepotDir = Path.Combine(dir, depot.DepotId.ToString());
                    if (Directory.Exists(subDepotDir))
                    {
                        var manifestFiles = Directory.GetFiles(subDepotDir, $"*{depot.DepotId}*.manifest", SearchOption.TopDirectoryOnly)
                            .Concat(Directory.GetFiles(subDepotDir, $"*{depot.ManifestId}*.manifest", SearchOption.TopDirectoryOnly))
                            .Distinct();

                        foreach (var mf in manifestFiles)
                        {
                            var date = SteamManifestDateHelper.GetManifestCreationDate(mf);
                            if (date.HasValue && (latestDepotDate == null || date.Value > latestDepotDate.Value))
                            {
                                latestDepotDate = date.Value;
                            }
                        }
                    }
                }
            }

            if (latestDepotDate.HasValue)
                return latestDepotDate;
        }

        return null;
    }

    /// <summary>
    /// Checks whether a newer build or updated depot manifests are available for the given game instance.
    /// </summary>
    public static async Task<(UpdateCheckStatus Status, string? Description)> CheckInstanceUpdateAsync(
        GameInstance instance,
        SteamStoreApiClient steamClient,
        CancellationToken ct = default)
    {
        var result = await CheckInstanceUpdateDetailsAsync(instance, steamClient, ct).ConfigureAwait(false);
        return (result.Status, result.Description);
    }

    /// <summary>
    /// Checks update status and returns detailed date and version information.
    /// </summary>
    public static async Task<(UpdateCheckStatus Status, string? Description, DateTimeOffset? LatestDate, string? LatestDateText, DateTimeOffset? InstalledDate, string? InstalledDateText)> CheckInstanceUpdateDetailsAsync(
        GameInstance instance,
        SteamStoreApiClient steamClient,
        CancellationToken ct = default)
    {
        if (instance == null || instance.AppId == 0 || steamClient == null || ct.IsCancellationRequested)
        {
            return (UpdateCheckStatus.Unknown, null, null, null, null, null);
        }

        try
        {
            var depotInfo = await steamClient.GetAppDepotInfoAsync(instance.AppId, ct).ConfigureAwait(false);
            var latestDate = depotInfo?.LatestBuildDate ?? await steamClient.GetLatestAppUpdateDateAsync(instance.AppId, ct).ConfigureAwait(false);

            if (!latestDate.HasValue && instance.Metadata != null && !string.IsNullOrWhiteSpace(instance.Metadata.ReleaseDate))
            {
                if (DateTimeOffset.TryParse(instance.Metadata.ReleaseDate, out var relDate))
                {
                    latestDate = relDate;
                }
            }

            var latestDateText = latestDate.HasValue ? $"{latestDate.Value:d MMM yyyy}" : instance.Metadata?.ReleaseDate;

            // 1. Resolve authentic installed patch/version date:
            // Priority: Instance.InstalledVersionDate -> Local manifest files -> SteamCMD matching -> Local installation time
            DateTimeOffset? installedDate = instance.InstalledVersionDate;
            if (!installedDate.HasValue)
            {
                installedDate = GetInstalledManifestDate(instance);
            }

            // If no local manifest file exists on disk, check if installed depots match public or SteamCMD builds
            if (!installedDate.HasValue && depotInfo != null && instance.Depots.Count > 0)
            {
                bool allMatch = true;
                bool hasCheck = false;
                foreach (var depot in instance.Depots)
                {
                    if (depot.ManifestId > 0 && depotInfo.PublicManifests.TryGetValue(depot.DepotId, out var pubGid) && pubGid > 0)
                    {
                        hasCheck = true;
                        if (depot.ManifestId != pubGid)
                        {
                            allMatch = false;
                            break;
                        }
                    }
                }
                if (hasCheck && allMatch && depotInfo.LatestBuildDate.HasValue)
                {
                    installedDate = depotInfo.LatestBuildDate.Value;
                }
            }

            // Fallback: use instance local installation timestamp instead of the game's initial release date
            if (!installedDate.HasValue)
            {
                installedDate = instance.UpdatedAt > DateTimeOffset.MinValue ? instance.UpdatedAt : instance.CreatedAt;
            }

            var installedDateText = installedDate.HasValue ? $"{installedDate.Value:d MMM yyyy}" : null;

            // 2. Direct Depot Manifest ID comparison
            if (instance.Status == InstanceStatus.Ready && depotInfo != null && instance.Depots.Count > 0)
            {
                bool allDepotsMatch = true;
                bool hasCheckedDepot = false;

                foreach (var depot in instance.Depots)
                {
                    // Only compare non-zero manifest IDs
                    if (depot.ManifestId > 0 && depotInfo.PublicManifests.TryGetValue(depot.DepotId, out var publicGid) && publicGid > 0)
                    {
                        hasCheckedDepot = true;
                        if (depot.ManifestId != publicGid)
                        {
                            allDepotsMatch = false;
                            break;
                        }
                    }
                }

                if (hasCheckedDepot && !allDepotsMatch)
                {
                    // If dates are also present and show no difference, don't flag as outdated
                    if (installedDate.HasValue && latestDate.HasValue && latestDate.Value <= installedDate.Value.AddDays(1))
                    {
                        return (UpdateCheckStatus.UpToDate, null, latestDate, latestDateText, installedDate, installedDateText);
                    }

                    return (UpdateCheckStatus.UpdateAvailable, "Newer depot build available on Steam/DepotBox", latestDate, latestDateText, installedDate, installedDateText);
                }
                else if (hasCheckedDepot && allDepotsMatch)
                {
                    var confirmedDate = installedDate ?? latestDate;
                    var confirmedText = installedDateText ?? latestDateText;
                    return (UpdateCheckStatus.UpToDate, null, latestDate, latestDateText, confirmedDate, confirmedText);
                }
            }

            // 3. Date consistency check: If installed date and latest date are within threshold, up to date
            if (installedDate.HasValue && latestDate.HasValue)
            {
                if (Math.Abs((latestDate.Value - installedDate.Value).TotalHours) <= 36.0 || latestDate.Value <= installedDate.Value.AddDays(1))
                {
                    return (UpdateCheckStatus.UpToDate, null, latestDate, latestDateText, installedDate, installedDateText ?? latestDateText);
                }
            }

            // 3. Build / Manifest date comparison
            if (installedDate.HasValue && latestDate.HasValue)
            {
                if (latestDate.Value > installedDate.Value.AddDays(1))
                {
                    return (UpdateCheckStatus.UpdateAvailable, $"New build released on {latestDate.Value:d MMM yyyy}", latestDate, latestDateText, installedDate, installedDateText);
                }
                else
                {
                    return (UpdateCheckStatus.UpToDate, null, latestDate, latestDateText, installedDate, installedDateText);
                }
            }

            // If neither depot manifests nor dates could be verified, return Unknown rather than false UpToDate
            if (depotInfo == null && !latestDate.HasValue)
            {
                return (UpdateCheckStatus.Unknown, null, null, null, installedDate, installedDateText);
            }

            return (UpdateCheckStatus.UpToDate, null, latestDate, latestDateText, installedDate, installedDateText);
        }
        catch
        {
            return (UpdateCheckStatus.Unknown, null, null, null, null, null);
        }
    }
}
