using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using BlueStar.Core.Interfaces;
using BlueStar.Core.Models;
using BlueStar.Infrastructure.Storage;
using Microsoft.Extensions.Logging;

namespace BlueStar.Infrastructure.Services;

/// <summary>
/// Production implementation of ICommunityStatsService.
/// Communicates with BlueStar Cloudflare Workers for real instance analytics and webhooks,
/// and Steam Store API for Steam ranking lists.
/// </summary>
public class CommunityStatsService : ICommunityStatsService
{
    private readonly HttpClient _httpClient;
    private readonly ICacheService _cacheService;
    private readonly AppSettingsService _settingsService;
    private readonly ILogger<CommunityStatsService> _logger;

    private const string DefaultCloudflareWorkerUrl = "https://bluestar-api-worker.blustar.workers.dev";

    public CommunityStatsService(
        HttpClient httpClient,
        ICacheService cacheService,
        AppSettingsService settingsService,
        ILogger<CommunityStatsService> logger)
    {
        _httpClient = httpClient;
        _cacheService = cacheService;
        _settingsService = settingsService;
        _logger = logger;
    }

    private string GetWorkerBaseUrl()
    {
        return DefaultCloudflareWorkerUrl.TrimEnd('/');
    }

    /// <summary>
    /// How long one call to the community worker may take.
    /// </summary>
    /// <remarks>
    /// The old three seconds was spent before the first byte every time the app opened: a DNS
    /// lookup and a cold TLS handshake to a host nothing had touched yet, while twenty other
    /// requests raced for the same startup. The worker answers a warm connection in about a
    /// third of a second, so a budget that small only ever fired on the handshake — and the
    /// stack it logged made a routine timeout read like a crash.
    /// </remarks>
    private static readonly TimeSpan WorkerTimeout = TimeSpan.FromSeconds(15);

