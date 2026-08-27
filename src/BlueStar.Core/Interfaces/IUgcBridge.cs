using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace BlueStar.Core.Interfaces;

/// <summary>
/// Bridge service that emulates Steam ISteamUGC subsystem offline, generating VDF manifests,
/// linking content folders, and configuring emulator mod hooks (ReFix / Goldberg).
/// </summary>
public interface IUgcBridge
{
    /// <summary>
    /// Deploys a cached workshop item to an instance's steamapps/workshop/content/<AppId>/<PublishedFileId>/
    /// and mirrors it to steam_settings/mods/ for emulator compatibility.
    /// </summary>
    Task<bool> DeployWorkshopItemToInstanceAsync(
        uint appId,
        ulong publishedFileId,
        string itemCachePath,
        string instancePath,
        CancellationToken ct = default);

    /// <summary>
    /// Synchronizes the instance's appworkshop_<AppId>.vdf and emulator mod mappings for all active workshop items.
    /// </summary>
    Task<bool> SynchronizeInstanceWorkshopAsync(
        uint appId,
        string instancePath,
        IEnumerable<WorkshopItemInfo> installedItems,
        CancellationToken ct = default);

    /// <summary>
    /// Gets the standard Steamworks path where a workshop item's content is expected.
    /// Format: <InstancePath>/steamapps/workshop/content/<AppId>/<PublishedFileId>/
    /// </summary>
    string GetInstanceWorkshopContentPath(string instancePath, uint appId, ulong publishedFileId);

    /// <summary>
    /// Gets the path to the appworkshop_<AppId>.vdf file in an instance.
    /// </summary>
    string GetInstanceWorkshopVdfPath(string instancePath, uint appId);
}
