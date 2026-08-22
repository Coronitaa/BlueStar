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
    private DateTimeOffset _lastRequest = DateTimeOffset.MinValue;

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
    public SteamStoreApiClient(HttpClient http, ILogger<SteamStoreApiClient> logger)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<GameMetadata?> GetMetadataAsync(uint appId, CancellationToken ct = default)
    {
        await ThrottleAsync(ct).ConfigureAwait(false);

        _logger.LogDebug("Fetching Steam metadata for AppId={AppId}", appId);

        try
        {
            var url = $"https://store.steampowered.com/api/appdetails?appids={appId}";
            var response = await _http.GetAsync(url, ct).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Steam API returned {StatusCode} for AppId={AppId}",
                    response.StatusCode, appId);
                return null;
            }

            var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);

            var appKey = appId.ToString();
            if (!doc.RootElement.TryGetProperty(appKey, out var appElement))
                return null;

            if (!appElement.TryGetProperty("success", out var successProp) || !successProp.GetBoolean())
                return null;

            if (!appElement.TryGetProperty("data", out var data))
                return null;

            return new GameMetadata
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
        await ThrottleAsync(ct).ConfigureAwait(false);

        try
        {
            var url = $"https://store.steampowered.com/api/appdetails?appids={appId}";
            var response = await _http.GetAsync(url, ct).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
                return [];

            var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
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

            _logger.LogDebug("Found {Count} DLCs for AppId={AppId}", dlcList.Count, appId);
            return dlcList.AsReadOnly();
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException)
        {
            _logger.LogWarning(ex, "Error fetching DLC list for AppId={AppId}", appId);
            return [];
        }
    }

    /// <inheritdoc />
    public async Task EnrichSearchResultAsync(SearchResult result, CancellationToken ct = default)
    {
        if (result == null || result.AppId == 0) return;

        try
        {
            var url = $"https://store.steampowered.com/api/appdetails?appids={result.AppId}";
            var response = await _http.GetAsync(url, ct).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
                return;

            var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);

            var appKey = result.AppId.ToString();
            if (!doc.RootElement.TryGetProperty(appKey, out var appElement) ||
                !appElement.TryGetProperty("success", out var s) || !s.GetBoolean() ||
                !appElement.TryGetProperty("data", out var data))
            {
                return;
            }

            // 1. DLC Count
            if (data.TryGetProperty("dlc", out var dlcArray) && dlcArray.ValueKind == JsonValueKind.Array)
            {
                result.DlcCount = dlcArray.GetArrayLength();
            }

            // 2. Supported Platforms
            if (data.TryGetProperty("platforms", out var platforms) && platforms.ValueKind == JsonValueKind.Object)
            {
                if (platforms.TryGetProperty("windows", out var winProp) && winProp.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    result.HasWindows = winProp.GetBoolean();

                if (platforms.TryGetProperty("linux", out var linProp) && linProp.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    result.HasLinux = linProp.GetBoolean();

                if (platforms.TryGetProperty("mac", out var macProp) && macProp.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    result.HasMac = macProp.GetBoolean();
            }

            // 3. App Type
            // 3. App Type & Genres Detection (Game vs Application vs Tool)
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

            // 4. Header image
            if (data.TryGetProperty("header_image", out var headerProp) && headerProp.ValueKind == JsonValueKind.String)
            {
                var img = headerProp.GetString();
                if (!string.IsNullOrWhiteSpace(img))
                    result.HeaderImageUrl = img;
            }

            // 5. Version / Latest update date from Steam depot history or Steam News
            try
            {
                var depotInfo = await GetAppDepotInfoAsync(result.AppId, ct).ConfigureAwait(false);

                // Check SteamCMD common.type for override (e.g. Wallpaper Engine returns "Application" in SteamCMD)
                if (!string.IsNullOrWhiteSpace(depotInfo?.AppType))
                {
                    var cmdType = depotInfo.AppType.Trim();
                    if (cmdType.Equals("Application", StringComparison.OrdinalIgnoreCase) ||
                        cmdType.Equals("Software", StringComparison.OrdinalIgnoreCase))
                    {
                        result.AppType = "Application";
                    }
                    else if (cmdType.Equals("Tool", StringComparison.OrdinalIgnoreCase) ||
                             cmdType.Equals("Utility", StringComparison.OrdinalIgnoreCase) ||
                             cmdType.Equals("Config", StringComparison.OrdinalIgnoreCase))
                    {
                        result.AppType = "Tool";
                    }
                }

                if (depotInfo?.LatestBuildDate != null)
                {
                    result.Version = $"{depotInfo.LatestBuildDate.Value.LocalDateTime:d MMM yyyy}";
                }
                else
                {
                    var newsUrl = $"https://api.steampowered.com/ISteamNews/GetNewsForApp/v2/?appid={result.AppId}&count=5&maxlength=300";
                    var request = new HttpRequestMessage(HttpMethod.Get, newsUrl);
                    if (!_http.DefaultRequestHeaders.Contains("User-Agent"))
                    {
                        request.Headers.UserAgent.ParseAdd("BlueStar/0.1.0");
                    }
                    var newsResponse = await _http.SendAsync(request, ct).ConfigureAwait(false);
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
                                    long unix = 0;
                                    if (dateEl.ValueKind == JsonValueKind.Number) dateEl.TryGetInt64(out unix);
                                    else if (dateEl.ValueKind == JsonValueKind.String && long.TryParse(dateEl.GetString(), out var p)) unix = p;
                                    if (unix > maxUnix) maxUnix = unix;
                                }
                            }

                            if (maxUnix > 0)
                            {
                                var updateDate = DateTimeOffset.FromUnixTimeSeconds(maxUnix).LocalDateTime;
                                result.Version = $"{updateDate:d MMM yyyy}";
                            }
                        }
                    }
                }
            }
            catch { }

            // Fallback to release_date if news date is unavailable
            if (string.IsNullOrWhiteSpace(result.Version))
            {
                if (data.TryGetProperty("release_date", out var rd) && rd.TryGetProperty("date", out var dateProp))
                {
                    var dateStr = dateProp.GetString();
                    if (!string.IsNullOrWhiteSpace(dateStr))
                    {
                        result.Version = dateStr;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to enrich search result for AppId={AppId}", result.AppId);
        }
    }

    /// <summary>
    /// Gets detailed depot and branch information from the SteamCMD/SteamDB app info catalog.
    /// </summary>
    public async Task<SteamAppDepotInfo?> GetAppDepotInfoAsync(uint appId, CancellationToken ct = default)
    {
        if (appId == 0) return null;

        try
        {
            var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.steamcmd.net/v1/info/{appId}");
            if (!_http.DefaultRequestHeaders.Contains("User-Agent"))
            {
                request.Headers.UserAgent.ParseAdd("BlueStar/0.1.0");
            }

            var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
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

                        return new SteamAppDepotInfo(latestDate, buildId, manifests, appType);
                    }
                    else if (!string.IsNullOrWhiteSpace(appType))
                    {
                        return new SteamAppDepotInfo(null, null, new Dictionary<ulong, ulong>(), appType);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to get SteamCMD depot info for AppId={AppId}", appId);
        }

        return null;
    }

    /// <summary>
    /// Gets the date of the latest patch or update from Steam depot history or Steam news API.
    /// </summary>
    public async Task<DateTimeOffset?> GetLatestAppUpdateDateAsync(uint appId, CancellationToken ct = default)
    {
        if (appId == 0) return null;

        // 1. Try exact depot/build date first
        var depotInfo = await GetAppDepotInfoAsync(appId, ct).ConfigureAwait(false);
        if (depotInfo?.LatestBuildDate != null)
        {
            return depotInfo.LatestBuildDate;
        }

        // 2. Fallback to Steam News API
        try
        {
            await ThrottleAsync(ct).ConfigureAwait(false);

            var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.steampowered.com/ISteamNews/GetNewsForApp/v2/?appid={appId}&count=5&maxlength=300");
            if (!_http.DefaultRequestHeaders.Contains("User-Agent"))
            {
                request.Headers.UserAgent.ParseAdd("BlueStar/0.1.0");
            }

            var newsResponse = await _http.SendAsync(request, ct).ConfigureAwait(false);
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
                        return DateTimeOffset.FromUnixTimeSeconds(maxUnix);
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

    /// <summary>
    /// Model holding Steam App depot branches and public manifest GIDs.
    /// </summary>
    public sealed record SteamAppDepotInfo(
        DateTimeOffset? LatestBuildDate,
        string? BuildId,
        IReadOnlyDictionary<ulong, ulong> PublicManifests,
        string? AppType = null
    );

    private async Task ThrottleAsync(CancellationToken ct)
    {
        var elapsed = DateTimeOffset.UtcNow - _lastRequest;
        if (elapsed < MinRequestInterval)
        {
            var delay = MinRequestInterval - elapsed;
            await Task.Delay(delay, ct).ConfigureAwait(false);
        }
        _lastRequest = DateTimeOffset.UtcNow;
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
