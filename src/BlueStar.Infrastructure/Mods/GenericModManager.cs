using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Threading;
using System.Threading.Tasks;
using BlueStar.Core.Interfaces;
using BlueStar.Core.Models;
using Microsoft.Extensions.Logging;

namespace BlueStar.Infrastructure.Mods;

/// <summary>
/// Generic folder-based mod manager for standard mods / plugins directory.
/// </summary>
public sealed class GenericModManager : IModManager
{
    private readonly ILogger<GenericModManager> _logger;

    public string Id => "generic";
    public string DisplayName => "Generic Mod Folder Manager (mods/)";

    public GenericModManager(ILogger<GenericModManager> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public bool IsSupported(GameInstance instance) => true; // Fallback for any instance

    public string GetModsDirectory(GameInstance instance)
    {
        if (string.IsNullOrWhiteSpace(instance.InstallPath) || !Directory.Exists(instance.InstallPath))
            return string.Empty;

        var resolution = GameModPathResolver.ResolveModPaths(instance);
        if (!string.IsNullOrWhiteSpace(resolution.PrimaryDirectory))
        {
            return resolution.PrimaryDirectory;
        }

        return Path.Combine(instance.InstallPath, "mods");
    }

    public Task<IReadOnlyList<ModItem>> GetInstalledModsAsync(GameInstance instance, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(instance.InstallPath) || !Directory.Exists(instance.InstallPath))
            return Task.FromResult<IReadOnlyList<ModItem>>([]);

        var candidateDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var resolution = GameModPathResolver.ResolveModPaths(instance);

        foreach (var dir in resolution.ScanDirectories)
        {
            if (Directory.Exists(dir)) candidateDirs.Add(dir);
        }

        var modsLower = Path.Combine(instance.InstallPath, "mods");
        if (Directory.Exists(modsLower)) candidateDirs.Add(modsLower);
        var modsUpper = Path.Combine(instance.InstallPath, "Mods");
        if (Directory.Exists(modsUpper)) candidateDirs.Add(modsUpper);

        var wsLocalUpper = Path.Combine(instance.InstallPath, "Mods", "Workshop");
        if (Directory.Exists(wsLocalUpper)) candidateDirs.Add(wsLocalUpper);

        var wsLocalLower = Path.Combine(instance.InstallPath, "mods", "workshop");
        if (Directory.Exists(wsLocalLower)) candidateDirs.Add(wsLocalLower);


        var result = new List<ModItem>();
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            // If Tabletop Simulator, perform automatic cleanup of broken metadata files
            if (resolution.GameCategory == "TabletopSimulator")
            {
                foreach (var dir in candidateDirs)
                {
                    if (Directory.Exists(dir))
                    {
                        GameModPathResolver.CleanUpBrokenTabletopSimulatorFiles(dir);
                    }
                }
            }

            foreach (var modsDir in candidateDirs)
            {
                if (!Directory.Exists(modsDir)) continue;

                // 1. Files
                var allFilesInDir = Directory.GetFiles(modsDir, "*.*", SearchOption.TopDirectoryOnly);

                // Pre-scan: collect base names of non-image files in this directory.
                // If a .png or .jpg has the same base name as a non-image mod (e.g. 2705675320.png alongside 2705675320.json),
                // the image is considered a companion preview/thumbnail and NOT a standalone mod.
                var nonImageBaseNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var f in allFilesInDir)
                {
                    var fn = Path.GetFileName(f);
                    if (GameModPathResolver.IsExcludedSystemOrMetadataFile(fn)) continue;
                    var ext = Path.GetExtension(f).ToLowerInvariant();
                    if (ext is not (".png" or ".jpg" or ".jpeg" or ".webp" or ".bmp"))
                    {
                        var bn = ext.Equals(".disabled", StringComparison.OrdinalIgnoreCase)
                            ? Path.GetFileNameWithoutExtension(fn[..^9])
                            : Path.GetFileNameWithoutExtension(f);
                        if (!string.IsNullOrWhiteSpace(bn))
                            nonImageBaseNames.Add(bn);
                    }
                }

                // Load Tabletop Simulator WorkshopFileInfos.json index map if present
                var ttsIndexMap = GameModPathResolver.LoadTabletopSimulatorIndexMap(modsDir);

                foreach (var file in allFilesInDir)
                {
                    var fileName = Path.GetFileName(file);
                    if (GameModPathResolver.IsExcludedSystemOrMetadataFile(fileName)) continue;

                    var ext = Path.GetExtension(file).ToLowerInvariant();
                    bool isEnabled = !ext.Equals(".disabled", StringComparison.OrdinalIgnoreCase);
                    string baseName = isEnabled
                        ? Path.GetFileNameWithoutExtension(file)
                        : Path.GetFileNameWithoutExtension(fileName.Substring(0, fileName.Length - 9));

                    // If it is an image and there is another mod file with the same base name,
                    // skip this image because it serves as the preview thumbnail of that mod.
                    bool isImageExt = ext is ".png" or ".jpg" or ".jpeg" or ".webp" or ".bmp" ||
                                      (ext == ".disabled" && (fileName.Contains(".png") || fileName.Contains(".jpg") || fileName.Contains(".jpeg")));
                    if (isImageExt && nonImageBaseNames.Contains(baseName))
                    {
                        continue;
                    }

                    var fi = new FileInfo(file);
                    var modId = fileName;
                    string displayName = baseName;
                    string? author = null;
                    string? description = null;
                    string? iconPath = null;

                    bool isInsideBepInEx = modsDir.IndexOf(Path.Combine("BepInEx", "plugins"), StringComparison.OrdinalIgnoreCase) >= 0 ||
                                           modsDir.IndexOf("BepInEx", StringComparison.OrdinalIgnoreCase) >= 0;
                    bool isInsideWorkshop = !isInsideBepInEx && (
                        modsDir.IndexOf("Workshop", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        (!string.IsNullOrWhiteSpace(resolution.WorkshopDirectory) && modsDir.StartsWith(resolution.WorkshopDirectory, StringComparison.OrdinalIgnoreCase)) ||
                        resolution.GameCategory == "TabletopSimulator");

                    string category = isInsideBepInEx
                        ? "BepInEx Plugin"
                        : (isInsideWorkshop ? "Steam Workshop" : (ext.Contains("dll", StringComparison.OrdinalIgnoreCase) ? "Plugin (.dll)" : "Mod Nativo"));

                    // Determine icon path
                    if (isImageExt)
                    {
                        iconPath = file;
                    }
                    else
                    {
                        var siblingPng = Path.ChangeExtension(file, ".png");
                        if (File.Exists(siblingPng)) iconPath = siblingPng;
                        else
                        {
                            var siblingJpg = Path.ChangeExtension(file, ".jpg");
                            if (File.Exists(siblingJpg)) iconPath = siblingJpg;
                        }
                    }

                    // A. Tabletop Simulator .json Save Game metadata
                    if (ext == ".json" || (ext == ".disabled" && fileName.Contains(".json")))
                    {
                        var saveInfo = GameModPathResolver.ParseTabletopSimulatorSave(file);
                        if (saveInfo.IsValidSave)
                        {
                            category = "Tabletop Simulator Mod";
                            if (!string.IsNullOrWhiteSpace(saveInfo.SaveName))
                                displayName = saveInfo.SaveName;
                            if (!string.IsNullOrWhiteSpace(saveInfo.GameMode))
                                description = saveInfo.GameMode;
                            if (!string.IsNullOrWhiteSpace(saveInfo.Date) && string.IsNullOrEmpty(description))
                                description = $"Created: {saveInfo.Date}";
                        }
                    }

                    // B. Query Tabletop Simulator WorkshopFileInfos.json index
                    if (ttsIndexMap.Count > 0 && (displayName == baseName || string.IsNullOrWhiteSpace(displayName)))
                    {
                        if (ttsIndexMap.TryGetValue(baseName, out var idxName) ||
                            ttsIndexMap.TryGetValue(fileName, out idxName))
                        {
                            displayName = idxName;
                            category = "Tabletop Simulator Mod";
                        }
                    }

                    // C. Check companion _info.json
                    var companionJson = Path.Combine(modsDir, $"{baseName}_info.json");
                    if (File.Exists(companionJson))
                    {
                        try
                        {
                            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(companionJson));
                            if (doc.RootElement.TryGetProperty("Title", out var t) && !string.IsNullOrWhiteSpace(t.GetString()))
                                displayName = t.GetString()!;
                            if (doc.RootElement.TryGetProperty("Author", out var a) && !string.IsNullOrWhiteSpace(a.GetString()))
                                author = a.GetString();
                            if (doc.RootElement.TryGetProperty("Description", out var d) && !string.IsNullOrWhiteSpace(d.GetString()))
                                description = d.GetString();

                            category = "Steam Workshop";
                        }
                        catch { }
                    }

                    // D. Fallback for Workshop files where baseName is a Workshop published file ID
                    var normalizedFileKey = baseName.StartsWith("workshop-", StringComparison.OrdinalIgnoreCase)
                        ? baseName.Substring(9)
                        : baseName;

                    if (ulong.TryParse(normalizedFileKey, out var wsId) && wsId > 1000)
                    {
                        category = "Steam Workshop";
                        author ??= $"Steam Workshop #{wsId}";
                        if (displayName == baseName)
                        {
                            displayName = $"Workshop Item #{wsId}";
                        }
                    }

                    bool isFileDuplicate = !seenIds.Add(normalizedFileKey) ||
                                           (!string.IsNullOrWhiteSpace(displayName) && !seenNames.Add(displayName.Trim()));

                    if (!isFileDuplicate)
                    {
                        result.Add(new ModItem
                        {
                            Id = modId,
                            Name = displayName,
                            Author = author,
                            Description = description,
                            FilePath = file,
                            IsEnabled = isEnabled,
                            SizeBytes = fi.Length,
                            Category = category,
                            IconPath = iconPath
                        });
                    }
                }

                // 2. Directories
                var dirs = Directory.GetDirectories(modsDir, "*", SearchOption.TopDirectoryOnly);
                foreach (var dir in dirs)
                {
                    var dirName = Path.GetFileName(dir);
                    if (dirName.Equals(".DepotDownloader", StringComparison.OrdinalIgnoreCase) || dirName.Equals("Workshop", StringComparison.OrdinalIgnoreCase) || dirName.Equals("plugins", StringComparison.OrdinalIgnoreCase)) continue;

                    bool isEnabled = !dirName.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase);
                    string cleanDirName = isEnabled ? dirName : dirName.Substring(0, dirName.Length - 9);

                    string displayName = cleanDirName;
                    string? author = null;
                    string? description = null;
                    string category = "Mod Package";
                    long sizeBytes = CalculateDirectorySize(dir);

                    // A. Check for workshop_info.json metadata
                    var workshopJsonPath = Path.Combine(dir, "workshop_info.json");
                    if (File.Exists(workshopJsonPath))
                    {
                        try
                        {
                            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(workshopJsonPath));
                            if (doc.RootElement.TryGetProperty("Title", out var t) && !string.IsNullOrWhiteSpace(t.GetString()))
                                displayName = t.GetString()!;
                            if (doc.RootElement.TryGetProperty("Author", out var a) && !string.IsNullOrWhiteSpace(a.GetString()))
                                author = a.GetString();
                            if (doc.RootElement.TryGetProperty("Description", out var d) && !string.IsNullOrWhiteSpace(d.GetString()))
                                description = d.GetString();

                            category = "Steam Workshop";
                        }
                        catch { }
                    }

