using System.IO.Compression;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using BlueStar.Core.Interfaces;
using BlueStar.Core.Models;
using Microsoft.Extensions.Logging;

namespace BlueStar.Infrastructure.Dlc;

/// <summary>
/// Implements the DLC unlocker installer using SmokeAPI + cream_api.ini.
///
/// Install flow:
///   1. Locate steam_api64.dll / steam_api.dll anywhere inside the install path (recursive).
///   2. Download SmokeAPI ZIP from acidicoala GitHub Releases (cached in %AppData%\BlueStar\cache\smokeapi\).
///   3. Back up original DLL → steam_api64_o.dll (skip if already backed up).
///   4. Deploy SmokeAPI DLL as steam_api64.dll (entry name: smoke_api64.dll inside the ZIP).
///   5. Write cream_api.ini with all required sections.
///   6. Write SmokeAPI.config.json (or use the one bundled in the ZIP).
/// </summary>
public class CreamInstallerService : IDlcInstaller
{
    // ── Download info ─────────────────────────────────────────────────────────
    // CreamAPI (by deadmau5/punto, bundled in FroggMaster/CreamInstaller)
    private const string CreamApi64Url =
        "https://raw.githubusercontent.com/FroggMaster/CreamInstaller/main/CreamInstaller/Resources/CreamAPI/steam_api64.dll";
    private const string CreamApi32Url =
        "https://raw.githubusercontent.com/FroggMaster/CreamInstaller/main/CreamInstaller/Resources/CreamAPI/steam_api.dll";

    // SmokeAPI (by acidicoala)
    private const string SmokeApiApiUrl =
        "https://api.github.com/repos/acidicoala/SmokeAPI/releases/latest";

    // ── ZIP entry names (as of SmokeAPI v4.x) ────────────────────────────────
    private const string ZipEntry64 = "smoke_api64.dll";
    private const string ZipEntry32 = "smoke_api32.dll";

    // ── Steam API file names ──────────────────────────────────────────────────
    private const string SteamApi64     = "steam_api64.dll";
    private const string SteamApi64Orig = "steam_api64_o.dll";
    private const string SteamApi32     = "steam_api.dll";
    private const string SteamApi32Orig = "steam_api_o.dll";
    private const string CreamApiIni    = "cream_api.ini";
    private const string SmokeApiJson   = "SmokeAPI.config.json";

    private readonly ILogger<CreamInstallerService> _logger;
    private readonly string _smokeCacheDir;
    private readonly string _creamCacheDir;

    public CreamInstallerService(ILogger<CreamInstallerService> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        _smokeCacheDir = Path.Combine(appData, "BlueStar", "cache", "smokeapi");
        _creamCacheDir = Path.Combine(appData, "BlueStar", "cache", "creamapi");
    }

    // ── IDlcInstaller ─────────────────────────────────────────────────────────

