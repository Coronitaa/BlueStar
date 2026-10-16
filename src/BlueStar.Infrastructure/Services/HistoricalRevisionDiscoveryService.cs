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
using BlueStar.Infrastructure.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BlueStar.Infrastructure.Services;

/// <summary>
/// Implements <see cref="IHistoricalRevisionDiscoveryService"/>, discovering and unifying historical
/// manifest revisions across BlueStar-ManifestHub, SSMGAlt/ManifestHub2, K25, and DepotBox.
/// 
/// INVARIANTS:
/// 1. Unifies candidates strictly by exact (AppId, DepotId, ManifestGid).
/// 2. Preserves authentic publication dates and never estimates or fabricates timestamps.
/// 3. Does not download game payloads; retrieves only individual manifest binaries when needed.
/// 4. DepotBox operations are strictly bounded (metadata/manifest querying only; zero payload downloads).
/// </summary>
public sealed class HistoricalRevisionDiscoveryService : IHistoricalRevisionDiscoveryService
{
    public const string PrimaryBuildsBaseUrl = "https://raw.githubusercontent.com/Coronitaa/BlueStar-ManifestHub/main/builds";
    public const string PrimaryManifestsBaseUrl = "https://raw.githubusercontent.com/Coronitaa/BlueStar-ManifestHub/main/manifests";
    public const string SecondaryManifestHub2BaseUrl = "https://raw.githubusercontent.com/SSMGAlt/ManifestHub2";
    public const string TertiaryK25BaseUrl = "https://raw.githubusercontent.com/qwe213312/k25FCdfEOoEJ42S6/main";

