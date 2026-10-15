using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace BlueStar.Core.Models;

/// <summary>
/// Record representing an immutable snapshot of an application build at a point in time in BlueStar Manifest Hub.
/// </summary>
[JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
public sealed class BuildSnapshot
{
    [JsonPropertyName("schemaVersion")]
    public string SchemaVersion { get; init; } = "v1";

    [JsonPropertyName("appId")]
    public uint AppId { get; init; }

    [JsonPropertyName("buildId")]
    public ulong BuildId { get; init; }

    [JsonPropertyName("timestamp")]
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Authentic manifest/build creation timestamp when known; strictly null if absent.
    /// UtcNow MUST NOT be used as a proxy for creation time.
    /// </summary>
    [JsonPropertyName("creationTime")]
    public DateTimeOffset? CreationTime { get; init; }

    [JsonPropertyName("branch")]
    public string Branch { get; init; } = "public";

    [JsonPropertyName("description")]
    public string Description { get; init; } = string.Empty;

    /// <summary>
    /// Mapping of DepotId to ManifestId for this build snapshot.
    /// </summary>
    [JsonPropertyName("depotManifests")]
    public IReadOnlyDictionary<uint, ulong> DepotManifests { get; init; } = new Dictionary<uint, ulong>();
}