    public async Task<bool> InstallDlcAsync(
        GameInstance instance,
        DlcInfo dlc,
        CancellationToken ct,
        IProgress<string>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(instance);

        var method = instance.DlcUnlockerMethod ?? "CreamAPI";
        _logger.LogInformation(
            "[CreamInstaller] Install started for {Game} (AppId={AppId}, Method={Method}) at '{Path}'",
            instance.Name, instance.AppId, method, instance.InstallPath);

        // If CreamAPI is selected, bypass ReFix_deploy dlc_unlocker.ps1 because that script deploys SmokeAPI with unlockall=true.
        // We use our native CreamAPI installer with exact DLC mapping.
        if (string.Equals(method, "CreamAPI", StringComparison.OrdinalIgnoreCase))
        {
            return await InstallCreamApiAsync(instance, dlc, ct, progress).ConfigureAwait(false);
        }

        // 1. Check if ReFix_deploy dlc_unlocker.ps1 script is available
        var deployPath = Emulators.ReFixEmulator.GetReFixDeployPath();
        if (!string.IsNullOrEmpty(deployPath))
        {
            var binDir = Directory.Exists(Path.Combine(deployPath, "bin")) ? Path.Combine(deployPath, "bin") : deployPath;
            var dlcScript = Path.Combine(binDir, "dlc_unlocker.ps1");
            if (File.Exists(dlcScript))
            {
                Report(progress, "🚀 Executing ReFix Suite dlc_unlocker.ps1...");
                var appIdStr = instance.AppId > 0 ? instance.AppId.ToString() : "480";
                var gameName = !string.IsNullOrWhiteSpace(instance.Name) ? instance.Name : Path.GetFileName(instance.InstallPath);
                var args = $"-NoProfile -ExecutionPolicy Bypass -File \"{dlcScript}\" -TargetDir \"{instance.InstallPath}\" -BinDir \"{binDir}\" -Action \"install\" -AppId \"{appIdStr}\" -GameName \"{gameName}\" -DLCMode \"all\"";

                var (exitCode, stdout, stderr) = await Emulators.ReFixEmulator.RunProcessAsync(
                    "powershell.exe",
                    args,
                    binDir,
                    line =>
                    {
                        _logger.LogInformation("[dlc_unlocker] {Line}", line);
                        Report(progress, line);
                    },
                    ct).ConfigureAwait(false);

                if (exitCode == 0)
                {
                    Report(progress, $"✅ DLC unlocker installed successfully via ReFix Suite ({instance.Dlcs.Count} DLC(s)).");
                    return true;
                }
                _logger.LogWarning("[CreamInstaller] dlc_unlocker.ps1 exited with code {Code}. Falling back to internal installer.", exitCode);
            }
        }

        // 2. Select internal deployment method: SmokeAPI vs CreamAPI (default)
        if (string.Equals(method, "SmokeAPI", StringComparison.OrdinalIgnoreCase))
        {
            return await InstallSmokeApiAsync(instance, dlc, ct, progress).ConfigureAwait(false);
        }

        return await InstallCreamApiAsync(instance, dlc, ct, progress).ConfigureAwait(false);
    }

    private async Task<bool> InstallCreamApiAsync(
        GameInstance instance,
        DlcInfo dlc,
        CancellationToken ct,
        IProgress<string>? progress = null)
    {
        return await Task.Run(async () =>
        {
            try
            {
                Report(progress, "🔍 Searching for Steam API DLLs in game folder...");
                var gameDir = FindGameDirectory(instance.InstallPath, out bool has64, out bool has32);
                if (gameDir is null)
                {
                    Report(progress, "⚠ Steam API DLL not found — configuration files will be written regardless.");
                    _logger.LogWarning("[CreamInstaller] No steam_api DLL found under '{Path}'", instance.InstallPath);
                    gameDir = instance.InstallPath;
                    has64 = true; // default to x64
                }
                else
                {
                    _logger.LogInformation("[CreamInstaller] Game dir: {Dir} (x64={x64}, x32={x32})", gameDir, has64, has32);
                }

                // Deploy 64-bit CreamAPI DLL
                if (has64)
                {
                    var origDll = Path.Combine(gameDir, SteamApi64);
                    var bakDll  = Path.Combine(gameDir, SteamApi64Orig);

                    Report(progress, "💾 Backing up original steam_api64.dll...");
                    BackupDll(origDll, bakDll);

                    Report(progress, "🔧 Deploying CreamAPI (64-bit)...");
                    string creamDll64 = await EnsureCreamApiDllAsync(is64: true, ct, progress).ConfigureAwait(false);
                    File.Copy(creamDll64, origDll, overwrite: true);
                    _logger.LogInformation("[CreamInstaller] Deployed CreamAPI x64 → {Dll}", origDll);
                }

                // Deploy 32-bit CreamAPI DLL
                if (has32)
                {
                    var origDll = Path.Combine(gameDir, SteamApi32);
                    var bakDll  = Path.Combine(gameDir, SteamApi32Orig);

                    Report(progress, "💾 Backing up original steam_api.dll...");
                    BackupDll(origDll, bakDll);

                    Report(progress, "🔧 Deploying CreamAPI (32-bit)...");
                    string creamDll32 = await EnsureCreamApiDllAsync(is64: false, ct, progress).ConfigureAwait(false);
                    File.Copy(creamDll32, origDll, overwrite: true);
                    _logger.LogInformation("[CreamInstaller] Deployed CreamAPI x86 → {Dll}", origDll);
                }

                // Clean up conflicting SmokeAPI files if switching from SmokeAPI
                foreach (var cfg in new[] { SmokeApiJson, "SmokeAPI.json", "SmokeAPI.log", "SmokeAPI.cache.json" })
                {
                    var p = Path.Combine(gameDir, cfg);
                    if (File.Exists(p)) try { File.Delete(p); } catch { }
                }

                // Write standard cream_api.ini
                Report(progress, "📝 Generating cream_api.ini...");
                int dlcCount = WriteCreamApiIni(gameDir, instance, dlc);

                Report(progress, $"✅ DLC unlocker (CreamAPI) installed successfully ({dlcCount} DLC(s)).");
                _logger.LogInformation("[CreamInstaller] Done CreamAPI — {Game} @ {Dir} ({Count} DLCs)", instance.Name, gameDir, dlcCount);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[CreamInstaller] CreamAPI install failed for {Game}", instance.Name);
                Report(progress, $"❌ Error during CreamAPI installation: {ex.Message}");
                return false;
            }
        }, ct).ConfigureAwait(false);
    }

