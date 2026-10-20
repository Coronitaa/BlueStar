using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace BlueStar.Core.Models;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DepotDiffStatus
{
    Unchanged,
    Modified,
    Added,
    Removed
}

public sealed record VersionDepotDiffItem(
    [property: JsonPropertyName("depotId")] uint DepotId,
    [property: JsonPropertyName("depotName")] string DepotName,
    [property: JsonPropertyName("baseManifestId")] ulong? BaseManifestId,
    [property: JsonPropertyName("targetManifestId")] ulong? TargetManifestId,
    [property: JsonPropertyName("sizeBytes")] long SizeBytes,
    [property: JsonPropertyName("status")] DepotDiffStatus Status,
    [property: JsonPropertyName("isAvailableInHub")] bool IsAvailableInHub
)
{
    public string FormattedSize => SizeBytes > 0
        ? $"{SizeBytes / (1024.0 * 1024.0):F2} MB"
        : "0 MB";

    public string StatusBadge => Status switch
    {
        DepotDiffStatus.Unchanged => "Sin cambios (reutilizado)",
        DepotDiffStatus.Modified => "Actualizado",
        DepotDiffStatus.Added => "Nuevo",
        DepotDiffStatus.Removed => "Retirado",
        _ => "Desconocido"
    };
}

public sealed class SnapshotDiffResult
{
    [JsonPropertyName("appId")]
    public uint AppId { get; init; }

    [JsonPropertyName("baseBuildId")]
    public ulong? BaseBuildId { get; init; }

    [JsonPropertyName("targetBuildId")]
    public ulong TargetBuildId { get; init; }

    [JsonPropertyName("branch")]
    public string Branch { get; init; } = "public";

    [JsonPropertyName("isCompleteComparison")]
    public bool IsCompleteComparison { get; init; } = true;

    [JsonPropertyName("warningMessage")]
    public string? WarningMessage { get; init; }

    [JsonPropertyName("unchanged")]
    public IReadOnlyList<VersionDepotDiffItem> Unchanged { get; init; } = Array.Empty<VersionDepotDiffItem>();

    [JsonPropertyName("modified")]
    public IReadOnlyList<VersionDepotDiffItem> Modified { get; init; } = Array.Empty<VersionDepotDiffItem>();

    [JsonPropertyName("added")]
    public IReadOnlyList<VersionDepotDiffItem> Added { get; init; } = Array.Empty<VersionDepotDiffItem>();

    [JsonPropertyName("removed")]
    public IReadOnlyList<VersionDepotDiffItem> Removed { get; init; } = Array.Empty<VersionDepotDiffItem>();

    [JsonPropertyName("totalDownloadSizeBytes")]
    public long TotalDownloadSizeBytes { get; init; }

    [JsonPropertyName("totalSavedSizeBytes")]
    public long TotalSavedSizeBytes { get; init; }

    public string FormattedDownloadSize => TotalDownloadSizeBytes > 0
        ? $"{TotalDownloadSizeBytes / (1024.0 * 1024.0 * 1024.0):F2} GB"
        : "0 GB";

    public string FormattedSavedSize => TotalSavedSizeBytes > 0
        ? $"{TotalSavedSizeBytes / (1024.0 * 1024.0 * 1024.0):F2} GB"
        : "0 GB";
}
