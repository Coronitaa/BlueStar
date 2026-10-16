using System;
using System.Collections.Generic;

namespace BlueStar.Core.Models;

/// <summary>
/// Represents an authenticated Steam license owned by the user's account,
/// enriched with DLC payload classification and community contribution eligibility.
/// </summary>
public sealed record SteamLicenseItem
{
    /// <summary>
    /// Gets the Steam application identifier.
    /// </summary>
    public uint AppId { get; init; }

    /// <summary>
    /// Gets the friendly title of the game or DLC item.
    /// </summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>
    /// Gets a value indicating whether this licensed item is a DLC.
    /// </summary>
    public bool IsDlc { get; init; }

    /// <summary>
    /// Gets the parent/base game AppID if this item is a DLC.
    /// </summary>
    public uint? ParentAppId { get; init; }

    /// <summary>
    /// Gets the DLC classification result if this item is a DLC.
    /// </summary>
    public DlcClassification? Classification { get; init; }

    /// <summary>
    /// Gets a value indicating whether this item is eligible for community manifest contributions.
    /// Invariant: Base games and DownloadablePayload DLCs are eligible; EntitlementOnly DLCs are excluded.
    /// </summary>
    public bool IsEligibleForContribution { get; init; } = true;

    /// <summary>
    /// Gets the package/sub IDs through which this AppID was licensed.
    /// </summary>
    public IReadOnlyList<uint> PackageIds { get; init; } = [];

    /// <summary>
    /// Gets the origin route through which the license was discovered (e.g. "SteamWebSession", "LocalClientCache").
    /// </summary>
    public string SourceRoute { get; init; } = "SteamWebSession";
}
