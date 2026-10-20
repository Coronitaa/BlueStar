using System;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using BlueStar.Core.Helpers;
using BlueStar.Core.Interfaces;
using BlueStar.Core.Models;
using DepotDownloader;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BlueStar.Infrastructure.Services;

/// <summary>
/// Harvester provider that uses DepotDownloaderMod in-process (or fallback headless CLI)
/// to fetch manifests using active Steam session credentials.
/// </summary>
public sealed class DepotDownloaderManifestHarvesterProvider : IManifestHarvesterProvider
{
    private readonly string? _toolsDirectory;
    private readonly IManifestCacheService? _manifestCache;
    private readonly Func<uint, uint, ulong, string, CancellationToken, Task<string?>>? _downloadRunner;
    private readonly ISteamSessionCookieProvider? _sessionCookieProvider;
    private readonly ILogger<DepotDownloaderManifestHarvesterProvider> _logger;

    public string Name => "DepotDownloaderMod";
    public int Priority => 300;

    public DepotDownloaderManifestHarvesterProvider(
        string? toolsDirectory = null,
        IManifestCacheService? manifestCache = null,
        Func<uint, uint, ulong, string, CancellationToken, Task<string?>>? downloadRunner = null,
        ISteamSessionCookieProvider? sessionCookieProvider = null,
        ILogger<DepotDownloaderManifestHarvesterProvider>? logger = null)
    {
        _toolsDirectory = toolsDirectory;
        _manifestCache = manifestCache;
        _downloadRunner = downloadRunner;
        _sessionCookieProvider = sessionCookieProvider;
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
            Path.Combine(Directory.GetCurrentDirectory(), "src", "BlueStar.App", "tools"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BlueStar", "tools"),
            @"C:\Users\Valen\Documents\BLUESTAR 1.3\src\BlueStar.App\tools",
            @"C:\Users\Valen\Documents\BLUESTAR 1.3\dist\publish\tools"
        };

        foreach (var dir in dirsToSearch)
        {
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) continue;

            var exe = Path.Combine(dir, "DepotDownloaderMod.exe");
            if (File.Exists(exe)) return exe;

            var standardExe = Path.Combine(dir, "DepotDownloader.exe");
            if (File.Exists(standardExe)) return standardExe;

            var dll = Path.Combine(dir, "DepotDownloaderMod.dll");
            if (File.Exists(dll)) return dll;
        }

        return null;
    }

    public Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        // ContentDownloader in-process engine is always compiled and available
        return Task.FromResult(true);
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

        // 3. Primary Execution: Headless in-process SteamKit2 download using authenticated Steam session token
        string? username = null;
        string? accessToken = null;

        if (_sessionCookieProvider != null)
        {
            try
            {
                var cookies = await _sessionCookieProvider.GetSteamSessionCookiesAsync(ct).ConfigureAwait(false);
                if (cookies != null && cookies.TryGetValue("steamLoginSecure", out var secureVal) && !string.IsNullOrWhiteSpace(secureVal))
                {
                    var decoded = Uri.UnescapeDataString(secureVal);
                    var sep = decoded.Contains("||") ? "||" : (decoded.Contains("%7C%7C") ? "%7C%7C" : null);
                    if (sep != null)
                    {
                        var parts = decoded.Split(sep, 2, StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length == 2)
                        {
                            var steamId = parts[0].Trim();
                            accessToken = parts[1].Trim();
                            username = ResolveUsernameFromLoginUsers(steamId);
                        }
                    }
                    else
                    {
                        accessToken = decoded.Trim();
                        username = ResolveUsernameFromLoginUsers(null);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to resolve active Steam session cookies for DepotDownloaderMod");
            }
        }

        try
        {
            var inProcessPath = await ContentDownloader.DownloadManifestInProcessAsync(
                appId, depotId, manifestId, tempOutputDir, username, accessToken, ct).ConfigureAwait(false);

            if (!string.IsNullOrWhiteSpace(inProcessPath) && File.Exists(inProcessPath) && SteamClientManifestHarvesterProvider.ValidateManifestHeader(inProcessPath))
            {
                if (_manifestCache != null)
                {
                    try
                    {
                        await _manifestCache.StoreManifestFileAsync(depotId, manifestId, inProcessPath, ct).ConfigureAwait(false);
                    }
                    catch { }
                }

                _logger.LogInformation("Successfully harvested manifest {DepotId}_{ManifestId} via in-process SteamKit2 backend", depotId, manifestId);
                return HarvestResult.Success(inProcessPath, Name);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "In-process SteamKit2 manifest download failed for Depot {DepotId}", depotId);
        }

        // 4. Secondary Fallback: launch DepotDownloaderMod binary with -manifest-only
        var binary = FindDepotDownloaderExecutable();
        if (string.IsNullOrWhiteSpace(binary))
        {
            return HarvestResult.Failed("In-process Steam download failed and DepotDownloaderMod executable not found.", Name);
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
            string stdout = string.Empty;
            string stderr = string.Empty;
            if (process != null)
            {
                var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
                var stderrTask = process.StandardError.ReadToEndAsync(ct);
                await process.WaitForExitAsync(ct).ConfigureAwait(false);
                stdout = await stdoutTask.ConfigureAwait(false);
                stderr = await stderrTask.ConfigureAwait(false);
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

            var errSnippet = !string.IsNullOrWhiteSpace(stderr) ? stderr : stdout;
            if (errSnippet.Length > 200) errSnippet = errSnippet[..200];
            _logger.LogWarning("DepotDownloaderMod failed for Depot {DepotId}. Output: {Output}", depotId, errSnippet);

            return HarvestResult.Failed($"DepotDownloaderMod did not produce a valid manifest for Depot {depotId}: {errSnippet}", Name);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to execute DepotDownloaderMod for Depot {DepotId}", depotId);
            return HarvestResult.Failed(ex.Message, Name);
        }
    }

    private static string? ResolveUsernameFromLoginUsers(string? steamId)
    {
        try
        {
            var steamPath = ShortcutHelper.GetSteamPath();
            if (string.IsNullOrWhiteSpace(steamPath)) return null;

            var loginUsersFile = Path.Combine(steamPath, "config", "loginusers.vdf");
            if (!File.Exists(loginUsersFile)) return null;

            var content = File.ReadAllText(loginUsersFile);
            if (!string.IsNullOrWhiteSpace(steamId))
            {
                var match = Regex.Match(content, $@"""{Regex.Escape(steamId)}""\s*\{{[^}}]+""AccountName""\s*""([^""]+)""", RegexOptions.IgnoreCase | RegexOptions.Singleline);
                if (match.Success && match.Groups.Count > 1)
                {
                    return match.Groups[1].Value;
                }
            }

            // Fallback: look for AutoLogin "1" or MostRecent "1"
            var autoLoginMatch = Regex.Match(content, @"""AccountName""\s*""([^""]+)""[^}}]+""AutoLogin""\s*""1""", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            if (autoLoginMatch.Success && autoLoginMatch.Groups.Count > 1)
            {
                return autoLoginMatch.Groups[1].Value;
            }

            var recentMatch = Regex.Match(content, @"""AccountName""\s*""([^""]+)""[^}}]+""MostRecent""\s*""1""", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            if (recentMatch.Success && recentMatch.Groups.Count > 1)
            {
                return recentMatch.Groups[1].Value;
            }
        }
        catch { }

        return null;
    }
}
