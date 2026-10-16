using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BlueStar.Core.Interfaces;
using BlueStar.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BlueStar.Infrastructure.Services;

/// <summary>
/// Implements <see cref="IInstallationPlanner"/>, computing executable plans
/// with differential depot calculations, local manifest reuse, and key resolution.
/// </summary>
public sealed class InstallationPlanner : IInstallationPlanner
{
    private readonly IManifestCacheService _manifestCache;
    private readonly IDifferentialUpdateResolver _differentialResolver;
    private readonly IDepotKeyRepository? _keyRepository;
    private readonly ILogger<InstallationPlanner> _logger;

    public InstallationPlanner(
        IManifestCacheService manifestCache,
        ILogger<InstallationPlanner> logger,
        IDepotKeyRepository? keyRepository = null,
        IDifferentialUpdateResolver? differentialResolver = null)
    {
        _manifestCache = manifestCache ?? throw new ArgumentNullException(nameof(manifestCache));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _keyRepository = keyRepository;
        _differentialResolver = differentialResolver ?? new DifferentialUpdateResolver(NullLogger<DifferentialUpdateResolver>.Instance);
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

        // Use IDifferentialUpdateResolver to actively resolve identical vs updated/new depots
        var diffPlan = _differentialResolver.ResolvePlan(
            instance.AppId,
            installedManifests,
            targetVersion.Depots,
            targetVersion.BuildId);

        var toDownload = new List<PlanDepotItem>();
        var reused = new List<PlanDepotItem>();
        var requiredManifests = new List<ManifestArtifact>();

        var depotIds = targetVersion.Depots.Select(d => d.DepotId).Distinct().ToList();
        var keys = _keyRepository != null
            ? await _keyRepository.GetKeysAsync(depotIds, ct).ConfigureAwait(false)
            : new Dictionary<uint, string>();

        // 1. Process Unchanged Depots (filtered out from download; reused from local disk)
        foreach (var depot in diffPlan.UnchangedDepots)
        {
            keys.TryGetValue(depot.DepotId, out var key);
            var effectiveKey = key ?? depot.DepotKey;
            var localManifestPath = _manifestCache.GetManifestPath(depot.DepotId, depot.ManifestId);

            installedManifests.TryGetValue(depot.DepotId, out var prevManifest);

            reused.Add(new PlanDepotItem
            {
                DepotId = depot.DepotId,
                ManifestId = depot.ManifestId,
                PreviousManifestId = prevManifest != 0 ? prevManifest : depot.ManifestId,
                Name = depot.Name,
                SizeBytes = depot.SizeBytes,
                DepotKey = effectiveKey,
                ManifestFilePath = localManifestPath,
                IsReused = true
            });
        }

        // 2. Process Updated and Newly Added Depots (scheduled for active download)
        var changedDepotFilter = (targetVersion.ChangedDepots != null && targetVersion.ChangedDepots.Count > 0)
            ? new HashSet<uint>(targetVersion.ChangedDepots)
            : null;

        foreach (var depot in diffPlan.UpdatedDepots.Concat(diffPlan.NewDepots))
        {
            keys.TryGetValue(depot.DepotId, out var key);
            var effectiveKey = key ?? depot.DepotKey;
            var localManifestPath = _manifestCache.GetManifestPath(depot.DepotId, depot.ManifestId);

            installedManifests.TryGetValue(depot.DepotId, out var prevManifest);

            // If ChangedDepots was explicitly specified, non-changed depots already installed are reused
            if (changedDepotFilter != null && !changedDepotFilter.Contains(depot.DepotId) && prevManifest != 0)
            {
                reused.Add(new PlanDepotItem
                {
                    DepotId = depot.DepotId,
                    ManifestId = depot.ManifestId,
                    PreviousManifestId = prevManifest,
                    Name = depot.Name,
                    SizeBytes = depot.SizeBytes,
                    DepotKey = effectiveKey,
                    ManifestFilePath = localManifestPath,
                    IsReused = true
                });
                continue;
            }

            toDownload.Add(new PlanDepotItem
            {
                DepotId = depot.DepotId,
                ManifestId = depot.ManifestId,
                PreviousManifestId = prevManifest != 0 ? prevManifest : null,
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

        _logger.LogInformation(
            "Differential plan calculated via IDifferentialUpdateResolver for {Name}: {ToDownloadCount} depots to download ({DownloadSize} bytes), {ReusedCount} depots reused ({SavedSize} bytes saved)",
            instance.Name, toDownload.Count, toDownload.Sum(d => d.SizeBytes), reused.Count, diffPlan.TotalSavedSizeBytes);

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

    /// <inheritdoc />
    public async Task<InstallationPlan> CreateUpdatePlanAsync(
        GameInstance instance,
        BuildSnapshot snapshot,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentNullException.ThrowIfNull(snapshot);

        var targetDepots = snapshot.DepotManifests.Select(kvp => new DepotVersion
        {
            DepotId = kvp.Key,
            ManifestId = kvp.Value,
            Name = $"Depot {kvp.Key}",
            IsRecommended = true
        }).ToList();

        var patchDate = !string.IsNullOrWhiteSpace(snapshot.PatchDate)
            ? snapshot.PatchDate
            : (snapshot.CreationTime?.ToString("yyyy-MM-dd") ?? snapshot.Timestamp.ToString("yyyy-MM-dd"));

        var targetVersion = new GameVersion
        {
            BuildId = snapshot.BuildId.ToString(),
            BranchName = snapshot.Branch,
            DisplayName = !string.IsNullOrWhiteSpace(snapshot.Description) ? snapshot.Description : $"Build {snapshot.BuildId}",
            PatchDate = patchDate,
            ChangedDepots = snapshot.ChangedDepots,
            UpdatedAt = snapshot.CreationTime ?? snapshot.Timestamp,
            Source = "ManifestHub",
            Depots = targetDepots.AsReadOnly()
        };

        return await CreateUpdatePlanAsync(instance, targetVersion, ct).ConfigureAwait(false);
    }
}
