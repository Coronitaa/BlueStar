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

    /// <inheritdoc />
    public async Task<IReadOnlyList<SearchResult>> GetTrendingBlueStarAsync(CancellationToken ct = default)
    {
        const string cacheKey = "bluestar_trending_7d_v2";
        try
        {
            var cached = await _cacheService.GetAsync<List<SearchResult>>(cacheKey, ct).ConfigureAwait(false);
            if (cached != null && cached.Count > 0) return Deduplicate(cached);
        }
        catch { }

        try
        {
            var workerUrl = $"{GetWorkerBaseUrl()}/api/stats/trending";
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(4));

            var response = await _httpClient.GetFromJsonAsync<StatsApiResponse>(workerUrl, cts.Token).ConfigureAwait(false);
            if (response?.Results != null && response.Results.Count > 0)
            {
                var list = Deduplicate(response.Results.Select(r => r.ToSearchResult()));
                await _cacheService.SetAsync(cacheKey, list, TimeSpan.FromMinutes(10), ct).ConfigureAwait(false);
                return list;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not fetch real trending stats from Cloudflare worker");
        }

        return Array.Empty<SearchResult>();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SearchResult>> GetMostPlayedBlueStarAsync(CancellationToken ct = default)
    {
        const string cacheKey = "bluestar_most_played_alltime_v2";
        try
        {
            var cached = await _cacheService.GetAsync<List<SearchResult>>(cacheKey, ct).ConfigureAwait(false);
            if (cached != null && cached.Count > 0) return Deduplicate(cached);
        }
        catch { }

        try
        {
            var workerUrl = $"{GetWorkerBaseUrl()}/api/stats/most-played";
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(4));

            var response = await _httpClient.GetFromJsonAsync<StatsApiResponse>(workerUrl, cts.Token).ConfigureAwait(false);
            if (response?.Results != null && response.Results.Count > 0)
            {
                var list = Deduplicate(response.Results.Select(r => r.ToSearchResult()));
                await _cacheService.SetAsync(cacheKey, list, TimeSpan.FromMinutes(20), ct).ConfigureAwait(false);
                return list;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not fetch all-time most added stats from Cloudflare worker");
        }

        return Array.Empty<SearchResult>();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SearchResult>> GetSteamDbListAsync(string listType, CancellationToken ct = default)
    {
        var cacheKey = $"steam_list_{listType}";
        try
        {
            var cached = await _cacheService.GetAsync<List<SearchResult>>(cacheKey, ct).ConfigureAwait(false);
            if (cached != null && cached.Count > 0) return Deduplicate(cached);
        }
        catch { }

        try
        {
            var workerUrl = $"{GetWorkerBaseUrl()}/api/steam/lists?type={listType}";
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(4));

            var response = await _httpClient.GetFromJsonAsync<StatsApiResponse>(workerUrl, cts.Token).ConfigureAwait(false);
            if (response?.Results != null && response.Results.Count > 0)
            {
                var list = Deduplicate(response.Results.Select(r => r.ToSearchResult()));
                await _cacheService.SetAsync(cacheKey, list, TimeSpan.FromHours(1), ct).ConfigureAwait(false);
                return list;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not fetch Steam list {ListType} from Cloudflare worker, querying Steam Store directly", listType);
        }

        // Direct Steam Store Featured API fallback for Steam lists
        var steamCategory = listType switch
        {
            "top_sellers" => "top_sellers",
            "trending" => "specials",
            "top_rated" => "new_releases",
            _ => "top_sellers"
        };

        var direct = await FetchSteamStoreCategoryAsync(steamCategory, ct).ConfigureAwait(false);
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
    public async Task<IReadOnlyList<SearchResult>> GetDepotBoxFeedAsync(string feedType, CancellationToken ct = default)
    {
        var cacheKey = $"depotbox_feed_{feedType}";
        try
        {
            var cached = await _cacheService.GetAsync<List<SearchResult>>(cacheKey, ct).ConfigureAwait(false);
            if (cached != null && cached.Count > 0) return Deduplicate(cached);
        }
        catch { }

        try
        {
            var workerUrl = $"{GetWorkerBaseUrl()}/api/feed/depotbox/{feedType}";
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(4));

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
            _logger.LogDebug(ex, "Could not fetch DepotBox feed {FeedType} from Cloudflare worker", feedType);
        }

        return Array.Empty<SearchResult>();
    }

    /// <inheritdoc />
    public async Task ReportInstanceAddedAsync(uint appId, string name, CancellationToken ct = default)
    {
        if (appId <= 0) return;

        try
        {
            var workerUrl = $"{GetWorkerBaseUrl()}/api/stats/report-instance";
            var payload = new { appId, name };
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await _httpClient.PostAsJsonAsync(workerUrl, payload, cts.Token).ConfigureAwait(false);
        }
        catch { }
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

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await _httpClient.PostAsJsonAsync(workerUrl, payload, cts.Token).ConfigureAwait(false);
        }
        catch { }
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
            return new SearchResult
            {
                AppId = AppId,
                Name = Name,
                AppType = AppType ?? "Game",
                Version = Version ?? "Ready",
                DlcCount = DlcCount,
                HasWindows = HasWindows ?? true,
                HasLinux = HasLinux ?? false,
                HasMac = HasMac ?? false,
                HeaderImageUrl = HeaderImageUrl ?? (AppId > 0 ? $"https://shared.cloudflare.steamstatic.com/store_item_assets/steam/apps/{AppId}/header.jpg" : null)
            };
        }
    }
}
