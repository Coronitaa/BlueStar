using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BlueStar.Core.Models;

namespace BlueStar.Core.Interfaces;

/// <summary>
/// Provides methods to discover, retrieve metadata, and download game fixes
/// or multiplayer emulators from a specific provider.
/// </summary>
public interface IFixProvider : IProvider
{
    /// <summary>
    /// Checks whether fixes are available for the specified AppID.
    /// </summary>
    Task<bool> HasFixesAsync(uint appId, CancellationToken ct = default);

    /// <summary>
    /// Retrieves all available fixes for the specified AppID.
    /// </summary>
    Task<IReadOnlyList<GameFixInfo>> GetFixesAsync(uint appId, CancellationToken ct = default);

    /// <summary>
    /// Downloads a fix package to the target directory.
    /// </summary>
    Task<string> DownloadFixAsync(GameFixInfo fix, string targetDirectory, IProgress<DownloadProgress>? progress = null, CancellationToken ct = default);
}
