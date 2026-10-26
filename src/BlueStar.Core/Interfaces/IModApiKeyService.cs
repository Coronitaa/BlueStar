using System.Threading;
using System.Threading.Tasks;

namespace BlueStar.Core.Interfaces;

/// <summary>
/// Third-party mod sites with keyed APIs. Thunderstore's read API is public; its service-account
/// token only unlocks account features. GameBanana, Modrinth and the Steam Workshop need no key.
/// </summary>
public enum ModApiProvider
{
    CurseForge,
    NexusMods,
    ModIo,
    Thunderstore
}

/// <summary>
/// Identifies where the active key for a mod provider originated from.
/// </summary>
public enum ModApiKeySource
{
    /// <summary>No key is configured; the provider stays hidden.</summary>
    None,

    /// <summary>Key shipped with the release (bundled settings.json, user settings or environment variable).</summary>
    BuiltIn,

    /// <summary>User-provided key from Settings, stored in secure storage.</summary>
    CustomOverride
}

/// <summary>
/// Outcome of checking a key against the provider's API.
/// </summary>
public enum ModApiKeyCheckStatus
{
    Valid,
    Invalid,
    RateLimited,
    MissingKey,
    NetworkError
}

/// <summary>
/// Result of validating a mod provider key.
/// </summary>
public sealed record ModApiKeyCheckResult(ModApiKeyCheckStatus Status, int? HttpStatusCode = null, string? Detail = null)
{
    public bool IsValid => Status == ModApiKeyCheckStatus.Valid;
}

/// <summary>
/// Resolves API keys for third-party mod providers. A personal key saved in Settings always takes
/// precedence over the built-in key, so users can swap in their own key when the shared one is saturated.
/// </summary>
public interface IModApiKeyService
{
    /// <summary>Gets the key to use for requests: the personal override if present, otherwise the built-in key.</summary>
    Task<string?> GetEffectiveKeyAsync(ModApiProvider provider, CancellationToken ct = default);

    /// <summary>Gets where the effective key comes from.</summary>
    Task<ModApiKeySource> GetKeySourceAsync(ModApiProvider provider, CancellationToken ct = default);

    /// <summary>Gets the personal override key, or null when none is saved.</summary>
    Task<string?> GetCustomKeyAsync(ModApiProvider provider, CancellationToken ct = default);

    /// <summary>Saves a personal override key. A blank key clears the override.</summary>
    Task SetCustomKeyAsync(ModApiProvider provider, string? apiKey, CancellationToken ct = default);

    /// <summary>Removes the personal override key, reverting to the built-in key.</summary>
    Task ClearCustomKeyAsync(ModApiProvider provider, CancellationToken ct = default);

    /// <summary>Checks a key against the provider's API. When <paramref name="apiKey"/> is null the effective key is checked.</summary>
    Task<ModApiKeyCheckResult> ValidateKeyAsync(ModApiProvider provider, string? apiKey = null, CancellationToken ct = default);

    /// <summary>
    /// API base URL for the provider, without a trailing slash. mod.io issues a per-account path
    /// (https://u-&lt;id&gt;.modapi.io/v1); the personal override wins over the built-in one.
    /// </summary>
    Task<string> GetApiBaseUrlAsync(ModApiProvider provider, CancellationToken ct = default);

    /// <summary>Gets the personal API base URL override, or null when none is saved.</summary>
    Task<string?> GetCustomApiBaseUrlAsync(ModApiProvider provider, CancellationToken ct = default);

    /// <summary>
    /// Saves a personal API base URL. A blank URL clears it. Only https URLs on the provider's own
    /// domains are accepted, so a key is never sent to another host.
    /// </summary>
    /// <exception cref="System.ArgumentException">The URL is not an https URL on the provider's domains.</exception>
    Task SetCustomApiBaseUrlAsync(ModApiProvider provider, string? baseUrl, CancellationToken ct = default);
}
