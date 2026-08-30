using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BlueStar.Core.Interfaces;
using BlueStar.Core.Models;
using Microsoft.Extensions.Logging;

namespace BlueStar.Infrastructure.Mods;

/// <summary>
/// Mod manager implementation for Unreal Engine games (managing Content/Paks/~mods and Mods/ folders).
/// </summary>
public sealed class UnrealModManager : IModManager
{
    private readonly ILogger<UnrealModManager> _logger;

    public string Id => "unreal-paks";
    public string DisplayName => "Unreal Engine Mod Manager (~mods/Paks)";

    public UnrealModManager(ILogger<UnrealModManager> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public bool IsSupported(GameInstance instance)
    {
        if (instance.Engine?.Type == EngineType.UnrealEngine) return true;
        if (instance.Engine?.Supports(EngineCapabilities.Mods) == true) return true;

        if (!string.IsNullOrWhiteSpace(instance.InstallPath) && Directory.Exists(instance.InstallPath))
        {
            var paksDirs = Directory.GetDirectories(instance.InstallPath, "Paks", SearchOption.AllDirectories);
            return paksDirs.Length > 0;
        }

        return false;
    }

    public string GetModsDirectory(GameInstance instance)
    {
        if (string.IsNullOrWhiteSpace(instance.InstallPath) || !Directory.Exists(instance.InstallPath))
            return string.Empty;

        var resolution = GameModPathResolver.ResolveModPaths(instance);
        if (!string.IsNullOrWhiteSpace(resolution.PrimaryDirectory))
        {
            return resolution.PrimaryDirectory;
        }

        // Try to find the Content/Paks directory
        var paksDirs = Directory.GetDirectories(instance.InstallPath, "Paks", SearchOption.AllDirectories);
        if (paksDirs.Length > 0)
        {
            return Path.Combine(paksDirs[0], "~mods");
        }

        return Path.Combine(instance.InstallPath, "Mods");
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

        var paksDirs = Directory.GetDirectories(instance.InstallPath, "Paks", SearchOption.AllDirectories);
        foreach (var pDir in paksDirs)
        {
            var tildeMods = Path.Combine(pDir, "~mods");
            if (Directory.Exists(tildeMods)) candidateDirs.Add(tildeMods);
        }

        var modsUpper = Path.Combine(instance.InstallPath, "Mods");
        if (Directory.Exists(modsUpper)) candidateDirs.Add(modsUpper);

        var modsLower = Path.Combine(instance.InstallPath, "mods");
        if (Directory.Exists(modsLower)) candidateDirs.Add(modsLower);

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
                    var ext = Path.GetExtension(file).ToLowerInvariant();
                    var fileName = Path.GetFileName(file);
                    if (fileName.Equals("workshop_info.json", StringComparison.OrdinalIgnoreCase) ||
                        fileName.EndsWith("_info.json", StringComparison.OrdinalIgnoreCase) ||
                        fileName.EndsWith(".bak", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    bool isModFile = ext is ".pak" or ".ucas" or ".utoc" or ".disabled";
                    if (!isModFile) continue;

                    bool isEnabled = !ext.Equals(".disabled", StringComparison.OrdinalIgnoreCase);
                    string baseName = isEnabled
                        ? Path.GetFileNameWithoutExtension(file)
                        : Path.GetFileNameWithoutExtension(fileName.Substring(0, fileName.Length - 9));

                    if (string.IsNullOrWhiteSpace(baseName)) baseName = fileName;

                    var fi = new FileInfo(file);
                    var modId = fileName;

                    string displayName = CleanModName(baseName);
                    string? author = null;
                    string? description = null;
                    string category = ext == ".pak" || fileName.EndsWith(".pak.disabled", StringComparison.OrdinalIgnoreCase) ? "Pak Mod" : "Asset Mod";

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
                    if (dirName.Equals(".DepotDownloader", StringComparison.OrdinalIgnoreCase) ||
                        dirName.Equals("~mods", StringComparison.OrdinalIgnoreCase) ||
                        dirName.Equals("mods", StringComparison.OrdinalIgnoreCase)) continue;

                    bool isEnabled = !dirName.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase);
                    string cleanDirName = isEnabled ? dirName : dirName.Substring(0, dirName.Length - 9);

                    string displayName = CleanModName(cleanDirName);
                    string? author = null;
                    string? description = null;
                    string category = "Mod Package";
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
            _logger.LogError(ex, "Failed to read installed mods for {Game}", instance.Name);
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
                Directory.CreateDirectory(modsDir);

                var ext = Path.GetExtension(sourceFilePath).ToLowerInvariant();
                if (ext == ".zip")
                {
                    using var archive = ZipFile.OpenRead(sourceFilePath);
                    foreach (var entry in archive.Entries)
                    {
                        var entryExt = Path.GetExtension(entry.Name).ToLowerInvariant();
                        if (entryExt is ".pak" or ".ucas" or ".utoc")
                        {
                            var dest = Path.Combine(modsDir, entry.Name);
                            entry.ExtractToFile(dest, overwrite: true);
                        }
                    }
                    return true;
                }
                else if (ext is ".pak" or ".ucas" or ".utoc")
                {
                    var dest = Path.Combine(modsDir, Path.GetFileName(sourceFilePath));
                    File.Copy(sourceFilePath, dest, overwrite: true);
                    return true;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to install mod {Path} for {Game}", sourceFilePath, instance.Name);
            }

            return false;
        }, ct).ConfigureAwait(false);
    }

