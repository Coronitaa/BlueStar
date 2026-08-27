using System;
using System.Threading;
using System.Threading.Tasks;
using BlueStar.Core.Models;

namespace BlueStar.Core.Interfaces;

/// <summary>
/// Service contract for checking, downloading, and applying ReFix emulator updates from GitHub.
/// </summary>
public interface IReFixUpdateService
{
    /// <summary>
    /// Gets the current locally installed global ReFix version.
    /// </summary>
    string GetCurrentInstalledVersion();

    /// <summary>
    /// Checks GitHub (Coronitaa/ReFix) for new releases.
    /// </summary>
    Task<ReFixVersionInfo?> CheckForUpdatesAsync(CancellationToken ct = default);

    /// <summary>
    /// Downloads and applies the new ReFix update into tools/ReFix_deploy.
    /// </summary>
    Task<bool> DownloadAndApplyUpdateAsync(ReFixVersionInfo update, IProgress<DownloadProgress>? progress = null, CancellationToken ct = default);

    /// <summary>
    /// Performs full auto-update workflow in background (check -> download & apply -> notify -> check instances).
    /// </summary>
    Task CheckAndPerformAutoUpdateAsync(CancellationToken ct = default);

    /// <summary>
    /// Checks all managed instances and notifies if any have an older ReFix installed.
    /// </summary>
    Task CheckAndNotifyOutdatedInstancesAsync(CancellationToken ct = default);

    /// <summary>
    /// Checks if a specific game instance has an outdated version of ReFix installed.
    /// </summary>
    bool IsInstanceReFixOutdated(GameInstance instance);

    /// <summary>
    /// Sequentially updates the emulator on all outdated instances in the background with DLC preservation.
    /// </summary>
    Task<int> UpdateAllOutdatedInstancesAsync(IReadOnlyList<GameInstance>? targetInstances = null, CancellationToken ct = default);
}