    private async Task<bool> InstallSmokeApiAsync(
        GameInstance instance,
        DlcInfo dlc,
        CancellationToken ct,
        IProgress<string>? progress = null)
    {
        return await Task.Run(async () =>
        {
            try
            {
                Report(progress, "🔍 Searching for Steam API DLLs in game folder...");
                var gameDir = FindGameDirectory(instance.InstallPath, out bool has64, out bool has32);
                if (gameDir is null)
                {
                    Report(progress, "⚠ Steam API DLL not found — configuration files will be written regardless.");
                    _logger.LogWarning("[CreamInstaller] No steam_api DLL found under '{Path}'", instance.InstallPath);
                    gameDir = instance.InstallPath;
                    has64 = true;
                }
                else
                {
                    _logger.LogInformation("[CreamInstaller] Game dir: {Dir} (x64={x64}, x32={x32})", gameDir, has64, has32);
                }

                // Download SmokeAPI ZIP
                string zipPath = await EnsureSmokeApiZipAsync(ct, progress).ConfigureAwait(false);

                // Deploy 64-bit DLL
                if (has64)
                {
                    var origDll = Path.Combine(gameDir, SteamApi64);
                    var bakDll  = Path.Combine(gameDir, SteamApi64Orig);

                    Report(progress, "💾 Backing up original steam_api64.dll...");
                    BackupDll(origDll, bakDll);

                    Report(progress, "🔧 Deploying SmokeAPI (64-bit)...");
                    ExtractEntry(zipPath, ZipEntry64, origDll);
                    _logger.LogInformation("[CreamInstaller] Deployed smoke_api64 → {Dll}", origDll);
                }

                // Deploy 32-bit DLL
                if (has32)
                {
                    var origDll = Path.Combine(gameDir, SteamApi32);
                    var bakDll  = Path.Combine(gameDir, SteamApi32Orig);

                    Report(progress, "💾 Backing up original steam_api.dll...");
                    BackupDll(origDll, bakDll);

                    Report(progress, "🔧 Deploying SmokeAPI (32-bit)...");
                    ExtractEntry(zipPath, ZipEntry32, origDll);
                    _logger.LogInformation("[CreamInstaller] Deployed smoke_api32 → {Dll}", origDll);
                }

                // Write configs
                Report(progress, "📝 Generating cream_api.ini...");
                WriteCreamApiIni(gameDir, instance);

                Report(progress, "📝 Generating SmokeAPI.config.json...");
                WriteSmokeApiConfig(gameDir);

                Report(progress, $"✅ DLC unlocker (SmokeAPI) installed successfully ({instance.Dlcs.Count} DLC(s)).");
                _logger.LogInformation("[CreamInstaller] Done SmokeAPI — {Game} @ {Dir}", instance.Name, gameDir);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[CreamInstaller] SmokeAPI install failed for {Game}", instance.Name);
                Report(progress, $"❌ Error during SmokeAPI installation: {ex.Message}");
                return false;
            }
        }, ct).ConfigureAwait(false);
    }

