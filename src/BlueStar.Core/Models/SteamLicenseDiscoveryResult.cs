using System;
using System.Collections.Generic;
using System.Linq;

namespace BlueStar.Core.Models;

/// <summary>
/// Encapsulates the complete result of Steam account license discovery.
/// When discovery fails or credentials cannot be verified, explicitly reports the real blockage reason
/// without silently falling back to installed game instances.
/// </summary>
public sealed record SteamLicenseDiscoveryResult
{
    /// <summary>
    /// Gets a value indicating whether license discovery succeeded.
    /// </summary>
    public bool IsSuccess { get; init; }

    /// <summary>
    /// Gets the blockage reason if discovery could not be completed.
    /// </summary>
    public SteamLicenseBlockageReason BlockageReason { get; init; } = SteamLicenseBlockageReason.None;

    /// <summary>
    /// Gets a human-readable diagnostic explanation of the blockage, if any.
    /// Invariant: Must never include plain-text session tokens, passwords, or SteamIDs.
    /// </summary>
    public string? BlockageDetails { get; init; }

    /// <summary>
    /// Gets the discovered licensed application items.
    /// </summary>
    public IReadOnlyList<SteamLicenseItem> LicensedApps { get; init; } = [];

    /// <summary>
    /// Gets the discovered package/sub IDs owned by the account.
    /// </summary>
    public IReadOnlyList<uint> DiscoveredPackageIds { get; init; } = [];

    /// <summary>
    /// Gets the total count of licensed applications discovered.
    /// </summary>
    public int TotalLicensedAppsCount => LicensedApps.Count;

    /// <summary>
    /// Gets the count of DLC items that require dedicated payload downloads and are eligible for contribution.
    /// </summary>
    public int DownloadableDlcCount => LicensedApps.Count(a => a.IsDlc && a.IsEligibleForContribution);

    /// <summary>
    /// Gets the count of DLC items that are entitlement-only and excluded from contribution generation.
    /// </summary>
    public int EntitlementOnlyDlcCount => LicensedApps.Count(a => a.IsDlc && !a.IsEligibleForContribution);

    /// <summary>
    /// Creates a successful license discovery result.
    /// </summary>
    public static SteamLicenseDiscoveryResult Success(
        IReadOnlyList<SteamLicenseItem> apps,
        IReadOnlyList<uint>? packageIds = null)
    {
        return new SteamLicenseDiscoveryResult
        {
            IsSuccess = true,
            BlockageReason = SteamLicenseBlockageReason.None,
            LicensedApps = apps ?? [],
            DiscoveredPackageIds = packageIds ?? []
        };
    }

    /// <summary>
    /// Creates a blocked license discovery result reporting the exact blockage cause.
    /// </summary>
    public static SteamLicenseDiscoveryResult Blocked(
        SteamLicenseBlockageReason reason,
        string details)
    {
        return new SteamLicenseDiscoveryResult
        {
            IsSuccess = false,
            BlockageReason = reason,
            BlockageDetails = details,
            LicensedApps = [],
            DiscoveredPackageIds = []
        };
    }
}
