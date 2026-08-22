using System.Threading;
using System.Threading.Tasks;

namespace BlueStar.Core.Interfaces;

/// <summary>
/// Identifies where the active DepotBox API key originated from.
/// </summary>
public enum DepotBoxAuthSource
{
    /// <summary>
    /// User provided custom personal key in Settings (overrides default backend API).
    /// </summary>
    CustomOverride,

    /// <summary>
    /// Default backend API key configured in backend or built-in system.
    /// </summary>
    DefaultBackend,

    /// <summary>
    /// Protected backend API key unlocked via active PRO license.
    /// </summary>
    ProBackend,

    /// <summary>
    /// No API key is currently active.
    /// </summary>
    None
}

/// <summary>
/// Service contract for resolving DepotBox authentication credentials.
/// Handles custom user overrides and protected backend PRO keys.
/// </summary>
public interface IDepotBoxAuthService
{
    /// <summary>
    /// Resolves the effective API key to be sent in HTTP requests.
    /// Priority:
    /// 1. User custom API key in Settings (if non-empty, overrides everything).
    /// 2. Protected backend key (if PRO license is active).
    /// 3. null if unauthenticated.
    /// </summary>
    Task<string?> GetEffectiveApiKeyAsync(CancellationToken ct = default);

    /// <summary>
    /// Gets the origin of the currently effective API key.
    /// </summary>
    Task<DepotBoxAuthSource> GetCurrentAuthSourceAsync(CancellationToken ct = default);

    /// <summary>
    /// Gets whether a custom user API key is saved in secure storage.
    /// </summary>
    Task<bool> HasCustomApiKeyAsync(CancellationToken ct = default);

    /// <summary>
    /// Gets the user's custom API key from secure storage if configured.
    /// </summary>
    Task<string?> GetCustomApiKeyAsync(CancellationToken ct = default);

    /// <summary>
    /// Saves the user's custom API key in secure storage.
    /// </summary>
    Task SetCustomApiKeyAsync(string apiKey, CancellationToken ct = default);

    /// <summary>
    /// Clears the user's custom API key from secure storage.
    /// </summary>
    Task ClearCustomApiKeyAsync(CancellationToken ct = default);
}
