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

    /// <summary>
    /// Scans all local Steam library folders and appmanifest_*.acf files to build a mapping
    /// from DepotId to (AppId, GameName).
    /// </summary>
    public static Dictionary<uint, (uint AppId, string GameName)> ScanInstalledDepotsToGameMap()
    {
        var map = new Dictionary<uint, (uint AppId, string GameName)>();
        var steamPath = ShortcutHelper.GetSteamPath();
        if (string.IsNullOrWhiteSpace(steamPath) || !Directory.Exists(steamPath))
            return map;

        var libraryFolders = GetLibraryFolders(steamPath);
        foreach (var libraryFolder in libraryFolders)
        {
            var steamapps = Path.Combine(libraryFolder, "steamapps");
            if (!Directory.Exists(steamapps)) continue;

            try
            {
                var manifestFiles = Directory.GetFiles(steamapps, "appmanifest_*.acf");
                foreach (var manifestFile in manifestFiles)
                {
                    try
                    {
                        var text = File.ReadAllText(manifestFile);
                        var appIdMatch = Regex.Match(text, @"""appid""\s+""(\d+)""", RegexOptions.IgnoreCase);
                        if (!appIdMatch.Success || !uint.TryParse(appIdMatch.Groups[1].Value, out var appId) || appId == 0)
                            continue;

                        var nameMatch = Regex.Match(text, @"""name""\s+""([^""]+)""", RegexOptions.IgnoreCase);
                        var name = nameMatch.Success ? nameMatch.Groups[1].Value : $"App {appId}";

                        map[appId] = (appId, name);

                        // Parse InstalledDepots with balanced braces to handle nested structures properly
                        ExtractDepotsFromVdfSection(text, "\"InstalledDepots\"", appId, name, map);

                        // Parse SharedDepots
                        ExtractSharedDepotsFromVdfSection(text, appId, name, map);
                    }
                    catch { }
                }
            }
            catch { }
        }

        return map;
    }

    private static void ExtractDepotsFromVdfSection(
        string text,
        string sectionHeader,
        uint appId,
        string name,
        Dictionary<uint, (uint AppId, string GameName)> map)
    {
        var idx = text.IndexOf(sectionHeader, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return;

        int openBrace = text.IndexOf('{', idx);
        if (openBrace < 0) return;

        int depth = 1;
        int endBrace = -1;
        for (int i = openBrace + 1; i < text.Length; i++)
        {
            if (text[i] == '{') depth++;
            else if (text[i] == '}')
            {
                depth--;
                if (depth == 0)
                {
                    endBrace = i;
                    break;
                }
            }
        }

        if (endBrace > openBrace)
        {
            var sectionBlock = text.Substring(openBrace, endBrace - openBrace);
            var matches = Regex.Matches(sectionBlock, @"""(\d+)""\s*\{");
            foreach (Match m in matches)
            {
                if (uint.TryParse(m.Groups[1].Value, out var dId) && dId > 0)
                {
                    map[dId] = (appId, name);
                }
            }
        }
    }

    private static void ExtractSharedDepotsFromVdfSection(
        string text,
        uint appId,
        string name,
        Dictionary<uint, (uint AppId, string GameName)> map)
    {
        var idx = text.IndexOf("\"SharedDepots\"", StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return;

        int openBrace = text.IndexOf('{', idx);
        if (openBrace < 0) return;

        int depth = 1;
        int endBrace = -1;
        for (int i = openBrace + 1; i < text.Length; i++)
        {
            if (text[i] == '{') depth++;
            else if (text[i] == '}')
            {
                depth--;
                if (depth == 0)
                {
                    endBrace = i;
                    break;
                }
            }
        }

        if (endBrace > openBrace)
        {
            var sectionBlock = text.Substring(openBrace, endBrace - openBrace);
            var matches = Regex.Matches(sectionBlock, @"""(\d+)""\s*""(\d+)""");
            foreach (Match m in matches)
            {
                if (uint.TryParse(m.Groups[1].Value, out var dId) && dId > 0)
                {
                    map[dId] = (appId, name);
                }
            }
        }
    }

    /// <summary>
    /// Attempts to resolve any DepotId to its corresponding (AppId, GameName) using exact mapping,
    /// known owned app metadata, redistributable ranges, or stem prefix matching.
    /// </summary>
    public static (uint AppId, string GameName) TryResolveDepotToGame(
        uint depotId,
        IReadOnlyDictionary<uint, (uint AppId, string GameName)> map,
        IReadOnlyDictionary<uint, string>? appNameMap = null)
    {
        if (map.TryGetValue(depotId, out var direct))
        {
            return direct;
        }

        // 1. Direct AppId match (base depots often equal AppId)
        if (appNameMap != null && appNameMap.TryGetValue(depotId, out var exactAppName))
        {
            return (depotId, exactAppName);
        }

        // 2. Steamworks Common Redistributables range (228980 - 229009)
        if (depotId >= 228980 && depotId <= 229009)
        {
            return (228980, "Steamworks Common Redistributables");
        }

        // 3. Prefix matching against known apps in map and appNameMap
        var depotStr = depotId.ToString();
        if (appNameMap != null)
        {
            foreach (var (appId, appName) in appNameMap)
            {
                var appStr = appId.ToString();
                if (depotStr.Length >= 3 && appStr.Length >= 3)
                {
                    int matchLen = Math.Min(depotStr.Length, appStr.Length) - 1;
                    if (matchLen >= 2 && depotStr[..matchLen] == appStr[..matchLen])
                    {
                        return (appId, appName);
                    }
                }
            }
        }

        foreach (var kvp in map)
        {
            var appStr = kvp.Value.AppId.ToString();
            if (depotStr.Length >= 3 && appStr.Length >= 3)
            {
                int matchLen = Math.Min(depotStr.Length, appStr.Length) - 1;
                if (matchLen >= 2 && depotStr[..matchLen] == appStr[..matchLen])
                {
                    return kvp.Value;
                }
            }
        }

        return (depotId, $"Depot {depotId}");
    }
}
