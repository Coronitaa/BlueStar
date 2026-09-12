using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using BlueStar.Core.Interfaces;
using BlueStar.Core.Models;
using Microsoft.Extensions.Logging;

namespace BlueStar.Infrastructure.Steam;

/// <summary>
/// The Steam store tag catalog, grouped thematically for the Explore filter panel.
/// </summary>
/// <remarks>
/// Steam publishes its tag list at <c>/tagdata/populartags/{language}</c>. The list is fetched
/// twice — once in the interface language for display and once in English so the group
/// definitions, which are written in English, can be matched by name. Both are cached for a
/// month; tag ids effectively never change.
/// </remarks>
public sealed class SteamTagCatalogService : ISteamTagCatalogService
{
    private const string TagDataUrl = "https://store.steampowered.com/tagdata/populartags/";
    private static readonly TimeSpan CatalogTtl = TimeSpan.FromDays(30);

    private readonly HttpClient _http;
    private readonly ISteamCatalogSearchService _search;
    private readonly ICacheService? _cache;
    private readonly ILogger<SteamTagCatalogService> _logger;
    private readonly string _language;

    private readonly SemaphoreSlim _catalogLock = new(1, 1);
    private readonly ConcurrentDictionary<string, IReadOnlyList<SteamTag>> _groups = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyDictionary<int, SteamTag>? _catalog;

    /// <summary>
    /// Initializes a new instance of the <see cref="SteamTagCatalogService"/> class.
    /// </summary>
    /// <param name="http">HTTP client used for the tag data requests.</param>
    /// <param name="search">Search service, used to count how many products carry a tag.</param>
    /// <param name="logger">Logger instance.</param>
    /// <param name="cache">Optional persistent cache.</param>
    /// <param name="language">Steam language token for display names.</param>
    public SteamTagCatalogService(
        HttpClient http,
        ISteamCatalogSearchService search,
        ILogger<SteamTagCatalogService> logger,
        ICacheService? cache = null,
        string language = "english")
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _search = search ?? throw new ArgumentNullException(nameof(search));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _cache = cache;
        _language = string.IsNullOrWhiteSpace(language) ? "english" : language;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<int, SteamTag>> GetCatalogAsync(CancellationToken ct = default)
    {
        if (_catalog != null) return _catalog;

        await _catalogLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_catalog != null) return _catalog;

            var localized = await FetchTagListAsync(_language, ct).ConfigureAwait(false);
            var english = _language.Equals("english", StringComparison.OrdinalIgnoreCase)
                ? localized
                : await FetchTagListAsync("english", ct).ConfigureAwait(false);

            var byId = new Dictionary<int, SteamTag>();
            foreach (var (id, canonical) in english)
            {
                byId[id] = new SteamTag
                {
                    TagId = id,
                    CanonicalName = canonical,
                    Name = localized.TryGetValue(id, out var display) && !string.IsNullOrWhiteSpace(display)
                        ? display
                        : canonical
                };
            }

            // A localized entry with no English twin still deserves to be selectable.
            foreach (var (id, display) in localized)
            {
                if (byId.ContainsKey(id)) continue;
                byId[id] = new SteamTag { TagId = id, CanonicalName = display, Name = display };
            }

