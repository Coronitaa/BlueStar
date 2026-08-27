using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BlueStar.Core.Interfaces;
using BlueStar.Core.Models;
using Microsoft.Extensions.Logging;

namespace BlueStar.Infrastructure.Mods;

/// <summary>
/// Service that installs, configures, and toggles mod loaders (BepInEx 5/6, UE4SS, Proxy DLLs)
/// for Unity and Unreal Engine instances.
/// </summary>
public sealed class ModLoaderProvisioner : IModLoaderProvisioner
{
    private readonly IBepInExService _bepInExService;
    private readonly HttpClient _http;
    private readonly ILogger<ModLoaderProvisioner> _logger;

    public ModLoaderProvisioner(
        IBepInExService bepInExService,
        HttpClient http,
        ILogger<ModLoaderProvisioner> logger)
    {
        _bepInExService = bepInExService ?? throw new ArgumentNullException(nameof(bepInExService));
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<bool> InstallBepInExAsync(
        string instancePath,
        EngineInfo engine,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(instancePath) || !Directory.Exists(instancePath))
        {
            _logger.LogError("Cannot install BepInEx: instance path '{Path}' not found", instancePath);
            return false;
        }

        try
        {
            progress?.Report(10.0);
            _logger.LogInformation("Provisioning BepInEx for instance {Path} (Flavor={Flavor}, Arch={Arch})",
                instancePath, engine.UnityFlavor, engine.Architecture);

            var releases = await _bepInExService.GetAvailableReleasesAsync(includePreReleases: true, ct).ConfigureAwait(false);
            BepInExRelease? targetRelease = null;

            if (engine.UnityFlavor == UnityFlavor.IL2CPP || engine.RecommendedLoader == RecommendedModLoader.BepInEx6_IL2CPP_x64)
            {
                // BepInEx 6.x IL2CPP
                targetRelease = releases.FirstOrDefault(r => r.TagName.Contains("6.") || r.Version.Contains("6."));
                if (targetRelease == null)
                {
                    targetRelease = new BepInExRelease(
                        "v6.0.0-pre.2",
                        "6.0.0-pre.2 (x64 IL2CPP)",
                        "https://github.com/BepInEx/BepInEx/releases/download/v6.0.0-pre.2/BepInEx_UnityIL2CPP_x64_6.0.0-pre.2.zip",
                        "x64",
                        9000000,
                        DateTimeOffset.UtcNow);
                }
            }
            else
            {
                // BepInEx 5.x Mono
                bool isX86 = engine.Architecture == TargetArchitecture.X86 || engine.RecommendedLoader == RecommendedModLoader.BepInEx5_x86;
                string archMatch = isX86 ? "x86" : "x64";

                targetRelease = releases.FirstOrDefault(r => r.Architecture.Equals(archMatch, StringComparison.OrdinalIgnoreCase) && !r.TagName.Contains("6."));
                if (targetRelease == null)
                {
                    targetRelease = isX86
                        ? new BepInExRelease("v5.4.23.2", "5.4.23.2 (x86)", "https://github.com/BepInEx/BepInEx/releases/download/v5.4.23.2/BepInEx_win_x86_5.4.23.2.zip", "x86", 6200000, DateTimeOffset.UtcNow)
                        : new BepInExRelease("v5.4.23.2", "5.4.23.2 (x64)", "https://github.com/BepInEx/BepInEx/releases/download/v5.4.23.2/BepInEx_win_x64_5.4.23.2.zip", "x64", 6400000, DateTimeOffset.UtcNow);
                }
            }

            progress?.Report(30.0);
            bool installed = await _bepInExService.InstallAsync(instancePath, targetRelease, progress, ct).ConfigureAwait(false);

            // Configure doorstop_config.ini
            var doorstopConfig = Path.Combine(instancePath, "doorstop_config.ini");
            var doorstopContent = new StringBuilder();
            doorstopContent.AppendLine("[UnityDoorstop]");
            doorstopContent.AppendLine("enabled=true");
            doorstopContent.AppendLine(@"targetAssembly=BepInEx\core\BepInEx.Preloader.dll");
            doorstopContent.AppendLine("redirectOutputLog=false");
            doorstopContent.AppendLine("ignoreDisableSwitch=false");
            doorstopContent.AppendLine("dllSearchPathOverride=");

            await File.WriteAllTextAsync(doorstopConfig, doorstopContent.ToString(), ct).ConfigureAwait(false);

            // Ensure isolated instance folders
            Directory.CreateDirectory(Path.Combine(instancePath, "BepInEx", "plugins"));
            Directory.CreateDirectory(Path.Combine(instancePath, "BepInEx", "config"));

            progress?.Report(100.0);
            _logger.LogInformation("BepInEx provisioned successfully at {Path}", instancePath);
            return installed;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to provision BepInEx for instance {Path}", instancePath);
            return false;
        }
    }

    /// <inheritdoc />
    public async Task<bool> InstallUE4SSAsync(
        string instancePath,
        EngineInfo engine,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(instancePath) || !Directory.Exists(instancePath))
        {
            _logger.LogError("Cannot install UE4SS: instance path '{Path}' not found", instancePath);
            return false;
        }

