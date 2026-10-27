using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BlueStar.Core.Helpers;
using BlueStar.Core.Interfaces;
using BlueStar.Core.Models;

namespace BlueStar.Infrastructure.Mods.Installation;

/// <summary>
/// Places mod files without a per-game address book. Rules, in order:
/// 1. Provider layout (Thunderstore BepInEx packages, game-root archives).
/// 2. Root alignment: the archive's top folders match folders that already exist in the game.
/// 3. Engine and file-signature rules (Bethesda Data, Unreal ~mods, Unity BepInEx plugins, Source addons).
/// 4. Low-confidence fallback into a mods folder, which the user must confirm.
/// </summary>
public sealed class ModPlacementResolver : IModPlacementResolver
{
    private const int MaxAnchorDepth = 5;
    private const int MaxWrapperDepth = 3;

    private static readonly string[] RootDocPrefixes = ["readme", "changelog", "license"];
    private static readonly HashSet<string> ThunderstoreMetadata = new(StringComparer.OrdinalIgnoreCase)
        { "manifest.json", "icon.png", "readme.md", "changelog.md" };
    private static readonly HashSet<string> BepInExSubfolders = new(StringComparer.OrdinalIgnoreCase)
        { "plugins", "patchers", "monomod", "config", "core" };
    private static readonly HashSet<string> UnrealExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".pak", ".ucas", ".utoc", ".sig" };
    private static readonly HashSet<string> BethesdaPluginExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".esp", ".esm", ".esl", ".bsa", ".ba2" };
    private static readonly HashSet<string> BethesdaDataFolders = new(StringComparer.OrdinalIgnoreCase)
        { "meshes", "textures", "scripts", "sound", "music", "interface", "materials", "seq", "strings", "skse", "f4se", "shadersfx" };

    /// <inheritdoc />
    public ModInstallPlan Resolve(GameInstance instance, string stagingDirectory, ModPlacementHints hints)
    {
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentNullException.ThrowIfNull(hints);

        if (string.IsNullOrWhiteSpace(instance.InstallPath) || !Directory.Exists(instance.InstallPath))
            throw new DirectoryNotFoundException($"Game folder not found: '{instance.InstallPath}'.");
        if (!Directory.Exists(stagingDirectory))
            throw new DirectoryNotFoundException($"Staging folder not found: '{stagingDirectory}'.");

        var tree = new InstallTree(instance.InstallPath);
        return hints.Layout == ModPackageLayout.SteamWorkshop
            ? ResolveWorkshopItem(instance, stagingDirectory, tree, hints)
            : ResolveFiles(instance, stagingDirectory, tree, hints);
    }

    private static ModInstallPlan ResolveFiles(GameInstance instance, string stagingDirectory, InstallTree tree, ModPlacementHints hints)
    {
        var payloadRoot = UnwrapSingleFolder(stagingDirectory, tree, hints.Layout);
        var files = EnumeratePayload(payloadRoot, hints.Layout);
        if (files.Count == 0)
            throw new InvalidOperationException("The mod package contains no installable files.");

        var modName = PathHelper.SanitizeFolderName(hints.ModName ?? Path.GetFileName(payloadRoot));
        var payload = new Payload(payloadRoot, files, modName);

        return TryProviderLayout(instance, payload, hints.Layout)
               ?? TryRootAlignment(payload, tree)
               ?? TryEngineRules(instance, payload, tree)
               ?? TryGameModPathResolver(instance, payload)
               ?? Fallback(payload, tree);
    }

    // ── Steam Workshop items ───────────────────────────────────────────────

    /// <summary>
    /// Steam never decides where a Workshop item goes inside a game: it keeps each item in its own folder and the
    /// game asks ISteamUGC::GetItemInstallInfo for it. With a Steam emulator, that folder is steam_settings/mods/&lt;id&gt;,
    /// so the item is kept exactly as uploaded there. Without one, the usual file rules decide.
    /// </summary>
    private static ModInstallPlan ResolveWorkshopItem(GameInstance instance, string stagingDirectory, InstallTree tree, ModPlacementHints hints)
    {
        var itemId = hints.WorkshopItemId
            ?? throw new ArgumentException("A Steam Workshop package needs its item id.", nameof(hints));

        var rawFiles = Directory.EnumerateFiles(stagingDirectory, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(stagingDirectory, f))
            .OrderBy(rel => rel, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (rawFiles.Count == 0)
            throw new InvalidOperationException("The Workshop item contains no files.");

        var settingsDirs = tree.FindAll("steam_settings");
        if (settingsDirs.Count > 0)
        {
            return EmulatorWorkshopPlan(stagingDirectory, rawFiles, settingsDirs, itemId, PlacementConfidence.High,
                "The Steam emulator serves Workshop items from steam_settings/mods, the way Steam serves them from its workshop folder.");
        }

        var byFiles = ResolveFiles(instance, stagingDirectory, tree, hints with { Layout = ModPackageLayout.Unknown });
        if (byFiles.Confidence != PlacementConfidence.Low) return byFiles;

        // No emulator settings yet and no rule matched: offer the folder an emulator next to steam_api would read.
        var steamApiDir = tree.FindDirectoryContainingFile("steam_api64.dll") ?? tree.FindDirectoryContainingFile("steam_api.dll");
        return steamApiDir == null
            ? byFiles
            : EmulatorWorkshopPlan(stagingDirectory, rawFiles, [Path.Combine(steamApiDir, "steam_settings")], itemId,
                PlacementConfidence.Low,
                "No Steam emulator settings were found. The item will go where an emulator next to steam_api reads Workshop items.");
    }

    private static ModInstallPlan EmulatorWorkshopPlan(
        string stagingDirectory, IReadOnlyList<string> files, IReadOnlyList<string> settingsDirs, ulong itemId,
        PlacementConfidence confidence, string reason) => new()
    {
        Strategy = "SteamWorkshopEmulator",
        Confidence = confidence,
        Reason = reason,
        Operations = settingsDirs
            .SelectMany(dir => files.Select(rel => new ModFileOperation(
                Path.Combine(stagingDirectory, rel),
                Path.Combine(dir, "mods", itemId.ToString(), rel))))
            .ToList()
    };

    // ── 1. Provider layout ─────────────────────────────────────────────────

    private static ModInstallPlan? TryProviderLayout(GameInstance instance, Payload payload, ModPackageLayout layout)
    {
        var bundlesBepInEx = payload.TopFolders.Contains("BepInEx", StringComparer.OrdinalIgnoreCase);
        if (layout == ModPackageLayout.GameRoot || (layout == ModPackageLayout.BepInExPackage && bundlesBepInEx))
        {
            return Plan("GameRoot", PlacementConfidence.High, payload, rel => rel,
                "The package mirrors the game folder.");
        }

        if (layout != ModPackageLayout.BepInExPackage) return null;

        // Unreal Engine games never use BepInEx!
        // Also packages containing Unreal files (.pak, .ucas, .utoc) are Unreal mods, not BepInEx plugins.
        if (instance.Engine?.Type == EngineType.UnrealEngine ||
            payload.Extensions.Any(UnrealExtensions.Contains))
        {
            return null;
        }

        return Plan("BepInExPackage", PlacementConfidence.High, payload, rel =>
        {
            var segments = Split(rel);
            if (segments.Length > 1 && BepInExSubfolders.Contains(segments[0]))
            {
                var rest = Path.Combine(segments[1..]);
                var folder = segments[0].ToLowerInvariant();
                // config and core are shared folders; the rest get a per-mod subfolder like r2modman does.
                return folder is "config" or "core"
                    ? Path.Combine("BepInEx", folder, rest)
                    : Path.Combine("BepInEx", folder, payload.ModName, rest);
            }

            return Path.Combine("BepInEx", "plugins", payload.ModName, rel);
        }, "Thunderstore BepInEx package.");
    }

    // ── 2. Root alignment ──────────────────────────────────────────────────

    private static ModInstallPlan? TryRootAlignment(Payload payload, InstallTree tree)
    {
        if (payload.TopFolders.Count == 0) return null;

        var prefixes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var folder in payload.TopFolders)
        {
            var childHint = payload.Files
                .Select(Split)
                .Where(s => s.Length > 2 && s[0].Equals(folder, StringComparison.OrdinalIgnoreCase))
                .Select(s => s[1])
                .FirstOrDefault();

            var anchor = tree.FindAnchor(folder, childHint);
            if (anchor == null) return null;
            prefixes[folder] = Path.GetDirectoryName(anchor) ?? string.Empty;
        }

        var distinctPrefixes = prefixes.Values.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var looseFilesPrefix = distinctPrefixes.OrderBy(p => Split(p).Length).First();
        var confidence = distinctPrefixes.Count == 1 ? PlacementConfidence.High : PlacementConfidence.Medium;

        return Plan("RootAligned", confidence, payload, rel =>
        {
            var segments = Split(rel);
            var prefix = segments.Length > 1 ? prefixes[segments[0]] : looseFilesPrefix;
            return Path.Combine(prefix, rel);
        }, "The package folders match folders that already exist in the game.");
    }

    // ── 3. Engine and file-signature rules ─────────────────────────────────

    private static ModInstallPlan? TryEngineRules(GameInstance instance, Payload payload, InstallTree tree)
    {
        return TryBethesdaData(payload, tree)
               ?? TryUnrealPaks(instance, payload, tree)
               ?? TryUnityPlugins(instance, payload, tree)
               ?? TrySourceAddons(payload, tree);
    }

    private static ModInstallPlan? TryBethesdaData(Payload payload, InstallTree tree)
    {
        var dataDir = Path.Combine(tree.Root, "Data");
        if (!Directory.Exists(dataDir) || !Directory.EnumerateFiles(dataDir, "*.esm").Any()) return null;

        var hasPlugins = payload.Extensions.Any(BethesdaPluginExtensions.Contains);
        var onlyDataFolders = payload.TopFolders.Count > 0 && payload.TopFolders.All(BethesdaDataFolders.Contains);
        if (!hasPlugins && !onlyDataFolders) return null;

        return Plan("BethesdaData", hasPlugins ? PlacementConfidence.High : PlacementConfidence.Medium, payload,
            rel => Path.Combine("Data", rel), "Creation Engine plugins and assets go into Data.");
    }

    private static ModInstallPlan? TryUnrealPaks(GameInstance instance, Payload payload, InstallTree tree)
    {
        if (!payload.Extensions.Any() || !payload.Extensions.All(UnrealExtensions.Contains)) return null;

        var paks = tree.FindContentPaks();
        if (paks != null)
        {
            return Plan("UnrealPaks", PlacementConfidence.High, payload,
                rel => Path.Combine(paks, "~mods", Path.GetFileName(rel)), "Unreal Engine pak mods load from Paks/~mods.");
        }

        if (instance.Engine?.Type == EngineType.UnrealEngine)
        {
            var resolution = GameModPathResolver.ResolveModPaths(instance);
            if (!string.IsNullOrWhiteSpace(resolution.PrimaryDirectory) &&
                !string.IsNullOrWhiteSpace(instance.InstallPath) &&
                PathHelper.IsSubPathOf(resolution.PrimaryDirectory, instance.InstallPath))
            {
                var relPrimary = Path.GetRelativePath(instance.InstallPath, resolution.PrimaryDirectory);
                return Plan("UnrealPaks", PlacementConfidence.High, payload,
                    rel => Path.Combine(relPrimary, Path.GetFileName(rel)), "Unreal Engine pak mods load from Paks/~mods.");
            }
        }

        return null;
    }

    private static ModInstallPlan? TryUnityPlugins(GameInstance instance, Payload payload, InstallTree tree)
    {
        var isUnity = instance.Engine?.Type == EngineType.Unity || tree.HasUnityDataFolder();
        if (!isUnity || !payload.Extensions.Contains(".dll", StringComparer.OrdinalIgnoreCase)) return null;

        if (tree.HasDirectory("BepInEx"))
        {
            return Plan("UnityBepInExPlugins", PlacementConfidence.High, payload,
                rel => Path.Combine("BepInEx", "plugins", payload.ModName, rel), "Unity plugin for BepInEx.");
        }

        if (tree.HasDirectory("MelonLoader"))
        {
            return Plan("UnityMelonLoaderMods", PlacementConfidence.Medium, payload,
                rel => Path.Combine("Mods", rel), "Unity mod for MelonLoader.");
        }

        return Plan("UnityBepInExPlugins", PlacementConfidence.Low, payload,
            rel => Path.Combine("BepInEx", "plugins", payload.ModName, rel),
            "Unity plugin, but no mod loader is installed yet. Install BepInEx first.");
    }

    private static ModInstallPlan? TrySourceAddons(Payload payload, InstallTree tree)
    {
        if (!payload.Extensions.Contains(".vpk", StringComparer.OrdinalIgnoreCase)) return null;

        var gameDir = tree.FindDirectoryContainingFile("gameinfo.txt");
        if (gameDir == null) return null;

        return Plan("SourceAddons", PlacementConfidence.Medium, payload,
            rel => Path.Combine(gameDir, "addons", Path.GetFileName(rel)), "Source Engine addon.");
    }

    private static ModInstallPlan? TryGameModPathResolver(GameInstance instance, Payload payload)
    {
        var resolution = GameModPathResolver.ResolveModPaths(instance);
        if (resolution.GameCategory is "Generic" or "Default" or "Unity" or "UnityBepInEx" ||
            string.IsNullOrWhiteSpace(resolution.PrimaryDirectory) ||
            string.IsNullOrWhiteSpace(instance.InstallPath))
        {
            return null;
        }

        if (PathHelper.IsSubPathOf(resolution.PrimaryDirectory, instance.InstallPath))
        {
            var relPrimary = Path.GetRelativePath(instance.InstallPath, resolution.PrimaryDirectory);
            return Plan("GameModPathResolver", PlacementConfidence.High, payload,
                rel => Path.Combine(relPrimary, rel), $"Game specific mod directory resolved for {resolution.GameCategory}.");
        }

        return null;
    }

    // ── 4. Fallback ────────────────────────────────────────────────────────

    private static ModInstallPlan Fallback(Payload payload, InstallTree tree)
    {
        var modsFolder = tree.HasDirectory("Mods") ? tree.ActualName("Mods") : "mods";
        return Plan("ModsFolder", PlacementConfidence.Low, payload,
            rel => Path.Combine(modsFolder, payload.ModName, rel),
            "Could not tell where this mod belongs. Check the destination before installing.");
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private static ModInstallPlan Plan(
        string strategy, PlacementConfidence confidence, Payload payload, Func<string, string> map, string reason) => new()
    {
        Strategy = strategy,
        Confidence = confidence,
        Reason = reason,
        Operations = payload.Files
            .Select(rel => new ModFileOperation(Path.Combine(payload.Root, rel), map(rel)))
            .ToList()
    };

    /// <summary>Skips wrapper folders ("MyMod-1.2/...") that do not exist in the game.</summary>
    private static string UnwrapSingleFolder(string stagingDirectory, InstallTree tree, ModPackageLayout layout)
    {
        var current = stagingDirectory;
        for (var i = 0; i < MaxWrapperDepth; i++)
        {
            var rootFiles = Directory.EnumerateFiles(current).Where(f => !IsMetadata(Path.GetFileName(f), layout, atRoot: true));
            var dirs = Directory.GetDirectories(current);
            if (rootFiles.Any() || dirs.Length != 1) break;

            var name = Path.GetFileName(dirs[0]);
            if (tree.FindAnchor(name, null) != null) break;
            // A Thunderstore package whose only folder is plugins/ must keep it, even before BepInEx exists.
            if (layout == ModPackageLayout.BepInExPackage &&
                (BepInExSubfolders.Contains(name) || name.Equals("BepInEx", StringComparison.OrdinalIgnoreCase))) break;
            current = dirs[0];
        }

        return current;
    }

    private static IReadOnlyList<string> EnumeratePayload(string root, ModPackageLayout layout) =>
        Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(root, f))
            .Where(rel => !IsMetadata(Path.GetFileName(rel), layout, atRoot: Split(rel).Length == 1))
            .OrderBy(rel => rel, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static bool IsMetadata(string fileName, ModPackageLayout layout, bool atRoot)
    {
        if (!atRoot) return false;
        if (ThunderstoreMetadata.Contains(fileName)) return true;
        return RootDocPrefixes.Any(p => fileName.StartsWith(p, StringComparison.OrdinalIgnoreCase));
    }

    private static string[] Split(string relativePath) =>
        relativePath.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);

    private sealed class Payload
    {
        public Payload(string root, IReadOnlyList<string> files, string modName)
        {
            Root = root;
            Files = files;
            ModName = modName;
            TopFolders = files.Select(Split).Where(s => s.Length > 1).Select(s => s[0])
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            Extensions = files.Select(Path.GetExtension).Where(e => !string.IsNullOrEmpty(e))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList()!;
        }

        public string Root { get; }
        public IReadOnlyList<string> Files { get; }
        public string ModName { get; }
        public IReadOnlyList<string> TopFolders { get; }
        public IReadOnlyList<string> Extensions { get; }
    }
}
