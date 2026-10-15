using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using BlueStar.Core.Interfaces;
using BlueStar.Core.Models;
using BlueStar.Infrastructure.Services;
using Microsoft.Extensions.Logging;

namespace BlueStar.Infrastructure.Providers.Manifest;

/// <summary>
/// Implements <see cref="IManifestProvider"/> for community ManifestHub repositories.
/// Primary source: Coronitaa/BlueStar-ManifestHub.
/// Secondary fallback: SSMGAlt/ManifestHub2 and qwe213312/k25FCdfEOoEJ42S6.
/// Extracts manifests, branch build snapshots, and depot AES decryption keys.
/// </summary>
public sealed class ManifestHubProvider : IManifestProvider
{
    public const string PrimaryBuildsBaseUrl = "https://raw.githubusercontent.com/Coronitaa/BlueStar-ManifestHub/main/builds";
    public const string PrimaryManifestsBaseUrl = "https://raw.githubusercontent.com/Coronitaa/BlueStar-ManifestHub/main/manifests";
    public const string SecondaryManifestHub2BaseUrl = "https://raw.githubusercontent.com/SSMGAlt/ManifestHub2";
    public const string TertiaryK25BaseUrl = "https://raw.githubusercontent.com/qwe213312/k25FCdfEOoEJ42S6/main";

    private readonly HttpClient _http;
    private readonly IManifestCacheService _cacheService;
    private readonly IDepotKeyRepository? _keyRepository;
    private readonly ILogger<ManifestHubProvider> _logger;
    private readonly ICacheService? _generalCache;
    private readonly IRequestCoordinator _coordinator;
    private readonly INetworkMetricsObserver? _metrics;

    public string ProviderId => "manifesthub";
    public string DisplayName => "ManifestHub (Community)";
    public int Priority => 400;

    public ProviderCapabilities Capabilities =>
        ProviderCapabilities.ManifestDiscovery |
        ProviderCapabilities.ManifestDownload |
        ProviderCapabilities.DepotKeys |
        ProviderCapabilities.BuildDiscovery;

    private readonly ConcurrentDictionary<uint, bool> _availabilityCache = new();

