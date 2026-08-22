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
/// Dedicated mod manager for Unity games supporting BepInEx plugins (BepInEx/plugins) and custom mods.
/// </summary>
public sealed class UnityModManager : IModManager
{
    private readonly ILogger<UnityModManager> _logger;

    public string Id => "unity";
    public string DisplayName => "Unity BepInEx Plugins Manager (BepInEx/plugins)";

    public UnityModManager(ILogger<UnityModManager> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public bool IsSupported(GameInstance instance) => instance.Engine?.Type == EngineType.Unity;

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

        var bepPlugins = Path.Combine(instance.InstallPath, "BepInEx", "plugins");
        if (Directory.Exists(Path.Combine(instance.InstallPath, "BepInEx")))
        {
            Directory.CreateDirectory(bepPlugins);
            return bepPlugins;
        }

        var fallbackMods = Path.Combine(instance.InstallPath, "mods");
        Directory.CreateDirectory(fallbackMods);
        return fallbackMods;
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

        var bepPlugins = Path.Combine(instance.InstallPath, "BepInEx", "plugins");
        if (Directory.Exists(bepPlugins)) candidateDirs.Add(bepPlugins);

        var modsLower = Path.Combine(instance.InstallPath, "mods");
        if (Directory.Exists(modsLower)) candidateDirs.Add(modsLower);

        var modsUpper = Path.Combine(instance.InstallPath, "Mods");
        if (Directory.Exists(modsUpper)) candidateDirs.Add(modsUpper);

        // If none exist, include the primary mods directory
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
                    string category = ext.Contains("dll", StringComparison.OrdinalIgnoreCase) ? "BepInEx Plugin (.dll)" : "Mod File";

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

                    // Check for workshop_info.json metadata
                    string displayName = cleanDirName;
                    string? author = null;
                    string? description = null;
                    string category = "Plugin Package";
                    long sizeBytes = CalculateDirectorySize(dir);

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
            _logger.LogError(ex, "Failed to read Unity plugins for {Game}", instance.Name);
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
                    var pluginFolder = Path.Combine(modsDir, Path.GetFileNameWithoutExtension(sourceFilePath));
                    ZipFile.ExtractToDirectory(sourceFilePath, pluginFolder, overwriteFiles: true);
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
                _logger.LogError(ex, "Failed to install Unity plugin {Path}", sourceFilePath);
                return false;
            }
        }, ct).ConfigureAwait(false);
    }

    public Task<bool> UninstallModAsync(GameInstance instance, string modId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(instance.InstallPath)) return Task.FromResult(false);

        var candidateDirs = new[]
        {
            GetModsDirectory(instance),
            Path.Combine(instance.InstallPath, "BepInEx", "plugins"),
            Path.Combine(instance.InstallPath, "mods"),
            Path.Combine(instance.InstallPath, "Mods")
        };

        try
        {
            foreach (var modsDir in candidateDirs.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(modsDir) || !Directory.Exists(modsDir)) continue;

                var file = Path.Combine(modsDir, modId);
                if (File.Exists(file))
                {
                    File.Delete(file);
                    return Task.FromResult(true);
                }
                if (Directory.Exists(file))
                {
                    Directory.Delete(file, recursive: true);
                    return Task.FromResult(true);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to uninstall Unity plugin {Id}", modId);
        }

        return Task.FromResult(false);
    }

    public Task<bool> ToggleModAsync(GameInstance instance, string modId, bool isEnabled, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(instance.InstallPath)) return Task.FromResult(false);

        var candidateDirs = new[]
        {
            GetModsDirectory(instance),
            Path.Combine(instance.InstallPath, "BepInEx", "plugins"),
            Path.Combine(instance.InstallPath, "mods"),
            Path.Combine(instance.InstallPath, "Mods")
        };

        try
        {
            foreach (var modsDir in candidateDirs.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(modsDir) || !Directory.Exists(modsDir)) continue;

                var path = Path.Combine(modsDir, modId);
                if (File.Exists(path))
                {
                    string target = isEnabled
                        ? (path.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase) ? path.Substring(0, path.Length - 9) : path)
                        : (!path.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase) ? path + ".disabled" : path);

                    if (path != target) File.Move(path, target, overwrite: true);
                    return Task.FromResult(true);
                }
                if (Directory.Exists(path))
                {
                    string target = isEnabled
                        ? (path.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase) ? path.Substring(0, path.Length - 9) : path)
                        : (!path.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase) ? path + ".disabled" : path);

                    if (path != target) Directory.Move(path, target);
                    return Task.FromResult(true);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to toggle Unity plugin {Id}", modId);
        }

        return Task.FromResult(false);
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
