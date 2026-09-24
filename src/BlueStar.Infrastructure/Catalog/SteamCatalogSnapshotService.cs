using System;
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
    /// downloads the snapshot, verifies its SHA256 checksum, imports it into the repository,
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
            if (manifest.Version <= currentVersion)
            {
                _logger.LogInformation("Catalog is up-to-date (Local: {Current}, Remote: {Remote})", currentVersion, manifest.Version);
                return false;
            }

            _logger.LogInformation("New catalog version {Remote} available. Downloading from {Url}...", manifest.Version, manifest.DownloadUrl);
            var tempSnapshot = Path.GetTempFileName();

            try
            {
                using (var response = await _http.GetAsync(manifest.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
                {
                    response.EnsureSuccessStatusCode();
                    await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                    await using var fileStream = File.Create(tempSnapshot);
                    await stream.CopyToAsync(fileStream, ct).ConfigureAwait(false);
                }

                // Verify SHA256 if declared
                if (!string.IsNullOrWhiteSpace(manifest.Sha256))
                {
                    await using var verifyStream = File.OpenRead(tempSnapshot);
                    var hashBytes = await SHA256.HashDataAsync(verifyStream, ct).ConfigureAwait(false);
                    var computedHash = Convert.ToHexString(hashBytes).ToLowerInvariant();

                    if (!computedHash.Equals(manifest.Sha256, StringComparison.OrdinalIgnoreCase))
                    {
                        _logger.LogError("Catalog snapshot SHA256 mismatch! Expected: {Expected}, Got: {Got}", manifest.Sha256, computedHash);
                        return false;
                    }
                }

                // Import verified snapshot
                await _catalogRepo.ImportSnapshotAsync(tempSnapshot, ct).ConfigureAwait(false);

                // Update state file
                var stateJson = JsonSerializer.Serialize(new { version = manifest.Version, updatedAt = DateTimeOffset.UtcNow });
                await File.WriteAllTextAsync(_stateFilePath, stateJson, ct).ConfigureAwait(false);

                _logger.LogInformation("Catalog successfully updated to version {Version}", manifest.Version);
                return true;
            }
            finally
            {
                if (File.Exists(tempSnapshot))
                {
                    try { File.Delete(tempSnapshot); } catch { }
                }
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
}
