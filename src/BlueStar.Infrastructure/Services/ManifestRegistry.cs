using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BlueStar.Core.Interfaces;
using BlueStar.Core.Models;
using Microsoft.Extensions.Logging;

namespace BlueStar.Infrastructure.Services;

/// <summary>
/// Implements <see cref="IManifestRegistry"/>, aggregating multiple manifest providers,
/// managing health and circuit breaking, and executing resilient multi-source failover.
/// </summary>
public sealed class ManifestRegistry : IManifestRegistry
{
    private readonly List<IManifestProvider> _providers = [];
    private readonly IManifestCacheService _cacheService;
    private readonly ILogger<ManifestRegistry> _logger;
    private readonly ConcurrentDictionary<string, ProviderHealthState> _healthStates = new();
    private readonly object _lock = new();

    private sealed class ProviderHealthState
    {
        public int ConsecutiveFailures;
        public DateTimeOffset CooldownUntil = DateTimeOffset.MinValue;
        public bool IsCircuitBroken => DateTimeOffset.UtcNow < CooldownUntil;

        public void RecordSuccess()
        {
            ConsecutiveFailures = 0;
            CooldownUntil = DateTimeOffset.MinValue;
        }

        public void RecordFailure()
        {
            ConsecutiveFailures++;
            if (ConsecutiveFailures >= 3)
            {
                CooldownUntil = DateTimeOffset.UtcNow.AddMinutes(1);
            }
        }
    }

    public ManifestRegistry(
        IEnumerable<IManifestProvider> providers,
        IManifestCacheService cacheService,
        ILogger<ManifestRegistry> logger)
    {
        _cacheService = cacheService ?? throw new ArgumentNullException(nameof(cacheService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        foreach (var p in providers ?? [])
        {
            RegisterProvider(p);
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<IManifestProvider> Providers
    {
        get
        {
            lock (_lock)
            {
                return _providers.OrderByDescending(p => p.Priority).ToList().AsReadOnly();
            }
        }
    }

    /// <inheritdoc />
    public void RegisterProvider(IManifestProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);

        lock (_lock)
        {
            if (!_providers.Any(p => string.Equals(p.ProviderId, provider.ProviderId, StringComparison.OrdinalIgnoreCase)))
            {
                _providers.Add(provider);
                _healthStates[provider.ProviderId] = new ProviderHealthState();
                _logger.LogInformation("Registered manifest provider: {Name} (ID: {Id}, Priority: {Priority})",
                    provider.DisplayName, provider.ProviderId, provider.Priority);
            }
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ManifestArtifact>> DiscoverManifestsAsync(uint appId, CancellationToken ct = default)
    {
        if (appId == 0) return [];

        var list = Providers;
        var aggregated = new Dictionary<(uint DepotId, ulong ManifestId), ManifestArtifact>();

        foreach (var provider in list)
        {
            ct.ThrowIfCancellationRequested();

            var health = _healthStates.GetOrAdd(provider.ProviderId, _ => new ProviderHealthState());
            if (health.IsCircuitBroken)
            {
                _logger.LogDebug("Skipping discovery on circuit-broken provider {ProviderId}", provider.ProviderId);
                continue;
            }

            try
            {
                var discovered = await provider.DiscoverManifestsAsync(appId, ct).ConfigureAwait(false);
                health.RecordSuccess();

                foreach (var artifact in discovered)
                {
                    var key = (artifact.DepotId, artifact.ManifestId);
                    var cachedPath = _cacheService.GetManifestPath(artifact.DepotId, artifact.ManifestId);
                    var incomingRoutes = artifact.Routes.Count > 0
                        ? artifact.Routes
                        : [new ManifestSourceRoute { ProviderId = provider.ProviderId, ProviderName = provider.DisplayName, Priority = provider.Priority }];

                    if (aggregated.TryGetValue(key, out var existing))
                    {
                        var mergedRoutes = existing.Routes.Concat(incomingRoutes).DistinctBy(r => r.ProviderId).ToList();
                        aggregated[key] = existing with
                        {
                            LocalCachePath = cachedPath ?? existing.LocalCachePath,
                            Routes = mergedRoutes.AsReadOnly()
                        };
                    }
                    else
                    {
                        aggregated[key] = artifact with
                        {
                            LocalCachePath = cachedPath ?? artifact.LocalCachePath,
                            Routes = incomingRoutes.ToList().AsReadOnly()
                        };
                    }
                }

            }
            catch (Exception ex)
            {
                health.RecordFailure();
                _logger.LogWarning(ex, "Provider {ProviderId} failed during manifest discovery for AppId {AppId}",
                    provider.ProviderId, appId);
            }
        }

        return aggregated.Values.ToList().AsReadOnly();
    }

    /// <inheritdoc />
    public async Task<string?> AcquireManifestAsync(uint depotId, ulong manifestId, uint appId = 0, string? preferredProviderId = null, CancellationToken ct = default)
    {
        // 1. Check local cache first
        if (_cacheService.HasManifest(depotId, manifestId))
        {
            var cached = _cacheService.GetManifestPath(depotId, manifestId);
            _logger.LogDebug("Manifest {DepotId}_{ManifestId} resolved directly from local cache", depotId, manifestId);
            return cached;
        }

        var tempDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BlueStar", "TempManifests");
        Directory.CreateDirectory(tempDir);

        var list = Providers.ToList();

        // 2. If a preferred provider is specified, prioritize it
        if (!string.IsNullOrWhiteSpace(preferredProviderId))
        {
            var preferred = list.FirstOrDefault(p => string.Equals(p.ProviderId, preferredProviderId, StringComparison.OrdinalIgnoreCase));
            if (preferred != null)
            {
                list.Remove(preferred);
                list.Insert(0, preferred);
            }
        }

        // 3. Try each provider in order with automatic failover
        foreach (var provider in list)
        {
            ct.ThrowIfCancellationRequested();

            var health = _healthStates.GetOrAdd(provider.ProviderId, _ => new ProviderHealthState());
            if (health.IsCircuitBroken)
            {
                _logger.LogDebug("Skipping acquisition on circuit-broken provider {ProviderId}", provider.ProviderId);
                continue;
            }

            try
            {
                _logger.LogDebug("Attempting to acquire manifest {DepotId}_{ManifestId} (AppId: {AppId}) via {ProviderId}",
                    depotId, manifestId, appId, provider.ProviderId);

                var downloadedPath = await provider.DownloadManifestAsync(depotId, manifestId, tempDir, appId, ct).ConfigureAwait(false);

                if (!string.IsNullOrWhiteSpace(downloadedPath) && File.Exists(downloadedPath))
                {
                    // Store in persistent global cache
                    var cachedPath = await _cacheService.StoreManifestFileAsync(depotId, manifestId, downloadedPath, ct).ConfigureAwait(false);
                    health.RecordSuccess();
                    _logger.LogInformation("Successfully acquired manifest {DepotId}_{ManifestId} via {ProviderId}",
                        depotId, manifestId, provider.ProviderId);

                    try { File.Delete(downloadedPath); } catch { }
                    return cachedPath;
                }
            }
            catch (Exception ex)
            {
                health.RecordFailure();
                _logger.LogWarning(ex, "Provider {ProviderId} failed to acquire manifest {DepotId}_{ManifestId}. Failing over to next provider.",
                    provider.ProviderId, depotId, manifestId);
            }
        }

        _logger.LogError("Failed to acquire manifest {DepotId}_{ManifestId} from any available provider.", depotId, manifestId);
        return null;
    }
}