    public async Task<bool> UninstallDlcAsync(
        GameInstance instance,
        DlcInfo dlc,
        CancellationToken ct,
        IProgress<string>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(instance);
        try
        {
            var deployPath = Emulators.ReFixEmulator.GetReFixDeployPath();
            if (!string.IsNullOrEmpty(deployPath))
            {
                var binDir = Directory.Exists(Path.Combine(deployPath, "bin")) ? Path.Combine(deployPath, "bin") : deployPath;
                var dlcScript = Path.Combine(binDir, "dlc_unlocker.ps1");
                if (File.Exists(dlcScript))
                {
                    Report(progress, "🗑 Restoring files via ReFix Suite dlc_unlocker.ps1...");
                    var args = $"-NoProfile -ExecutionPolicy Bypass -File \"{dlcScript}\" -TargetDir \"{instance.InstallPath}\" -BinDir \"{binDir}\" -Action \"uninstall\"";

                    await Emulators.ReFixEmulator.RunProcessAsync(
                        "powershell.exe",
                        args,
                        binDir,
                        line => _logger.LogInformation("[dlc_unlocker] {Line}", line),
                        ct).ConfigureAwait(false);
                }
            }

            var gameDir = FindGameDirectory(instance.InstallPath, out _, out _) ?? instance.InstallPath;

            RestoreDll(gameDir, SteamApi64, SteamApi64Orig, progress, "steam_api64.dll");
            RestoreDll(gameDir, SteamApi32, SteamApi32Orig, progress, "steam_api.dll");

            Report(progress, "🗑 Removing configuration files...");
            foreach (var cfg in new[] { CreamApiIni, SmokeApiJson, "SmokeAPI.json",
                                         "SmokeAPI.log", "SmokeAPI.cache.json" })
            {
                var p = Path.Combine(gameDir, cfg);
                if (File.Exists(p)) File.Delete(p);
            }

            Report(progress, "✅ DLC unlocker uninstalled successfully.");
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[CreamInstaller] Uninstall failed for {Game}", instance.Name);
            Report(progress, $"❌ Error during uninstall: {ex.Message}");
            return false;
        }
    }

