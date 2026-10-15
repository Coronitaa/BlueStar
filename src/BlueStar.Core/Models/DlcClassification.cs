using System.Collections.Generic;

namespace BlueStar.Core.Models;

/// <summary>
/// Represents the audited DLC classification result indicating whether the DLC requires
/// payload depot downloading or entitlement/unlock activation only.
/// </summary>
public record DlcClassification
{
    /// <summary>
    /// Gets the Steam application identifier for the DLC.
    /// </summary>
    public uint DlcAppId { get; init; }

    /// <summary>
    /// Gets the parent/base game application identifier.
    /// </summary>
    public uint ParentAppId { get; init; }

    /// <summary>
    /// Gets the friendly name of the DLC.
    /// </summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>
    /// Gets the payload type classification.
    /// </summary>
    public DlcPayloadType PayloadType { get; init; } = DlcPayloadType.Unknown;

    /// <summary>
    /// Gets the list of depot IDs associated with this DLC.
    /// </summary>
    public IReadOnlyList<uint> DepotIds { get; init; } = [];

    /// <summary>
    /// Gets the total size in bytes for the DLC depots.
    /// </summary>
    public long TotalSizeBytes { get; init; }

    /// <summary>
    /// Gets a value indicating whether file download is required for this DLC.
    /// True for DownloadablePayload with depots; false for EntitlementOnly.
    /// </summary>
    public bool IsDownloadRequired =>
        PayloadType == DlcPayloadType.DownloadablePayload &&
        DepotIds.Count > 0;

    /// <summary>
    /// Gets an optional classification rationale or diagnostic note.
    /// </summary>
    public string? Reason { get; init; }

    /// <summary>
    /// Gets the formatted size in MB or GB for UI display.
    /// </summary>
    public string FormattedSize => TotalSizeBytes switch
    {
        <= 0 => "0 B",
        < 1024 * 1024 => $"{TotalSizeBytes / 1024.0:F1} KB",
        < 1024 * 1024 * 1024 => $"{TotalSizeBytes / (1024.0 * 1024.0):F1} MB",
        _ => $"{TotalSizeBytes / (1024.0 * 1024.0 * 1024.0):F2} GB"
    };
}
