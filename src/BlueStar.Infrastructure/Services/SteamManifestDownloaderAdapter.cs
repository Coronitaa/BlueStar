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
/// Harvester adapter that supports external Steam-Manifest-Downloader scripts (.lua / .st)
/// or local Steam web sessions.
/// </summary>
public sealed class SteamManifestDownloaderAdapter : IManifestHarvesterProvider
{
    private readonly string? _scriptPath;
    private readonly ISteamSessionCookieProvider? _sessionCookieProvider;
    private readonly Func<uint, uint, ulong, string, CancellationToken, Task<string?>>? _scriptRunner;
    private readonly ILogger<SteamManifestDownloaderAdapter> _logger;

    public string Name => "SteamManifestDownloaderAdapter";
    public int Priority => 100;

    public SteamManifestDownloaderAdapter(
        string? scriptPath = null,
        ISteamSessionCookieProvider? sessionCookieProvider = null,
        Func<uint, uint, ulong, string, CancellationToken, Task<string?>>? scriptRunner = null,
        ILogger<SteamManifestDownloaderAdapter>? logger = null)
    {
        _scriptPath = scriptPath;
        _sessionCookieProvider = sessionCookieProvider;
        _scriptRunner = scriptRunner;
        _logger = logger ?? NullLogger<SteamManifestDownloaderAdapter>.Instance;
    }

    private string? FindScriptPath()
    {
        if (!string.IsNullOrWhiteSpace(_scriptPath) && File.Exists(_scriptPath))
        {
            return _scriptPath;
        }

        var candidateDirs = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "scripts"),
            Path.Combine(AppContext.BaseDirectory, "tools"),
            Path.Combine(Directory.GetCurrentDirectory(), "scripts"),
            Path.Combine(Directory.GetCurrentDirectory(), "tools")
        };

        foreach (var dir in candidateDirs)
        {
            if (!Directory.Exists(dir)) continue;

            var lua = Path.Combine(dir, "manifest_downloader.lua");
            if (File.Exists(lua)) return lua;

            var st = Path.Combine(dir, "manifest_downloader.st");
            if (File.Exists(st)) return st;
        }

        return null;
    }

    public async Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        if (_scriptRunner != null) return true;
        if (!string.IsNullOrWhiteSpace(FindScriptPath())) return true;

        if (_sessionCookieProvider != null)
        {
            try
            {
                var cookies = await _sessionCookieProvider.GetSteamSessionCookiesAsync(ct).ConfigureAwait(false);
                return cookies != null && cookies.Count > 0;
            }
            catch
            {
                return false;
            }
        }

        return false;
    }

    public async Task<HarvestResult> HarvestManifestAsync(uint appId, uint depotId, ulong manifestId, CancellationToken ct = default)
    {
        var tempOutputDir = Path.Combine(Path.GetTempPath(), "BlueStar_ScriptAdapter_Harvest", $"{depotId}_{manifestId}");
        Directory.CreateDirectory(tempOutputDir);
        var expectedFile = Path.Combine(tempOutputDir, $"{depotId}_{manifestId}.manifest");

        // 1. If custom script runner is injected, execute it
        if (_scriptRunner != null)
        {
            try
            {
                var customPath = await _scriptRunner(appId, depotId, manifestId, tempOutputDir, ct).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(customPath) && File.Exists(customPath) && SteamClientManifestHarvesterProvider.ValidateManifestHeader(customPath))
                {
                    return HarvestResult.Success(customPath, Name);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Custom script runner failed for Depot {DepotId}", depotId);
                return HarvestResult.Failed(ex.Message, Name);
            }
        }

        // 2. Locate .lua or .st script
        var script = FindScriptPath();
        if (string.IsNullOrWhiteSpace(script))
        {
            return HarvestResult.Failed("No .lua or .st manifest downloader script configured.", Name);
        }

        try
        {
            string interpreter = script.EndsWith(".lua", StringComparison.OrdinalIgnoreCase) ? "lua" : "python";
            var psi = new ProcessStartInfo
            {
                FileName = interpreter,
                Arguments = $"\"{script}\" {appId} {depotId} {manifestId} \"{tempOutputDir}\"",
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

            var files = Directory.GetFiles(tempOutputDir, "*.manifest");
            foreach (var f in files)
            {
                if (SteamClientManifestHarvesterProvider.ValidateManifestHeader(f))
                {
                    return HarvestResult.Success(f, Name);
                }
            }

            return HarvestResult.Failed($"Script {Path.GetFileName(script)} did not produce a valid manifest.", Name);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Execution failed for script {Script}", script);
            return HarvestResult.Failed(ex.Message, Name);
        }
    }
}
