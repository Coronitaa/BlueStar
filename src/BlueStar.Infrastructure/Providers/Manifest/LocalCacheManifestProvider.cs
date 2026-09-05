using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using BlueStar.Core.Interfaces;
using BlueStar.Core.Models;
using Microsoft.Extensions.Logging;

namespace BlueStar.Infrastructure.Providers.Manifest;

/// <summary>
/// Manifest provider that resolves locally cached or pre-existing manifests on disk.
/// Evaluated with highest priority (1000) to avoid unnecessary remote network requests.
/// </summary>
public sealed class LocalCacheManifestProvider : IManifestProvider
{
    private readonly IManifestCacheService _cacheService;
    private readonly ILogger<LocalCacheManifestProvider> _logger;

    public string ProviderId => "local_cache";
    public string DisplayName => "Local Cache";
    public int Priority => 1000;

    public ProviderCapabilities Capabilities =>
        ProviderCapabilities.ManifestDownload |
        ProviderCapabilities.ManifestDiscovery;

    public LocalCacheManifestProvider(
        IManifestCacheService cacheService,
        ILogger<LocalCacheManifestProvider> logger)
    {
        _cacheService = cacheService ?? throw new ArgumentNullException(nameof(cacheService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public Task<bool> IsAvailableAsync(uint appId, CancellationToken ct = default)
    {
        // Local cache is always operational
        return Task.FromResult(true);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<ManifestArtifact>> DiscoverManifestsAsync(uint appId, CancellationToken ct = default)
    {
        return Task.FromResult<IReadOnlyList<ManifestArtifact>>([]);
    }

    /// <inheritdoc />
    public Task<string> DownloadManifestAsync(uint depotId, ulong manifestId, string targetDirectory, uint appId = 0, CancellationToken ct = default)
    {
        var cached = _cacheService.GetManifestPath(depotId, manifestId);
        if (!string.IsNullOrWhiteSpace(cached) && File.Exists(cached))
        {
            Directory.CreateDirectory(targetDirectory);
            var dest = Path.Combine(targetDirectory, $"{depotId}_{manifestId}.manifest");
            if (!string.Equals(Path.GetFullPath(cached), Path.GetFullPath(dest), StringComparison.OrdinalIgnoreCase))
            {
                File.Copy(cached, dest, overwrite: true);
            }
            return Task.FromResult(dest);
        }

        throw new FileNotFoundException($"Manifest {depotId}_{manifestId} is not present in local cache.");
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<ManifestInfo>> GetManifestsAsync(uint appId, CancellationToken ct = default)
    {
        return Task.FromResult<IReadOnlyList<ManifestInfo>>([]);
    }
}
