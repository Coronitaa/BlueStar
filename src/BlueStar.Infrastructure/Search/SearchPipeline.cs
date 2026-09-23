using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BlueStar.Core.Helpers;
using BlueStar.Core.Interfaces;
using BlueStar.Core.Models;
using BlueStar.Infrastructure.Steam;
using Microsoft.Extensions.Logging;

namespace BlueStar.Infrastructure.Search;

/// <summary>
/// Layered search pipeline executing ParseQuery → ResolveIntent → LocalCandidateSearch → Filter → Sort → Page → LiveEnrichment.
/// Prioritizes the local SQLite FTS5 catalog to eliminate unnecessary requests to Steam.
/// </summary>
public sealed class SearchPipeline : ISearchPipeline
{
    private readonly ILocalCatalogRepository _localRepo;
    private readonly ISteamCatalogSearchService _steamSearchService;
    private readonly IMetadataProvider? _metadataProvider;
    private readonly SteamResponseValidator _validator;
    private readonly ILogger<SearchPipeline>? _logger;

    public SearchPipeline(
        ILocalCatalogRepository localRepo,
        ISteamCatalogSearchService steamSearchService,
        SteamResponseValidator? validator = null,
        ILogger<SearchPipeline>? logger = null,
        IMetadataProvider? metadataProvider = null)
    {
        _localRepo = localRepo ?? throw new ArgumentNullException(nameof(localRepo));
        _steamSearchService = steamSearchService ?? throw new ArgumentNullException(nameof(steamSearchService));
        _validator = validator ?? new SteamResponseValidator();
        _logger = logger;
        _metadataProvider = metadataProvider;
    }

    public SearchPipeline(
        ILocalCatalogRepository localRepo,
        ISteamCatalogSearchService steamSearchService,
        IMetadataProvider? metadataProvider,
        SteamResponseValidator? validator = null,
        ILogger<SearchPipeline>? logger = null)
        : this(localRepo, steamSearchService, validator, logger, metadataProvider)
    {
    }