                    // B. Check for Klei modinfo.lua metadata
                    var modInfoLua = Path.Combine(dir, "modinfo.lua");
                    if (File.Exists(modInfoLua))
                    {
                        try
                        {
                            var luaText = File.ReadAllText(modInfoLua);
                            var nameMatch = System.Text.RegularExpressions.Regex.Match(luaText, @"name\s*=\s*[""']([^""']+)[""']");
                            if (nameMatch.Success && !string.IsNullOrWhiteSpace(nameMatch.Groups[1].Value))
                                displayName = nameMatch.Groups[1].Value;

                            var authorMatch = System.Text.RegularExpressions.Regex.Match(luaText, @"author\s*=\s*[""']([^""']+)[""']");
                            if (authorMatch.Success && !string.IsNullOrWhiteSpace(authorMatch.Groups[1].Value))
                                author = authorMatch.Groups[1].Value;

                            var descMatch = System.Text.RegularExpressions.Regex.Match(luaText, @"description\s*=\s*[""']([^""']+)[""']");
                            if (descMatch.Success && !string.IsNullOrWhiteSpace(descMatch.Groups[1].Value))
                                description = descMatch.Groups[1].Value;

                            category = "Klei Workshop Mod";
                        }
                        catch { }
                    }

                    var modId = dirName;
                    var normalizedKey = cleanDirName.StartsWith("workshop-", StringComparison.OrdinalIgnoreCase)
                        ? cleanDirName.Substring(9)
                        : cleanDirName;

