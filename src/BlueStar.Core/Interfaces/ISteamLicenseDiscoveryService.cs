using System.Threading;
using System.Threading.Tasks;
using BlueStar.Core.Models;

namespace BlueStar.Core.Interfaces;

/// <summary>
/// Discovers owned Steam AppIDs and packages from the user's authentic Steam account via secure routes.
/// 
/// ZERO-KNOWLEDGE & PRIVACY INVARIANTS:
/// 1. NEVER transmits SteamID, credentials, session cookies, tokens, usernames, or the full license list
///    to the community Manifest Hub, telemetry servers, or any external host.
/// 2. Session cookies are decrypted in-memory using DPAPI and zero plain text secrets are ever stored on disk or logged.
/// 3. If Steam licenses cannot be obtained (e.g. unauthenticated or no session), reports the real blockage cause
///    and NEVER silently falls back to local installed game manifests.
/// </summary>
public interface ISteamLicenseDiscoveryService
{
    /// <summary>
    /// Discovers all owned Steam application licenses and classifies DLC items.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The discovery result containing licensed AppIDs or an explicit blockage description.</returns>
    Task<SteamLicenseDiscoveryResult> DiscoverLicensesAsync(CancellationToken ct = default);

    /// <summary>
    /// Discovers owned Steam application licenses using custom configuration options.
    /// </summary>
    /// <param name="options">Discovery options controlling cache inspection, metadata enrichment, and DLC classification.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The discovery result containing licensed AppIDs or an explicit blockage description.</returns>
    Task<SteamLicenseDiscoveryResult> DiscoverLicensesAsync(SteamLicenseDiscoveryOptions options, CancellationToken ct = default);
}
