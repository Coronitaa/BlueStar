using System;
using System.Text.Json.Serialization;

namespace BlueStar.Core.Models;

/// <summary>
/// Immutable model representing a contributor's Stardust point balance fetched from the authorized Hub server ledger.
/// Strictly distinguishes cryptographically confirmed ledger points from unconfirmed pending points.
/// </summary>
public sealed record StardustBalanceInfo
{
    /// <summary>
    /// Anonymous cryptographic contributor UUID v4.
    /// </summary>
    [JsonPropertyName("contributorId")]
    public string ContributorId { get; init; } = string.Empty;

    /// <summary>
    /// Confirmed points verified and chained in the Stardust ledger.
    /// </summary>
    [JsonPropertyName("confirmedPoints")]
    public long ConfirmedPoints { get; init; }

    /// <summary>
    /// Pending points currently queued or undergoing verification.
    /// </summary>
    [JsonPropertyName("pendingPoints")]
    public long PendingPoints { get; init; }

    /// <summary>
    /// Total aggregate points (confirmed + pending).
    /// </summary>
    [JsonIgnore]
    public long TotalPoints => ConfirmedPoints + PendingPoints;

    /// <summary>
    /// Timestamp when this balance was queried from the server ledger.
    /// </summary>
    [JsonPropertyName("lastUpdated")]
    public DateTimeOffset LastUpdated { get; init; } = DateTimeOffset.UtcNow;
}