    /// <summary>Pause before the one retry, long enough for a stalled handshake to give up.</summary>
    private static readonly TimeSpan WorkerRetryDelay = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Calls the community worker, once more after a pause if the first attempt ran out of time.
    /// </summary>
    /// <remarks>
    /// Every one of these calls has something to fall back on — a cached list, a built-in list,
    /// or simply not reporting — so a failure is a quiet line in the log, not a stack trace.
    /// The caller's own token still cancels immediately: a closing app does not sit through a
    /// retry.
    /// </remarks>
    /// <returns>What the call returned, or <c>null</c> if the worker could not be reached.</returns>
    private async Task<T?> CallWorkerAsync<T>(
        string what, Func<CancellationToken, Task<T?>> call, CancellationToken ct)
        where T : class
    {
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(WorkerTimeout);

            try
            {
                return await call(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // The app is closing, or the caller lost interest. Nothing to report.
                return null;
            }
            catch (OperationCanceledException)
            {
                if (attempt == 2)
                {
                    _logger.LogDebug(
                        "{What}: the community worker did not answer within {Seconds:0}s, twice. Carrying on without it.",
                        what, WorkerTimeout.TotalSeconds);
                    return null;
                }

                try
                {
                    await Task.Delay(WorkerRetryDelay, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return null;
                }
            }
            catch (HttpRequestException ex)
            {
                _logger.LogDebug("{What}: the community worker is unreachable ({Message}).", what, ex.Message);
                return null;
            }
            catch (JsonException ex)
            {
                _logger.LogDebug("{What}: the community worker answered with something unreadable ({Message}).", what, ex.Message);
                return null;
            }
        }

        return null;
    }

    private static readonly System.Text.RegularExpressions.Regex SteamSearchItemRegex = new(
        @"(?s)<a [^>]*data-ds-appid=""(?<appid>\d+)""[^>]*>.*?<span class=""title"">(?<title>[^<]+)</span>.*?</a>",
        System.Text.RegularExpressions.RegexOptions.Compiled);

    private static readonly System.Text.RegularExpressions.Regex SteamImageRegex = new(
        @"<img [^>]*src=""(?<imgurl>https://shared\.[^""]+)""",
        System.Text.RegularExpressions.RegexOptions.Compiled);

    private static readonly System.Text.RegularExpressions.Regex SteamReleaseDateRegex = new(
        @"<div class=""col search_released responsive_secondrow"">(?<release>[^<]+)</div>",
        System.Text.RegularExpressions.RegexOptions.Compiled);

    private static readonly List<SearchResult> DefaultCommunityTrendingFallback = new()
    {
        new SearchResult { AppId = 286160, Name = "Tabletop Simulator", AppType = "Game", HasWindows = true, HasLinux = true, HasMac = true, HeaderImageUrl = "https://shared.cloudflare.steamstatic.com/store_item_assets/steam/apps/286160/header.jpg" },
        new SearchResult { AppId = 322330, Name = "Don't Starve Together", AppType = "Game", HasWindows = true, HasLinux = true, HasMac = true, HeaderImageUrl = "https://shared.cloudflare.steamstatic.com/store_item_assets/steam/apps/322330/header.jpg" },
        new SearchResult { AppId = 1966720, Name = "Lethal Company", AppType = "Game", HasWindows = true, HeaderImageUrl = "https://shared.cloudflare.steamstatic.com/store_item_assets/steam/apps/1966720/header.jpg" },
        new SearchResult { AppId = 1623730, Name = "Palworld", AppType = "Game", HasWindows = true, HeaderImageUrl = "https://shared.cloudflare.steamstatic.com/store_item_assets/steam/apps/1623730/header.jpg" },
        new SearchResult { AppId = 2379780, Name = "Balatro", AppType = "Game", HasWindows = true, HasMac = true, HeaderImageUrl = "https://shared.cloudflare.steamstatic.com/store_item_assets/steam/apps/2379780/header.jpg" },
        new SearchResult { AppId = 1145360, Name = "Hades II", AppType = "Game", HasWindows = true, HeaderImageUrl = "https://shared.cloudflare.steamstatic.com/store_item_assets/steam/apps/1145360/header.jpg" },
        new SearchResult { AppId = 739630, Name = "Phasmophobia", AppType = "Game", HasWindows = true, HeaderImageUrl = "https://shared.cloudflare.steamstatic.com/store_item_assets/steam/apps/739630/header.jpg" },
        new SearchResult { AppId = 553850, Name = "HELLDIVERS™ 2", AppType = "Game", HasWindows = true, HeaderImageUrl = "https://shared.cloudflare.steamstatic.com/store_item_assets/steam/apps/553850/header.jpg" }
    };

    private static readonly List<SearchResult> DefaultCommunityMostPlayedFallback = new()
    {
        new SearchResult { AppId = 1091500, Name = "Cyberpunk 2077", AppType = "Game", HasWindows = true, HeaderImageUrl = "https://shared.cloudflare.steamstatic.com/store_item_assets/steam/apps/1091500/header.jpg" },
        new SearchResult { AppId = 1245620, Name = "ELDEN RING", AppType = "Game", HasWindows = true, HeaderImageUrl = "https://shared.cloudflare.steamstatic.com/store_item_assets/steam/apps/1245620/header.jpg" },
        new SearchResult { AppId = 374320, Name = "DARK SOULS™ III", AppType = "Game", HasWindows = true, HeaderImageUrl = "https://shared.cloudflare.steamstatic.com/store_item_assets/steam/apps/374320/header.jpg" },
        new SearchResult { AppId = 292030, Name = "The Witcher 3: Wild Hunt", AppType = "Game", HasWindows = true, HeaderImageUrl = "https://shared.cloudflare.steamstatic.com/store_item_assets/steam/apps/292030/header.jpg" },
        new SearchResult { AppId = 1086940, Name = "Baldur's Gate 3", AppType = "Game", HasWindows = true, HasMac = true, HeaderImageUrl = "https://shared.cloudflare.steamstatic.com/store_item_assets/steam/apps/1086940/header.jpg" },
        new SearchResult { AppId = 582010, Name = "Monster Hunter: World", AppType = "Game", HasWindows = true, HeaderImageUrl = "https://shared.cloudflare.steamstatic.com/store_item_assets/steam/apps/582010/header.jpg" },
        new SearchResult { AppId = 1174180, Name = "Red Dead Redemption 2", AppType = "Game", HasWindows = true, HeaderImageUrl = "https://shared.cloudflare.steamstatic.com/store_item_assets/steam/apps/1174180/header.jpg" },
        new SearchResult { AppId = 413150, Name = "Stardew Valley", AppType = "Game", HasWindows = true, HasLinux = true, HasMac = true, HeaderImageUrl = "https://shared.cloudflare.steamstatic.com/store_item_assets/steam/apps/413150/header.jpg" }
    };

    private IReadOnlyList<SearchResult>? _sessionTrending;
    private IReadOnlyList<SearchResult>? _sessionMostPlayed;
    private readonly SemaphoreSlim _sessionFeedsLock = new(1, 1);

    /// <inheritdoc />
    public async Task PreloadBlueStarFeedsAsync(CancellationToken ct = default)
    {
        if (_sessionTrending != null && _sessionMostPlayed != null) return;

        await _sessionFeedsLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var trendingTask = _sessionTrending != null ? Task.FromResult(_sessionTrending) : FetchTrendingInternalAsync(ct);
            var mostPlayedTask = _sessionMostPlayed != null ? Task.FromResult(_sessionMostPlayed) : FetchMostPlayedInternalAsync(ct);

            await Task.WhenAll(trendingTask, mostPlayedTask).ConfigureAwait(false);

            _sessionTrending = await trendingTask.ConfigureAwait(false);
            _sessionMostPlayed = await mostPlayedTask.ConfigureAwait(false);
        }
        finally
        {
            _sessionFeedsLock.Release();
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SearchResult>> GetTrendingBlueStarAsync(CancellationToken ct = default)
    {
        if (_sessionTrending != null) return _sessionTrending;

        await _sessionFeedsLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_sessionTrending != null) return _sessionTrending;
            _sessionTrending = await FetchTrendingInternalAsync(ct).ConfigureAwait(false);
            return _sessionTrending;
        }
        finally
        {
            _sessionFeedsLock.Release();
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SearchResult>> GetMostPlayedBlueStarAsync(CancellationToken ct = default)
    {
        if (_sessionMostPlayed != null) return _sessionMostPlayed;

        await _sessionFeedsLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_sessionMostPlayed != null) return _sessionMostPlayed;
            _sessionMostPlayed = await FetchMostPlayedInternalAsync(ct).ConfigureAwait(false);
            return _sessionMostPlayed;
        }
        finally
        {
            _sessionFeedsLock.Release();
        }
    }

