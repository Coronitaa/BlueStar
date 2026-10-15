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
    DifferentialUpdatePlan ResolvePlan(
        uint appId,
        IEnumerable<DepotInfo> currentDepots,
        IEnumerable<DepotInfo> targetDepots,
        string targetBuildId = "");

    /// <summary>
    /// Computes a differential update plan using an existing depot manifest mapping.
    /// </summary>
    DifferentialUpdatePlan ResolvePlan(
        uint appId,
        IReadOnlyDictionary<uint, ulong> currentManifestMap,
        IEnumerable<DepotInfo> targetDepots,
        string targetBuildId = "");

    /// <summary>
    /// Computes a differential update plan using an existing depot manifest mapping and target <see cref="DepotVersion"/> collection.
    /// </summary>
    DifferentialUpdatePlan ResolvePlan(
        uint appId,
        IReadOnlyDictionary<uint, ulong> currentManifestMap,
        IEnumerable<DepotVersion> targetDepots,
        string targetBuildId = "");

    /// <summary>
    /// Computes a differential update plan between current installed depots and target <see cref="DepotVersion"/> collection.
    /// </summary>
    DifferentialUpdatePlan ResolvePlan(
        uint appId,
        IEnumerable<DepotInfo> currentDepots,
        IEnumerable<DepotVersion> targetDepots,
        string targetBuildId = "");
}
