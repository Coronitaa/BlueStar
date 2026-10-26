using System;
using System.Threading;
using System.Threading.Tasks;

namespace BlueStar.Core.Interfaces;

/// <summary>
/// Result of turning a Workshop item into an installable package.
/// </summary>
/// <param name="RequiresOwnedAccount">Steam only serves this item to a signed-in account that owns the game.</param>
public sealed record WorkshopFetchResult(
    ModPackage? Package, WorkshopItemInfo? Details = null, string? Error = null, bool RequiresOwnedAccount = false)
{
    public bool Succeeded => Package != null;
}

/// <summary>
/// Downloads Steam Workshop items into BlueStar's Workshop cache and hands them to the install pipeline
/// as <see cref="ModPackage"/>s. Cached items are reused while Steam reports no newer version.
/// </summary>
public interface IWorkshopModFetcher
{
    /// <summary>
    /// Fetches the item for the game <paramref name="appId"/>. Fails if the item belongs to another game.
    /// </summary>
    Task<WorkshopFetchResult> FetchAsync(uint appId, ulong publishedFileId, IProgress<double>? progress = null, CancellationToken ct = default);

    /// <summary>Page of a Workshop item on the Steam Community site.</summary>
    static string ItemPageUrl(ulong publishedFileId) =>
        $"https://steamcommunity.com/sharedfiles/filedetails/?id={publishedFileId}";
}