    public Task<bool> UninstallModAsync(GameInstance instance, string modId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(instance.InstallPath)) return Task.FromResult(false);

        var candidateDirs = new List<string> { GetModsDirectory(instance), Path.Combine(instance.InstallPath, "Mods"), Path.Combine(instance.InstallPath, "mods") };
        var paksDirs = Directory.Exists(instance.InstallPath) ? Directory.GetDirectories(instance.InstallPath, "Paks", SearchOption.AllDirectories) : [];
        foreach (var p in paksDirs) candidateDirs.Add(Path.Combine(p, "~mods"));

        try
        {
            foreach (var modsDir in candidateDirs.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(modsDir) || !Directory.Exists(modsDir)) continue;

                var target = Path.Combine(modsDir, modId);
                if (File.Exists(target))
                {
                    File.Delete(target);
                    return Task.FromResult(true);
                }
                if (Directory.Exists(target))
                {
                    Directory.Delete(target, recursive: true);
                    return Task.FromResult(true);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete mod {Id}", modId);
        }

        return Task.FromResult(false);
    }

    public Task<bool> ToggleModAsync(GameInstance instance, string modId, bool isEnabled, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(instance.InstallPath)) return Task.FromResult(false);

        var candidateDirs = new List<string> { GetModsDirectory(instance), Path.Combine(instance.InstallPath, "Mods"), Path.Combine(instance.InstallPath, "mods") };
        var paksDirs = Directory.Exists(instance.InstallPath) ? Directory.GetDirectories(instance.InstallPath, "Paks", SearchOption.AllDirectories) : [];
        foreach (var p in paksDirs) candidateDirs.Add(Path.Combine(p, "~mods"));

        try
        {
            foreach (var modsDir in candidateDirs.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(modsDir) || !Directory.Exists(modsDir)) continue;

                var currentPath = Path.Combine(modsDir, modId);
                if (!File.Exists(currentPath) && !Directory.Exists(currentPath)) continue;

                string targetPath;
                if (isEnabled)
                {
                    // Remove .disabled
                    if (currentPath.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase))
                        targetPath = currentPath.Substring(0, currentPath.Length - 9);
                    else
                        return Task.FromResult(true);
                }
                else
                {
                    // Add .disabled
                    if (!currentPath.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase))
                        targetPath = currentPath + ".disabled";
                    else
                        return Task.FromResult(true);
                }

                if (File.Exists(currentPath)) File.Move(currentPath, targetPath, overwrite: true);
                else if (Directory.Exists(currentPath)) Directory.Move(currentPath, targetPath);

                return Task.FromResult(true);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to toggle mod {Id}", modId);
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

    private static string CleanModName(string raw)
    {
        var name = Path.GetFileNameWithoutExtension(raw);
        if (name.StartsWith("_") || name.StartsWith("~"))
            name = name.TrimStart('_', '~', ' ');
        return name;
    }
}
