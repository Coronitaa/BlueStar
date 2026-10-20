using System;
using System.Collections.Generic;
using System.Linq;
using BlueStar.Core.Interfaces;

namespace BlueStar.Core.Models;

/// <summary>
/// Model representing a complete application build snapshot available from Manifest Hub.
/// Contains all depot manifests for that snapshot, along with transparent authentic publication date provenance
/// and differential inspection capabilities against the locally installed instance.
/// </summary>
public sealed record HubSnapshotItem
{
    public required BuildSnapshot Snapshot { get; init; }

    public string BuildId => Snapshot.BuildId.ToString();

    public string BranchName => !string.IsNullOrWhiteSpace(Snapshot.Branch) ? Snapshot.Branch : "public";

    public string Description => Snapshot.Description;

    public int DepotCount => Snapshot.DepotManifests?.Count ?? 0;

    public IReadOnlyDictionary<uint, ulong> DepotManifests => Snapshot.DepotManifests ?? new Dictionary<uint, ulong>();

    public bool IsCompleteSnapshot => Snapshot.Completeness == SnapshotCompleteness.Complete;

    public string CoverageStatus => IsCompleteSnapshot ? "Completo (verificado)" : "Parcial / Delta";

    /// <summary>
    /// Gets whether the snapshot has a reliable authentic publication date from the original source.
    /// Strictly false if CreationTime is null, UnixEpoch, or MinValue.
    /// </summary>
    public bool HasAuthenticDate =>
        Snapshot.CreationTime.HasValue &&
        Snapshot.CreationTime.Value > DateTimeOffset.UnixEpoch &&
        Snapshot.CreationTime.Value > DateTimeOffset.MinValue;

    public string AuthenticDateDisplay => HasAuthenticDate
        ? Snapshot.CreationTime!.Value.LocalDateTime.ToString("yyyy-MM-dd HH:mm")
        : string.Empty;

    public bool HasIngestionDate =>
        Snapshot.Timestamp > DateTimeOffset.UnixEpoch &&
        Snapshot.Timestamp > DateTimeOffset.MinValue;

    public string IngestionDateDisplay => HasIngestionDate
        ? Snapshot.Timestamp.LocalDateTime.ToString("yyyy-MM-dd HH:mm")
        : string.Empty;

    public string DisplayDate => HasAuthenticDate
        ? AuthenticDateDisplay
        : (HasIngestionDate ? $"Ingerido: {IngestionDateDisplay}" : "Fecha desconocida");

    public string Provenance => "ManifestHub";

    public IReadOnlyList<VersionDepotDiffItem> DiffItems { get; init; } = Array.Empty<VersionDepotDiffItem>();

    public int UnchangedCount => DiffItems.Count(d => d.Status == DepotDiffStatus.Unchanged);
    public int ModifiedCount => DiffItems.Count(d => d.Status == DepotDiffStatus.Modified);
    public int AddedCount => DiffItems.Count(d => d.Status == DepotDiffStatus.Added);
    public int RemovedCount => DiffItems.Count(d => d.Status == DepotDiffStatus.Removed);

    public bool HasDiffDetails => DiffItems.Count > 0;
    public bool HasAddedDepots => AddedCount > 0;
    public bool HasModifiedDepots => ModifiedCount > 0;
    public bool HasUnchangedDepots => UnchangedCount > 0;
    public bool HasRemovedDepots => RemovedCount > 0;

    /// <summary>
    /// Computes differential comparison against installed manifests and returns a copy of this item with populated DiffItems.
    /// </summary>
    public HubSnapshotItem WithDiffAgainst(IReadOnlyDictionary<uint, ulong>? installedManifestMap, IManifestCacheService? cache = null)
    {
        var installed = installedManifestMap ?? new Dictionary<uint, ulong>();
        var items = new List<VersionDepotDiffItem>();

        foreach (var (depotId, targetManifestId) in DepotManifests)
        {
            bool isCached = cache?.HasManifest(depotId, targetManifestId) ?? false;
            if (installed.TryGetValue(depotId, out ulong currentManifestId))
            {
                if (currentManifestId == targetManifestId)
                {
                    items.Add(new VersionDepotDiffItem(depotId, $"Depot {depotId}", currentManifestId, targetManifestId, 0, DepotDiffStatus.Unchanged, isCached));
                }
                else
                {
                    items.Add(new VersionDepotDiffItem(depotId, $"Depot {depotId}", currentManifestId, targetManifestId, 0, DepotDiffStatus.Modified, isCached));
                }
            }
            else
            {
                items.Add(new VersionDepotDiffItem(depotId, $"Depot {depotId}", null, targetManifestId, 0, DepotDiffStatus.Added, isCached));
            }
        }

        foreach (var (depotId, installedManifestId) in installed)
        {
            if (!DepotManifests.ContainsKey(depotId))
            {
                items.Add(new VersionDepotDiffItem(depotId, $"Depot {depotId}", installedManifestId, null, 0, DepotDiffStatus.Removed, true));
            }
        }

        return this with { DiffItems = items.AsReadOnly() };
    }
}
