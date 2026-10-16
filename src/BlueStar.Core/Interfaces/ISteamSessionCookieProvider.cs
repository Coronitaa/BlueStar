using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace BlueStar.Core.Interfaces;

/// <summary>
/// Provides secure, in-memory Steam session cookies (e.g. steamLoginSecure and sessionid)
/// retrieved from the local Steam client CEF cache or browser profiles using Windows DPAPI.
/// Invariant: Cookie values must NEVER be logged, serialized, or transmitted to any third party.
/// </summary>
public interface ISteamSessionCookieProvider
{
    /// <summary>
    /// Attempts to extract active Steam session cookies in volatile process memory.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A dictionary of cookie names to decrypted values, or null if no active session is found.</returns>
    Task<IReadOnlyDictionary<string, string>?> GetSteamSessionCookiesAsync(CancellationToken ct = default);
}
