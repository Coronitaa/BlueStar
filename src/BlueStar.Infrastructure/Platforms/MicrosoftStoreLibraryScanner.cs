using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace BlueStar.Infrastructure.Platforms;

/// <summary>
/// Model representing a detected game installed via Microsoft Store or Xbox App.
/// </summary>
public record InstalledMicrosoftStoreGame(
    string PackageId,
    string DisplayName,
    string InstallLocation,
    string? ExecutablePath,
    long SizeOnDiskBytes,
    string? LogoPath = null)
{
    public string FormattedSize => SizeOnDiskBytes switch
    {
        >= 1024L * 1024L * 1024L => $"{SizeOnDiskBytes / (1024.0 * 1024.0 * 1024.0):F1} GB",
        >= 1024L * 1024L => $"{SizeOnDiskBytes / (1024.0 * 1024.0):F1} MB",
        > 0 => $"{SizeOnDiskBytes / 1024.0:F1} KB",
        _ => "Installed"
    };
}

/// <summary>
/// Scans local Xbox Games / Microsoft Store installations (via .GamingRoot, XboxGames folders,
/// MicrosoftGame.Config, and AppModel package registry) to discover installed Store games.
/// </summary>
public static class MicrosoftStoreLibraryScanner
{
    private static readonly string[] IgnoredPackagePrefixes =
    [
        "Microsoft.Windows.",
        "Microsoft.VCLibs",
        "Microsoft.NET.",
        "Microsoft.UI.",
        "Microsoft.DirectX",
        "Microsoft.Services.",
        "Microsoft.GamingServices",
        "Microsoft.GamingApp",
        "Microsoft.Xbox",
        "Microsoft.StorePurchaseApp",
        "Microsoft.WindowsStore",
        "Microsoft.DesktopAppInstaller",
        "Microsoft.WebMediaExtensions",
        "Microsoft.VP9VideoExtensions",
        "Microsoft.HEIFImageExtension",
        "Microsoft.HEVCVideoExtension",
        "Microsoft.WebpImageExtension",
        "Microsoft.RawImageExtension",
        "Microsoft.AV1VideoExtension",
        "Microsoft.Edge",
        "Microsoft.Bing",
        "Microsoft.Zune",
        "Microsoft.ScreenSketch",
        "Microsoft.Paint",
        "Microsoft.Notepad",
        "Microsoft.549981C3F5F10",
        "Windows.",
        "windows.",
        "NVIDIACorp.",
        "AMD.",
        "Intel."
    ];

    public static async Task<IReadOnlyList<InstalledMicrosoftStoreGame>> ScanInstalledGamesAsync(CancellationToken ct = default)
    {
        return await Task.Run(() => ScanInstalledGamesSync(ct), ct).ConfigureAwait(false);
    }

