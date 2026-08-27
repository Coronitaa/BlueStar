using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using BlueStar.Core.Helpers;
using BlueStar.Core.Interfaces;
using BlueStar.Core.Models;

namespace BlueStar.Infrastructure.Mods;

/// <summary>
/// Result of resolving the mod installation path for a game instance.
/// </summary>
public record ModPathResolution(
    string PrimaryDirectory,
    IReadOnlyList<string> ScanDirectories,
    string GameCategory,
    bool RequiresSpecialDeployment = false
);

/// <summary>
/// Universal resolver that discovers where Steam Workshop and local mods must be installed for any game.
/// Implements a 4-layer taxonomy:
///   1. Known game profiles (Tabletop Simulator, DST, L4D2, GMod, Portal 2, RimWorld, PZ, etc.)
///   2. Engine &amp; Directory Structure Heuristics (Source Engine, Unreal, Klei, Unity, Bethesda, Paradox)
///   3. Existing directory probing
///   4. Universal fallback + Steamworks ISteamUGC standard content mirroring
/// </summary>
public static class GameModPathResolver
{
    private static readonly EnumerationOptions ShallowSearch = new()
    {
        MaxRecursionDepth = 2,
        RecurseSubdirectories = true,
        IgnoreInaccessible = true
    };

    /// <summary>
    /// Resolves the optimal primary mod directory and all secondary scan directories for a game instance.
    /// </summary>
    public static ModPathResolution ResolveModPaths(GameInstance instance)
    {
        if (string.IsNullOrWhiteSpace(instance.InstallPath))
        {
            return new ModPathResolution(string.Empty, Array.Empty<string>(), "Generic");
        }

        var installPath = instance.InstallPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var scanDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // ═════════════════════════════════════════════════════════════════════
        // CAPA 1: Perfiles de Juegos Conocidos por AppID y Nombre
        // ═════════════════════════════════════════════════════════════════════

        // 1. Tabletop Simulator (AppId: 286160)
        if (instance.AppId == 286160 || instance.Name?.Contains("Tabletop Simulator", StringComparison.OrdinalIgnoreCase) == true)
        {
            var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            var ttsDocsWorkshop = Path.Combine(docs, "My Games", "Tabletop Simulator", "Mods", "Workshop");
            var ttsLocalMods = Path.Combine(installPath, "Mods");

            scanDirs.Add(ttsDocsWorkshop);
            if (Directory.Exists(ttsLocalMods)) scanDirs.Add(ttsLocalMods);

            var steamContent = GetSteamWorkshopContentPath(installPath, 286160);
            if (!string.IsNullOrEmpty(steamContent) && Directory.Exists(steamContent)) scanDirs.Add(steamContent);

            return new ModPathResolution(
                PrimaryDirectory: ttsDocsWorkshop,
                ScanDirectories: scanDirs.ToList().AsReadOnly(),
                GameCategory: "TabletopSimulator",
                RequiresSpecialDeployment: true
            );
        }

        // 2. Don't Starve Together (AppId: 322330) &amp; Don't Starve (AppId: 214950)
        if (instance.AppId == 322330 || instance.AppId == 214950 ||
            instance.Name?.Contains("Don't Starve", StringComparison.OrdinalIgnoreCase) == true)
        {
            var dstMods = Path.Combine(installPath, "mods");
            scanDirs.Add(dstMods);

            return new ModPathResolution(
                PrimaryDirectory: dstMods,
                ScanDirectories: scanDirs.ToList().AsReadOnly(),
                GameCategory: "Klei",
                RequiresSpecialDeployment: true
            );
        }

        // 3. Left 4 Dead 2 (AppId: 550) &amp; Left 4 Dead (AppId: 500)
        if (instance.AppId == 550 || instance.AppId == 500 ||
            instance.Name?.Contains("Left 4 Dead", StringComparison.OrdinalIgnoreCase) == true)
        {
            var l4d2Dir = Path.Combine(installPath, "left4dead2");
            var targetAddons = Directory.Exists(l4d2Dir)
                ? Path.Combine(l4d2Dir, "addons", "workshop")
                : Path.Combine(installPath, "addons", "workshop");

            var normalAddons = Directory.Exists(l4d2Dir)
                ? Path.Combine(l4d2Dir, "addons")
                : Path.Combine(installPath, "addons");

            scanDirs.Add(targetAddons);
            if (Directory.Exists(normalAddons)) scanDirs.Add(normalAddons);

            return new ModPathResolution(
                PrimaryDirectory: targetAddons,
                ScanDirectories: scanDirs.ToList().AsReadOnly(),
                GameCategory: "SourceEngine",
                RequiresSpecialDeployment: true
            );
        }

        // 4. Garry's Mod (AppId: 4000)
        if (instance.AppId == 4000 || instance.Name?.Contains("Garry's Mod", StringComparison.OrdinalIgnoreCase) == true)
        {
            var gmodAddons = Path.Combine(installPath, "garrysmod", "addons");
            scanDirs.Add(gmodAddons);

            return new ModPathResolution(
                PrimaryDirectory: gmodAddons,
                ScanDirectories: scanDirs.ToList().AsReadOnly(),
                GameCategory: "SourceEngine",
                RequiresSpecialDeployment: true
            );
        }

        // 5. Portal 2 (AppId: 620)
        if (instance.AppId == 620 || instance.Name?.Contains("Portal 2", StringComparison.OrdinalIgnoreCase) == true)
        {
            var portalAddons = Path.Combine(installPath, "portal2", "addons");
            scanDirs.Add(portalAddons);

            return new ModPathResolution(
                PrimaryDirectory: portalAddons,
                ScanDirectories: scanDirs.ToList().AsReadOnly(),
                GameCategory: "SourceEngine",
                RequiresSpecialDeployment: true
            );
        }

        // 6. RimWorld (AppId: 294100)
        if (instance.AppId == 294100 || instance.Name?.Contains("RimWorld", StringComparison.OrdinalIgnoreCase) == true)
        {
            var rimMods = Path.Combine(installPath, "Mods");
            scanDirs.Add(rimMods);

            return new ModPathResolution(
                PrimaryDirectory: rimMods,
                ScanDirectories: scanDirs.ToList().AsReadOnly(),
                GameCategory: "RimWorld"
            );
        }

        // 7. Project Zomboid (AppId: 108600)
        if (instance.AppId == 108600 || instance.Name?.Contains("Project Zomboid", StringComparison.OrdinalIgnoreCase) == true)
        {
            var userHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var pzUserMods = Path.Combine(userHome, "Zomboid", "mods");
            var pzLocalMods = Path.Combine(installPath, "mods");

            scanDirs.Add(pzUserMods);
            if (Directory.Exists(pzLocalMods)) scanDirs.Add(pzLocalMods);

            return new ModPathResolution(
                PrimaryDirectory: pzUserMods,
                ScanDirectories: scanDirs.ToList().AsReadOnly(),
                GameCategory: "ProjectZomboid"
            );
        }

        // 8. Cities: Skylines (AppId: 255710)
        if (instance.AppId == 255710 || instance.Name?.Contains("Cities: Skylines", StringComparison.OrdinalIgnoreCase) == true)
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var csMods = Path.Combine(localAppData, "Colossal Order", "Cities_Skylines", "Addons", "Mods");
            var csFilesMods = Path.Combine(installPath, "Files", "Mods");

            scanDirs.Add(csMods);
            if (Directory.Exists(csFilesMods)) scanDirs.Add(csFilesMods);

            return new ModPathResolution(
                PrimaryDirectory: csMods,
                ScanDirectories: scanDirs.ToList().AsReadOnly(),
                GameCategory: "CitiesSkylines"
            );
        }

