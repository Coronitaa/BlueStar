using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BlueStar.Core.Interfaces;
using BlueStar.Core.Models;
using BlueStar.Infrastructure.Services;
using Microsoft.Extensions.Logging;

namespace BlueStar.Infrastructure.Providers.Manifest;

/// <summary>
/// Implements <see cref="IManifestProvider"/> for community ManifestHub repositories.
/// Validated against live sources (SSMGAlt/ManifestHub2 branches and qwe213312/k25FCdfEOoEJ42S6).
/// Extracts manifests, branch build IDs, and depot AES decryption keys.
/// </summary>
public sealed class ManifestHubProvider : IManifestProvider
{
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
            var url = $"https://raw.githubusercontent.com/SSMGAlt/ManifestHub2/{appId}/{appId}.json";
            using var req = new HttpRequestMessage(HttpMethod.Head, url);
            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);

            var available = resp.IsSuccessStatusCode;
            _availabilityCache[appId] = available;
            return available;
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

            try
            {
                _metrics?.OnProviderRequest("ManifestHub", $"https://raw.githubusercontent.com/SSMGAlt/ManifestHub2/{appId}/{appId}.json");
                var url = $"https://raw.githubusercontent.com/SSMGAlt/ManifestHub2/{appId}/{appId}.json";
                using var resp = await _http.GetAsync(url, innerCt).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                {
                    if (_generalCache != null)
                    {
                        try { await _generalCache.SetAsync(cacheKey, new List<ManifestArtifact>(), TimeSpan.FromDays(1), innerCt).ConfigureAwait(false); } catch { }
                    }
                    return [];
                }

                var json = await resp.Content.ReadAsStringAsync(innerCt).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("depot", out var depotEl) || depotEl.ValueKind != JsonValueKind.Object)
                {
                    return [];
                }

                var results = new List<ManifestArtifact>();
                var keysToRegister = new List<KeyValuePair<uint, string>>();

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
                                    results.Add(new ManifestArtifact
                                    {
                                        DepotId = depotId,
                                        ManifestId = manifestId,
                                        SizeBytes = size,
                                        LocalCachePath = cachedPath
                                    });
                                }
                            }
                        }
                    }
                }

                // Register discovered keys
                if (_keyRepository != null)
                {
                    if (keysToRegister.Count > 0)
                    {
                        await _keyRepository.RegisterKeysAsync(keysToRegister, "ManifestHub JSON", innerCt).ConfigureAwait(false);
                    }

                    // Only fetch lua if any discovered depot is still missing its key
                    var depotIds = results.Select(r => r.DepotId).Distinct().ToList();
                    var existingKeys = await _keyRepository.GetKeysAsync(depotIds, innerCt).ConfigureAwait(false);
                    var missingKey = depotIds.Any(id => !existingKeys.ContainsKey(id));
                    if (missingKey)
                    {
                        try
                        {
                            _metrics?.OnProviderRequest("ManifestHub", $"https://raw.githubusercontent.com/SSMGAlt/ManifestHub2/{appId}/{appId}.lua");
                            var luaUrl = $"https://raw.githubusercontent.com/SSMGAlt/ManifestHub2/{appId}/{appId}.lua";
                            using var luaResp = await _http.GetAsync(luaUrl, innerCt).ConfigureAwait(false);
                            if (luaResp.IsSuccessStatusCode)
                            {
                                var lua = await luaResp.Content.ReadAsStringAsync(innerCt).ConfigureAwait(false);
                                await _keyRepository.ImportFromLuaAsync(lua, innerCt).ConfigureAwait(false);
                            }
                        }
                        catch { }
                    }
                }

                if (_generalCache != null)
                {
                    try { await _generalCache.SetAsync(cacheKey, results, TimeSpan.FromDays(7), innerCt).ConfigureAwait(false); } catch { }
                }

                return results;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to discover manifests from ManifestHub for AppId {AppId}", appId);
                return [];
            }
        }, ct).ConfigureAwait(false);
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

            // 2. Build candidate URLs in order of specificity
            var candidateUrls = new List<string>();

            if (appId > 0)
            {
                candidateUrls.Add($"https://raw.githubusercontent.com/SSMGAlt/ManifestHub2/{appId}/{depotId}_{manifestId}.manifest");
            }

            candidateUrls.Add($"https://raw.githubusercontent.com/qwe213312/k25FCdfEOoEJ42S6/main/{depotId}_{manifestId}.manifest");

            // 3. Try each candidate URL
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
                        File.Copy(cachedPath, targetFile, overwrite: true);
                        return targetFile;
                    }
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
