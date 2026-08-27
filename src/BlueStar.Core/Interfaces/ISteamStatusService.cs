using System;

namespace BlueStar.Core.Interfaces;

/// <summary>
/// Status of the local Steam client and active logged-in user.
/// </summary>
public record SteamStatus(
    bool IsRunning,
    string? AccountName,
    string? PersonaName,
    ulong? SteamId64
)
{
    /// <summary>
    /// Gets the friendly display name for the active Steam user.
    /// </summary>
    public string DisplayName => !string.IsNullOrWhiteSpace(PersonaName)
        ? PersonaName
        : (!string.IsNullOrWhiteSpace(AccountName) ? AccountName : "Steam User");

    /// <summary>
    /// Formatted status summary (e.g. "Steam: Valentín" or "Steam: Not Running").
    /// </summary>
    public string StatusText => IsRunning
        ? $"Steam: {DisplayName}"
        : "Steam: Not Running";
}

/// <summary>
/// Service that monitors the Steam desktop client process and active user in real-time.
/// </summary>
public interface ISteamStatusService : IDisposable
{
    /// <summary>
    /// Current live status of Steam and the active account.
    /// </summary>
    SteamStatus CurrentStatus { get; }

    /// <summary>
    /// Event triggered when Steam is launched, closed, or when the active user profile changes.
    /// </summary>
    event EventHandler<SteamStatus>? StatusChanged;

    /// <summary>
    /// Triggers an immediate status evaluation.
    /// </summary>
    void CheckStatusNow();

    /// <summary>
    /// Launches the local Steam client (if not already running) and waits until Steam is fully loaded,
    /// logged in, and user data is initialized (past the "Loading user data" stage).
    /// </summary>
    /// <param name="timeout">Maximum time to wait. Defaults to 45 seconds if null.</param>
    /// <param name="progress">Optional progress reporter for UI status updates.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if Steam is fully loaded and logged in; otherwise false.</returns>
    System.Threading.Tasks.Task<bool> LaunchAndWaitForSteamFullyLoadedAsync(
        TimeSpan? timeout = null,
        IProgress<string>? progress = null,
        System.Threading.CancellationToken cancellationToken = default);
}