    private static readonly JsonSerializerOptions SnapshotSerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };

    public ManifestHubProvider(
        HttpClient http,
        IManifestCacheService cacheService,
        ILogger<ManifestHubProvider> logger,
        IDepotKeyRepository? keyRepository = null,
        ICacheService? generalCache = null,
        IRequestCoordinator? coordinator = null,
        INetworkMetricsObserver? metrics = null)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _cacheService = cacheService ?? throw new ArgumentNullException(nameof(cacheService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _keyRepository = keyRepository;
        _generalCache = generalCache;
        _coordinator = coordinator ?? RequestCoordinator.Instance;
        _metrics = metrics;
    }

    /// <inheritdoc />
    public async Task<bool> IsAvailableAsync(uint appId, CancellationToken ct = default)
    {
        if (appId == 0) return false;
        if (_availabilityCache.TryGetValue(appId, out var cached))
            return cached;

        try
        {
            // 1. Try Primary: Coronitaa/BlueStar-ManifestHub
            try
            {
                var primaryUrl = $"{PrimaryBuildsBaseUrl}/{appId}.json";
                using var reqPrimary = new HttpRequestMessage(HttpMethod.Head, primaryUrl);
                using var respPrimary = await _http.SendAsync(reqPrimary, ct).ConfigureAwait(false);
                if (respPrimary.IsSuccessStatusCode)
                {
                    _availabilityCache[appId] = true;
                    return true;
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Primary ManifestHub availability check failed for AppId {AppId}", appId);
            }

            // 2. Fallback secondary: SSMGAlt/ManifestHub2
            try
            {
                var secondaryUrl = $"{SecondaryManifestHub2BaseUrl}/{appId}/{appId}.json";
                using var reqSecondary = new HttpRequestMessage(HttpMethod.Head, secondaryUrl);
                using var respSecondary = await _http.SendAsync(reqSecondary, ct).ConfigureAwait(false);
                var available = respSecondary.IsSuccessStatusCode;
                _availabilityCache[appId] = available;
                return available;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Secondary ManifestHub availability check failed for AppId {AppId}", appId);
            }

            _availabilityCache[appId] = false;
            return false;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "ManifestHub availability check failed for AppId {AppId}", appId);
            return false;
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ManifestArtifact>> DiscoverManifestsAsync(uint appId, CancellationToken ct = default)
    {
        if (appId == 0) return [];

        var cacheKey = $"manifesthub_discovery_{appId}";
        if (_generalCache != null)
        {
            try
            {
                var cached = await _generalCache.GetAsync<List<ManifestArtifact>>(cacheKey, ct).ConfigureAwait(false);
                if (cached != null)
                {
                    _metrics?.OnCacheHit("ManifestHub", $"{appId}.json");
                    return cached;
                }
            }
            catch { }
        }

        return await _coordinator.ExecuteAsync($"manifesthub_discover_{appId}", async innerCt =>
        {
            if (_generalCache != null)
            {
                try
                {
                    var cached = await _generalCache.GetAsync<List<ManifestArtifact>>(cacheKey, innerCt).ConfigureAwait(false);
                    if (cached != null)
                    {
                        _metrics?.OnCacheHit("ManifestHub", $"{appId}.json");
                        return (IReadOnlyList<ManifestArtifact>)cached;
                    }
                }
                catch { }
            }

            List<ManifestArtifact> results = [];
            bool primaryDiscovered = false;

            // 1. Primary: Coronitaa/BlueStar-ManifestHub
            var primaryUrl = $"{PrimaryBuildsBaseUrl}/{appId}.json";
            try
            {
                _metrics?.OnProviderRequest("ManifestHub", primaryUrl);
                using var resp = await _http.GetAsync(primaryUrl, innerCt).ConfigureAwait(false);
                if (resp.IsSuccessStatusCode)
                {
                    var json = await resp.Content.ReadAsStringAsync(innerCt).ConfigureAwait(false);
                    var artifacts = ParseHubBuildSnapshots(json, appId);
                    if (artifacts.Count > 0)
                    {
                        results.AddRange(artifacts);
                        primaryDiscovered = true;
                    }
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Primary ManifestHub discovery failed for AppId {AppId}", appId);
            }

            // 2. Fallback secondary: SSMGAlt/ManifestHub2
            if (!primaryDiscovered)
            {
                var secondaryUrl = $"{SecondaryManifestHub2BaseUrl}/{appId}/{appId}.json";
                try
                {
                    _metrics?.OnProviderRequest("ManifestHub", secondaryUrl);
                    using var resp = await _http.GetAsync(secondaryUrl, innerCt).ConfigureAwait(false);
                    if (resp.IsSuccessStatusCode)
                    {
                        var json = await resp.Content.ReadAsStringAsync(innerCt).ConfigureAwait(false);
                        var (secondaryArtifacts, keysToRegister) = ParseCommunityManifestHub2Json(json, appId);
                        results.AddRange(secondaryArtifacts);

                        // Register discovered keys from SSMGAlt JSON
                        if (_keyRepository != null && keysToRegister.Count > 0)
                        {
                            await _keyRepository.RegisterKeysAsync(keysToRegister, "ManifestHub JSON", innerCt).ConfigureAwait(false);
                        }
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Secondary ManifestHub discovery failed for AppId {AppId}", appId);
                }
            }

            // 3. For any missing AES depot keys, attempt community Lua import if needed
            if (_keyRepository != null && results.Count > 0)
            {
                try
                {
                    var depotIds = results.Select(r => r.DepotId).Distinct().ToList();
                    var existingKeys = await _keyRepository.GetKeysAsync(depotIds, innerCt).ConfigureAwait(false);
                    var missingKey = depotIds.Any(id => !existingKeys.ContainsKey(id));
                    if (missingKey)
                    {
                        var luaUrl = $"{SecondaryManifestHub2BaseUrl}/{appId}/{appId}.lua";
                        _metrics?.OnProviderRequest("ManifestHub", luaUrl);
                        using var luaResp = await _http.GetAsync(luaUrl, innerCt).ConfigureAwait(false);
                        if (luaResp.IsSuccessStatusCode)
                        {
                            var lua = await luaResp.Content.ReadAsStringAsync(innerCt).ConfigureAwait(false);
                            await _keyRepository.ImportFromLuaAsync(lua, innerCt).ConfigureAwait(false);
                        }
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Failed to import keys from community Lua for AppId {AppId}", appId);
                }
            }

            // 4. Cache results (even if empty to prevent repeated requests)
            if (_generalCache != null)
            {
                var ttl = results.Count > 0 ? TimeSpan.FromDays(7) : TimeSpan.FromDays(1);
                try { await _generalCache.SetAsync(cacheKey, results, ttl, innerCt).ConfigureAwait(false); } catch { }
            }

            return results;
        }, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Parses builds snapshots from JSON payload supporting List of BuildSnapshot, single BuildSnapshot,
    /// or Hub v1 format with depotManifests.
    /// </summary>
    public IReadOnlyList<ManifestArtifact> ParseHubBuildSnapshots(string json, uint appId = 0)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];

        var results = new List<ManifestArtifact>();
        var seen = new HashSet<(uint, ulong)>();

        void AddArtifact(uint depotId, ulong manifestId, long size = 0)
        {
            if (depotId == 0 || manifestId == 0) return;
            if (!seen.Add((depotId, manifestId))) return;

            var cachedPath = _cacheService.GetManifestPath(depotId, manifestId);
            results.Add(new ManifestArtifact
            {
                DepotId = depotId,
                ManifestId = manifestId,
                SizeBytes = size,
                LocalCachePath = cachedPath,
                Routes = [
                    new ManifestSourceRoute
                    {
                        ProviderId = ProviderId,
                        ProviderName = DisplayName,
                        RouteType = ManifestSourceType.GitHubRaw,
                        Url = $"{PrimaryManifestsBaseUrl}/{depotId}/{manifestId}.manifest",
                        Priority = Priority
                    }
                ]
            });
        }

        // Try direct strongly-typed deserialization first
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.ValueKind == JsonValueKind.Array)
            {
                var snapshots = JsonSerializer.Deserialize<List<BuildSnapshot>>(json, SnapshotSerializerOptions);
                if (snapshots != null)
                {
                    foreach (var s in snapshots)
                    {
                        if (s.DepotManifests == null) continue;
                        foreach (var (dId, mId) in s.DepotManifests)
                        {
                            AddArtifact(dId, mId);
                        }
                    }
                }
            }
            else if (root.ValueKind == JsonValueKind.Object)
            {
                // Could be single BuildSnapshot or Hub v1 with depotManifests
                var single = JsonSerializer.Deserialize<BuildSnapshot>(json, SnapshotSerializerOptions);
                if (single?.DepotManifests != null && single.DepotManifests.Count > 0)
                {
                    foreach (var (dId, mId) in single.DepotManifests)
                    {
                        AddArtifact(dId, mId);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Strongly-typed snapshot deserialization failed, falling back to JsonDocument inspection");
        }

        if (results.Count > 0)
            return results;

        // Fallback / tolerant JsonDocument parsing for Hub v1 variations
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in root.EnumerateArray())
                {
                    ExtractFromSnapshotElement(item, AddArtifact);
                }
            }
            else if (root.ValueKind == JsonValueKind.Object)
            {
                ExtractFromSnapshotElement(root, AddArtifact);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse build snapshots JSON for AppId {AppId}", appId);
        }

        return results;
    }

    private static void ExtractFromSnapshotElement(JsonElement element, Action<uint, ulong, long> addArtifact)
    {
        // 1. Check for depotManifests / depot_manifests
        JsonProperty dmProp = default;
        bool foundDm = false;
        foreach (var prop in element.EnumerateObject())
        {
            if (prop.NameEquals("depotManifests") ||
                prop.NameEquals("depot_manifests") ||
                string.Equals(prop.Name, "depotManifests", StringComparison.OrdinalIgnoreCase))
            {
                dmProp = prop;
                foundDm = true;
                break;
            }
        }

        if (foundDm)
        {
            var dmVal = dmProp.Value;
            if (dmVal.ValueKind == JsonValueKind.Object)
            {
                foreach (var depotProp in dmVal.EnumerateObject())
                {
                    if (uint.TryParse(depotProp.Name, out var depotId))
                    {
                        ulong manifestId = 0;
                        if (depotProp.Value.ValueKind == JsonValueKind.Number)
                        {
                            depotProp.Value.TryGetUInt64(out manifestId);
                        }
                        else if (depotProp.Value.ValueKind == JsonValueKind.String)
                        {
                            ulong.TryParse(depotProp.Value.GetString(), out manifestId);
                        }

                        if (manifestId > 0)
                        {
                            addArtifact(depotId, manifestId, 0);
                        }
                    }
                }
                return;
            }
            else if (dmVal.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in dmVal.EnumerateArray())
                {
                    uint depotId = 0;
                    ulong manifestId = 0;
                    if (item.TryGetProperty("depotId", out var dProp) || item.TryGetProperty("depot_id", out dProp))
                    {
                        if (dProp.ValueKind == JsonValueKind.Number) dProp.TryGetUInt32(out depotId);
                        else if (dProp.ValueKind == JsonValueKind.String) uint.TryParse(dProp.GetString(), out depotId);
                    }
                    if (item.TryGetProperty("manifestId", out var mProp) || item.TryGetProperty("manifest_id", out mProp))
                    {
                        if (mProp.ValueKind == JsonValueKind.Number) mProp.TryGetUInt64(out manifestId);
                        else if (mProp.ValueKind == JsonValueKind.String) ulong.TryParse(mProp.GetString(), out manifestId);
                    }
                    if (depotId > 0 && manifestId > 0)
                    {
                        addArtifact(depotId, manifestId, 0);
                    }
                }
                return;
            }
        }

        // 2. Check for "depots" property
        if (element.TryGetProperty("depots", out var depotsEl) && depotsEl.ValueKind == JsonValueKind.Object)
        {
            foreach (var depotProp in depotsEl.EnumerateObject())
            {
                if (uint.TryParse(depotProp.Name, out var depotId))
                {
                    ulong manifestId = 0;
                    if (depotProp.Value.ValueKind == JsonValueKind.Number)
                    {
                        depotProp.Value.TryGetUInt64(out manifestId);
                    }
                    else if (depotProp.Value.ValueKind == JsonValueKind.String)
                    {
                        ulong.TryParse(depotProp.Value.GetString(), out manifestId);
                    }
                    if (manifestId > 0)
                    {
                        addArtifact(depotId, manifestId, 0);
                    }
                }
            }
            return;
        }

        // 3. Check for "depot" property (SSMGAlt / community format)
        if (element.TryGetProperty("depot", out var depotEl) && depotEl.ValueKind == JsonValueKind.Object)
        {
            foreach (var depotProp in depotEl.EnumerateObject())
            {
                if (!uint.TryParse(depotProp.Name, out var depotId)) continue;
                var depotObj = depotProp.Value;
                if (depotObj.TryGetProperty("manifests", out var manifestsEl) && manifestsEl.ValueKind == JsonValueKind.Object)
                {
                    foreach (var branchProp in manifestsEl.EnumerateObject())
                    {
                        if (branchProp.Value.TryGetProperty("gid", out var gidProp))
                        {
                            var gidStr = gidProp.GetString();
                            if (ulong.TryParse(gidStr, out var manifestId) && manifestId > 0)
                            {
                                long size = 0;
                                if (branchProp.Value.TryGetProperty("size", out var sProp))
                                {
                                    if (sProp.ValueKind == JsonValueKind.Number) sProp.TryGetInt64(out size);
                                    else if (sProp.ValueKind == JsonValueKind.String) long.TryParse(sProp.GetString(), out size);
                                }
                                addArtifact(depotId, manifestId, size);
                            }
                        }
                    }
                }
            }
        }
    }

    private (List<ManifestArtifact> Artifacts, List<KeyValuePair<uint, string>> Keys) ParseCommunityManifestHub2Json(string json, uint appId)
    {
        var artifacts = new List<ManifestArtifact>();
        var keysToRegister = new List<KeyValuePair<uint, string>>();

        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("depot", out var depotEl) || depotEl.ValueKind != JsonValueKind.Object)
        {
            return (artifacts, keysToRegister);
        }

        foreach (var depotProp in depotEl.EnumerateObject())
        {
            if (!uint.TryParse(depotProp.Name, out var depotId)) continue;

            var depotObj = depotProp.Value;

            // Extract decryption key if present
            if (depotObj.TryGetProperty("decryptionkey", out var dkProp) && dkProp.ValueKind == JsonValueKind.String)
            {
                var key = dkProp.GetString();
                if (!string.IsNullOrWhiteSpace(key))
                {
                    keysToRegister.Add(new KeyValuePair<uint, string>(depotId, key));
                }
            }

            // Enumerate manifests per branch
            if (depotObj.TryGetProperty("manifests", out var manifestsEl) && manifestsEl.ValueKind == JsonValueKind.Object)
            {
                foreach (var branchProp in manifestsEl.EnumerateObject())
                {
                    if (branchProp.Value.TryGetProperty("gid", out var gidProp))
                    {
                        var gidStr = gidProp.GetString();
                        if (ulong.TryParse(gidStr, out var manifestId) && manifestId > 0)
                        {
                            long size = 0;
                            if (branchProp.Value.TryGetProperty("size", out var sProp))
                            {
                                if (sProp.ValueKind == JsonValueKind.Number) sProp.TryGetInt64(out size);
                                else if (sProp.ValueKind == JsonValueKind.String) long.TryParse(sProp.GetString(), out size);
                            }

                            var cachedPath = _cacheService.GetManifestPath(depotId, manifestId);
                            artifacts.Add(new ManifestArtifact
                            {
                                DepotId = depotId,
                                ManifestId = manifestId,
                                SizeBytes = size,
                                LocalCachePath = cachedPath,
                                Routes = [
                                    new ManifestSourceRoute
                                    {
                                        ProviderId = ProviderId,
                                        ProviderName = DisplayName,
                                        RouteType = ManifestSourceType.GitHubRaw,
                                        Url = $"{SecondaryManifestHub2BaseUrl}/{appId}/{depotId}_{manifestId}.manifest",
                                        Priority = Priority
                                    }
                                ]
                            });
                        }
                    }
                }
            }
        }

        return (artifacts, keysToRegister);
    }

    /// <inheritdoc />
    public async Task<string> DownloadManifestAsync(uint depotId, ulong manifestId, string targetDirectory, uint appId = 0, CancellationToken ct = default)
    {
        // 1. Check local cache first
        if (_cacheService.HasManifest(depotId, manifestId))
        {
            var cached = _cacheService.GetManifestPath(depotId, manifestId)!;
            Directory.CreateDirectory(targetDirectory);
            var dest = Path.Combine(targetDirectory, $"{depotId}_{manifestId}.manifest");
            if (!string.Equals(Path.GetFullPath(cached), Path.GetFullPath(dest), StringComparison.OrdinalIgnoreCase))
            {
                File.Copy(cached, dest, overwrite: true);
            }
            _metrics?.OnCacheHit("ManifestHub", $"{depotId}_{manifestId}.manifest");
            return dest;
        }

        return await _coordinator.ExecuteAsync($"manifesthub_dl_{depotId}_{manifestId}", async innerCt =>
        {
            if (_cacheService.HasManifest(depotId, manifestId))
            {
                var cached = _cacheService.GetManifestPath(depotId, manifestId)!;
                Directory.CreateDirectory(targetDirectory);
                var dest = Path.Combine(targetDirectory, $"{depotId}_{manifestId}.manifest");
                if (!string.Equals(Path.GetFullPath(cached), Path.GetFullPath(dest), StringComparison.OrdinalIgnoreCase))
                {
                    File.Copy(cached, dest, overwrite: true);
                }
                _metrics?.OnCacheHit("ManifestHub", $"{depotId}_{manifestId}.manifest");
                return dest;
            }

            // 2. Build candidate URLs in order of specificity and priority
            // Ruta 1 (Primaria): Coronitaa/BlueStar-ManifestHub
            var candidateUrls = new List<string>
            {
                $"{PrimaryManifestsBaseUrl}/{depotId}/{manifestId}.manifest"
            };

            // Ruta 2 (Secundaria comunitaria): SSMGAlt/ManifestHub2
            if (appId > 0)
            {
                candidateUrls.Add($"{SecondaryManifestHub2BaseUrl}/{appId}/{depotId}_{manifestId}.manifest");
            }

            // Ruta 3 (Fallback terciario K25 - formato canónico y plano):
            candidateUrls.Add($"{TertiaryK25BaseUrl}/manifests/{depotId}/{manifestId}.manifest");
            candidateUrls.Add($"{TertiaryK25BaseUrl}/{depotId}_{manifestId}.manifest");

            // 3. Try each candidate URL in strict priority order
            foreach (var url in candidateUrls)
            {
                innerCt.ThrowIfCancellationRequested();

                try
                {
                    _metrics?.OnProviderRequest("ManifestHub", url);
                    using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, innerCt).ConfigureAwait(false);
                    if (response.IsSuccessStatusCode)
                    {
                        await using var stream = await response.Content.ReadAsStreamAsync(innerCt).ConfigureAwait(false);
                        var cachedPath = await _cacheService.StoreManifestAsync(depotId, manifestId, stream, innerCt).ConfigureAwait(false);

                        Directory.CreateDirectory(targetDirectory);
                        var targetFile = Path.Combine(targetDirectory, $"{depotId}_{manifestId}.manifest");
                        if (!string.Equals(Path.GetFullPath(cachedPath), Path.GetFullPath(targetFile), StringComparison.OrdinalIgnoreCase))
                        {
                            File.Copy(cachedPath, targetFile, overwrite: true);
                        }
                        return targetFile;
                    }
                    else
                    {
                        _logger.LogDebug("ManifestHub candidate URL {Url} returned status code {StatusCode}", url, response.StatusCode);
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "ManifestHub URL attempt failed: {Url}", url);
                }
            }

            throw new HttpRequestException(
                $"Manifest {depotId}_{manifestId} could not be acquired from ManifestHub (tested {candidateUrls.Count} routes).");
        }, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ManifestInfo>> GetManifestsAsync(uint appId, CancellationToken ct = default)
    {
        var artifacts = await DiscoverManifestsAsync(appId, ct).ConfigureAwait(false);
        var list = new List<ManifestInfo>();
        foreach (var a in artifacts)
        {
            list.Add(new ManifestInfo
            {
                DepotId = a.DepotId,
                ManifestId = a.ManifestId,
                SizeBytes = a.SizeBytes,
                IsDownloaded = a.IsDownloaded,
                FilePath = a.LocalCachePath
            });
        }
        return list.AsReadOnly();
    }
}
