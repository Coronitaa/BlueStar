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
using BlueStar.Infrastructure.Storage;
using Microsoft.Extensions.Logging;

namespace BlueStar.Infrastructure.Providers.Manifest;

/// <summary>
/// Implements <see cref="IManifestProvider"/> for community ManifestHub repositories.
/// Primary source: Coronitaa/BlueStar-ManifestHub.
/// Secondary fallback: SSMGAlt/ManifestHub2 and qwe213312/k25FCdfEOoEJ42S6.
/// Extracts manifests, branch build snapshots, and depot AES decryption keys.
/// </summary>
public sealed class ManifestHubProvider : IManifestHubProvider
{
    public const string PrimaryBuildsBaseUrl = "https://raw.githubusercontent.com/Coronitaa/BlueStar-ManifestHub/main/builds";
    public const string PrimaryManifestsBaseUrl = "https://raw.githubusercontent.com/Coronitaa/BlueStar-ManifestHub/main/manifests";
    public const string SecondaryManifestHub2BaseUrl = "https://raw.githubusercontent.com/SSMGAlt/ManifestHub2";
    public const string TertiaryK25BaseUrl = "https://raw.githubusercontent.com/qwe213312/k25FCdfEOoEJ42S6/main";
    public const string PrimaryIndicesBaseUrl = "https://raw.githubusercontent.com/Coronitaa/BlueStar-ManifestHub/main/indices";

    private readonly HttpClient _http;
    private readonly IManifestCacheService _cacheService;
    private readonly IDepotKeyRepository? _keyRepository;
    private readonly ILogger<ManifestHubProvider> _logger;
    private readonly ICacheService? _generalCache;
    private readonly IRequestCoordinator _coordinator;
    private readonly INetworkMetricsObserver? _metrics;
    private readonly AppSettingsService? _appSettings;
    private readonly string? _hubBaseUrl;

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

    private readonly IHistoricalRevisionDiscoveryService? _revisionDiscoveryService;

