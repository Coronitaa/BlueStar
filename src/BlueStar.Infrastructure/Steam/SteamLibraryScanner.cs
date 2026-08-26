using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using BlueStar.Core.Helpers;

namespace BlueStar.Infrastructure.Steam;

/// <summary>
/// Model representing a detected game installed in a local Steam library folder.
/// </summary>
public record InstalledSteamGame(
    uint AppId,
    string Name,
    string InstallDir,
    string FullPath,
    long SizeOnDiskBytes,
    string? HeaderImageUrl = null)
{
    public string HeaderImageUrlFormatted => !string.IsNullOrWhiteSpace(HeaderImageUrl)
        ? HeaderImageUrl
        : $"https://cdn.cloudflare.steamstatic.com/steam/apps/{AppId}/header.jpg";

    public string FormattedSize => SizeOnDiskBytes switch
    {
        >= 1024L * 1024L * 1024L => $"{SizeOnDiskBytes / (1024.0 * 1024.0 * 1024.0):F1} GB",
        >= 1024L * 1024L => $"{SizeOnDiskBytes / (1024.0 * 1024.0):F1} MB",
        > 0 => $"{SizeOnDiskBytes / 1024.0:F1} KB",
        _ => "Installed"
    };
}

/// <summary>
/// Scans local Steam installations across all configured library folders to discover installed games and their manifests.
/// </summary>
public static class SteamLibraryScanner
{
    private static readonly HashSet<uint> ExcludedAppIds =
    [
        228980, // Steamworks Common Redistributables
        1070560, // Steam Linux Runtime
        1391110, // Steam Linux Runtime - Soldier
        1628350, // Steam Linux Runtime - Sniper
        893800,  // Proton 4.11
        1113280, // Proton 5.0
        1245040, // Proton 5.13
        1420170, // Proton 6.3
        1580130, // Proton 7.0
        2348590, // Proton 8.0
        2805730, // Proton 9.0
        1493710, // Proton Experimental
        22300    // SteamVR
    ];

    public static async Task<IReadOnlyList<InstalledSteamGame>> ScanInstalledGamesAsync(CancellationToken ct = default)
    {
        return await Task.Run(() =>
        {
            var results = new List<InstalledSteamGame>();
            var steamPath = ShortcutHelper.GetSteamPath();
            if (string.IsNullOrWhiteSpace(steamPath) || !Directory.Exists(steamPath))
                return (IReadOnlyList<InstalledSteamGame>)results;

            var libraryFolders = GetLibraryFolders(steamPath);

            foreach (var libraryFolder in libraryFolders)
            {
                if (ct.IsCancellationRequested) break;

                var steamapps = Path.Combine(libraryFolder, "steamapps");
                if (!Directory.Exists(steamapps)) continue;

                var commonDir = Path.Combine(steamapps, "common");

                try
                {
                    var manifestFiles = Directory.GetFiles(steamapps, "appmanifest_*.acf");
                    foreach (var manifestFile in manifestFiles)
                    {
                        if (ct.IsCancellationRequested) break;

                        try
                        {
                            var game = ParseManifest(manifestFile, commonDir);
                            if (game != null && !ExcludedAppIds.Contains(game.AppId) && Directory.Exists(game.FullPath))
                            {
                                results.Add(game);
                            }
                        }
                        catch { }
                    }
                }
                catch { }
            }

            return results
                .DistinctBy(g => g.AppId)
                .OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }, ct).ConfigureAwait(false);
    }

    private static List<string> GetLibraryFolders(string mainSteamPath)
    {
        var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { mainSteamPath };
        var libraryVdf = Path.Combine(mainSteamPath, "steamapps", "libraryfolders.vdf");

        if (File.Exists(libraryVdf))
        {
            try
            {
                var content = File.ReadAllText(libraryVdf);
                var matches = Regex.Matches(content, @"""path""\s+""([^""]+)""", RegexOptions.IgnoreCase);
                foreach (Match m in matches)
                {
                    if (m.Success && m.Groups.Count > 1)
                    {
                        var rawPath = m.Groups[1].Value.Replace(@"\\", @"\");
                        if (Directory.Exists(rawPath))
                        {
                            folders.Add(rawPath);
                        }
                    }
                }
            }
            catch { }
        }

        return folders.ToList();
    }

    private static InstalledSteamGame? ParseManifest(string manifestFilePath, string commonDir)
    {
        var text = File.ReadAllText(manifestFilePath);

        var appIdMatch = Regex.Match(text, @"""appid""\s+""(\d+)""", RegexOptions.IgnoreCase);
        if (!appIdMatch.Success || !uint.TryParse(appIdMatch.Groups[1].Value, out var appId) || appId == 0)
            return null;

        var nameMatch = Regex.Match(text, @"""name""\s+""([^""]+)""", RegexOptions.IgnoreCase);
        var name = nameMatch.Success ? nameMatch.Groups[1].Value : $"App {appId}";

        var installDirMatch = Regex.Match(text, @"""installdir""\s+""([^""]+)""", RegexOptions.IgnoreCase);
        var installDir = installDirMatch.Success ? installDirMatch.Groups[1].Value : name;

        var sizeMatch = Regex.Match(text, @"""SizeOnDisk""\s+""(\d+)""", RegexOptions.IgnoreCase);
        _ = long.TryParse(sizeMatch.Success ? sizeMatch.Groups[1].Value : "0", out var sizeBytes);

        var fullPath = Path.Combine(commonDir, installDir);

        return new InstalledSteamGame(appId, name, installDir, fullPath, sizeBytes);
    }
}
