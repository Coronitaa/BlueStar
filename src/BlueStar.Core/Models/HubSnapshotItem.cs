using System;
using System.Collections.Generic;

namespace BlueStar.Core.Models;

/// <summary>
/// Model representing a complete application build snapshot available from Manifest Hub.
/// Contains all depot manifests for that snapshot, along with transparent authentic publication date provenance.
/// </summary>
public sealed record HubSnapshotItem
{
    public required BuildSnapshot Snapshot { get; init; }

    public string BuildId => Snapshot.BuildId.ToString();

    public string BranchName => !string.IsNullOrWhiteSpace(Snapshot.Branch) ? Snapshot.Branch : "public";

    public string Description => Snapshot.Description;

    public int DepotCount => Snapshot.DepotManifests?.Count ?? 0;

    public IReadOnlyDictionary<uint, ulong> DepotManifests => Snapshot.DepotManifests ?? new Dictionary<uint, ulong>();

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

    public string Provenance => "ManifestHub";
}