    /// <inheritdoc />
    public async Task<SearchResponse> ExecuteAsync(SearchRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Stage 1: Parse Query
        var parsed = SteamQueryParser.Parse(request.RawQuery);

        // Stage 2: Intent Resolution - AppID or DepotID Mode
        if (parsed.AppId.HasValue)
        {
            return await ResolveAppIdIntentAsync(parsed.AppId.Value, request, ct).ConfigureAwait(false);
        }
        if (parsed.DepotId.HasValue)
        {
            return await ResolveDepotIdIntentAsync(parsed.DepotId.Value, request, ct).ConfigureAwait(false);
        }

        // Normalize Sort and Direction
        var (sortBase, descending) = NormalizeSort(request.SortBy, request.Descending);

        // Stage 3: Store Browsing (No keyword query entered)
        // When browsing without an active search term, query the store according to the selected pool and filters.
        if (string.IsNullOrWhiteSpace(parsed.NormalizedTerm))
        {
            return await ExecuteStoreBrowseAsync(request, sortBase, descending, ct).ConfigureAwait(false);
        }

        // Stage 4: Local Candidate Search via SQLite + FTS5
        var localQuery = new LocalCatalogQuery
        {
            Term = parsed.NormalizedTerm,
            AppTypes = request.AppTypes,
            SortBy = sortBase,
            Descending = descending,
            HasWindows = request.HasWindows,
            HasMac = request.HasMac,
            HasLinux = request.HasLinux,
            MinRatingPercent = request.MinRatingPercent,
            MaxRatingPercent = request.MaxRatingPercent,
            NoDrm = request.NoDrm,
            NoExternalLauncher = request.NoExternalLauncher,
            HideAdult = request.HideAdult,
            DiscountedOnly = request.DiscountedOnly,
            IncludedTagIds = request.IncludedTagIds?.ToList(),
            ExcludedTagIds = request.ExcludedTagIds?.ToList(),
            RestrictToAppIds = request.RestrictToAppIds?.ToList(),
            Offset = request.Start,
            Limit = request.Count
        };

        var (localItems, totalCount) = await _localRepo.QueryAsync(localQuery, ct).ConfigureAwait(false);

        if (totalCount > 0)
        {
            // Determine resolution type
            var resType = SearchResolutionType.FullText;
            if (!string.IsNullOrWhiteSpace(parsed.NormalizedTerm) && localItems.Count > 0)
            {
                var top = localItems[0];
                var termCompact = DeterministicNormalizer.ToCompactKey(parsed.NormalizedTerm);

                if (string.Equals(top.NormalizedName, parsed.NormalizedTerm, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(top.CompactName, termCompact, StringComparison.OrdinalIgnoreCase))
                {
                    resType = SearchResolutionType.ExactName;
                }
                else if (top.NormalizedName.StartsWith(parsed.NormalizedTerm, StringComparison.OrdinalIgnoreCase))
                {
                    resType = SearchResolutionType.Prefix;
                }
            }

            var results = localItems.Select(ToSearchResult).ToList();

            return new SearchResponse
            {
                Items = results,
                TotalCount = totalCount,
                Start = request.Start,
                ResolutionType = resType,
                IsFromLocalCatalog = true
            };
        }

        // Stage 5: Fallback to Steam live store search
        return await ExecuteSteamFallbackSearchAsync(request, parsed.NormalizedTerm, sortBase, descending, ct).ConfigureAwait(false);
    }

    private async Task<SearchResponse> ResolveAppIdIntentAsync(uint appId, SearchRequest request, CancellationToken ct)
    {
        // 1. Try local catalog first
        var localItem = await _localRepo.GetByAppIdAsync(appId, ct).ConfigureAwait(false);
        if (localItem != null)
        {
            var res = ToSearchResult(localItem);
            if (MatchesRequestFilters(res, request))
            {
                return new SearchResponse
                {
                    Items = [res],
                    TotalCount = 1,
                    Start = 0,
                    ResolutionType = SearchResolutionType.ExactAppId,
                    IsFromLocalCatalog = true
                };
            }

            return new SearchResponse
            {
                Items = [],
                TotalCount = 0,
                Start = 0,
                ResolutionType = SearchResolutionType.ExactAppId,
                IsFromLocalCatalog = true
            };
        }

        // 2. Fetch metadata from official Steam Store API via IMetadataProvider or direct HTTP
        GameMetadata? meta = null;
        if (_metadataProvider != null)
        {
            try
            {
                meta = await _metadataProvider.GetMetadataAsync(appId, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "Failed to fetch metadata from IMetadataProvider for AppId {AppId}", appId);
            }
        }

        if (meta != null)
        {
            var item = CreateSearchResultFromMeta(meta);
            var catItem = CreateCatalogItemFromMeta(meta);

            _ = Task.Run(async () =>
            {
                try
                {
                    await _localRepo.UpsertAppsAsync([catItem], CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "Failed to cache AppId {AppId} into local catalog", appId);
                }
            }, CancellationToken.None);

            if (MatchesRequestFilters(item, request))
            {
                return new SearchResponse
                {
                    Items = [item],
                    TotalCount = 1,
                    Start = 0,
                    ResolutionType = SearchResolutionType.ExactAppId,
                    IsFromLocalCatalog = false
                };
            }

            return new SearchResponse
            {
                Items = [],
                TotalCount = 0,
                Start = 0,
                ResolutionType = SearchResolutionType.ExactAppId,
                IsFromLocalCatalog = false
            };
        }

        // 3. If AppID lookup yielded nothing, check if this numeric ID might be a Depot ID
        var depotFallback = await ResolveDepotIdIntentAsync(appId, request, ct).ConfigureAwait(false);
        if (depotFallback.TotalCount > 0)
        {
            return depotFallback;
        }

        // 4. Final fallback to Steam catalog search service
        var steamQuery = new SteamSearchQuery
        {
            Term = appId.ToString(CultureInfo.InvariantCulture),
            RestrictToAppIds = [appId],
            Count = 1
        };

        try
        {
            var page = await _steamSearchService.SearchAsync(steamQuery, ct).ConfigureAwait(false);
            var found = page.Items.FirstOrDefault(i => i.AppId == appId);
            if (found != null)
            {
                _ = Task.Run(async () =>
                {
                    try { await _localRepo.UpsertAppsAsync([ToCatalogItem(found)], CancellationToken.None).ConfigureAwait(false); } catch { }
                }, CancellationToken.None);

                return new SearchResponse
                {
                    Items = [found],
                    TotalCount = 1,
                    Start = 0,
                    ResolutionType = SearchResolutionType.ExactAppId,
                    IsFromLocalCatalog = false
                };
            }
        }
        catch { }

        return SearchResponse.Empty;
    }

    private async Task<SearchResponse> ResolveDepotIdIntentAsync(uint depotId, SearchRequest request, CancellationToken ct)
    {
        if (depotId == 0) return SearchResponse.Empty;

        // Common Steam depot mappings:
        // 1. depotId - 1 (e.g. 731 -> 730, 401 -> 400, 481 -> 480)
        // 2. depotId - (depotId % 10) (e.g. 735 -> 730, 271591 -> 271590)
        // 3. depotId - (depotId % 100) (e.g. 1091501 -> 1091500)
        var candidates = new HashSet<uint>();
        if (depotId > 1) candidates.Add(depotId - 1);
        var base10 = depotId - (depotId % 10);
        if (base10 > 0 && base10 != depotId) candidates.Add(base10);
        var base100 = depotId - (depotId % 100);
        if (base100 > 0 && base100 != depotId) candidates.Add(base100);

        foreach (var candidateId in candidates)
        {
            // Try local catalog first
            var localItem = await _localRepo.GetByAppIdAsync(candidateId, ct).ConfigureAwait(false);
            if (localItem != null)
            {
                var res = ToSearchResult(localItem);
                if (MatchesRequestFilters(res, request))
                {
                    return new SearchResponse
                    {
                        Items = [res],
                        TotalCount = 1,
                        Start = 0,
                        ResolutionType = SearchResolutionType.ExactAppId,
                        IsFromLocalCatalog = true
                    };
                }
            }

            // Try metadata
            GameMetadata? meta = null;
            if (_metadataProvider != null)
            {
                try { meta = await _metadataProvider.GetMetadataAsync(candidateId, ct).ConfigureAwait(false); } catch { }
            }

            if (meta != null)
            {
                var item = CreateSearchResultFromMeta(meta);
                var catItem = CreateCatalogItemFromMeta(meta);

                _ = Task.Run(async () =>
                {
                    try { await _localRepo.UpsertAppsAsync([catItem], CancellationToken.None).ConfigureAwait(false); } catch { }
                }, CancellationToken.None);

                if (MatchesRequestFilters(item, request))
                {
                    return new SearchResponse
                    {
                        Items = [item],
                        TotalCount = 1,
                        Start = 0,
                        ResolutionType = SearchResolutionType.ExactAppId,
                        IsFromLocalCatalog = false
                    };
                }
            }
        }

        return SearchResponse.Empty;
    }

    private static SearchResult CreateSearchResultFromMeta(GameMetadata meta)
    {
        bool hasWin = meta.Platforms.Count == 0 || meta.Platforms.Any(p => p.Contains("win", StringComparison.OrdinalIgnoreCase));
        bool hasMac = meta.Platforms.Any(p => p.Contains("mac", StringComparison.OrdinalIgnoreCase) || p.Contains("osx", StringComparison.OrdinalIgnoreCase));
        bool hasLin = meta.Platforms.Any(p => p.Contains("lin", StringComparison.OrdinalIgnoreCase) || p.Contains("steamos", StringComparison.OrdinalIgnoreCase));

        return new SearchResult
        {
            AppId = meta.AppId,
            Name = meta.Name,
            AppType = "Game",
            HeaderImageUrl = meta.HeaderImageUrl ?? $"https://shared.fastly.steamstatic.com/store_item_assets/steam/apps/{meta.AppId}/header.jpg",
            ReleaseDateText = meta.ReleaseDate,
            HasWindows = hasWin,
            HasMac = hasMac,
            HasLinux = hasLin,
            IsAvailable = true
        };
    }

    private static CatalogAppItem CreateCatalogItemFromMeta(GameMetadata meta)
    {
        bool hasWin = meta.Platforms.Count == 0 || meta.Platforms.Any(p => p.Contains("win", StringComparison.OrdinalIgnoreCase));
        bool hasMac = meta.Platforms.Any(p => p.Contains("mac", StringComparison.OrdinalIgnoreCase) || p.Contains("osx", StringComparison.OrdinalIgnoreCase));
        bool hasLin = meta.Platforms.Any(p => p.Contains("lin", StringComparison.OrdinalIgnoreCase) || p.Contains("steamos", StringComparison.OrdinalIgnoreCase));

        return new CatalogAppItem
        {
            AppId = meta.AppId,
            Name = meta.Name,
            NormalizedName = DeterministicNormalizer.Normalize(meta.Name),
            CompactName = DeterministicNormalizer.ToCompactKey(meta.Name),
            HeaderImageUrl = meta.HeaderImageUrl,
            ReleaseDateText = meta.ReleaseDate,
            HasWindows = hasWin,
            HasMac = hasMac,
            HasLinux = hasLin,
            LastModified = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        };
    }

    private async Task<SearchResponse> ExecuteStoreBrowseAsync(
        SearchRequest request, string sortBase, bool descending, CancellationToken ct)
    {
        var isCuratedPool = !string.IsNullOrWhiteSpace(request.Pool) &&
                            !string.Equals(request.Pool, "all", StringComparison.OrdinalIgnoreCase);

        // Curated pools (popularnew, globaltopsellers, comingsoon) have their own intrinsic Steam ranking.
        // Sending sort_by to Steam on curated lists breaks them (e.g. popularnew returns 2006-2017 games, or comingsoon breaks).
        // Therefore, for curated lists we fetch the natural pool and apply any user-selected sort locally.
        // For general catalog browsing (request.Pool == "all" or null), we pass the sort token directly to Steam.
        string steamSort = string.Empty;
        if (!isCuratedPool && !string.IsNullOrEmpty(sortBase))
        {
            steamSort = SteamStoreFacets.ComposeSort(sortBase, descending);
        }

        var steamQuery = BuildSteamQuery(request, null, steamSort);

        try
        {
            var page = await _steamSearchService.SearchAsync(steamQuery, ct).ConfigureAwait(false);

            // Anomaly validation
            var validation = _validator.ValidateResponse(steamQuery, "{\"results_html\":\"ok\"}", page.Items, page.TotalCount);
            IReadOnlyList<SearchResult> items = page.Items.Where(i => MatchesRequestFilters(i, request)).ToList();

            // When browsing "popularnew" (Popular new releases), filter out legacy releases
            // so games released years ago (e.g. Brawlhalla from 2017, Warframe from 2013) are never presented as "new releases".
            if (string.Equals(request.Pool, "popularnew", StringComparison.OrdinalIgnoreCase))
            {
                var cutoff = DateTime.UtcNow.AddDays(-365);
                items = items.Where(i =>
                {
                    var dt = ParseReleaseDate(i.ReleaseDateText);
                    if (dt == DateTime.MinValue) return true; // keep upcoming/unreleased items
                    return dt >= cutoff;
                }).ToList();
            }

            if (!string.IsNullOrEmpty(sortBase))
            {
                items = ApplyLocalSort(items, sortBase, descending);
            }

            // Background caching
            if (items.Count > 0)
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await _localRepo.UpsertAppsAsync(items.Select(ToCatalogItem), CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogDebug(ex, "Background upsert of browse items failed");
                    }
                }, CancellationToken.None);
            }

