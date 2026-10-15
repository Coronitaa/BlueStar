using System;
using System.Text.Json.Serialization;

namespace BlueStar.Core.Models;

/// <summary>
/// Model representing a depot manifest revision available from the community Manifest Hub.
/// Enforces transparent date provenance: authentic publication date (when known) is strictly
/// distinguished from ingestion date, with an explicit unknown badge when authentic creation time is absent.
/// Chronology MUST NOT be inferred from numerical ManifestGID values.
/// </summary>
public sealed record HubRevisionItem
{
    [JsonPropertyName("appId")]
    public uint AppId { get; init; }

    [JsonPropertyName("depotId")]
    public uint DepotId { get; init; }

    [JsonPropertyName("manifestId")]
    public ulong ManifestId { get; init; }

    [JsonIgnore]
    public string ManifestIdString => ManifestId > 0 ? ManifestId.ToString() : string.Empty;

    [JsonPropertyName("buildId")]
    public string? BuildId { get; init; }

    [JsonPropertyName("branch")]
    public string? BranchName { get; init; } = "public";

    [JsonPropertyName("depotName")]
    public string DepotName { get; init; } = string.Empty;

    /// <summary>
    /// Authentic publication date assigned by the original source or Steam CDN.
    /// Strictly null if unknown; NEVER set to UtcNow or estimated.
    /// </summary>
    [JsonPropertyName("creationTime")]
    public DateTimeOffset? AuthenticPublicationDate { get; init; }

    [JsonIgnore]
    public bool HasAuthenticDate => AuthenticPublicationDate.HasValue &&
                                    AuthenticPublicationDate.Value > DateTimeOffset.UnixEpoch &&
                                    AuthenticPublicationDate.Value > DateTimeOffset.MinValue;

    [JsonIgnore]
    public string AuthenticDateDisplay => HasAuthenticDate
        ? AuthenticPublicationDate!.Value.LocalDateTime.ToString("yyyy-MM-dd HH:mm")
        : string.Empty;

    /// <summary>
    /// Date when the manifest was ingested into the Manifest Hub archive.
    /// </summary>
    [JsonPropertyName("ingestedAt")]
    public DateTimeOffset? IngestionDate { get; init; }

    [JsonIgnore]
    public string IngestionDateDisplay => IngestionDate.HasValue
        ? IngestionDate.Value.LocalDateTime.ToString("yyyy-MM-dd HH:mm")
        : string.Empty;

    /// <summary>
    /// Source provenance badge (e.g., "K25", "ManifestHub2", "Community", "ManifestHub").
    /// </summary>
    [JsonPropertyName("source")]
    public string Provenance { get; init; } = "Community";

    [JsonPropertyName("sizeOriginal")]
    public long SizeOriginal { get; init; }

    [JsonIgnore]
    public string SizeFormatted => FormatBytes(SizeOriginal);

    [JsonPropertyName("sha256")]
    public string? Sha256 { get; init; }

    [JsonPropertyName("status")]
    public string Status { get; init; } = "Available";

    [JsonIgnore]
    public bool IsSelected { get; init; }

    private static string FormatBytes(long bytes)
    {
        if (bytes <= 0) return "Unknown size";
        string[] suffixes = ["B", "KB", "MB", "GB", "TB"];
        int suffixIndex = 0;
        double size = bytes;
        while (size >= 1024 && suffixIndex < suffixes.Length - 1)
        {
            size /= 1024;
            suffixIndex++;
        }
        return $"{size:0.##} {suffixes[suffixIndex]}";
    }
}
