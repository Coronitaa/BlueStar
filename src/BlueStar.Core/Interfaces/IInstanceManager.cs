using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BlueStar.Core.Models;
using BlueStar.Core.Storage;

namespace BlueStar.Core.Interfaces;

/// <summary>
/// Provides methods to manage game instances.
/// </summary>
public interface IInstanceManager
{
    /// <summary>
    /// Event raised whenever instances are created, updated, or deleted.
    /// </summary>
    event EventHandler? InstancesChanged;

    /// <summary>
    /// Retrieves all managed game instances.
    /// </summary>
    Task<IReadOnlyList<GameInstance>> GetAllAsync(CancellationToken ct);

    /// <summary>
    /// Retrieves a specific game instance by its unique identifier.
    /// </summary>
    Task<GameInstance?> GetByIdAsync(Guid id, CancellationToken ct);

    /// <summary>
    /// Creates a new game instance.
    /// </summary>
    Task<GameInstance> CreateAsync(GameInstance instance, CancellationToken ct);

    /// <summary>
    /// Updates an existing game instance.
    /// </summary>
    Task<GameInstance> UpdateAsync(GameInstance instance, CancellationToken ct);

    /// <summary>
    /// Deletes a game instance by its unique identifier.
    /// </summary>
    Task<bool> DeleteAsync(Guid id, CancellationToken ct);

    /// <summary>
    /// Deploys a new zero-copy game instance from an immutable base depot with isolated ReFix settings.
    /// </summary>
    Task<GameInstance> CreateInstanceFromDepotAsync(
        uint appId,
        string instanceName,
        string depotPath,
        string? customInstancePath = null,
        InstanceDeployOptions? deployOptions = null,
        CancellationToken ct = default);

    /// <summary>
    /// Clones an existing game instance with zero-copy hardlinks, allocating unique SteamID and ports.
    /// </summary>
    Task<GameInstance> CloneInstanceAsync(
        Guid sourceInstanceId,
        string newInstanceName,
        CancellationToken ct = default);

    /// <summary>
    /// Gets the standard immutable base depot path for an AppId (e.g., data/depots/<AppId>_base/).
    /// </summary>
    string GetBaseDepotPath(uint appId);
}
