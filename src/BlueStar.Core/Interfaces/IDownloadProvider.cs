using System;
using System.Threading;
using System.Threading.Tasks;
using BlueStar.Core.Models;

namespace BlueStar.Core.Interfaces;

/// <summary>
/// Provides methods to download and validate game instances.
/// </summary>
public interface IDownloadProvider
{
    /// <summary>
    /// Starts or resumes the download of a game instance.
    /// </summary>
    /// <param name="instance">The game instance to download.</param>
    /// <param name="progress">An optional provider for progress updates.</param>
    /// <param name="ct">A token to monitor for cancellation requests.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    Task DownloadAsync(GameInstance instance, IProgress<DownloadProgress>? progress, CancellationToken ct);

    /// <summary>
    /// Validates the installation files of a game instance.
    /// </summary>
    /// <param name="instance">The game instance to validate.</param>
    /// <param name="ct">A token to monitor for cancellation requests.</param>
    /// <returns>A task that represents the asynchronous operation. The task result is true if the files are valid; otherwise, false.</returns>
    Task<bool> ValidateAsync(GameInstance instance, CancellationToken ct);

    /// <summary>
    /// Cancels an ongoing download for a specific game instance.
    /// </summary>
    /// <param name="instanceId">The unique identifier of the game instance.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    Task CancelAsync(Guid instanceId);
}