    private readonly HttpClient _http;
    private readonly IManifestCacheService _cacheService;
    private readonly IDepotBoxApiClient? _depotBoxClient;
    private readonly IDepotKeyRepository? _keyRepository;
    private readonly AppSettingsService? _appSettings;
    private readonly ILogger<HistoricalRevisionDiscoveryService> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };

    public HistoricalRevisionDiscoveryService(
        HttpClient http,
        IManifestCacheService cacheService,
        IDepotBoxApiClient? depotBoxClient = null,
        IDepotKeyRepository? keyRepository = null,
        AppSettingsService? appSettings = null,
        ILogger<HistoricalRevisionDiscoveryService>? logger = null)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _cacheService = cacheService ?? throw new ArgumentNullException(nameof(cacheService));
        _depotBoxClient = depotBoxClient;
        _keyRepository = keyRepository;
        _appSettings = appSettings;
        _logger = logger ?? NullLogger<HistoricalRevisionDiscoveryService>.Instance;
    }

    private string GetEffectiveHubBaseUrl()
    {
        if (_appSettings != null && !string.IsNullOrWhiteSpace(_appSettings.HubServerUrl))
        {
            return _appSettings.HubServerUrl.TrimEnd('/');
        }
        return string.Empty;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<HubRevisionItem>> DiscoverRevisionsAsync(uint appId, CancellationToken ct = default)
    {
        if (appId == 0) return [];

        var rawCandidates = new List<HubRevisionItem>();

        // 1. Source 1: BlueStar-ManifestHub (Primary)
        await DiscoverFromPrimaryManifestHubAsync(appId, rawCandidates, ct).ConfigureAwait(false);

        // 2. Source 2: SSMGAlt/ManifestHub2
        await DiscoverFromManifestHub2Async(appId, rawCandidates, ct).ConfigureAwait(false);

        // 3. Source 3: K25
        await DiscoverFromK25Async(appId, rawCandidates, ct).ConfigureAwait(false);

        // 4. Source 4: DepotBox (Bounded ops only)
        await DiscoverFromDepotBoxBoundedAsync(appId, rawCandidates, ct).ConfigureAwait(false);

        // 5. Unify candidates by exact (AppId, DepotId, ManifestGid)
        var unified = UnifyCandidateRevisions(rawCandidates);

        _logger.LogInformation(
            "Historical revision discovery for App {AppId}: unified {Total} unique revisions across sources",
            appId, unified.Count);

        return unified;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<uint, IReadOnlyList<HubRevisionItem>>> DiscoverRevisionsForAppsAsync(
        IEnumerable<uint> appIds,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(appIds);
        var distinct = appIds.Where(id => id > 0).Distinct().ToList();
        var result = new ConcurrentDictionary<uint, IReadOnlyList<HubRevisionItem>>();

        await Parallel.ForEachAsync(distinct, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct }, async (id, innerCt) =>
        {
            try
            {
                var revisions = await DiscoverRevisionsAsync(id, innerCt).ConfigureAwait(false);
                result[id] = revisions;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to discover historical revisions for App {AppId}", id);
                result[id] = [];
            }
        }).ConfigureAwait(false);

        return result;
    }

    /// <inheritdoc />
    public async Task<string> FetchManifestBinaryAsync(
        uint appId,
        uint depotId,
        ulong manifestId,
        string targetDirectory,
        CancellationToken ct = default)
    {
        if (depotId == 0 || manifestId == 0)
            throw new ArgumentException("Invalid DepotId or ManifestId");

        Directory.CreateDirectory(targetDirectory);
        var targetFile = Path.Combine(targetDirectory, $"{depotId}_{manifestId}.manifest");

        // 1. Check local cache first
        if (_cacheService.HasManifest(depotId, manifestId))
        {
            var cached = _cacheService.GetManifestPath(depotId, manifestId)!;
            if (!string.Equals(Path.GetFullPath(cached), Path.GetFullPath(targetFile), StringComparison.OrdinalIgnoreCase))
            {
                File.Copy(cached, targetFile, overwrite: true);
            }
            return targetFile;
        }

        // 2. Build candidate routes in strict priority order
        var candidateUrls = new List<string>();

        // Route 1 (Primary): BlueStar-ManifestHub
        var effectiveHub = GetEffectiveHubBaseUrl();
        if (!string.IsNullOrEmpty(effectiveHub))
        {
            candidateUrls.Add($"{effectiveHub}/manifests/{depotId}/{manifestId}.manifest");
        }
        candidateUrls.Add($"{PrimaryManifestsBaseUrl}/{depotId}/{manifestId}.manifest");

        // Route 2 (Secondary): SSMGAlt/ManifestHub2
        if (appId > 0)
        {
            candidateUrls.Add($"{SecondaryManifestHub2BaseUrl}/{appId}/{depotId}_{manifestId}.manifest");
        }

        // Route 3 (Tertiary): K25
        candidateUrls.Add($"{TertiaryK25BaseUrl}/manifests/{depotId}/{manifestId}.manifest");
        candidateUrls.Add($"{TertiaryK25BaseUrl}/{depotId}_{manifestId}.manifest");

        // 3. Try each candidate URL
        foreach (var url in candidateUrls)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                if (resp.IsSuccessStatusCode)
                {
                    await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                    var cachedPath = await _cacheService.StoreManifestAsync(depotId, manifestId, stream, ct).ConfigureAwait(false);

                    if (!string.Equals(Path.GetFullPath(cachedPath), Path.GetFullPath(targetFile), StringComparison.OrdinalIgnoreCase))
                    {
                        File.Copy(cachedPath, targetFile, overwrite: true);
                    }
                    return targetFile;
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to download manifest from route {Url}", url);
            }
        }

        // 4. Route 4: Bounded DepotBox fallback check (only if existing manifest is found locally)
        if (File.Exists(targetFile) && _cacheService.ValidateManifest(targetFile))
        {
            await _cacheService.StoreManifestFileAsync(depotId, manifestId, targetFile, ct).ConfigureAwait(false);
            return targetFile;
        }

        throw new HttpRequestException(
            $"Manifest binary {depotId}_{manifestId} could not be acquired across any historical source (tested {candidateUrls.Count} routes).");
    }

    /// <summary>
    /// Unifies revision candidates by exact (AppId, DepotId, ManifestGid), preserving authentic publication date and merging provenances.
    /// </summary>
    internal static IReadOnlyList<HubRevisionItem> UnifyCandidateRevisions(IEnumerable<HubRevisionItem> candidates)
    {
        var map = new Dictionary<(uint AppId, uint DepotId, ulong ManifestId), HubRevisionItem>();
        var provenanceMap = new Dictionary<(uint AppId, uint DepotId, ulong ManifestId), HashSet<string>>();

        foreach (var c in candidates)
        {
            if (c.DepotId == 0 || c.ManifestId == 0) continue;
            var key = (c.AppId, c.DepotId, c.ManifestId);

            if (!provenanceMap.TryGetValue(key, out var provSet))
            {
                provSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                provenanceMap[key] = provSet;
            }
            if (!string.IsNullOrWhiteSpace(c.Provenance))
            {
                foreach (var part in c.Provenance.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    provSet.Add(part);
                }
            }

            if (!map.TryGetValue(key, out var existing))
            {
                map[key] = c;
            }
            else
            {
                // INVARIANT: Preserve authentic publication date. NEVER overwrite an authentic date with null or UtcNow!
                DateTimeOffset? preservedDate = existing.AuthenticPublicationDate;
                if (!existing.HasAuthenticDate && c.HasAuthenticDate)
                {
                    preservedDate = c.AuthenticPublicationDate;
                }
                else if (existing.HasAuthenticDate && c.HasAuthenticDate)
                {
                    // If both have authentic dates, take the earliest authentic date
                    preservedDate = existing.AuthenticPublicationDate < c.AuthenticPublicationDate
                        ? existing.AuthenticPublicationDate
                        : c.AuthenticPublicationDate;
                }

                // Preserve best known metadata
                var bestBuildId = !string.IsNullOrWhiteSpace(existing.BuildId) ? existing.BuildId : c.BuildId;
                var bestBranch = (!string.IsNullOrWhiteSpace(existing.BranchName) && existing.BranchName != "public")
                    ? existing.BranchName
                    : c.BranchName ?? "public";
                var bestSize = existing.SizeOriginal > 0 ? existing.SizeOriginal : c.SizeOriginal;
                var bestSha = !string.IsNullOrWhiteSpace(existing.Sha256) ? existing.Sha256 : c.Sha256;

                map[key] = existing with
                {
                    AuthenticPublicationDate = preservedDate,
                    BuildId = bestBuildId,
                    BranchName = bestBranch,
                    SizeOriginal = bestSize,
                    Sha256 = bestSha
                };
            }
        }

        // Apply merged provenance badges and sort transparently
        var results = new List<HubRevisionItem>();
        foreach (var (key, item) in map)
        {
            var provs = provenanceMap[key];
            var joinedProv = provs.Count > 0 ? string.Join(", ", provs) : item.Provenance;

            results.Add(item with { Provenance = joinedProv });
        }

        // Order: revisions with authentic publication date come first (ordered by publication date descending),
        // followed by revisions with unknown dates (without using numerical ManifestId as chronological proxy).
        var ordered = results
            .OrderByDescending(r => r.HasAuthenticDate)
            .ThenByDescending(r => r.AuthenticPublicationDate ?? DateTimeOffset.MinValue)
            .ThenByDescending(r => r.IngestionDate ?? DateTimeOffset.MinValue)
            .ThenBy(r => r.DepotId)
            .ToList();

        return ordered.AsReadOnly();
    }

    private async Task DiscoverFromPrimaryManifestHubAsync(uint appId, List<HubRevisionItem> list, CancellationToken ct)
    {
        var effective = GetEffectiveHubBaseUrl();
        var urls = new List<string>();
        if (!string.IsNullOrEmpty(effective))
        {
            urls.Add($"{effective}/builds/{appId}.json");
        }
        urls.Add($"{PrimaryBuildsBaseUrl}/{appId}.json");

        foreach (var url in urls)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var resp = await _http.GetAsync(url, ct).ConfigureAwait(false);
                if (resp.IsSuccessStatusCode)
                {
                    var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                    using var doc = JsonDocument.Parse(json);
                    var root = doc.RootElement;

                    if (root.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var item in root.EnumerateArray())
                        {
                            ParseManifestHubJsonElement(item, appId, "ManifestHub", list);
                        }
                    }
                    else if (root.ValueKind == JsonValueKind.Object)
                    {
                        ParseManifestHubJsonElement(root, appId, "ManifestHub", list);
                    }
                    return;
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Primary ManifestHub revision discovery failed for App {AppId} at {Url}", appId, url);
            }
        }
    }

    private async Task DiscoverFromManifestHub2Async(uint appId, List<HubRevisionItem> list, CancellationToken ct)
    {
        var url = $"{SecondaryManifestHub2BaseUrl}/{appId}/{appId}.json";
        try
        {
            using var resp = await _http.GetAsync(url, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return;

            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty("depot", out var depotEl) || depotEl.ValueKind != JsonValueKind.Object)
                return;

            var keysToRegister = new List<KeyValuePair<uint, string>>();

            foreach (var depotProp in depotEl.EnumerateObject())
            {
                if (!uint.TryParse(depotProp.Name, out var depotId)) continue;
                var depotObj = depotProp.Value;

                // Register decryption key if present
                if (depotObj.TryGetProperty("decryptionkey", out var dkProp) && dkProp.ValueKind == JsonValueKind.String)
                {
                    var key = dkProp.GetString();
                    if (!string.IsNullOrWhiteSpace(key))
                    {
                        keysToRegister.Add(new KeyValuePair<uint, string>(depotId, key));
                    }
                }

                if (depotObj.TryGetProperty("manifests", out var manifestsEl) && manifestsEl.ValueKind == JsonValueKind.Object)
                {
                    foreach (var branchProp in manifestsEl.EnumerateObject())
                    {
                        var branchName = branchProp.Name;
                        var branchObj = branchProp.Value;

                        ulong manifestId = 0;
                        if (branchObj.TryGetProperty("gid", out var gidProp))
                        {
                            if (gidProp.ValueKind == JsonValueKind.String) ulong.TryParse(gidProp.GetString(), out manifestId);
                            else if (gidProp.ValueKind == JsonValueKind.Number) gidProp.TryGetUInt64(out manifestId);
                        }

                        if (manifestId == 0) continue;

                        long size = 0;
                        if (branchObj.TryGetProperty("size", out var sProp))
                        {
                            if (sProp.ValueKind == JsonValueKind.Number) sProp.TryGetInt64(out size);
                            else if (sProp.ValueKind == JsonValueKind.String) long.TryParse(sProp.GetString(), out size);
                        }

                        DateTimeOffset? authenticDate = null;
                        if (branchObj.TryGetProperty("date", out var dProp) && dProp.ValueKind == JsonValueKind.String)
                        {
                            if (DateTimeOffset.TryParse(dProp.GetString(), out var parsedDate))
                                authenticDate = parsedDate;
                        }
                        else if (branchObj.TryGetProperty("time", out var tProp) && tProp.ValueKind == JsonValueKind.Number)
                        {
                            if (tProp.TryGetInt64(out var unixTime) && unixTime > 0)
                                authenticDate = DateTimeOffset.FromUnixTimeSeconds(unixTime);
                        }

                        list.Add(new HubRevisionItem
                        {
                            AppId = appId,
                            DepotId = depotId,
                            ManifestId = manifestId,
                            BranchName = branchName,
                            DepotName = $"Depot {depotId}",
                            AuthenticPublicationDate = authenticDate,
                            IngestionDate = DateTimeOffset.UtcNow,
                            Provenance = "ManifestHub2",
                            SizeOriginal = size,
                            Status = "Available"
                        });
                    }
                }
            }

            if (_keyRepository != null && keysToRegister.Count > 0)
            {
                try
                {
                    await _keyRepository.RegisterKeysAsync(keysToRegister, "ManifestHub2 JSON", ct).ConfigureAwait(false);
                }
                catch { }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "ManifestHub2 revision discovery failed for App {AppId}", appId);
        }
    }

    private async Task DiscoverFromK25Async(uint appId, List<HubRevisionItem> list, CancellationToken ct)
    {
        // Check K25 repository for app/depot JSON mapping
        var candidateUrls = new[]
        {
            $"{TertiaryK25BaseUrl}/{appId}.json",
            $"{TertiaryK25BaseUrl}/manifests/{appId}.json"
        };

        foreach (var url in candidateUrls)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var resp = await _http.GetAsync(url, ct).ConfigureAwait(false);
                if (resp.IsSuccessStatusCode)
                {
                    var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                    using var doc = JsonDocument.Parse(json);
                    var root = doc.RootElement;

                    if (root.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var item in root.EnumerateArray())
                        {
                            ParseManifestHubJsonElement(item, appId, "K25", list);
                        }
                    }
                    else if (root.ValueKind == JsonValueKind.Object)
                    {
                        ParseManifestHubJsonElement(root, appId, "K25", list);
                    }
                    return;
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "K25 revision discovery failed for App {AppId} at {Url}", appId, url);
            }
        }
    }

    private async Task DiscoverFromDepotBoxBoundedAsync(uint appId, List<HubRevisionItem> list, CancellationToken ct)
    {
        if (_depotBoxClient == null) return;

        try
        {
            // Strictly bounded metadata check: zero game archive downloads
            var isAvailable = await _depotBoxClient.CheckAvailabilityAsync(appId, ct).ConfigureAwait(false);
            if (!isAvailable) return;

            var manifests = await _depotBoxClient.GetManifestsAsync(appId, ct).ConfigureAwait(false);
            foreach (var m in manifests)
            {
                if (m.DepotId == 0 || m.ManifestId == 0) continue;

                list.Add(new HubRevisionItem
                {
                    AppId = appId,
                    DepotId = m.DepotId,
                    ManifestId = m.ManifestId,
                    DepotName = $"Depot {m.DepotId}",
                    BranchName = "public",
                    AuthenticPublicationDate = null, // Strictly null if absent
                    IngestionDate = DateTimeOffset.UtcNow,
                    Provenance = "DepotBox",
                    SizeOriginal = m.SizeBytes,
                    Status = "Available"
                });
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Bounded DepotBox revision discovery failed for App {AppId}", appId);
        }
    }

    private static void ParseManifestHubJsonElement(
        JsonElement element,
        uint appId,
        string provenance,
        List<HubRevisionItem> list)
    {
        string? buildId = null;
        if (element.TryGetProperty("buildId", out var bProp))
            buildId = bProp.ToString();

        string branch = "public";
        if (element.TryGetProperty("branch", out var brProp))
            branch = brProp.GetString() ?? "public";

        DateTimeOffset? authenticDate = null;
        if ((element.TryGetProperty("creationTime", out var ctProp) || element.TryGetProperty("date", out ctProp)) &&
            ctProp.ValueKind == JsonValueKind.String)
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

                    if (manifestId > 0)
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
                            IngestionDate = ingestionDate ?? DateTimeOffset.UtcNow,
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

            if (depotId > 0 && manifestId > 0)
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
                    IngestionDate = ingestionDate ?? DateTimeOffset.UtcNow,
                    Provenance = prov,
                    SizeOriginal = size,
                    Sha256 = sha256,
                    Status = "Available"
                });
            }
        }
    }
}
