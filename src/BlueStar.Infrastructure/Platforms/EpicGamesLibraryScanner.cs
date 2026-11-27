using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace BlueStar.Infrastructure.Platforms;

/// <summary>
/// Model representing a detected game installed via Epic Games Launcher.
/// </summary>
public record InstalledEpicGame(
    string AppName,
    string DisplayName,
    string InstallLocation,
    string? LaunchExecutable,
    long SizeOnDiskBytes,
    string? CatalogNamespace = null,
    string? CatalogItemId = null)
{
    public string? ExecutableFullPath => !string.IsNullOrWhiteSpace(LaunchExecutable)
        ? Path.Combine(InstallLocation, LaunchExecutable.Replace('/', Path.DirectorySeparatorChar))
        : null;

    public string FormattedSize => SizeOnDiskBytes switch
    {
        >= 1024L * 1024L * 1024L => $"{SizeOnDiskBytes / (1024.0 * 1024.0 * 1024.0):F1} GB",
        >= 1024L * 1024L => $"{SizeOnDiskBytes / (1024.0 * 1024.0):F1} MB",
        > 0 => $"{SizeOnDiskBytes / 1024.0:F1} KB",
        _ => "Installed"
    };
}

/// <summary>
/// Scans local Epic Games Launcher manifests to discover installed Epic Games titles.
/// </summary>
public static class EpicGamesLibraryScanner
{
    public static async Task<IReadOnlyList<InstalledEpicGame>> ScanInstalledGamesAsync(CancellationToken ct = default)
    {
        return await Task.Run(() => ScanInstalledGamesSync(ct), ct).ConfigureAwait(false);
    }

    public static bool IsGameInstalled(string? appName, string? installPath)
    {
        if (string.IsNullOrWhiteSpace(installPath) || !Directory.Exists(installPath))
            return false;

        if (!OperatingSystem.IsWindows())
            return Directory.Exists(installPath);

        var installed = ScanInstalledGamesSync(CancellationToken.None);
        if (!string.IsNullOrWhiteSpace(appName))
        {
            return installed.Any(g =>
                string.Equals(g.AppName, appName, StringComparison.OrdinalIgnoreCase) &&
                Directory.Exists(g.InstallLocation));
        }

        var normalized = installPath.TrimEnd('\\', '/');
        return installed.Any(g =>
            string.Equals(g.InstallLocation.TrimEnd('\\', '/'), normalized, StringComparison.OrdinalIgnoreCase));
    }

    private static IReadOnlyList<InstalledEpicGame> ScanInstalledGamesSync(CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows())
            return [];

        var results = new List<InstalledEpicGame>();
        var manifestDirs = GetManifestDirectories();

        foreach (var dir in manifestDirs)
        {
            if (ct.IsCancellationRequested) break;
            if (!Directory.Exists(dir)) continue;

            try
            {
                foreach (var itemFile in Directory.EnumerateFiles(dir, "*.item"))
                {
                    if (ct.IsCancellationRequested) break;
                    try
                    {
                        var game = ParseItemManifest(itemFile);
                        if (game != null && Directory.Exists(game.InstallLocation))
                        {
                            results.Add(game);
                        }
                    }
                    catch { }
                }
            }
            catch { }
        }

        // Fallback: LauncherInstalled.dat
        if (results.Count == 0)
        {
            TryReadLauncherInstalledDat(results, ct);
        }

