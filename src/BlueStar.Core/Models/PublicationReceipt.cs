using System;

namespace BlueStar.Core.Models;

/// <summary>
/// Server publication confirmation and receipt returned upon successful community contribution ingestion.
/// </summary>
public record PublicationReceipt
{
    /// <summary>
    /// Unique server-generated receipt identifier (e.g. UUID or confirmation token).
    /// </summary>
    public string ReceiptId { get; init; } = string.Empty;

    /// <summary>
    /// UTC timestamp when the contribution was accepted and published by the hub.
    /// </summary>
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Publication status (e.g. "Accepted", "Published", "Duplicate", "Rejected").
    /// </summary>
    public string Status { get; init; } = "Published";

    /// <summary>
    /// Number of Stardust units credited for this successful contribution.
    /// </summary>
    public int StardustCredited { get; init; }

    /// <summary>
    /// Steam Application ID.
    /// </summary>
    public uint AppId { get; init; }

    /// <summary>
    /// Steam Depot ID.
    /// </summary>
    public uint DepotId { get; init; }

    /// <summary>
    /// Manifest revision identifier (GID).
    /// </summary>
    public ulong ManifestId { get; init; }

    /// <summary>
    /// Whether the publication was confirmed and succeeded.
    /// </summary>
    public bool IsSuccess => string.Equals(Status, "Published", StringComparison.OrdinalIgnoreCase) ||
                             string.Equals(Status, "Accepted", StringComparison.OrdinalIgnoreCase) ||
                             string.Equals(Status, "Success", StringComparison.OrdinalIgnoreCase);
}