        // 9. People Playground (AppId: 1118200)
        if (instance.AppId == 1118200 || instance.Name?.Contains("People Playground", StringComparison.OrdinalIgnoreCase) == true)
        {
            var ppgMods = Path.Combine(installPath, "Mods");
            scanDirs.Add(ppgMods);

            return new ModPathResolution(
                PrimaryDirectory: ppgMods,
                ScanDirectories: scanDirs.ToList().AsReadOnly(),
                GameCategory: "Generic"
            );
        }

        // 10. Teardown (AppId: 1167630)
        if (instance.AppId == 1167630 || instance.Name?.Contains("Teardown", StringComparison.OrdinalIgnoreCase) == true)
        {
            var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            var tdDocsMods = Path.Combine(docs, "Teardown", "mods");
            var tdLocalMods = Path.Combine(installPath, "mods");

            scanDirs.Add(tdDocsMods);
            if (Directory.Exists(tdLocalMods)) scanDirs.Add(tdLocalMods);

            return new ModPathResolution(
                PrimaryDirectory: tdDocsMods,
                ScanDirectories: scanDirs.ToList().AsReadOnly(),
                GameCategory: "Teardown"
            );
        }

        // 11. Blade &amp; Sorcery (AppId: 629730)
        if (instance.AppId == 629730 || instance.Name?.Contains("Blade and Sorcery", StringComparison.OrdinalIgnoreCase) == true || instance.Name?.Contains("Blade &amp; Sorcery", StringComparison.OrdinalIgnoreCase) == true)
        {
            var bsMods = Path.Combine(installPath, "BladeAndSorcery_Data", "StreamingAssets", "Mods");
            scanDirs.Add(bsMods);

            return new ModPathResolution(
                PrimaryDirectory: bsMods,
                ScanDirectories: scanDirs.ToList().AsReadOnly(),
                GameCategory: "UnityStreamingAssets"
            );
        }

