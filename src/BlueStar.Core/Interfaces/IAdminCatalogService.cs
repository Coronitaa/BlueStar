using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BlueStar.Core.Models;

namespace BlueStar.Core.Interfaces;

/// <summary>
/// Reads the admin catalog (Explore additions, hidden games, install presets) published by the
/// BlueStar admin panel. Never throws: a missing or invalid catalog is the empty catalog.
/// </summary>
public interface IAdminCatalogService
{
    /// <summary>The last catalog loaded, or <see cref="AdminCatalog.Empty"/> before the first load.</summary>
    AdminCatalog Current { get; }

    /// <summary>Loads the catalog from the network (falling back to the disk cache) at most once per run unless forced.</summary>
    Task<AdminCatalog> GetAsync(bool forceRefresh = false, CancellationToken ct = default);

    /// <summary>The presets the admin attached to a game, in the admin's order.</summary>
    Task<IReadOnlyList<AdminInstallPreset>> GetPresetsAsync(uint appId, CancellationToken ct = default);
}

/// <summary>
/// Lays down the parts of an admin preset BlueStar has no built-in step for: a custom emulator and mods.
/// </summary>
public interface IAdminPresetDeployer
{
    /// <summary>Deploys the preset named by <see cref="GameInstance.PendingAdminPresetId"/>; returns false if anything failed.</summary>
    Task<bool> DeployPendingAsync(GameInstance instance, IProgress<DeployProgress>? progress = null, CancellationToken ct = default);
}
