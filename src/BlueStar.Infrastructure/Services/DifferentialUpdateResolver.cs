using System;
using System.Collections.Generic;
using System.Linq;
using BlueStar.Core.Interfaces;
using BlueStar.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BlueStar.Infrastructure.Services;

/// <summary>
/// Implements <see cref="IDifferentialUpdateResolver"/>, calculating intelligent differential update plans
/// that compare installed depots against a target release build to avoid redundant downloads of unchanged depots.
/// </summary>
public sealed class DifferentialUpdateResolver : IDifferentialUpdateResolver
{
    private readonly ILogger<DifferentialUpdateResolver> _logger;

    public DifferentialUpdateResolver(ILogger<DifferentialUpdateResolver>? logger = null)
    {
        _logger = logger ?? NullLogger<DifferentialUpdateResolver>.Instance;
    }

    /// <inheritdoc />
    public DifferentialUpdatePlan ResolvePlan(
        uint appId,
        IEnumerable<DepotInfo> currentDepots,
        IEnumerable<DepotInfo> targetDepots,
        string targetBuildId = "")
    {
        ArgumentNullException.ThrowIfNull(currentDepots);
        ArgumentNullException.ThrowIfNull(targetDepots);

        var currentMap = new Dictionary<uint, DepotInfo>();
        foreach (var d in currentDepots)
        {
            currentMap[d.DepotId] = d;
        }

        var targetList = targetDepots.ToList();
        var targetDepotIds = new HashSet<uint>();

        var unchanged = new List<DepotInfo>();
        var updated = new List<DepotInfo>();
        var added = new List<DepotInfo>();
        var removed = new List<DepotInfo>();

        foreach (var target in targetList)
        {
            targetDepotIds.Add(target.DepotId);

            if (currentMap.TryGetValue(target.DepotId, out var current))
            {
                if (current.ManifestId == target.ManifestId && target.ManifestId > 0)
                {
                    // Identical ManifestId: depot payload is byte-for-byte identical, skip download
                    unchanged.Add(target);
                    _logger.LogDebug("Depot {DepotId} unchanged (ManifestId {ManifestId}); reusing existing local content",
                        target.DepotId, target.ManifestId);
                }
                else
                {
                    // Different ManifestId: content changed, must download updated depot
                    updated.Add(target);
                    _logger.LogDebug("Depot {DepotId} changed (Current: {CurrentManifestId} -> Target: {TargetManifestId}); download scheduled",
                        target.DepotId, current.ManifestId, target.ManifestId);
                }
            }
            else
            {
                // New depot added in target build
                added.Add(target);
                _logger.LogDebug("Depot {DepotId} is newly added in build '{BuildId}'; download scheduled",
                    target.DepotId, targetBuildId);
            }
        }

        // Depots present in current install that no longer exist in target build
        foreach (var current in currentMap.Values)
        {
            if (!targetDepotIds.Contains(current.DepotId))
            {
                removed.Add(current);
                _logger.LogDebug("Depot {DepotId} obsolete in target build '{BuildId}'; marked for removal",
                    current.DepotId, targetBuildId);
            }
        }

        var totalDownloadBytes = updated.Sum(d => d.SizeBytes) + added.Sum(d => d.SizeBytes);
        var totalSavedBytes = unchanged.Sum(d => d.SizeBytes);

        _logger.LogInformation(
            "Differential plan computed for App {AppId} (Build '{BuildId}'): {UnchangedCount} unchanged ({SavedBytes} bytes saved), " +
            "{UpdatedCount} updated, {NewCount} new, {RemovedCount} removed ({DownloadBytes} bytes to download)",
            appId, targetBuildId, unchanged.Count, totalSavedBytes, updated.Count, added.Count, removed.Count, totalDownloadBytes);

        return new DifferentialUpdatePlan
        {
            AppId = appId,
            TargetBuildId = targetBuildId,
            UnchangedDepots = unchanged.AsReadOnly(),
            UpdatedDepots = updated.AsReadOnly(),
            NewDepots = added.AsReadOnly(),
            RemovedDepots = removed.AsReadOnly(),
            TotalDownloadSizeBytes = totalDownloadBytes,
            TotalSavedSizeBytes = totalSavedBytes
        };
    }

    /// <inheritdoc />
    public DifferentialUpdatePlan ResolvePlan(
        uint appId,
        IReadOnlyDictionary<uint, ulong> currentManifestMap,
        IEnumerable<DepotInfo> targetDepots,
        string targetBuildId = "")
    {
        ArgumentNullException.ThrowIfNull(currentManifestMap);
        ArgumentNullException.ThrowIfNull(targetDepots);

        var currentDepots = currentManifestMap.Select(kvp => new DepotInfo
        {
            DepotId = kvp.Key,
            ManifestId = kvp.Value,
            Name = $"Depot {kvp.Key}"
        });

        return ResolvePlan(appId, currentDepots, targetDepots, targetBuildId);
    }

    /// <inheritdoc />
    public DifferentialUpdatePlan ResolvePlan(
        uint appId,
        IReadOnlyDictionary<uint, ulong> currentManifestMap,
        IEnumerable<DepotVersion> targetDepots,
        string targetBuildId = "")
    {
        ArgumentNullException.ThrowIfNull(targetDepots);
        return ResolvePlan(appId, currentManifestMap, targetDepots.Select(d => d.ToDepotInfo()), targetBuildId);
    }

    /// <inheritdoc />
    public DifferentialUpdatePlan ResolvePlan(
        uint appId,
        IEnumerable<DepotInfo> currentDepots,
        IEnumerable<DepotVersion> targetDepots,
        string targetBuildId = "")
    {
        ArgumentNullException.ThrowIfNull(targetDepots);
        return ResolvePlan(appId, currentDepots, targetDepots.Select(d => d.ToDepotInfo()), targetBuildId);
    }
}
