using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BlueStar.Core.Interfaces;
using BlueStar.Core.Models;
using BlueStar.Infrastructure.Metadata;
using Microsoft.Extensions.Logging;

namespace BlueStar.Infrastructure.Services;

/// <summary>
/// Implements <see cref="IBuildResolver"/>, resolving formal and inferred game versions
/// across SteamCMD branches, community curation, and multi-provider manifest registries.
/// </summary>
public sealed class BuildResolver : IBuildResolver
{
    private readonly SteamStoreApiClient _steamClient;
    private readonly IRecommendationProvider? _curationProvider;
    private readonly IManifestRegistry _manifestRegistry;
    private readonly IDepotKeyRepository? _keyRepository;
    private readonly ILogger<BuildResolver> _logger;

    public BuildResolver(
        SteamStoreApiClient steamClient,
        IManifestRegistry manifestRegistry,
        ILogger<BuildResolver> logger,
        IRecommendationProvider? curationProvider = null,
        IDepotKeyRepository? keyRepository = null)
    {
        _steamClient = steamClient ?? throw new ArgumentNullException(nameof(steamClient));
        _manifestRegistry = manifestRegistry ?? throw new ArgumentNullException(nameof(manifestRegistry));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _curationProvider = curationProvider;
        _keyRepository = keyRepository;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<GameVersion>> GetAvailableVersionsAsync(uint appId, CancellationToken ct = default)
    {
        if (appId == 0) return [];

        var versions = new List<GameVersion>();

        try
        {
            // 1. Query canonical SteamCMD branches and builds
            var steamBuilds = await _steamClient.GetAppBuildsAsync(appId, ct).ConfigureAwait(false);

            // 2. Discover manifests across all registered providers
            var discoveredManifests = await _manifestRegistry.DiscoverManifestsAsync(appId, ct).ConfigureAwait(false);
            var manifestMap = discoveredManifests.ToDictionary(m => m.DepotId, m => m.ManifestId);

            // 3. Resolve depot keys if key repository is available
            var allDepotIds = steamBuilds.SelectMany(b => b.DepotManifests.Keys)
                .Concat(discoveredManifests.Select(m => m.DepotId))
                .Distinct()
                .ToList();

            IReadOnlyDictionary<uint, string> knownKeys = _keyRepository != null
                ? await _keyRepository.GetKeysAsync(allDepotIds, ct).ConfigureAwait(false)
                : new Dictionary<uint, string>();

            // 4. Map SteamCMD builds into GameVersion snapshots
            foreach (var build in steamBuilds)
            {
                var depots = new List<DepotVersion>();
                foreach (var (depotId, manifestId) in build.DepotManifests)
                {
                    knownKeys.TryGetValue(depotId, out var key);
                    depots.Add(new DepotVersion
                    {
                        DepotId = depotId,
                        ManifestId = manifestId,
                        Name = $"Depot {depotId}",
                        DepotKey = key,
                        IsRecommended = true
                    });
                }

                versions.Add(new GameVersion
                {
                    BuildId = build.BuildId,
                    BranchName = build.BranchName,
                    DisplayName = build.DisplayName,
                    UpdatedAt = build.UpdatedAt,
                    Description = build.Description,
                    Source = build.Source,
                    IsInferred = false,
                    Depots = depots.AsReadOnly()
                });
            }

            // 5. If no formal builds returned from SteamCMD, synthesize an inferred version from discovered provider manifests
            if (versions.Count == 0 && discoveredManifests.Count > 0)
            {
                var inferredDepots = discoveredManifests.Select(m =>
                {
                    knownKeys.TryGetValue(m.DepotId, out var key);
                    return new DepotVersion
                    {
                        DepotId = m.DepotId,
                        ManifestId = m.ManifestId,
                        Name = $"Depot {m.DepotId}",
                        SizeBytes = m.SizeBytes,
                        DepotKey = key,
                        IsRecommended = true
                    };
                }).ToList();

                versions.Add(new GameVersion
                {
                    BuildId = "Discovered",
                    BranchName = "public",
                    DisplayName = "Latest Discovered Provider Build",
                    UpdatedAt = DateTimeOffset.UtcNow,
                    Description = $"{discoveredManifests.Count} verified manifests discovered across providers",
                    Source = "MultiProvider",
                    IsInferred = true,
                    Depots = inferredDepots.AsReadOnly()
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to resolve available versions for AppId {AppId}", appId);
        }

        return versions.AsReadOnly();
    }

    /// <inheritdoc />
    public async Task<GameVersion?> ResolveRecommendedVersionAsync(uint appId, CancellationToken ct = default)
    {
        var allVersions = await GetAvailableVersionsAsync(appId, ct).ConfigureAwait(false);
        if (allVersions.Count == 0) return null;

        // 1. Check curated advice provider if available
        if (_curationProvider != null)
        {
            try
            {
                var advice = await _curationProvider.GetRecommendationAsync(appId, ct).ConfigureAwait(false);
                if (advice != null && !string.IsNullOrWhiteSpace(advice.RecommendedBuildId))
                {
                    var matching = allVersions.FirstOrDefault(v =>
                        string.Equals(v.BuildId, advice.RecommendedBuildId, StringComparison.OrdinalIgnoreCase));

                    if (matching != null)
                    {
                        return matching with { IsCurated = true };
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Curation lookup failed for AppId {AppId}; falling back to public release", appId);
            }
        }

        // 2. Fallback: return official "public" branch build
        var publicVersion = allVersions.FirstOrDefault(v =>
            string.Equals(v.BranchName, "public", StringComparison.OrdinalIgnoreCase));

        if (publicVersion != null)
        {
            return publicVersion;
        }

        // 3. Fallback: return first available version
        return allVersions.FirstOrDefault();
    }

    /// <inheritdoc />
    public async Task<GameVersion?> ResolveLatestVersionAsync(uint appId, CancellationToken ct = default)
    {
        var allVersions = await GetAvailableVersionsAsync(appId, ct).ConfigureAwait(false);
        return allVersions.FirstOrDefault(v => string.Equals(v.BranchName, "public", StringComparison.OrdinalIgnoreCase))
            ?? allVersions.FirstOrDefault();
    }

    /// <inheritdoc />
    public async Task<GameVersion?> ResolveVersionAsync(uint appId, string buildIdOrBranch, CancellationToken ct = default)
    {
        var allVersions = await GetAvailableVersionsAsync(appId, ct).ConfigureAwait(false);
        return allVersions.FirstOrDefault(v =>
            string.Equals(v.BuildId, buildIdOrBranch, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(v.BranchName, buildIdOrBranch, StringComparison.OrdinalIgnoreCase));
    }
}
