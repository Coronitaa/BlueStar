using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BlueStar.Core.Models;

namespace BlueStar.Core.Interfaces;

/// <summary>
/// Service for detecting and installing game prerequisites (Visual C++ Redistributables, DirectX, .NET, Unreal Engine Prereqs).
/// </summary>
public interface IPrerequisiteService
{
    /// <summary>
    /// Detects all relevant prerequisites for the specified game instance (both from game folders and standard requirements).
    /// </summary>
    Task<IReadOnlyList<PrerequisiteItem>> DetectPrerequisitesAsync(GameInstance instance, CancellationToken ct = default);

    /// <summary>
    /// Installs a specific prerequisite (using local installer or downloading if necessary).
    /// </summary>
    Task<bool> InstallPrerequisiteAsync(GameInstance instance, PrerequisiteItem item, IProgress<string>? progress = null, CancellationToken ct = default);

    /// <summary>
    /// Automatically installs all missing or available prerequisites in 1-click.
    /// </summary>
    Task<int> InstallAllPrerequisitesAsync(GameInstance instance, IProgress<string>? progress = null, CancellationToken ct = default);

    /// <summary>
    /// Checks whether a prerequisite is already installed in the host Windows operating system.
    /// </summary>
    bool IsSystemInstalled(PrerequisiteItem item);
}
