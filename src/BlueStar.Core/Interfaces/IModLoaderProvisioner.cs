using System;
using System.Threading;
using System.Threading.Tasks;
using BlueStar.Core.Models;

namespace BlueStar.Core.Interfaces;

/// <summary>
/// Service for automatically provisioning and configuring mod loaders (BepInEx 5/6, UE4SS, Proxy DLLs)
/// for game instances based on detected engine information.
/// </summary>
public interface IModLoaderProvisioner
{
    /// <summary>
    /// Deploys and configures BepInEx (5.x Mono x86/x64 or 6.x IL2CPP x64) with UnityDoorstop into the game instance.
    /// </summary>
    Task<bool> InstallBepInExAsync(string instancePath, EngineInfo engine, IProgress<double>? progress = null, CancellationToken ct = default);

    /// <summary>
    /// Deploys and configures UE4SS (Unreal Engine 4/5 Scripting System) into Binaries/Win64 with proxy DLL and ~mods folder.
    /// </summary>
    Task<bool> InstallUE4SSAsync(string instancePath, EngineInfo engine, IProgress<double>? progress = null, CancellationToken ct = default);

    /// <summary>
    /// Toggles the enabled state of the installed mod loader for an instance (e.g. by renaming proxy DLL / doorstop config).
    /// </summary>
    Task<bool> ToggleModLoaderAsync(string instancePath, bool enabled, CancellationToken ct = default);

    /// <summary>
    /// Checks if any supported mod loader is currently installed in the instance.
    /// </summary>
    bool IsModLoaderInstalled(string instancePath);

    /// <summary>
    /// Gets the name and version of the installed mod loader if present.
    /// </summary>
    string? GetInstalledModLoader(string instancePath);
}
