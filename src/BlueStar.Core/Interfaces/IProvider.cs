using BlueStar.Core.Models;

namespace BlueStar.Core.Interfaces;

/// <summary>
/// Base interface for all external, community, or local providers in BlueStar.
/// Providers are strategies for resolving data, never part of an instance's persisted identity.
/// </summary>
public interface IProvider
{
    /// <summary>
    /// Gets the unique identifier for this provider (e.g. "local_cache", "depotbox", "manifesthub", "steam").
    /// </summary>
    string ProviderId { get; }

    /// <summary>
    /// Gets the friendly display name of the provider.
    /// </summary>
    string DisplayName { get; }

    /// <summary>
    /// Gets the execution priority (higher numbers execute before lower numbers).
    /// </summary>
    int Priority { get; }

    /// <summary>
    /// Gets the set of capabilities supported by this provider.
    /// </summary>
    ProviderCapabilities Capabilities { get; }
}
