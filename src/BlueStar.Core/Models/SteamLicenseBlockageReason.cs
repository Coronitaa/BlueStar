namespace BlueStar.Core.Models;

/// <summary>
/// Specifies the precise blockage reason when Steam account licenses cannot be reliably discovered.
/// In accordance with privacy and integrity invariants, the system NEVER falls back silently
/// to installed game manifests when license discovery is blocked.
/// </summary>
public enum SteamLicenseBlockageReason
{
    /// <summary>
    /// License discovery completed successfully.
    /// </summary>
    None = 0,

    /// <summary>
    /// No active Steam client session or browser session cookies were discovered in local CEF or Chromium caches.
    /// </summary>
    NoSteamSession = 1,

    /// <summary>
    /// Session cookies were located, but Steam returned an unauthenticated status or login redirect.
    /// </summary>
    Unauthenticated = 2,

    /// <summary>
    /// A network error or Steam service outage prevented license inventory retrieval.
    /// </summary>
    NetworkError = 3,

    /// <summary>
    /// Local Steam client installation or appcache files were inaccessible.
    /// </summary>
    LocalCacheUnavailable = 4,

    /// <summary>
    /// The authenticated account owns zero licenses.
    /// </summary>
    NoLicensesFound = 5
}