        // 12. Oxygen Not Included (AppId: 457140)
        if (instance.AppId == 457140 || instance.Name?.Contains("Oxygen Not Included", StringComparison.OrdinalIgnoreCase) == true)
        {
            var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            var oniMods = Path.Combine(docs, "Klei", "OxygenNotIncluded", "mods", "Steam");
            var oniLocalMods = Path.Combine(installPath, "mods");

            scanDirs.Add(oniMods);
            if (Directory.Exists(oniLocalMods)) scanDirs.Add(oniLocalMods);

            return new ModPathResolution(
                PrimaryDirectory: oniMods,
                ScanDirectories: scanDirs.ToList().AsReadOnly(),
                GameCategory: "Klei"
            );
        }

        // 13. Paradox Games (Stellaris 281990, HOI4 394360, CK3 1158310, EU4 236850)
        if (instance.AppId is 281990 or 394360 or 1158310 or 236850 ||
            File.Exists(Path.Combine(installPath, "launcher-settings.json")) ||
            Directory.Exists(Path.Combine(installPath, "pdx_launcher")))
        {
            var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            var gameFolder = !string.IsNullOrWhiteSpace(instance.Name) ? instance.Name : "Stellaris";
            var pdxDocsMod = Path.Combine(docs, "Paradox Interactive", gameFolder, "mod");
            var pdxLocalMod = Path.Combine(installPath, "mod");

            scanDirs.Add(pdxDocsMod);
            if (Directory.Exists(pdxLocalMod)) scanDirs.Add(pdxLocalMod);

            return new ModPathResolution(
                PrimaryDirectory: pdxDocsMod,
                ScanDirectories: scanDirs.ToList().AsReadOnly(),
                GameCategory: "Paradox"
            );
        }

        // ═════════════════════════════════════════════════════════════════════
        // CAPA 2: Detección Heurística por Motor y Estructura de Archivos
        // ═════════════════════════════════════════════════════════════════════

