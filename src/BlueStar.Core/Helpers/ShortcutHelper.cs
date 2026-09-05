using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace BlueStar.Core.Helpers;

/// <summary>
/// Result of a shortcut creation operation.
/// </summary>
public record ShortcutCreationResult(bool Success, string Message, IReadOnlyList<string> CreatedPaths);

/// <summary>
/// Helper methods for discovering game executables and creating Windows shell shortcuts (.lnk) and Steam shortcuts (shortcuts.vdf + grid artwork).
/// </summary>
public static class ShortcutHelper
{
    private static readonly HttpClient SharedHttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(10),
        DefaultRequestHeaders =
        {
            { "User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36" }
        }
    };

    private static readonly string[] ExcludedExePrefixes =
    [
        "unitycrashhandler",
        "crashpad_handler",
        "dxsetup",
        "vcredist",
        "dotnetfx",
        "unins",
        "setup",
        "easyanticheat",
        "epiconlineservices",
        "ue4prereqsetup",
        "ue5prereqsetup",
        "steam_api",
        "steamclient",
        "installer",
        "cleanup",
        "touchup",
        "support",
        "redist"
    ];

    /// <summary>
    /// Computes the IEEE 802.3 CRC32 hash of a UTF-8 string.
    /// </summary>
    public static uint ComputeCrc32(string input)
    {
        if (string.IsNullOrEmpty(input)) return 0;
        var bytes = Encoding.UTF8.GetBytes(input);
        uint crc = 0xFFFFFFFF;
        foreach (var b in bytes)
        {
            crc ^= b;
            for (int i = 0; i < 8; i++)
            {
                if ((crc & 1) != 0)
                    crc = (crc >> 1) ^ 0xEDB88320;
                else
                    crc >>= 1;
            }
        }
        return ~crc;
    }

    /// <summary>
    /// Finds all likely game executables inside the specified installation path.
    /// Filters out common crash handlers, redistributables, and uninstaller executables,
    /// and orders candidates so the most likely game executable appears first.
    /// </summary>
    public static List<string> FindGameExecutables(string installPath, string? gameName = null)
    {
        if (string.IsNullOrWhiteSpace(installPath) || !Directory.Exists(installPath))
            return [];

        try
        {
            var enumOptions = new EnumerationOptions
            {
                MaxRecursionDepth = 3,
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                ReturnSpecialDirectories = false
            };

            var exeFiles = Directory.GetFiles(installPath, "*.exe", enumOptions);
            var candidates = new List<string>();

            foreach (var exePath in exeFiles)
            {
                var fileName = Path.GetFileNameWithoutExtension(exePath).ToLowerInvariant();
                if (ExcludedExePrefixes.Any(prefix => fileName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
                                                      fileName.Contains(prefix, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                candidates.Add(exePath);
            }

            var cleanGameName = (gameName ?? "").ToLowerInvariant().Replace(" ", "").Replace(":", "").Replace("-", "").Replace("_", "");

            return candidates
                .OrderByDescending(path =>
                {
                    var name = Path.GetFileNameWithoutExtension(path).ToLowerInvariant().Replace(" ", "").Replace(":", "").Replace("-", "").Replace("_", "");
                    int score = 0;

                    // Root folder exe gets a bonus
                    var parentDir = Path.GetDirectoryName(path);
                    if (string.Equals(parentDir, installPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
                    {
                        score += 50;
                    }

                    // Game name matching bonus
                    if (!string.IsNullOrEmpty(cleanGameName))
                    {
                        if (name == cleanGameName) score += 100;
                        else if (name.Contains(cleanGameName) || cleanGameName.Contains(name)) score += 40;
                    }

                    // "Shipping" or "Win64" game binary bonus (common in Unreal / Unity games)
                    if (path.Contains("Win64", StringComparison.OrdinalIgnoreCase) || path.Contains("Shipping", StringComparison.OrdinalIgnoreCase))
                    {
                        score += 30;
                    }

                    return score;
                })
                .ThenBy(path => path.Length)
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    /// <summary>
    /// Gets the Steam installation directory from the Windows registry or common directory locations.
    /// </summary>
    public static string? GetSteamPath()
    {
        if (OperatingSystem.IsWindows())
        {
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
                var path = key?.GetValue("SteamPath")?.ToString();
                if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
                    return path;
            }
            catch { }

            try
            {
                using var keyLM = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Valve\Steam");
                var pathLM = keyLM?.GetValue("InstallPath")?.ToString();
                if (!string.IsNullOrWhiteSpace(pathLM) && Directory.Exists(pathLM))
                    return pathLM;
            }
            catch { }

            try
            {
                using var keyLM64 = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Valve\Steam");
                var pathLM64 = keyLM64?.GetValue("InstallPath")?.ToString();
                if (!string.IsNullOrWhiteSpace(pathLM64) && Directory.Exists(pathLM64))
                    return pathLM64;
            }
            catch { }
        }

        // Common directory fallbacks
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);

        string?[] commonPaths =
        [
            string.IsNullOrWhiteSpace(programFilesX86) ? null : Path.Combine(programFilesX86, "Steam"),
            string.IsNullOrWhiteSpace(programFiles) ? null : Path.Combine(programFiles, "Steam"),
            @"C:\Steam", @"D:\Steam", @"E:\Steam", @"F:\Steam", @"G:\Steam"
        ];

        foreach (var p in commonPaths)
        {
            if (!string.IsNullOrEmpty(p) && Directory.Exists(p))
                return p;
        }

        return null;
    }

    /// <summary>
    /// Checks whether a local Steam installation exists with a valid userdata directory.
    /// </summary>
    public static bool IsSteamInstalled()
    {
        var steamPath = GetSteamPath();
        if (string.IsNullOrWhiteSpace(steamPath)) return false;
        var userdata = Path.Combine(steamPath, "userdata");
        return Directory.Exists(userdata);
    }

    /// <summary>
    /// Builds a binary VDF entry for Steam's shortcuts.vdf file.
    /// </summary>
    public static byte[] BuildVdfEntry(int index, string appName, string exePath, string startDir, string? iconPath = null, string? launchOptions = null)
    {
        uint crc = ComputeCrc32(exePath + appName);
        uint appId = crc | 0x80000000;

        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);

        // Entry header: \x00 + index string + \x00
        bw.Write((byte)0);
        bw.Write(Encoding.UTF8.GetBytes(index.ToString()));
        bw.Write((byte)0);

        void WriteString(string key, string val)
        {
            bw.Write((byte)1);
            bw.Write(Encoding.UTF8.GetBytes(key));
            bw.Write((byte)0);
            bw.Write(Encoding.UTF8.GetBytes(val));
            bw.Write((byte)0);
        }

        void WriteInt(string key, uint val)
        {
            bw.Write((byte)2);
            bw.Write(Encoding.UTF8.GetBytes(key));
            bw.Write((byte)0);
            bw.Write(val);
        }

        WriteInt("appid", appId);
        WriteString("AppName", appName);
        WriteString("Exe", $"\"{exePath}\"");
        var cleanDir = startDir.TrimEnd('\\', '/');
        WriteString("StartDir", $"\"{cleanDir}\\\"");
        WriteString("icon", iconPath ?? "");
        WriteString("ShortcutPath", "");
        WriteString("LaunchOptions", launchOptions ?? "");
        WriteInt("IsHidden", 0);
        WriteInt("AllowDesktopConfig", 1);
        WriteInt("AllowOverlay", 1);
        WriteInt("OpenVR", 0);
        WriteInt("Devkit", 0);
        WriteString("DevkitGameID", "");
        WriteInt("DevkitOverrideAppID", 0);
        WriteInt("LastPlayTime", 0);
        WriteString("FlatpakAppID", "");

        // tags sub-section: \x00 + "tags" + \x00 + \x08
        bw.Write((byte)0);
        bw.Write(Encoding.UTF8.GetBytes("tags"));
        bw.Write((byte)0);
        bw.Write((byte)8);

        // End of entry
        bw.Write((byte)8);

        bw.Flush();
        return ms.ToArray();
    }

    /// <summary>
    /// Adds or updates a non-Steam game shortcut in a specific user's shortcuts.vdf.
    /// </summary>
    public static bool AddSteamShortcutToUser(string vdfPath, string appName, string exePath, string startDir, string? iconPath = null, string? launchOptions = null)
    {
        try
        {
            var configDir = Path.GetDirectoryName(vdfPath);
            if (!string.IsNullOrEmpty(configDir) && !Directory.Exists(configDir))
            {
                Directory.CreateDirectory(configDir);
            }

            byte[] raw;
            if (File.Exists(vdfPath))
            {
                raw = File.ReadAllBytes(vdfPath);
                var backupPath = $"{vdfPath}.bak";
                if (!File.Exists(backupPath))
                {
                    File.Copy(vdfPath, backupPath, overwrite: false);
                }
            }
            else
            {
                // Minimum empty shortcuts.vdf: \x00shortcuts\x00\x08\x08
                raw = [0x00, 0x73, 0x68, 0x6f, 0x72, 0x74, 0x63, 0x75, 0x74, 0x73, 0x00, 0x08, 0x08];
            }

            var appNameBytes = Encoding.UTF8.GetBytes(appName);
            var exeBytes = Encoding.UTF8.GetBytes(exePath);

            // Check if already added
            if (ContainsSequence(raw, appNameBytes) && ContainsSequence(raw, exeBytes))
            {
                return true; // Already registered
            }

            // Count existing entries by counting \x01AppName\x00 headers
            var appNameHeader = Encoding.UTF8.GetBytes("\x01AppName\x00");
            int index = CountSequenceOccurrences(raw, appNameHeader);

            var entryBytes = BuildVdfEntry(index, appName, exePath, startDir, iconPath, launchOptions);

            // Strip the trailing 2-byte VDF footer (\x08\x08)
            int endPos = raw.Length;
            if (endPos >= 2 && raw[endPos - 1] == 0x08 && raw[endPos - 2] == 0x08)
            {
                endPos -= 2;
            }
            else if (endPos >= 1 && raw[endPos - 1] == 0x08)
            {
                endPos -= 1;
            }

            var newRaw = new byte[endPos + entryBytes.Length + 2];
            Buffer.BlockCopy(raw, 0, newRaw, 0, endPos);
            Buffer.BlockCopy(entryBytes, 0, newRaw, endPos, entryBytes.Length);
            newRaw[^2] = 0x08;
            newRaw[^1] = 0x08;

            File.WriteAllBytes(vdfPath, newRaw);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Downloads and installs Steam grid artwork (vertical capsule, hero, logo, header)
    /// into Steam's userdata/userId/config/grid/ folder for the given shortcut.
    /// </summary>
    private static string? ExtractAssetRelPath(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.String) return element.GetString();
        if (element.ValueKind == JsonValueKind.Object)
        {
            if (element.TryGetProperty("english", out var en) && en.ValueKind == JsonValueKind.String)
                return en.GetString();
            if (element.TryGetProperty("en", out var en2) && en2.ValueKind == JsonValueKind.String)
                return en2.GetString();
            foreach (var prop in element.EnumerateObject())
            {
                if (prop.Value.ValueKind == JsonValueKind.String)
                    return prop.Value.GetString();
            }
        }
        return null;
    }

    /// <summary>
    /// Downloads and installs Steam grid artwork (vertical capsule, hero, logo, header, clienticon)
    /// into Steam's userdata/userId/config/grid/ folder for the given shortcut.
    /// Returns the path to the downloaded icon if available.
    /// </summary>
    public static async Task<string?> InstallSteamGridArtworkAsync(
        string userFolder,
        uint shortcutAppId32,
        uint originalGameAppId,
        string? customHeaderUrl = null,
        string? customCapsuleUrl = null,
        CancellationToken ct = default)
    {
        try
        {
            var gridDir = Path.Combine(userFolder, "config", "grid");
            Directory.CreateDirectory(gridDir);

            ulong appId64 = ((ulong)shortcutAppId32 << 32) | 0x02000000;
            string id32 = shortcutAppId32.ToString();
            string idSigned32 = unchecked((int)shortcutAppId32).ToString();
            string id64 = appId64.ToString();

            var http = SharedHttpClient;

            async Task<byte[]?> DownloadFirstAvailableAsset(IEnumerable<string?> sourceUrls)
            {
                foreach (var url in sourceUrls)
                {
                    if (string.IsNullOrWhiteSpace(url)) continue;
                    try
                    {
                        var resp = await http.GetAsync(url, ct).ConfigureAwait(false);
                        if (resp.IsSuccessStatusCode)
                        {
                            var bytes = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
                            if (bytes != null && bytes.Length > 100)
                            {
                                return bytes;
                            }
                        }
                    }
                    catch { }
                }
                return null;
            }

            async Task SaveAssetToGrid(byte[]? data, string[] destFileNames)
            {
                if (data is null || data.Length <= 100) return;
                foreach (var destName in destFileNames)
                {
                    try
                    {
                        var destPath = Path.Combine(gridDir, destName);
                        await File.WriteAllBytesAsync(destPath, data, ct).ConfigureAwait(false);
                    }
                    catch { }
                }
            }

            string? cmdCapsuleRel = null;
            string? cmdHeroRel = null;
            string? cmdLogoRel = null;
            string? cmdHeaderRel = null;
            string? cmdClientIcon = null;

            // Query Steam PICS / SteamDB data for exact library_assets_full relative paths and clienticon
            if (originalGameAppId > 0)
            {
                try
                {
                    var cmdUrl = $"https://api.steamcmd.net/v1/info/{originalGameAppId}";
                    var cmdJson = await http.GetStringAsync(cmdUrl, ct).ConfigureAwait(false);
                    using var doc = JsonDocument.Parse(cmdJson);
                    var appKey = originalGameAppId.ToString();
                    if (doc.RootElement.TryGetProperty("data", out var dataEl) &&
                        dataEl.TryGetProperty(appKey, out var appEntry))
                    {
                        JsonElement commonEl = default;
                        if (appEntry.TryGetProperty("common", out var c1)) commonEl = c1;
                        else if (appEntry.TryGetProperty("appinfo", out var ai) && ai.TryGetProperty("common", out var c2)) commonEl = c2;

                        if (commonEl.ValueKind == JsonValueKind.Object)
                        {
                            if (commonEl.TryGetProperty("library_assets_full", out var laf) && laf.ValueKind == JsonValueKind.Object)
                            {
                                if (laf.TryGetProperty("library_capsule", out var lc))
                                {
                                    if (lc.TryGetProperty("image2x", out var i2x)) cmdCapsuleRel = ExtractAssetRelPath(i2x);
                                    if (string.IsNullOrWhiteSpace(cmdCapsuleRel) && lc.TryGetProperty("image", out var i1x)) cmdCapsuleRel = ExtractAssetRelPath(i1x);
                                }

                                if (laf.TryGetProperty("library_hero", out var lh))
                                {
                                    if (lh.TryGetProperty("image2x", out var i2x)) cmdHeroRel = ExtractAssetRelPath(i2x);
                                    if (string.IsNullOrWhiteSpace(cmdHeroRel) && lh.TryGetProperty("image", out var i1x)) cmdHeroRel = ExtractAssetRelPath(i1x);
                                }

                                if (laf.TryGetProperty("library_logo", out var ll))
                                {
                                    if (ll.TryGetProperty("image2x", out var i2x)) cmdLogoRel = ExtractAssetRelPath(i2x);
                                    if (string.IsNullOrWhiteSpace(cmdLogoRel) && ll.TryGetProperty("image", out var i1x)) cmdLogoRel = ExtractAssetRelPath(i1x);
                                }

                                if (laf.TryGetProperty("library_header", out var lhd))
                                {
                                    if (lhd.TryGetProperty("image2x", out var i2x)) cmdHeaderRel = ExtractAssetRelPath(i2x);
                                    if (string.IsNullOrWhiteSpace(cmdHeaderRel) && lhd.TryGetProperty("image", out var i1x)) cmdHeaderRel = ExtractAssetRelPath(i1x);
                                }
                            }

                            if (string.IsNullOrWhiteSpace(cmdHeaderRel) && commonEl.TryGetProperty("header_image", out var hi))
                            {
                                cmdHeaderRel = ExtractAssetRelPath(hi);
                            }

                            if (commonEl.TryGetProperty("clienticon", out var ci) && ci.ValueKind == JsonValueKind.String)
                            {
                                cmdClientIcon = ci.GetString();
                            }
                            else if (commonEl.TryGetProperty("icon", out var ico) && ico.ValueKind == JsonValueKind.String)
                            {
                                cmdClientIcon = ico.GetString();
                            }
                        }
                    }
                }
                catch { }
            }

            var cdns = originalGameAppId > 0
                ? new[]
                {
                    $"https://shared.akamai.steamstatic.com/store_item_assets/steam/apps/{originalGameAppId}",
                    $"https://shared.fastly.steamstatic.com/store_item_assets/steam/apps/{originalGameAppId}",
                    $"https://shared.cloudflare.steamstatic.com/store_item_assets/steam/apps/{originalGameAppId}",
                    $"https://cdn.cloudflare.steamstatic.com/steam/apps/{originalGameAppId}",
                    $"https://cdn.akamai.steamstatic.com/steam/apps/{originalGameAppId}"
                }
                : Array.Empty<string>();

            // 1. Portada (library_capsule -> 600x900 vertical poster)
            var verticalUrls = new List<string?>();
            if (!string.IsNullOrWhiteSpace(cmdCapsuleRel))
            {
                foreach (var cdn in cdns) verticalUrls.Add($"{cdn}/{cmdCapsuleRel}");
            }
            foreach (var cdn in cdns)
            {
                verticalUrls.Add($"{cdn}/library_600x900_2x.jpg");
                verticalUrls.Add($"{cdn}/library_600x900.jpg");
                verticalUrls.Add($"{cdn}/library_capsule_2x.jpg");
                verticalUrls.Add($"{cdn}/library_capsule.jpg");
            }
            if (!string.IsNullOrWhiteSpace(customCapsuleUrl)) verticalUrls.Add(customCapsuleUrl);

            var verticalData = await DownloadFirstAvailableAsset(verticalUrls).ConfigureAwait(false);
            await SaveAssetToGrid(verticalData, [
                $"{id32}p.jpg", $"{id32}_p.jpg", $"{id32}p.png", $"{id32}_p.png",
                $"{idSigned32}p.jpg", $"{idSigned32}_p.jpg", $"{idSigned32}p.png", $"{idSigned32}_p.png",
                $"{id64}p.jpg", $"{id64}_p.jpg", $"{id64}p.png", $"{id64}_p.png"
            ]).ConfigureAwait(false);

            // 2. Fondo (library_hero -> 1920x620 hero banner)
            var heroUrls = new List<string?>();
            if (!string.IsNullOrWhiteSpace(cmdHeroRel))
            {
                foreach (var cdn in cdns) heroUrls.Add($"{cdn}/{cmdHeroRel}");
            }
            foreach (var cdn in cdns)
            {
                heroUrls.Add($"{cdn}/library_hero_2x.jpg");
                heroUrls.Add($"{cdn}/library_hero.jpg");
            }

            var heroData = await DownloadFirstAvailableAsset(heroUrls).ConfigureAwait(false);
            await SaveAssetToGrid(heroData, [
                $"{id32}_hero.jpg", $"{id32}hero.jpg", $"{id32}_hero.png", $"{id32}hero.png",
                $"{idSigned32}_hero.jpg", $"{idSigned32}hero.jpg", $"{idSigned32}_hero.png", $"{idSigned32}hero.png",
                $"{id64}_hero.jpg", $"{id64}hero.jpg", $"{id64}_hero.png", $"{id64}hero.png"
            ]).ConfigureAwait(false);

            // 3. Logo (library_logo -> transparent logo.png)
            var logoUrls = new List<string?>();
            if (!string.IsNullOrWhiteSpace(cmdLogoRel))
            {
                foreach (var cdn in cdns) logoUrls.Add($"{cdn}/{cmdLogoRel}");
            }
            foreach (var cdn in cdns)
            {
                logoUrls.Add($"{cdn}/logo_2x.png");
                logoUrls.Add($"{cdn}/logo.png");
            }

            var logoData = await DownloadFirstAvailableAsset(logoUrls).ConfigureAwait(false);
            await SaveAssetToGrid(logoData, [
                $"{id32}_logo.png", $"{id32}logo.png", $"{id32}_logo.jpg", $"{id32}logo.jpg",
                $"{idSigned32}_logo.png", $"{idSigned32}logo.png", $"{idSigned32}_logo.jpg", $"{idSigned32}logo.jpg",
                $"{id64}_logo.png", $"{id64}logo.png", $"{id64}_logo.jpg", $"{id64}logo.jpg"
            ]).ConfigureAwait(false);

            // 4. Portada Ancha (header from Assets section)
            var headerUrls = new List<string?>();
            if (!string.IsNullOrWhiteSpace(cmdHeaderRel))
            {
                foreach (var cdn in cdns) headerUrls.Add($"{cdn}/{cmdHeaderRel}");
            }
            foreach (var cdn in cdns)
            {
                headerUrls.Add($"{cdn}/header_2x.jpg");
                headerUrls.Add($"{cdn}/header.jpg");
                headerUrls.Add($"{cdn}/library_header_2x.jpg");
                headerUrls.Add($"{cdn}/library_header.jpg");
            }
            if (!string.IsNullOrWhiteSpace(customHeaderUrl)) headerUrls.Add(customHeaderUrl);

            var headerData = await DownloadFirstAvailableAsset(headerUrls).ConfigureAwait(false);
            await SaveAssetToGrid(headerData, [
                $"{id32}.jpg", $"{id32}.png", $"{id32}_header.jpg", $"{id32}_grid.jpg",
                $"{idSigned32}.jpg", $"{idSigned32}.png", $"{idSigned32}_header.jpg", $"{idSigned32}_grid.jpg",
                $"{id64}.jpg", $"{id64}.png", $"{id64}_header.jpg", $"{id64}_grid.jpg"
            ]).ConfigureAwait(false);

            // 5. Ícono (clienticon -> .ico)
            string? resolvedIconPath = null;
            var iconUrls = new List<string?>();
            if (!string.IsNullOrWhiteSpace(cmdClientIcon))
            {
                iconUrls.Add($"https://cdn.cloudflare.steamstatic.com/steamcommunity/public/images/apps/{originalGameAppId}/{cmdClientIcon}.ico");
                iconUrls.Add($"https://cdn.akamai.steamstatic.com/steamcommunity/public/images/apps/{originalGameAppId}/{cmdClientIcon}.ico");
                iconUrls.Add($"https://steamcdn-a.akamaihd.net/steamcommunity/public/images/apps/{originalGameAppId}/{cmdClientIcon}.ico");
                iconUrls.Add($"https://shared.cloudflare.steamstatic.com/store_item_assets/steam/apps/{originalGameAppId}/{cmdClientIcon}.ico");
            }

            var iconData = await DownloadFirstAvailableAsset(iconUrls).ConfigureAwait(false);
            if (iconData != null && iconData.Length > 100)
            {
                await SaveAssetToGrid(iconData, [
                    $"{id32}_icon.ico", $"{id32}.ico",
                    $"{idSigned32}_icon.ico", $"{idSigned32}.ico",
                    $"{id64}_icon.ico", $"{id64}.ico"
                ]).ConfigureAwait(false);

                var primaryIcon = Path.Combine(gridDir, $"{id32}_icon.ico");
                if (File.Exists(primaryIcon))
                {
                    resolvedIconPath = primaryIcon;
                }
            }

            return resolvedIconPath;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Adds a shortcut for the game executable across all local Steam user profiles, along with downloaded artwork and icon.
    /// </summary>
    public static async Task<(bool Success, int UpdatedUsers, string Message)> AddSteamShortcutsAsync(
        string targetExePath,
        string shortcutName,
        uint originalGameAppId = 0,
        string? customHeaderUrl = null,
        string? customCapsuleUrl = null,
        string? iconPath = null,
        string? launchOptions = null,
        CancellationToken ct = default)
    {
        var steamPath = GetSteamPath();
        if (string.IsNullOrWhiteSpace(steamPath))
        {
            return (false, 0, "Steam installation could not be found.");
        }

        var userdataDir = Path.Combine(steamPath, "userdata");
        if (!Directory.Exists(userdataDir))
        {
            return (false, 0, "Steam userdata directory not found.");
        }

        var userFolders = Directory.GetDirectories(userdataDir)
            .Where(d => Path.GetFileName(d).All(char.IsDigit))
            .ToList();

        if (userFolders.Count == 0)
        {
            return (false, 0, "No Steam user profiles found in userdata.");
        }

        var startDir = Path.GetDirectoryName(targetExePath) ?? "";
        uint shortcutAppId32 = ComputeCrc32(targetExePath + shortcutName) | 0x80000000;
        int successCount = 0;

        foreach (var userFolder in userFolders)
        {
            var vdfPath = Path.Combine(userFolder, "config", "shortcuts.vdf");
            string? effectiveIconPath = iconPath;

            // 1. Download artwork & clienticon first if AppID is provided
            if (originalGameAppId > 0)
            {
                var downloadedIcon = await InstallSteamGridArtworkAsync(
                    userFolder,
                    shortcutAppId32,
                    originalGameAppId,
                    customHeaderUrl,
                    customCapsuleUrl,
                    ct).ConfigureAwait(false);

                if (!string.IsNullOrWhiteSpace(downloadedIcon) && File.Exists(downloadedIcon))
                {
                    effectiveIconPath = downloadedIcon;
                }
            }

            // 2. If no clienticon downloaded and no custom icon provided, use the target .exe path
            if (string.IsNullOrWhiteSpace(effectiveIconPath))
            {
                effectiveIconPath = targetExePath;
            }

            // 3. Add to shortcuts.vdf with effectiveIconPath
            if (AddSteamShortcutToUser(vdfPath, shortcutName, targetExePath, startDir, effectiveIconPath, launchOptions))
            {
                successCount++;
            }
        }

        if (successCount > 0)
        {
            return (true, successCount, $"Steam shortcut & artwork added to {successCount} user profile(s).");
        }

        return (false, 0, "Failed to update Steam shortcuts.");
    }

    /// <summary>
    /// Restarts the Steam client if installed.
    /// Closes any running Steam processes and launches steam.exe again.
    /// </summary>
    public static bool RestartSteam()
    {
        try
        {
            var steamPath = GetSteamPath();
            if (string.IsNullOrWhiteSpace(steamPath)) return false;

            var steamExe = Path.Combine(steamPath, "steam.exe");
            if (!File.Exists(steamExe)) return false;

            var steamProcs = Process.GetProcessesByName("steam");
            if (steamProcs.Length > 0)
            {
                try
                {
                    var shutdownPsi = new ProcessStartInfo
                    {
                        FileName = steamExe,
                        Arguments = "-shutdown",
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };
                    using var p = Process.Start(shutdownPsi);
                    p?.WaitForExit(3000);
                }
                catch { }

                foreach (var proc in Process.GetProcessesByName("steam"))
                {
                    try
                    {
                        proc.Kill();
                        proc.WaitForExit(2000);
                    }
                    catch { }
                }
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = steamExe,
                UseShellExecute = true
            });

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool ContainsSequence(byte[] source, byte[] pattern)
    {
        if (source.Length < pattern.Length || pattern.Length == 0) return false;
        for (int i = 0; i <= source.Length - pattern.Length; i++)
        {
            bool match = true;
            for (int j = 0; j < pattern.Length; j++)
            {
                if (source[i + j] != pattern[j])
                {
                    match = false;
                    break;
                }
            }
            if (match) return true;
        }
        return false;
    }

    private static int CountSequenceOccurrences(byte[] source, byte[] pattern)
    {
        if (source.Length < pattern.Length || pattern.Length == 0) return 0;
        int count = 0;
        for (int i = 0; i <= source.Length - pattern.Length; i++)
        {
            bool match = true;
            for (int j = 0; j < pattern.Length; j++)
            {
                if (source[i + j] != pattern[j])
                {
                    match = false;
                    break;
                }
            }
            if (match) count++;
        }
        return count;
    }

    /// <summary>
    /// Creates Windows shortcuts (.lnk) and/or Steam shortcut (.vdf + grid artwork) for a game executable.
    /// </summary>
    public static async Task<ShortcutCreationResult> CreateShortcutsAsync(
        string targetExePath,
        string shortcutName,
        bool createDesktop,
        bool createStartMenu,
        bool createSteam = false,
        uint originalGameAppId = 0,
        string? customHeaderUrl = null,
        string? customCapsuleUrl = null,
        string? arguments = null,
        string? iconPath = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(targetExePath) || !File.Exists(targetExePath))
        {
            return new ShortcutCreationResult(false, "The target executable file does not exist.", []);
        }

        if (!createDesktop && !createStartMenu && !createSteam)
        {
            return new ShortcutCreationResult(false, "Please select at least one shortcut destination (Desktop, Start Menu, or Steam).", []);
        }

        var safeName = PathHelper.SanitizeFolderName(string.IsNullOrWhiteSpace(shortcutName)
            ? Path.GetFileNameWithoutExtension(targetExePath)
            : shortcutName);

        var workingDir = Path.GetDirectoryName(targetExePath) ?? "";
        var created = new List<string>();
        var errors = new List<string>();

        if (createDesktop)
        {
            try
            {
                var desktopPath = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                if (string.IsNullOrWhiteSpace(desktopPath) || !Directory.Exists(desktopPath))
                {
                    desktopPath = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
                }

                if (!string.IsNullOrWhiteSpace(desktopPath))
                {
                    var shortcutFile = Path.Combine(desktopPath, $"{safeName}.lnk");
                    if (CreateSingleShortcut(shortcutFile, targetExePath, workingDir, safeName, arguments, iconPath))
                    {
                        created.Add("Desktop");
                    }
                    else
                    {
                        errors.Add("Desktop");
                    }
                }
            }
            catch (Exception ex)
            {
                errors.Add($"Desktop ({ex.Message})");
            }
        }

        if (createStartMenu)
        {
            try
            {
                var startMenuPrograms = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
                if (!string.IsNullOrWhiteSpace(startMenuPrograms))
                {
                    Directory.CreateDirectory(startMenuPrograms);
                    var shortcutFile = Path.Combine(startMenuPrograms, $"{safeName}.lnk");
                    if (CreateSingleShortcut(shortcutFile, targetExePath, workingDir, safeName, arguments, iconPath))
                    {
                        created.Add("Start Menu");
                    }
                    else
                    {
                        errors.Add("Start Menu");
                    }
                }
            }
            catch (Exception ex)
            {
                errors.Add($"Start Menu ({ex.Message})");
            }
        }

        if (createSteam)
        {
            try
            {
                var steamRes = await AddSteamShortcutsAsync(
                    targetExePath,
                    safeName,
                    originalGameAppId,
                    customHeaderUrl,
                    customCapsuleUrl,
                    iconPath,
                    arguments,
                    ct).ConfigureAwait(false);

                if (steamRes.Success)
                {
                    created.Add("Steam");
                }
                else
                {
                    errors.Add($"Steam ({steamRes.Message})");
                }
            }
            catch (Exception ex)
            {
                errors.Add($"Steam ({ex.Message})");
            }
        }

        if (created.Count > 0)
        {
            var msg = $"✅ Shortcut created successfully for: {string.Join(", ", created)}!";
            return new ShortcutCreationResult(true, msg, created);
        }

        return new ShortcutCreationResult(false, $"❌ Failed to create shortcuts: {string.Join(", ", errors)}", []);
    }

    /// <summary>
    /// Synchronous wrapper for backwards compatibility and unit tests.
    /// </summary>
    public static ShortcutCreationResult CreateShortcuts(
        string targetExePath,
        string shortcutName,
        bool createDesktop,
        bool createStartMenu,
        bool createSteam = false,
        string? arguments = null,
        string? iconPath = null)
    {
        return CreateShortcutsAsync(
            targetExePath,
            shortcutName,
            createDesktop,
            createStartMenu,
            createSteam,
            originalGameAppId: 0,
            arguments: arguments,
            iconPath: iconPath).GetAwaiter().GetResult();
    }

    /// <summary>
    /// Creates a single .lnk shortcut using Windows Script Host COM.
    /// </summary>
    public static bool CreateSingleShortcut(
        string shortcutFilePath,
        string targetPath,
        string workingDirectory,
        string description,
        string? arguments = null,
        string? iconPath = null)
    {
        try
        {
            if (!OperatingSystem.IsWindows())
                return false;

            var destinationDir = Path.GetDirectoryName(shortcutFilePath);
            if (!string.IsNullOrEmpty(destinationDir) && !Directory.Exists(destinationDir))
            {
                Directory.CreateDirectory(destinationDir);
            }

            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType is null) return false;

            dynamic shell = Activator.CreateInstance(shellType)!;
            dynamic shortcut = shell.CreateShortcut(shortcutFilePath);
            shortcut.TargetPath = targetPath;
            shortcut.WorkingDirectory = string.IsNullOrWhiteSpace(workingDirectory)
                ? Path.GetDirectoryName(targetPath) ?? ""
                : workingDirectory;

            if (!string.IsNullOrWhiteSpace(description))
                shortcut.Description = description;

            if (!string.IsNullOrWhiteSpace(arguments))
                shortcut.Arguments = arguments;

            if (!string.IsNullOrWhiteSpace(iconPath) && File.Exists(iconPath))
            {
                shortcut.IconLocation = $"{iconPath},0";
            }
            else if (File.Exists(targetPath))
            {
                shortcut.IconLocation = $"{targetPath},0";
            }

            shortcut.Save();
            return true;
        }
        catch
        {
            return false;
        }
    }
}
