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
        var gameRoot = FindGameRoot(installPath, instance.ExecutablePath);
        var scanDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // ═════════════════════════════════════════════════════════════════════
        // CAPA 1: Perfiles de Juegos Conocidos por AppID y Nombre
        // ═════════════════════════════════════════════════════════════════════

        // 1. Tabletop Simulator (AppId: 286160)
        if (instance.AppId == 286160 || instance.Name?.Contains("Tabletop Simulator", StringComparison.OrdinalIgnoreCase) == true)
        {
            var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            var ttsDocsWorkshop = Path.Combine(docs, "My Games", "Tabletop Simulator", "Mods", "Workshop");
            var ttsLocalMods = Path.Combine(gameRoot, "Mods");

            scanDirs.Add(ttsDocsWorkshop);
            if (Directory.Exists(ttsLocalMods)) scanDirs.Add(ttsLocalMods);

            var steamContent = GetSteamWorkshopContentPath(gameRoot, 286160);
            if (!string.IsNullOrEmpty(steamContent) && Directory.Exists(steamContent)) scanDirs.Add(steamContent);

            return new ModPathResolution(
                PrimaryDirectory: ttsDocsWorkshop,
                ScanDirectories: scanDirs.ToList().AsReadOnly(),
                GameCategory: "TabletopSimulator",
                RequiresSpecialDeployment: true
            );
        }

        // 2. Don't Starve Together (AppId: 322330) & Don't Starve (AppId: 214950) & Oxygen Not Included (457140)
        if (instance.AppId == 322330 || instance.AppId == 214950 || instance.AppId == 457140 ||
            instance.Name?.Contains("Don't Starve", StringComparison.OrdinalIgnoreCase) == true ||
            instance.Name?.Contains("Dont Starve", StringComparison.OrdinalIgnoreCase) == true ||
            instance.Engine?.Type == EngineType.Klei)
        {
            var dstMods = Path.Combine(gameRoot, "mods");
            var dataMods = Path.Combine(gameRoot, "data", "mods");
            var directInstallMods = Path.Combine(installPath, "mods");

            scanDirs.Add(dstMods);
            if (Directory.Exists(dataMods)) scanDirs.Add(dataMods);
            if (Directory.Exists(directInstallMods)) scanDirs.Add(directInstallMods);

            // Dynamically discover Klei Documents directories
            foreach (var docDir in FindKleiUserModDirectories())
            {
                if (Directory.Exists(docDir)) scanDirs.Add(docDir);
            }

            var steamContent = GetSteamWorkshopContentPath(gameRoot, instance.AppId > 0 ? instance.AppId : 214950);
            if (!string.IsNullOrEmpty(steamContent) && Directory.Exists(steamContent)) scanDirs.Add(steamContent);

            var emulatorMods = Path.Combine(gameRoot, "steam_settings", "mods");
            if (Directory.Exists(emulatorMods)) scanDirs.Add(emulatorMods);

            return new ModPathResolution(
                PrimaryDirectory: dstMods,
                ScanDirectories: scanDirs.ToList().AsReadOnly(),
                GameCategory: "Klei",
                RequiresSpecialDeployment: true
            );
        }

        // 3. Left 4 Dead 2 (AppId: 550) & Left 4 Dead (AppId: 500)
        if (instance.AppId == 550 || instance.AppId == 500 ||
            instance.Name?.Contains("Left 4 Dead", StringComparison.OrdinalIgnoreCase) == true)
        {
            var l4d2Dir = Path.Combine(gameRoot, "left4dead2");
            var targetAddons = Directory.Exists(l4d2Dir)
                ? Path.Combine(l4d2Dir, "addons", "workshop")
                : Path.Combine(gameRoot, "addons", "workshop");

            var normalAddons = Directory.Exists(l4d2Dir)
                ? Path.Combine(l4d2Dir, "addons")
                : Path.Combine(gameRoot, "addons");

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
                DeployKleiMod(instance, stagingFolder, targetFolder, publishedFileId, details?.Title);
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

        // Mirror to steamapps/workshop/content/<AppId>/<PublishedFileId> and emulator steam_settings/
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

    private static void DeployKleiMod(GameInstance instance, string stagingFolder, string targetFolder, ulong publishedFileId, string? title)
    {
        var gameRoot = FindGameRoot(instance.InstallPath, instance.ExecutablePath);

        // Find the root of the mod files in staging (containing modinfo.lua)
        string modSourceRoot = stagingFolder;
        var modInfoFiles = Directory.GetFiles(stagingFolder, "modinfo.lua", SearchOption.AllDirectories);
        if (modInfoFiles.Length > 0)
        {
            modSourceRoot = Path.GetDirectoryName(modInfoFiles[0]) ?? stagingFolder;
        }

        // 1. Primary destination: GameRoot/mods/workshop-<PublishedFileId>/
        var primaryTarget = Path.Combine(gameRoot, "mods", $"workshop-{publishedFileId}");
        Directory.CreateDirectory(primaryTarget);
        CopyDirectoryRecursive(modSourceRoot, primaryTarget);
        SanitizeLuaFilesAndModInfo(primaryTarget, publishedFileId, title);

        // 2. Also deploy by sanitized title if title is available (e.g. mods/CombinedStatus/)
        var safeTitle = !string.IsNullOrWhiteSpace(title) ? PathHelper.SanitizeFolderName(title) : null;
        if (!string.IsNullOrWhiteSpace(safeTitle) && !safeTitle.Equals($"workshop-{publishedFileId}", StringComparison.OrdinalIgnoreCase))
        {
            var titleTarget = Path.Combine(gameRoot, "mods", safeTitle);
            Directory.CreateDirectory(titleTarget);
            CopyDirectoryRecursive(modSourceRoot, titleTarget);
            SanitizeLuaFilesAndModInfo(titleTarget, publishedFileId, title);
        }

        // 3. Deploy to data/mods/workshop-<PublishedFileId>/ if data/ folder exists
        var dataDir = Path.Combine(gameRoot, "data");
        if (Directory.Exists(dataDir))
        {
            var dataModsTarget = Path.Combine(dataDir, "mods", $"workshop-{publishedFileId}");
            Directory.CreateDirectory(dataModsTarget);
            CopyDirectoryRecursive(modSourceRoot, dataModsTarget);
            SanitizeLuaFilesAndModInfo(dataModsTarget, publishedFileId, title);
        }

        // 4. Deploy to installPath/mods if installPath is distinct from gameRoot
        if (!string.IsNullOrWhiteSpace(instance.InstallPath) && !string.Equals(instance.InstallPath, gameRoot, StringComparison.OrdinalIgnoreCase))
        {
            var installModsTarget = Path.Combine(instance.InstallPath, "mods", $"workshop-{publishedFileId}");
            Directory.CreateDirectory(installModsTarget);
            CopyDirectoryRecursive(modSourceRoot, installModsTarget);
            SanitizeLuaFilesAndModInfo(installModsTarget, publishedFileId, title);
        }

        // 5. Deploy to Klei User Documents directories
        foreach (var userModDir in FindKleiUserModDirectories())
        {
            try
            {
                var userTarget = Path.Combine(userModDir, $"workshop-{publishedFileId}");
                Directory.CreateDirectory(userTarget);
                CopyDirectoryRecursive(modSourceRoot, userTarget);
                SanitizeLuaFilesAndModInfo(userTarget, publishedFileId, title);
            }
            catch { }
        }

        // 6. Force-enable in modsettings.lua across all candidate mods folders
        var candidateModsDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Path.Combine(gameRoot, "mods"),
            Path.Combine(gameRoot, "data", "mods"),
            Path.Combine(instance.InstallPath ?? string.Empty, "mods")
        };

        foreach (var mDir in candidateModsDirs)
        {
            if (string.IsNullOrWhiteSpace(mDir) || !Directory.Exists(mDir)) continue;

            var modSettingsFile = Path.Combine(mDir, "modsettings.lua");
            try
            {
                var content = File.Exists(modSettingsFile) ? File.ReadAllText(modSettingsFile) : "-- Mod Settings\n";
                var modId = $"workshop-{publishedFileId}";

                var linesToAppend = new List<string>();
                if (!content.Contains(modId, StringComparison.OrdinalIgnoreCase))
                {
                    linesToAppend.Add($"ForceEnableMod(\"{modId}\")");
                    linesToAppend.Add($"EnableMod(\"{modId}\")");
                }
                if (!string.IsNullOrWhiteSpace(safeTitle) && !content.Contains(safeTitle, StringComparison.OrdinalIgnoreCase))
                {
                    linesToAppend.Add($"ForceEnableMod(\"{safeTitle}\")");
                    linesToAppend.Add($"EnableMod(\"{safeTitle}\")");
                }

                if (linesToAppend.Count > 0)
                {
                    var appendStr = "\n" + string.Join("\n", linesToAppend) + "\n";
                    File.AppendAllText(modSettingsFile, appendStr);
                }
            }
            catch { }
        }
    }

    private static void DeployGenericMod(string stagingFolder, string targetFolder)
    {
        CopyDirectoryRecursive(stagingFolder, targetFolder);
    }

    private static void MirrorToSteamUgcFolder(GameInstance instance, ulong publishedFileId, string stagingFolder, WorkshopItemInfo? details)
    {
        if (instance.AppId == 0 || string.IsNullOrWhiteSpace(instance.InstallPath)) return;

        var gameRoot = FindGameRoot(instance.InstallPath, instance.ExecutablePath);

        // 1. Steamapps workshop content path
        var contentPath = GetSteamWorkshopContentPath(gameRoot, instance.AppId);
        if (string.IsNullOrEmpty(contentPath))
        {
            contentPath = Path.Combine(gameRoot, "steamapps", "workshop", "content", instance.AppId.ToString());
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

        // 2. Emulator steam_settings/ integration (Goldberg / ReFix)
        var steamSettingsDirs = new List<string>();
        var rootSettings = Path.Combine(gameRoot, "steam_settings");
        if (Directory.Exists(rootSettings)) steamSettingsDirs.Add(rootSettings);

        try
        {
            foreach (var d in Directory.GetDirectories(gameRoot, "steam_settings", SearchOption.AllDirectories))
            {
                if (!steamSettingsDirs.Contains(d, StringComparer.OrdinalIgnoreCase))
                    steamSettingsDirs.Add(d);
            }
        }
        catch { }

        if (steamSettingsDirs.Count == 0)
        {
            Directory.CreateDirectory(rootSettings);
            steamSettingsDirs.Add(rootSettings);
        }

        foreach (var sDir in steamSettingsDirs)
        {
            try
            {
                var emulatorModsDir = Path.Combine(sDir, "mods", publishedFileId.ToString());
                Directory.CreateDirectory(emulatorModsDir);
                CopyDirectoryRecursive(stagingFolder, emulatorModsDir);

                // Register in subscribed_items.txt and workshop_items.txt
                var subFile = Path.Combine(sDir, "subscribed_items.txt");
                var pubIdStr = publishedFileId.ToString();
                var subContent = File.Exists(subFile) ? File.ReadAllText(subFile) : string.Empty;
                if (!subContent.Contains(pubIdStr))
                {
                    File.AppendAllText(subFile, pubIdStr + Environment.NewLine);
                }

                var wsFile = Path.Combine(sDir, "workshop_items.txt");
                var wsContent = File.Exists(wsFile) ? File.ReadAllText(wsFile) : string.Empty;
                if (!wsContent.Contains(pubIdStr))
                {
                    File.AppendAllText(wsFile, pubIdStr + Environment.NewLine);
                }
            }
            catch { }
        }
    }

    private static string? GetSteamWorkshopContentPath(string installPath, uint appId)
    {
        if (string.IsNullOrWhiteSpace(installPath) || appId == 0) return null;

        try
        {
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

    /// <summary>
    /// Dynamically locates the true Game Root directory by inspecting the directory hierarchy,
    /// traversing up from subfolders like bin, Binaries, Win64, etc., and looking for root markers.
    /// </summary>
    public static string FindGameRoot(string installPath, string? executablePath = null)
    {
        if (string.IsNullOrWhiteSpace(installPath)) return string.Empty;

        string current = File.Exists(installPath) ? (Path.GetDirectoryName(installPath) ?? installPath) : installPath;
        current = Path.GetFullPath(current).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        if (!string.IsNullOrWhiteSpace(executablePath) && File.Exists(executablePath))
        {
            var exeDir = Path.GetDirectoryName(Path.GetFullPath(executablePath));
            if (!string.IsNullOrEmpty(exeDir))
            {
                var fromExe = InspectHierarchyForGameRoot(exeDir);
                if (!string.IsNullOrEmpty(fromExe)) return fromExe;
            }
        }

        var foundRoot = InspectHierarchyForGameRoot(current);
        return !string.IsNullOrEmpty(foundRoot) ? foundRoot : current;
    }

    private static string? InspectHierarchyForGameRoot(string startDir)
    {
        try
        {
            var dirInfo = new DirectoryInfo(startDir);
            while (dirInfo != null)
            {
                var name = dirInfo.Name;

                // If we are currently inside a binary subfolder, climb up to parent
                if (name.Equals("bin", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("Binaries", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("Win64", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("Win32", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("x64", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("x86", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("Release", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("Shipping", StringComparison.OrdinalIgnoreCase))
                {
                    if (dirInfo.Parent != null)
                    {
                        dirInfo = dirInfo.Parent;
                        continue;
                    }
                }

                return dirInfo.FullName;
            }
        }
        catch { }

        return startDir;
    }

    /// <summary>
    /// Dynamically finds Klei user document and save directories across Documents and LocalAppData.
    /// </summary>
    public static IEnumerable<string> FindKleiUserModDirectories()
    {
        var dirs = new List<string>();
        try
        {
            var userDocs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            var kleiDocs = Path.Combine(userDocs, "Klei");
            if (Directory.Exists(kleiDocs))
            {
                foreach (var gameFolder in Directory.GetDirectories(kleiDocs))
                {
                    dirs.Add(Path.Combine(gameFolder, "mods"));
                    dirs.Add(Path.Combine(gameFolder, "client_mods"));
                }
            }

            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var kleiLocal = Path.Combine(localAppData, "Klei");
            if (Directory.Exists(kleiLocal))
            {
                foreach (var gameFolder in Directory.GetDirectories(kleiLocal))
                {
                    dirs.Add(Path.Combine(gameFolder, "mods"));
                    dirs.Add(Path.Combine(gameFolder, "client_mods"));
                }
            }
        }
        catch { }
        return dirs;
    }

    private static void SanitizeLuaFilesAndModInfo(string modDirectory, ulong publishedFileId, string? title)
    {
        try
        {
            // 1. Strip UTF-8 BOM from all .lua files to avoid Lua 5.1 parser syntax errors
            foreach (var luaFile in Directory.GetFiles(modDirectory, "*.lua", SearchOption.AllDirectories))
            {
                try
                {
                    var bytes = File.ReadAllBytes(luaFile);
                    if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
                    {
                        var cleanBytes = new byte[bytes.Length - 3];
                        Array.Copy(bytes, 3, cleanBytes, 0, cleanBytes.Length);
                        File.WriteAllBytes(luaFile, cleanBytes);
                    }
                }
                catch { }
            }

            // 2. Ensure modinfo.lua has full compatibility flags
            var modInfoPath = Path.Combine(modDirectory, "modinfo.lua");
            if (File.Exists(modInfoPath))
            {
                var content = File.ReadAllText(modInfoPath);
                var appendLines = new List<string>();

                if (!content.Contains("dont_starve_compatible", StringComparison.OrdinalIgnoreCase))
                    appendLines.Add("dont_starve_compatible = true");

                if (!content.Contains("reign_of_giants_compatible", StringComparison.OrdinalIgnoreCase))
                    appendLines.Add("reign_of_giants_compatible = true");

                if (!content.Contains("shipwrecked_compatible", StringComparison.OrdinalIgnoreCase))
                    appendLines.Add("shipwrecked_compatible = true");

                if (!content.Contains("hamlet_compatible", StringComparison.OrdinalIgnoreCase))
                    appendLines.Add("hamlet_compatible = true");

                if (!content.Contains("dst_compatible", StringComparison.OrdinalIgnoreCase))
                    appendLines.Add("dst_compatible = true");

                if (!content.Contains("name =", StringComparison.OrdinalIgnoreCase))
                    appendLines.Add($"name = \"{title ?? $"WorkshopMod_{publishedFileId}"}\"");

                if (appendLines.Count > 0)
                {
                    var extra = "\n-- Compatibility flags added by BlueStar\n" + string.Join("\n", appendLines) + "\n";
                    File.AppendAllText(modInfoPath, extra);
                }
            }
        }
        catch { }
    }
}
