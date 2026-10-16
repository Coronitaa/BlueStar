using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using BlueStar.Core.Helpers;
using BlueStar.Core.Interfaces;
using BlueStar.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BlueStar.Infrastructure.Services;

/// <summary>
/// Harvester provider that triggers Steam client console download command (download_manifest)
/// and monitors the Steam depotcache directory using FileSystemWatcher.
/// </summary>
public sealed class SteamClientManifestHarvesterProvider : IManifestHarvesterProvider
{
    private static readonly byte[] SteamProtobufMagic = [0xD0, 0x17, 0xF6, 0x71];

    private readonly string? _steamPath;
    private readonly Func<string, string, bool>? _commandLauncher;
    private readonly TimeSpan _timeout;
    private readonly ILogger<SteamClientManifestHarvesterProvider> _logger;

    public string Name => "SteamClient";
    public int Priority => 300;

    public SteamClientManifestHarvesterProvider(
        string? steamPath = null,
        Func<string, string, bool>? commandLauncher = null,
        TimeSpan? timeout = null,
        ILogger<SteamClientManifestHarvesterProvider>? logger = null)
    {
        _steamPath = steamPath;
        _commandLauncher = commandLauncher;
        _timeout = timeout ?? TimeSpan.FromSeconds(15);
        _logger = logger ?? NullLogger<SteamClientManifestHarvesterProvider>.Instance;
    }

    private string? GetEffectiveSteamPath()
    {
        if (!string.IsNullOrWhiteSpace(_steamPath) && Directory.Exists(_steamPath))
        {
            return _steamPath;
        }
        return ShortcutHelper.GetSteamPath();
    }

    public Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        var steamPath = GetEffectiveSteamPath();
        if (string.IsNullOrWhiteSpace(steamPath))
        {
            return Task.FromResult(false);
        }

        var steamExe = Path.Combine(steamPath, "steam.exe");
        var available = File.Exists(steamExe) || _commandLauncher != null;
        return Task.FromResult(available);
    }

    public async Task<HarvestResult> HarvestManifestAsync(uint appId, uint depotId, ulong manifestId, CancellationToken ct = default)
    {
        var steamPath = GetEffectiveSteamPath();
        if (string.IsNullOrWhiteSpace(steamPath))
        {
            return HarvestResult.Failed("Steam installation directory not found.", Name);
        }

        var depotCacheDir = Path.Combine(steamPath, "depotcache");
        Directory.CreateDirectory(depotCacheDir);

        var manifestFileName = $"{depotId}_{manifestId}.manifest";
        var targetFilePath = Path.Combine(depotCacheDir, manifestFileName);

        // 1. Check if already present in depotcache
        if (File.Exists(targetFilePath) && ValidateManifestHeader(targetFilePath))
        {
            _logger.LogInformation("Manifest {FileName} already present in depotcache", manifestFileName);
            return HarvestResult.Success(targetFilePath, Name);
        }

        // 2. Setup FileSystemWatcher
        using var tcs = new CancellationTokenSource(_timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, tcs.Token);
        var fileDetectedTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        using var watcher = new FileSystemWatcher(depotCacheDir, manifestFileName)
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
            EnableRaisingEvents = true
        };

        FileSystemEventHandler onChangeOrCreated = (s, e) =>
        {
            if (File.Exists(targetFilePath))
            {
                fileDetectedTcs.TrySetResult(true);
            }
        };

        watcher.Created += onChangeOrCreated;
        watcher.Changed += onChangeOrCreated;

        // 3. Trigger Steam download_manifest command
        var commandArgs = $"-console +download_manifest {appId} {depotId} {manifestId}";
        try
        {
            if (_commandLauncher != null)
            {
                _commandLauncher("steam.exe", commandArgs);
            }
            else
            {
                var steamExe = Path.Combine(steamPath, "steam.exe");
                if (File.Exists(steamExe))
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = steamExe,
                        Arguments = commandArgs,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };
                    Process.Start(psi);
                }
                else
                {
                    // Fallback to steam:// protocol URL
                    var uri = $"steam://nav/console";
                    Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to launch Steam download_manifest command for Depot {DepotId}", depotId);
        }

        // 4. Await file appearance via watcher or polling fallback
        try
        {
            using (linkedCts.Token.Register(() => fileDetectedTcs.TrySetCanceled()))
            {
                // Check if file appeared immediately
                if (File.Exists(targetFilePath))
                {
                    fileDetectedTcs.TrySetResult(true);
                }

                await fileDetectedTcs.Task.ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            if (ct.IsCancellationRequested)
            {
                throw;
            }

            // Check once more in case FileSystemWatcher missed notification
            if (File.Exists(targetFilePath) && ValidateManifestHeader(targetFilePath))
            {
                return HarvestResult.Success(targetFilePath, Name);
            }

            return HarvestResult.Failed($"Timeout waiting for Steam to download manifest {manifestFileName}", Name);
        }

        // Allow write stream to flush
        await Task.Delay(50, ct).ConfigureAwait(false);

        // 5. Verify captured file header (0x71F617D0)
        if (File.Exists(targetFilePath) && ValidateManifestHeader(targetFilePath))
        {
            _logger.LogInformation("Successfully harvested manifest {FileName} via Steam Client", manifestFileName);
            return HarvestResult.Success(targetFilePath, Name);
        }

        return HarvestResult.Failed($"Captured manifest {manifestFileName} failed protobuf header validation.", Name);
    }

    public static bool ValidateManifestHeader(string filePath)
    {
        try
        {
            if (!File.Exists(filePath)) return false;
            var fi = new FileInfo(filePath);
            if (fi.Length < 4) return false;

            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var header = new byte[4];
            var bytesRead = fs.Read(header, 0, 4);
            if (bytesRead < 4) return false;

            return header[0] == SteamProtobufMagic[0] &&
                   header[1] == SteamProtobufMagic[1] &&
                   header[2] == SteamProtobufMagic[2] &&
                   header[3] == SteamProtobufMagic[3];
        }
        catch
        {
            return false;
        }
    }
}
