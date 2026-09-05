using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BlueStar.Core.Interfaces;
using BlueStar.Core.Models;
using Microsoft.Extensions.Logging;

namespace BlueStar.Infrastructure.Services;

/// <summary>
/// Implements <see cref="IInstallationPlanner"/>, computing executable plans
/// with differential depot calculations, local manifest reuse, and key resolution.
/// </summary>
public sealed class InstallationPlanner : IInstallationPlanner
{
    private readonly IManifestCacheService _manifestCache;
    private readonly IDepotKeyRepository? _keyRepository;
    private readonly ILogger<InstallationPlanner> _logger;

    public InstallationPlanner(
        IManifestCacheService manifestCache,
        ILogger<InstallationPlanner> logger,
        IDepotKeyRepository? keyRepository = null)
    {
        _manifestCache = manifestCache ?? throw new ArgumentNullException(nameof(manifestCache));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _keyRepository = keyRepository;
    }

    /// <inheritdoc />
    public async Task<InstallationPlan> CreateInstallPlanAsync(
        GameInstance instance,
        GameVersion targetVersion,
        IEnumerable<uint>? selectedDepotIds = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentNullException.ThrowIfNull(targetVersion);

        var selectedSet = selectedDepotIds != null
            ? new HashSet<uint>(selectedDepotIds)
            : null;

        var targetDepots = targetVersion.Depots
            .Where(d => selectedSet == null || selectedSet.Contains(d.DepotId))
            .ToList();

        var toDownload = new List<PlanDepotItem>();
        var requiredManifests = new List<ManifestArtifact>();

        var depotIds = targetDepots.Select(d => d.DepotId).Distinct().ToList();
        var keys = _keyRepository != null
            ? await _keyRepository.GetKeysAsync(depotIds, ct).ConfigureAwait(false)
            : new Dictionary<uint, string>();

        foreach (var depot in targetDepots)
        {
            keys.TryGetValue(depot.DepotId, out var key);
            var effectiveKey = key ?? depot.DepotKey;

            var localManifestPath = _manifestCache.GetManifestPath(depot.DepotId, depot.ManifestId);

            toDownload.Add(new PlanDepotItem
            {
                DepotId = depot.DepotId,
                ManifestId = depot.ManifestId,
                Name = depot.Name,
                SizeBytes = depot.SizeBytes,
                DepotKey = effectiveKey,
                ManifestFilePath = localManifestPath,
                IsReused = false
            });

            requiredManifests.Add(new ManifestArtifact
            {
                DepotId = depot.DepotId,
                ManifestId = depot.ManifestId,
                SizeBytes = depot.SizeBytes,
                LocalCachePath = localManifestPath
            });
        }

        return new InstallationPlan
        {
            InstanceId = instance.Id,
            AppId = instance.AppId,
            TargetBuildId = targetVersion.BuildId,
            TargetBranch = targetVersion.BranchName,
            InstallPath = instance.InstallPath,
            DepotsToDownload = toDownload.AsReadOnly(),
            ReusedDepots = [],
            RequiredManifests = requiredManifests.AsReadOnly()
        };
    }

    /// <inheritdoc />
    public async Task<InstallationPlan> CreateUpdatePlanAsync(
        GameInstance instance,
        GameVersion targetVersion,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentNullException.ThrowIfNull(targetVersion);

        var installedManifests = instance.InstalledManifestMap ?? new Dictionary<uint, ulong>();
        if (installedManifests.Count == 0 && instance.Depots.Count > 0)
        {
            // Build fallback lookup from existing instance.Depots
            installedManifests = instance.Depots
                .Where(d => d.IsDownloaded)
                .ToDictionary(d => d.DepotId, d => d.ManifestId);
        }

        var toDownload = new List<PlanDepotItem>();
        var reused = new List<PlanDepotItem>();
        var requiredManifests = new List<ManifestArtifact>();

        var depotIds = targetVersion.Depots.Select(d => d.DepotId).Distinct().ToList();
        var keys = _keyRepository != null
            ? await _keyRepository.GetKeysAsync(depotIds, ct).ConfigureAwait(false)
            : new Dictionary<uint, string>();

        foreach (var depot in targetVersion.Depots)
        {
            keys.TryGetValue(depot.DepotId, out var key);
            var effectiveKey = key ?? depot.DepotKey;

            var localManifestPath = _manifestCache.GetManifestPath(depot.DepotId, depot.ManifestId);

            // Check if this depot is already installed with the exact same ManifestId
            if (installedManifests.TryGetValue(depot.DepotId, out var installedManifestId) &&
                installedManifestId == depot.ManifestId)
            {
                // Unchanged depot — reuse existing local files without downloading!
                reused.Add(new PlanDepotItem
                {
                    DepotId = depot.DepotId,
                    ManifestId = depot.ManifestId,
                    PreviousManifestId = installedManifestId,
                    Name = depot.Name,
                    SizeBytes = depot.SizeBytes,
                    DepotKey = effectiveKey,
                    ManifestFilePath = localManifestPath,
                    IsReused = true
                });
            }
            else
            {
                // New or updated depot — schedule download
                toDownload.Add(new PlanDepotItem
                {
                    DepotId = depot.DepotId,
                    ManifestId = depot.ManifestId,
                    PreviousManifestId = installedManifests.TryGetValue(depot.DepotId, out var prev) ? prev : null,
                    Name = depot.Name,
                    SizeBytes = depot.SizeBytes,
                    DepotKey = effectiveKey,
                    ManifestFilePath = localManifestPath,
                    IsReused = false
                });

                requiredManifests.Add(new ManifestArtifact
                {
                    DepotId = depot.DepotId,
                    ManifestId = depot.ManifestId,
                    SizeBytes = depot.SizeBytes,
                    LocalCachePath = localManifestPath
                });
            }
        }

        _logger.LogInformation(
            "Differential plan calculated for {Name}: {ToDownloadCount} depots to download ({Size} bytes), {ReusedCount} depots reused",
            instance.Name, toDownload.Count, toDownload.Sum(d => d.SizeBytes), reused.Count);

        return new InstallationPlan
        {
            InstanceId = instance.Id,
            AppId = instance.AppId,
            TargetBuildId = targetVersion.BuildId,
            TargetBranch = targetVersion.BranchName,
            InstallPath = instance.InstallPath,
            DepotsToDownload = toDownload.AsReadOnly(),
            ReusedDepots = reused.AsReadOnly(),
            RequiredManifests = requiredManifests.AsReadOnly()
        };
    }
}
