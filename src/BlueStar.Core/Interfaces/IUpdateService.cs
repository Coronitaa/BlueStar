using System;
using System.Threading;
using System.Threading.Tasks;
using BlueStar.Core.Models;

namespace BlueStar.Core.Interfaces;

/// <summary>
/// Provides methods for checking and applying application updates.
/// </summary>
public interface IUpdateService
{
    /// <summary>
    /// Checks if a new update is available.
    /// </summary>
    /// <param name="ct">A token to monitor for cancellation requests.</param>
    /// <returns>A task that represents the asynchronous operation. The task result contains the update information, or null if no update is available.</returns>
    Task<UpdateInfo?> CheckForUpdatesAsync(CancellationToken ct);

    /// <summary>
    /// Downloads a specific update.
    /// </summary>
    /// <param name="update">The update information to download.</param>
    /// <param name="progress">An optional provider for progress updates.</param>
    /// <param name="ct">A token to monitor for cancellation requests.</param>
    /// <returns>A task that represents the asynchronous operation. The task result is the file path of the downloaded update.</returns>
    Task<string> DownloadUpdateAsync(UpdateInfo update, IProgress<DownloadProgress>? progress, CancellationToken ct);

    /// <summary>
    /// Applies a previously downloaded update.
    /// </summary>
    /// <param name="updateFilePath">The file path to the downloaded update file.</param>
    /// <param name="ct">A token to monitor for cancellation requests.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    Task ApplyUpdateAsync(string updateFilePath, CancellationToken ct);
}
