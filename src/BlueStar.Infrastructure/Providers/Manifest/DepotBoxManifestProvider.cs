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
/// Implements <see cref="IManifestProvider"/> adapting the DepotBox REST API service.
/// </summary>
public sealed class DepotBoxManifestProvider : IManifestProvider
{
    private readonly IDepotBoxApiClient _apiClient;
    private readonly IDepotBoxArchiveParser _archiveParser;
    private readonly IManifestCacheService _cacheService;
    private readonly IDepotKeyRepository? _keyRepository;
    private readonly ILogger<DepotBoxManifestProvider> _logger;

    public string ProviderId => "depotbox";
    public string DisplayName => "DepotBox";
    public int Priority => 100;


    public ProviderCapabilities Capabilities =>
        ProviderCapabilities.ManifestDiscovery |
        ProviderCapabilities.ArchiveDownload |
        ProviderCapabilities.FixCatalog |
        ProviderCapabilities.FixDownload;

    public DepotBoxManifestProvider(
        IDepotBoxApiClient apiClient,
        IDepotBoxArchiveParser archiveParser,
        IManifestCacheService cacheService,
        ILogger<DepotBoxManifestProvider> logger,
        IDepotKeyRepository? keyRepository = null)
    {
        _apiClient = apiClient ?? throw new ArgumentNullException(nameof(apiClient));
        _archiveParser = archiveParser ?? throw new ArgumentNullException(nameof(archiveParser));
        _cacheService = cacheService ?? throw new ArgumentNullException(nameof(cacheService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _keyRepository = keyRepository;
    }

    /// <inheritdoc />
    public async Task<bool> IsAvailableAsync(uint appId, CancellationToken ct = default)
    {
        try
        {
            return await _apiClient.CheckAvailabilityAsync(appId, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "DepotBox availability check failed for AppId {AppId}", appId);
            return false;
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ManifestArtifact>> DiscoverManifestsAsync(uint appId, CancellationToken ct = default)
    {
        try
        {
            var rawList = await _apiClient.GetManifestsAsync(appId, ct).ConfigureAwait(false);
            var results = new List<ManifestArtifact>();

            foreach (var m in rawList)
            {
                var cachedPath = _cacheService.GetManifestPath(m.DepotId, m.ManifestId);
                results.Add(new ManifestArtifact
                {
                    DepotId = m.DepotId,
                    ManifestId = m.ManifestId,
                    SizeBytes = m.SizeBytes,
                    LocalCachePath = cachedPath
                });
            }

            return results.AsReadOnly();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to discover manifests from DepotBox for AppId {AppId}", appId);
            return [];
        }
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
            return dest;
        }

        // 2. Look for an existing manifest in targetDirectory
        var expectedFile = Path.Combine(targetDirectory, $"{depotId}_{manifestId}.manifest");
        if (File.Exists(expectedFile) && _cacheService.ValidateManifest(expectedFile))
        {
            await _cacheService.StoreManifestFileAsync(depotId, manifestId, expectedFile, ct).ConfigureAwait(false);
            return expectedFile;
        }

        throw new NotSupportedException(
            $"DepotBox provider requires a full game archive download to extract manifest {depotId}_{manifestId}. " +
            "Individual manifest downloads should be routed via ManifestHub or direct route.");
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<ManifestInfo>> GetManifestsAsync(uint appId, CancellationToken ct = default)
    {
        return _apiClient.GetManifestsAsync(appId, ct);
    }
}
