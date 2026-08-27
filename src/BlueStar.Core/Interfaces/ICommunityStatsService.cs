using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BlueStar.Core.Models;

namespace BlueStar.Core.Interfaces;

/// <summary>
/// Service responsible for gathering community statistics from Cloudflare Worker,
/// caching Steam lists, and consuming DepotBox webhook feeds.
/// </summary>
public interface ICommunityStatsService
{
    /// <summary>
    /// Gets trending games added as instances across the BlueStar community in the last 7 days.
    /// </summary>
    Task<IReadOnlyList<SearchResult>> GetTrendingBlueStarAsync(CancellationToken ct = default);

    /// <summary>
    /// Gets the most added game instances across the BlueStar community of all time.
    /// </summary>
    Task<IReadOnlyList<SearchResult>> GetMostPlayedBlueStarAsync(CancellationToken ct = default);

    /// <summary>
    /// Gets one of the 4 Steam ranking lists (e.g. most_played, trending, top_sellers, top_rated).
    /// </summary>
    Task<IReadOnlyList<SearchResult>> GetSteamDbListAsync(string listType, CancellationToken ct = default);

    /// <summary>
    /// Gets the latest DepotBox games feed from webhook ingestion ("added" or "updated").
    /// </summary>
    Task<IReadOnlyList<SearchResult>> GetDepotBoxFeedAsync(string feedType, CancellationToken ct = default);

    /// <summary>
    /// Reports an instance creation event to the Cloudflare analytics worker.
    /// </summary>
    Task ReportInstanceAddedAsync(uint appId, string name, CancellationToken ct = default);

    /// <summary>
    /// Reports an anonymous gameplay session to the Cloudflare analytics worker.
    /// </summary>
    Task ReportGamePlayAsync(int appId, string name, TimeSpan duration, CancellationToken ct = default);
}