    private async Task<IReadOnlyList<SearchResult>> FetchTrendingInternalAsync(CancellationToken ct)
    {
        const string cacheKey = "bluestar_trending_7d_v7";
        try
        {
            var cached = await _cacheService.GetAsync<List<SearchResult>>(cacheKey, ct).ConfigureAwait(false);
            if (cached != null && cached.Count > 0)
            {
                foreach (var item in cached)
                {
                    if (item.Version != null && (item.Version.Contains("community instances", StringComparison.OrdinalIgnoreCase) || item.Version.Contains("added this week", StringComparison.OrdinalIgnoreCase)))
                        item.Version = null;
                }
                return Deduplicate(cached);
            }
        }
        catch { }

        var workerUrl = $"{GetWorkerBaseUrl()}/api/stats/trending";

        var response = await CallWorkerAsync(
            "Community trending",
            token => _httpClient.GetFromJsonAsync<StatsApiResponse>(workerUrl, token),
            ct).ConfigureAwait(false);

        if (response?.Results is { Count: > 0 })
        {
            var list = Deduplicate(response.Results.Select(r => r.ToSearchResult()));

            try
            {
                await _cacheService.SetAsync(cacheKey, list, TimeSpan.FromMinutes(5), ct).ConfigureAwait(false);
            }
            catch
            {
                // A cache write that fails is not worth losing the list over.
            }

            return list;
        }

        return DefaultCommunityTrendingFallback;
    }

