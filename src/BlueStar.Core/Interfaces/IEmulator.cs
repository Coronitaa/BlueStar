using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BlueStar.Core.Models;

namespace BlueStar.Core.Interfaces;

/// <summary>
/// Status information reported by an emulator.
/// </summary>
public record EmulatorStatus
{
    public required string EmulatorId { get; init; }
    public required string EmulatorName { get; init; }
    public bool IsConfigured { get; init; }
    public bool IsActive { get; init; }
    public string StatusMessage { get; init; } = "Inactive";
    public string? BackendInfo { get; init; }
    public string? NetworkInfo { get; init; }
    public IReadOnlyDictionary<string, string> ConfigValues { get; init; } = new Dictionary<string, string>();
}

/// <summary>
/// Abstraction for an emulator provider (e.g. ReFix, SmokeAPI, Generic Steam Emulator).
/// </summary>
public interface IEmulator
{
    /// <summary>
    /// Unique identifier for the emulator (e.g., "refix", "smokeapi").
    /// </summary>
    string Id { get; }

    /// <summary>
    /// Friendly display name for the emulator.
    /// </summary>
    string DisplayName { get; }

    /// <summary>
    /// Description of the emulator and its capabilities.
    /// </summary>
    string Description { get; }

    /// <summary>
    /// Determines whether this emulator can be used for the given game instance.
    /// </summary>
    bool IsSupported(GameInstance instance);

    /// <summary>
    /// Gets the current status of the emulator for the specified instance.
    /// </summary>
    Task<EmulatorStatus> GetStatusAsync(GameInstance instance, CancellationToken ct = default);

    /// <summary>
    /// Enables the emulator for the given instance.
    /// </summary>
    Task<bool> EnableAsync(GameInstance instance, CancellationToken ct = default);

    /// <summary>
    /// Disables the emulator for the given instance.
    /// </summary>
    Task<bool> DisableAsync(GameInstance instance, CancellationToken ct = default);

    /// <summary>
    /// Configures the emulator settings for the given instance.
    /// </summary>
    Task<bool> ConfigureAsync(GameInstance instance, IDictionary<string, string> config, CancellationToken ct = default);
}

/// <summary>
/// Registry to manage multiple emulator providers.
/// </summary>
public interface IEmulatorRegistry
{
    /// <summary>
    /// Gets an emulator by its ID.
    /// </summary>
    IEmulator? GetById(string id);

    /// <summary>
    /// Gets all emulators supported for the given game instance.
    /// </summary>
    IReadOnlyList<IEmulator> GetSupportedEmulators(GameInstance instance);

    /// <summary>
    /// Gets all registered emulators.
    /// </summary>
    IReadOnlyList<IEmulator> GetAllEmulators();
}
