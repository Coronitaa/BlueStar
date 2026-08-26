using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BlueStar.Core.Models;

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
    /// <param name="ct">A token to monitor for cancellation requests.</param>
    /// <returns>A task that represents the asynchronous operation. The task result contains a read-only list of game instances.</returns>
    Task<IReadOnlyList<GameInstance>> GetAllAsync(CancellationToken ct);

    /// <summary>
    /// Retrieves a specific game instance by its unique identifier.
    /// </summary>
    /// <param name="id">The unique identifier of the game instance.</param>
    /// <param name="ct">A token to monitor for cancellation requests.</param>
    /// <returns>A task that represents the asynchronous operation. The task result contains the game instance, or null if it was not found.</returns>
    Task<GameInstance?> GetByIdAsync(Guid id, CancellationToken ct);

    /// <summary>
    /// Creates a new game instance.
    /// </summary>
    /// <param name="instance">The game instance to create.</param>
    /// <param name="ct">A token to monitor for cancellation requests.</param>
    /// <returns>A task that represents the asynchronous operation. The task result contains the created game instance.</returns>
    Task<GameInstance> CreateAsync(GameInstance instance, CancellationToken ct);

    /// <summary>
    /// Updates an existing game instance.
    /// </summary>
    /// <param name="instance">The game instance to update.</param>
    /// <param name="ct">A token to monitor for cancellation requests.</param>
    /// <returns>A task that represents the asynchronous operation. The task result contains the updated game instance.</returns>
    Task<GameInstance> UpdateAsync(GameInstance instance, CancellationToken ct);

    /// <summary>
    /// Deletes a game instance by its unique identifier.
    /// </summary>
    /// <param name="id">The unique identifier of the game instance to delete.</param>
    /// <param name="ct">A token to monitor for cancellation requests.</param>
    /// <returns>A task that represents the asynchronous operation. The task result is true if the instance was successfully deleted; otherwise, false.</returns>
    Task<bool> DeleteAsync(Guid id, CancellationToken ct);
}
