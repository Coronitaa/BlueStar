using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BlueStar.Core.Models;

namespace BlueStar.Core.Interfaces;

/// <summary>
/// Generates concrete, executable installation and update plans.
/// Compares the requested target <see cref="GameVersion"/> against the current installed instance
/// to calculate differential downloads and reuse unchanged depots.
/// </summary>
public interface IInstallationPlanner
{
    /// <summary>
    /// Creates an installation plan for a new or clean game installation.
    /// </summary>
    Task<InstallationPlan> CreateInstallPlanAsync(
        GameInstance instance,
        GameVersion targetVersion,
        IEnumerable<uint>? selectedDepotIds = null,
        CancellationToken ct = default);

    /// <summary>
    /// Creates a differential update plan for an existing instance, identifying changed depots
    /// and retaining untouched depots.
    /// </summary>
    Task<InstallationPlan> CreateUpdatePlanAsync(
        GameInstance instance,
        GameVersion targetVersion,
        CancellationToken ct = default);
}