                    bool isDirDuplicate = !seenIds.Add(normalizedKey) ||
                                          (!string.IsNullOrWhiteSpace(displayName) && !seenNames.Add(displayName.Trim()));

                    if (!isDirDuplicate)
                    {
                        result.Add(new ModItem
                        {
                            Id = modId,
                            Name = displayName,
                            Author = author,
                            Description = description,
                            FilePath = dir,
                            IsEnabled = isEnabled,
                            SizeBytes = sizeBytes,
                            Category = category
                        });
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to read mods for {Game}", instance.Name);
        }

        return Task.FromResult<IReadOnlyList<ModItem>>(result.AsReadOnly());
    }

    public async Task<bool> InstallModAsync(GameInstance instance, string sourceFilePath, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(sourceFilePath) || !File.Exists(sourceFilePath))
            return false;

        var modsDir = GetModsDirectory(instance);
        if (string.IsNullOrWhiteSpace(modsDir)) return false;

        return await Task.Run(() =>
        {
            try
            {
                var ext = Path.GetExtension(sourceFilePath).ToLowerInvariant();
                if (ext is ".zip" or ".rar" or ".7z" or ".tar" or ".gz")
                {
                    var modFolder = Path.Combine(modsDir, Path.GetFileNameWithoutExtension(sourceFilePath));
                    BlueStar.Infrastructure.Common.ArchiveExtractor.ExtractToDirectory(sourceFilePath, modFolder, overwrite: true);
                    return true;
                }
                else
                {
                    var dest = Path.Combine(modsDir, Path.GetFileName(sourceFilePath));
                    File.Copy(sourceFilePath, dest, overwrite: true);
                    return true;
                }

            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to install generic mod {Path}", sourceFilePath);
                return false;
            }
        }, ct).ConfigureAwait(false);
    }

    public Task<bool> UninstallModAsync(GameInstance instance, string modId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(instance.InstallPath) || string.IsNullOrWhiteSpace(modId))
            return Task.FromResult(false);

        // 1. Direct path check
        if (File.Exists(modId))
        {
            try { File.Delete(modId); return Task.FromResult(true); } catch { }
        }
        if (Directory.Exists(modId))
        {
            try { Directory.Delete(modId, recursive: true); return Task.FromResult(true); } catch { }
        }

        var candidateDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var resolution = GameModPathResolver.ResolveModPaths(instance);
        if (!string.IsNullOrWhiteSpace(resolution.PrimaryDirectory)) candidateDirs.Add(resolution.PrimaryDirectory);
        foreach (var s in resolution.ScanDirectories) candidateDirs.Add(s);

        if (!string.IsNullOrWhiteSpace(instance.InstallPath))
        {
            candidateDirs.Add(Path.Combine(instance.InstallPath, "mods"));
            candidateDirs.Add(Path.Combine(instance.InstallPath, "Mods"));
        }

        bool deletedAny = false;
        var cleanModId = modId.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase)
            ? modId.Substring(0, modId.Length - 9)
            : modId;

        try
        {
            foreach (var modsDir in candidateDirs)
            {
                if (string.IsNullOrWhiteSpace(modsDir) || !Directory.Exists(modsDir)) continue;

                // Check file
                var fileCandidates = new[]
                {
                    Path.Combine(modsDir, modId),
                    Path.Combine(modsDir, cleanModId),
                    Path.Combine(modsDir, cleanModId + ".disabled")
                };

                foreach (var f in fileCandidates)
                {
                    if (File.Exists(f))
                    {
                        File.Delete(f);
                        deletedAny = true;

                        // Delete companion _info.json
                        var baseName = Path.GetFileNameWithoutExtension(cleanModId);
                        var infoJson = Path.Combine(modsDir, $"{baseName}_info.json");
                        if (File.Exists(infoJson)) File.Delete(infoJson);
                    }
                    else if (Directory.Exists(f))
                    {
                        Directory.Delete(f, recursive: true);
                        deletedAny = true;
                    }
                }

                // If Klei game, remove from modsettings.lua
                if (resolution.GameCategory == "Klei")
                {
                    var modSettings = Path.Combine(modsDir, "modsettings.lua");
                    if (File.Exists(modSettings))
                    {
                        try
                        {
                            var text = File.ReadAllText(modSettings);
                            var lines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                                .Where(l => !l.Contains(cleanModId, StringComparison.OrdinalIgnoreCase));
                            File.WriteAllText(modSettings, string.Join(Environment.NewLine, lines) + Environment.NewLine);
                        }
                        catch { }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to uninstall mod {Id}", modId);
        }

        return Task.FromResult(deletedAny);
    }

    public Task<bool> ToggleModAsync(GameInstance instance, string modId, bool isEnabled, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(instance.InstallPath) || string.IsNullOrWhiteSpace(modId))
            return Task.FromResult(false);

        var candidateDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var resolution = GameModPathResolver.ResolveModPaths(instance);
        if (!string.IsNullOrWhiteSpace(resolution.PrimaryDirectory)) candidateDirs.Add(resolution.PrimaryDirectory);
        foreach (var s in resolution.ScanDirectories) candidateDirs.Add(s);

        if (!string.IsNullOrWhiteSpace(instance.InstallPath))
        {
            candidateDirs.Add(Path.Combine(instance.InstallPath, "mods"));
            candidateDirs.Add(Path.Combine(instance.InstallPath, "Mods"));
        }

        var cleanModId = modId.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase)
            ? modId.Substring(0, modId.Length - 9)
            : modId;

        bool toggledAny = false;

        try
        {
            foreach (var modsDir in candidateDirs)
            {
                if (string.IsNullOrWhiteSpace(modsDir) || !Directory.Exists(modsDir)) continue;

                var normalPath = Path.Combine(modsDir, cleanModId);
                var disabledPath = Path.Combine(modsDir, cleanModId + ".disabled");

                // If toggling a directory
                if (Directory.Exists(normalPath) && !isEnabled)
                {
                    Directory.Move(normalPath, disabledPath);
                    toggledAny = true;
                }
                else if (Directory.Exists(disabledPath) && isEnabled)
                {
                    Directory.Move(disabledPath, normalPath);
                    toggledAny = true;
                }

                // If toggling a single file
                if (File.Exists(normalPath) && !isEnabled)
                {
                    File.Move(normalPath, disabledPath, overwrite: true);
                    toggledAny = true;
                }
                else if (File.Exists(disabledPath) && isEnabled)
                {
                    File.Move(disabledPath, normalPath, overwrite: true);
                    toggledAny = true;
                }

                // Sync Klei modsettings.lua
                if (resolution.GameCategory == "Klei")
                {
                    var modSettings = Path.Combine(modsDir, "modsettings.lua");
                    try
                    {
                        var text = File.Exists(modSettings) ? File.ReadAllText(modSettings) : "-- Mod Settings\n";
                        var lines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).ToList();

                        if (!isEnabled)
                        {
                            // Remove force enable lines
                            lines.RemoveAll(l => l.Contains(cleanModId, StringComparison.OrdinalIgnoreCase));
                            File.WriteAllText(modSettings, string.Join(Environment.NewLine, lines) + Environment.NewLine);
                        }
                        else
                        {
                            // Add force enable lines if missing
                            if (!lines.Any(l => l.Contains(cleanModId, StringComparison.OrdinalIgnoreCase)))
                            {
                                lines.Add($"ForceEnableMod(\"{cleanModId}\")");
                                File.WriteAllText(modSettings, string.Join(Environment.NewLine, lines) + Environment.NewLine);
                            }
                        }
                    }
                    catch { }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to toggle generic mod {Id}", modId);
        }

        return Task.FromResult(toggledAny);
    }

    private static long CalculateDirectorySize(string directoryPath)
    {
        try
        {
            var files = Directory.GetFiles(directoryPath, "*", SearchOption.AllDirectories);
            long total = 0;
            foreach (var f in files)
            {
                try { total += new FileInfo(f).Length; } catch { }
            }
            return total;
        }
        catch
        {
            return 0;
        }
    }
}
