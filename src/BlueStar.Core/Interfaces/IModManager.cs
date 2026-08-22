using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BlueStar.Core.Models;

namespace BlueStar.Core.Interfaces;

/// <summary>
/// Model representing an installed or detected mod.
/// </summary>
public record ModItem
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string? Version { get; init; }
    public string? Author { get; init; }
    public string? Description { get; init; }
    public bool IsEnabled { get; init; } = true;
    public required string FilePath { get; init; }
    public string? Category { get; init; }
    public long SizeBytes { get; init; }
}

/// <summary>
/// Contract for engine-specific or generic mod management.
/// </summary>
public interface IModManager
{
    /// <summary>
    /// Unique identifier for this mod manager (e.g., "unreal-paks", "generic").
    /// </summary>
    string Id { get; }

    /// <summary>
    /// Friendly display name for the mod manager.
    /// </summary>
    string DisplayName { get; }

    /// <summary>
    /// Determines whether this mod manager supports the given game instance.
    /// </summary>
    bool IsSupported(GameInstance instance);

    /// <summary>
    /// Gets all installed mods for the specified instance.
    /// </summary>
    Task<IReadOnlyList<ModItem>> GetInstalledModsAsync(GameInstance instance, CancellationToken ct = default);

    /// <summary>
    /// Installs a mod from an archive (.zip, .pak, .dll, etc.) or loose file.
    /// </summary>
    Task<bool> InstallModAsync(GameInstance instance, string sourceFilePath, CancellationToken ct = default);

    /// <summary>
    /// Uninstalls or removes a mod by its ID.
    /// </summary>
    Task<bool> UninstallModAsync(GameInstance instance, string modId, CancellationToken ct = default);

    /// <summary>
    /// Toggles the enabled/disabled state of a mod.
    /// </summary>
    Task<bool> ToggleModAsync(GameInstance instance, string modId, bool isEnabled, CancellationToken ct = default);

    /// <summary>
    /// Gets the directory where mods are installed for this game.
    /// </summary>
    string GetModsDirectory(GameInstance instance);
}

/// <summary>
/// Registry to look up the appropriate mod manager for any game instance.
/// </summary>
public interface IModManagerRegistry
{
    /// <summary>
    /// Gets the most suitable mod manager for the specified game instance.
    /// </summary>
    IModManager? GetManagerForInstance(GameInstance instance);

    /// <summary>
    /// Gets all registered mod managers.
    /// </summary>
    IReadOnlyList<IModManager> GetAllManagers();
}