        // A. Source Engine (gameinfo.txt o subcarpeta con addons/)
        try
        {
            var gameInfoFiles = Directory.GetFiles(installPath, "gameinfo.txt", ShallowSearch);
            if (gameInfoFiles.Length > 0)
            {
                var sourceSubDir = Path.GetDirectoryName(gameInfoFiles[0]) ?? installPath;
                var sourceAddonsWorkshop = Path.Combine(sourceSubDir, "addons", "workshop");
                var sourceAddons = Path.Combine(sourceSubDir, "addons");
                var sourceCustom = Path.Combine(sourceSubDir, "custom");

                scanDirs.Add(sourceAddonsWorkshop);
                if (Directory.Exists(sourceAddons)) scanDirs.Add(sourceAddons);
                if (Directory.Exists(sourceCustom)) scanDirs.Add(sourceCustom);

                return new ModPathResolution(
                    PrimaryDirectory: sourceAddonsWorkshop,
                    ScanDirectories: scanDirs.ToList().AsReadOnly(),
                    GameCategory: "SourceEngine",
                    RequiresSpecialDeployment: true
                );
            }
        }
        catch { }

        // B. Unreal Engine (Content/Paks/~mods)
        if (instance.Engine?.Type == EngineType.UnrealEngine ||
            Directory.Exists(Path.Combine(installPath, "Engine")))
        {
            try
            {
                var paksDirs = Directory.GetDirectories(installPath, "Paks", SearchOption.AllDirectories);
                if (paksDirs.Length > 0)
                {
                    var tildeMods = Path.Combine(paksDirs[0], "~mods");
                    var modsFolder = Path.Combine(paksDirs[0], "Mods");
                    scanDirs.Add(tildeMods);
                    if (Directory.Exists(modsFolder)) scanDirs.Add(modsFolder);

                    return new ModPathResolution(
                        PrimaryDirectory: tildeMods,
                        ScanDirectories: scanDirs.ToList().AsReadOnly(),
                        GameCategory: "UnrealPaks"
                    );
                }
            }
            catch { }
        }

        // C. Klei Engine (modinfo.lua)
        try
        {
            var hasModInfo = Directory.GetFiles(installPath, "modinfo.lua", ShallowSearch).Length > 0;
            if (hasModInfo)
            {
                var kleiMods = Path.Combine(installPath, "mods");
                scanDirs.Add(kleiMods);

                return new ModPathResolution(
                    PrimaryDirectory: kleiMods,
                    ScanDirectories: scanDirs.ToList().AsReadOnly(),
                    GameCategory: "Klei",
                    RequiresSpecialDeployment: true
                );
            }
        }
        catch { }

        // D. Unity Engine (BepInEx/plugins, StreamingAssets/Mods, *_Data/Mods)
        if (instance.Engine?.Type == EngineType.Unity)
        {
            var bepDir = Path.Combine(installPath, "BepInEx");
            if (Directory.Exists(bepDir))
            {
                var bepPlugins = Path.Combine(bepDir, "plugins");
                scanDirs.Add(bepPlugins);

                return new ModPathResolution(
                    PrimaryDirectory: bepPlugins,
                    ScanDirectories: scanDirs.ToList().AsReadOnly(),
                    GameCategory: "UnityBepInEx"
                );
            }

            try
            {
                var streamingAssets = Directory.GetDirectories(installPath, "StreamingAssets", SearchOption.AllDirectories);
                if (streamingAssets.Length > 0)
                {
                    var saMods = Path.Combine(streamingAssets[0], "Mods");
                    if (Directory.Exists(saMods))
                    {
                        scanDirs.Add(saMods);
                        return new ModPathResolution(
                            PrimaryDirectory: saMods,
                            ScanDirectories: scanDirs.ToList().AsReadOnly(),
                            GameCategory: "UnityStreamingAssets"
                        );
                    }
                }
            }
            catch { }
        }

        // E. Bethesda / Creation Engine (Data/)
        try
        {
            var dataDir = Path.Combine(installPath, "Data");
            if (Directory.Exists(dataDir) &&
                (Directory.GetFiles(dataDir, "*.esm").Length > 0 || Directory.GetFiles(dataDir, "*.esp").Length > 0))
            {
                scanDirs.Add(dataDir);
                return new ModPathResolution(
                    PrimaryDirectory: dataDir,
                    ScanDirectories: scanDirs.ToList().AsReadOnly(),
                    GameCategory: "BethesdaData"
                );
            }
        }
        catch { }

