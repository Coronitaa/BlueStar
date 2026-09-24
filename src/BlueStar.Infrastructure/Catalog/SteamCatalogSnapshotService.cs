using System;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BlueStar.Core.Interfaces;
using BlueStar.Core.Models;
using Microsoft.Extensions.Logging;

namespace BlueStar.Infrastructure.Catalog;

/// <summary>
/// Manages catalog snapshot version checking, download, and incremental sync.
/// Never embeds a Steam Web API key in the client binary.
/// Operates offline-first: if remote manifest or downloads fail, the local database remains intact and active.
/// </summary>
public sealed class SteamCatalogSnapshotService : ICatalogSnapshotService
{
    private readonly HttpClient _http;
    private readonly ILocalCatalogRepository _catalogRepo;
    private readonly ILogger<SteamCatalogSnapshotService> _logger;
    private readonly string _stateFilePath;

    public SteamCatalogSnapshotService(
        HttpClient http,
        ILocalCatalogRepository catalogRepo,
        ILogger<SteamCatalogSnapshotService> logger,
        string? stateDir = null)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _catalogRepo = catalogRepo ?? throw new ArgumentNullException(nameof(catalogRepo));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        var dir = stateDir ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "BlueStar", "catalog");
        Directory.CreateDirectory(dir);
        _stateFilePath = Path.Combine(dir, "catalog-state.json");
    }

    /// <summary>
    /// Gets the current locally installed catalog version, or 0 if unversioned.
    /// </summary>
    public async Task<int> GetCurrentVersionAsync(CancellationToken ct = default)
    {
        // 1. Try reading snapshot_version directly from repository metadata table
        try
        {
            var metaVer = await _catalogRepo.GetMetadataAsync("snapshot_version", ct).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(metaVer) && int.TryParse(metaVer, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedMetaVer))
            {
                return parsedMetaVer;
            }
        }
        catch { }

        // 2. Fall back to state file
        if (!File.Exists(_stateFilePath)) return 0;
        try
        {
            var json = await File.ReadAllTextAsync(_stateFilePath, ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("version", out var v) && v.TryGetInt32(out var ver))
            {
                return ver;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed reading catalog state file");
        }

        return 0;
    }

    /// <summary>
    /// Checks a remote manifest URL for catalog snapshot updates. If a newer version is found,
    /// downloads the snapshot, verifies its SHA256 checksum, decompresses if .zst, imports it into the repository,
    /// and updates the local version state.
    /// </summary>
    public async Task<bool> CheckAndUpdateSnapshotAsync(string manifestUrl, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(manifestUrl)) return false;

        try
        {
            _logger.LogInformation("Checking catalog manifest at {Url}", manifestUrl);
            var manifest = await _http.GetFromJsonAsync<CatalogManifest>(manifestUrl, ct).ConfigureAwait(false);
            if (manifest == null) return false;

            var currentVersion = await GetCurrentVersionAsync(ct).ConfigureAwait(false);
            var currentSha = await _catalogRepo.GetMetadataAsync("snapshot_sha256", ct).ConfigureAwait(false);

            bool isNewerVersion = manifest.Version > currentVersion;
            bool isHashDifferent = !string.IsNullOrWhiteSpace(manifest.Sha256) &&
                                   !string.Equals(manifest.Sha256, currentSha, StringComparison.OrdinalIgnoreCase);

            if (!isNewerVersion && !isHashDifferent)
            {
                _logger.LogInformation("Catalog is up-to-date (Local: {Current}, Remote: {Remote})", currentVersion, manifest.Version);
                return false;
            }

            _logger.LogInformation("New catalog version {Remote} (SHA: {Sha}) available. Downloading from {Url}...", manifest.Version, manifest.Sha256, manifest.DownloadUrl);
            var tempDownloaded = Path.GetTempFileName();
            var tempSqlite = Path.GetTempFileName();

            try
            {
                using (var response = await _http.GetAsync(manifest.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
                {
                    response.EnsureSuccessStatusCode();
                    await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                    await using var fileStream = File.Create(tempDownloaded);
                    await stream.CopyToAsync(fileStream, ct).ConfigureAwait(false);
                }

                // Verify SHA256 of downloaded archive/file if declared
                if (!string.IsNullOrWhiteSpace(manifest.Sha256))
                {
                    await using var verifyStream = File.OpenRead(tempDownloaded);
                    var hashBytes = await SHA256.HashDataAsync(verifyStream, ct).ConfigureAwait(false);
                    var computedHash = Convert.ToHexString(hashBytes).ToLowerInvariant();

                    if (!computedHash.Equals(manifest.Sha256, StringComparison.OrdinalIgnoreCase))
                    {
                        _logger.LogError("Catalog snapshot SHA256 mismatch! Expected: {Expected}, Got: {Got}", manifest.Sha256, computedHash);
                        return false;
                    }
                }

                // Determine if decompression is needed (.zst or .zstandard)
                bool isZstd = manifest.DownloadUrl.EndsWith(".zst", StringComparison.OrdinalIgnoreCase) ||
                              manifest.DownloadUrl.EndsWith(".zstandard", StringComparison.OrdinalIgnoreCase);

                string fileToImport;
                if (isZstd)
                {
                    _logger.LogInformation("Decompressing Zstandard catalog snapshot...");
                    await using var compressedStream = File.OpenRead(tempDownloaded);
                    await using var decompressedStream = File.Create(tempSqlite);
                    using var zstdStream = new ZstdSharp.DecompressionStream(compressedStream);
                    await zstdStream.CopyToAsync(decompressedStream, ct).ConfigureAwait(false);
                    fileToImport = tempSqlite;
                }
                else
                {
                    fileToImport = tempDownloaded;
                }

                // Import verified snapshot
                await _catalogRepo.ImportSnapshotAsync(fileToImport, ct).ConfigureAwait(false);

                // Update persistent metadata in SQLite
                await _catalogRepo.SetMetadataAsync("snapshot_version", manifest.Version.ToString(CultureInfo.InvariantCulture), ct).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(manifest.Sha256))
                {
                    await _catalogRepo.SetMetadataAsync("snapshot_sha256", manifest.Sha256, ct).ConfigureAwait(false);
                }
                if (manifest.TotalApps > 0)
                {
                    await _catalogRepo.SetMetadataAsync("expected_app_count", manifest.TotalApps.ToString(CultureInfo.InvariantCulture), ct).ConfigureAwait(false);
                    await _catalogRepo.SetMetadataAsync("identity_complete", "1", ct).ConfigureAwait(false);
                }
                await _catalogRepo.SetMetadataAsync("last_sync_at", DateTimeOffset.UtcNow.ToString("O"), ct).ConfigureAwait(false);

                // Update state file
                var stateJson = JsonSerializer.Serialize(new { version = manifest.Version, updatedAt = DateTimeOffset.UtcNow, appCount = manifest.TotalApps });
                await File.WriteAllTextAsync(_stateFilePath, stateJson, ct).ConfigureAwait(false);

                _logger.LogInformation("Catalog successfully updated to version {Version} with {TotalApps} apps", manifest.Version, manifest.TotalApps);
                return true;
            }
            finally
            {
                if (File.Exists(tempDownloaded)) { try { File.Delete(tempDownloaded); } catch { } }
                if (File.Exists(tempSqlite)) { try { File.Delete(tempSqlite); } catch { } }
            }
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Catalog snapshot update check/download skipped or failed. Continuing offline with local catalog.");
            return false;
        }
    }

    /// <summary>
    /// Standalone helper for CI / build-worker synchronization against Steam IStoreService/GetAppList.
    /// Requires an API key passed explicitly by the CI environment (NEVER embedded in the client).
    /// </summary>
    public static async Task<int> SyncFromSteamWebToRepositoryAsync(
        HttpClient http,
        ILocalCatalogRepository repo,
        string apiKey,
        uint startAppId = 0,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        var totalSynced = 0;
        uint lastAppId = startAppId;
        bool hasMore = true;

        while (hasMore && !ct.IsCancellationRequested)
        {
            var url = $"https://api.steampowered.com/IStoreService/GetAppList/v1/?key={Uri.EscapeDataString(apiKey)}" +
                      $"&include_games=true&include_dlc=false&include_software=true" +
                      $"&last_appid={lastAppId}&max_results=50000";

            var response = await http.GetAsync(url, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty("response", out var resp)) break;
            if (!resp.TryGetProperty("apps", out var appsArray) || appsArray.ValueKind != JsonValueKind.Array) break;

            var batch = new List<CatalogAppItem>();
            foreach (var el in appsArray.EnumerateArray())
            {
                if (!el.TryGetProperty("appid", out var idProp) || !idProp.TryGetUInt32(out var appId) || appId == 0) continue;
                var name = el.TryGetProperty("name", out var n) ? n.GetString() ?? $"App {appId}" : $"App {appId}";
                var lastMod = el.TryGetProperty("last_modified", out var lm) && lm.TryGetInt64(out var mod) ? mod : 0;
                var priceChg = el.TryGetProperty("price_change_number", out var pcn) && pcn.TryGetUInt32(out var pchg) ? pchg : 0;

                var rawType = el.TryGetProperty("type", out var tp) ? tp.GetString() : null;
                var appType = SteamAppTaxonomy.ToTaxonomyString(SteamAppTaxonomy.Parse(rawType ?? "game"));

                batch.Add(new CatalogAppItem
                {
                    AppId = appId,
                    Name = name,
                    NormalizedName = BlueStar.Core.Helpers.DeterministicNormalizer.Normalize(name),
                    CompactName = BlueStar.Core.Helpers.DeterministicNormalizer.ToCompactKey(name),
                    AppType = appType,
                    LastModified = lastMod,
                    PriceChangeNumber = priceChg,
                    HeaderImageUrl = $"https://shared.cloudflare.steamstatic.com/store_item_assets/steam/apps/{appId}/header.jpg"
                });

                if (appId > lastAppId) lastAppId = appId;
            }

            if (batch.Count > 0)
            {
                await repo.UpsertAppsAsync(batch, ct).ConfigureAwait(false);
                totalSynced += batch.Count;
            }

            hasMore = resp.TryGetProperty("have_more_results", out var mr) && mr.GetBoolean();
        }

        return totalSynced;
    }

    /// <summary>
    /// Synchronizes apps from Steam's public ISteamApps/GetAppList/v2 endpoint (no API key required).
    /// Batches insertion into the repository.
    /// </summary>
    public static async Task<int> SyncFromSteamPublicAppListAsync(
        HttpClient http,
        ILocalCatalogRepository repo,
        int? limit = null,
        CancellationToken ct = default)
    {
        var url = "https://api.steampowered.com/ISteamApps/GetAppList/v2/";
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);

        if (!doc.RootElement.TryGetProperty("applist", out var applist) ||
            !applist.TryGetProperty("apps", out var appsArray) ||
            appsArray.ValueKind != JsonValueKind.Array)
        {
            return 0;
        }

        var batch = new List<CatalogAppItem>(5000);
        var totalSynced = 0;

        foreach (var el in appsArray.EnumerateArray())
        {
            if (ct.IsCancellationRequested) break;
            if (limit.HasValue && totalSynced + batch.Count >= limit.Value) break;

            if (!el.TryGetProperty("appid", out var idProp) || !idProp.TryGetUInt32(out var appId) || appId == 0)
                continue;

            var name = el.TryGetProperty("name", out var n) ? n.GetString() ?? $"App {appId}" : $"App {appId}";
            if (string.IsNullOrWhiteSpace(name)) continue;

            batch.Add(new CatalogAppItem
            {
                AppId = appId,
                Name = name,
                NormalizedName = BlueStar.Core.Helpers.DeterministicNormalizer.Normalize(name),
                CompactName = BlueStar.Core.Helpers.DeterministicNormalizer.ToCompactKey(name),
                AppType = "game",
                HeaderImageUrl = $"https://shared.cloudflare.steamstatic.com/store_item_assets/steam/apps/{appId}/header.jpg"
            });

            if (batch.Count >= 5000)
            {
                await repo.UpsertAppsAsync(batch, ct).ConfigureAwait(false);
                totalSynced += batch.Count;
                batch.Clear();
            }
        }

        if (batch.Count > 0)
        {
            await repo.UpsertAppsAsync(batch, ct).ConfigureAwait(false);
            totalSynced += batch.Count;
        }

        return totalSynced;
    }

    /// <summary>
    /// Compresses a file using Zstandard algorithm (level 19 for maximum distribution ratio).
    /// </summary>
    public static async Task CompressToZstdAsync(
        string inputFilePath,
        string outputZstdPath,
        int compressionLevel = 19,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputFilePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputZstdPath);

        await using var inputStream = File.OpenRead(inputFilePath);
        await using var outputStream = File.Create(outputZstdPath);
        using var zstdStream = new ZstdSharp.CompressionStream(outputStream, compressionLevel);
        await inputStream.CopyToAsync(zstdStream, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Enriches catalog apps with tags, release dates, reviews, platforms, and prices using Steam's bulk IStoreBrowseService/GetItems endpoint.
    /// Batches apps into chunks (e.g. 50-100 apps per request) with controlled concurrency and retry on 429.
    /// </summary>
    public static async Task<int> EnrichCatalogMetadataAsync(
        HttpClient http,
        ILocalCatalogRepository repo,
        IReadOnlyList<uint> appIds,
        int batchSize = 100,
        int maxConcurrency = 4,
        Action<int, int>? progressCallback = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(repo);
        ArgumentNullException.ThrowIfNull(appIds);

        if (appIds.Count == 0) return 0;

        var chunks = new List<List<uint>>();
        for (var i = 0; i < appIds.Count; i += batchSize)
        {
            chunks.Add(appIds.Skip(i).Take(batchSize).ToList());
        }

        var totalChunks = chunks.Count;
        var processedChunks = 0;
        var totalEnriched = 0;
        var channel = System.Threading.Channels.Channel.CreateBounded<List<AppMetadataEnrichment>>(new System.Threading.Channels.BoundedChannelOptions(50)
        {
            FullMode = System.Threading.Channels.BoundedChannelFullMode.Wait,
            SingleWriter = false,
            SingleReader = true
        });

        // Background single DB writer to avoid SQLite database locks
        var writerTask = Task.Run(async () =>
        {
            await foreach (var batch in channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                if (batch.Count > 0)
                {
                    await repo.UpdateAppMetadataBatchAsync(batch, ct).ConfigureAwait(false);
                    Interlocked.Add(ref totalEnriched, batch.Count);
                }
            }
        }, ct);

        using var semaphore = new SemaphoreSlim(maxConcurrency, maxConcurrency);
        var tasks = new List<Task>();

        foreach (var chunk in chunks)
        {
            if (ct.IsCancellationRequested) break;
            await semaphore.WaitAsync(ct).ConfigureAwait(false);

            tasks.Add(Task.Run(async () =>
            {
                try
                {
                    var enrichedItems = await FetchStoreItemsBatchAsync(http, chunk, ct).ConfigureAwait(false);
                    if (enrichedItems.Count > 0)
                    {
                        await channel.Writer.WriteAsync(enrichedItems, ct).ConfigureAwait(false);
                    }
                }
                finally
                {
                    semaphore.Release();
                    var done = Interlocked.Increment(ref processedChunks);
                    progressCallback?.Invoke(done * batchSize > appIds.Count ? appIds.Count : done * batchSize, appIds.Count);
                }
            }, ct));
        }

        await Task.WhenAll(tasks).ConfigureAwait(false);
        channel.Writer.Complete();
        await writerTask.ConfigureAwait(false);

        return totalEnriched;
    }

    private static async Task<List<AppMetadataEnrichment>> FetchStoreItemsBatchAsync(
        HttpClient http,
        List<uint> appIds,
        CancellationToken ct)
    {
        var result = new List<AppMetadataEnrichment>(appIds.Count);
        const int maxRetries = 3;
        var delayMs = 1000;

        for (var attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                var requestObj = new
                {
                    ids = appIds.Select(id => new { appid = id }).ToArray(),
                    context = new { language = "english", country_code = "US" },
                    data_request = new
                    {
                        include_tag_count = 20,
                        include_release = true,
                        include_reviews = true,
                        include_platforms = true
                    }
                };

                var jsonPayload = JsonSerializer.Serialize(requestObj);
                var url = "https://api.steampowered.com/IStoreBrowseService/GetItems/v1/?input_json=" + Uri.EscapeDataString(jsonPayload);

                using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                if ((int)response.StatusCode == 429)
                {
                    await Task.Delay(delayMs, ct).ConfigureAwait(false);
                    delayMs *= 2;
                    continue;
                }

                response.EnsureSuccessStatusCode();

                await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);

                if (!doc.RootElement.TryGetProperty("response", out var resp) ||
                    !resp.TryGetProperty("store_items", out var items) ||
                    items.ValueKind != JsonValueKind.Array)
                {
                    return result;
                }

                foreach (var el in items.EnumerateArray())
                {
                    if (!el.TryGetProperty("id", out var idProp) && !el.TryGetProperty("appid", out idProp))
                        continue;

                    var appId = idProp.GetUInt32();
                    var success = el.TryGetProperty("success", out var sc) ? sc.GetInt32() : 1;
                    if (success != 1) continue;

                    // Tags: format as ",id1,id2,id3,"
                    string? tagIds = null;
                    var tagList = new List<int>();
                    if (el.TryGetProperty("tagids", out var tagsArr) && tagsArr.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var t in tagsArr.EnumerateArray())
                        {
                            if (t.TryGetInt32(out var tid)) tagList.Add(tid);
                        }
                    }
                    if (tagList.Count > 0)
                    {
                        tagIds = "," + string.Join(",", tagList) + ",";
                    }

                    // Release date
                    long? relDateUtc = null;
                    if (el.TryGetProperty("release", out var rel) && rel.TryGetProperty("steam_release_date", out var srd) && srd.TryGetInt64(out var rUtc) && rUtc > 0)
                    {
                        relDateUtc = rUtc;
                    }

                    // Price cents: only pre-record Free games (0 cents). Paid games vary by region and discounts,
                    // so leave null to let client query real-time regional store pricing.
                    int? priceCents = null;
                    if (el.TryGetProperty("is_free", out var isFreeProp) && isFreeProp.GetBoolean())
                    {
                        priceCents = 0;
                    }

                    // Reviews
                    int? revPct = null;
                    int? revCnt = null;
                    if (el.TryGetProperty("reviews", out var revs) && revs.TryGetProperty("summary_filtered", out var sf))
                    {
                        if (sf.TryGetProperty("percent_positive", out var pp) && pp.TryGetInt32(out var pPct)) revPct = pPct;
                        if (sf.TryGetProperty("review_count", out var rc) && rc.TryGetInt32(out var pCnt)) revCnt = pCnt;
                    }

                    // Platforms
                    var hasWin = true;
                    var hasMac = false;
                    var hasLin = false;
                    if (el.TryGetProperty("platforms", out var plats))
                    {
                        if (plats.TryGetProperty("windows", out var w)) hasWin = w.GetBoolean();
                        if (plats.TryGetProperty("mac", out var m)) hasMac = m.GetBoolean();
                        if (plats.TryGetProperty("steamos_linux", out var l)) hasLin = l.GetBoolean();
                    }

                    // NSFW flag
                    var isNsfw = false;
                    if (el.TryGetProperty("content_descriptorids", out var descArr) && descArr.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var d in descArr.EnumerateArray())
                        {
                            if (d.TryGetInt32(out var did) && (did == 3 || did == 4))
                            {
                                isNsfw = true;
                                break;
                            }
                        }
                    }
                    if (!isNsfw && tagList.Any(t => t == 12095 || t == 5611 || t == 6650))
                    {
                        isNsfw = true;
                    }

                    string? relDateText = null;
                    if (relDateUtc.HasValue && relDateUtc.Value > 0)
                    {
                        relDateText = DateTimeOffset.FromUnixTimeSeconds(relDateUtc.Value)
                            .ToString("MMM d, yyyy", CultureInfo.InvariantCulture);
                    }

                    string? priceText = null;
                    if (priceCents.HasValue && priceCents.Value == 0)
                    {
                        priceText = "Free";
                    }

                    result.Add(new AppMetadataEnrichment(
                        appId,
                        tagIds,
                        relDateUtc,
                        priceCents,
                        revPct,
                        revCnt,
                        hasWin,
                        hasMac,
                        hasLin,
                        isNsfw,
                        ReleaseDateText: relDateText,
                        PriceText: priceText
                    ));
                }

                return result;
            }
            catch (Exception) when (attempt < maxRetries)
            {
                await Task.Delay(delayMs, ct).ConfigureAwait(false);
                delayMs *= 2;
            }
        }

        return result;
    }
}

