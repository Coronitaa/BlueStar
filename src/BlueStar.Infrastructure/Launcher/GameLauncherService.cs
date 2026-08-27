using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using BlueStar.Core.Helpers;
using BlueStar.Core.Interfaces;
using BlueStar.Core.Models;
using Microsoft.Extensions.Logging;

namespace BlueStar.Infrastructure.Launcher;

/// <summary>
/// Service that launches, monitors, captures logs, and terminates game processes.
/// </summary>
public sealed class GameLauncherService : IGameLauncher
{
    private readonly IInstanceManager? _instanceManager;
    private readonly IEngineDetector? _engineDetector;
    private readonly ICommunityStatsService? _statsService;
    private readonly ILogger<GameLauncherService> _logger;
    private readonly ConcurrentDictionary<Guid, Process> _runningProcesses = new();

    public event EventHandler<(Guid InstanceId, bool IsRunning)>? RunningStateChanged;
    public event EventHandler<(Guid InstanceId, string LogLine)>? LogReceived;

    public GameLauncherService(
        ILogger<GameLauncherService> logger,
        IInstanceManager? instanceManager = null,
        IEngineDetector? engineDetector = null,
        ICommunityStatsService? statsService = null)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _instanceManager = instanceManager;
        _engineDetector = engineDetector;
        _statsService = statsService;
    }

    public async Task<GameLaunchResult> LaunchAsync(GameInstance instance, Action<string>? onLog = null, CancellationToken ct = default)
    {
        if (instance is null)
            return new GameLaunchResult(false, "Instance cannot be null.");

        if (instance.Origin == InstanceOrigin.Steam)
        {
            // If install directory or executable is not present on disk, launch via Steam protocol
            var needsSteamProtocol = string.IsNullOrWhiteSpace(instance.InstallPath) ||
                                     !Directory.Exists(instance.InstallPath) ||
                                     string.IsNullOrWhiteSpace(instance.ExecutablePath) ||
                                     !File.Exists(instance.ExecutablePath);

            if (needsSteamProtocol && instance.AppId > 0)
            {
                try
                {
                    _logger.LogInformation("Launching Steam game {Name} (AppID: {AppId}) via Steam client protocol...", instance.Name, instance.AppId);
                    Process.Start(new ProcessStartInfo($"steam://rungameid/{instance.AppId}") { UseShellExecute = true });
                    return new GameLaunchResult(true, "Launched via Steam");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to launch Steam game via protocol");
                    return new GameLaunchResult(false, $"Failed to launch Steam game: {ex.Message}");
                }
            }
        }

        if (string.IsNullOrWhiteSpace(instance.InstallPath) || !Directory.Exists(instance.InstallPath))
            return new GameLaunchResult(false, "Game install directory does not exist.");

        if (IsRunning(instance.Id))
            return new GameLaunchResult(false, "Game is already running.");

        // Resolve executable path
        string? exePath = instance.ExecutablePath;
        if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
        {
            exePath = _engineDetector?.FindPrimaryExecutable(instance.InstallPath, instance.Name)
                      ?? ShortcutHelper.FindGameExecutables(instance.InstallPath, instance.Name).FirstOrDefault();
        }

        if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
        {
            if (instance.Origin == InstanceOrigin.Steam && instance.AppId > 0)
            {
                try
                {
                    _logger.LogInformation("Executable not found locally, launching Steam game {Name} via Steam protocol...", instance.Name);
                    Process.Start(new ProcessStartInfo($"steam://rungameid/{instance.AppId}") { UseShellExecute = true });
                    return new GameLaunchResult(true, "Launched via Steam");
                }
                catch (Exception ex)
                {
                    return new GameLaunchResult(false, $"Failed to launch Steam game: {ex.Message}");
                }
            }

            return new GameLaunchResult(false, "No executable (.exe) found in the game folder. Please configure the executable in instance settings.");
        }

        var workingDir = Path.GetDirectoryName(exePath) ?? instance.InstallPath;

        var psi = new ProcessStartInfo
        {
            FileName = exePath,
            WorkingDirectory = workingDir,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = false
        };

        // Add custom launch arguments
        if (!string.IsNullOrWhiteSpace(instance.LaunchArguments))
        {
            psi.Arguments = instance.LaunchArguments;
        }

        // Set fix environment variables for Unreal & older clients if needed
        psi.Environment["OPENSSL_ia32cap"] = "~0x20000000";

        try
        {
            _logger.LogInformation("Launching game {GameName} via {ExePath} with args: '{Args}'",
                instance.Name, exePath, psi.Arguments);

            var process = new Process { StartInfo = psi, EnableRaisingEvents = true };

            process.OutputDataReceived += (_, e) =>
            {
                if (string.IsNullOrEmpty(e.Data)) return;
                _logger.LogDebug("[Game {Game}] {Log}", instance.Name, e.Data);
                onLog?.Invoke(e.Data);
                LogReceived?.Invoke(this, (instance.Id, e.Data));
            };

            process.ErrorDataReceived += (_, e) =>
            {
                if (string.IsNullOrEmpty(e.Data)) return;
                _logger.LogWarning("[Game {Game} ERR] {Log}", instance.Name, e.Data);
                onLog?.Invoke($"[ERR] {e.Data}");
                LogReceived?.Invoke(this, (instance.Id, $"[ERR] {e.Data}"));
            };

            var startTime = DateTimeOffset.UtcNow;

            process.Exited += async (_, _) =>
            {
                var duration = DateTimeOffset.UtcNow - startTime;
                _logger.LogInformation("Game {GameName} exited. Session duration: {Duration}", instance.Name, duration);

                _runningProcesses.TryRemove(instance.Id, out _);
                RunningStateChanged?.Invoke(this, (instance.Id, false));

                // Update instance in manager with playtime and status Ready
                if (_instanceManager != null)
                {
                    try
                    {
                        var fresh = await _instanceManager.GetByIdAsync(instance.Id, CancellationToken.None).ConfigureAwait(false) ?? instance;
                        var updated = fresh with
                        {
                            Status = InstanceStatus.Ready,
                            LastPlayedAt = DateTimeOffset.UtcNow,
                            TotalPlayTime = fresh.TotalPlayTime + duration
                        };
                        await _instanceManager.UpdateAsync(updated, CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to persist instance status on exit for {Game}", instance.Name);
                    }
                }

                if (_statsService != null && instance.AppId > 0)
                {
                    _ = _statsService.ReportGamePlayAsync((int)instance.AppId, instance.Name, duration, CancellationToken.None);
                }
            };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            _runningProcesses[instance.Id] = process;
            RunningStateChanged?.Invoke(this, (instance.Id, true));

            // Update instance status to Running and save last played time
            if (_instanceManager != null)
            {
                try
                {
                    var updated = instance with
                    {
                        Status = InstanceStatus.Running,
                        LastPlayedAt = startTime,
                        ExecutablePath = exePath
                    };
                    await _instanceManager.UpdateAsync(updated, CancellationToken.None).ConfigureAwait(false);
                }
                catch { }
            }

            return new GameLaunchResult(true, "Game launched successfully.", process.Id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to launch {GameName}", instance.Name);
            return new GameLaunchResult(false, $"Launch failed: {ex.Message}");
        }
    }

    public Task<bool> KillAsync(Guid instanceId)
    {
        if (_runningProcesses.TryGetValue(instanceId, out var process))
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
                return Task.FromResult(true);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to kill process for instance {Id}", instanceId);
            }
        }
        return Task.FromResult(false);
    }

    public bool IsRunning(Guid instanceId)
    {
        if (_runningProcesses.TryGetValue(instanceId, out var process))
        {
            return !process.HasExited;
        }
        return false;
    }

    public int? GetProcessId(Guid instanceId)
    {
        if (_runningProcesses.TryGetValue(instanceId, out var process))
        {
            return process.HasExited ? null : process.Id;
        }
        return null;
    }
}