            if (items.Count > 0 || page.TotalCount > 0)
            {
                return new SearchResponse
                {
                    Items = items,
                    TotalCount = page.TotalCount,
                    Start = request.Start,
                    ResolutionType = SearchResolutionType.FallbackSteam,
                    IsFromLocalCatalog = false,
                    AnomalyDetected = validation.Anomaly != SteamAnomalyType.None ? validation.Description : null
                };
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Steam store browse request failed; attempting fallback to local catalog");
        }

        // Fallback to local SQLite catalog if Steam is offline or returned empty
        var localQuery = new LocalCatalogQuery
        {
            Term = null,
            AppTypes = request.AppTypes,
            SortBy = sortBase,
            Descending = descending,
            HasWindows = request.HasWindows,
            HasMac = request.HasMac,
            HasLinux = request.HasLinux,
            MinRatingPercent = request.MinRatingPercent,
            MaxRatingPercent = request.MaxRatingPercent,
            NoDrm = request.NoDrm,
            NoExternalLauncher = request.NoExternalLauncher,
            HideAdult = request.HideAdult,
            DiscountedOnly = request.DiscountedOnly,
            IncludedTagIds = request.IncludedTagIds?.ToList(),
            ExcludedTagIds = request.ExcludedTagIds?.ToList(),
            RestrictToAppIds = request.RestrictToAppIds?.ToList(),
            Offset = request.Start,
            Limit = request.Count
        };