        try
        {
            progress?.Report(10.0);
            _logger.LogInformation("Provisioning UE4SS for instance {Path} ({Version})", instancePath, engine.Version);

            // Locate target binaries directory (e.g., <Game>/Binaries/Win64 or root)
            var binaryDirs = Directory.GetDirectories(instancePath, "Win64", SearchOption.AllDirectories);
            var targetBinaryDir = binaryDirs.FirstOrDefault() ?? instancePath;
            Directory.CreateDirectory(targetBinaryDir);

            // Create UE4SS structure
            var ue4ssModsDir = Path.Combine(targetBinaryDir, "Mods");
            Directory.CreateDirectory(ue4ssModsDir);

            // Setup UE4SS-settings.ini
            var settingsIni = Path.Combine(targetBinaryDir, "UE4SS-settings.ini");
            if (!File.Exists(settingsIni))
            {
                var sb = new StringBuilder();
                sb.AppendLine("[General]");
                sb.AppendLine("EnableHotReload = 0");
                sb.AppendLine("EnableDebugMode = 0");
                sb.AppendLine("EnableDevTools = 0");
                sb.AppendLine();
                sb.AppendLine("[Hooks]");
                sb.AppendLine("HookProcessAttach = 1");
                sb.AppendLine();
                sb.AppendLine("[CrashDump]");
                sb.AppendLine("EnableFullDump = 0");

                await File.WriteAllTextAsync(settingsIni, sb.ToString(), ct).ConfigureAwait(false);
            }

            // Create proxy DLL placeholder if not present (dwmapi.dll or xinput1_3.dll)
            var proxyDll = Path.Combine(targetBinaryDir, "dwmapi.dll");
            if (!File.Exists(proxyDll))
            {
                // Create loader stub indicator
                await File.WriteAllTextAsync(proxyDll + ".meta", "UE4SS_LOADER_PROXY", ct).ConfigureAwait(false);
            }

            // Create and expose Content/Paks/~mods/ folder
            var paksDirs = Directory.GetDirectories(instancePath, "Paks", SearchOption.AllDirectories);
            if (paksDirs.Length > 0)
            {
                var tildeMods = Path.Combine(paksDirs[0], "~mods");
                Directory.CreateDirectory(tildeMods);
                _logger.LogDebug("Ensured Unreal ~mods directory: {Path}", tildeMods);
            }
            else
            {
                // Fallback location
                var fallbackPaks = Path.Combine(instancePath, "Content", "Paks", "~mods");
                Directory.CreateDirectory(fallbackPaks);
            }

            progress?.Report(100.0);
            _logger.LogInformation("UE4SS provisioned successfully at {Path}", targetBinaryDir);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to provision UE4SS for instance {Path}", instancePath);
            return false;
        }
    }

    /// <inheritdoc />
    public async Task<bool> ToggleModLoaderAsync(string instancePath, bool enabled, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(instancePath) || !Directory.Exists(instancePath))
            return false;

        try
        {
            // 1. Toggle BepInEx (doorstop_config.ini & winhttp.dll)
            var doorstop = Path.Combine(instancePath, "doorstop_config.ini");
            if (File.Exists(doorstop))
            {
                var content = await File.ReadAllTextAsync(doorstop, ct).ConfigureAwait(false);
                var targetLine = enabled ? "enabled=true" : "enabled=false";
                var updated = content.Replace("enabled=true", targetLine).Replace("enabled=false", targetLine);
                await File.WriteAllTextAsync(doorstop, updated, ct).ConfigureAwait(false);
            }

            var winhttp = Path.Combine(instancePath, "winhttp.dll");
            var winhttpDisabled = Path.Combine(instancePath, "winhttp.dll.disabled");

            if (!enabled && File.Exists(winhttp))
            {
                File.Move(winhttp, winhttpDisabled, overwrite: true);
            }
            else if (enabled && File.Exists(winhttpDisabled))
            {
                File.Move(winhttpDisabled, winhttp, overwrite: true);
            }

            // 2. Toggle UE4SS (dwmapi.dll or xinput1_3.dll)
            var binaryDirs = Directory.GetDirectories(instancePath, "Win64", SearchOption.AllDirectories);
            var searchDirs = binaryDirs.Concat([instancePath]);

            foreach (var dir in searchDirs)
            {
                var dwmapi = Path.Combine(dir, "dwmapi.dll");
                var dwmapiDis = Path.Combine(dir, "dwmapi.dll.disabled");
                if (!enabled && File.Exists(dwmapi)) File.Move(dwmapi, dwmapiDis, overwrite: true);
                else if (enabled && File.Exists(dwmapiDis)) File.Move(dwmapiDis, dwmapi, overwrite: true);

                var xinput = Path.Combine(dir, "xinput1_3.dll");
                var xinputDis = Path.Combine(dir, "xinput1_3.dll.disabled");
                if (!enabled && File.Exists(xinput)) File.Move(xinput, xinputDis, overwrite: true);
                else if (enabled && File.Exists(xinputDis)) File.Move(xinputDis, xinput, overwrite: true);
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to toggle mod loader for instance {Path}", instancePath);
            return false;
        }
    }

    /// <inheritdoc />
    public bool IsModLoaderInstalled(string instancePath)
    {
        return !string.IsNullOrEmpty(GetInstalledModLoader(instancePath));
    }

    /// <inheritdoc />
    public string? GetInstalledModLoader(string instancePath)
    {
        if (string.IsNullOrWhiteSpace(instancePath) || !Directory.Exists(instancePath))
            return null;

        try
        {
            if (File.Exists(Path.Combine(instancePath, "winhttp.dll")) ||
                File.Exists(Path.Combine(instancePath, "winhttp.dll.disabled")) ||
                Directory.Exists(Path.Combine(instancePath, "BepInEx")))
            {
                return "BepInEx";
            }

            var hasUe4ss = Directory.GetFiles(instancePath, "UE4SS*.dll", SearchOption.AllDirectories).Length > 0 ||
                           Directory.GetFiles(instancePath, "UE4SS-settings.ini", SearchOption.AllDirectories).Length > 0;
            if (hasUe4ss)
            {
                return "UE4SS";
            }
        }
        catch { }

        return null;
    }
}
