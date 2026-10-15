namespace BlueStar.Core.Models;

/// <summary>
/// Defines the distribution and payload architecture of a downloadable content (DLC) item.
/// </summary>
public enum DlcPayloadType
{
    /// <summary>
    /// The DLC requires dedicated depot payloads containing downloadable assets and binary files
    /// (e.g., Poppy Playtime Chapters 2, 3, 4).
    /// </summary>
    DownloadablePayload,

    /// <summary>
    /// The DLC content is already embedded in the base game or activated purely via client entitlement/unlock keys
    /// (e.g., Tabletop Simulator DLCs, skin unlocks, season pass licenses).
    /// No separate depot payload download is required.
    /// </summary>
    EntitlementOnly,

    /// <summary>
    /// The DLC relies on third-party external CDN, account registration, or dedicated external servers.
    /// </summary>
    ExternalContent,

    /// <summary>
    /// The payload structure could not be determined or is unindexed.
    /// </summary>
    Unknown
}