            _catalog = byId;
            return _catalog;
        }
        finally
        {
            _catalogLock.Release();
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SteamTag>> GetGroupAsync(string groupKey, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(groupKey)) return [];
        if (_groups.TryGetValue(groupKey, out var cached)) return cached;

        var catalog = await GetCatalogAsync(ct).ConfigureAwait(false);
        if (catalog.Count == 0) return [];

        var byCanonical = new Dictionary<string, SteamTag>(StringComparer.OrdinalIgnoreCase);
        foreach (var tag in catalog.Values)
        {
            byCanonical.TryAdd(tag.CanonicalName, tag);
        }

        IReadOnlyList<SteamTag> result;

        if (groupKey.Equals(SteamTagGroups.OtherGroupKey, StringComparison.OrdinalIgnoreCase))
        {
            var claimed = SteamTagGroups.All
                .SelectMany(g => g.TagNames)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            result = catalog.Values
                .Where(t => !claimed.Contains(t.CanonicalName))
                .OrderBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }
        else
        {
            var definition = SteamTagGroups.All
                .FirstOrDefault(g => g.Key.Equals(groupKey, StringComparison.OrdinalIgnoreCase));

            if (definition is null) return [];

            var ordered = new List<SteamTag>(definition.TagNames.Count);
            foreach (var name in definition.TagNames)
            {
                if (byCanonical.TryGetValue(name, out var tag) && !ordered.Contains(tag))
                {
                    ordered.Add(tag);
                }
            }

            result = ordered;
        }

        _groups[groupKey] = result;
        return result;
    }

    /// <inheritdoc />
    public async Task ResolveCountsAsync(IEnumerable<SteamTag> tags, CancellationToken ct = default)
    {
        if (tags is null) return;

        var pending = tags.Where(t => t is { ProductCount: null }).ToList();
        if (pending.Count == 0) return;

        // Two at a time: enough to feel instant, gentle enough not to trip Steam's rate limit.
        await Parallel.ForEachAsync(
            pending,
            new ParallelOptions { MaxDegreeOfParallelism = 2, CancellationToken = ct },
            async (tag, token) =>
            {
                try
                {
                    var probe = new SteamSearchQuery
                    {
                        Count = 1,
                        Language = _language,
                        Facets = new Dictionary<SteamFacetOption, FacetState>
                        {
                            [tag.ToFacet()] = FacetState.Include
                        }
                    };

                    var count = await _search.GetMatchCountAsync(probe, token).ConfigureAwait(false);
                    if (count.HasValue) tag.ProductCount = count.Value;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Could not count products for tag {TagId}", tag.TagId);
                }
            }).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<SteamTag?> GetRandomAsync(
        string groupKey, IReadOnlyCollection<int> excludeTagIds, CancellationToken ct = default)
    {
        var group = await GetGroupAsync(groupKey, ct).ConfigureAwait(false);
        if (group.Count == 0) return null;

        var pool = excludeTagIds is { Count: > 0 }
            ? group.Where(t => !excludeTagIds.Contains(t.TagId)).ToList()
            : group.ToList();

        if (pool.Count == 0) return null;

        return pool[Random.Shared.Next(pool.Count)];
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> ResolveNamesAsync(IEnumerable<int> tagIds, CancellationToken ct = default)
    {
        if (tagIds is null) return [];

        var catalog = await GetCatalogAsync(ct).ConfigureAwait(false);
        if (catalog.Count == 0) return [];

        var names = new List<string>();
        foreach (var id in tagIds)
        {
            if (catalog.TryGetValue(id, out var tag) && !string.IsNullOrWhiteSpace(tag.Name))
            {
                names.Add(tag.Name);
            }
        }

        return names;
    }

    private async Task<Dictionary<int, string>> FetchTagListAsync(string language, CancellationToken ct)
    {
        var cacheKey = $"steam_tagdata_{language}_v1";

        if (_cache != null)
        {
            try
            {
                var cached = await _cache.GetAsync<Dictionary<int, string>>(cacheKey, ct).ConfigureAwait(false);
                if (cached is { Count: > 0 }) return cached;
            }
            catch
            {
                // Ignored.
            }
        }

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(15));

            var entries = await _http.GetFromJsonAsync<List<TagDataEntry>>(
                TagDataUrl + Uri.EscapeDataString(language), cts.Token).ConfigureAwait(false);

            var map = new Dictionary<int, string>();
            foreach (var entry in entries ?? [])
            {
                if (entry.TagId <= 0 || string.IsNullOrWhiteSpace(entry.Name)) continue;
                map[entry.TagId] = entry.Name.Trim();
            }

            if (_cache != null && map.Count > 0)
            {
                try
                {
                    await _cache.SetAsync(cacheKey, map, CatalogTtl, ct).ConfigureAwait(false);
                }
                catch
                {
                    // Ignored.
                }
            }

            return map;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            _logger.LogWarning(ex, "Could not download the Steam tag catalog for {Language}", language);
            return [];
        }
    }

    private sealed class TagDataEntry
    {
        [JsonPropertyName("tagid")]
        public int TagId { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }
    }
}
