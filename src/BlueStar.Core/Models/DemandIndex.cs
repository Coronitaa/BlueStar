using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace BlueStar.Core.Models;

/// <summary>
/// Represents a demanded manifest record requested by the community or missing from Manifest Hub.
/// </summary>
[JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
public sealed class DemandRecord
{
    [JsonPropertyName("appId")]
    public uint AppId { get; init; }

    [JsonPropertyName("depotId")]
    public uint DepotId { get; init; }

    [JsonPropertyName("manifestId")]
    public ulong ManifestId { get; init; }

    [JsonPropertyName("appName")]
    public string AppName { get; init; } = string.Empty;

    [JsonPropertyName("priority")]
    public int Priority { get; init; }

    [JsonPropertyName("stardustReward")]
    public int StardustReward { get; init; } = 100;

    [JsonPropertyName("detectedAt")]
    public DateTimeOffset? DetectedAt { get; init; }

    [JsonPropertyName("requestedAt")]
    public DateTimeOffset? RequestedAt { get; init; }

    [JsonPropertyName("requestCount")]
    public int RequestCount { get; init; } = 1;
}

/// <summary>
/// Index model representing active community manifest demand (indices/demand.json).
/// </summary>
public sealed class DemandIndex
{
    [JsonPropertyName("schemaVersion")]
    public string SchemaVersion { get; init; } = "v1";

    [JsonPropertyName("generatedAt")]
    public DateTimeOffset GeneratedAt { get; init; } = DateTimeOffset.UtcNow;

    [JsonPropertyName("lastUpdated")]
    public DateTimeOffset? LastUpdated { get; init; }

    [JsonPropertyName("totalMissing")]
    public int TotalMissing { get; init; }

    [JsonPropertyName("items")]
    public IReadOnlyList<DemandRecord>? Items { get; init; }

    private IReadOnlyList<DemandRecord>? _records;

    [JsonPropertyName("records")]
    public IReadOnlyList<DemandRecord> Records
    {
        get => _records ?? Items ?? Array.Empty<DemandRecord>();
        init => _records = value;
    }
}