    public ManifestHubProvider(
        HttpClient http,
        IManifestCacheService cacheService,
        ILogger<ManifestHubProvider> logger,
        IDepotKeyRepository? keyRepository = null,
        ICacheService? generalCache = null,
        IRequestCoordinator? coordinator = null,
        INetworkMetricsObserver? metrics = null,
        AppSettingsService? appSettings = null,
        string? hubBaseUrl = null,
        IHistoricalRevisionDiscoveryService? revisionDiscoveryService = null)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _cacheService = cacheService ?? throw new ArgumentNullException(nameof(cacheService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _keyRepository = keyRepository;
        _generalCache = generalCache;
        _coordinator = coordinator ?? RequestCoordinator.Instance;
        _metrics = metrics;
        _appSettings = appSettings;
        _hubBaseUrl = hubBaseUrl;
        _revisionDiscoveryService = revisionDiscoveryService;
    }

    private string GetEffectiveHubBaseUrl()
    {
        if (_appSettings != null && !string.IsNullOrWhiteSpace(_appSettings.HubServerUrl))
        {
            return _appSettings.HubServerUrl.TrimEnd('/');
        }
        if (!string.IsNullOrWhiteSpace(_hubBaseUrl))
        {
            return _hubBaseUrl.TrimEnd('/');
        }
        return string.Empty;
    }

    private string GetPrimaryBuildsUrl(uint appId)
    {
        var effective = GetEffectiveHubBaseUrl();
        if (!string.IsNullOrEmpty(effective))
        {
            return $"{effective}/builds/{appId}.json";
        }
        return $"{PrimaryBuildsBaseUrl}/{appId}.json";
    }

    private string GetPrimaryManifestUrl(uint depotId, ulong manifestId)
    {
        var effective = GetEffectiveHubBaseUrl();
        if (!string.IsNullOrEmpty(effective))
        {
            return $"{effective}/manifests/{depotId}/{manifestId}.manifest";
        }
        return $"{PrimaryManifestsBaseUrl}/{depotId}/{manifestId}.manifest";
    }

    private string GetPrimaryDemandUrl()
    {
        var effective = GetEffectiveHubBaseUrl();
        if (!string.IsNullOrEmpty(effective))
        {
            return $"{effective}/indices/demand.json";
        }
        return $"{PrimaryIndicesBaseUrl}/demand.json";
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

    /// <inheritdoc />
    public async Task<IReadOnlyList<ManifestArtifact>> GetCoverageAsync(IEnumerable<uint> appIds, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(appIds);
        var distinctAppIds = appIds.Where(id => id > 0).Distinct().ToList();
        if (distinctAppIds.Count == 0) return [];

        var effectiveBase = GetEffectiveHubBaseUrl();
        if (!string.IsNullOrEmpty(effectiveBase) || _http.BaseAddress != null)
        {
            // 1. Try POST /api/v1/coverage/query
            try
            {
                var queryUrl = !string.IsNullOrEmpty(effectiveBase)
                    ? $"{effectiveBase}/api/v1/coverage/query"
                    : "/api/v1/coverage/query";

                var reqBody = new { appIds = distinctAppIds };
                using var content = new StringContent(JsonSerializer.Serialize(reqBody), System.Text.Encoding.UTF8, "application/json");
                _metrics?.OnProviderRequest("ManifestHub", queryUrl);
                using var response = await _http.PostAsync(queryUrl, content, ct).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    var responseJson = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                    using var doc = JsonDocument.Parse(responseJson);
                    var root = doc.RootElement;
                    var results = new List<ManifestArtifact>();
                    var seen = new HashSet<(uint DepotId, ulong ManifestId)>();

                    if (root.TryGetProperty("existingManifests", out var emEl) && emEl.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var item in emEl.EnumerateArray())
                        {
                            uint dId = 0;
                            ulong mId = 0;
                            if (item.TryGetProperty("depotId", out var dProp)) dProp.TryGetUInt32(out dId);
                            if (item.TryGetProperty("manifestId", out var mProp)) mProp.TryGetUInt64(out mId);
                            if (dId > 0 && mId > 0 && seen.Add((dId, mId)))
                            {
                                results.Add(new ManifestArtifact
                                {
                                    DepotId = dId,
                                    ManifestId = mId,
                                    LocalCachePath = _cacheService.GetManifestPath(dId, mId),
                                    Routes = [
                                        new ManifestSourceRoute
                                        {
                                            ProviderId = ProviderId,
                                            ProviderName = DisplayName,
                                            RouteType = ManifestSourceType.GitHubRaw,
                                            Url = GetPrimaryManifestUrl(dId, mId),
                                            Priority = Priority
                                        }
                                    ]
                                });
                            }
                        }
                    }

                    return results.AsReadOnly();
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "POST /api/v1/coverage/query failed during GetCoverageAsync");
            }

            // 2. Try GET /indices/coverage.json
            try
            {
                var covUrl = !string.IsNullOrEmpty(effectiveBase)
                    ? $"{effectiveBase}/indices/coverage.json"
                    : "/indices/coverage.json";

                _metrics?.OnProviderRequest("ManifestHub", covUrl);
                using var covResp = await _http.GetAsync(covUrl, ct).ConfigureAwait(false);
                if (covResp.IsSuccessStatusCode)
                {
                    var covJson = await covResp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                    using var doc = JsonDocument.Parse(covJson);
                    var results = new List<ManifestArtifact>();
                    var seen = new HashSet<(uint DepotId, ulong ManifestId)>();
                    var requestedSet = new HashSet<uint>(distinctAppIds);

                    foreach (var prop in doc.RootElement.EnumerateObject())
                    {
                        var parts = prop.Name.Split(':');
                        if (parts.Length == 2 && uint.TryParse(parts[0], out var aId) && requestedSet.Contains(aId) && uint.TryParse(parts[1], out var dId))
                        {
                            var covObj = prop.Value;
                            if (covObj.TryGetProperty("latestAvailableManifestId", out var lProp) && lProp.TryGetUInt64(out var mId) && mId > 0)
                            {
                                if (seen.Add((dId, mId)))
                                {
                                    results.Add(new ManifestArtifact
                                    {
                                        DepotId = dId,
                                        ManifestId = mId,
                                        LocalCachePath = _cacheService.GetManifestPath(dId, mId),
                                        Routes = [
                                            new ManifestSourceRoute
                                            {
                                                ProviderId = ProviderId,
                                                ProviderName = DisplayName,
                                                RouteType = ManifestSourceType.GitHubRaw,
                                                Url = GetPrimaryManifestUrl(dId, mId),
                                                Priority = Priority
                                            }
                                        ]
                                    });
                                }
                            }
                        }
                    }

                    if (results.Count > 0)
                    {
                        return results.AsReadOnly();
                    }
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "GET /indices/coverage.json failed during GetCoverageAsync");
            }
        }

        // 3. Fallback: discover manifests per app ID
        var fallbackResults = new List<ManifestArtifact>();
        var fallbackSeen = new HashSet<(uint DepotId, ulong ManifestId)>();

        foreach (var appId in distinctAppIds)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var manifests = await DiscoverManifestsAsync(appId, ct).ConfigureAwait(false);
                foreach (var m in manifests)
                {
                    if (fallbackSeen.Add((m.DepotId, m.ManifestId)))
                    {
                        fallbackResults.Add(m);
                    }
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to discover manifests during GetCoverageAsync for AppId {AppId}", appId);
            }
        }

