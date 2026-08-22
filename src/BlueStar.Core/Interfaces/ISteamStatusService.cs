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
}
