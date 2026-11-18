using BlueStar.Core.Helpers;
using BlueStar.Core.Models;
using Microsoft.Extensions.Logging;

namespace BlueStar.Infrastructure.Emulators;

/// <summary>
/// Linux deploy: ReFix's own AutoDeploy.sh does the DLL swap. It writes ReFix.ini, steam_appid.txt and the Fusion
/// <c>ReFix/</c> folder into the folder it was given, which for Unreal is the game root, not Binaries/Win64 where
/// the exe reads them. The script is overwritten by every ReFix update, so the placement is fixed here instead.
/// </summary>
public sealed partial class ReFixEmulator
{
    private static readonly string[] ScriptConfigFiles = ["ReFix.ini", "steam_appid.txt"];
    private const string FusionFolder = "ReFix";

    /// <summary>AutoDeploy.sh mode for a BlueStar option: lan = ReFix Online + LAN, offline = Offline LAN, online = Online Legacy.</summary>
    internal static string ShellMode(string optionId) =>
        IsLanOfflineOption(optionId) ? "offline"
        : optionId.Contains("goldberg", StringComparison.OrdinalIgnoreCase) ? "lan"
        : "online";

    /// <summary>
    /// Moves what AutoDeploy.sh writes from <paramref name="from"/> to <paramref name="to"/>. Run once before the
    /// script (exe dir to game root, so it updates the player's existing ReFix.ini instead of writing a fresh one)
    /// and once after (game root to exe dir).
    /// </summary>
    internal static void MoveScriptConfig(string from, string to)
    {
        if (string.Equals(Path.GetFullPath(from), Path.GetFullPath(to), StringComparison.Ordinal))
            return;

        foreach (var name in ScriptConfigFiles)
        {
            var source = Path.Combine(from, name);
            if (File.Exists(source))
                File.Move(source, Path.Combine(to, name), overwrite: true);
        }

        var fusion = Path.Combine(from, FusionFolder);
        if (!Directory.Exists(fusion))
            return;

        var target = Path.Combine(to, FusionFolder);
        if (Directory.Exists(target))
            Directory.Delete(target, recursive: true); // ReFix's own Fusion Shared server folder, replaced by the fresh copy
        Directory.Move(fusion, target);
    }

    private async Task<bool> DeployWithAutoDeployShAsync(
        GameInstance instance,
        string optionId,
        string deployPath,
        IProgress<DeployProgress>? progress,
        CancellationToken ct)
    {
        var script = Path.Combine(deployPath, "AutoDeploy.sh");
        if (!File.Exists(script))
        {
            _logger.LogError("AutoDeploy.sh not found in {Path}", deployPath);
            progress?.Report(new DeployProgress { Percentage = 0, Message = "AutoDeploy.sh was not found in the ReFix suite.", CurrentStep = "Error" });
            return false;
        }

        var gameDir = instance.InstallPath.TrimEnd('/', '\\');
        var exeDir = !string.IsNullOrWhiteSpace(instance.ExecutablePath) && File.Exists(instance.ExecutablePath)
            ? Path.GetDirectoryName(instance.ExecutablePath)!
            : gameDir;
        var appId = instance.AppId > 0 ? instance.AppId.ToString() : "480";
        var mode = ShellMode(optionId);
        var userName = !string.IsNullOrWhiteSpace(instance.CustomEmulatorUsername)
            ? instance.CustomEmulatorUsername.Trim()
            : Environment.UserName;

        progress?.Report(new DeployProgress { Percentage = 40, Message = $"Running AutoDeploy.sh ({mode})...", CurrentStep = "DeployHelper" });

        try
        {
            MoveScriptConfig(exeDir, gameDir);
            var (exitCode, _, error) = await RunProcessAsync(
                "bash",
                $"\"{script}\" \"{gameDir}\" {mode} {appId}",
                deployPath,
                line =>
                {
                    _logger.LogInformation("[AutoDeploy.sh] {Line}", line);
                    progress?.Report(new DeployProgress { Percentage = 65, Message = line, CurrentStep = "DeployHelper" });
                },
                ct,
                new Dictionary<string, string> { ["REFIX_USERNAME"] = userName }).ConfigureAwait(false);

            MoveScriptConfig(gameDir, exeDir);

            if (exitCode != 0)
            {
                _logger.LogError("AutoDeploy.sh exited with code {Code}: {Error}", exitCode, error);
                progress?.Report(new DeployProgress { Percentage = 0, Message = $"AutoDeploy.sh failed: {error.Trim()}", CurrentStep = "Error" });
                return false;
            }

            EmulatorIniHelper.ApplyOverrides(exeDir, instance.CustomEmulatorUsername, instance.CustomEmulatorSteamId);
            AppendReFixLog(exeDir, $"Deployed Mode: {GetOptionDisplayName(optionId)} | Game: {instance.Name} | AutoDeploy.sh {mode} | RealAppId: {appId}");
            progress?.Report(new DeployProgress { Percentage = 100, Message = $"Successfully deployed {GetOptionDisplayName(optionId)}!", CurrentStep = "Complete" });
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            _logger.LogError(ex, "Failed to deploy ReFix with AutoDeploy.sh for {Game}", instance.Name);
            progress?.Report(new DeployProgress { Percentage = 0, Message = $"Error during deployment: {ex.Message}", CurrentStep = "Error" });
            return false;
        }
    }

    private async Task RunUninstallShAsync(string deployPath, string targetDir, CancellationToken ct)
    {
        var script = Path.Combine(deployPath, "AutoDeploy.sh");
        if (!File.Exists(script))
            return;

        await RunProcessAsync(
            "bash",
            $"\"{script}\" --uninstall \"{targetDir}\"",
            deployPath,
            line => _logger.LogInformation("[AutoDeploy.sh --uninstall] {Line}", line),
            ct).ConfigureAwait(false);
    }
}
