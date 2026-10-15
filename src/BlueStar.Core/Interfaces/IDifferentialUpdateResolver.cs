using System.Collections.Generic;
using BlueStar.Core.Models;

namespace BlueStar.Core.Interfaces;

/// <summary>
/// Resolves differential update plans by comparing current installed depots against target version depots,
/// identifying unchanged depots to prevent redundant downloads and save bandwidth.
/// </summary>
public interface IDifferentialUpdateResolver
{
    /// <summary>
    /// Computes a differential update plan between current installed depots and the target version depots.
    /// </summary>
    /// <param name="appId">The Steam App ID.</param>
    /// <param name="currentDepots">Depots currently installed locally.</param>
    /// <param name="targetDepots">Depots required by the target version.</param>
    /// <param name="targetBuildId">Target build identifier (optional).</param>
    /// <returns>A plan detailing unchanged, updated, new, and removed depots with calculated bandwidth savings.</returns>
    DifferentialUpdatePlan ResolvePlan(
        uint appId,
        IEnumerable<DepotInfo> currentDepots,
        IEnumerable<DepotInfo> targetDepots,
        string targetBuildId = "");

    /// <summary>
    /// Computes a differential update plan using an existing depot manifest mapping.
    /// </summary>
    /// <param name="appId">The Steam App ID.</param>
    /// <param name="currentManifestMap">Dictionary mapping DepotId to currently installed ManifestId.</param>
    /// <param name="targetDepots">Depots required by the target version.</param>
    /// <param name="targetBuildId">Target build identifier (optional).</param>
    /// <returns>A plan detailing unchanged, updated, new, and removed depots with calculated bandwidth savings.</returns>
    DifferentialUpdatePlan ResolvePlan(
        uint appId,
        IReadOnlyDictionary<uint, ulong> currentManifestMap,
        IEnumerable<DepotInfo> targetDepots,
        string targetBuildId = "");
}