        // ═════════════════════════════════════════════════════════════════════
        // CAPA 3: Inspección de Carpetas Existentes
        // ═════════════════════════════════════════════════════════════════════
        var candidateNames = new[] { "Mods", "mods", "Addons", "addons", "Plugins", "plugins" };
        foreach (var cName in candidateNames)
        {
            var p = Path.Combine(installPath, cName);
            if (Directory.Exists(p))
            {
                scanDirs.Add(p);
                return new ModPathResolution(
                    PrimaryDirectory: p,
                    ScanDirectories: scanDirs.ToList().AsReadOnly(),
                    GameCategory: "Generic"
                );
            }
        }

        // ═════════════════════════════════════════════════════════════════════
        // CAPA 4: Fallback Estándar y Soporte Steamworks ISteamUGC
        // ═════════════════════════════════════════════════════════════════════
        var defaultModsDir = Path.Combine(installPath, "mods");
        scanDirs.Add(defaultModsDir);

        var steamContentFallback = GetSteamWorkshopContentPath(installPath, instance.AppId);
        if (!string.IsNullOrEmpty(steamContentFallback) && Directory.Exists(steamContentFallback))
        {
            scanDirs.Add(steamContentFallback);
        }

        return new ModPathResolution(
            PrimaryDirectory: defaultModsDir,
            ScanDirectories: scanDirs.ToList().AsReadOnly(),
            GameCategory: "Generic"
        );
    }

    /// <summary>
    /// Gets the target directory where a specific Steam Workshop item should be installed.
    /// </summary>
    public static string GetItemTargetFolder(GameInstance instance, ulong publishedFileId, string? title = null)
    {
        var resolution = ResolveModPaths(instance);
        var baseDir = resolution.PrimaryDirectory;
        var safeTitle = PathHelper.SanitizeFolderName(title ?? $"WorkshopMod_{publishedFileId}");

        return resolution.GameCategory switch
        {
            "SourceEngine" => baseDir, // Source engine places files directly in addons/workshop/
            "TabletopSimulator" => baseDir, // Tabletop Simulator places JSON files directly in Mods/Workshop/
            "Klei" => Path.Combine(baseDir, $"workshop-{publishedFileId}"), // DST requires prefix workshop-<id>
            _ => Path.Combine(baseDir, safeTitle)
        };
    }

    /// <summary>
    /// Processes and adapts downloaded Workshop files for the specific game format.
    /// </summary>
    public static void AdaptAndDeployWorkshopMod(
        GameInstance instance,
        ulong publishedFileId,
        string stagingFolder,
        string targetFolder,
        WorkshopItemInfo? details)
    {
        var resolution = ResolveModPaths(instance);
        var safeTitle = details != null ? PathHelper.SanitizeFolderName(details.Title) : null;
        if (string.IsNullOrWhiteSpace(safeTitle)) safeTitle = $"WorkshopMod_{publishedFileId}";

        Directory.CreateDirectory(targetFolder);

        switch (resolution.GameCategory)
        {
            case "SourceEngine":
                DeploySourceEngineMod(stagingFolder, targetFolder, publishedFileId, safeTitle);
                break;

            case "TabletopSimulator":
                DeployTabletopSimulatorMod(stagingFolder, targetFolder, publishedFileId, safeTitle);
                break;

            case "Klei":
                DeployKleiMod(instance, stagingFolder, targetFolder, publishedFileId);
                break;

            default:
                DeployGenericMod(stagingFolder, targetFolder);
                break;
        }

        // Always write workshop_info.json in the target directory if details are available
        if (details != null)
        {
            try
            {
                var metaFile = resolution.GameCategory is "SourceEngine" or "TabletopSimulator"
                    ? Path.Combine(targetFolder, $"{publishedFileId}_info.json")
                    : Path.Combine(targetFolder, "workshop_info.json");

                var json = JsonSerializer.Serialize(details, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(metaFile, json);
            }
            catch { }
        }

        // Mirror to steamapps/workshop/content/<AppId>/<PublishedFileId> for ISteamUGC / Goldberg emulator compatibility
        try
        {
            MirrorToSteamUgcFolder(instance, publishedFileId, stagingFolder, details);
        }
        catch { }
    }

    private static void DeploySourceEngineMod(string stagingFolder, string targetFolder, ulong publishedFileId, string safeTitle)
    {
        // 1. Check for any .vpk files in staging folder or subdirectories
        var vpkFiles = Directory.GetFiles(stagingFolder, "*.vpk", SearchOption.AllDirectories);
        if (vpkFiles.Length > 0)
        {
            foreach (var vpk in vpkFiles)
            {
                var destName = $"{publishedFileId}.vpk";
                var destPath = Path.Combine(targetFolder, destName);
                File.Copy(vpk, destPath, overwrite: true);
            }
            return;
        }

        // 2. Check for zip files that may contain a .vpk
        var zipFiles = Directory.GetFiles(stagingFolder, "*.zip", SearchOption.AllDirectories);
        foreach (var zip in zipFiles)
        {
            try
            {
                using var archive = ZipFile.OpenRead(zip);
                foreach (var entry in archive.Entries)
                {
                    if (entry.Name.EndsWith(".vpk", StringComparison.OrdinalIgnoreCase))
                    {
                        var destPath = Path.Combine(targetFolder, $"{publishedFileId}.vpk");
                        entry.ExtractToFile(destPath, overwrite: true);
                        return;
                    }
                }
            }
            catch { }
        }

        // 3. Fallback: Copy all files to target folder
        CopyDirectoryRecursive(stagingFolder, targetFolder);
    }

    private static void DeployTabletopSimulatorMod(string stagingFolder, string targetFolder, ulong publishedFileId, string safeTitle)
    {
        var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var ttsRoot = Path.Combine(docs, "My Games", "Tabletop Simulator", "Mods");
        var ttsWorkshop = Path.Combine(ttsRoot, "Workshop");
        var ttsImages = Path.Combine(ttsRoot, "Images");
        var ttsModels = Path.Combine(ttsRoot, "Models");

        Directory.CreateDirectory(ttsWorkshop);
        Directory.CreateDirectory(ttsImages);
        Directory.CreateDirectory(ttsModels);

        var files = Directory.GetFiles(stagingFolder, "*", SearchOption.AllDirectories);
        foreach (var file in files)
        {
            var fileName = Path.GetFileName(file);
            var ext = Path.GetExtension(file).ToLowerInvariant();

            if (ext == ".json" || fileName.Equals("WorkshopUpload", StringComparison.OrdinalIgnoreCase) || !Path.HasExtension(file))
            {
                try
                {
                    var text = File.ReadAllText(file).TrimStart();
                    if (text.StartsWith('{') || text.StartsWith('['))
                    {
                        var destJson = Path.Combine(ttsWorkshop, $"{publishedFileId}.json");
                        File.Copy(file, destJson, overwrite: true);

                        var targetDest = Path.Combine(targetFolder, $"{publishedFileId}.json");
                        File.Copy(file, targetDest, overwrite: true);
                        continue;
                    }
                }
                catch { }
            }

            if (ext is ".png" or ".jpg" or ".jpeg")
            {
                try { File.Copy(file, Path.Combine(ttsImages, fileName), overwrite: true); } catch { }
            }
            else if (ext is ".obj" or ".assetbundle" or ".unity3d")
            {
                try { File.Copy(file, Path.Combine(ttsModels, fileName), overwrite: true); } catch { }
            }

            try { File.Copy(file, Path.Combine(targetFolder, fileName), overwrite: true); } catch { }
        }
    }

    private static void DeployKleiMod(GameInstance instance, string stagingFolder, string targetFolder, ulong publishedFileId)
    {
        // Don't Starve / DST mods require modinfo.lua to be placed directly in the root of targetFolder (mods/workshop-<PublishedFileId>/)
        string modSourceRoot = stagingFolder;
        var modInfoFiles = Directory.GetFiles(stagingFolder, "modinfo.lua", SearchOption.AllDirectories);
        if (modInfoFiles.Length > 0)
        {
            modSourceRoot = Path.GetDirectoryName(modInfoFiles[0]) ?? stagingFolder;
        }

        Directory.CreateDirectory(targetFolder);
        CopyDirectoryRecursive(modSourceRoot, targetFolder);

        // Also check if instance install path has data/mods/ and mirror if present
        if (!string.IsNullOrWhiteSpace(instance.InstallPath) && Directory.Exists(instance.InstallPath))
        {
            var dataMods = Path.Combine(instance.InstallPath, "data", "mods");
            if (Directory.Exists(dataMods))
            {
                var dataTarget = Path.Combine(dataMods, $"workshop-{publishedFileId}");
                Directory.CreateDirectory(dataTarget);
                CopyDirectoryRecursive(modSourceRoot, dataTarget);
            }

            // Check mods/modsettings.lua to ensure ForceEnableMod is configured so Don't Starve enables the mod automatically
            var modSettingsFile = Path.Combine(instance.InstallPath, "mods", "modsettings.lua");
            if (File.Exists(modSettingsFile))
            {
                try
                {
                    var lines = File.ReadAllText(modSettingsFile);
                    var modIdStr = $"workshop-{publishedFileId}";
                    if (!lines.Contains(modIdStr, StringComparison.OrdinalIgnoreCase))
                    {
                        var appendStr = $"\nForceEnableMod(\"{modIdStr}\")\n";
                        File.AppendAllText(modSettingsFile, appendStr);
                    }
                }
                catch { }
            }
        }
    }

    private static void DeployGenericMod(string stagingFolder, string targetFolder)
    {
        CopyDirectoryRecursive(stagingFolder, targetFolder);
    }

    private static void MirrorToSteamUgcFolder(GameInstance instance, ulong publishedFileId, string stagingFolder, WorkshopItemInfo? details)
    {
        if (instance.AppId == 0 || string.IsNullOrWhiteSpace(instance.InstallPath)) return;

        var contentPath = GetSteamWorkshopContentPath(instance.InstallPath, instance.AppId);
        if (string.IsNullOrEmpty(contentPath))
        {
            contentPath = Path.Combine(instance.InstallPath, "steamapps", "workshop", "content", instance.AppId.ToString());
        }

        var itemDir = Path.Combine(contentPath, publishedFileId.ToString());
        Directory.CreateDirectory(itemDir);
        CopyDirectoryRecursive(stagingFolder, itemDir);

        if (details != null)
        {
            var metaFile = Path.Combine(itemDir, "workshop_info.json");
            var json = JsonSerializer.Serialize(details, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(metaFile, json);
        }
    }

    private static string? GetSteamWorkshopContentPath(string installPath, uint appId)
    {
        if (string.IsNullOrWhiteSpace(installPath) || appId == 0) return null;

        try
        {
            // If inside .../steamapps/common/<Game>, standard is .../steamapps/workshop/content/<AppId>
            var norm = Path.GetFullPath(installPath);
            var commonIdx = norm.IndexOf(Path.Combine("steamapps", "common"), StringComparison.OrdinalIgnoreCase);
            if (commonIdx >= 0)
            {
                var steamapps = norm.Substring(0, commonIdx + 9);
                return Path.Combine(steamapps, "workshop", "content", appId.ToString());
            }
        }
        catch { }

        return null;
    }

    private static void CopyDirectoryRecursive(string sourceDir, string targetDir)
    {
        Directory.CreateDirectory(targetDir);

        foreach (var file in Directory.GetFiles(sourceDir))
        {
            var dest = Path.Combine(targetDir, Path.GetFileName(file));
            File.Copy(file, dest, overwrite: true);
        }

        foreach (var sub in Directory.GetDirectories(sourceDir))
        {
            var dirName = Path.GetFileName(sub);
            if (dirName.Equals(".DepotDownloader", StringComparison.OrdinalIgnoreCase)) continue;
            var destSub = Path.Combine(targetDir, dirName);
            CopyDirectoryRecursive(sub, destSub);
        }
    }
}
