namespace BlueStar.Core.Models;

/// <summary>
/// Configuration options for Steam account license discovery.
/// </summary>
public sealed record SteamLicenseDiscoveryOptions
{
    /// <summary>
    /// Gets a value indicating whether to inspect local Steam client cache (appcache/packageinfo.vdf, userdata).
    /// </summary>
    public bool IncludeLocalClientCache { get; init; } = true;

    /// <summary>
    /// Gets a value indicating whether to inspect the active Steam Web session via secure in-memory DPAPI cookies.
    /// </summary>
    public bool IncludeWebSession { get; init; } = true;

    /// <summary>
    /// Gets a value indicating whether to enrich discovered AppIDs with titles and metadata from Steam Store API.
    /// </summary>
    public bool EnrichMetadata { get; init; } = true;

    /// <summary>
    /// Gets a value indicating whether to apply IDlcPayloadClassifier to determine contribution eligibility.
    /// </summary>
    public bool ClassifyDlcs { get; init; } = true;

    /// <summary>
    /// Gets a value indicating whether to resolve package/sub IDs to AppIDs.
    /// </summary>
    public bool ResolvePackages { get; init; } = true;
}
