using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using BlueStar.Core.Interfaces;
using BlueStar.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BlueStar.Infrastructure.Services;

/// <summary>
/// Harvester provider that uses DepotDownloaderMod (-manifest-only) to fetch manifests.
/// </summary>
public sealed class DepotDownloaderManifestHarvesterProvider : IManifestHarvesterProvider
{
    private readonly string? _toolsDirectory;
    private readonly IManifestCacheService? _manifestCache;
    private readonly Func<uint, uint, ulong, string, CancellationToken, Task<string?>>? _downloadRunner;
    private readonly ILogger<DepotDownloaderManifestHarvesterProvider> _logger;

    public string Name => "DepotDownloaderMod";
    public int Priority => 200;

    public DepotDownloaderManifestHarvesterProvider(
        string? toolsDirectory = null,
        IManifestCacheService? manifestCache = null,
        Func<uint, uint, ulong, string, CancellationToken, Task<string?>>? downloadRunner = null,
        ILogger<DepotDownloaderManifestHarvesterProvider>? logger = null)
    {
        _toolsDirectory = toolsDirectory;
        _manifestCache = manifestCache;
        _downloadRunner = downloadRunner;
        _logger = logger ?? NullLogger<DepotDownloaderManifestHarvesterProvider>.Instance;
    }

    private string? FindDepotDownloaderExecutable()
    {
        var dirsToSearch = new[]
        {
            _toolsDirectory,
            Path.Combine(AppContext.BaseDirectory, "tools"),
            AppContext.BaseDirectory,
            Path.Combine(Directory.GetCurrentDirectory(), "tools"),
            Path.Combine(Directory.GetCurrentDirectory(), "src", "BlueStar.App", "tools")
        };

        foreach (var dir in dirsToSearch)
        {
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) continue;

            var exe = Path.Combine(dir, "DepotDownloaderMod.exe");
            if (File.Exists(exe)) return exe;

            var dll = Path.Combine(dir, "DepotDownloaderMod.dll");
            if (File.Exists(dll)) return dll;
        }

        return null;
    }

    public Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        if (_downloadRunner != null) return Task.FromResult(true);
        var binary = FindDepotDownloaderExecutable();
        return Task.FromResult(!string.IsNullOrWhiteSpace(binary));
    }

    public async Task<HarvestResult> HarvestManifestAsync(uint appId, uint depotId, ulong manifestId, CancellationToken ct = default)
    {
        // 1. Check local manifest cache first
        if (_manifestCache != null)
        {
            var cachedPath = _manifestCache.GetManifestPath(depotId, manifestId);
            if (File.Exists(cachedPath) && SteamClientManifestHarvesterProvider.ValidateManifestHeader(cachedPath))
            {
                _logger.LogInformation("DepotDownloaderMod: Manifest {DepotId}_{ManifestId} already available in local cache", depotId, manifestId);
                return HarvestResult.Success(cachedPath, Name);
            }
        }

        var tempOutputDir = Path.Combine(Path.GetTempPath(), "BlueStar_DepotDownloader_Harvest", $"{depotId}_{manifestId}");
        Directory.CreateDirectory(tempOutputDir);

        var expectedFile = Path.Combine(tempOutputDir, $"{depotId}_{manifestId}.manifest");

        // 2. Custom runner / delegate if injected
        if (_downloadRunner != null)
        {
            try
            {
                var customPath = await _downloadRunner(appId, depotId, manifestId, tempOutputDir, ct).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(customPath) && File.Exists(customPath) && SteamClientManifestHarvesterProvider.ValidateManifestHeader(customPath))
                {
                    return HarvestResult.Success(customPath, Name);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Custom DepotDownloader runner failed for Depot {DepotId}", depotId);
                return HarvestResult.Failed(ex.Message, Name);
            }
        }

        // 3. Fallback: launch DepotDownloaderMod binary with -manifest-only
        var binary = FindDepotDownloaderExecutable();
        if (string.IsNullOrWhiteSpace(binary))
        {
            return HarvestResult.Failed("DepotDownloaderMod executable not found.", Name);
        }

        try
        {
            var isDll = binary.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);
            var psi = new ProcessStartInfo
            {
                FileName = isDll ? "dotnet" : binary,
                Arguments = isDll
                    ? $"\"{binary}\" -app {appId} -depot {depotId} -manifest {manifestId} -manifest-only -dir \"{tempOutputDir}\""
                    : $"-app {appId} -depot {depotId} -manifest {manifestId} -manifest-only -dir \"{tempOutputDir}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var process = Process.Start(psi);
            if (process != null)
            {
                await process.WaitForExitAsync(ct).ConfigureAwait(false);
            }

            if (File.Exists(expectedFile) && SteamClientManifestHarvesterProvider.ValidateManifestHeader(expectedFile))
            {
                return HarvestResult.Success(expectedFile, Name);
            }

            // Also check any .manifest file in the output dir
            var files = Directory.GetFiles(tempOutputDir, "*.manifest");
            foreach (var f in files)
            {
                if (SteamClientManifestHarvesterProvider.ValidateManifestHeader(f))
                {
                    return HarvestResult.Success(f, Name);
                }
            }

            return HarvestResult.Failed($"DepotDownloaderMod did not produce a valid manifest for Depot {depotId}.", Name);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to execute DepotDownloaderMod for Depot {DepotId}", depotId);
            return HarvestResult.Failed(ex.Message, Name);
        }
    }
}
