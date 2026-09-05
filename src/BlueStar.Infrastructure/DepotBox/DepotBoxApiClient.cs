using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using BlueStar.Core.Interfaces;
using BlueStar.Core.Models;
using BlueStar.Infrastructure.Services;
using Microsoft.Extensions.Logging;

namespace BlueStar.Infrastructure.DepotBox;

/// <summary>
/// HTTP client for the DepotBox REST API (depotbox.org).
/// </summary>
public sealed class DepotBoxApiClient : IDepotBoxApiClient
{
    private readonly HttpClient _http;
    private readonly IDepotBoxAuthService _authService;
    private readonly BlueStar.Infrastructure.Storage.AppSettingsService? _appSettings;
    private readonly ICacheService? _cacheService;
    private readonly ILogger<DepotBoxApiClient> _logger;
    private readonly IRequestCoordinator _coordinator;
    private readonly INetworkMetricsObserver? _metrics;

    private IReadOnlyList<GameFixInfo>? _cachedFixesCatalog;
    private DateTimeOffset _catalogExpiresAt = DateTimeOffset.MinValue;
    private Task<IReadOnlyList<GameFixInfo>>? _inFlightCatalogTask;
    private readonly SemaphoreSlim _catalogLock = new(1, 1);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true
    };

    private const int DownloadBufferSize = 81920; // 80 KB

    /// <summary>
    /// Initializes a new instance of the <see cref="DepotBoxApiClient"/> class.
    /// </summary>
    /// <param name="http">Configured HTTP client with BaseAddress set to DepotBox API.</param>
    /// <param name="authService">Authentication service for effective API key resolution.</param>
    /// <param name="logger">Logger instance.</param>
    /// <param name="appSettings">Application settings service for custom API URLs.</param>
    /// <param name="cacheService">Cache service for persistent L1/L2 caching across sessions.</param>
    /// <param name="coordinator">Request coordinator for in-flight deduplication.</param>
    /// <param name="metrics">Network metrics observer.</param>
    public DepotBoxApiClient(
        HttpClient http,
        IDepotBoxAuthService authService,
        ILogger<DepotBoxApiClient> logger,
        BlueStar.Infrastructure.Storage.AppSettingsService? appSettings = null,
        ICacheService? cacheService = null,
        IRequestCoordinator? coordinator = null,
        INetworkMetricsObserver? metrics = null)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _authService = authService ?? throw new ArgumentNullException(nameof(authService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _appSettings = appSettings;
        _cacheService = cacheService;
        _coordinator = coordinator ?? RequestCoordinator.Instance;
        _metrics = metrics;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SearchResult>> SearchGamesAsync(string query, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);

        using var request = await CreateRequestAsync(HttpMethod.Post, "/api/search-games", ct).ConfigureAwait(false);
        request.Content = JsonContent.Create(new { searchTerm = query }, options: JsonOptions);

        using var response = await SendWithErrorHandlingAsync(request, ct).ConfigureAwait(false);
        var jsonString = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        _logger.LogDebug("DepotBox search response: {Json}", jsonString);
        return ParseSearchResults(jsonString);
    }

    /// <inheritdoc />
    public async Task<GameMetadata?> GetGameAsync(uint appId, CancellationToken ct = default, bool forceRefresh = false)
    {
        if (appId == 0) return null;

        var cacheKey = $"depotbox_game_{appId}";
        if (!forceRefresh && _cacheService != null)
        {
            try
            {
                var cached = await _cacheService.GetAsync<GameMetadata>(cacheKey, ct).ConfigureAwait(false);
                if (cached != null)
                {
                    _metrics?.OnCacheHit("DepotBox", $"/api/games/{appId}");
                    return cached;
                }
            }
            catch { }
        }

        return await _coordinator.ExecuteAsync($"depotbox_game_{appId}", async innerCt =>
        {
            if (!forceRefresh && _cacheService != null)
            {
                try
                {
                    var cached = await _cacheService.GetAsync<GameMetadata>(cacheKey, innerCt).ConfigureAwait(false);
                    if (cached != null)
                    {
                        _metrics?.OnCacheHit("DepotBox", $"/api/games/{appId}");
                        return cached;
                    }
                }
                catch { }
            }

            try
            {
                _metrics?.OnProviderRequest("DepotBox", $"/api/games/{appId}");
                using var request = await CreateRequestAsync(HttpMethod.Get, $"/api/games/{appId}", innerCt).ConfigureAwait(false);
                using var response = await SendWithErrorHandlingAsync(request, innerCt).ConfigureAwait(false);
                var jsonString = await response.Content.ReadAsStringAsync(innerCt).ConfigureAwait(false);

                var game = ParseGameDetails(jsonString, appId);
                if (game != null && _cacheService != null)
                {
                    try
                    {
                        await _cacheService.SetAsync(cacheKey, game, TimeSpan.FromDays(30), innerCt).ConfigureAwait(false);
                    }
                    catch { }
                }

                return game;
            }
            catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                _logger.LogDebug("DepotBox returned 404 for game details AppId {AppId}", appId);
                return null;
            }
        }, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ManifestInfo>> GetManifestsAsync(uint appId, CancellationToken ct = default, bool forceRefresh = false)
    {
        if (appId == 0) return [];

        var cacheKey = $"depotbox_manifests_{appId}";
        if (!forceRefresh && _cacheService != null)
        {
            try
            {
                var cached = await _cacheService.GetAsync<List<ManifestInfo>>(cacheKey, ct).ConfigureAwait(false);
                if (cached != null)
                {
                    _logger.LogDebug("DepotBox manifests for AppId {AppId} retrieved from cache ({Count} items)", appId, cached.Count);
                    if (cached.Count == 0)
                    {
                        _metrics?.OnNegativeCacheHit("DepotBox", $"/api/manifests/{appId}");
                    }
                    else
                    {
                        _metrics?.OnCacheHit("DepotBox", $"/api/manifests/{appId}");
                    }
                    return cached;
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to read manifests cache for AppId {AppId}", appId);
            }
        }

        return await _coordinator.ExecuteAsync($"depotbox_manifests_{appId}", async innerCt =>
        {
            if (!forceRefresh && _cacheService != null)
            {
                try
                {
                    var cached = await _cacheService.GetAsync<List<ManifestInfo>>(cacheKey, innerCt).ConfigureAwait(false);
                    if (cached != null)
                    {
                        if (cached.Count == 0)
                        {
                            _metrics?.OnNegativeCacheHit("DepotBox", $"/api/manifests/{appId}");
                        }
                        else
                        {
                            _metrics?.OnCacheHit("DepotBox", $"/api/manifests/{appId}");
                        }
                        return (IReadOnlyList<ManifestInfo>)cached;
                    }
                }
                catch { }
            }

            try
            {
                _metrics?.OnProviderRequest("DepotBox", $"/api/manifests/{appId}");
                using var request = await CreateRequestAsync(HttpMethod.Get, $"/api/manifests/{appId}", innerCt).ConfigureAwait(false);
                using var response = await SendWithErrorHandlingAsync(request, innerCt).ConfigureAwait(false);
                var jsonString = await response.Content.ReadAsStringAsync(innerCt).ConfigureAwait(false);

                var manifests = ParseManifests(jsonString);

                if (_cacheService != null)
                {
                    try
                    {
                        // 7 days for positive manifests, 24 hours for negative caching of empty lists
                        var ttl = manifests.Count > 0 ? TimeSpan.FromDays(7) : TimeSpan.FromHours(24);
                        await _cacheService.SetAsync(cacheKey, manifests.ToList(), ttl, innerCt).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Failed to persist manifests cache for AppId {AppId}", appId);
                    }
                }

                return manifests;
            }
            catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                _logger.LogDebug("DepotBox returned 404 for AppId {AppId}, negative caching empty manifest list for 24h", appId);
                if (_cacheService != null)
                {
                    try { await _cacheService.SetAsync(cacheKey, new List<ManifestInfo>(), TimeSpan.FromHours(24), innerCt).ConfigureAwait(false); } catch { }
                }
                return (IReadOnlyList<ManifestInfo>)[];
            }
        }, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<bool> CheckAvailabilityAsync(uint appId, CancellationToken ct = default, bool forceRefresh = false)
    {
        if (appId == 0) return false;

        var cacheKey = $"depotbox_avail_{appId}";
        if (!forceRefresh && _cacheService != null)
        {
            try
            {
                var cached = await _cacheService.GetAsync<bool?>(cacheKey, ct).ConfigureAwait(false);
                if (cached.HasValue)
                {
                    _metrics?.OnCacheHit("DepotBox", $"/api/games/{appId}/availability");
                    return cached.Value;
                }
            }
            catch { }
        }

        return await _coordinator.ExecuteAsync($"depotbox_avail_{appId}", async innerCt =>
        {
            if (!forceRefresh && _cacheService != null)
            {
                try
                {
                    var cached = await _cacheService.GetAsync<bool?>(cacheKey, innerCt).ConfigureAwait(false);
                    if (cached.HasValue)
                    {
                        _metrics?.OnCacheHit("DepotBox", $"/api/games/{appId}/availability");
                        return cached.Value;
                    }
                }
                catch { }
            }

            try
            {
                _metrics?.OnProviderRequest("DepotBox", $"/api/games/{appId}/availability");
                using var request = await CreateRequestAsync(HttpMethod.Get, $"/api/games/{appId}/availability", innerCt).ConfigureAwait(false);
                using var response = await SendWithErrorHandlingAsync(request, innerCt).ConfigureAwait(false);
                var jsonString = await response.Content.ReadAsStringAsync(innerCt).ConfigureAwait(false);

                bool isAvail = false;
                try
                {
                    using var doc = JsonDocument.Parse(jsonString);
                    var root = doc.RootElement;
                    if (TryGetBool(root, out var avail, "isAvailable", "available", "is_available", "success"))
                        isAvail = avail;
                }
                catch { }

                if (_cacheService != null)
                {
                    try
                    {
                        await _cacheService.SetAsync(cacheKey, (bool?)isAvail, TimeSpan.FromDays(30), innerCt).ConfigureAwait(false);
                    }
                    catch { }
                }

                return isAvail;
            }
            catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                if (_cacheService != null)
                {
                    try { await _cacheService.SetAsync(cacheKey, (bool?)false, TimeSpan.FromDays(7), innerCt).ConfigureAwait(false); } catch { }
                }
                return false;
            }
        }, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IDictionary<uint, bool>> BatchCheckAvailabilityAsync(IEnumerable<uint> appIds, CancellationToken ct = default, bool forceRefresh = false)
    {
        var idList = appIds.ToList();
        if (idList.Count == 0)
            return new Dictionary<uint, bool>();

        var result = new Dictionary<uint, bool>();
        var pendingIds = new List<uint>();

        if (!forceRefresh && _cacheService != null)
        {
            foreach (var id in idList)
            {
                var cacheKey = $"depotbox_avail_{id}";
                try
                {
                    var cached = await _cacheService.GetAsync<bool?>(cacheKey, ct).ConfigureAwait(false);
                    if (cached.HasValue)
                    {
                        result[id] = cached.Value;
                        continue;
                    }
                }
                catch { }
                pendingIds.Add(id);
            }
        }
        else
        {
            pendingIds = idList;
        }

        if (pendingIds.Count == 0)
            return result;

        using var request = await CreateRequestAsync(HttpMethod.Post, "/api/games/batch-availability", ct).ConfigureAwait(false);
        request.Content = JsonContent.Create(new { appids = pendingIds }, options: JsonOptions);

        using var response = await SendWithErrorHandlingAsync(request, ct).ConfigureAwait(false);
        var jsonString = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        try
        {
            using var doc = JsonDocument.Parse(jsonString);
            var root = doc.RootElement;

            JsonElement objEl = root;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object)
            {
                objEl = data;
            }

            if (objEl.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in objEl.EnumerateObject())
                {
                    if (uint.TryParse(prop.Name, out var parsedAppId))
                    {
                        bool avail = false;
                        if (prop.Value.ValueKind == JsonValueKind.True) avail = true;
                        else if (prop.Value.ValueKind == JsonValueKind.False) avail = false;
                        else if (prop.Value.ValueKind == JsonValueKind.Number) avail = prop.Value.GetInt32() != 0;
                        else if (prop.Value.ValueKind == JsonValueKind.Object && TryGetBool(prop.Value, out var a, "isAvailable", "available"))
                            avail = a;

                        result[parsedAppId] = avail;
                        if (_cacheService != null)
                        {
                            _ = _cacheService.SetAsync($"depotbox_avail_{parsedAppId}", (bool?)avail, TimeSpan.FromDays(30), ct);
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse batch availability response");
        }

        return result;
    }

    /// <inheritdoc />
    public void InvalidateAppCache(uint appId)
    {
        if (appId == 0 || _cacheService == null) return;
        try
        {
            _ = _cacheService.RemoveAsync($"depotbox_manifests_{appId}");
            _ = _cacheService.RemoveAsync($"depotbox_avail_{appId}");
            _ = _cacheService.RemoveAsync($"depotbox_game_{appId}");
            _logger.LogDebug("Invalidated DepotBox cache for AppId {AppId}", appId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to invalidate DepotBox cache for AppId {AppId}", appId);
        }
    }

    /// <inheritdoc />
    public async Task<string> DownloadArchiveAsync(uint appId, string targetPath, IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);

        _logger.LogInformation("Starting direct download for AppId={AppId}", appId);

        using var request = await CreateRequestAsync(HttpMethod.Get, $"/api/direct-download?appid={appId}", ct).ConfigureAwait(false);

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response, ct).ConfigureAwait(false);

        var totalBytes = response.Content.Headers.ContentLength ?? -1;
        var filePath = Path.Combine(targetPath, $"{appId}.zip");

        Directory.CreateDirectory(targetPath);

        await using var contentStream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var fileStream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None, DownloadBufferSize, useAsync: true);

        var buffer = new byte[DownloadBufferSize];
        long totalRead = 0;
        int bytesRead;
        var lastReport = DateTimeOffset.MinValue;

        while ((bytesRead = await contentStream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), ct).ConfigureAwait(false);
            totalRead += bytesRead;

            // Report progress at most every 250ms
            if (progress is not null && DateTimeOffset.UtcNow - lastReport > TimeSpan.FromMilliseconds(250))
            {
                var pct = totalBytes > 0 ? (double)totalRead / totalBytes * 100.0 : 0;
                progress.Report(new DownloadProgress
                {
                    TotalBytes = totalBytes,
                    DownloadedBytes = totalRead,
                    Percentage = pct,
                    CurrentFile = $"{appId}.zip"
                });
                lastReport = DateTimeOffset.UtcNow;
            }
        }

        _logger.LogInformation("Download complete: {Path} ({Bytes} bytes)", filePath, totalRead);
        return filePath;
    }

    /// <inheritdoc />
    public async Task<string> StartAsyncDownloadAsync(uint appId, CancellationToken ct)
    {
        using var request = await CreateRequestAsync(HttpMethod.Post, "/api/download", ct).ConfigureAwait(false);
        request.Content = JsonContent.Create(new { appid = appId }, options: JsonOptions);

        using var response = await SendWithErrorHandlingAsync(request, ct).ConfigureAwait(false);
        var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        string? token = null;
        if (root.ValueKind == JsonValueKind.Object)
        {
            token = TryGetString(root, "token", "downloadToken", "download_token", "id");
            if (token is null && root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object)
            {
                token = TryGetString(data, "token", "downloadToken", "download_token", "id");
            }
        }
        else if (root.ValueKind == JsonValueKind.String)
        {
            token = root.GetString();
        }

        return token ?? throw new InvalidOperationException($"No download token received from API. Response: {json}");
    }

    /// <inheritdoc />
    public async Task<DownloadProgress> CheckDownloadStatusAsync(string downloadToken, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(downloadToken);

        using var request = await CreateRequestAsync(HttpMethod.Get, $"/api/status/{downloadToken}", ct).ConfigureAwait(false);

        using var response = await SendWithErrorHandlingAsync(request, ct).ConfigureAwait(false);
        var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        JsonElement target = root;
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object)
        {
            target = data;
        }

        TryGetLong(target, out var totalBytes, "totalBytes", "total_bytes", "size", "total");
        TryGetLong(target, out var downloadedBytes, "downloadedBytes", "downloaded_bytes", "progress", "downloaded");
        TryGetDouble(target, out var percentage, "percentage", "percent", "pct");
        var currentFile = TryGetString(target, "currentFile", "current_file", "file", "status");

        if (percentage <= 0 && totalBytes > 0)
        {
            percentage = (double)downloadedBytes / totalBytes * 100.0;
        }

        return new DownloadProgress
        {
            TotalBytes = totalBytes,
            DownloadedBytes = downloadedBytes,
            Percentage = percentage,
            CurrentFile = currentFile
        };
    }

    /// <inheritdoc />
    public async Task<string> DownloadCompletedArchiveAsync(string downloadToken, string targetPath, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(downloadToken);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);

        using var request = await CreateRequestAsync(HttpMethod.Get, $"/api/download/{downloadToken}", ct).ConfigureAwait(false);

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response, ct).ConfigureAwait(false);

        var filePath = Path.Combine(targetPath, $"download_{downloadToken}.zip");
        Directory.CreateDirectory(targetPath);

        await using var contentStream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var fileStream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None, DownloadBufferSize, useAsync: true);
        await contentStream.CopyToAsync(fileStream, ct).ConfigureAwait(false);

        return filePath;
    }

    /// <summary>
    /// Retrieves the entire game fixes catalog from DepotBox, using L1 memory cache and single-flight request deduplication.
    /// </summary>
    public async Task<IReadOnlyList<GameFixInfo>> GetFullGameFixesCatalogAsync(CancellationToken ct = default)
    {
        if (_cachedFixesCatalog != null && DateTimeOffset.UtcNow < _catalogExpiresAt)
        {
            return _cachedFixesCatalog;
        }

        Task<IReadOnlyList<GameFixInfo>> task;
        await _catalogLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_cachedFixesCatalog != null && DateTimeOffset.UtcNow < _catalogExpiresAt)
            {
                return _cachedFixesCatalog;
            }

            if (_inFlightCatalogTask == null)
            {
                _inFlightCatalogTask = FetchFullGameFixesCatalogCoreAsync(ct);
            }
            task = _inFlightCatalogTask;
        }
        finally
        {
            _catalogLock.Release();
        }

        return await task.ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<GameFixInfo>> FetchFullGameFixesCatalogCoreAsync(CancellationToken ct)
    {
        try
        {
            using var requestAll = await CreateRequestAsync(HttpMethod.Get, "/api/game-fixes", ct).ConfigureAwait(false);
            using var responseAll = await SendWithErrorHandlingAsync(requestAll, ct).ConfigureAwait(false);
            var allJson = await responseAll.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var allFixes = ParseGameFixes(allJson);
            _cachedFixesCatalog = allFixes;
            _catalogExpiresAt = DateTimeOffset.UtcNow.AddMinutes(30);
            return allFixes;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed querying full game fixes list");
            return _cachedFixesCatalog ?? [];
        }
        finally
        {
            await _catalogLock.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                _inFlightCatalogTask = null;
            }
            finally
            {
                _catalogLock.Release();
            }
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<GameFixInfo>> GetGameFixesAsync(string? query = null, string? tags = null, CancellationToken ct = default)
    {
        var queryParams = new List<string>();
        if (!string.IsNullOrWhiteSpace(query))
        {
            queryParams.Add($"q={Uri.EscapeDataString(query.Trim())}");
        }
        if (!string.IsNullOrWhiteSpace(tags))
        {
            queryParams.Add($"tag={Uri.EscapeDataString(tags.Trim())}");
        }

        var endpoint = "/api/game-fixes" + (queryParams.Count > 0 ? "?" + string.Join("&", queryParams) : "");
        try
        {
            using var request = await CreateRequestAsync(HttpMethod.Get, endpoint, ct).ConfigureAwait(false);
            using var response = await SendWithErrorHandlingAsync(request, ct).ConfigureAwait(false);
            var jsonString = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            _logger.LogDebug("DepotBox game fixes response: {Json}", jsonString);
            var fixes = ParseGameFixes(jsonString);
            if (fixes.Count > 0) return fixes;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed querying game fixes at {Endpoint}", endpoint);
        }

        // Fallback: If querying with parameter returned nothing, query cached catalog and match locally
        if (!string.IsNullOrWhiteSpace(query))
        {
            var allFixes = await GetFullGameFixesCatalogAsync(ct).ConfigureAwait(false);
            var q = query.Trim();
            var filtered = allFixes.Where(f =>
                f.Id.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                f.Name.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                f.DownloadName.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                (f.Description != null && f.Description.Contains(q, StringComparison.OrdinalIgnoreCase)) ||
                f.Tags.Any(t => t.Contains(q, StringComparison.OrdinalIgnoreCase))
            ).ToList();
            if (filtered.Count > 0) return filtered.AsReadOnly();
        }

        return [];
    }

    /// <inheritdoc />
    public async Task<string> DownloadGameFixAsync(
        string fixIdOrFilename,
        string targetPath,
        IProgress<DownloadProgress>? progress = null,
        string? downloadName = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fixIdOrFilename);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);

        _logger.LogInformation("Starting download for GameFix={Fix} (DownloadName={DownloadName})", fixIdOrFilename, downloadName);

        HttpResponseMessage? response = null;
        if (fixIdOrFilename.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            fixIdOrFilename.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            using var directReq = new HttpRequestMessage(HttpMethod.Get, fixIdOrFilename);
            response = await _http.SendAsync(directReq, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            await EnsureSuccessAsync(response, ct).ConfigureAwait(false);
        }
        else
        {
            var candidateUrls = new List<string>();

            if (!string.IsNullOrWhiteSpace(downloadName))
            {
                var cleanDl = downloadName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
                    ? downloadName
                    : $"{downloadName}.zip";
                candidateUrls.Add($"/api/game-fixes/download?file={Uri.EscapeDataString(cleanDl)}");

                var cleanDlId = downloadName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
                    ? Path.GetFileNameWithoutExtension(downloadName)
                    : downloadName;
                candidateUrls.Add($"/api/game-fixes/download?id={Uri.EscapeDataString(cleanDlId)}");
            }

            var cleanFixFilename = fixIdOrFilename.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
                ? fixIdOrFilename
                : $"{fixIdOrFilename}.zip";
            var cleanFixId = fixIdOrFilename.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
                ? Path.GetFileNameWithoutExtension(fixIdOrFilename)
                : fixIdOrFilename;

            candidateUrls.Add($"/api/game-fixes/download?file={Uri.EscapeDataString(cleanFixFilename)}");
            candidateUrls.Add($"/api/game-fixes/download?id={Uri.EscapeDataString(cleanFixId)}");

            var uniqueEndpoints = candidateUrls.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            Exception? lastEx = null;

            foreach (var ep in uniqueEndpoints)
            {
                try
                {
                    using var req = await CreateRequestAsync(HttpMethod.Get, ep, ct).ConfigureAwait(false);
                    var candidateResp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

                    if (candidateResp.IsSuccessStatusCode || (int)candidateResp.StatusCode is 301 or 302 or 303 or 307 or 308)
                    {
                        response = candidateResp;
                        break;
                    }

                    var statusCode = (int)candidateResp.StatusCode;
                    var reason = candidateResp.ReasonPhrase;
                    candidateResp.Dispose();

                    if (statusCode is 500 or 502 or 503 or 504)
                    {
                        lastEx = new HttpRequestException($"DepotBox Server Error ({statusCode} {reason}): The upstream DepotBox server timed out or failed to stream '{cleanFixFilename}'. The archive on DepotBox is very large and their server is failing to deliver it.");
                    }
                    else if (statusCode == 401)
                    {
                        lastEx = new HttpRequestException("DepotBox Authentication Error (401 Unauthorized): API Key is invalid or expired.");
                    }
                    else if (statusCode == 404)
                    {
                        lastEx = new HttpRequestException($"Fix archive '{cleanFixFilename}' was not found on DepotBox (404 Not Found).");
                    }
                    else
                    {
                        lastEx = new HttpRequestException($"DepotBox returned HTTP {statusCode} ({reason}) for '{cleanFixFilename}'.");
                    }
                }
                catch (HttpRequestException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    lastEx = ex;
                    _logger.LogWarning(ex, "Failed attempting game fix download endpoint {Endpoint}", ep);
                }
            }

            if (response is null)
            {
                throw lastEx ?? new HttpRequestException($"Could not download game fix '{cleanFixFilename}' from DepotBox.");
            }
        }

        // Handle possible 3xx redirects if not auto-followed
        if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308 && response.Headers.Location != null)
        {
            var redirectUri = response.Headers.Location.IsAbsoluteUri
                ? response.Headers.Location
                : new Uri(new Uri(_appSettings?.DefaultApiUrl ?? "https://depotbox.org"), response.Headers.Location);

            response.Dispose();
            using var redirectReq = new HttpRequestMessage(HttpMethod.Get, redirectUri);
            response = await _http.SendAsync(redirectReq, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            await EnsureSuccessAsync(response, ct).ConfigureAwait(false);
        }

        using (response)
        {
            var totalBytes = response.Content.Headers.ContentLength ?? -1;

            // Determine destination file name
            var filename = fixIdOrFilename.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
                ? fixIdOrFilename
                : $"{fixIdOrFilename}.zip";

            if (response.Content.Headers.ContentDisposition?.FileName != null)
            {
                var serverFileName = response.Content.Headers.ContentDisposition.FileName.Trim('\"', '\'');
                if (!string.IsNullOrWhiteSpace(serverFileName))
                {
                    filename = serverFileName;
                }
            }

            var filePath = Path.HasExtension(targetPath) && targetPath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
                ? targetPath
                : Path.Combine(targetPath, filename);

            var dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            try
            {
                await using var contentStream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                await using var fileStream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None, DownloadBufferSize, useAsync: true);

                var buffer = new byte[DownloadBufferSize];
                long totalRead = 0;
                int bytesRead;
                var lastReport = DateTimeOffset.MinValue;

                while ((bytesRead = await contentStream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), ct).ConfigureAwait(false);
                    totalRead += bytesRead;

                    if (progress is not null && DateTimeOffset.UtcNow - lastReport > TimeSpan.FromMilliseconds(250))
                    {
                        var pct = totalBytes > 0 ? (double)totalRead / totalBytes * 100.0 : 0;
                        progress.Report(new DownloadProgress
                        {
                            TotalBytes = totalBytes,
                            DownloadedBytes = totalRead,
                            Percentage = pct,
                            CurrentFile = Path.GetFileName(filePath)
                        });
                        lastReport = DateTimeOffset.UtcNow;
                    }
                }

                _logger.LogInformation("GameFix download complete: {Path} ({Bytes} bytes)", filePath, totalRead);
                return filePath;
            }
            catch
            {
                try { if (File.Exists(filePath)) File.Delete(filePath); } catch { }
                throw;
            }
        }
    }

    public static IReadOnlyList<GameFixInfo> ParseGameFixes(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var list = new List<GameFixInfo>();

            if (root.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in root.EnumerateArray())
                {
                    ProcessGameFixEntry(el, list);
                }
            }
            else if (root.ValueKind == JsonValueKind.Object)
            {
                // DepotBox API returns: { "success": true, "count": 1, "games": [ { "appid": "...", "name": "...", "fixes": [...] } ] }
                if (root.TryGetProperty("games", out var gamesArr) && gamesArr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var gameEl in gamesArr.EnumerateArray())
                    {
                        ProcessGameFixEntry(gameEl, list);
                    }
                }
                else if (root.TryGetProperty("fixes", out var fixesArr) && fixesArr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var fixEl in fixesArr.EnumerateArray())
                    {
                        var fix = ParseGameFixElement(fixEl);
                        if (fix != null) list.Add(fix);
                    }
                }
                else if (root.TryGetProperty("data", out var dataArr) && dataArr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var el in dataArr.EnumerateArray())
                    {
                        ProcessGameFixEntry(el, list);
                    }
                }
                else
                {
                    ProcessGameFixEntry(root, list);
                }
            }

            return list.AsReadOnly();
        }
        catch
        {
            return [];
        }
    }

    private static void ProcessGameFixEntry(JsonElement el, List<GameFixInfo> list)
    {
        if (el.ValueKind != JsonValueKind.Object) return;

        // If the element has a nested "fixes" array (standard DepotBox /api/game-fixes structure)
        if (el.TryGetProperty("fixes", out var fixesEl) && fixesEl.ValueKind == JsonValueKind.Array)
        {
            var gameName = TryGetString(el, "name", "gameName", "game_name", "title");
            var appIdStr = TryGetString(el, "appid", "appId", "id");

            foreach (var fixItem in fixesEl.EnumerateArray())
            {
                var fix = ParseGameFixElement(fixItem, gameName, appIdStr);
                if (fix != null) list.Add(fix);
            }
            return;
        }

        // Direct single fix object fallback
        var singleFix = ParseGameFixElement(el);
        if (singleFix != null) list.Add(singleFix);
    }

    private static GameFixInfo? ParseGameFixElement(JsonElement el, string? contextGameName = null, string? contextAppId = null)
    {
        if (el.ValueKind != JsonValueKind.Object) return null;

        var id = TryGetString(el, "id", "fixId", "fix_id", "slug", "filename", "file", "downloadName");
        var downloadName = TryGetString(el, "downloadName", "download_name", "filename", "file", "downloadFilename") ?? id;

        if (string.IsNullOrWhiteSpace(id) && string.IsNullOrWhiteSpace(downloadName)) return null;

        id ??= downloadName!;
        downloadName ??= $"{id}.zip";

        if (!downloadName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) &&
            !downloadName.EndsWith(".rar", StringComparison.OrdinalIgnoreCase) &&
            !downloadName.EndsWith(".7z", StringComparison.OrdinalIgnoreCase))
        {
            downloadName += ".zip";
        }

        // Parse tags and badges
        var tagsList = new List<string>();

        // 1. Badges array (e.g. ["Bypass"], ["Online", "Tested"])
        if (el.TryGetProperty("badges", out var badgesEl) && badgesEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var b in badgesEl.EnumerateArray())
            {
                if (b.ValueKind == JsonValueKind.String && b.GetString() is string s && !string.IsNullOrWhiteSpace(s))
                    tagsList.Add(s.Trim());
            }
        }

        // 2. Tags array / string (e.g. ["bypass"], ["online"])
        if (el.TryGetProperty("tags", out var tagsEl))
        {
            if (tagsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var t in tagsEl.EnumerateArray())
                {
                    if (t.ValueKind == JsonValueKind.String && t.GetString() is string s && !string.IsNullOrWhiteSpace(s))
                        tagsList.Add(s.Trim());
                }
            }
            else if (tagsEl.ValueKind == JsonValueKind.String && tagsEl.GetString() is string s)
            {
                tagsList.AddRange(s.Split(new[] { ',', ';', '|', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            }
        }

        var rawType = TryGetString(el, "type", "fixType", "fix_type", "category", "kind");
        if (!string.IsNullOrWhiteSpace(rawType))
        {
            tagsList.Add(rawType.Trim());
        }

        var description = TryGetString(el, "description", "notes", "summary", "info");

        // Infer tags from filename, id, name, description, contextGameName
        var combined = $"{id} {downloadName} {description} {contextGameName}".ToLowerInvariant();
        if (combined.Contains("bypass")) tagsList.Add("Bypass");
        if (combined.Contains("hypervisor")) tagsList.Add("Hypervisor");
        if (combined.Contains("online") || combined.Contains("onlinefix")) tagsList.Add("Online");
        if (combined.Contains("refix")) tagsList.Add("ReFix");
        if (combined.Contains("goldberg")) tagsList.Add("Goldberg");
        if (combined.Contains("steamless")) tagsList.Add("Steamless");

        // Determine user-friendly fix display name (without redundant 'Online' or file extensions)
        var rawName = TryGetString(el, "name", "gameName", "game_name", "title");
        if (string.IsNullOrWhiteSpace(rawName))
        {
            rawName = !string.IsNullOrWhiteSpace(contextGameName)
                ? contextGameName
                : Path.GetFileNameWithoutExtension(downloadName);
        }

        var name = CleanGameFixName(rawName, contextGameName);

        var sizeBytes = ParseSizeBytes(el);
        var downloadUrl = TryGetString(el, "url", "downloadUrl", "download_url", "link");

        return new GameFixInfo
        {
            Id = id,
            Name = name,
            DownloadName = downloadName,
            Tags = tagsList.Distinct(StringComparer.OrdinalIgnoreCase).ToList().AsReadOnly(),
            SizeBytes = sizeBytes,
            Description = description ?? (!string.IsNullOrWhiteSpace(contextGameName) ? $"DepotBox specific fix for {contextGameName}." : "DepotBox game fix archive."),
            DownloadUrl = downloadUrl
        };
    }

    public static string CleanGameFixName(string? rawName, string? contextGameName = null)
    {
        if (string.IsNullOrWhiteSpace(rawName))
        {
            return !string.IsNullOrWhiteSpace(contextGameName) ? contextGameName.Trim() : "Game Fix";
        }

        var name = rawName.Trim();

        // 1. Remove leading AppID bracket or number prefix e.g. "[4704690]_", "[286160] "
        name = System.Text.RegularExpressions.Regex.Replace(name, @"^\[\d+\]_?\s*", "");

        // 2. Remove common archive file extensions (.rar, .zip, .7z, .tar, .gz)
        name = System.Text.RegularExpressions.Regex.Replace(name, @"\.(rar|zip|7z|tar|gz)\b", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        // 3. Replace underscores with spaces
        name = name.Replace('_', ' ');

        // 4. Remove redundant category/type suffixes like "(Online)", "(Online Fix)", "(Bypass)", "(Hypervisor)", "[Online]"
        name = System.Text.RegularExpressions.Regex.Replace(
            name,
            @"\s*[\(\[](Online(\s*Fix)?|Bypass|Hypervisor)[\)\]]\s*",
            "",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        // 5. Replace multiple spaces with a single space
        name = System.Text.RegularExpressions.Regex.Replace(name, @"\s+", " ").Trim();

        if (string.IsNullOrWhiteSpace(name))
        {
            return !string.IsNullOrWhiteSpace(contextGameName) ? contextGameName.Trim() : "Game Fix";
        }

        return name;
    }

    private static long? ParseSizeBytes(JsonElement el)
    {
        if (TryGetLong(el, out var numericSize, "sizeBytes", "size_bytes", "fileSize", "file_size"))
        {
            return numericSize;
        }

        if (el.TryGetProperty("size", out var sizeEl))
        {
            if (sizeEl.ValueKind == JsonValueKind.Number && sizeEl.TryGetInt64(out var num))
            {
                return num;
            }
            if (sizeEl.ValueKind == JsonValueKind.String && sizeEl.GetString() is string str)
            {
                var s = str.Trim();
                if (s.EndsWith("GB", StringComparison.OrdinalIgnoreCase) && double.TryParse(s[..^2].Trim(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var gb))
                {
                    return (long)(gb * 1024 * 1024 * 1024);
                }
                if (s.EndsWith("MB", StringComparison.OrdinalIgnoreCase) && double.TryParse(s[..^2].Trim(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var mb))
                {
                    return (long)(mb * 1024 * 1024);
                }
                if (s.EndsWith("KB", StringComparison.OrdinalIgnoreCase) && double.TryParse(s[..^2].Trim(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var kb))
                {
                    return (long)(kb * 1024);
                }
                if (s.EndsWith("B", StringComparison.OrdinalIgnoreCase) && double.TryParse(s[..^1].Trim(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var b))
                {
                    return (long)b;
                }
            }
        }
        return null;
    }

    // --- Private helpers & Flexible Parsers ---

    public static IReadOnlyList<SearchResult> ParseSearchResults(string jsonString)
    {
        if (string.IsNullOrWhiteSpace(jsonString))
            return [];

        using var doc = JsonDocument.Parse(jsonString);
        var root = doc.RootElement;
        var list = new List<SearchResult>();

        if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (var element in root.EnumerateArray())
            {
                var item = ParseSearchResultElement(element);
                if (item != null) list.Add(item);
            }
        }
        else if (root.ValueKind == JsonValueKind.Object)
        {
            // Look for array properties: "games", "data", "results", "apps", "items", "list"
            bool foundArray = false;
            foreach (var propName in new[] { "games", "data", "results", "apps", "items", "list", "result" })
            {
                if (root.TryGetProperty(propName, out var arrayProp) && arrayProp.ValueKind == JsonValueKind.Array)
                {
                    foundArray = true;
                    foreach (var element in arrayProp.EnumerateArray())
                    {
                        var item = ParseSearchResultElement(element);
                        if (item != null) list.Add(item);
                    }
                    break;
                }
            }

            if (!foundArray)
            {
                // Check if the object contains dictionary of appId -> game or is a single game object
                if (root.TryGetProperty("appId", out _) || root.TryGetProperty("appid", out _) || root.TryGetProperty("app_id", out _) || root.TryGetProperty("id", out _))
                {
                    var single = ParseSearchResultElement(root);
                    if (single != null) list.Add(single);
                }
                else
                {
                    // Check if properties themselves are game objects (e.g. "1966720": { "name": "Lethal Company" })
                    foreach (var prop in root.EnumerateObject())
                    {
                        if (prop.Value.ValueKind == JsonValueKind.Object)
                        {
                            var item = ParseSearchResultElement(prop.Value, uint.TryParse(prop.Name, out var idKey) ? idKey : 0);
                            if (item != null) list.Add(item);
                        }
                    }
                }
            }
        }

        return list.AsReadOnly();
    }

    private static SearchResult? ParseSearchResultElement(JsonElement el, uint fallbackAppId = 0)
    {
        if (el.ValueKind != JsonValueKind.Object) return null;

        uint appId = fallbackAppId;
        if (TryGetUint(el, out var parsedId, "appId", "appid", "app_id", "id", "gameId", "game_id"))
        {
            appId = parsedId;
        }

        if (appId == 0) return null;

        string name = TryGetString(el, "name", "gameName", "game_name", "title", "appName", "app_name") ?? $"App {appId}";

        // ── 1. Type & Category Detection ──
        string? rawType = TryGetString(el, "type", "appType", "app_type", "category", "kind");
        var typeLower = (rawType ?? "").Trim().ToLowerInvariant();

        bool isExplicitDlc = typeLower is "dlc" or "dlcs" or "expansion";
        if (TryGetBool(el, out var dlcFlag, "isDlc", "is_dlc", "is_expansion"))
        {
            if (dlcFlag) isExplicitDlc = true;
        }

        bool isRedistributable = typeLower is "redistributable" or "redist" or "commonredist" or "depot" or "config";
        bool isOtherNonApp = typeLower is "music" or "soundtrack" or "video" or "series" or "episode" or "guide" or "hardware" or "demo";

        // Check common naming patterns for DLCs, Soundtracks, and Redistributables
        var nameLower = name.ToLowerInvariant();
        if (nameLower.Contains("steamworks common redistributables") ||
            nameLower.Contains("vcredist") ||
            nameLower.Contains("visual c++") ||
            nameLower.Contains("directx redist") ||
            nameLower.Contains("microsoft .net framework") ||
            nameLower.StartsWith("redistributable") ||
            nameLower.Contains("common redist"))
        {
            isRedistributable = true;
        }

        if (nameLower.EndsWith(" dlc") ||
            nameLower.StartsWith("dlc - ") ||
            nameLower.StartsWith("dlc: ") ||
            nameLower.Contains(" season pass") ||
            nameLower.Contains(" expansion pack") ||
            nameLower.Contains(" soundtrack") ||
            nameLower.EndsWith(" ost") ||
            nameLower.Contains(" original soundtrack") ||
            nameLower.Contains(" artbook") ||
            nameLower.Contains(" digital art book"))
        {
            isExplicitDlc = true;
        }

        // Filter out if not a Game or Application
        if (isExplicitDlc || isRedistributable || isOtherNonApp)
        {
            return null;
        }

        // Determine friendly AppType ("Game" vs "Application" / "Program")
        string appType = typeLower switch
        {
            "application" or "program" or "software" or "tool" or "utility" => "Application",
            _ => "Game"
        };

        bool isAvailable = true;
        if (TryGetBool(el, out var avail, "isAvailable", "is_available", "available", "is_ready", "ready"))
        {
            isAvailable = avail;
        }

        // ── 2. DLC Count ──
        int? dlcCount = null;
        if (TryGetInt(el, out var dlcs, "dlcCount", "dlc_count", "dlcs", "totalDlcs"))
        {
            dlcCount = dlcs;
        }
        else if (el.TryGetProperty("dlcs", out var dlcArr) && dlcArr.ValueKind == JsonValueKind.Array)
        {
            dlcCount = dlcArr.GetArrayLength();
        }

        // ── 3. Supported Operating Systems ──
        bool hasWindows = true;
        bool hasLinux = false;
        bool hasMac = false;

        if (el.TryGetProperty("platforms", out var platEl))
        {
            if (platEl.ValueKind == JsonValueKind.Object)
            {
                if (TryGetBool(platEl, out var w, "windows", "win")) hasWindows = w;
                if (TryGetBool(platEl, out var l, "linux", "steamos")) hasLinux = l;
                if (TryGetBool(platEl, out var m, "mac", "macos", "osx")) hasMac = m;
            }
            else if (platEl.ValueKind == JsonValueKind.Array)
            {
                hasWindows = false;
                foreach (var p in platEl.EnumerateArray())
                {
                    var pStr = p.GetString()?.ToLowerInvariant() ?? "";
                    if (pStr.Contains("win")) hasWindows = true;
                    if (pStr.Contains("linux") || pStr.Contains("steamos")) hasLinux = true;
                    if (pStr.Contains("mac") || pStr.Contains("osx")) hasMac = true;
                }
            }
            else if (platEl.ValueKind == JsonValueKind.String)
            {
                var pStr = platEl.GetString()?.ToLowerInvariant() ?? "";
                hasWindows = pStr.Contains("win");
                hasLinux = pStr.Contains("linux") || pStr.Contains("steamos");
                hasMac = pStr.Contains("mac") || pStr.Contains("osx");
            }
        }
        else if (el.TryGetProperty("os", out var osEl))
        {
            if (osEl.ValueKind == JsonValueKind.Array)
            {
                hasWindows = false;
                foreach (var o in osEl.EnumerateArray())
                {
                    var oStr = o.GetString()?.ToLowerInvariant() ?? "";
                    if (oStr.Contains("win")) hasWindows = true;
                    if (oStr.Contains("linux") || oStr.Contains("steamos")) hasLinux = true;
                    if (oStr.Contains("mac") || oStr.Contains("osx")) hasMac = true;
                }
            }
            else if (osEl.ValueKind == JsonValueKind.String)
            {
                var oStr = osEl.GetString()?.ToLowerInvariant() ?? "";
                hasWindows = oStr.Contains("win");
                hasLinux = oStr.Contains("linux") || oStr.Contains("steamos");
                hasMac = oStr.Contains("mac") || oStr.Contains("osx");
            }
        }

        // ── 4. Version / Build information ──
        string? version = TryGetString(el, "version", "buildId", "build_id", "build", "gameVersion", "game_version");
        if (string.IsNullOrWhiteSpace(version))
        {
            if (TryGetUint(el, out var bId, "buildId", "build_id", "build"))
            {
                version = $"Build {bId}";
            }
        }

        // ── 5. Banner / Header Image URL ──
        string? headerImage = TryGetString(el, "headerImageUrl", "header_image_url", "headerImage", "header_image", "imageUrl", "image_url", "image", "banner");
        if (string.IsNullOrWhiteSpace(headerImage))
        {
            headerImage = $"https://cdn.cloudflare.steamstatic.com/steam/apps/{appId}/header.jpg";
        }

        // ── 6. Specific Tags & Emulators (e.g. BYPASS, ONLINE, REFIX) ──
        var tags = new List<string>();
        if (el.TryGetProperty("tags", out var searchTagsEl))
        {
            if (searchTagsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var t in searchTagsEl.EnumerateArray())
                {
                    if (t.ValueKind == JsonValueKind.String && t.GetString() is string s && !string.IsNullOrWhiteSpace(s))
                        tags.Add(s.Trim());
                }
            }
            else if (searchTagsEl.ValueKind == JsonValueKind.String && searchTagsEl.GetString() is string s)
            {
                tags.AddRange(s.Split(new[] { ',', ';', '|', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            }
        }
        var emuTag = TryGetString(el, "emulator", "emu", "fix", "crack", "bypass");
        if (!string.IsNullOrWhiteSpace(emuTag))
        {
            tags.Add(emuTag.Trim());
        }
        if (typeLower is "bypass" or "online" or "crack")
        {
            tags.Add(rawType?.Trim() ?? "BYPASS");
        }

        return new SearchResult
        {
            AppId = appId,
            Name = name,
            IsAvailable = isAvailable,
            DlcCount = dlcCount,
            HeaderImageUrl = headerImage,
            AppType = appType,
            Version = version,
            HasWindows = hasWindows,
            HasLinux = hasLinux,
            HasMac = hasMac,
            IsDlc = false,
            IsRedistributable = false,
            Tags = tags.Distinct(StringComparer.OrdinalIgnoreCase).ToList().AsReadOnly()
        };
    }

    public static GameMetadata? ParseGameDetails(string jsonString, uint fallbackAppId)
    {
        if (string.IsNullOrWhiteSpace(jsonString)) return null;

        using var doc = JsonDocument.Parse(jsonString);
        var root = doc.RootElement;

        JsonElement el = root;
        if (root.ValueKind == JsonValueKind.Object)
        {
            if (root.TryGetProperty("game", out var g) && g.ValueKind == JsonValueKind.Object) el = g;
            else if (root.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Object) el = d;
            else if (root.TryGetProperty("result", out var r) && r.ValueKind == JsonValueKind.Object) el = r;
        }

        if (el.ValueKind != JsonValueKind.Object) return null;

        TryGetUint(el, out var appId, "appId", "appid", "app_id", "id");
        if (appId == 0) appId = fallbackAppId;

        string name = TryGetString(el, "name", "gameName", "game_name", "title") ?? $"App {appId}";
        string? developer = TryGetString(el, "developer", "dev");
        string? publisher = TryGetString(el, "publisher", "pub");
        string? description = TryGetString(el, "description", "desc", "short_description", "summary");
        string? headerImage = TryGetString(el, "headerImageUrl", "header_image_url", "headerImage", "header_image", "imageUrl", "image");
        string? capsuleImage = TryGetString(el, "capsuleImageUrl", "capsule_image_url", "capsuleImage", "capsule_image");
        string? releaseDate = TryGetString(el, "releaseDate", "release_date", "release");

        var categories = new List<string>();
        if (el.TryGetProperty("categories", out var catEl) && catEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var c in catEl.EnumerateArray())
            {
                if (c.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(c.GetString()))
                    categories.Add(c.GetString()!);
                else if (c.ValueKind == JsonValueKind.Object && TryGetString(c, "description", "name") is string s)
                    categories.Add(s);
            }
        }

        var genres = new List<string>();
        if (el.TryGetProperty("genres", out var genEl) && genEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var g in genEl.EnumerateArray())
            {
                if (g.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(g.GetString()))
                    genres.Add(g.GetString()!);
                else if (g.ValueKind == JsonValueKind.Object && TryGetString(g, "description", "name") is string s)
                    genres.Add(s);
            }
        }

        return new GameMetadata
        {
            AppId = appId,
            Name = name,
            Developer = developer,
            Publisher = publisher,
            Description = description,
            HeaderImageUrl = headerImage,
            CapsuleImageUrl = capsuleImage,
            ReleaseDate = releaseDate,
            Categories = categories.AsReadOnly(),
            Genres = genres.AsReadOnly(),
            LastUpdated = DateTimeOffset.UtcNow
        };
    }

    public static IReadOnlyList<ManifestInfo> ParseManifests(string jsonString)
    {
        if (string.IsNullOrWhiteSpace(jsonString)) return [];

        using var doc = JsonDocument.Parse(jsonString);
        var root = doc.RootElement;
        var list = new List<ManifestInfo>();

        void ProcessElement(JsonElement el)
        {
            if (el.ValueKind == JsonValueKind.Object)
            {
                TryGetUint(el, out var depotId, "depotId", "depotid", "depot_id", "id");
                TryGetUlong(el, out var manifestId, "manifestId", "manifestid", "manifest_id");
                TryGetLong(el, out var sizeBytes, "sizeBytes", "size_bytes", "size", "total_size", "bytes");

                if (depotId > 0)
                {
                    list.Add(new ManifestInfo
                    {
                        DepotId = depotId,
                        ManifestId = manifestId,
                        SizeBytes = sizeBytes,
                        IsDownloaded = false
                    });
                }
            }
            else if (el.ValueKind == JsonValueKind.String)
            {
                var str = el.GetString();
                if (!string.IsNullOrWhiteSpace(str))
                {
                    var clean = str.Replace(".manifest", "", StringComparison.OrdinalIgnoreCase).Trim();
                    var parts = clean.Split('_');
                    if (parts.Length >= 2 &&
                        uint.TryParse(parts[0], out var dId) &&
                        ulong.TryParse(parts[1], out var mId))
                    {
                        list.Add(new ManifestInfo
                        {
                            DepotId = dId,
                            ManifestId = mId,
                            SizeBytes = 0,
                            IsDownloaded = false
                        });
                    }
                }
            }
        }

        void ScanNode(JsonElement node)
        {
            if (node.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in node.EnumerateArray())
                {
                    ProcessElement(item);
                }
            }
            else if (node.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in node.EnumerateObject())
                {
                    if (prop.NameEquals("manifests") || prop.NameEquals("depots") || prop.NameEquals("items") || prop.NameEquals("results"))
                    {
                        ScanNode(prop.Value);
                    }
                    else if (prop.NameEquals("sources") && prop.Value.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var srcProp in prop.Value.EnumerateObject())
                        {
                            ScanNode(srcProp.Value);
                        }
                    }
                }
            }
        }

        ScanNode(root);

        var distinctList = new List<ManifestInfo>();
        var seenDepots = new HashSet<uint>();
        foreach (var item in list)
        {
            if (seenDepots.Add(item.DepotId))
            {
                distinctList.Add(item);
            }
        }

        return distinctList.AsReadOnly();
    }

    private static bool TryGetUint(JsonElement el, out uint value, params string[] propertyNames)
    {
        value = 0;
        foreach (var prop in propertyNames)
        {
            if (el.TryGetProperty(prop, out var p))
            {
                if (p.ValueKind == JsonValueKind.Number && p.TryGetUInt32(out value)) return true;
                if (p.ValueKind == JsonValueKind.String && uint.TryParse(p.GetString(), out value)) return true;
            }
        }
        return false;
    }

    private static bool TryGetUlong(JsonElement el, out ulong value, params string[] propertyNames)
    {
        value = 0;
        foreach (var prop in propertyNames)
        {
            if (el.TryGetProperty(prop, out var p))
            {
                if (p.ValueKind == JsonValueKind.Number && p.TryGetUInt64(out value)) return true;
                if (p.ValueKind == JsonValueKind.String && ulong.TryParse(p.GetString(), out value)) return true;
            }
        }
        return false;
    }

    private static bool TryGetInt(JsonElement el, out int value, params string[] propertyNames)
    {
        value = 0;
        foreach (var prop in propertyNames)
        {
            if (el.TryGetProperty(prop, out var p))
            {
                if (p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out value)) return true;
                if (p.ValueKind == JsonValueKind.String && int.TryParse(p.GetString(), out value)) return true;
            }
        }
        return false;
    }

    private static bool TryGetLong(JsonElement el, out long value, params string[] propertyNames)
    {
        value = 0;
        foreach (var prop in propertyNames)
        {
            if (el.TryGetProperty(prop, out var p))
            {
                if (p.ValueKind == JsonValueKind.Number && p.TryGetInt64(out value)) return true;
                if (p.ValueKind == JsonValueKind.String && long.TryParse(p.GetString(), out value)) return true;
            }
        }
        return false;
    }

    private static bool TryGetDouble(JsonElement el, out double value, params string[] propertyNames)
    {
        value = 0;
        foreach (var prop in propertyNames)
        {
            if (el.TryGetProperty(prop, out var p))
            {
                if (p.ValueKind == JsonValueKind.Number && p.TryGetDouble(out value)) return true;
                if (p.ValueKind == JsonValueKind.String && double.TryParse(p.GetString(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out value)) return true;
            }
        }
        return false;
    }

    private static bool TryGetBool(JsonElement el, out bool value, params string[] propertyNames)
    {
        value = false;
        foreach (var prop in propertyNames)
        {
            if (el.TryGetProperty(prop, out var p))
            {
                if (p.ValueKind == JsonValueKind.True) { value = true; return true; }
                if (p.ValueKind == JsonValueKind.False) { value = false; return true; }
                if (p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out var num)) { value = num != 0; return true; }
                if (p.ValueKind == JsonValueKind.String && bool.TryParse(p.GetString(), out value)) return true;
            }
        }
        return false;
    }

    private static string? TryGetString(JsonElement el, params string[] propertyNames)
    {
        foreach (var prop in propertyNames)
        {
            if (el.TryGetProperty(prop, out var p))
            {
                if (p.ValueKind == JsonValueKind.String) return p.GetString();
                if (p.ValueKind == JsonValueKind.Number) return p.GetRawText();
            }
        }
        return null;
    }

    private async Task<HttpRequestMessage> CreateRequestAsync(HttpMethod method, string endpoint, CancellationToken ct)
    {
        var baseUrl = _appSettings?.DefaultApiUrl ?? "https://depotbox.org";
        var uri = new Uri(new Uri(baseUrl.TrimEnd('/') + "/"), endpoint.TrimStart('/'));
        var request = new HttpRequestMessage(method, uri);

        var apiKey = await _authService.GetEffectiveApiKeyAsync(ct).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            request.Headers.Add("X-API-Key", apiKey);
        }

        return request;
    }

    private async Task<HttpResponseMessage> SendWithErrorHandlingAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response, ct).ConfigureAwait(false);
        return response;
    }

    private async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;

        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var statusCode = (int)response.StatusCode;

        _logger.LogError("DepotBox API error: {StatusCode} {Body}", statusCode, body);

        throw statusCode switch
        {
            401 => new UnauthorizedAccessException($"DepotBox API: Invalid or missing API key. {body}"),
            429 => new HttpRequestException($"DepotBox API: Rate limit exceeded. {body}", null, response.StatusCode),
            _ => new HttpRequestException($"DepotBox API error {statusCode}: {body}", null, response.StatusCode)
        };
    }
}