    public static bool IsGameInstalled(string? packageId, string? installPath)
    {
        if (string.IsNullOrWhiteSpace(installPath) || !Directory.Exists(installPath))
            return false;

        if (!OperatingSystem.IsWindows())
            return Directory.Exists(installPath);

        var hasConfig = File.Exists(Path.Combine(installPath, "MicrosoftGame.Config")) ||
                        File.Exists(Path.Combine(installPath, "AppxManifest.xml"));
        if (!hasConfig)
            return false;

        if (string.IsNullOrWhiteSpace(packageId))
            return true;

        var scanned = ScanInstalledGamesSync(CancellationToken.None);
        return scanned.Any(g =>
            string.Equals(g.PackageId, packageId, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(g.InstallLocation.TrimEnd('\\', '/'), installPath.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase));
    }

    private static IReadOnlyList<InstalledMicrosoftStoreGame> ScanInstalledGamesSync(CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows())
            return [];

        var results = new List<InstalledMicrosoftStoreGame>();
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 1. Scan XboxGames / .GamingRoot folders across all fixed drives
        foreach (var rootDir in GetXboxGameRoots())
        {
            if (ct.IsCancellationRequested) break;
            if (!Directory.Exists(rootDir)) continue;

            try
            {
                foreach (var gameDir in Directory.EnumerateDirectories(rootDir))
                {
                    if (ct.IsCancellationRequested) break;

                    // Xbox PC games typically live inside <GameFolder>\Content\MicrosoftGame.Config or directly in <GameFolder>\MicrosoftGame.Config
                    var contentDir = Path.Combine(gameDir, "Content");
                    var candidateDir = File.Exists(Path.Combine(contentDir, "MicrosoftGame.Config"))
                        ? contentDir
                        : gameDir;

                    var configPath = Path.Combine(candidateDir, "MicrosoftGame.Config");
                    if (!File.Exists(configPath))
                        continue;

                    var folderDisplayName = Path.GetFileName(gameDir.TrimEnd('\\', '/'));
                    var parsed = ParseMicrosoftGameConfig(configPath, candidateDir, folderDisplayName);
                    if (parsed != null && seenPaths.Add(parsed.InstallLocation.TrimEnd('\\', '/')))
                    {
                        results.Add(parsed);
                    }
                }
            }
            catch { }
        }

        // 2. Scan GamingServices PackageRepository & AppModel Packages in Registry
        TryScanRegistryPackages(results, seenPaths, ct);

        return results
            .DistinctBy(g => g.PackageId, StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static List<string> GetXboxGameRoots()
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            foreach (var drive in DriveInfo.GetDrives())
            {
                if (!drive.IsReady || drive.DriveType != DriveType.Fixed) continue;
                var driveRoot = drive.RootDirectory.FullName;

                var gamingRootFile = Path.Combine(driveRoot, ".GamingRoot");
                if (File.Exists(gamingRootFile))
                {
                    var customFolder = TryReadGamingRootFile(gamingRootFile);
                    if (!string.IsNullOrWhiteSpace(customFolder))
                    {
                        var fullCustom = Path.IsPathRooted(customFolder)
                            ? customFolder
                            : Path.Combine(driveRoot, customFolder);
                        if (Directory.Exists(fullCustom))
                            roots.Add(fullCustom);
                    }
                }

                var defaultXbox = Path.Combine(driveRoot, "XboxGames");
                if (Directory.Exists(defaultXbox))
                    roots.Add(defaultXbox);

                var modifiable = Path.Combine(driveRoot, "Program Files", "ModifiableWindowsApps");
                if (Directory.Exists(modifiable))
                    roots.Add(modifiable);
            }
        }
        catch { }

        return roots.ToList();
    }

    private static string? TryReadGamingRootFile(string filePath)
    {
        try
        {
            var bytes = File.ReadAllBytes(filePath);
            // .GamingRoot starts with 8-byte header ("RGBX" + version/flags) followed by UTF-16LE relative path
            if (bytes.Length <= 8) return null;
            var text = Encoding.Unicode.GetString(bytes, 8, bytes.Length - 8).Trim('\0', ' ', '\r', '\n');
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }
        catch
        {
            return null;
        }
    }

    private static void TryScanRegistryPackages(
        List<InstalledMicrosoftStoreGame> results,
        HashSet<string> seenPaths,
        CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows()) return;