    private async Task<IReadOnlyList<SearchResult>> FetchMostPlayedInternalAsync(CancellationToken ct)
    {
        const string cacheKey = "bluestar_most_played_alltime_v7";
        try
        {
            var cached = await _cacheService.GetAsync<List<SearchResult>>(cacheKey, ct).ConfigureAwait(false);
            if (cached != null && cached.Count > 0)
            {
                foreach (var item in cached)
                {
                    if (item.Version != null && (item.Version.Contains("community instances", StringComparison.OrdinalIgnoreCase) || item.Version.Contains("added this week", StringComparison.OrdinalIgnoreCase)))
                        item.Version = null;
                }
                return Deduplicate(cached);
            }
        }
        catch { }

        var workerUrl = $"{GetWorkerBaseUrl()}/api/stats/most-played";

        var response = await CallWorkerAsync(
            "Community most added",
            token => _httpClient.GetFromJsonAsync<StatsApiResponse>(workerUrl, token),
            ct).ConfigureAwait(false);

        if (response?.Results is { Count: > 0 })
        {
            var list = Deduplicate(response.Results.Select(r => r.ToSearchResult()));

            try
            {
                await _cacheService.SetAsync(cacheKey, list, TimeSpan.FromMinutes(5), ct).ConfigureAwait(false);
            }
            catch
            {
                // A cache write that fails is not worth losing the list over.
            }

            return list;
        }

        return DefaultCommunityMostPlayedFallback;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<SearchResult>> GetSteamDbListAsync(string listType, CancellationToken ct = default)
        => GetSteamDbListAsync(listType, 0, 25, ct);

    /// <inheritdoc />
    public async Task<IReadOnlyList<SearchResult>> GetSteamDbListAsync(string listType, int offset, int count = 25, CancellationToken ct = default)
    {
        var cacheKey = $"steam_list_{listType}_{offset}_{count}_v5";
        try
        {
            var cached = await _cacheService.GetAsync<List<SearchResult>>(cacheKey, ct).ConfigureAwait(false);
            if (cached != null && cached.Count > 0) return Deduplicate(cached);
        }
        catch { }

        // If offset is 0, check worker first
        if (offset == 0)
        {
            try
            {
                var workerUrl = $"{GetWorkerBaseUrl()}/api/steam/lists?type={listType}";
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(3));

                var response = await _httpClient.GetFromJsonAsync<StatsApiResponse>(workerUrl, cts.Token).ConfigureAwait(false);
                if (response?.Results != null && response.Results.Count >= count)
                {
                    var list = Deduplicate(response.Results.Select(r => r.ToSearchResult()));
                    await _cacheService.SetAsync(cacheKey, list, TimeSpan.FromHours(1), ct).ConfigureAwait(false);
                    return list;
                }
            }
            catch { }
        }

        // Direct Steam Search API query with infinite pagination support for real Steam ranking lists
        var steamFilter = listType.ToLowerInvariant() switch
        {
            "top_sellers" or "steamdb_top_sellers" => "filter=topsellers",
            "trending" or "steamdb_trending" => "filter=specials",
            "top_rated" or "steamdb_top_rated" => "sort_by=Reviews_DESC",
            "most_played" or "steamdb_most_played" => "filter=topsellers",
            _ => "filter=topsellers"
        };

        var direct = await FetchSteamSearchResultsAsync(steamFilter, offset, count, ct).ConfigureAwait(false);
        if (direct.Count > 0)
        {
            var distinctDirect = Deduplicate(direct);
            try
            {
                await _cacheService.SetAsync(cacheKey, distinctDirect, TimeSpan.FromMinutes(30), ct).ConfigureAwait(false);
            }
            catch { }
            return distinctDirect;
        }

        return Array.Empty<SearchResult>();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SearchResult>> SearchSteamGamesAsync(string query, int offset = 0, int count = 25, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return await GetSteamDbListAsync("top_sellers", offset, count, ct).ConfigureAwait(false);
        }

        return await FetchSteamSearchResultsAsync(query, offset, count, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SearchResult>> GetDepotBoxFeedAsync(string feedType, CancellationToken ct = default)
    {
        var cacheKey = $"depotbox_feed_{feedType}_v4";
        try
        {
            var cached = await _cacheService.GetAsync<List<SearchResult>>(cacheKey, ct).ConfigureAwait(false);
            if (cached != null) return Deduplicate(cached);
        }
        catch { }

        try
        {
            var workerUrl = $"{GetWorkerBaseUrl()}/api/feed/depotbox/{feedType}";
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(3));

            var response = await _httpClient.GetFromJsonAsync<StatsApiResponse>(workerUrl, cts.Token).ConfigureAwait(false);
            if (response?.Results != null && response.Results.Count > 0)
            {
                var list = Deduplicate(response.Results.Select(r => r.ToSearchResult()));
                await _cacheService.SetAsync(cacheKey, list, TimeSpan.FromMinutes(5), ct).ConfigureAwait(false);
                return list;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not fetch DepotBox webhook feed {FeedType} from Cloudflare worker", feedType);
        }

        return Array.Empty<SearchResult>();
    }

    public async Task<IReadOnlyList<SearchResult>> FetchSteamSearchResultsAsync(string filterOrQuery, int start, int count, CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(6));

            string url;
            if (filterOrQuery.StartsWith("filter=", StringComparison.OrdinalIgnoreCase) || filterOrQuery.StartsWith("sort_by=", StringComparison.OrdinalIgnoreCase))
            {
                url = $"https://store.steampowered.com/search/results/?query=&start={start}&count={count}&{filterOrQuery}&infinite=1";
            }
            else
            {
                url = $"https://store.steampowered.com/search/results/?term={Uri.EscapeDataString(filterOrQuery)}&start={start}&count={count}&infinite=1";
            }

            var jsonResp = await _httpClient.GetFromJsonAsync<JsonElement>(url, cts.Token).ConfigureAwait(false);
            if (jsonResp.TryGetProperty("results_html", out var htmlProp))
            {
                var html = htmlProp.GetString();
                if (!string.IsNullOrWhiteSpace(html))
                {
                    var results = new List<SearchResult>();
                    var matches = SteamSearchItemRegex.Matches(html);
                    foreach (System.Text.RegularExpressions.Match match in matches)
                    {
                        if (uint.TryParse(match.Groups["appid"].Value, out var appId) && appId > 0)
                        {
                            var rawTitle = match.Groups["title"].Value.Trim();
                            var title = System.Net.WebUtility.HtmlDecode(rawTitle);

                            var slice = match.Value;
                            var headerImg = $"https://shared.cloudflare.steamstatic.com/store_item_assets/steam/apps/{appId}/header.jpg";

                            var dateMatch = SteamReleaseDateRegex.Match(slice);
                            var releaseDate = dateMatch.Success ? dateMatch.Groups["release"].Value.Trim() : null;

                            var hasMac = slice.Contains("platform_img mac", StringComparison.OrdinalIgnoreCase);
                            var hasLinux = slice.Contains("platform_img linux", StringComparison.OrdinalIgnoreCase);
                            var hasWin = slice.Contains("platform_img win", StringComparison.OrdinalIgnoreCase) || (!hasMac && !hasLinux);

                            results.Add(new SearchResult
                            {
                                AppId = appId,
                                Name = title,
                                AppType = "Game",
                                HasWindows = hasWin,
                                HasMac = hasMac,
                                HasLinux = hasLinux,
                                HeaderImageUrl = headerImg,
                                Version = !string.IsNullOrWhiteSpace(releaseDate) ? releaseDate : null
                            });
                        }
                    }

                    if (results.Count > 0) return Deduplicate(results);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Steam search request failed for query '{Query}' at start={Start}", filterOrQuery, start);
        }

        // Fallback: StoreSearch API
        try
        {
            var term = string.IsNullOrWhiteSpace(filterOrQuery) ? "game" : filterOrQuery.Replace("filter=", "").Replace("sort_by=", "");
            var page = (start / Math.Max(1, count)) + 1;
            var storeUrl = $"https://store.steampowered.com/api/storesearch/?term={Uri.EscapeDataString(term)}&l=english&cc=US&page={page}";

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(5));

            var storeResp = await _httpClient.GetFromJsonAsync<JsonElement>(storeUrl, cts.Token).ConfigureAwait(false);
            if (storeResp.TryGetProperty("items", out var itemsProp) && itemsProp.ValueKind == JsonValueKind.Array)
            {
                var results = new List<SearchResult>();
                foreach (var item in itemsProp.EnumerateArray())
                {
                    var id = item.TryGetProperty("id", out var idProp) ? (uint)idProp.GetInt32() : 0u;
                    var name = item.TryGetProperty("name", out var nameProp) ? nameProp.GetString() ?? $"Game {id}" : $"Game {id}";
                    var headerImg = $"https://shared.cloudflare.steamstatic.com/store_item_assets/steam/apps/{id}/header.jpg";

                    if (id > 0)
                    {
                        results.Add(new SearchResult
                        {
                            AppId = id,
                            Name = name,
                            AppType = "Game",
                            HasWindows = true,
                            HeaderImageUrl = headerImg,
                            Version = "Steam Store"
                        });
                    }
                }
                return Deduplicate(results);
            }
        }
        catch { }

        return [];
    }

    /// <inheritdoc />
    public async Task ReportInstanceAddedAsync(uint appId, string name, CancellationToken ct = default)
    {
        if (appId <= 0) return;

        var workerUrl = $"{GetWorkerBaseUrl()}/api/stats/report-instance";
        var payload = new { appId, name };

        var resp = await CallWorkerAsync(
            "Community instance report",
            async token => (HttpResponseMessage?)await _httpClient
                .PostAsJsonAsync(workerUrl, payload, token).ConfigureAwait(false),
            ct).ConfigureAwait(false);

        if (resp is null) return;

        using (resp)
        {
            _logger.LogInformation("Reported instance to Cloudflare: {Name} (AppId={AppId}), Status={Status}", name, appId, resp.StatusCode);
        }

        try
        {
            // Invalidate local feeds cache so subsequent refreshes pull new data
            await _cacheService.RemoveAsync("bluestar_trending_7d_v7", CancellationToken.None).ConfigureAwait(false);
            await _cacheService.RemoveAsync("bluestar_most_played_alltime_v7", CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // The next refresh will pick the new data up anyway.
        }
    }

    /// <inheritdoc />
    public async Task ReportGamePlayAsync(int appId, string name, TimeSpan duration, CancellationToken ct = default)
    {
        if (appId <= 0) return;

        try
        {
            var workerUrl = $"{GetWorkerBaseUrl()}/api/stats/report-instance";
            var payload = new
            {
                appId,
                name,
                durationMinutes = Math.Max(1, (int)duration.TotalMinutes)
            };

            var resp = await CallWorkerAsync(
                "Community playtime report",
                async token => (HttpResponseMessage?)await _httpClient
                    .PostAsJsonAsync(workerUrl, payload, token).ConfigureAwait(false),
                ct).ConfigureAwait(false);

            resp?.Dispose();
        }
        catch { }
    }

    /// <inheritdoc />
    public async Task SyncInstancesAsync(IEnumerable<GameInstance> instances, CancellationToken ct = default)
    {
        if (instances == null) return;
        var valid = instances
            .Where(i => i.AppId > 0)
            .Select(i => new { appId = i.AppId, name = i.Name })
            .ToList();

        if (valid.Count == 0) return;

        var workerUrl = $"{GetWorkerBaseUrl()}/api/stats/sync-instances";
        var payload = new { instances = valid };

        var resp = await CallWorkerAsync(
            "Community instance sync",
            async token => (HttpResponseMessage?)await _httpClient
                .PostAsJsonAsync(workerUrl, payload, token).ConfigureAwait(false),
            ct).ConfigureAwait(false);

        if (resp is null) return;

        using (resp)
        {
            _logger.LogInformation("Synced {Count} local instances to Cloudflare: Status={Status}", valid.Count, resp.StatusCode);
        }

        try
        {
            await _cacheService.RemoveAsync("bluestar_trending_7d_v7", CancellationToken.None).ConfigureAwait(false);
            await _cacheService.RemoveAsync("bluestar_most_played_alltime_v7", CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // The next refresh will pick the new data up anyway.
        }
    }

    private static List<SearchResult> Deduplicate(IEnumerable<SearchResult> source)
    {
        var seen = new HashSet<uint>();
        var result = new List<SearchResult>();
        foreach (var item in source)
        {
            if (item.AppId > 0 && seen.Add(item.AppId))
            {
                result.Add(item);
            }
        }
        return result;
    }

    private async Task<IReadOnlyList<SearchResult>> FetchSteamStoreCategoryAsync(string category, CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(5));

            var data = await _httpClient.GetFromJsonAsync<JsonElement>("https://store.steampowered.com/api/featuredcategories", cts.Token).ConfigureAwait(false);
            if (data.TryGetProperty(category, out var catProp) && catProp.TryGetProperty("items", out var itemsProp) && itemsProp.ValueKind == JsonValueKind.Array)
            {
                var results = new List<SearchResult>();
                foreach (var item in itemsProp.EnumerateArray())
                {
                    var id = item.TryGetProperty("id", out var idProp) ? (uint)idProp.GetInt32() : 0u;
                    var name = item.TryGetProperty("name", out var nameProp) ? nameProp.GetString() ?? $"Game {id}" : $"Game {id}";
                    var headerImg = item.TryGetProperty("header_image", out var imgProp)
                        ? imgProp.GetString()
                        : $"https://shared.cloudflare.steamstatic.com/store_item_assets/steam/apps/{id}/header.jpg";

                    var hasWin = item.TryGetProperty("windows_available", out var winProp) && winProp.GetBoolean();
                    var hasMac = item.TryGetProperty("mac_available", out var macProp) && macProp.GetBoolean();
                    var hasLinux = item.TryGetProperty("linux_available", out var linProp) && linProp.GetBoolean();

                    if (id > 0)
                    {
                        results.Add(new SearchResult
                        {
                            AppId = id,
                            Name = name,
                            AppType = "Game",
                            HasWindows = hasWin || (!hasMac && !hasLinux),
                            HasMac = hasMac,
                            HasLinux = hasLinux,
                            HeaderImageUrl = headerImg,
                            Version = "Steam Store"
                        });
                    }
                }

                if (results.Count > 0) return Deduplicate(results);
            }
        }
        catch { }

        return [];
    }

    private class StatsApiResponse
    {
        [JsonPropertyName("count")]
        public int Count { get; set; }

        [JsonPropertyName("results")]
        public List<StatsItemDto> Results { get; set; } = [];
    }

    private class StatsItemDto
    {
        [JsonPropertyName("appId")]
        public uint AppId { get; set; }

        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("appType")]
        public string? AppType { get; set; }

        [JsonPropertyName("version")]
        public string? Version { get; set; }

        [JsonPropertyName("dlcCount")]
        public int? DlcCount { get; set; }

        [JsonPropertyName("hasWindows")]
        public bool? HasWindows { get; set; }

        [JsonPropertyName("hasLinux")]
        public bool? HasLinux { get; set; }

        [JsonPropertyName("hasMac")]
        public bool? HasMac { get; set; }

        [JsonPropertyName("headerImageUrl")]
        public string? HeaderImageUrl { get; set; }

        public SearchResult ToSearchResult()
        {
            var cleanVersion = Version;
            if (!string.IsNullOrEmpty(cleanVersion) && 
                (cleanVersion.Contains("community instances", StringComparison.OrdinalIgnoreCase) || 
                 cleanVersion.Contains("added this week", StringComparison.OrdinalIgnoreCase)))
            {
                cleanVersion = null;
            }

            return new SearchResult
            {
                AppId = AppId,
                Name = Name,
                AppType = AppType ?? "Game",
                Version = cleanVersion,
                DlcCount = DlcCount,
                HasWindows = HasWindows ?? true,
                HasLinux = HasLinux ?? false,
                HasMac = HasMac ?? false,
                HeaderImageUrl = HeaderImageUrl ?? (AppId > 0 ? $"https://shared.cloudflare.steamstatic.com/store_item_assets/steam/apps/{AppId}/header.jpg" : null)
            };
        }
    }
}
