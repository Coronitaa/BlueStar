using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using BlueStar.Core.Interfaces;
using BlueStar.Core.Models;
using BlueStar.Infrastructure.Common;
using Microsoft.Extensions.Logging;

namespace BlueStar.Infrastructure.Admin;

/// <summary>
/// After a preset install finishes downloading, lays down its custom emulator and mods. The preset is
/// looked up again in the current (validated) catalog by id, so nothing from the network is trusted
/// just because it was stored on the instance earlier.
/// </summary>
public sealed class AdminPresetDeployer : IAdminPresetDeployer
{
    private const long MaxDownloadBytes = 4L * 1024 * 1024 * 1024;

    private readonly HttpClient _http;
    private readonly IAdminCatalogService _catalog;
    private readonly IModManagerRegistry? _modRegistry;
    private readonly ILogger<AdminPresetDeployer> _logger;

    public AdminPresetDeployer(HttpClient http, IAdminCatalogService catalog, ILogger<AdminPresetDeployer> logger, IModManagerRegistry? modRegistry = null)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _modRegistry = modRegistry;
    }

    public async Task<bool> DeployPendingAsync(GameInstance instance, IProgress<DeployProgress>? progress = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(instance);
        if (string.IsNullOrWhiteSpace(instance.PendingAdminPresetId)) return true;
        if (string.IsNullOrWhiteSpace(instance.InstallPath) || !Directory.Exists(instance.InstallPath)) return false;

        var presets = await _catalog.GetPresetsAsync(instance.AppId, ct).ConfigureAwait(false);
        var preset = presets.FirstOrDefault(p => p.Id == instance.PendingAdminPresetId);
        if (preset == null)
        {
            _logger.LogWarning("Preset {Preset} for {Game} is no longer in the admin catalog", instance.PendingAdminPresetId, instance.Name);
            return false;
        }

        var ok = true;
        var workDir = Path.Combine(Path.GetTempPath(), "BlueStar", "preset-" + Guid.NewGuid().ToString("N"));
        try
        {
            if (preset.Emulator is { Type: AdminEmulatorType.Custom, Download: { } emuDownload })
            {
                progress?.Report(new DeployProgress { Percentage = 20, Message = $"Installing {preset.Emulator.Name}...", CurrentStep = "Preset_Emulator" });
                ok &= await DeployCustomEmulatorAsync(instance.InstallPath, preset.Emulator, emuDownload, workDir, ct).ConfigureAwait(false);
            }

            var manager = preset.Mods.Count > 0 ? _modRegistry?.GetManagerForInstance(instance) : null;
            if (preset.Mods.Count > 0 && manager == null)
            {
                _logger.LogWarning("No mod manager for {Game}; skipping {Count} preset mods", instance.Name, preset.Mods.Count);
                ok = false;
            }

            for (var i = 0; manager != null && i < preset.Mods.Count; i++)
            {
                var mod = preset.Mods[i];
                progress?.Report(new DeployProgress { Percentage = 40 + 50 * i / preset.Mods.Count, Message = $"Installing mod {mod.Name}...", CurrentStep = "Preset_Mods" });
                try
                {
                    var file = await DownloadVerifiedAsync(mod, workDir, ct).ConfigureAwait(false);
                    ok &= await manager.InstallModAsync(instance, file, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or TaskCanceledException)
                {
                    _logger.LogError(ex, "Preset mod {Mod} failed for {Game}", mod.Name, instance.Name);
                    ok = false;
                }
            }
        }
        finally
        {
            TryDelete(workDir);
        }

        return ok;
    }

    private async Task<bool> DeployCustomEmulatorAsync(string installPath, AdminEmulator emulator, AdminDownload download, string workDir, CancellationToken ct)
    {
        try
        {
            var archive = await DownloadVerifiedAsync(download, workDir, ct).ConfigureAwait(false);
            // ArchiveExtractor drops entries that would land outside the destination.
            ArchiveExtractor.ExtractToDirectory(archive, installPath, overwrite: true);

            if (emulator.SettingsFile != null && emulator.Settings.Count > 0)
            {
                var target = ResolveInside(installPath, emulator.SettingsFile);
                await WriteIniValuesAsync(target, emulator.Settings, ct).ConfigureAwait(false);
            }
            return true;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or TaskCanceledException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Custom emulator {Name} failed to install", emulator.Name);
            return false;
        }
    }

    /// <summary>Downloads to a fresh file and checks the pinned SHA-256 when there is one.</summary>
    internal async Task<string> DownloadVerifiedAsync(AdminDownload download, string workDir, CancellationToken ct)
    {
        // Re-checked here so a caller cannot hand in a download that skipped the parser.
        if (download.Url.Scheme != Uri.UriSchemeHttps
            || (download.Sha256 == null && !AdminCatalogParser.IsTrustedDownloadOrigin(download.Url)))
        {
            throw new InvalidDataException($"Refusing untrusted download {download.Url}");
        }

        var dir = Path.Combine(workDir, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var name = Path.GetFileName(Uri.UnescapeDataString(download.Url.AbsolutePath));
        if (!AdminCatalogParser.IsSafeRelativePath(name) || name.Contains('/')) name = "download.zip";
        var path = Path.Combine(dir, name);

        using var response = await _http.GetAsync(download.Url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > MaxDownloadBytes) throw new InvalidDataException("Download too large");

        using var sha = SHA256.Create();
        await using (var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
        await using (var output = File.Create(path))
        {
            var buffer = new byte[81920];
            long total = 0;
            int read;
            while ((read = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                total += read;
                if (total > MaxDownloadBytes) throw new InvalidDataException("Download too large");
                sha.TransformBlock(buffer, 0, read, null, 0);
                await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            }
        }
        sha.TransformFinalBlock([], 0, 0);

        var actual = Convert.ToHexString(sha.Hash!).ToLowerInvariant();
        if (download.Sha256 != null && actual != download.Sha256)
        {
            File.Delete(path);
            throw new InvalidDataException($"Checksum mismatch for {download.Name}");
        }
        return path;
    }

    /// <summary>Joins a validated relative path to the game folder and refuses anything that escapes it.</summary>
    internal static string ResolveInside(string root, string relative)
    {
        if (!AdminCatalogParser.IsSafeRelativePath(relative)) throw new InvalidDataException("Unsafe path");
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(Path.Combine(fullRoot, relative));
        if (!full.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Path escapes the game folder");
        return full;
    }

    /// <summary>Sets <c>key=value</c> lines, replacing an existing line for the same key or appending one.</summary>
    internal static async Task WriteIniValuesAsync(string path, IReadOnlyDictionary<string, string> values, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var lines = File.Exists(path) ? (await File.ReadAllLinesAsync(path, ct).ConfigureAwait(false)).ToList() : [];
        foreach (var (key, value) in values)
        {
            var idx = lines.FindIndex(l => l.TrimStart().StartsWith(key + "=", StringComparison.OrdinalIgnoreCase)
                || l.TrimStart().StartsWith(key + " =", StringComparison.OrdinalIgnoreCase));
            if (idx >= 0) lines[idx] = $"{key}={value}";
            else lines.Add($"{key}={value}");
        }
        await File.WriteAllLinesAsync(path, lines, ct).ConfigureAwait(false);
    }

    private void TryDelete(string dir)
    {
        try
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Could not clean {Dir}", dir);
        }
    }
}