        return fallbackResults.AsReadOnly();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<BuildSnapshot>> GetAppSnapshotsAsync(uint appId, CancellationToken ct = default)
    {
        if (appId == 0) return [];

        var primaryUrl = GetPrimaryBuildsUrl(appId);
        try
        {
            _metrics?.OnProviderRequest("ManifestHub", primaryUrl);
            using var resp = await _http.GetAsync(primaryUrl, ct).ConfigureAwait(false);
            if (resp.IsSuccessStatusCode)
            {
                var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind == JsonValueKind.Array)
                {
                    var list = JsonSerializer.Deserialize<List<BuildSnapshot>>(json, SnapshotSerializerOptions);
                    if (list != null && list.Count > 0) return list.AsReadOnly();
                }
                else if (doc.RootElement.ValueKind == JsonValueKind.Object)
                {
                    var single = JsonSerializer.Deserialize<BuildSnapshot>(json, SnapshotSerializerOptions);
                    if (single != null) return new[] { single };
                }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to load primary build snapshots for AppId {AppId}", appId);
        }

        var secondaryUrl = $"{SecondaryManifestHub2BaseUrl}/{appId}/{appId}.json";
        try
        {
            _metrics?.OnProviderRequest("ManifestHub2", secondaryUrl);
            using var resp = await _http.GetAsync(secondaryUrl, ct).ConfigureAwait(false);
            if (resp.IsSuccessStatusCode)
            {
                var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                var artifacts = ParseHubBuildSnapshots(json, appId);
                if (artifacts.Count > 0)
                {
                    var map = artifacts.ToDictionary(a => a.DepotId, a => a.ManifestId);
                    return new[]
                    {
                        new BuildSnapshot
                        {
                            AppId = appId,
                            Branch = "public",
                            Description = "ManifestHub2 fallback snapshot",
                            DepotManifests = map
                        }
                    };
                }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to load secondary build snapshots for AppId {AppId}", appId);
        }

        return [];
    }

    /// <summary>
    /// Retrieves all known manifest revisions and build snapshots for the specified App ID from the Manifest Hub.
    /// Strictly guarantees authentic publication dates are never fabricated and chronology is not deduced from ManifestGIDs.
    /// </summary>
    public async Task<IReadOnlyList<HubRevisionItem>> GetRevisionsForAppAsync(uint appId, CancellationToken ct = default)
    {
        if (appId == 0) return [];

        if (_revisionDiscoveryService != null)
        {
            return await _revisionDiscoveryService.DiscoverRevisionsAsync(appId, ct).ConfigureAwait(false);
        }

        var list = new List<HubRevisionItem>();
        var seen = new HashSet<(uint DepotId, ulong ManifestId)>();

        // 1. Try Primary: Configured Hub or Coronitaa/BlueStar-ManifestHub builds snapshot
        var primaryUrl = GetPrimaryBuildsUrl(appId);
        try
        {
            using var resp = await _http.GetAsync(primaryUrl, ct).ConfigureAwait(false);
            if (resp.IsSuccessStatusCode)
            {
                var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (root.ValueKind == JsonValueKind.Array)
                {
                    foreach (var element in root.EnumerateArray())
                    {
                        ParseRevisionElement(element, appId, "ManifestHub", list, seen);
                    }
                }
                else if (root.ValueKind == JsonValueKind.Object)
                {
                    ParseRevisionElement(root, appId, "ManifestHub", list, seen);
                }
            }
            else if (!string.IsNullOrEmpty(GetEffectiveHubBaseUrl()))
            {
                // Fallback to GitHub raw primary if configured hub returned 404
                var githubUrl = $"{PrimaryBuildsBaseUrl}/{appId}.json";
                using var ghResp = await _http.GetAsync(githubUrl, ct).ConfigureAwait(false);
                if (ghResp.IsSuccessStatusCode)
                {
                    var ghJson = await ghResp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                    using var ghDoc = JsonDocument.Parse(ghJson);
                    if (ghDoc.RootElement.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var element in ghDoc.RootElement.EnumerateArray())
                            ParseRevisionElement(element, appId, "ManifestHub", list, seen);
                    }
                    else if (ghDoc.RootElement.ValueKind == JsonValueKind.Object)
                    {
                        ParseRevisionElement(ghDoc.RootElement, appId, "ManifestHub", list, seen);
                    }
                }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to load primary Hub revisions for AppId {AppId}", appId);
        }

        // 2. Try Secondary: SSMGAlt/ManifestHub2
        var secondaryUrl = $"{SecondaryManifestHub2BaseUrl}/{appId}/{appId}.json";
        try
        {
            using var resp = await _http.GetAsync(secondaryUrl, ct).ConfigureAwait(false);
            if (resp.IsSuccessStatusCode)
            {
                var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("depot", out var depotEl) && depotEl.ValueKind == JsonValueKind.Object)
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
                                    if (ulong.TryParse(gidProp.GetString(), out var manifestId) && manifestId > 0)
                                    {
                                        if (seen.Add((depotId, manifestId)))
                                        {
                                            long size = 0;
                                            if (branchProp.Value.TryGetProperty("size", out var sProp))
                                            {
                                                if (sProp.ValueKind == JsonValueKind.Number) sProp.TryGetInt64(out size);
                                                else if (sProp.ValueKind == JsonValueKind.String) long.TryParse(sProp.GetString(), out size);
                                            }

                                            DateTimeOffset? authenticDate = null;
                                            if (branchProp.Value.TryGetProperty("date", out var dProp) && dProp.ValueKind == JsonValueKind.String)
                                            {
                                                if (DateTimeOffset.TryParse(dProp.GetString(), out var parsedDate))
                                                    authenticDate = parsedDate;
                                            }
                                            else if (branchProp.Value.TryGetProperty("time", out var tProp) && tProp.ValueKind == JsonValueKind.Number)
                                            {
                                                if (tProp.TryGetInt64(out var unixTime) && unixTime > 0)
                                                    authenticDate = DateTimeOffset.FromUnixTimeSeconds(unixTime);
                                            }

                                            list.Add(new HubRevisionItem
                                            {
                                                AppId = appId,
                                                DepotId = depotId,
                                                ManifestId = manifestId,
                                                BranchName = branchProp.Name,
                                                DepotName = $"Depot {depotId}",
                                                AuthenticPublicationDate = authenticDate, // Strictly null if absent
                                                IngestionDate = DateTimeOffset.UtcNow,
                                                Provenance = "ManifestHub2",
                                                SizeOriginal = size,
                                                Status = "Available"
                                            });
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to load secondary Hub revisions for AppId {AppId}", appId);
        }

        // Order: revisions with authentic publication date come first (ordered by publication date descending),
        // followed by revisions with unknown dates (without using numerical ManifestId as chronological proxy).
        var ordered = list
            .OrderByDescending(r => r.HasAuthenticDate)
            .ThenByDescending(r => r.AuthenticPublicationDate ?? DateTimeOffset.MinValue)
            .ThenByDescending(r => r.IngestionDate ?? DateTimeOffset.MinValue)
            .ToList();

        return ordered.AsReadOnly();
    }

    private static void ParseRevisionElement(
        JsonElement element,
        uint appId,
        string provenance,
        List<HubRevisionItem> list,
        HashSet<(uint DepotId, ulong ManifestId)> seen)
    {
        string? buildId = null;
        if (element.TryGetProperty("buildId", out var bProp))
            buildId = bProp.ToString();

        string branch = "public";
        if (element.TryGetProperty("branch", out var brProp))
            branch = brProp.GetString() ?? "public";

        DateTimeOffset? authenticDate = null;
        if ((element.TryGetProperty("creationTime", out var ctProp) || element.TryGetProperty("date", out ctProp)) && ctProp.ValueKind == JsonValueKind.String)
        {
            if (DateTimeOffset.TryParse(ctProp.GetString(), out var ctParsed))
                authenticDate = ctParsed;
        }
        else if (element.TryGetProperty("time", out var timeProp) && timeProp.ValueKind == JsonValueKind.Number)
        {
            if (timeProp.TryGetInt64(out var unixSec) && unixSec > 0)
                authenticDate = DateTimeOffset.FromUnixTimeSeconds(unixSec);
        }

        DateTimeOffset? ingestionDate = null;
        if (element.TryGetProperty("timestamp", out var tsProp) && tsProp.ValueKind == JsonValueKind.String)
        {
            if (DateTimeOffset.TryParse(tsProp.GetString(), out var tsParsed))
                ingestionDate = tsParsed;
        }
        else if (element.TryGetProperty("ingestedAt", out var ingProp) && ingProp.ValueKind == JsonValueKind.String)
        {
            if (DateTimeOffset.TryParse(ingProp.GetString(), out var ingParsed))
                ingestionDate = ingParsed;
        }

        string prov = provenance;
        if (element.TryGetProperty("source", out var srcProp) && srcProp.ValueKind == JsonValueKind.String)
        {
            var s = srcProp.GetString();
            if (!string.IsNullOrWhiteSpace(s)) prov = s;
        }

        if (element.TryGetProperty("depotManifests", out var dmProp) && dmProp.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in dmProp.EnumerateObject())
            {
                if (uint.TryParse(prop.Name, out var depotId))
                {
                    ulong manifestId = 0;
                    if (prop.Value.ValueKind == JsonValueKind.Number) prop.Value.TryGetUInt64(out manifestId);
                    else if (prop.Value.ValueKind == JsonValueKind.String) ulong.TryParse(prop.Value.GetString(), out manifestId);

                    if (manifestId > 0 && seen.Add((depotId, manifestId)))
                    {
                        list.Add(new HubRevisionItem
                        {
                            AppId = appId,
                            DepotId = depotId,
                            ManifestId = manifestId,
                            BuildId = buildId,
                            BranchName = branch,
                            DepotName = $"Depot {depotId}",
                            AuthenticPublicationDate = authenticDate,
                            IngestionDate = ingestionDate,
                            Provenance = prov,
                            Status = "Available"
                        });
                    }
                }
            }
        }
        else if (element.TryGetProperty("depotId", out var dIdProp) && element.TryGetProperty("manifestId", out var mIdProp))
        {
            uint depotId = 0;
            if (dIdProp.ValueKind == JsonValueKind.Number) dIdProp.TryGetUInt32(out depotId);
            else if (dIdProp.ValueKind == JsonValueKind.String) uint.TryParse(dIdProp.GetString(), out depotId);

            ulong manifestId = 0;
            if (mIdProp.ValueKind == JsonValueKind.Number) mIdProp.TryGetUInt64(out manifestId);
            else if (mIdProp.ValueKind == JsonValueKind.String) ulong.TryParse(mIdProp.GetString(), out manifestId);

            long size = 0;
            if (element.TryGetProperty("sizeOriginal", out var sProp) || element.TryGetProperty("size", out sProp))
            {
                if (sProp.ValueKind == JsonValueKind.Number) sProp.TryGetInt64(out size);
                else if (sProp.ValueKind == JsonValueKind.String) long.TryParse(sProp.GetString(), out size);
            }

            string? sha256 = null;
            if (element.TryGetProperty("sha256", out var shaProp))
                sha256 = shaProp.GetString();

            if (depotId > 0 && manifestId > 0 && seen.Add((depotId, manifestId)))
            {
                list.Add(new HubRevisionItem
                {
                    AppId = appId,
                    DepotId = depotId,
                    ManifestId = manifestId,
                    BuildId = buildId,
                    BranchName = branch,
                    DepotName = $"Depot {depotId}",
                    AuthenticPublicationDate = authenticDate,
                    IngestionDate = ingestionDate,
                    Provenance = prov,
                    SizeOriginal = size,
                    Sha256 = sha256,
                    Status = "Available"
                });
            }
        }
    }

    /// <inheritdoc />
    public async Task<DemandIndex?> GetDemandAsync(CancellationToken ct = default)
    {
        const string CacheKey = "manifesthub_demand_index";
        if (_generalCache != null)
        {
            try
            {
                var cached = await _generalCache.GetAsync<DemandIndex>(CacheKey, ct).ConfigureAwait(false);
                if (cached != null)
                {
                    return cached;
                }
            }
            catch { }
        }

        var urlsToTry = new List<string> { GetPrimaryDemandUrl() };
        var secondaryUrl = $"{SecondaryManifestHub2BaseUrl}/indices/demand.json";
        if (!urlsToTry.Contains(secondaryUrl))
        {
            urlsToTry.Add(secondaryUrl);
        }

        foreach (var url in urlsToTry)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                _metrics?.OnProviderRequest("ManifestHub", url);
                using var resp = await _http.GetAsync(url, ct).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                {
                    continue;
                }

                var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(json)) continue;

                using var doc = JsonDocument.Parse(json);
                DemandIndex? result = null;

                if (doc.RootElement.ValueKind == JsonValueKind.Array)
                {
                    var records = JsonSerializer.Deserialize<List<DemandRecord>>(json, SnapshotSerializerOptions);
                    if (records != null)
                    {
                        result = new DemandIndex
                        {
                            Records = records.AsReadOnly()
                        };
                    }
                }
                else if (doc.RootElement.ValueKind == JsonValueKind.Object)
                {
                    if ((doc.RootElement.TryGetProperty("records", out var recordsProp) && recordsProp.ValueKind == JsonValueKind.Array) ||
                        (doc.RootElement.TryGetProperty("items", out var itemsProp) && itemsProp.ValueKind == JsonValueKind.Array))
                    {
                        result = JsonSerializer.Deserialize<DemandIndex>(json, SnapshotSerializerOptions);
                    }
                    else
                    {
                        var parsedRecords = new List<DemandRecord>();
                        foreach (var prop in doc.RootElement.EnumerateObject())
                        {
                            var parts = prop.Name.Split(':');
                            uint appId = 0;
                            uint depotId = 0;
                            if (parts.Length == 2)
                            {
                                uint.TryParse(parts[0], out appId);
                                uint.TryParse(parts[1], out depotId);
                            }
                            else if (parts.Length == 1)
                            {
                                uint.TryParse(parts[0], out depotId);
                            }

                            var obj = prop.Value;
                            ulong manifestId = 0;
                            if (obj.ValueKind == JsonValueKind.Object)
                            {
                                if (obj.TryGetProperty("manifestId", out var mProp))
                                {
                                    if (mProp.ValueKind == JsonValueKind.Number) mProp.TryGetUInt64(out manifestId);
                                    else if (mProp.ValueKind == JsonValueKind.String) ulong.TryParse(mProp.GetString(), out manifestId);
                                }
                                else if (obj.TryGetProperty("latestAvailableManifestId", out var lProp))
                                {
                                    if (lProp.ValueKind == JsonValueKind.Number) lProp.TryGetUInt64(out manifestId);
                                    else if (lProp.ValueKind == JsonValueKind.String) ulong.TryParse(lProp.GetString(), out manifestId);
                                }

                                if (appId == 0 && obj.TryGetProperty("appId", out var aProp))
                                {
                                    if (aProp.ValueKind == JsonValueKind.Number) aProp.TryGetUInt32(out appId);
                                    else if (aProp.ValueKind == JsonValueKind.String) uint.TryParse(aProp.GetString(), out appId);
                                }

                                string appName = string.Empty;
                                if (obj.TryGetProperty("appName", out var nameProp))
                                {
                                    appName = nameProp.GetString() ?? string.Empty;
                                }

                                int priority = 0;
                                if (obj.TryGetProperty("priority", out var pProp) && pProp.ValueKind == JsonValueKind.Number)
                                {
                                    pProp.TryGetInt32(out priority);
                                }

                                if (depotId > 0 && manifestId > 0)
                                {
                                    parsedRecords.Add(new DemandRecord
                                    {
                                        AppId = appId,
                                        DepotId = depotId,
                                        ManifestId = manifestId,
                                        AppName = appName,
                                        Priority = priority
                                    });
                                }
                            }
                        }

                        if (parsedRecords.Count > 0)
                        {
                            result = new DemandIndex
                            {
                                Records = parsedRecords.AsReadOnly()
                            };
                        }
                    }
                }

                if (result != null)
                {
                    if (_generalCache != null)
                    {
                        try
                        {
                            await _generalCache.SetAsync(CacheKey, result, TimeSpan.FromMinutes(10), ct).ConfigureAwait(false);
                        }
                        catch { }
                    }

                    return result;
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to download demand index from {Url}", url);
            }
        }

        return null;
    }
}
