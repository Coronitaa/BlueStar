using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json;
using BlueStar.Core.Interfaces;
using BlueStar.Core.Models;
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

    private readonly ConcurrentDictionary<uint, Lazy<Task<GameMetadata?>>> _inFlightMetadata = new();
    private readonly ConcurrentDictionary<uint, Lazy<Task<SteamAppDepotInfo?>>> _inFlightDepotInfo = new();
    private readonly ConcurrentDictionary<uint, Lazy<Task<DateTimeOffset?>>> _inFlightUpdateDates = new();

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
    public SteamStoreApiClient(HttpClient http, ILogger<SteamStoreApiClient> logger, ICacheService? cache = null)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _cache = cache;
    }

    private async Task<bool> ThrottleAsync(CancellationToken ct)
    {
        if (DateTimeOffset.UtcNow < _cooldownUntil)
        {
            _logger.LogWarning("Steam Store API is in backoff cooldown until {Cooldown}", _cooldownUntil);
            return false;
        }

        await _throttleSemaphore.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (DateTimeOffset.UtcNow < _cooldownUntil)
                return false;

            var elapsed = DateTimeOffset.UtcNow - _lastRequest;
            if (elapsed < MinRequestInterval)
            {
                var delay = MinRequestInterval - elapsed;
                await Task.Delay(delay, ct).ConfigureAwait(false);
            }
            _lastRequest = DateTimeOffset.UtcNow;
            return true;
        }
        finally
        {
            _throttleSemaphore.Release();
        }
    }

    private void ReportRateLimitEncountered(System.Net.HttpStatusCode statusCode)
    {
        _logger.LogWarning("Steam API rate limit / block encountered ({StatusCode}). Entering 5-minute backoff cooldown.", statusCode);
        _cooldownUntil = DateTimeOffset.UtcNow.AddMinutes(5);
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
                if (cached != null) return cached;
            }
            catch { }
        }

        // Deduplicate concurrent requests for the same AppId
        var lazyTask = _inFlightMetadata.GetOrAdd(appId, id => new Lazy<Task<GameMetadata?>>(() => FetchMetadataCoreAsync(id, cacheKey, ct)));
        return await lazyTask.Value.ConfigureAwait(false);
    }

    private async Task<GameMetadata?> FetchMetadataCoreAsync(uint appId, string cacheKey, CancellationToken ct)
    {
        try
        {
            if (!await ThrottleAsync(ct).ConfigureAwait(false))
                return null;

            _logger.LogDebug("Fetching Steam metadata for AppId={AppId}", appId);

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
                LastUpdated = DateTimeOffset.UtcNow
            };

            if (_cache != null)
            {
                try { await _cache.SetAsync(cacheKey, meta, TimeSpan.FromDays(7), ct).ConfigureAwait(false); } catch { }
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
        finally
        {
            _inFlightMetadata.TryRemove(appId, out _);
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
                if (cached != null) return cached.AsReadOnly();
            }
            catch { }
        }

        if (!await ThrottleAsync(ct).ConfigureAwait(false))
            return [];

        try
        {
            var url = $"https://store.steampowered.com/api/appdetails?appids={appId}&l=english&cc=US";
            var response = await _http.GetAsync(url, ct).ConfigureAwait(false);

            if (response.StatusCode is System.Net.HttpStatusCode.TooManyRequests or System.Net.HttpStatusCode.Forbidden)
            {
                ReportRateLimitEncountered(response.StatusCode);
                return [];
            }

            if (!response.IsSuccessStatusCode)
                return [];

            var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (json.Contains("Access Denied", StringComparison.OrdinalIgnoreCase) ||
                json.Contains("edgesuite.net", StringComparison.OrdinalIgnoreCase))
            {
                ReportRateLimitEncountered(System.Net.HttpStatusCode.Forbidden);
                return [];
            }

            using var doc = JsonDocument.Parse(json);

            var appKey = appId.ToString();
            if (!doc.RootElement.TryGetProperty(appKey, out var appElement) ||
                !appElement.TryGetProperty("success", out var s) || !s.GetBoolean() ||
                !appElement.TryGetProperty("data", out var data) ||
                !data.TryGetProperty("dlc", out var dlcArray))
            {
                return [];
            }

            var dlcList = new List<DlcInfo>();
            foreach (var dlcElement in dlcArray.EnumerateArray())
            {
                if (dlcElement.TryGetUInt32(out var dlcAppId))
                {
                    dlcList.Add(new DlcInfo
                    {
                        AppId = dlcAppId,
                        Name = $"DLC {dlcAppId}", // Resolved later with individual appdetails calls
                        Depots = [],
                        IsInstalled = false
                    });
                }
            }

            if (_cache != null && dlcList.Count > 0)
            {
                try { await _cache.SetAsync(cacheKey, dlcList, TimeSpan.FromDays(7), ct).ConfigureAwait(false); } catch { }
            }

            _logger.LogDebug("Found {Count} DLCs for AppId={AppId}", dlcList.Count, appId);
            return dlcList.AsReadOnly();
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException)
        {
            _logger.LogWarning(ex, "Error fetching DLC list for AppId={AppId}", appId);
            return [];
        }
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
                if (cached != null) return cached;
            }
            catch { }
        }

        var lazyTask = _inFlightDepotInfo.GetOrAdd(appId, id => new Lazy<Task<SteamAppDepotInfo?>>(() => FetchAppDepotInfoCoreAsync(id, cacheKey, ct)));
        return await lazyTask.Value.ConfigureAwait(false);
    }

    private async Task<SteamAppDepotInfo?> FetchAppDepotInfoCoreAsync(uint appId, string cacheKey, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.steamcmd.net/v1/info/{appId}");
            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("data", out var dataEl) &&
                    dataEl.TryGetProperty(appId.ToString(), out var appEl))
                {
                    string? appType = null;
                    if (appEl.TryGetProperty("common", out var commonEl) &&
                        commonEl.TryGetProperty("type", out var typeProp))
                    {
                        appType = typeProp.GetString();
                    }

                    if (appEl.TryGetProperty("depots", out var depotsEl))
                    {
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

                        var info = new SteamAppDepotInfo(latestDate, buildId, manifests, appType);
                        if (_cache != null)
                        {
                            try { await _cache.SetAsync(cacheKey, info, TimeSpan.FromDays(7), ct).ConfigureAwait(false); } catch { }
                        }
                        return info;
                    }
                    else if (!string.IsNullOrWhiteSpace(appType))
                    {
                        var info = new SteamAppDepotInfo(null, null, new Dictionary<ulong, ulong>(), appType);
                        if (_cache != null)
                        {
                            try { await _cache.SetAsync(cacheKey, info, TimeSpan.FromDays(7), ct).ConfigureAwait(false); } catch { }
                        }
                        return info;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to get SteamCMD AppInfo for AppId={AppId}", appId);
        }
        finally
        {
            _inFlightDepotInfo.TryRemove(appId, out _);
        }

        return null;
    }

    /// <summary>

    /// Gets all available game branches and builds from SteamCMD / Steam app info.
    /// </summary>
    public async Task<IReadOnlyList<GameBuildInfo>> GetAppBuildsAsync(uint appId, CancellationToken ct = default)
    {
        if (appId == 0) return [];

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.steamcmd.net/v1/info/{appId}");
            if (!_http.DefaultRequestHeaders.Contains("User-Agent"))
            {
                request.Headers.UserAgent.ParseAdd("BlueStar/1.2.1");
            }

            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return [];

            var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("data", out var dataEl) ||
                !dataEl.TryGetProperty(appId.ToString(), out var appEl) ||
                !appEl.TryGetProperty("depots", out var depotsEl))
            {
                return [];
            }

            var builds = new List<GameBuildInfo>();

            if (depotsEl.TryGetProperty("branches", out var branchesEl))
            {
                foreach (var branchProp in branchesEl.EnumerateObject())
                {
                    var branchName = branchProp.Name;
                    var branchObj = branchProp.Value;

                    string buildId = string.Empty;
                    if (branchObj.TryGetProperty("buildid", out var bIdProp))
                    {
                        buildId = bIdProp.GetString() ?? string.Empty;
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

                    // Extract depot manifests for this specific branch
                    var depotManifests = new Dictionary<uint, ulong>();
                    foreach (var depotProp in depotsEl.EnumerateObject())
                    {
                        if (uint.TryParse(depotProp.Name, out var depotId) &&
                            depotProp.Value.TryGetProperty("manifests", out var mEl) &&
                            mEl.TryGetProperty(branchName, out var pManEl) &&
                            pManEl.TryGetProperty("gid", out var gidProp))
                        {
                            var gidStr = gidProp.GetString();
                            if (ulong.TryParse(gidStr, out var gid))
                            {
                                depotManifests[depotId] = gid;
                            }
                        }
                    }

                    var displayName = branchName.Equals("public", StringComparison.OrdinalIgnoreCase)
                        ? (string.IsNullOrWhiteSpace(buildId) ? "Latest Public Release" : $"Latest Build {buildId} (public)")
                        : $"{branchName} (Build {buildId})";

                    if (!string.IsNullOrWhiteSpace(desc))
                    {
                        displayName += $" - {desc}";
                    }

                    builds.Add(new GameBuildInfo
                    {
                        BuildId = buildId,
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

            return builds.OrderByDescending(b => b.BranchName == "public")
                         .ThenByDescending(b => b.UpdatedAt ?? DateTimeOffset.MinValue)
                         .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to get SteamCMD branches and builds for AppId={AppId}", appId);
            return [];
        }
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
                if (cached.HasValue) return cached.Value;
            }
            catch { }
        }

        var lazyTask = _inFlightUpdateDates.GetOrAdd(appId, id => new Lazy<Task<DateTimeOffset?>>(() => FetchLatestAppUpdateDateCoreAsync(id, cacheKey, ct)));
        return await lazyTask.Value.ConfigureAwait(false);
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
                    request.Headers.UserAgent.ParseAdd("BlueStar/1.2.1");
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
        finally
        {
            _inFlightUpdateDates.TryRemove(appId, out _);
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
                if (cached != null) return cached;
            }
            catch { }
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.steamcmd.net/v1/info/{appId}");
            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return new Dictionary<uint, SteamDepotMeta>();

            var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty("data", out var dataEl) ||
                !dataEl.TryGetProperty(appId.ToString(), out var appEl) ||
                !appEl.TryGetProperty("depots", out var depotsEl))
                return new Dictionary<uint, SteamDepotMeta>();

            var result = new Dictionary<uint, SteamDepotMeta>();

            foreach (var depotProp in depotsEl.EnumerateObject())
            {
                // Skip non-numeric keys like "branches", "overrides", "baselanguages"
                if (!uint.TryParse(depotProp.Name, out var depotId))
                    continue;

                var depotEl = depotProp.Value;

                // Depot name (may be at root or under "config")
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

                // Some depots mark shared via a top-level flag
                if (!isShared && depotEl.TryGetProperty("sharedinstall", out var si))
                    isShared = si.GetString() == "1" || (si.ValueKind == JsonValueKind.Number && si.GetInt32() == 1);

                result[depotId] = new SteamDepotMeta(depotId, name, osList, isOptional, isShared);
            }

            if (_cache != null && result.Count > 0)
            {
                try { await _cache.SetAsync(cacheKey, result, TimeSpan.FromDays(7), ct).ConfigureAwait(false); } catch { }
            }

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to get depot enrichment for AppId={AppId}", appId);
            return new Dictionary<uint, SteamDepotMeta>();
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
