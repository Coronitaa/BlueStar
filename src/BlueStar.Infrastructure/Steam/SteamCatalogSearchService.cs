using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using BlueStar.Core.Helpers;
using BlueStar.Core.Interfaces;
using BlueStar.Core.Models;
using Microsoft.Extensions.Logging;

namespace BlueStar.Infrastructure.Steam;

/// <summary>
/// Faceted search against the Steam store search endpoint.
/// </summary>
/// <remarks>
/// The endpoint answers with a JSON envelope whose <c>results_html</c> is a fragment of store
/// markup. Every row carries the app id, the store tag ids and the content descriptors as data
/// attributes, so a single request yields the results and their tags with no follow-up calls.
/// </remarks>
public sealed partial class SteamCatalogSearchService : ISteamCatalogSearchService
{
    private readonly HttpClient _http;
    private readonly ICacheService? _cache;
    private readonly ILogger<SteamCatalogSearchService> _logger;
    private readonly SingleFlight _singleFlight = new();

    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(12);

    /// <summary>Search pages change often enough that a short cache is all that is safe.</summary>
    private static readonly TimeSpan PageCacheTtl = TimeSpan.FromMinutes(10);

    /// <summary>Match counts move slowly; a day is plenty and keeps the bubbles instant.</summary>
    private static readonly TimeSpan CountCacheTtl = TimeSpan.FromHours(24);

    private static readonly TimeSpan EventsCacheTtl = TimeSpan.FromMinutes(30);

    /// <summary>A title's spelling does not change; half a day of memory saves a round trip.</summary>
    private static readonly TimeSpan SuggestCacheTtl = TimeSpan.FromHours(12);

