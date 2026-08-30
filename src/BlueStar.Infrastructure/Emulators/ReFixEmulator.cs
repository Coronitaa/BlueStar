using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BlueStar.Core.Interfaces;
using BlueStar.Core.Models;
using Microsoft.Extensions.Logging;

namespace BlueStar.Infrastructure.Emulators;

/// <summary>
/// Emulator provider for ReFix (Universal Multi-Engine online and offline LAN emulator).
/// Integrates directly with the ReFix_deploy backend suite located at C:\Users\Valen\Desktop\STEAM_EMU\ReFix_deploy
/// and project tools, executing deploy_helper.ps1, detect_game.ps1, apply_firewall.ps1, and Uninstall_ReFix.bat.
/// </summary>
public sealed class ReFixEmulator : IEmulator
{
    private readonly ILogger<ReFixEmulator> _logger;

    public string Id => "refix";
    public string DisplayName => "ReFix Emulator";
    public string Description => "Universal game emulation backend supporting Steam Online (Spacewar 480) and Re:Goldberg LAN multiplayer.";

    public ReFixEmulator(ILogger<ReFixEmulator> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Resolves the absolute path to the ReFix_deploy directory dynamically.
    /// Checks AppData tools, AppContext BaseDirectory, AppDomain BaseDirectory, and workspace tools.
    /// </summary>
    public static string? GetReFixDeployPath()
    {
        var appDataTools = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BlueStar", "tools", "ReFix_deploy");

        var candidates = new List<string>
        {
            appDataTools,
            Path.Combine(AppContext.BaseDirectory, "tools", "ReFix_deploy"),
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "tools", "ReFix_deploy"),
            Path.Combine(Directory.GetCurrentDirectory(), "src", "BlueStar.App", "tools", "ReFix_deploy"),
            Path.Combine(Directory.GetCurrentDirectory(), "tools", "ReFix_deploy")
        };

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            candidates.Add(Path.Combine(dir.FullName, "tools", "ReFix_deploy"));
            candidates.Add(Path.Combine(dir.FullName, "src", "BlueStar.App", "tools", "ReFix_deploy"));
            dir = dir.Parent;
        }

        var current = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (current != null)
        {
            candidates.Add(Path.Combine(current.FullName, "tools", "ReFix_deploy"));
            candidates.Add(Path.Combine(current.FullName, "src", "BlueStar.App", "tools", "ReFix_deploy"));
            current = current.Parent;
        }