        try
        {
            using var packagesKey = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages");
            if (packagesKey == null) return;

            foreach (var subKeyName in packagesKey.GetSubKeyNames())
            {
                if (ct.IsCancellationRequested) break;
                if (IgnoredPackagePrefixes.Any(p => subKeyName.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
                    continue;

                try
                {
                    using var pkgKey = packagesKey.OpenSubKey(subKeyName);
                    if (pkgKey?.GetValue("PackageRootFolder") is not string rootFolder || string.IsNullOrWhiteSpace(rootFolder))
                        continue;

                    if (!Directory.Exists(rootFolder))
                        continue;

                    var normalized = rootFolder.TrimEnd('\\', '/');
                    if (seenPaths.Contains(normalized))
                        continue;

                    var mgcPath = Path.Combine(rootFolder, "MicrosoftGame.Config");
                    if (File.Exists(mgcPath))
                    {
                        var parsed = ParseMicrosoftGameConfig(mgcPath, rootFolder, null);
                        if (parsed != null && seenPaths.Add(normalized))
                        {
                            results.Add(parsed);
                        }
                        continue;
                    }

                    // Only include AppxManifest packages that explicitly reside in XboxGames or ModifiableWindowsApps
                    // to avoid polluting the list with regular UWP productivity/system apps.
                    var isGameFolder = rootFolder.Contains("XboxGames", StringComparison.OrdinalIgnoreCase) ||
                                       rootFolder.Contains("ModifiableWindowsApps", StringComparison.OrdinalIgnoreCase);
                    if (!isGameFolder) continue;

                    var appxPath = Path.Combine(rootFolder, "AppxManifest.xml");
                    if (File.Exists(appxPath))
                    {
                        var parsed = ParseAppxManifest(appxPath, rootFolder, subKeyName);
                        if (parsed != null && seenPaths.Add(normalized))
                        {
                            results.Add(parsed);
                        }
                    }
                }
                catch { }
            }
        }
        catch { }
    }

    private static InstalledMicrosoftStoreGame? ParseMicrosoftGameConfig(
        string configPath,
        string installDir,
        string? fallbackName)
    {
        try
        {
            var doc = XDocument.Load(configPath);
            var root = doc.Root;
            if (root == null) return null;

            var identityEl = root.Element("Identity");
            var identityName = identityEl?.Attribute("Name")?.Value;
            if (string.IsNullOrWhiteSpace(identityName))
                identityName = fallbackName ?? Path.GetFileName(installDir.TrimEnd('\\', '/'));

            var shellVisuals = root.Element("ShellVisuals");
            var displayName = shellVisuals?.Attribute("DefaultDisplayName")?.Value;
            if (string.IsNullOrWhiteSpace(displayName) ||
                displayName.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase))
            {
                displayName = fallbackName;
            }
            if (string.IsNullOrWhiteSpace(displayName))
            {
                displayName = identityName;
            }

            var logoRel = shellVisuals?.Attribute("Square150x150Logo")?.Value
                          ?? shellVisuals?.Attribute("StoreLogo")?.Value
                          ?? shellVisuals?.Attribute("SplashScreenImage")?.Value;
            string? logoPath = null;
            if (!string.IsNullOrWhiteSpace(logoRel))
            {
                var candidate = Path.Combine(installDir, logoRel.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(candidate))
                    logoPath = candidate;
            }

            var exeRel = root.Element("ExecutableList")?
                .Elements("Executable")
                .Select(e => e.Attribute("Name")?.Value)
                .FirstOrDefault(n => !string.IsNullOrWhiteSpace(n));

            string? exePath = null;
            if (!string.IsNullOrWhiteSpace(exeRel))
            {
                var candidateExe = Path.Combine(installDir, exeRel.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(candidateExe))
                    exePath = candidateExe;
            }

            return new InstalledMicrosoftStoreGame(
                PackageId: identityName,
                DisplayName: displayName,
                InstallLocation: installDir,
                ExecutablePath: exePath,
                SizeOnDiskBytes: EstimateDirectorySize(installDir),
                LogoPath: logoPath);
        }
        catch
        {
            return null;
        }
    }

    private static InstalledMicrosoftStoreGame? ParseAppxManifest(
        string appxPath,
        string installDir,
        string packageFullName)
    {
        try
        {
            var doc = XDocument.Load(appxPath);
            var root = doc.Root;
            if (root == null) return null;

            var ns = root.GetDefaultNamespace();
            var identityName = root.Element(ns + "Identity")?.Attribute("Name")?.Value ?? packageFullName;
            var displayName = root.Element(ns + "Properties")?.Element(ns + "DisplayName")?.Value;
            if (string.IsNullOrWhiteSpace(displayName) ||
                displayName.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase))
            {
                displayName = Path.GetFileName(installDir.TrimEnd('\\', '/'));
            }

            var appEl = root.Element(ns + "Applications")?.Element(ns + "Application");
            var exeRel = appEl?.Attribute("Executable")?.Value;
            string? exePath = null;
            if (!string.IsNullOrWhiteSpace(exeRel))
            {
                var candidate = Path.Combine(installDir, exeRel.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(candidate))
                    exePath = candidate;
            }

            return new InstalledMicrosoftStoreGame(
                PackageId: identityName,
                DisplayName: string.IsNullOrWhiteSpace(displayName) ? identityName : displayName,
                InstallLocation: installDir,
                ExecutablePath: exePath,
                SizeOnDiskBytes: EstimateDirectorySize(installDir),
                LogoPath: null);
        }
        catch
        {
            return null;
        }
    }

    private static long EstimateDirectorySize(string dir)
    {
        try
        {
            // Fast top-level + 1-level scan to avoid blocking on massive folders during discovery
            long total = 0;
            var di = new DirectoryInfo(dir);
            foreach (var fi in di.EnumerateFiles("*", SearchOption.TopDirectoryOnly))
                total += fi.Length;
            foreach (var sub in di.EnumerateDirectories())
            {
                try
                {
                    foreach (var fi in sub.EnumerateFiles("*", SearchOption.AllDirectories))
                        total += fi.Length;
                }
                catch { }
            }
            return total;
        }
        catch
        {
            return 0;
        }
    }
}