        var (localFallbackItems, totalFallbackCount) = await _localRepo.QueryAsync(localQuery, ct).ConfigureAwait(false);
        return new SearchResponse
        {
            Items = localFallbackItems.Select(ToSearchResult).ToList(),
            TotalCount = totalFallbackCount,
            Start = request.Start,
            ResolutionType = SearchResolutionType.FullText,
            IsFromLocalCatalog = true
        };
    }

    private async Task<SearchResponse> ExecuteSteamFallbackSearchAsync(
        SearchRequest request, string? term, string sortBase, bool descending, CancellationToken ct)
    {
        var steamSort = SteamStoreFacets.ComposeSort(sortBase, descending);
        var steamQuery = BuildSteamQuery(request, term, steamSort);

        try
        {
            var page = await _steamSearchService.SearchAsync(steamQuery, ct).ConfigureAwait(false);
            var validation = _validator.ValidateResponse(steamQuery, "{\"results_html\":\"ok\"}", page.Items, page.TotalCount);
            var items = page.Items.Where(i => MatchesRequestFilters(i, request)).ToList();

            if (!string.IsNullOrEmpty(sortBase))
            {
                items = ApplyLocalSort(items, sortBase, descending).ToList();
            }

            // Cache discovered games in local SQLite catalog
            if (items.Count > 0)
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await _localRepo.UpsertAppsAsync(items.Select(ToCatalogItem), CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogDebug(ex, "Background upsert of Steam search results failed");
                    }
                }, CancellationToken.None);
            }

            return new SearchResponse
            {
                Items = items,
                TotalCount = page.TotalCount,
                Start = request.Start,
                ResolutionType = SearchResolutionType.FallbackSteam,
                IsFromLocalCatalog = false,
                AnomalyDetected = validation.Anomaly != SteamAnomalyType.None ? validation.Description : null
            };
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Steam fallback search failed for term: {Term}", term);
            return SearchResponse.Empty;
        }
    }

    private static (string SortBase, bool Descending) NormalizeSort(string? sortBy, bool descending)
    {
        var s = (sortBy ?? string.Empty).Trim();
        if (s.EndsWith("_DESC", StringComparison.OrdinalIgnoreCase))
        {
            return (s[..^5], true);
        }
        if (s.EndsWith("_ASC", StringComparison.OrdinalIgnoreCase))
        {
            return (s[..^4], false);
        }
        return (s, descending);
    }

    private static SteamSearchQuery BuildSteamQuery(SearchRequest request, string? term, string sortBy)
    {
        var facets = new Dictionary<SteamFacetOption, FacetState>();

        if (request.IncludedTagIds != null)
        {
            foreach (var tagId in request.IncludedTagIds)
            {
                facets[new SteamFacetOption(SteamFacetKind.Tag, tagId.ToString(CultureInfo.InvariantCulture), "")] = FacetState.Include;
            }
        }

        if (request.ExcludedTagIds != null)
        {
            foreach (var tagId in request.ExcludedTagIds)
            {
                facets[new SteamFacetOption(SteamFacetKind.Tag, tagId.ToString(CultureInfo.InvariantCulture), "")] = FacetState.Exclude;
            }
        }

        if (request.HasWindows == true)
            facets[new SteamFacetOption(SteamFacetKind.Os, "win", "Windows")] = FacetState.Include;
        if (request.HasMac == true)
            facets[new SteamFacetOption(SteamFacetKind.Os, "mac", "macOS")] = FacetState.Include;
        if (request.HasLinux == true)
            facets[new SteamFacetOption(SteamFacetKind.Os, "linux", "Linux")] = FacetState.Include;
        if (request.DiscountedOnly == true)
            facets[new SteamFacetOption(SteamFacetKind.Toggle, "specials", "Specials")] = FacetState.Include;

        var storeList = string.Equals(request.Pool, "all", StringComparison.OrdinalIgnoreCase) ? null : request.Pool;

        return new SteamSearchQuery
        {
            Term = term,
            AppTypes = request.AppTypes,
            SortBy = sortBy,
            StoreList = storeList,
            Start = request.Start,
            Count = request.Count,
            Facets = facets,
            RestrictToAppIds = request.RestrictToAppIds
        };
    }

    private static DateTime ParseReleaseDate(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return DateTime.MinValue;
        var trimmed = text.Trim();
        if (DateTime.TryParse(trimmed, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt)) return dt;
        if (DateTime.TryParse(trimmed, CultureInfo.CurrentCulture, DateTimeStyles.None, out dt)) return dt;
        if (DateTime.TryParse(trimmed, CultureInfo.GetCultureInfo("en-US"), DateTimeStyles.None, out dt)) return dt;

        var match = System.Text.RegularExpressions.Regex.Match(trimmed, @"\b(19\d\d|20\d\d)\b");
        if (match.Success && int.TryParse(match.Value, out var year))
        {
            return new DateTime(year, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        }

        return DateTime.MinValue;
    }

    private static decimal ParsePrice(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return decimal.MaxValue;
        var lower = text.ToLowerInvariant();
        if (lower.Contains("free") || lower.Contains("gratis") || lower == "$0" || lower == "$0.00" || lower == "0€") return 0m;

        var sb = new System.Text.StringBuilder();
        foreach (var ch in text)
        {
            if (char.IsDigit(ch) || ch == '.' || ch == ',') sb.Append(ch);
        }
        var cleaned = sb.ToString();
        if (string.IsNullOrWhiteSpace(cleaned)) return decimal.MaxValue;

        if (cleaned.Contains(',') && cleaned.Contains('.'))
        {
            if (cleaned.IndexOf(',') < cleaned.IndexOf('.')) cleaned = cleaned.Replace(",", "");
            else cleaned = cleaned.Replace(".", "").Replace(',', '.');
        }
        else if (cleaned.Contains(','))
        {
            cleaned = cleaned.Replace(',', '.');
        }

        return decimal.TryParse(cleaned, NumberStyles.Any, CultureInfo.InvariantCulture, out var price) ? price : decimal.MaxValue;
    }

    private static IReadOnlyList<SearchResult> ApplyLocalSort(IReadOnlyList<SearchResult> items, string sortBase, bool descending)
    {
        var key = sortBase.ToLowerInvariant();
        return key switch
        {
            "name" => descending
                ? items.OrderByDescending(i => i.Name, StringComparer.CurrentCultureIgnoreCase).ToList()
                : items.OrderBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase).ToList(),
            "reviews" => descending
                ? items.OrderByDescending(i => i.ReviewPercent ?? 0).ThenByDescending(i => i.MatchedTagCount).ToList()
                : items.OrderBy(i => i.ReviewPercent ?? 100).ThenBy(i => i.MatchedTagCount).ToList(),
            "appid" => descending
                ? items.OrderByDescending(i => i.AppId).ToList()
                : items.OrderBy(i => i.AppId).ToList(),
            "price" => descending
                ? items.OrderByDescending(i => ParsePrice(i.PriceText)).ThenByDescending(i => ParseReleaseDate(i.ReleaseDateText)).ToList()
                : items.OrderBy(i => ParsePrice(i.PriceText)).ThenByDescending(i => ParseReleaseDate(i.ReleaseDateText)).ToList(),
            "discount" => descending
                ? items.OrderByDescending(i => i.DiscountPercent).ToList()
                : items.OrderBy(i => i.DiscountPercent).ToList(),
            "released" => descending
                ? items.OrderByDescending(i => ParseReleaseDate(i.ReleaseDateText)).ToList()
                : items.OrderBy(i => ParseReleaseDate(i.ReleaseDateText)).ToList(),
            "deckcompatdate" or "deck" => descending
                ? items.OrderByDescending(i => i.DeckCompatibility ?? string.Empty).ToList()
                : items.OrderBy(i => i.DeckCompatibility ?? string.Empty).ToList(),
            _ => items
        };
    }

    private static bool MatchesRequestFilters(SearchResult res, SearchRequest req)
    {
        if (req.HasWindows == true && !res.HasWindows) return false;
        if (req.HasMac == true && !res.HasMac) return false;
        if (req.HasLinux == true && !res.HasLinux) return false;
        if (req.NoDrm == true && res.HasDrm) return false;
        if (req.NoExternalLauncher == true && res.HasExternalLauncher) return false;
        if (req.HideAdult == true && res.IsNsfw) return false;
        if (req.DiscountedOnly == true && res.DiscountPercent <= 0) return false;
        if (req.MinRatingPercent.HasValue && (res.ReviewPercent ?? 0) < req.MinRatingPercent.Value) return false;
        if (req.MaxRatingPercent.HasValue && (res.ReviewPercent ?? 100) >= req.MaxRatingPercent.Value) return false;

        if (!string.IsNullOrWhiteSpace(req.AppTypes))
        {
            var types = req.AppTypes.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var hasGames = types.Contains("998");
            var hasSoftware = types.Contains("994");
            var isSoftware = string.Equals(res.AppType, "software", StringComparison.OrdinalIgnoreCase)
                             || string.Equals(res.AppType, "application", StringComparison.OrdinalIgnoreCase)
                             || string.Equals(res.AppType, "tool", StringComparison.OrdinalIgnoreCase)
                             || string.Equals(res.AppType, "utility", StringComparison.OrdinalIgnoreCase);

            if (hasGames && !hasSoftware && isSoftware) return false;
            if (hasSoftware && !hasGames && !isSoftware) return false;
        }

        return true;
    }

    public static SearchResult ToSearchResult(CatalogAppItem item)
    {
        return new SearchResult
        {
            AppId = item.AppId,
            Name = item.Name,
            AppType = item.AppType,
            HeaderImageUrl = item.HeaderImageUrl ?? $"https://shared.fastly.steamstatic.com/store_item_assets/steam/apps/{item.AppId}/header.jpg",
            HasWindows = item.HasWindows,
            HasMac = item.HasMac,
            HasLinux = item.HasLinux,
            IsNsfw = item.IsNsfw,
            HasDrm = item.HasDrm,
            HasExternalLauncher = item.HasExternalLauncher,
            ReviewPercent = item.ReviewPercent,
            ReviewSummary = item.ReviewPercent.HasValue
                ? RatingEngine.GetReviewSummary(item.ReviewPercent.Value, item.ReviewCount ?? 100)
                : null,
            PriceText = item.PriceText,
            DiscountPercent = item.DiscountPercent,
            ReleaseDateText = item.ReleaseDateText,
            TagIds = item.TagIds,
            IsEnriched = item.HasDrm || item.HasExternalLauncher || item.ReviewPercent.HasValue
        };
    }

    public static CatalogAppItem ToCatalogItem(SearchResult result)
    {
        var normalized = DeterministicNormalizer.Normalize(result.Name);
        var compact = DeterministicNormalizer.ToCompactKey(result.Name);

        return new CatalogAppItem
        {
            AppId = result.AppId,
            Name = result.Name,
            NormalizedName = normalized,
            CompactName = compact,
            AppType = result.AppType ?? "game",
            HeaderImageUrl = result.HeaderImageUrl,
            HasWindows = result.HasWindows,
            HasMac = result.HasMac,
            HasLinux = result.HasLinux,
            IsNsfw = result.IsNsfw,
            HasDrm = result.HasDrm,
            HasExternalLauncher = result.HasExternalLauncher,
            ReviewPercent = result.ReviewPercent,
            PriceText = result.PriceText,
            DiscountPercent = result.DiscountPercent,
            ReleaseDateText = result.ReleaseDateText,
            TagIds = result.TagIds
        };
    }
}