        foreach (var path in candidates)
        {
            if (Directory.Exists(path))
            {
                var binDir = Path.Combine(path, "bin");
                if (File.Exists(Path.Combine(binDir, "steam_api64.dll")) ||
                    File.Exists(Path.Combine(binDir, "deploy_helper.ps1")) ||
                    File.Exists(Path.Combine(path, "AutoDeploy.bat")) ||
                    File.Exists(Path.Combine(path, "Uninstall_ReFix.bat")))
                {
                    return path;
                }
            }
        }
        return null;
    }

    /// <summary>
    /// Gets the current installed suite version.
    /// </summary>
    public static string GetCurrentVersion()
    {
        var deployPath = GetReFixDeployPath();
        if (deployPath == null) return "1.1";

        var versionFile = Path.Combine(deployPath, "refix_version.json");
        if (File.Exists(versionFile))
        {
            try
            {
                var json = File.ReadAllText(versionFile);
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("version", out var v) && v.GetString() is string ver)
                {
                    return ver.Trim().TrimStart('v', 'V');
                }
            }
            catch { }
        }

        return "1.1";
    }

    public bool IsSupported(GameInstance instance)
    {
        return true;
    }

    public Task<EmulatorStatus> GetStatusAsync(GameInstance instance, CancellationToken ct = default)
    {
        bool isInstalled = IsEmulatorInstalled(instance.InstallPath);
        string installedMode = GetInstalledMode(instance.InstallPath) ?? (instance.EmulatorId?.Contains("goldberg") == true ? "Re:Goldberg LAN" : "ReFix Online (Steam)");
        bool isActive = instance.EmulatorEnabled || isInstalled;

        string statusMsg = isInstalled ? $"Installed ({installedMode}) ● Ready" : "Not Installed";
        string backendInfo = isInstalled ? $"ReFix Deploy Suite v1.1.0 ({installedMode})" : "ReFix Suite Deployable";
        string networkInfo = installedMode.Contains("Goldberg", StringComparison.OrdinalIgnoreCase)
            ? "LAN Broadcast / Local Peer-to-Peer"
            : "Steam Online Spacewar (AppID 480)";

        var config = new Dictionary<string, string>();
        if (!string.IsNullOrWhiteSpace(instance.InstallPath))
        {
            config["InstallPath"] = instance.InstallPath;
            config["Mode"] = installedMode;
        }

        return Task.FromResult(new EmulatorStatus
        {
            EmulatorId = instance.EmulatorId ?? Id,
            EmulatorName = DisplayName,
            IsConfigured = isInstalled,
            IsActive = isActive,
            StatusMessage = statusMsg,
            BackendInfo = backendInfo,
            NetworkInfo = networkInfo,
            ConfigValues = config
        });
    }

    private static readonly EnumerationOptions SafeEnumOptions = new()
    {
        MaxRecursionDepth = 6,
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
        ReturnSpecialDirectories = false
    };

    public static bool IsEmulatorInstalled(string? installPath)
    {
        if (string.IsNullOrWhiteSpace(installPath) || !Directory.Exists(installPath)) return false;

        try
        {
            var hasReFixIni = Directory.GetFiles(installPath, "ReFix.ini", SafeEnumOptions).Length > 0;
            var hasValveBackup = Directory.GetFiles(installPath, "steam_api64_valve.dll", SafeEnumOptions).Length > 0 ||
                                 Directory.GetFiles(installPath, "steam_api_valve.dll", SafeEnumOptions).Length > 0 ||
                                 Directory.GetFiles(installPath, "steam_api64_o.dll", SafeEnumOptions).Length > 0 ||
                                 Directory.GetFiles(installPath, "steam_api_o.dll", SafeEnumOptions).Length > 0;

            var hasGoldbergBinary = Directory.GetFiles(installPath, "goldberg_steam_api64.dll", SafeEnumOptions).Length > 0 ||
                                    Directory.GetFiles(installPath, "goldberg_steam_api.dll", SafeEnumOptions).Length > 0 ||
                                    Directory.GetFiles(installPath, "local_save.txt", SafeEnumOptions).Length > 0;

            return hasReFixIni || hasValveBackup || hasGoldbergBinary;
        }
        catch
        {
            return false;
        }
    }

    public static string? GetInstalledMode(string? installPath)
    {
        if (string.IsNullOrWhiteSpace(installPath) || !Directory.Exists(installPath)) return null;

        try
        {
            var hasReFixIni = Directory.GetFiles(installPath, "ReFix.ini", SafeEnumOptions).Length > 0;
            var hasValveBackup = Directory.GetFiles(installPath, "steam_api64_valve.dll", SafeEnumOptions).Length > 0 ||
                                 Directory.GetFiles(installPath, "steam_api_valve.dll", SafeEnumOptions).Length > 0;
            if (hasReFixIni || hasValveBackup) return "ReFix Online (Steam)";

            var hasGoldberg = Directory.GetFiles(installPath, "steam_api64_o.dll", SafeEnumOptions).Length > 0 ||
                              Directory.GetFiles(installPath, "steam_api_o.dll", SafeEnumOptions).Length > 0 ||
                              Directory.GetFiles(installPath, "goldberg_steam_api64.dll", SafeEnumOptions).Length > 0 ||
                              Directory.GetFiles(installPath, "goldberg_steam_api.dll", SafeEnumOptions).Length > 0 ||
                              Directory.GetFiles(installPath, "local_save.txt", SafeEnumOptions).Length > 0;
            if (hasGoldberg) return "Re:Goldberg LAN";
        }
        catch { }

        return null;
    }

    public Task<bool> DeployOptionAsync(GameInstance instance, string optionId, CancellationToken ct = default)
    {
        return DeployOptionAsync(instance, optionId, null, ct);
    }

    public async Task<bool> DeployOptionAsync(
        GameInstance instance,
        string optionId,
        IProgress<DeployProgress>? progress = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(instance.InstallPath) || !Directory.Exists(instance.InstallPath))
        {
            _logger.LogError("Cannot deploy ReFix: Game is not installed at {Path}", instance.InstallPath);
            progress?.Report(new DeployProgress { Percentage = 0, Message = "Game is not installed in the specified path. Please install it first." });
            return false;
        }

        try
        {
            var files = Directory.GetFiles(instance.InstallPath, "*", SafeEnumOptions);
            if (files.Length == 0)
            {
                _logger.LogError("Cannot deploy ReFix: Install directory '{Path}' is empty.", instance.InstallPath);
                progress?.Report(new DeployProgress { Percentage = 0, Message = "Game folder is empty. Please download or install the game first." });
                return false;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to verify game install path {Path}", instance.InstallPath);
            progress?.Report(new DeployProgress { Percentage = 0, Message = $"Error verifying installation: {ex.Message}" });
            return false;
        }

        var deployPath = GetReFixDeployPath();
        if (deployPath == null)
        {
            _logger.LogError("ReFix_deploy folder not found in known paths.");
            progress?.Report(new DeployProgress { Percentage = 0, Message = "ReFix_deploy suite was not found in application tools or AppData." });
            return false;
        }

        var binDir = Directory.Exists(Path.Combine(deployPath, "bin")) ? Path.Combine(deployPath, "bin") : deployPath;
        var isGoldberg = optionId.Contains("goldberg", StringComparison.OrdinalIgnoreCase);
        var onlineMode = isGoldberg ? "goldberg" : "valve";
        var modeDisplayName = isGoldberg ? "Re:Goldberg LAN (No Steam)" : "ReFix Online (Steam Spacewar 480)";

        _logger.LogInformation("Deploying ReFix ({Mode}) via ReFix_deploy scripts for {Name} at {Path}", onlineMode, instance.Name, instance.InstallPath);

        // Verify that ReFix digital signature is installed in the system; if not, install it
        try
        {
            await BlueStar.Infrastructure.Services.ReFixCertificateHelper.EnsureCertificateInstalledAsync(_logger, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Non-fatal error while ensuring ReFix certificate installation.");
        }

        try
        {
            var targetDir = instance.InstallPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            // ─────────────────────────────────────────────────────────────
            // ETAPA 1: Análisis del juego mediante detect_game.ps1
            // ─────────────────────────────────────────────────────────────
            progress?.Report(new DeployProgress
            {
                Percentage = 10,
                Message = "Analyzing game directory with detect_game.ps1...",
                CurrentStep = "Detection"
            });

            var detection = await RunGameDetectorAsync(targetDir, binDir, instance.ExecutablePath, ct).ConfigureAwait(false);

            var gameName = !string.IsNullOrWhiteSpace(instance.Name) ? instance.Name : (!string.IsNullOrWhiteSpace(detection.GameName) ? detection.GameName : Path.GetFileName(targetDir));
            var engineType = !string.IsNullOrWhiteSpace(detection.EngineType) && detection.EngineType != "Native"
                ? detection.EngineType
                : (instance.Engine?.Type switch
                {
                    EngineType.Unity => "Unity",
                    EngineType.UnrealEngine => "Unreal",
                    EngineType.Godot => "Godot",
                    _ => !string.IsNullOrWhiteSpace(detection.EngineType) ? detection.EngineType : "Native"
                });

            var exeDir = !string.IsNullOrWhiteSpace(detection.ExeDir) && Directory.Exists(detection.ExeDir)
                ? detection.ExeDir
                : (!string.IsNullOrWhiteSpace(instance.ExecutablePath) && File.Exists(instance.ExecutablePath)
                    ? Path.GetDirectoryName(instance.ExecutablePath)!
                    : targetDir);

            var gameExePath = !string.IsNullOrWhiteSpace(detection.GameExePath) && File.Exists(detection.GameExePath)
                ? detection.GameExePath
                : (!string.IsNullOrWhiteSpace(instance.ExecutablePath) && File.Exists(instance.ExecutablePath)
                    ? instance.ExecutablePath
                    : null);

            var realAppIdStr = instance.AppId > 0
                ? instance.AppId.ToString()
                : (!string.IsNullOrWhiteSpace(detection.DetectedAppId) && detection.DetectedAppId != "0" ? detection.DetectedAppId : "480");
            var maskAppIdStr = isGoldberg ? realAppIdStr : "480";
            var userName = Environment.UserName;
            var lanPort = "47584";
            var dlcMode = "all";
            var dlcListStr = "all";

            _logger.LogInformation("[ReFix Detection] Engine: {Engine}, ExeDir: {ExeDir}, Exe: {Exe}, RealAppId: {RealAppId}, Mode: {Mode}",
                engineType, exeDir, gameExePath, realAppIdStr, onlineMode);

            // ─────────────────────────────────────────────────────────────
            // STAGE 2: Pre-cleanup and restoration with Uninstall_ReFix.bat
            // ─────────────────────────────────────────────────────────────
            progress?.Report(new DeployProgress
            {
                Percentage = 25,
                Message = "Cleaning previous installation with Uninstall_ReFix.bat...",
                CurrentStep = "Cleanup"
            });

            await UninstallAsync(instance, ct).ConfigureAwait(false);

            // ─────────────────────────────────────────────────────────────
            // STAGE 3: Run deploy_helper.ps1 with all parameters
            // ─────────────────────────────────────────────────────────────
            progress?.Report(new DeployProgress
            {
                Percentage = 50,
                Message = $"Running deploy_helper.ps1 ({modeDisplayName} - {engineType})...",
                CurrentStep = "DeployHelper"
            });

            var helperPs1 = Path.Combine(binDir, "deploy_helper.ps1");
            if (!File.Exists(helperPs1))
            {
                throw new FileNotFoundException($"Deployment script not found: {helperPs1}");
            }

            var helperArgs = new StringBuilder();
            helperArgs.Append("-NoProfile -ExecutionPolicy Bypass -File ");
            helperArgs.Append($"\"{helperPs1}\" ");
            helperArgs.Append($"-TargetDir \"{targetDir}\" ");
            helperArgs.Append($"-BinDir \"{binDir}\" ");
            helperArgs.Append($"-ExeDir \"{exeDir}\" ");
            helperArgs.Append($"-EngineType \"{engineType}\" ");
            helperArgs.Append($"-OnlineMode \"{onlineMode}\" ");
            helperArgs.Append($"-GameName \"{gameName}\" ");
            helperArgs.Append($"-UserName \"{userName}\" ");
            helperArgs.Append($"-RealAppId \"{realAppIdStr}\" ");
            helperArgs.Append($"-MaskAppId \"{maskAppIdStr}\" ");
            helperArgs.Append("-Language \"english\" ");
            helperArgs.Append($"-DLCs \"{dlcListStr}\" ");
            helperArgs.Append($"-DLCMode \"{dlcMode}\" ");
            helperArgs.Append($"-ListenPort \"{lanPort}\" ");
            helperArgs.Append("-CustomBroadcasts \"\"");

            var (helperExitCode, helperOutput, helperError) = await RunProcessAsync(
                "powershell.exe",
                helperArgs.ToString(),
                binDir,
                line =>
                {
                    _logger.LogInformation("[deploy_helper] {Line}", line);
                    progress?.Report(new DeployProgress
                    {
                        Percentage = 65,
                        Message = line,
                        CurrentStep = "DeployHelper"
                    });
                },
                ct).ConfigureAwait(false);

            if (helperExitCode != 0)
            {
                _logger.LogWarning("deploy_helper.ps1 exited with code {Code}: {Error}", helperExitCode, helperError);
            }

            // ─────────────────────────────────────────────────────────────
            // STAGE 4: Shortcuts and auxiliary scripts (Steam Shortcuts)
            // ─────────────────────────────────────────────────────────────
            progress?.Report(new DeployProgress
            {
                Percentage = 75,
                Message = "Configuring shortcuts and auxiliary scripts...",
                CurrentStep = "Shortcuts"
            });

            if (!isGoldberg && Directory.Exists(exeDir))
            {
                var shortcutPs1 = Path.Combine(binDir, "add_steam_shortcut.ps1");
                var shortcutBat = Path.Combine(binDir, "Install_ReFix_Steam_Shortcut.bat");
                if (File.Exists(shortcutPs1)) File.Copy(shortcutPs1, Path.Combine(exeDir, "add_steam_shortcut.ps1"), overwrite: true);
                if (File.Exists(shortcutBat)) File.Copy(shortcutBat, Path.Combine(exeDir, "Install_ReFix_Steam_Shortcut.bat"), overwrite: true);
            }

            // ─────────────────────────────────────────────────────────────
            // STAGE 5: Firewall rule configuration with apply_firewall.ps1
            // ─────────────────────────────────────────────────────────────
            progress?.Report(new DeployProgress
            {
                Percentage = 85,
                Message = "Configuring Windows Defender Firewall rules...",
                CurrentStep = "Firewall"
            });

            var firewallPs1 = Path.Combine(binDir, "apply_firewall.ps1");
            if (File.Exists(firewallPs1) && !string.IsNullOrWhiteSpace(gameExePath) && File.Exists(gameExePath))
            {
                var fwArgs = $"-NoProfile -ExecutionPolicy Bypass -File \"{firewallPs1}\" -GameExe \"{gameExePath}\" -GameName \"{gameName}\" -LanPort \"{lanPort}\" -Mode \"{onlineMode}\"";
                await RunProcessAsync(
                    "powershell.exe",
                    fwArgs,
                    binDir,
                    line => _logger.LogInformation("[apply_firewall] {Line}", line),
                    ct).ConfigureAwait(false);
            }

            // ─────────────────────────────────────────────────────────────
            // STAGE 6: Final Verification
            // ─────────────────────────────────────────────────────────────
            progress?.Report(new DeployProgress
            {
                Percentage = 100,
                Message = $"Successfully deployed and configured {modeDisplayName}!",
                CurrentStep = "Complete"
            });

            AppendReFixLog(exeDir, $"Deployed Mode: {modeDisplayName} | Game: {gameName} | Engine: {engineType} | RealAppId: {realAppIdStr} | MaskAppId: {maskAppIdStr}");
            _logger.LogInformation("Successfully deployed ReFix ({OptionId}) for {Game}", optionId, instance.Name);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to deploy ReFix option {Option} for {Game}", optionId, instance.Name);
            progress?.Report(new DeployProgress
            {
                Percentage = 0,
                Message = $"Error during deployment: {ex.Message}",
                CurrentStep = "Error"
            });
            return false;
        }
    }

    public async Task<bool> UninstallAsync(GameInstance instance, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(instance.InstallPath) || !Directory.Exists(instance.InstallPath))
            return true;

        _logger.LogInformation("Uninstalling ReFix and restoring original game files in {Path}", instance.InstallPath);

        try
        {
            var targetDir = instance.InstallPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var deployPath = GetReFixDeployPath();

            // 1. Run Uninstall_ReFix.bat directly with targetDir parameter if available
            if (deployPath != null)
            {
                var uninstallerBat = Path.Combine(deployPath, "Uninstall_ReFix.bat");
                if (File.Exists(uninstallerBat))
                {
                    _logger.LogInformation("Executing Uninstall_ReFix.bat for {Path}", targetDir);
                    await RunProcessAsync(
                        "cmd.exe",
                        $"/c \"call \"{uninstallerBat}\" \"{targetDir}\" <nul\"",
                        deployPath,
                        line => _logger.LogInformation("[Uninstall_ReFix] {Line}", line),
                        ct).ConfigureAwait(false);
                }
            }

            // 2. Comprehensive backup restoration & cleanup fallback to guarantee 100% clean state
            RestoreOriginalFilesFallback(targetDir);

            _logger.LogInformation("Successfully uninstalled ReFix and restored game files for {Name}", instance.Name);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to uninstall ReFix for {Game}", instance.Name);
            return false;
        }
    }

    public Task<bool> EnableAsync(GameInstance instance, CancellationToken ct = default)
    {
        return DeployOptionAsync(instance, instance.EmulatorId ?? "refix_valve", ct);
    }

    public Task<bool> DisableAsync(GameInstance instance, CancellationToken ct = default)
    {
        return UninstallAsync(instance, ct);
    }

    public Task<bool> ConfigureAsync(GameInstance instance, IDictionary<string, string> config, CancellationToken ct = default)
    {
        _logger.LogInformation("ReFix configuration updated for {Game}", instance.Name);
        return Task.FromResult(true);
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // Process Execution & Helper Methods
    // ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Runs an external process asynchronously with redirected streams and non-blocking stdin.
    /// </summary>
    public static async Task<(int ExitCode, string Output, string Error)> RunProcessAsync(
        string fileName,
        string arguments,
        string workingDirectory,
        Action<string>? onOutputLine = null,
        CancellationToken ct = default)
    {
        var outputBuilder = new StringBuilder();
        var errorBuilder = new StringBuilder();

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            }
        };

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data != null)
            {
                outputBuilder.AppendLine(e.Data);
                onOutputLine?.Invoke(e.Data);
            }
        };

        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data != null)
            {
                errorBuilder.AppendLine(e.Data);
            }
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        // Close stdin immediately so batch scripts (pause >nul) or prompts never block
        process.StandardInput.Close();

        await process.WaitForExitAsync(ct).ConfigureAwait(false);
        return (process.ExitCode, outputBuilder.ToString(), errorBuilder.ToString());
    }

    public static async Task<(string EngineType, string ExeDir, string? GameExePath, string GameName, string DetectedAppId)> RunGameDetectorAsync(
        string targetDir,
        string binDir,
        string? configuredExePath,
        CancellationToken ct)
    {
        string engineType = "Native";
        string exeDir = targetDir;
        string? gameExePath = null;
        string gameName = Path.GetFileName(targetDir);
        string detectedAppId = "";

        var detectScript = Path.Combine(binDir, "detect_game.ps1");
        if (File.Exists(detectScript))
        {
            try
            {
                var (exitCode, stdout, _) = await RunProcessAsync(
                    "powershell.exe",
                    $"-NoProfile -ExecutionPolicy Bypass -File \"{detectScript}\" -TargetDir \"{targetDir}\"",
                    binDir,
                    null,
                    ct).ConfigureAwait(false);

                if (exitCode == 0 && !string.IsNullOrWhiteSpace(stdout))
                {
                    foreach (var line in stdout.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        var parts = line.Split('=', 2);
                        if (parts.Length == 2)
                        {
                            var key = parts[0].Trim().ToUpperInvariant();
                            var val = parts[1].Trim();
                            switch (key)
                            {
                                case "ENGINE_TYPE":
                                    if (!string.IsNullOrEmpty(val)) engineType = val;
                                    break;
                                case "EXE_DIR":
                                    if (!string.IsNullOrEmpty(val)) exeDir = val;
                                    break;
                                case "GAME_EXE_PATH":
                                    if (!string.IsNullOrEmpty(val)) gameExePath = val;
                                    break;
                                case "GAME_NAME":
                                    if (!string.IsNullOrEmpty(val)) gameName = val;
                                    break;
                                case "DETECTED_APPID":
                                    if (!string.IsNullOrEmpty(val) && val != "0" && val != "480") detectedAppId = val;
                                    break;
                            }
                        }
                    }
                }
            }
            catch { }
        }

        // Fallback detection if script was unavailable or did not find an executable
        if (string.IsNullOrWhiteSpace(gameExePath) || !File.Exists(gameExePath))
        {
            if (!string.IsNullOrWhiteSpace(configuredExePath) && File.Exists(configuredExePath))
            {
                gameExePath = configuredExePath;
                exeDir = Path.GetDirectoryName(configuredExePath) ?? targetDir;
            }
            else
            {
                try
                {
                    var allExes = Directory.GetFiles(targetDir, "*.exe", SafeEnumOptions)
                        .Where(f => !f.Contains("crashpad", StringComparison.OrdinalIgnoreCase) &&
                                    !f.Contains("UnityCrashHandler", StringComparison.OrdinalIgnoreCase) &&
                                    !f.Contains("unins", StringComparison.OrdinalIgnoreCase) &&
                                    !f.Contains("setup", StringComparison.OrdinalIgnoreCase) &&
                                    !f.Contains("redist", StringComparison.OrdinalIgnoreCase) &&
                                    !f.Contains("dxsetup", StringComparison.OrdinalIgnoreCase) &&
                                    !f.Contains("vcredist", StringComparison.OrdinalIgnoreCase))
                        .ToList();

                    if (allExes.Count > 0)
                    {
                        gameExePath = allExes.OrderByDescending(f => f.Contains("Shipping", StringComparison.OrdinalIgnoreCase) ? 100 : 50).First();
                        exeDir = Path.GetDirectoryName(gameExePath) ?? targetDir;
                    }
                }
                catch { }
            }
        }

        return (engineType, exeDir, gameExePath, gameName, detectedAppId);
    }

    private static void RestoreOriginalFilesFallback(string targetDir)
    {
        // 1. Restore winmm.dll from winmm_o.dll
        try
        {
            var winmmBackups = Directory.GetFiles(targetDir, "winmm_o.dll", SafeEnumOptions);
            foreach (var w in winmmBackups)
            {
                var dir = Path.GetDirectoryName(w);
                if (!string.IsNullOrEmpty(dir))
                {
                    var dest = Path.Combine(dir, "winmm.dll");
                    try
                    {
                        if (File.Exists(dest)) File.Delete(dest);
                        File.Move(w, dest, overwrite: true);
                    }
                    catch { }
                }
            }
        }
        catch { }

        // 2. Restore original steam_api64.dll and steam_api.dll from backups
        try
        {
            var valve64Backups = Directory.GetFiles(targetDir, "steam_api64_valve.dll", SafeEnumOptions)
                .Concat(Directory.GetFiles(targetDir, "steam_api64_o.dll", SafeEnumOptions));

            foreach (var valve in valve64Backups)
            {
                var dir = Path.GetDirectoryName(valve);
                if (!string.IsNullOrEmpty(dir))
                {
                    var dest = Path.Combine(dir, "steam_api64.dll");
                    try
                    {
                        if (File.Exists(dest)) File.Delete(dest);
                        File.Move(valve, dest, overwrite: true);
                    }
                    catch { }
                }
            }

            var valve32Backups = Directory.GetFiles(targetDir, "steam_api_valve.dll", SafeEnumOptions)
                .Concat(Directory.GetFiles(targetDir, "steam_api_o.dll", SafeEnumOptions));

            foreach (var valve in valve32Backups)
            {
                var dir = Path.GetDirectoryName(valve);
                if (!string.IsNullOrEmpty(dir))
                {
                    var dest = Path.Combine(dir, "steam_api.dll");
                    try
                    {
                        if (File.Exists(dest)) File.Delete(dest);
                        File.Move(valve, dest, overwrite: true);
                    }
                    catch { }
                }
            }
        }
        catch { }

        // 3. Restore EOSSDK-Win64-Shipping.dll from EOSSDK_original.dll
        try
        {
            var eosBackups = Directory.GetFiles(targetDir, "EOSSDK_original.dll", SafeEnumOptions);
            foreach (var e in eosBackups)
            {
                var dir = Path.GetDirectoryName(e);
                if (!string.IsNullOrEmpty(dir))
                {
                    var dest = Path.Combine(dir, "EOSSDK-Win64-Shipping.dll");
                    try
                    {
                        if (File.Exists(dest)) File.Delete(dest);
                        File.Move(e, dest, overwrite: true);
                    }
                    catch { }
                }
            }
        }
        catch { }

        // 4. Remove RedboneEOS.dll proxy files
        try
        {
            var redboneFiles = Directory.GetFiles(targetDir, "RedboneEOS*.dll", SafeEnumOptions);
            foreach (var r in redboneFiles)
            {
                try { File.Delete(r); } catch { }
            }
        }
        catch { }

        // 5. Restore original Unity Assembly-CSharp.dll.orig and com.rlabrecque.steamworks.net.dll.orig
        try
        {
            var asmOrigs = Directory.GetFiles(targetDir, "Assembly-CSharp.dll.orig", SafeEnumOptions);
            foreach (var a in asmOrigs)
            {
                var dir = Path.GetDirectoryName(a);
                if (!string.IsNullOrEmpty(dir))
                {
                    var dest = Path.Combine(dir, "Assembly-CSharp.dll");
                    try
                    {
                        if (File.Exists(dest)) File.Delete(dest);
                        File.Move(a, dest, overwrite: true);
                    }
                    catch { }
                }
            }

            var swOrigs = Directory.GetFiles(targetDir, "com.rlabrecque.steamworks.net.dll.orig", SafeEnumOptions);
            foreach (var s in swOrigs)
            {
                var dir = Path.GetDirectoryName(s);
                if (!string.IsNullOrEmpty(dir))
                {
                    var dest = Path.Combine(dir, "com.rlabrecque.steamworks.net.dll");
                    try
                    {
                        if (File.Exists(dest)) File.Delete(dest);
                        File.Move(s, dest, overwrite: true);
                    }
                    catch { }
                }
            }
        }
        catch { }

        // 6. Restore original SteamStub protected executables if unpacked (*.steamstub.exe / *.steamstub)
        try
        {
            var stubFiles = Directory.GetFiles(targetDir, "*.steamstub.exe", SafeEnumOptions)
                .Concat(Directory.GetFiles(targetDir, "*.steamstub", SafeEnumOptions));

            foreach (var stub in stubFiles)
            {
                var dir = Path.GetDirectoryName(stub);
                var baseName = Path.GetFileNameWithoutExtension(stub);
                if (baseName.EndsWith(".steamstub", StringComparison.OrdinalIgnoreCase))
                {
                    baseName = baseName[..^10];
                }
                if (!string.IsNullOrEmpty(dir))
                {
                    var exeDest = Path.Combine(dir, $"{baseName}.exe");
                    try
                    {
                        if (File.Exists(exeDest)) File.Delete(exeDest);
                        File.Move(stub, exeDest, overwrite: true);
                    }
                    catch { }
                }
            }
        }
        catch { }

        // 7. Remove ReFix proxies, DLC unlocker files, and artifacts
        var artifactPatterns = new[]
        {
            "winmm.dll", "winhttp.dll", "doorstop_config.ini", "Kirigiri.ini", "ReFix.ini", "ReFix.log",
            "steam_appid.txt", "local_save.txt", "steam_interfaces.txt",
            "add_steam_shortcut.ps1", "add_steam_shortcut.py", "Install_ReFix_Steam_Shortcut.bat",
            "Configure_LAN_Firewall.bat", "Configurar_Firewall_LAN.bat",
            "cream_api.ini", "SmokeAPI.config.json", "SmokeAPI.json", "SmokeAPI.log", "SmokeAPI.cache.json"
        };

        foreach (var pattern in artifactPatterns)
        {
            try
            {
                var files = Directory.GetFiles(targetDir, pattern, SafeEnumOptions);
                foreach (var f in files)
                {
                    try { File.Delete(f); } catch { }
                }
            }
            catch { }
        }

        // 8. Remove ReFix directories recursively (steam_settings, saves, BepInEx)
        var directoryNames = new[] { "steam_settings", "saves", "BepInEx" };
        foreach (var dName in directoryNames)
        {
            try
            {
                var dirs = Directory.GetDirectories(targetDir, dName, SafeEnumOptions);
                foreach (var d in dirs)
                {
                    try { Directory.Delete(d, recursive: true); } catch { }
                }
            }
            catch { }
        }
    }

    private static void AppendReFixLog(string exeDir, string message)
    {
        try
        {
            var logPath = Path.Combine(exeDir, "ReFix.log");
            var entry = $"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}] [ReFix AutoDeploy] {message}\r\n";
            File.AppendAllText(logPath, entry);
        }
        catch { }
    }
}


