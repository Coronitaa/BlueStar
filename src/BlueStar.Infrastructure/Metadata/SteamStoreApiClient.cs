using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json;
using BlueStar.Core.Interfaces;
using BlueStar.Core.Models;
using BlueStar.Infrastructure.Services;
using Microsoft.Extensions.Logging;

namespace BlueStar.Infrastructure.Metadata;

/// <summary>
/// Retrieves game and DLC metadata from the public Steam Store API.
/// </summary>
public sealed class SteamStoreApiClient : IMetadataProvider
{
    private readonly HttpClient _http;
    private readonly ILogger<SteamStoreApiClient> _logger;
    private readonly ICacheService? _cache;
    private readonly IRequestCoordinator _coordinator;
    private readonly INetworkMetricsObserver? _metrics;

    private static readonly SemaphoreSlim _throttleSemaphore = new(1, 1);
    private static DateTimeOffset _lastRequest = DateTimeOffset.MinValue;
    private static DateTimeOffset _cooldownUntil = DateTimeOffset.MinValue;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    // Steam API rate limit: ~200 requests / 5 minutes ≈ 1 request / 1.5 seconds
    private static readonly TimeSpan MinRequestInterval = TimeSpan.FromMilliseconds(1600);

    /// <summary>
    /// Initializes a new instance of the <see cref="SteamStoreApiClient"/> class.
    /// </summary>
    /// <param name="http">HTTP client configured for Steam Store API.</param>
    /// <param name="logger">Logger instance.</param>
    /// <param name="cache">Optional persistent cache service.</param>
    /// <param name="coordinator">Optional request coordinator for in-flight deduplication.</param>
    /// <param name="metrics">Optional network metrics observer.</param>
    public SteamStoreApiClient(
        HttpClient http,
        ILogger<SteamStoreApiClient> logger,
        ICacheService? cache = null,
        IRequestCoordinator? coordinator = null,
        INetworkMetricsObserver? metrics = null)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _cache = cache;
        _coordinator = coordinator ?? RequestCoordinator.Instance;
        _metrics = metrics;
    }

    /// <summary>
    /// Waits for this client's turn to talk to Steam.
    /// </summary>
    /// <remarks>
    /// Pacing lives in <see cref="BlueStar.Infrastructure.Steam.SteamRequestGate"/> rather than
    /// here, so that this client and the Explore catalog search share one budget instead of each
    /// staying inside its own limit and together exceeding Steam's.
    /// </remarks>
    private async Task<bool> ThrottleAsync(CancellationToken ct)
    {
        var lease = await BlueStar.Infrastructure.Steam.SteamRequestGate
            .AcquireAsync(BlueStar.Infrastructure.Steam.SteamRequestPriority.Background, ct)
            .ConfigureAwait(false);

        if (lease is null) return false;

        // The lease has already enforced the spacing; the request itself may overlap.
        lease.Dispose();

        _lastRequest = DateTimeOffset.UtcNow;
        return true;
    }

    private void ReportRateLimitEncountered(System.Net.HttpStatusCode statusCode)
    {
        _cooldownUntil = DateTimeOffset.UtcNow.AddMinutes(5);
        BlueStar.Infrastructure.Steam.SteamRequestGate.ReportBlocked(statusCode);
    }

    /// <inheritdoc />
    public async Task<GameMetadata?> GetMetadataAsync(uint appId, CancellationToken ct = default)
    {
        if (appId == 0) return null;

        var cacheKey = $"steam_meta_{appId}_v3";
        if (_cache != null)
        {
            try
            {
                var cached = await _cache.GetAsync<GameMetadata>(cacheKey, ct).ConfigureAwait(false);
                if (cached != null)
                {
                    _metrics?.OnCacheHit("SteamStore", $"meta/{appId}");
                    return cached;
                }
            }
            catch { }
        }

        return await _coordinator.ExecuteAsync($"steam_meta_{appId}", innerCt => FetchMetadataCoreAsync(appId, cacheKey, innerCt), ct).ConfigureAwait(false);
    }

    private async Task<GameMetadata?> FetchMetadataCoreAsync(uint appId, string cacheKey, CancellationToken ct)
    {
        if (_cache != null)
        {
            try
            {
                var cached = await _cache.GetAsync<GameMetadata>(cacheKey, ct).ConfigureAwait(false);
                if (cached != null)
                {
                    _metrics?.OnCacheHit("SteamStore", $"meta/{appId}");
                    return cached;
                }
            }
            catch { }
        }

        try
        {
            if (!await ThrottleAsync(ct).ConfigureAwait(false))
                return null;

            _logger.LogDebug("Fetching Steam metadata for AppId={AppId}", appId);
            _metrics?.OnProviderRequest("SteamStore", $"https://store.steampowered.com/api/appdetails?appids={appId}");

            var url = $"https://store.steampowered.com/api/appdetails?appids={appId}&l=english&cc=US";
            using var response = await _http.GetAsync(url, ct).ConfigureAwait(false);

            if (response.StatusCode is System.Net.HttpStatusCode.TooManyRequests or System.Net.HttpStatusCode.Forbidden)
            {
                ReportRateLimitEncountered(response.StatusCode);
                return null;
            }

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Steam API returned {StatusCode} for AppId={AppId}",
                    response.StatusCode, appId);
                return null;
            }

            var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (json.Contains("Access Denied", StringComparison.OrdinalIgnoreCase) ||
                json.Contains("edgesuite.net", StringComparison.OrdinalIgnoreCase))
            {
                ReportRateLimitEncountered(System.Net.HttpStatusCode.Forbidden);
                return null;
            }

            using var doc = JsonDocument.Parse(json);

            var appKey = appId.ToString();
            if (!doc.RootElement.TryGetProperty(appKey, out var appElement))
                return null;

            if (!appElement.TryGetProperty("success", out var successProp) || !successProp.GetBoolean())
                return null;

            if (!appElement.TryGetProperty("data", out var data))
                return null;

            var meta = new GameMetadata
            {
                AppId = appId,
                Name = data.TryGetProperty("name", out var name) ? name.GetString() ?? $"App {appId}" : $"App {appId}",
                Developer = GetFirstArrayString(data, "developers"),
                Publisher = GetFirstArrayString(data, "publishers"),
                Description = data.TryGetProperty("short_description", out var desc) ? desc.GetString() : null,
                HeaderImageUrl = data.TryGetProperty("header_image", out var header) ? header.GetString() : null,
                CapsuleImageUrl = data.TryGetProperty("capsule_image", out var capsule) ? capsule.GetString() : null,
                ReleaseDate = GetReleaseDate(data),
                Categories = GetStringArray(data, "categories", "description"),
                Genres = GetStringArray(data, "genres", "description"),
                AboutTheGame = StripHtml(data.TryGetProperty("about_the_game", out var about) ? about.GetString() : null),
                Screenshots = GetScreenshots(data),
                Website = data.TryGetProperty("website", out var site) ? site.GetString() : null,
                MetacriticScore = GetMetacriticScore(data),
                Platforms = GetPlatforms(data),
                LastUpdated = DateTimeOffset.UtcNow
            };

            if (_cache != null)
            {
                try { await _cache.SetAsync(cacheKey, meta, TimeSpan.FromDays(7), ct).ConfigureAwait(false); } catch { }

                // Pre-populate or negative cache DLC list from same appdetails payload
                var dlcCacheKey = $"steam_dlcs_{appId}_v3";
                var prePopulatedDlcs = new List<DlcInfo>();
                if (data.TryGetProperty("dlc", out var dlcArray) && dlcArray.ValueKind == JsonValueKind.Array)
                {
                    foreach (var dlcElement in dlcArray.EnumerateArray())
                    {
                        if (dlcElement.TryGetUInt32(out var dlcAppId))
                        {
                            prePopulatedDlcs.Add(new DlcInfo
                            {
                                AppId = dlcAppId,
                                Name = $"DLC {dlcAppId}",
                                Depots = [],
                                IsInstalled = false
                            });
                        }
                    }
                }
                try { await _cache.SetAsync(dlcCacheKey, prePopulatedDlcs, TimeSpan.FromDays(7), ct).ConfigureAwait(false); } catch { }
            }

            return meta;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "HTTP error fetching Steam metadata for AppId={AppId}", appId);
            return null;
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "JSON parse error for Steam metadata AppId={AppId}", appId);
            return null;
        }
    }


    /// <inheritdoc />
    public async Task<IReadOnlyList<DlcInfo>> GetDlcListAsync(uint appId, CancellationToken ct = default)
    {
        if (appId == 0) return [];

        var cacheKey = $"steam_dlcs_{appId}_v3";
        if (_cache != null)
        {
            try
            {
                var cached = await _cache.GetAsync<List<DlcInfo>>(cacheKey, ct).ConfigureAwait(false);
                if (cached != null)
                {
                    if (cached.Count == 0)
                    {
                        _metrics?.OnNegativeCacheHit("SteamStore", $"dlcs/{appId}");
                    }
                    else
                    {
                        _metrics?.OnCacheHit("SteamStore", $"dlcs/{appId}");
                    }
                    return cached.AsReadOnly();
                }
            }
            catch { }
        }

        // Delegate to GetMetadataAsync which queries appdetails and pre-populates steam_dlcs_{appId}_v3 under coordinated in-flight lock
        await GetMetadataAsync(appId, ct).ConfigureAwait(false);

        if (_cache != null)
        {
            try
            {
                var cached = await _cache.GetAsync<List<DlcInfo>>(cacheKey, ct).ConfigureAwait(false);
                if (cached != null)
                {
                    return cached.AsReadOnly();
                }
            }
            catch { }
        }

        return [];
    }

    /// <summary>
    /// Cache payload for SearchResult enrichment to avoid repeated store API calls.
    /// </summary>
    public sealed record SearchResultEnrichmentCache(
        string? Name,
        int? DlcCount,
        bool HasWindows,
        bool HasLinux,
        bool HasMac,
        string AppType,
        string? HeaderImageUrl,
        bool IsNsfw,
        bool HasDrm,
        string? DrmNotice,
        string? Version
    );

    /// <inheritdoc />
    public async Task EnrichSearchResultAsync(SearchResult result, CancellationToken ct = default)
    {
        if (result == null || result.AppId == 0) return;

        var cacheKey = $"steam_enrich_{result.AppId}_v3";
        if (_cache != null)
        {
            try
            {
                var cached = await _cache.GetAsync<SearchResultEnrichmentCache>(cacheKey, ct).ConfigureAwait(false);
                if (cached != null)
                {
                    if (!string.IsNullOrWhiteSpace(cached.Name)) result.Name = cached.Name;
                    if (cached.DlcCount.HasValue) result.DlcCount = cached.DlcCount.Value;
                    result.HasWindows = cached.HasWindows;
                    result.HasLinux = cached.HasLinux;
                    result.HasMac = cached.HasMac;
                    if (!string.IsNullOrWhiteSpace(cached.AppType)) result.AppType = cached.AppType;
                    if (!string.IsNullOrWhiteSpace(cached.HeaderImageUrl)) result.HeaderImageUrl = cached.HeaderImageUrl;
                    result.IsNsfw = cached.IsNsfw;
                    result.HasDrm = cached.HasDrm;
                    result.DrmNotice = cached.DrmNotice;
                    if (!string.IsNullOrWhiteSpace(cached.Version)) result.Version = cached.Version;
                    return;
                }
            }
            catch { }
        }

        // Throttle and serialize Steam Store requests
        if (!await ThrottleAsync(ct).ConfigureAwait(false))
            return;

        // 1. Try fetching rich store data via Steam Store API (appdetails)
        try
        {
            var url = $"https://store.steampowered.com/api/appdetails?appids={result.AppId}&l=english&cc=US";
            var response = await _http.GetAsync(url, ct).ConfigureAwait(false);

            if (response.StatusCode is System.Net.HttpStatusCode.TooManyRequests or System.Net.HttpStatusCode.Forbidden)
            {
                ReportRateLimitEncountered(response.StatusCode);
                return;
            }

            if (response.IsSuccessStatusCode)
            {
                var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                if (json.Contains("Access Denied", StringComparison.OrdinalIgnoreCase) ||
                    json.Contains("edgesuite.net", StringComparison.OrdinalIgnoreCase))
                {
                    ReportRateLimitEncountered(System.Net.HttpStatusCode.Forbidden);
                    return;
                }

                using var doc = JsonDocument.Parse(json);

                var appKey = result.AppId.ToString();
                if (doc.RootElement.TryGetProperty(appKey, out var appElement) &&
                    appElement.TryGetProperty("success", out var s) && s.GetBoolean() &&
                    appElement.TryGetProperty("data", out var data))
                {
                    // 1.0 Official Game Name
                    if (data.TryGetProperty("name", out var nameProp) && nameProp.ValueKind == JsonValueKind.String)
                    {
                        var officialName = nameProp.GetString();
                        if (!string.IsNullOrWhiteSpace(officialName))
                        {
                            result.Name = System.Net.WebUtility.HtmlDecode(officialName.Trim());
                        }
                    }

                    // 1.1 DLC Count
                    if (data.TryGetProperty("dlc", out var dlcArray) && dlcArray.ValueKind == JsonValueKind.Array)
                    {
                        result.DlcCount = dlcArray.GetArrayLength();
                    }

                    // 1.2 Supported Platforms
                    if (data.TryGetProperty("platforms", out var platforms) && platforms.ValueKind == JsonValueKind.Object)
                    {
                        if (platforms.TryGetProperty("windows", out var winProp) && winProp.ValueKind is JsonValueKind.True or JsonValueKind.False)
                            result.HasWindows = winProp.GetBoolean();

                        if (platforms.TryGetProperty("linux", out var linProp) && linProp.ValueKind is JsonValueKind.True or JsonValueKind.False)
                            result.HasLinux = linProp.GetBoolean();

                        if (platforms.TryGetProperty("mac", out var macProp) && macProp.ValueKind is JsonValueKind.True or JsonValueKind.False)
                            result.HasMac = macProp.GetBoolean();
                    }

                    // 1.3 App Type & Genres Detection
                    bool isSoftwareGenre = false;
                    if (data.TryGetProperty("genres", out var genresEl) && genresEl.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var g in genresEl.EnumerateArray())
                        {
                            if (g.TryGetProperty("description", out var descProp))
                            {
                                var desc = descProp.GetString() ?? "";
                                if (desc.Equals("Utilities", StringComparison.OrdinalIgnoreCase) ||
                                    desc.Equals("Design & Illustration", StringComparison.OrdinalIgnoreCase) ||
                                    desc.Equals("Animation & Modeling", StringComparison.OrdinalIgnoreCase) ||
                                    desc.Equals("Software Training", StringComparison.OrdinalIgnoreCase) ||
                                    desc.Equals("Software", StringComparison.OrdinalIgnoreCase) ||
                                    desc.Equals("Audio Production", StringComparison.OrdinalIgnoreCase) ||
                                    desc.Equals("Video Production", StringComparison.OrdinalIgnoreCase) ||
                                    desc.Equals("Web Publishing", StringComparison.OrdinalIgnoreCase) ||
                                    desc.Equals("Photo Editing", StringComparison.OrdinalIgnoreCase) ||
                                    desc.Equals("Game Development", StringComparison.OrdinalIgnoreCase) ||
                                    desc.Equals("Education", StringComparison.OrdinalIgnoreCase))
                                {
                                    isSoftwareGenre = true;
                                    break;
                                }
                            }
                        }
                    }

                    string rawType = string.Empty;
                    if (data.TryGetProperty("type", out var typeProp) && typeProp.ValueKind == JsonValueKind.String)
                    {
                        rawType = (typeProp.GetString() ?? "").Trim().ToLowerInvariant();
                    }

                    if (rawType is "tool" or "utility" or "driver")
                    {
                        result.AppType = "Tool";
                    }
                    else if (rawType is "application" || isSoftwareGenre)
                    {
                        result.AppType = "Application";
                    }
                    else
                    {
                        result.AppType = "Game";
                    }

                    // 1.4 Header image
                    if (data.TryGetProperty("header_image", out var headerProp) && headerProp.ValueKind == JsonValueKind.String)
                    {
                        var img = headerProp.GetString();
                        if (!string.IsNullOrWhiteSpace(img))
                            result.HeaderImageUrl = img;
                    }

                    // 1.5 NSFW / Adult Content Detection
                    bool isNsfw = false;
                    if (data.TryGetProperty("content_descriptors", out var cdProp))
                    {
                        if (cdProp.TryGetProperty("ids", out var idsProp) && idsProp.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var id in idsProp.EnumerateArray())
                            {
                                if (id.TryGetInt32(out var descriptorId) && descriptorId is 3 or 4)
                                {
                                    isNsfw = true;
                                    break;
                                }
                            }
                        }
                        if (cdProp.TryGetProperty("notes", out var cdNotes) && cdNotes.ValueKind == JsonValueKind.String)
                        {
                            var notes = cdNotes.GetString() ?? "";
                            if (notes.Contains("sexual content", StringComparison.OrdinalIgnoreCase) ||
                                notes.Contains("explicit sexual", StringComparison.OrdinalIgnoreCase) ||
                                notes.Contains("erotic", StringComparison.OrdinalIgnoreCase) ||
                                notes.Contains("hentai", StringComparison.OrdinalIgnoreCase) ||
                                notes.Contains("porn", StringComparison.OrdinalIgnoreCase))
                            {
                                isNsfw = true;
                            }
                        }
                    }
                    if (data.TryGetProperty("genres", out var allGenres) && allGenres.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var g in allGenres.EnumerateArray())
                        {
                            if (g.TryGetProperty("description", out var gDescProp))
                            {
                                var desc = gDescProp.GetString() ?? "";
                                if (desc.Equals("Hentai", StringComparison.OrdinalIgnoreCase) ||
                                    desc.Equals("Adult Only", StringComparison.OrdinalIgnoreCase) ||
                                    desc.Equals("Sexual Content", StringComparison.OrdinalIgnoreCase) ||
                                    desc.Equals("Erotic", StringComparison.OrdinalIgnoreCase))
                                {
                                    isNsfw = true;
                                    break;
                                }
                            }
                        }
                    }
                    if (!isNsfw && !string.IsNullOrWhiteSpace(result.Name))
                    {
                        var n = result.Name;
                        if (n.Contains("Hentai", StringComparison.OrdinalIgnoreCase) ||
                            n.Contains("Porn", StringComparison.OrdinalIgnoreCase) ||
                            n.Contains("Erotic", StringComparison.OrdinalIgnoreCase) ||
                            n.Contains("Adult Only", StringComparison.OrdinalIgnoreCase) ||
                            n.EndsWith(" Sex", StringComparison.OrdinalIgnoreCase))
                        {
                            isNsfw = true;
                        }
                    }
                    result.IsNsfw = isNsfw;

                    // 1.6 DRM
                    bool hasDrm = false;
                    string? drmNotice = null;
                    if (data.TryGetProperty("drm_notice", out var drmProp) && drmProp.ValueKind == JsonValueKind.String)
                    {
                        var notice = drmProp.GetString();
                        if (!string.IsNullOrWhiteSpace(notice))
                        {
                            hasDrm = true;
                            drmNotice = notice.Trim();
                        }
                    }
                    if (data.TryGetProperty("ext_user_account_notice", out var extAccProp) && extAccProp.ValueKind == JsonValueKind.String)
                    {
                        var notice = extAccProp.GetString();
                        if (!string.IsNullOrWhiteSpace(notice))
                        {
                            hasDrm = true;
                            drmNotice = string.IsNullOrWhiteSpace(drmNotice) ? notice.Trim() : $"{drmNotice} • {notice.Trim()}";
                        }
                    }
                    if (data.TryGetProperty("legal_notice", out var legalProp) && legalProp.ValueKind == JsonValueKind.String)
                    {
                        var legal = legalProp.GetString() ?? "";
                        if (legal.Contains("Denuvo", StringComparison.OrdinalIgnoreCase) ||
                            legal.Contains("SecuROM", StringComparison.OrdinalIgnoreCase) ||
                            legal.Contains("VMProtect", StringComparison.OrdinalIgnoreCase))
                        {
                            hasDrm = true;
                            if (string.IsNullOrWhiteSpace(drmNotice))
                                drmNotice = "Incorporates 3rd-party DRM";
                        }
                    }
                    result.HasDrm = hasDrm;
                    result.DrmNotice = drmNotice;

                    // 1.7 Release Date fallback
                    if (string.IsNullOrWhiteSpace(result.Version) &&
                        data.TryGetProperty("release_date", out var rd) &&
                        rd.TryGetProperty("date", out var dateProp))
                    {
                        var dateStr = dateProp.GetString();
                        if (!string.IsNullOrWhiteSpace(dateStr))
                        {
                            result.Version = dateStr;
                        }
                    }

                    // Save enriched payload in persistent cache
                    if (_cache != null)
                    {
                        var item = new SearchResultEnrichmentCache(
                            result.Name,
                            result.DlcCount,
                            result.HasWindows,
                            result.HasLinux,
                            result.HasMac,
                            result.AppType ?? "Game",
                            result.HeaderImageUrl,
                            result.IsNsfw,
                            result.HasDrm,
                            result.DrmNotice,
                            result.Version
                        );
                        try { await _cache.SetAsync(cacheKey, item, TimeSpan.FromDays(7), ct).ConfigureAwait(false); } catch { }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Steam Store appdetails API call skipped/failed for AppId={AppId}", result.AppId);
        }

        // Keyword heuristic overrides for well-known application and tool packages
        if (result.AppId == 431960 ||
            (!string.IsNullOrWhiteSpace(result.Name) &&
             (result.Name.Contains("Wallpaper Engine", StringComparison.OrdinalIgnoreCase) ||
              result.Name.Contains("Soundpad", StringComparison.OrdinalIgnoreCase) ||
              result.Name.Contains("Aseprite", StringComparison.OrdinalIgnoreCase) ||
              result.Name.Contains("3DMark", StringComparison.OrdinalIgnoreCase) ||
              result.Name.Contains("Benchmark", StringComparison.OrdinalIgnoreCase) ||
              result.Name.Contains("OBS Studio", StringComparison.OrdinalIgnoreCase))))
        {
            result.AppType = "Application";
        }
    }

    /// <summary>
    /// Consolidated model holding SteamCMD depots, builds, and per-depot enrichment metadata.
    /// </summary>
    public sealed record SteamCmdUnifiedInfo(
        SteamAppDepotInfo? DepotInfo,
        IReadOnlyList<GameBuildInfo> Builds,
        IReadOnlyDictionary<uint, SteamDepotMeta> DepotEnrichment
    );

    /// <summary>
    /// Fetches all SteamCMD data (depots, builds, enrichment) in a SINGLE network request, cached and coordinated.
    /// </summary>
    public async Task<SteamCmdUnifiedInfo?> GetAppUnifiedInfoAsync(uint appId, CancellationToken ct = default)
    {
        if (appId == 0) return null;

        var cacheKey = $"steamcmd_unified_{appId}_v3";
        if (_cache != null)
        {
            try
            {
                var cached = await _cache.GetAsync<SteamCmdUnifiedInfo>(cacheKey, ct).ConfigureAwait(false);
                if (cached != null)
                {
                    _metrics?.OnCacheHit("SteamCMD", $"unified/{appId}");
                    return cached;
                }
            }
            catch { }
        }

        return await _coordinator.ExecuteAsync($"steamcmd_unified_{appId}", async innerCt =>
        {
            if (_cache != null)
            {
                try
                {
                    var cached = await _cache.GetAsync<SteamCmdUnifiedInfo>(cacheKey, innerCt).ConfigureAwait(false);
                    if (cached != null)
                    {
                        _metrics?.OnCacheHit("SteamCMD", $"unified/{appId}");
                        return cached;
                    }
                }
                catch { }
            }

            try
            {
                _metrics?.OnProviderRequest("SteamCMD", $"https://api.steamcmd.net/v1/info/{appId}");

                using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.steamcmd.net/v1/info/{appId}");
                if (!_http.DefaultRequestHeaders.Contains("User-Agent"))
                {
                    request.Headers.UserAgent.ParseAdd("BlueStar/1.2.3");
                }

                using var response = await _http.SendAsync(request, innerCt).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogDebug("SteamCMD API returned {StatusCode} for AppId={AppId}", response.StatusCode, appId);
                    return null;
                }

                var json = await response.Content.ReadAsStringAsync(innerCt).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("data", out var dataEl) ||
                    !dataEl.TryGetProperty(appId.ToString(), out var appEl))
                {
                    return null;
                }

                string? appType = null;
                if (appEl.TryGetProperty("common", out var commonEl) &&
                    commonEl.TryGetProperty("type", out var typeProp))
                {
                    appType = typeProp.GetString();
                }

                SteamAppDepotInfo? depotInfo = null;
                var builds = new List<GameBuildInfo>();
                var depotEnrichment = new Dictionary<uint, SteamDepotMeta>();

                if (appEl.TryGetProperty("depots", out var depotsEl))
                {
                    // 1. Build SteamAppDepotInfo
                    DateTimeOffset? latestDate = null;
                    string? buildId = null;

                    if (depotsEl.TryGetProperty("branches", out var branchesEl) &&
                        branchesEl.TryGetProperty("public", out var publicEl))
                    {
                        if (publicEl.TryGetProperty("buildid", out var bIdProp))
                            buildId = bIdProp.GetString();

                        long unix = 0;
                        if (publicEl.TryGetProperty("timeupdated", out var tuProp))
                        {
                            if (tuProp.ValueKind == JsonValueKind.Number) tuProp.TryGetInt64(out unix);
                            else if (tuProp.ValueKind == JsonValueKind.String) long.TryParse(tuProp.GetString(), out unix);
                        }

                        if (unix == 0 && publicEl.TryGetProperty("timebuildupdated", out var tbuProp))
                        {
                            if (tbuProp.ValueKind == JsonValueKind.Number) tbuProp.TryGetInt64(out unix);
                            else if (tbuProp.ValueKind == JsonValueKind.String) long.TryParse(tbuProp.GetString(), out unix);
                        }

                        if (unix > 0)
                        {
                            latestDate = DateTimeOffset.FromUnixTimeSeconds(unix);
                        }
                    }

                    var manifests = new Dictionary<ulong, ulong>();
                    foreach (var depotProp in depotsEl.EnumerateObject())
                    {
                        if (ulong.TryParse(depotProp.Name, out var depotId) &&
                            depotProp.Value.TryGetProperty("manifests", out var mEl) &&
                            mEl.TryGetProperty("public", out var pManEl) &&
                            pManEl.TryGetProperty("gid", out var gidProp))
                        {
                            var gidStr = gidProp.GetString();
                            if (ulong.TryParse(gidStr, out var gid))
                            {
                                manifests[depotId] = gid;
                            }
                        }
                    }

                    depotInfo = new SteamAppDepotInfo(latestDate, buildId, manifests, appType);

                    // 2. Build GameBuildInfo list
                    if (depotsEl.TryGetProperty("branches", out var allBranchesEl))
                    {
                        foreach (var branchProp in allBranchesEl.EnumerateObject())
                        {
                            var branchName = branchProp.Name;
                            var branchObj = branchProp.Value;

                            string branchBuildId = string.Empty;
                            if (branchObj.TryGetProperty("buildid", out var bIdProp))
                            {
                                branchBuildId = bIdProp.GetString() ?? string.Empty;
                            }

                            string? desc = null;
                            if (branchObj.TryGetProperty("description", out var descProp))
                            {
                                desc = descProp.GetString();
                            }

                            DateTimeOffset? updateDate = null;
                            long unix = 0;
                            if (branchObj.TryGetProperty("timeupdated", out var tuProp))
                            {
                                if (tuProp.ValueKind == JsonValueKind.Number) tuProp.TryGetInt64(out unix);
                                else if (tuProp.ValueKind == JsonValueKind.String) long.TryParse(tuProp.GetString(), out unix);
                            }
                            if (unix == 0 && branchObj.TryGetProperty("timebuildupdated", out var tbuProp))
                            {
                                if (tbuProp.ValueKind == JsonValueKind.Number) tbuProp.TryGetInt64(out unix);
                                else if (tbuProp.ValueKind == JsonValueKind.String) long.TryParse(tbuProp.GetString(), out unix);
                            }
                            if (unix > 0)
                            {
                                updateDate = DateTimeOffset.FromUnixTimeSeconds(unix);
                            }

                            var depotManifests = new Dictionary<uint, ulong>();
                            foreach (var depotProp in depotsEl.EnumerateObject())
                            {
                                if (uint.TryParse(depotProp.Name, out var dId) &&
                                    depotProp.Value.TryGetProperty("manifests", out var mEl) &&
                                    mEl.TryGetProperty(branchName, out var pManEl) &&
                                    pManEl.TryGetProperty("gid", out var gidProp))
                                {
                                    var gidStr = gidProp.GetString();
                                    if (ulong.TryParse(gidStr, out var gid))
                                    {
                                        depotManifests[dId] = gid;
                                    }
                                }
                            }

                            var displayName = branchName.Equals("public", StringComparison.OrdinalIgnoreCase)
                                ? (string.IsNullOrWhiteSpace(branchBuildId) ? "Latest Public Release" : $"Latest Build {branchBuildId} (public)")
                                : $"{branchName} (Build {branchBuildId})";

                            if (!string.IsNullOrWhiteSpace(desc))
                            {
                                displayName += $" - {desc}";
                            }

                            builds.Add(new GameBuildInfo
                            {
                                BuildId = branchBuildId,
                                BranchName = branchName,
                                DisplayName = displayName,
                                UpdatedAt = updateDate,
                                Description = desc,
                                IsCurrentBuild = false,
                                Source = "Steam",
                                DepotManifests = depotManifests
                            });
                        }
                    }

                    // 3. Build SteamDepotMeta enrichment
                    foreach (var depotProp in depotsEl.EnumerateObject())
                    {
                        if (!uint.TryParse(depotProp.Name, out var depotId))
                            continue;

                        var depotEl = depotProp.Value;
                        string? name = null;
                        if (depotEl.TryGetProperty("name", out var nameProp))
                            name = nameProp.GetString()?.Trim();

                        string? osList = null;
                        bool isOptional = false;
                        bool isShared = false;

                        if (depotEl.TryGetProperty("config", out var configEl))
                        {
                            if (configEl.TryGetProperty("oslist", out var osListProp))
                                osList = osListProp.GetString()?.Trim().ToLowerInvariant();

                            if (configEl.TryGetProperty("optional", out var optProp))
                                isOptional = optProp.GetString() == "1" || (optProp.ValueKind == JsonValueKind.Number && optProp.GetInt32() == 1);

                            if (configEl.TryGetProperty("SharedInstall", out var sharedProp))
                                isShared = sharedProp.GetString() == "1" || (sharedProp.ValueKind == JsonValueKind.Number && sharedProp.GetInt32() == 1);
                        }

                        if (!isShared && depotEl.TryGetProperty("sharedinstall", out var si))
                            isShared = si.GetString() == "1" || (si.ValueKind == JsonValueKind.Number && si.GetInt32() == 1);

                        depotEnrichment[depotId] = new SteamDepotMeta(depotId, name, osList, isOptional, isShared);
                    }
                }
                else if (!string.IsNullOrWhiteSpace(appType))
                {
                    depotInfo = new SteamAppDepotInfo(null, null, new Dictionary<ulong, ulong>(), appType);
                }

                var orderedBuilds = builds.OrderByDescending(b => b.BranchName == "public")
                                          .ThenByDescending(b => b.UpdatedAt ?? DateTimeOffset.MinValue)
                                          .ToList();

                var unified = new SteamCmdUnifiedInfo(depotInfo, orderedBuilds, depotEnrichment);

                if (_cache != null)
                {
                    try
                    {
                        await _cache.SetAsync(cacheKey, unified, TimeSpan.FromDays(7), innerCt).ConfigureAwait(false);
                        if (depotInfo != null)
                            await _cache.SetAsync($"steamcmd_depotinfo_{appId}_v3", depotInfo, TimeSpan.FromDays(7), innerCt).ConfigureAwait(false);
                        if (depotEnrichment.Count > 0)
                            await _cache.SetAsync($"steamcmd_depot_enrich_{appId}_v3", depotEnrichment, TimeSpan.FromDays(7), innerCt).ConfigureAwait(false);
                    }
                    catch { }
                }

                return unified;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to get SteamCMD AppInfo for AppId={AppId}", appId);
                return null;
            }
        }, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Gets detailed depot and branch information from the SteamCMD/SteamDB app info catalog.
    /// </summary>
    public async Task<SteamAppDepotInfo?> GetAppDepotInfoAsync(uint appId, CancellationToken ct = default)
    {
        if (appId == 0) return null;

        var cacheKey = $"steamcmd_depotinfo_{appId}_v3";
        if (_cache != null)
        {
            try
            {
                var cached = await _cache.GetAsync<SteamAppDepotInfo>(cacheKey, ct).ConfigureAwait(false);
                if (cached != null)
                {
                    _metrics?.OnCacheHit("SteamCMD", $"depotinfo/{appId}");
                    return cached;
                }
            }
            catch { }
        }

        var unified = await GetAppUnifiedInfoAsync(appId, ct).ConfigureAwait(false);
        return unified?.DepotInfo;
    }

    /// <summary>
    /// Gets all available game branches and builds from SteamCMD / Steam app info.
    /// </summary>
    public async Task<IReadOnlyList<GameBuildInfo>> GetAppBuildsAsync(uint appId, CancellationToken ct = default)
    {
        if (appId == 0) return [];

        var unified = await GetAppUnifiedInfoAsync(appId, ct).ConfigureAwait(false);
        return unified?.Builds ?? [];
    }

    /// <summary>
    /// Gets the date of the latest patch or update from Steam depot history or Steam news API.
    /// </summary>
    public async Task<DateTimeOffset?> GetLatestAppUpdateDateAsync(uint appId, CancellationToken ct = default)
    {
        if (appId == 0) return null;

        var cacheKey = $"steam_latest_update_{appId}_v3";
        if (_cache != null)
        {
            try
            {
                var cached = await _cache.GetAsync<DateTimeOffset?>(cacheKey, ct).ConfigureAwait(false);
                if (cached.HasValue)
                {
                    _metrics?.OnCacheHit("SteamStore", $"latest_update/{appId}");
                    return cached.Value;
                }
            }
            catch { }
        }

        return await _coordinator.ExecuteAsync($"steam_update_date_{appId}", innerCt => FetchLatestAppUpdateDateCoreAsync(appId, cacheKey, innerCt), ct).ConfigureAwait(false);
    }

    private async Task<DateTimeOffset?> FetchLatestAppUpdateDateCoreAsync(uint appId, string cacheKey, CancellationToken ct)
    {
        try
        {
            // 1. Try exact depot/build date first
            var depotInfo = await GetAppDepotInfoAsync(appId, ct).ConfigureAwait(false);
            if (depotInfo?.LatestBuildDate != null)
            {
                if (_cache != null)
                {
                    try { await _cache.SetAsync(cacheKey, (DateTimeOffset?)depotInfo.LatestBuildDate, TimeSpan.FromDays(3), ct).ConfigureAwait(false); } catch { }
                }
                return depotInfo.LatestBuildDate;
            }

            // 2. Fallback to Steam News API
            try
            {
                await ThrottleAsync(ct).ConfigureAwait(false);

                using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.steampowered.com/ISteamNews/GetNewsForApp/v2/?appid={appId}&count=5&maxlength=300");
                if (!_http.DefaultRequestHeaders.Contains("User-Agent"))
                {
                    request.Headers.UserAgent.ParseAdd("BlueStar/1.2.3");
                }

                using var newsResponse = await _http.SendAsync(request, ct).ConfigureAwait(false);
                if (newsResponse.IsSuccessStatusCode)
                {
                    var newsJson = await newsResponse.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                    using var newsDoc = JsonDocument.Parse(newsJson);
                    if (newsDoc.RootElement.TryGetProperty("appnews", out var appNews) &&
                        appNews.TryGetProperty("newsitems", out var newsItems) &&
                        newsItems.ValueKind == JsonValueKind.Array &&
                        newsItems.GetArrayLength() > 0)
                    {
                        long maxUnix = 0;
                        foreach (var item in newsItems.EnumerateArray())
                        {
                            if (item.TryGetProperty("date", out var dateEl))
                            {
                                long dateUnix = 0;
                                if (dateEl.ValueKind == JsonValueKind.Number)
                                {
                                    dateEl.TryGetInt64(out dateUnix);
                                }
                                else if (dateEl.ValueKind == JsonValueKind.String && long.TryParse(dateEl.GetString(), out var parsed))
                                {
                                    dateUnix = parsed;
                                }

                                if (dateUnix > maxUnix)
                                {
                                    maxUnix = dateUnix;
                                }
                            }
                        }

                        if (maxUnix > 0)
                        {
                            var updateDate = DateTimeOffset.FromUnixTimeSeconds(maxUnix);
                            if (_cache != null)
                            {
                                try { await _cache.SetAsync(cacheKey, (DateTimeOffset?)updateDate, TimeSpan.FromDays(3), ct).ConfigureAwait(false); } catch { }
                            }
                            return updateDate;
                        }
                    }
                }
                else
                {
                    _logger.LogWarning("Steam News API returned {StatusCode} for AppId={AppId}", newsResponse.StatusCode, appId);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to fetch latest update date for AppId={AppId}", appId);
            }

            return null;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed in FetchLatestAppUpdateDateCoreAsync for {AppId}", appId);
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SteamStoreSearchItem>> SearchStoreAsync(string query, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            return [];

        // If direct numeric AppId provided, attempt direct fetch first
        if (uint.TryParse(query.Trim(), out var directAppId) && directAppId > 0)
        {
            var meta = await GetMetadataAsync(directAppId, ct).ConfigureAwait(false);
            if (meta != null)
            {
                return [new SteamStoreSearchItem(
                    directAppId,
                    meta.Name,
                    meta.CapsuleImageUrl,
                    meta.HeaderImageUrl ?? $"https://cdn.cloudflare.steamstatic.com/steam/apps/{directAppId}/header.jpg")];
            }
        }

        try
        {
            await ThrottleAsync(ct).ConfigureAwait(false);
            var url = $"https://store.steampowered.com/api/storesearch/?term={Uri.EscapeDataString(query.Trim())}&l=english&cc=US";
            var response = await _http.GetAsync(url, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return [];

            var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
                return [];

            var results = new List<SteamStoreSearchItem>();
            foreach (var item in items.EnumerateArray())
            {
                if (item.TryGetProperty("id", out var idProp) && idProp.TryGetUInt32(out var id) && id > 0)
                {
                    var name = item.TryGetProperty("name", out var nameProp) ? nameProp.GetString() ?? $"App {id}" : $"App {id}";
                    var tiny = item.TryGetProperty("tiny_image", out var tinyProp) ? tinyProp.GetString() : null;
                    var header = $"https://cdn.cloudflare.steamstatic.com/steam/apps/{id}/header.jpg";
                    results.Add(new SteamStoreSearchItem(id, name, tiny, header));
                }
            }
            return results;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error searching Steam Store for query: {Query}", query);
            return [];
        }
    }

    /// <summary>
    /// Model holding Steam App depot branches and public manifest GIDs.
    /// </summary>
    public sealed record SteamAppDepotInfo(
        DateTimeOffset? LatestBuildDate,
        string? BuildId,
        IReadOnlyDictionary<ulong, ulong> PublicManifests,
        string? AppType = null
    );

    /// <summary>
    /// Per-depot enrichment data pulled from the SteamCMD info endpoint.
    /// </summary>
    public sealed record SteamDepotMeta(
        uint DepotId,
        string? Name,
        /// <summary>Comma-separated OS list from config.oslist, e.g. "windows" or "linux,macos".</summary>
        string? OsList,
        /// <summary>True when config.optional = "1" — not auto-installed by Steam.</summary>
        bool IsOptional,
        /// <summary>True when config.SharedInstall = "1" — shared depot, not game-specific content.</summary>
        bool IsShared
    );

    /// <summary>
    /// Fetches per-depot enrichment data (names, OS lists, optional flags) for <paramref name="appId"/>
    /// via the SteamCMD info API. Returns a dictionary keyed by DepotId.
    /// </summary>
    public async Task<IReadOnlyDictionary<uint, SteamDepotMeta>> GetDepotEnrichmentAsync(
        uint appId, CancellationToken ct = default)
    {
        if (appId == 0) return new Dictionary<uint, SteamDepotMeta>();

        var cacheKey = $"steamcmd_depot_enrich_{appId}_v3";
        if (_cache != null)
        {
            try
            {
                var cached = await _cache.GetAsync<Dictionary<uint, SteamDepotMeta>>(cacheKey, ct).ConfigureAwait(false);
                if (cached != null)
                {
                    _metrics?.OnCacheHit("SteamCMD", $"depot_enrich/{appId}");
                    return cached;
                }
            }
            catch { }
        }

        var unified = await GetAppUnifiedInfoAsync(appId, ct).ConfigureAwait(false);
        return unified?.DepotEnrichment ?? new Dictionary<uint, SteamDepotMeta>();
    }


    /// <summary>
    /// Fetches the community tags from the game's store page.
    /// <para>
    /// The appdetails API returns genres and categories but never the user tags, and there is no
    /// public endpoint for them. The store page embeds them as a JSON array in its
    /// <c>InitAppTagModal(appid, [ … ])</c> call, which is what this reads. If the page shape ever
    /// changes the method just returns nothing — tags are additive, never load-bearing.
    /// </para>
    /// </summary>
    public async Task<IReadOnlyList<string>> GetStoreTagsAsync(uint appId, CancellationToken ct = default)
    {
        if (appId == 0) return [];

        var cacheKey = $"steam_store_tags_{appId}_v1";
        if (_cache != null)
        {
            try
            {
                var cached = await _cache.GetAsync<List<string>>(cacheKey, ct).ConfigureAwait(false);
                if (cached is { Count: > 0 }) return cached.AsReadOnly();
            }
            catch { }
        }

        try
        {
            if (!await ThrottleAsync(ct).ConfigureAwait(false)) return [];

            // birthtime + mature content cookies keep age-gated pages from redirecting to a form.
            var url = $"https://store.steampowered.com/app/{appId}/?l=english&cc=US";
            _metrics?.OnProviderRequest("SteamStoreTags", url);

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("Cookie", "birthtime=283993201; mature_content=1; lastagecheckage=1-January-1980; wants_mature_content=1");

            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogDebug("Store page returned {Status} for AppId={AppId}", response.StatusCode, appId);
                return [];
            }

            var html = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            var marker = "InitAppTagModal(";
            var start = html.IndexOf(marker, StringComparison.Ordinal);
            if (start < 0) return [];

            var arrayStart = html.IndexOf('[', start);
            if (arrayStart < 0) return [];

            // Walk to the matching bracket so a "]" inside a tag name cannot truncate the array.
            int depth = 0;
            int arrayEnd = -1;
            bool inString = false, escaped = false;
            for (int i = arrayStart; i < html.Length; i++)
            {
                var c = html[i];
                if (escaped) { escaped = false; continue; }
                if (c == '\\' && inString) { escaped = true; continue; }
                if (c == '"') { inString = !inString; continue; }
                if (inString) continue;
                if (c == '[') depth++;
                else if (c == ']')
                {
                    depth--;
                    if (depth == 0) { arrayEnd = i; break; }
                }
            }

            if (arrayEnd < 0) return [];

            var json = html[arrayStart..(arrayEnd + 1)];
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return [];

            var tags = new List<string>();
            foreach (var tag in doc.RootElement.EnumerateArray())
            {
                if (tag.ValueKind != JsonValueKind.Object) continue;
                if (!tag.TryGetProperty("name", out var nameProp)) continue;

                var name = nameProp.GetString();
                if (string.IsNullOrWhiteSpace(name)) continue;

                name = System.Net.WebUtility.HtmlDecode(name).Trim();
                if (!tags.Contains(name, StringComparer.OrdinalIgnoreCase)) tags.Add(name);
            }

            if (_cache != null && tags.Count > 0)
            {
                try { await _cache.SetAsync(cacheKey, tags, TimeSpan.FromDays(7), ct).ConfigureAwait(false); } catch { }
            }

            _logger.LogDebug("Parsed {Count} store tags for AppId={AppId}", tags.Count, appId);
            return tags.AsReadOnly();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to read store tags for AppId={AppId}", appId);
            return [];
        }
    }

    /// <summary>
    /// Reads the store screenshot list. Full-size URLs are preferred; the thumbnail is the
    /// fallback so a game with only thumbnails still shows something.
    /// </summary>
    private static IReadOnlyList<string> GetScreenshots(JsonElement data)
    {
        if (!data.TryGetProperty("screenshots", out var shots) || shots.ValueKind != JsonValueKind.Array)
            return [];

        var list = new List<string>();
        foreach (var shot in shots.EnumerateArray())
        {
            string? url = null;
            if (shot.TryGetProperty("path_full", out var full)) url = full.GetString();
            if (string.IsNullOrWhiteSpace(url) && shot.TryGetProperty("path_thumbnail", out var thumb))
                url = thumb.GetString();

            if (!string.IsNullOrWhiteSpace(url)) list.Add(url!);
        }
        return list.AsReadOnly();
    }

    private static int? GetMetacriticScore(JsonElement data)
    {
        if (data.TryGetProperty("metacritic", out var mc) &&
            mc.ValueKind == JsonValueKind.Object &&
            mc.TryGetProperty("score", out var score) &&
            score.TryGetInt32(out var value))
        {
            return value;
        }
        return null;
    }

    private static IReadOnlyList<string> GetPlatforms(JsonElement data)
    {
        if (!data.TryGetProperty("platforms", out var p) || p.ValueKind != JsonValueKind.Object)
            return [];

        var list = new List<string>();
        if (p.TryGetProperty("windows", out var w) && w.ValueKind == JsonValueKind.True) list.Add("Windows");
        if (p.TryGetProperty("mac", out var m) && m.ValueKind == JsonValueKind.True) list.Add("macOS");
        if (p.TryGetProperty("linux", out var l) && l.ValueKind == JsonValueKind.True) list.Add("Linux");
        return list.AsReadOnly();
    }

    /// <summary>
    /// Turns Steam's HTML / BBCode announcement bodies into plain readable text. Steam mixes both
    /// in the same field depending on the feed, so both are handled.
    /// </summary>
    private static string? StripHtml(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return html;

        var text = html;

        // Keep paragraph and list structure as line breaks before dropping the tags.
        text = System.Text.RegularExpressions.Regex.Replace(text, @"<\s*br\s*/?>", "\n", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        text = System.Text.RegularExpressions.Regex.Replace(text, @"</\s*(p|div|li|h[1-6])\s*>", "\n", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        text = System.Text.RegularExpressions.Regex.Replace(text, @"<\s*li[^>]*>", "• ", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        text = System.Text.RegularExpressions.Regex.Replace(text, @"<[^>]+>", string.Empty);

        // BBCode used by community announcements.
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\[/?(b|i|u|h[1-3]|list|url[^\]]*|img|quote[^\]]*|code|strike|spoiler|noparse)\]", string.Empty, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\[\*\]", "• ");

        text = System.Net.WebUtility.HtmlDecode(text);
        text = System.Text.RegularExpressions.Regex.Replace(text, @"[ \t]+", " ");
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\n{3,}", "\n\n");

        return text.Trim();
    }

    /// <summary>
    /// Fetches recent Steam news for an app, patch notes first.
    /// <para>
    /// This is the public ISteamNews endpoint, so it needs no key. Entries Steam tags as patch
    /// notes are surfaced ahead of general announcements, because "what changed in this build" is
    /// what someone looking at an installed game wants.
    /// </para>
    /// </summary>
    public async Task<IReadOnlyList<SteamNewsItem>> GetNewsAsync(uint appId, int count = 8, CancellationToken ct = default)
    {
        if (appId == 0) return [];

        var cacheKey = $"steam_news_{appId}_v1";
        if (_cache != null)
        {
            try
            {
                var cached = await _cache.GetAsync<List<SteamNewsItem>>(cacheKey, ct).ConfigureAwait(false);
                if (cached is { Count: > 0 }) return cached.AsReadOnly();
            }
            catch { }
        }

        try
        {
            if (!await ThrottleAsync(ct).ConfigureAwait(false)) return [];

            var url = $"https://api.steampowered.com/ISteamNews/GetNewsForApp/v2/?appid={appId}&count={Math.Clamp(count, 1, 30)}&maxlength=1200&format=json";
            _metrics?.OnProviderRequest("SteamNews", url);

            using var response = await _http.GetAsync(url, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogDebug("Steam news returned {Status} for AppId={AppId}", response.StatusCode, appId);
                return [];
            }

            var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty("appnews", out var appnews) ||
                !appnews.TryGetProperty("newsitems", out var items) ||
                items.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var list = new List<SteamNewsItem>();
            foreach (var item in items.EnumerateArray())
            {
                var feedName = item.TryGetProperty("feedname", out var fn) ? fn.GetString() ?? string.Empty : string.Empty;

                bool isPatch = feedName.Contains("update", StringComparison.OrdinalIgnoreCase);
                if (item.TryGetProperty("tags", out var tags) && tags.ValueKind == JsonValueKind.Array)
                {
                    foreach (var tag in tags.EnumerateArray())
                    {
                        var t = tag.GetString();
                        if (!string.IsNullOrWhiteSpace(t) && t.Contains("patchnote", StringComparison.OrdinalIgnoreCase))
                        {
                            isPatch = true;
                            break;
                        }
                    }
                }

                var unix = item.TryGetProperty("date", out var d) && d.TryGetInt64(out var epoch) ? epoch : 0;

                list.Add(new SteamNewsItem
                {
                    Gid = item.TryGetProperty("gid", out var g) ? g.GetString() ?? string.Empty : string.Empty,
                    Title = item.TryGetProperty("title", out var t2) ? t2.GetString() ?? string.Empty : string.Empty,
                    Url = item.TryGetProperty("url", out var u) ? u.GetString() ?? string.Empty : string.Empty,
                    Contents = StripHtml(item.TryGetProperty("contents", out var c) ? c.GetString() : null) ?? string.Empty,
                    FeedLabel = item.TryGetProperty("feedlabel", out var fl) ? fl.GetString() ?? string.Empty : string.Empty,
                    FeedName = feedName,
                    Author = item.TryGetProperty("author", out var a2) ? a2.GetString() : null,
                    PublishedAt = unix > 0 ? DateTimeOffset.FromUnixTimeSeconds(unix) : DateTimeOffset.MinValue,
                    IsPatchNote = isPatch
                });
            }

            var ordered = list
                .OrderByDescending(n => n.IsPatchNote)
                .ThenByDescending(n => n.PublishedAt)
                .ToList();

            if (_cache != null)
            {
                try { await _cache.SetAsync(cacheKey, ordered, TimeSpan.FromHours(6), ct).ConfigureAwait(false); } catch { }
            }

            return ordered.AsReadOnly();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to fetch Steam news for AppId={AppId}", appId);
            return [];
        }
    }

    private static string? GetFirstArrayString(JsonElement parent, string propertyName)
    {
        if (parent.TryGetProperty(propertyName, out var arr) &&
            arr.ValueKind == JsonValueKind.Array &&
            arr.GetArrayLength() > 0)
        {
            return arr[0].GetString();
        }
        return null;
    }

    private static string? GetReleaseDate(JsonElement data)
    {
        if (data.TryGetProperty("release_date", out var rd) &&
            rd.TryGetProperty("date", out var date))
        {
            return date.GetString();
        }
        return null;
    }

    private static IReadOnlyList<string> GetStringArray(JsonElement parent, string arrayProp, string valueProp)
    {
        if (!parent.TryGetProperty(arrayProp, out var arr) || arr.ValueKind != JsonValueKind.Array)
            return [];

        var list = new List<string>();
        foreach (var item in arr.EnumerateArray())
        {
            if (item.TryGetProperty(valueProp, out var val))
            {
                var s = val.GetString();
                if (!string.IsNullOrWhiteSpace(s))
                    list.Add(s);
            }
        }
        return list.AsReadOnly();
    }
}
