using System;
using System.Threading;
using System.Threading.Tasks;
using BlueStar.Core.Models;

namespace BlueStar.Core.Interfaces;

/// <summary>
/// Result of a game launch operation.
/// </summary>
public record GameLaunchResult(bool Success, string Message, int? ProcessId = null);

/// <summary>
/// Service responsible for launching, monitoring, and terminating game processes.
/// </summary>
public interface IGameLauncher
{
    /// <summary>
    /// Event fired when an instance's running state changes.
    /// </summary>
    event EventHandler<(Guid InstanceId, bool IsRunning)>? RunningStateChanged;

    /// <summary>
    /// Event fired when a log line is received from a running game instance.
    /// </summary>
    event EventHandler<(Guid InstanceId, string LogLine)>? LogReceived;

    /// <summary>
    /// Launches a game instance asynchronously.
    /// </summary>
    Task<GameLaunchResult> LaunchAsync(GameInstance instance, Action<string>? onLog = null, CancellationToken ct = default);

    /// <summary>
    /// Terminates a running game instance.
    /// </summary>
    Task<bool> KillAsync(Guid instanceId);

    /// <summary>
    /// Checks if a game instance process is currently running.
    /// </summary>
    bool IsRunning(Guid instanceId);

    /// <summary>
    /// Gets the process ID of a running instance, if active.
    /// </summary>
    int? GetProcessId(Guid instanceId);
}