    public Task<bool> IsDlcInstalledAsync(GameInstance instance, DlcInfo dlc, CancellationToken ct)
    {
        if (instance is null || string.IsNullOrWhiteSpace(instance.InstallPath) || !Directory.Exists(instance.InstallPath))
            return Task.FromResult(false);

        try
        {
            var hasBak64 = Directory.GetFiles(instance.InstallPath, SteamApi64Orig, SearchOption.AllDirectories).Length > 0;
            var hasBak32 = Directory.GetFiles(instance.InstallPath, SteamApi32Orig, SearchOption.AllDirectories).Length > 0;
            var hasCream = Directory.GetFiles(instance.InstallPath, CreamApiIni, SearchOption.AllDirectories).Length > 0;
            var hasSmoke = Directory.GetFiles(instance.InstallPath, SmokeApiJson, SearchOption.AllDirectories).Length > 0;

            return Task.FromResult((hasBak64 || hasBak32) && (hasCream || hasSmoke));
        }
        catch
        {
            var gameDir = FindGameDirectory(instance.InstallPath, out _, out _) ?? instance.InstallPath;
            bool hasBak = File.Exists(Path.Combine(gameDir, SteamApi64Orig)) || File.Exists(Path.Combine(gameDir, SteamApi32Orig));
            bool hasCfg = File.Exists(Path.Combine(gameDir, CreamApiIni)) || File.Exists(Path.Combine(gameDir, SmokeApiJson));
            return Task.FromResult(hasBak && hasCfg);
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>Recursively searches for the first folder under <paramref name="root"/> that has steam_api*.dll.</summary>
    private static string? FindGameDirectory(string root, out bool has64, out bool has32)
    {
        has64 = has32 = false;
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return null;

        // Check root first, then all sub-directories (depth-first, limited to 60 dirs to avoid runaway scans)
        var dirs = new List<string> { root };
        dirs.AddRange(Directory.GetDirectories(root, "*", SearchOption.AllDirectories).Take(60));

        foreach (var dir in dirs)
        {
            bool d64 = File.Exists(Path.Combine(dir, SteamApi64));
            bool d32 = File.Exists(Path.Combine(dir, SteamApi32));
            if (d64 || d32)
            {
                has64 = d64;
                has32 = d32;
                return dir;
            }
        }
        return null;
    }

    /// <summary>
    /// Downloads the CreamAPI DLL (64-bit or 32-bit) from FroggMaster/CreamInstaller repo,
    /// caches it locally, and returns its path.
    /// </summary>
    private async Task<string> EnsureCreamApiDllAsync(bool is64, CancellationToken ct, IProgress<string>? progress)
    {
        Directory.CreateDirectory(_creamCacheDir);

        var fileName = is64 ? SteamApi64 : SteamApi32;
        var cachedPath = Path.Combine(_creamCacheDir, fileName);

        if (File.Exists(cachedPath) && new FileInfo(cachedPath).Length > 10240)
        {
            _logger.LogDebug("[CreamInstaller] Using cached CreamAPI DLL: {Path}", cachedPath);
            return cachedPath;
        }

        var downloadUrl = is64 ? CreamApi64Url : CreamApi32Url;
        var archLabel = is64 ? "64-bit" : "32-bit";

        Report(progress, $"⬇ Downloading CreamAPI {archLabel} DLL (FroggMaster/CreamInstaller)...");
        _logger.LogInformation("[CreamInstaller] Downloading CreamAPI {Arch} from {Url}", archLabel, downloadUrl);

        using var http = new HttpClient();
        http.DefaultRequestHeaders.UserAgent.ParseAdd("BlueStar-Launcher/1.0");
        http.Timeout = TimeSpan.FromMinutes(2);

        var bytes = await http.GetByteArrayAsync(downloadUrl, ct).ConfigureAwait(false);
        await File.WriteAllBytesAsync(cachedPath, bytes, ct).ConfigureAwait(false);

        _logger.LogInformation("[CreamInstaller] Cached CreamAPI {Arch} DLL at {Path} ({Kb:F0} KB)",
            archLabel, cachedPath, bytes.Length / 1024.0);
        return cachedPath;
    }

    /// <summary>
    /// Downloads the SmokeAPI ZIP from GitHub Releases via the API (to get the versioned filename),
    /// caches it locally, and returns its path.
    /// </summary>
    private async Task<string> EnsureSmokeApiZipAsync(CancellationToken ct, IProgress<string>? progress)
    {
        Directory.CreateDirectory(_smokeCacheDir);

        // Check for any previously cached ZIP
        var cached = Directory.GetFiles(_smokeCacheDir, "SmokeAPI-v*.zip").FirstOrDefault();
        if (cached is not null && new FileInfo(cached).Length > 0)
        {
            _logger.LogDebug("[CreamInstaller] Using cached ZIP: {Path}", cached);
            return cached;
        }

        // Resolve the download URL from the GitHub API
        Report(progress, "🌐 Resolving SmokeAPI download URL...");
        using var http = new HttpClient();
        http.DefaultRequestHeaders.UserAgent.ParseAdd("BlueStar-Launcher/1.0");
        http.Timeout = TimeSpan.FromMinutes(3);

        var apiJson = await http.GetStringAsync(SmokeApiApiUrl, ct).ConfigureAwait(false);

        // Parse the browser_download_url for the .zip asset
        using var doc = JsonDocument.Parse(apiJson);
        string? downloadUrl = null;
        string? tagName     = null;

        if (doc.RootElement.TryGetProperty("tag_name", out var tag))
            tagName = tag.GetString();

        if (doc.RootElement.TryGetProperty("assets", out var assets))
        {
            foreach (var asset in assets.EnumerateArray())
            {
                var name = asset.TryGetProperty("name", out var n) ? n.GetString() : null;
                if (name is not null && name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    if (asset.TryGetProperty("browser_download_url", out var u))
                        downloadUrl = u.GetString();
                    break;
                }
            }
        }

        if (downloadUrl is null)
            throw new InvalidOperationException("ZIP release asset not found in SmokeAPI release.");

        Report(progress, $"⬇ Downloading SmokeAPI {tagName ?? "latest"} from GitHub...");
        _logger.LogInformation("[CreamInstaller] Downloading SmokeAPI from {Url}", downloadUrl);

        var bytes    = await http.GetByteArrayAsync(downloadUrl, ct).ConfigureAwait(false);
        var zipName  = Path.GetFileName(downloadUrl);
        var zipPath  = Path.Combine(_smokeCacheDir, zipName);
        await File.WriteAllBytesAsync(zipPath, bytes, ct).ConfigureAwait(false);

        _logger.LogInformation("[CreamInstaller] Cached ZIP at {Path} ({Kb:F0} KB)", zipPath, bytes.Length / 1024.0);
        return zipPath;
    }

    private static void ExtractEntry(string zipPath, string entryName, string outputPath)
    {
        using var archive = ZipFile.OpenRead(zipPath);
        var entry = archive.GetEntry(entryName)
                 ?? archive.Entries.FirstOrDefault(e =>
                        string.Equals(e.Name, entryName, StringComparison.OrdinalIgnoreCase));

        if (entry is null)
        {
            var available = string.Join(", ", archive.Entries.Select(e => e.Name));
            throw new FileNotFoundException(
                $"Entry '{entryName}' not found in ZIP archive. Available entries: {available}");
        }

        using var entryStream = entry.Open();
        using var outStream   = File.Create(outputPath);
        entryStream.CopyTo(outStream);
    }

    private static void BackupDll(string src, string dest)
    {
        if (File.Exists(dest)) return;     // already backed up — don't overwrite with already-swapped DLL
        if (!File.Exists(src))  return;
        File.Copy(src, dest, overwrite: false);
    }

    private static void RestoreDll(string dir, string apiFile, string bakFile,
                                    IProgress<string>? progress, string label)
    {
        var bak = Path.Combine(dir, bakFile);
        var api = Path.Combine(dir, apiFile);
        if (!File.Exists(bak)) return;
        Report(progress, $"♻ Restoring {label}...");
        if (File.Exists(api)) File.Delete(api);
        File.Move(bak, api);
    }

    // ── Config writers ────────────────────────────────────────────────────────

    internal int WriteCreamApiIni(string gameDir, GameInstance instance, DlcInfo? contextDlc = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"; Generated by BlueStar for {instance.Name}");
        sb.AppendLine("[steam]");
        sb.AppendLine($"appid = {instance.AppId}");
        sb.AppendLine("unlockall = false");
        sb.AppendLine($"orgapi = {SteamApi32Orig}");
        sb.AppendLine($"orgapi64 = {SteamApi64Orig}");
        sb.AppendLine("extraprotection = false");
        sb.AppendLine("forceoffline = false");
        sb.AppendLine();
        // MANDATORY for CreamAPI v5.3+ — the INI is silently ignored without this section
        sb.AppendLine("[steam_misc]");
        sb.AppendLine("disableuserinterface = false");
        sb.AppendLine();
        sb.AppendLine("[dlc]");

        // Filter DLCs: if UnlockedDlcIds is provided, strictly include only selected DLCs.
        // If UnlockedDlcIds is null, fallback to all known DLCs.
        List<DlcInfo> activeDlcs;
        if (instance.UnlockedDlcIds != null)
        {
            var allowedIds = instance.UnlockedDlcIds.ToHashSet();
            activeDlcs = instance.Dlcs.Where(d => allowedIds.Contains(d.AppId)).OrderBy(d => d.AppId).ToList();

            // If UnlockedDlcIds did not match anything in instance.Dlcs but contextDlc is valid and selected, add it
            if (activeDlcs.Count == 0 && contextDlc != null && contextDlc.AppId > 0 && contextDlc.AppId != instance.AppId && allowedIds.Contains(contextDlc.AppId))
            {
                activeDlcs.Add(contextDlc);
            }
        }
        else
        {
            activeDlcs = instance.Dlcs.OrderBy(d => d.AppId).ToList();
            if (activeDlcs.Count == 0 && contextDlc != null && contextDlc.AppId > 0 && contextDlc.AppId != instance.AppId)
            {
                activeDlcs.Add(contextDlc);
            }
        }

        foreach (var d in activeDlcs)
        {
            sb.AppendLine($"{d.AppId} = {d.Name}");
        }

        var iniPath = Path.Combine(gameDir, CreamApiIni);
        File.WriteAllText(iniPath, sb.ToString(), new UTF8Encoding(false));
        _logger.LogInformation("[CreamInstaller] Wrote cream_api.ini ({Count} DLCs) for {Game} at {Path}", activeDlcs.Count, instance.Name, iniPath);
        return activeDlcs.Count;
    }

    private static void WriteSmokeApiConfig(string gameDir)
    {
        // SmokeAPI.config.json is already bundled in the ZIP; we overwrite it with unlock_all = true
        var config = new { logging = false, unlock_all = true, dlcs = new Dictionary<string, string>() };
        var json   = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(Path.Combine(gameDir, SmokeApiJson), json, Encoding.UTF8);
    }

    /// <summary>Thread-safe progress report (Report() is already thread-safe in Progress{T}).</summary>
    private static void Report(IProgress<string>? p, string msg) => p?.Report(msg);
}
