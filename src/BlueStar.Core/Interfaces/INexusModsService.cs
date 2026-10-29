using System;
using System.Threading;
using System.Threading.Tasks;
using BlueStar.Core.Models;
using BlueStar.Core.Interfaces;

namespace BlueStar.Core.Interfaces;

/// <summary>
/// Service for Nexus Mods interactions, resolving games, fetching metadata, and downloading
/// mods using the nxm:// flow (valid for non-premium accounts when the user triggers the download on the web).
/// </summary>
public interface INexusModsService
{
    /// <summary>
    /// Checks if the given game instance is supported on Nexus Mods, returning its game domain slug (e.g. "skyrimspecialedition") or null.
    /// </summary>
    Task<string?> ResolveGameDomainAsync(GameInstance instance, CancellationToken ct = default);

    /// <summary>
    /// Gets metadata for a specific mod on Nexus Mods.
    /// </summary>
    Task<NexusModInfo?> GetModDetailsAsync(string gameDomain, long modId, CancellationToken ct = default);

    /// <summary>
    /// Gets metadata for a specific file of a mod on Nexus Mods.
    /// </summary>
    Task<NexusFileInfo?> GetFileDetailsAsync(string gameDomain, long modId, long fileId, CancellationToken ct = default);

    /// <summary>
    /// Obtains direct CDN download URI using key and expires parameters extracted from the nxm:// link.
    /// Works with free / non-premium accounts.
    /// </summary>
    Task<string?> GetDownloadUrlFromNxmAsync(string nxmUrl, CancellationToken ct = default);

    /// <summary>
    /// Downloads a mod via its nxm:// link into local cache and returns a <see cref="ModPackage"/> ready for installation.
    /// </summary>
    Task<ModPackage?> DownloadFromNxmAsync(
        GameInstance instance,
        string nxmUrl,
        IProgress<double>? progress = null,
        CancellationToken ct = default);

    /// <summary>
    /// Checks if the input string is a valid nxm:// protocol URL.
    /// </summary>
    bool CanHandleNxmUrl(string? url);

    /// <summary>
    /// Parses an nxm:// URL into its components.
    /// </summary>
    NexusNxmLink? ParseNxmUrl(string? nxmUrl);

    /// <summary>
    /// Registers the nxm:// protocol handler in Windows Registry for the current user.
    /// </summary>
    void RegisterProtocolHandler();
}
