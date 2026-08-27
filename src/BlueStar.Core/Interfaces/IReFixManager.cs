using System;
using System.Threading;
using System.Threading.Tasks;

namespace BlueStar.Core.Interfaces;

/// <summary>
/// Configuration for an instance''s isolated ReFix / Goldberg emulator settings.
/// </summary>
public record InstanceReFixConfig
{
    public required uint AppId { get; init; }
    public ulong SteamId { get; init; } = 76561198000000001UL;
    public string AccountName { get; init; } = "Player_1";
    public int ListenPort { get; init; } = 47584;
    public bool DisableOverlay { get; init; } = true;
    public bool LocalSave { get; init; } = true;
    public bool DisableNetworking { get; init; } = false;
}

/// <summary>
/// Service that configures and maintains isolated per-instance emulator settings (steam_settings/)
/// for ReFix and Goldberg to enable concurrent local LAN multiplayer without SteamID or port collisions.
/// </summary>
public interface IReFixManager
{
    /// <summary>
    /// Writes isolated steam_settings/ files (steam_appid.txt, force_steamid.txt, force_account_name.txt, listen_port.txt, etc.)
    /// into the game instance directory.
    /// </summary>
    Task<bool> ConfigureInstanceSettingsAsync(string instancePath, InstanceReFixConfig config, CancellationToken ct = default);

    /// <summary>
    /// Reads and parses the active steam_settings/ configuration from a game instance directory.
    /// </summary>
    Task<InstanceReFixConfig?> ReadInstanceSettingsAsync(string instancePath, CancellationToken ct = default);

    /// <summary>
    /// Generates a deterministic, unique 64-bit SteamID for an instance.
    /// </summary>
    ulong GenerateUniqueSteamId(Guid instanceId, int slotIndex = 0);

    /// <summary>
    /// Allocates a unique network listen port for an instance.
    /// </summary>
    int GenerateUniquePort(int slotIndex = 0);
}