    /// <summary>
    /// Initializes a new instance of the <see cref="SteamCatalogSearchService"/> class.
    /// </summary>
    public SteamCatalogSearchService(
        HttpClient http,
        ILogger<SteamCatalogSearchService> logger,
        ICacheService? cache = null)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _cache = cache;
    }

    [GeneratedRegex("""<a [^>]*?data-ds-appid="(?<appid>\d+)"(?<attrs>[^>]*)>(?<body>.*?)</a>""", RegexOptions.Singleline)]
    private static partial Regex RowRegex();

    [GeneratedRegex("""data-ds-tagids="\[(?<ids>[^\]]*)\]""" )]
    private static partial Regex TagIdsRegex();

    [GeneratedRegex("""data-ds-descids="\[(?<ids>[^\]]*)\]""" )]
    private static partial Regex DescIdsRegex();

    [GeneratedRegex("""<span class="title">(?<title>[^<]*)</span>""")]
    private static partial Regex TitleRegex();

    [GeneratedRegex("""<div class="search_released[^"]*">(?<date>[^<]*)</div>""")]
    private static partial Regex ReleasedRegex();

    [GeneratedRegex("""<span class="search_review_summary [^"]*" data-tooltip-html="(?<tip>[^"]*)"></span>""")]
    private static partial Regex ReviewRegex();

    [GeneratedRegex("""<div class="discount_pct">(?<pct>[^<]*)</div>""")]
    private static partial Regex DiscountPctRegex();

    [GeneratedRegex("""<div class="discount_original_price">(?<price>[^<]*)</div>""")]
    private static partial Regex OriginalPriceRegex();

    [GeneratedRegex("""<div class="discount_final_price[^"]*">(?<price>[^<]*)</div>""")]
    private static partial Regex FinalPriceRegex();

    /// <inheritdoc />
    public async Task<SteamSearchPage> SearchAsync(SteamSearchQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var url = query.ToUrl();
        var cacheKey = "steam_search_v1_" + Hash(url);

        if (_cache != null)
        {
            try
            {
                var cached = await _cache.GetAsync<CachedPage>(cacheKey, ct).ConfigureAwait(false);
                if (cached?.Items != null)
                {
                    return new SteamSearchPage(cached.Items, cached.TotalCount, query.Start);
                }
            }
            catch
            {
                // A cache miss must never fail a search.
            }
        }

        return await _singleFlight.ExecuteAsync(cacheKey, async token =>
        {
            var envelope = await GetEnvelopeAsync(url, SteamRequestPriority.Interactive, token).ConfigureAwait(false);
            if (envelope is null) return SteamSearchPage.Empty;

            var items = ParseRows(envelope.Value.Html, query.AppTypes);
            if (query.RestrictToAppIds is { Count: > 0 } restrictSet)
            {
                var set = restrictSet.ToHashSet();
                items = items.Where(i => set.Contains(i.AppId)).ToList();
            }
            var page = new SteamSearchPage(items, envelope.Value.TotalCount, query.Start, envelope.Value.RawPayload);

            if (_cache != null && items.Count > 0)
            {
                try
                {
                    await _cache.SetAsync(cacheKey,
                        new CachedPage { Items = items, TotalCount = envelope.Value.TotalCount },
                        PageCacheTtl, token).ConfigureAwait(false);
                }
                catch
                {
                    // Caching is best effort.
                }
            }

            return page;
        }, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<int?> GetMatchCountAsync(SteamSearchQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var url = query.AsCountProbe().ToUrl();
        var cacheKey = "steam_count_v1_" + Hash(url);

        if (_cache != null)
        {
            try
            {
                var cached = await _cache.GetAsync<CachedCount>(cacheKey, ct).ConfigureAwait(false);
                if (cached != null) return cached.Value;
            }
            catch
            {
                // Ignored.
            }
        }

        return await _singleFlight.ExecuteAsync(cacheKey, async token =>
        {
            var envelope = await GetEnvelopeAsync(url, SteamRequestPriority.Background, token).ConfigureAwait(false);

            // Unknown is not zero: a blocked or failed probe leaves the bubble without a number
            // rather than claiming the tag matches nothing.
            if (envelope is null) return (int?)null;

            if (_cache != null)
            {
                try
                {
                    await _cache.SetAsync(cacheKey, new CachedCount { Value = envelope.Value.TotalCount },
                        CountCacheTtl, token).ConfigureAwait(false);
                }
                catch
                {
                    // Ignored.
                }
            }

            return (int?)envelope.Value.TotalCount;
        }, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SteamTitleSuggestion>> SuggestTitlesAsync(
        string term, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(term)) return [];

        var trimmed = term.Trim();
        var cacheKey = "steam_suggest_v1_" + Hash(trimmed.ToUpperInvariant());

        if (_cache != null)
        {
            try
            {
                var cached = await _cache.GetAsync<List<SteamTitleSuggestion>>(cacheKey, ct).ConfigureAwait(false);
                if (cached != null) return cached;
            }
            catch
            {
                // A cache miss must never fail a search.
            }
        }

        var url = "https://store.steampowered.com/api/storesearch/?term="
                  + Uri.EscapeDataString(trimmed) + "&l=english&cc=US";

        return await _singleFlight.ExecuteAsync(cacheKey, async token =>
        {
            try
            {
                using var lease = await SteamRequestGate
                    .AcquireAsync(SteamRequestPriority.Interactive, token).ConfigureAwait(false);

                if (lease is null) return (IReadOnlyList<SteamTitleSuggestion>)[];

                using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
                cts.CancelAfter(RequestTimeout);

                using var response = await _http.GetAsync(url, cts.Token).ConfigureAwait(false);

                if (response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.Forbidden)
                {
                    SteamRequestGate.ReportBlocked(response.StatusCode);
                    return [];
                }

                if (!response.IsSuccessStatusCode) return [];

                var payload = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
                SteamRequestGate.ReportSuccess();

                using var doc = JsonDocument.Parse(payload);

                if (!doc.RootElement.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
                {
                    return [];
                }

                var suggestions = new List<SteamTitleSuggestion>();
                var seen = new HashSet<uint>();

                foreach (var item in items.EnumerateArray())
                {
                    if (!item.TryGetProperty("id", out var idProp) || !idProp.TryGetUInt32(out var appId) || appId == 0)
                    {
                        continue;
                    }

                    var name = item.TryGetProperty("name", out var n) ? n.GetString() : null;
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    if (!seen.Add(appId)) continue;

                    suggestions.Add(new SteamTitleSuggestion(appId, WebUtility.HtmlDecode(name).Trim()));
                }

                if (_cache != null && suggestions.Count > 0)
                {
                    try
                    {
                        await _cache.SetAsync(cacheKey, suggestions, SuggestCacheTtl, token).ConfigureAwait(false);
                    }
                    catch
                    {
                        // Caching is best effort.
                    }
                }

                return suggestions;
            }
            catch (OperationCanceledException)
            {
                return [];
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException)
            {
                _logger.LogDebug(ex, "Steam title autocomplete failed for {Term}", trimmed);
                return [];
            }
        }, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SteamStoreEvent>> GetStoreEventsAsync(CancellationToken ct = default)
    {
        const string cacheKey = "steam_store_events_v1";

        if (_cache != null)
        {
            try
            {
                var cached = await _cache.GetAsync<List<SteamStoreEvent>>(cacheKey, ct).ConfigureAwait(false);
                if (cached is { Count: > 0 }) return cached;
            }
            catch
            {
                // Ignored.
            }
        }

        try
        {
            using var lease = await SteamRequestGate
                .AcquireAsync(SteamRequestPriority.Background, ct).ConfigureAwait(false);

            if (lease is null) return [];

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(RequestTimeout);

            var json = await _http.GetFromJsonAsync<JsonElement>(
                "https://store.steampowered.com/api/featuredcategories/?cc=US&l=english",
                cts.Token).ConfigureAwait(false);

            SteamRequestGate.ReportSuccess();

            var events = new List<SteamStoreEvent>();

            // Spotlights are the seasonal sales and fests. They carry a landing page but no app
            // ids, so their chips open the store instead of narrowing the results.
            foreach (var property in json.EnumerateObject())
            {
                if (property.Value.ValueKind != JsonValueKind.Object) continue;
                if (!property.Value.TryGetProperty("id", out var idProp)) continue;
                if (idProp.GetString() != "cat_spotlight") continue;
                if (!property.Value.TryGetProperty("items", out var spotItems)) continue;

                foreach (var item in spotItems.EnumerateArray())
                {
                    var name = item.TryGetProperty("name", out var n) ? n.GetString() : null;
                    if (string.IsNullOrWhiteSpace(name)) continue;

                    events.Add(new SteamStoreEvent
                    {
                        Id = "spotlight_" + Hash(name),
                        Title = name,
                        Subtitle = item.TryGetProperty("body", out var b) ? Trim(b.GetString(), 70) : string.Empty,
                        IconKey = "IconFlame",
                        Url = item.TryGetProperty("url", out var u) ? u.GetString() : null
                    });
                }
            }

            AddIdBearingCategory(json, "specials", "Specials", "IconPercent", events);
            AddIdBearingCategory(json, "top_sellers", "Top sellers", "IconTag", events);
            AddIdBearingCategory(json, "new_releases", "New releases", "IconSpark", events);
            AddIdBearingCategory(json, "coming_soon", "Coming soon", "IconCalendar", events);

            if (_cache != null && events.Count > 0)
            {
                try
                {
                    await _cache.SetAsync(cacheKey, events, EventsCacheTtl, ct).ConfigureAwait(false);
                }
                catch
                {
                    // Ignored.
                }
            }

            return events;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            _logger.LogDebug(ex, "Could not read the Steam featured categories");
            return [];
        }
    }

    /// <summary>
    /// Turns one featured category into an event carrying its products.
    /// </summary>
    /// <remarks>
    /// Steam already sends the name, capsule, platforms, price and discount for every item in
    /// this feed, so an event arrives complete. Picking one can narrow the results without a
    /// single further request, and its chip can state a real count rather than however many of
    /// its games happen to be on the current page.
    /// </remarks>
    private static void AddIdBearingCategory(
        JsonElement root, string key, string title, string iconKey, List<SteamStoreEvent> into)
    {
        if (!root.TryGetProperty(key, out var cat) || cat.ValueKind != JsonValueKind.Object) return;
        if (!cat.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array) return;

        var products = new List<SearchResult>();
        var ids = new List<uint>();

        foreach (var item in items.EnumerateArray())
        {
            if (!item.TryGetProperty("id", out var idProp) || !idProp.TryGetUInt32(out var appId) || appId == 0)
            {
                continue;
            }

            if (ids.Contains(appId)) continue;
            ids.Add(appId);

            var result = new SearchResult
            {
                AppId = appId,
                Name = item.TryGetProperty("name", out var n) ? n.GetString() ?? $"App {appId}" : $"App {appId}",
                AppType = "Game",
                HeaderImageUrl = $"https://shared.cloudflare.steamstatic.com/store_item_assets/steam/apps/{appId}/header.jpg",
                HasWindows = !item.TryGetProperty("windows_available", out var w) || w.ValueKind != JsonValueKind.False,
                HasMac = item.TryGetProperty("mac_available", out var m) && m.ValueKind == JsonValueKind.True,
                HasLinux = item.TryGetProperty("linux_available", out var l) && l.ValueKind == JsonValueKind.True
            };

            var currency = item.TryGetProperty("currency", out var c) ? c.GetString() : null;

            if (item.TryGetProperty("final_price", out var fp) && fp.TryGetInt32(out var finalCents))
            {
                result.PriceText = FormatPrice(finalCents, currency);
            }

            if (item.TryGetProperty("discount_percent", out var dp) && dp.TryGetInt32(out var pct) && pct > 0)
            {
                result.DiscountPercent = pct;

                if (item.TryGetProperty("original_price", out var op) && op.TryGetInt32(out var originalCents))
                {
                    result.OriginalPriceText = FormatPrice(originalCents, currency);
                }
            }

            products.Add(result);
        }

        if (products.Count == 0) return;

        into.Add(new SteamStoreEvent
        {
            Id = "cat_" + key,
            Title = cat.TryGetProperty("name", out var catName) ? catName.GetString() ?? title : title,
            IconKey = iconKey,
            Items = products,
            AppIds = ids
        });
    }

    private static string FormatPrice(int cents, string? currency)
    {
        if (cents <= 0) return "Free";

        var amount = (cents / 100d).ToString("0.00", CultureInfo.InvariantCulture);

        return currency switch
        {
            "USD" or null or "" => "$" + amount,
            "EUR" => amount + " €",
            "GBP" => "£" + amount,
            _ => amount + " " + currency
        };
    }

    private async Task<(string Html, int TotalCount, string RawPayload)?> GetEnvelopeAsync(
        string url, SteamRequestPriority priority, CancellationToken ct)
    {
        using var lease = await SteamRequestGate.AcquireAsync(priority, ct).ConfigureAwait(false);
        if (lease is null) return null;

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(RequestTimeout);

            using var response = await _http.GetAsync(url, cts.Token).ConfigureAwait(false);

            if (response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.Forbidden)
            {
                SteamRequestGate.ReportBlocked(response.StatusCode);
                return null;
            }

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Steam store search answered {StatusCode}", response.StatusCode);
                return null;
            }

            var payload = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
            SteamRequestGate.ReportSuccess();

            using var doc = JsonDocument.Parse(payload);

            var html = doc.RootElement.TryGetProperty("results_html", out var h) ? h.GetString() ?? string.Empty : string.Empty;
            var total = doc.RootElement.TryGetProperty("total_count", out var t) && t.TryGetInt32(out var parsed) ? parsed : 0;

            return (html, total, payload);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException)
        {
            _logger.LogWarning(ex, "Steam store search request failed");
            return null;
        }
    }

    /// <summary>
    /// Turns the store markup fragment into results. Anything the markup does not state is left
    /// unset rather than guessed: DRM, DLC count and Deck rating arrive later, from appdetails.
    /// </summary>
    private static List<SearchResult> ParseRows(string html, string? appTypes = null)
    {
        var results = new List<SearchResult>();
        if (string.IsNullOrWhiteSpace(html)) return results;

        var seen = new HashSet<uint>();

        foreach (Match row in RowRegex().Matches(html))
        {
            if (!uint.TryParse(row.Groups["appid"].Value, out var appId) || appId == 0) continue;
            if (!seen.Add(appId)) continue;

            var attrs = row.Groups["attrs"].Value;
            var body = row.Groups["body"].Value;

            var titleMatch = TitleRegex().Match(body);
            var name = titleMatch.Success
                ? WebUtility.HtmlDecode(titleMatch.Groups["title"].Value).Trim()
                : $"App {appId}";

            var hasWin = body.Contains("platform_img win", StringComparison.OrdinalIgnoreCase);
            var hasMac = body.Contains("platform_img mac", StringComparison.OrdinalIgnoreCase);
            var hasLinux = body.Contains("platform_img linux", StringComparison.OrdinalIgnoreCase);

            var isSoftware = string.Equals(appTypes, "994", StringComparison.OrdinalIgnoreCase)
                             || body.Contains("for this software", StringComparison.OrdinalIgnoreCase);

            var result = new SearchResult
            {
                AppId = appId,
                Name = name,
                AppType = isSoftware ? "Software" : "Game",
                HeaderImageUrl = $"https://shared.cloudflare.steamstatic.com/store_item_assets/steam/apps/{appId}/header.jpg",
                HasWindows = hasWin || (!hasMac && !hasLinux),
                HasMac = hasMac,
                HasLinux = hasLinux,
                TagIds = ParseIntList(TagIdsRegex().Match(attrs)),
                ReleaseDateText = ReleasedRegex().Match(body) is { Success: true } d
                    ? WebUtility.HtmlDecode(d.Groups["date"].Value).Trim()
                    : null
            };

            // Content descriptors 3 and 4 are Steam's adult-content markers.
            var descIds = ParseIntList(DescIdsRegex().Match(attrs));
            result.IsNsfw = descIds.Contains(3) || descIds.Contains(4);

            ApplyReview(result, body);
            ApplyPrice(result, body);

            result.Version = result.ReleaseDateText;
            results.Add(result);
        }

        return results;
    }

    private static void ApplyReview(SearchResult result, string body)
    {
        var match = ReviewRegex().Match(body);
        if (!match.Success) return;

        var tip = WebUtility.HtmlDecode(match.Groups["tip"].Value);
        var head = tip.Split("<br>", StringSplitOptions.RemoveEmptyEntries);
        if (head.Length == 0) return;

        result.ReviewSummary = head[0].Trim();

        if (head.Length > 1)
        {
            var percentText = head[1].AsSpan();
            var cut = percentText.IndexOf('%');
            if (cut > 0 && int.TryParse(percentText[..cut], NumberStyles.Integer, CultureInfo.InvariantCulture, out var pct))
            {
                result.ReviewPercent = pct;
            }
        }
    }

    private static void ApplyPrice(SearchResult result, string body)
    {
        var final = FinalPriceRegex().Match(body);
        if (final.Success)
        {
            result.PriceText = WebUtility.HtmlDecode(final.Groups["price"].Value).Trim();
        }

        var pct = DiscountPctRegex().Match(body);
        if (!pct.Success) return;

        var digits = new string(pct.Groups["pct"].Value.Where(char.IsDigit).ToArray());
        if (int.TryParse(digits, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            result.DiscountPercent = parsed;
        }

        var original = OriginalPriceRegex().Match(body);
        if (original.Success)
        {
            result.OriginalPriceText = WebUtility.HtmlDecode(original.Groups["price"].Value).Trim();
        }
    }

    private static IReadOnlyList<int> ParseIntList(Match match)
    {
        if (!match.Success) return [];

        var raw = match.Groups["ids"].Value;
        if (string.IsNullOrWhiteSpace(raw)) return [];

        var list = new List<int>();
        foreach (var part in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
            {
                list.Add(value);
            }
        }

        return list;
    }

    /// <summary>
    /// Cleans a spotlight blurb for display.
    /// </summary>
    /// <remarks>
    /// Steam serves these strings with its own substitution markers still in them — "Offer ends
    /// %1$s." and friends — which it fills in client-side. We have nothing to fill them with, so
    /// any sentence carrying one is dropped rather than shown raw.
    /// </remarks>
    private static string Trim(string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;

        var clean = Regex.Replace(WebUtility.HtmlDecode(value), "<.*?>", " ");
        clean = Regex.Replace(clean, @"\s+", " ").Trim();

        var kept = clean
            .Split(['.', '!', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(sentence => !PlaceholderRegex().IsMatch(sentence))
            .ToList();

        clean = string.Join(". ", kept).Trim();
        if (clean.Length == 0) return string.Empty;

        return clean.Length <= max ? clean : clean[..max].TrimEnd() + "…";
    }

    [GeneratedRegex(@"%\d+\$[sd]|%s|%d|\{\d+\}")]
    private static partial Regex PlaceholderRegex();

    private static string Hash(string value)
    {
        var bytes = System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes)[..16];
    }

    private sealed class CachedPage
    {
        public List<SearchResult> Items { get; set; } = [];

        public int TotalCount { get; set; }
    }

    private sealed class CachedCount
    {
        public int Value { get; set; }
    }
}