        return results
            .DistinctBy(g => g.AppName, StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static List<string> GetManifestDirectories()
    {
        var dirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var commonAppData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        if (!string.IsNullOrWhiteSpace(commonAppData))
        {
            dirs.Add(Path.Combine(commonAppData, "Epic", "EpicGamesLauncher", "Data", "Manifests"));
        }

        if (OperatingSystem.IsWindows())
        {
            try
            {
                using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Epic Games\EpicGamesLauncher")
                                ?? Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Epic Games\EpicGamesLauncher");
                if (key?.GetValue("AppDataPath") is string appDataPath && !string.IsNullOrWhiteSpace(appDataPath))
                {
                    dirs.Add(Path.Combine(appDataPath, "Manifests"));
                }
            }
            catch { }
        }

        return dirs.ToList();
    }

    private static InstalledEpicGame? ParseItemManifest(string itemFilePath)
    {
        using var stream = File.OpenRead(itemFilePath);
        using var doc = JsonDocument.Parse(stream);
        var root = doc.RootElement;

        if (root.ValueKind != JsonValueKind.Object)
            return null;

        var appName = GetString(root, "AppName");
        var displayName = GetString(root, "DisplayName");
        var installLocation = GetString(root, "InstallLocation");
        if (string.IsNullOrWhiteSpace(appName) || string.IsNullOrWhiteSpace(installLocation))
            return null;

        var catalogNamespace = GetString(root, "CatalogNamespace");
        var catalogItemId = GetString(root, "CatalogItemId");

        // Skip Unreal Engine / non-game tools
        if (string.Equals(catalogNamespace, "ue", StringComparison.OrdinalIgnoreCase) ||
            appName.StartsWith("UE_", StringComparison.OrdinalIgnoreCase))
            return null;

        if (root.TryGetProperty("bIsApplication", out var isAppProp) && isAppProp.ValueKind == JsonValueKind.False)
            return null;

        if (root.TryGetProperty("AppCategories", out var catsProp) && catsProp.ValueKind == JsonValueKind.Array)
        {
            var cats = catsProp.EnumerateArray()
                .Where(c => c.ValueKind == JsonValueKind.String)
                .Select(c => c.GetString() ?? string.Empty)
                .ToList();
            if (cats.Count > 0 &&
                !cats.Any(c => string.Equals(c, "games", StringComparison.OrdinalIgnoreCase)) &&
                cats.Any(c => c.Equals("engines", StringComparison.OrdinalIgnoreCase) ||
                              c.Equals("plugins", StringComparison.OrdinalIgnoreCase) ||
                              c.Equals("assets", StringComparison.OrdinalIgnoreCase)))
            {
                return null;
            }
        }

        var launchExecutable = GetString(root, "LaunchExecutable");
        long installSize = 0;
        if (root.TryGetProperty("InstallSize", out var sizeProp) && sizeProp.TryGetInt64(out var parsedSize))
            installSize = parsedSize;

        return new InstalledEpicGame(
            AppName: appName,
            DisplayName: string.IsNullOrWhiteSpace(displayName) ? appName : displayName,
            InstallLocation: installLocation,
            LaunchExecutable: launchExecutable,
            SizeOnDiskBytes: installSize,
            CatalogNamespace: catalogNamespace,
            CatalogItemId: catalogItemId);
    }

    private static void TryReadLauncherInstalledDat(List<InstalledEpicGame> results, CancellationToken ct)
    {
        var commonAppData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        if (string.IsNullOrWhiteSpace(commonAppData)) return;

        var datPath = Path.Combine(commonAppData, "Epic", "UnrealEngineLauncher", "LauncherInstalled.dat");
        if (!File.Exists(datPath)) return;

        try
        {
            using var stream = File.OpenRead(datPath);
            using var doc = JsonDocument.Parse(stream);
            if (!doc.RootElement.TryGetProperty("InstallationList", out var list) || list.ValueKind != JsonValueKind.Array)
                return;

            foreach (var entry in list.EnumerateArray())
            {
                if (ct.IsCancellationRequested) break;
                var appName = GetString(entry, "AppName");
                var installLocation = GetString(entry, "InstallLocation");
                var ns = GetString(entry, "NamespaceId");
                var itemId = GetString(entry, "ItemId");

                if (string.IsNullOrWhiteSpace(appName) || string.IsNullOrWhiteSpace(installLocation))
                    continue;
                if (string.Equals(ns, "ue", StringComparison.OrdinalIgnoreCase) ||
                    appName.StartsWith("UE_", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!Directory.Exists(installLocation))
                    continue;

                var displayName = Path.GetFileName(installLocation.TrimEnd('\\', '/'));
                results.Add(new InstalledEpicGame(
                    AppName: appName,
                    DisplayName: string.IsNullOrWhiteSpace(displayName) ? appName : displayName,
                    InstallLocation: installLocation,
                    LaunchExecutable: null,
                    SizeOnDiskBytes: 0,
                    CatalogNamespace: ns,
                    CatalogItemId: itemId));
            }
        }
        catch { }
    }

    private static string? GetString(JsonElement obj, string propertyName)
    {
        return obj.TryGetProperty(propertyName, out var prop) && prop.ValueKind == JsonValueKind.String
            ? prop.GetString()
            : null;
    }
}
