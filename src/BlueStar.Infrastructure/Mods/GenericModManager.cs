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
            Directory.CreateDirectory(resolution.PrimaryDirectory);
            return resolution.PrimaryDirectory;
        }

        var modsDir = Path.Combine(instance.InstallPath, "mods");
        Directory.CreateDirectory(modsDir);
        return modsDir;
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

        if (candidateDirs.Count == 0)
        {
            var primary = GetModsDirectory(instance);
            if (!string.IsNullOrWhiteSpace(primary) && Directory.Exists(primary))
                candidateDirs.Add(primary);
        }

        var result = new List<ModItem>();
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            foreach (var modsDir in candidateDirs)
            {
                if (!Directory.Exists(modsDir)) continue;

                // 1. Files
                var files = Directory.GetFiles(modsDir, "*.*", SearchOption.TopDirectoryOnly);
                foreach (var file in files)
                {
                    var fileName = Path.GetFileName(file);
                    if (fileName.Equals("workshop_info.json", StringComparison.OrdinalIgnoreCase) ||
                        fileName.EndsWith("_info.json", StringComparison.OrdinalIgnoreCase) ||
                        fileName.EndsWith(".bak", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var ext = Path.GetExtension(file).ToLowerInvariant();
                    bool isEnabled = !ext.Equals(".disabled", StringComparison.OrdinalIgnoreCase);
                    string baseName = isEnabled
                        ? Path.GetFileNameWithoutExtension(file)
                        : Path.GetFileNameWithoutExtension(fileName.Substring(0, fileName.Length - 9));

                    var fi = new FileInfo(file);
                    var modId = fileName;

                    string displayName = baseName;
                    string? author = null;
                    string? description = null;
                    string category = "Mod File";

                    // Check for companion _info.json (e.g. 123456_info.json for 123456.vpk)
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

                    if (seenIds.Add(modId))
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
                            Category = category
                        });
                    }
                }

                // 2. Directories
                var dirs = Directory.GetDirectories(modsDir, "*", SearchOption.TopDirectoryOnly);
                foreach (var dir in dirs)
                {
                    var dirName = Path.GetFileName(dir);
                    if (dirName.Equals(".DepotDownloader", StringComparison.OrdinalIgnoreCase)) continue;

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
                    if (seenIds.Add(modId))
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
                if (ext == ".zip")
                {
                    var modFolder = Path.Combine(modsDir, Path.GetFileNameWithoutExtension(sourceFilePath));
                    ZipFile.ExtractToDirectory(sourceFilePath, modFolder, overwriteFiles: true);
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
                                lines.Add($"EnableMod(\"{cleanModId}\")");
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
