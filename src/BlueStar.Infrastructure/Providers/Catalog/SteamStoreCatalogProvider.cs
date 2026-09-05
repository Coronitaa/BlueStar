using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BlueStar.Core.Interfaces;
using BlueStar.Core.Models;
using BlueStar.Infrastructure.Metadata;
using Microsoft.Extensions.Logging;

namespace BlueStar.Infrastructure.Providers.Catalog;

/// <summary>
/// Implements <see cref="IGameCatalogProvider"/> backed by official Steam Store API and SteamCMD data.
/// Allows discovering and adding any Steam game independently of manifest availability.
/// </summary>
public sealed class SteamStoreCatalogProvider : IGameCatalogProvider
{
    private readonly SteamStoreApiClient _steamClient;
    private readonly ILogger<SteamStoreCatalogProvider> _logger;

    public string ProviderId => "steam";
    public string DisplayName => "Steam Store & SteamCMD";
    public int Priority => 100;

    public ProviderCapabilities Capabilities =>
        ProviderCapabilities.CatalogSearch |
        ProviderCapabilities.BuildDiscovery;

    public SteamStoreCatalogProvider(
        SteamStoreApiClient steamClient,
        ILogger<SteamStoreCatalogProvider> logger)
    {
        _steamClient = steamClient ?? throw new ArgumentNullException(nameof(steamClient));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SearchResult>> SearchGamesAsync(string query, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query)) return [];

        try
        {
            var storeItems = await _steamClient.SearchStoreAsync(query, ct).ConfigureAwait(false);
            var results = new List<SearchResult>();

            foreach (var item in storeItems)
            {
                var result = new SearchResult
                {
                    AppId = item.AppId,
                    Name = item.Name,
                    HeaderImageUrl = item.HeaderImageUrl,
                    HasWindows = true,
                    AppType = "Game"
                };

                results.Add(result);
            }


            return results.AsReadOnly();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed searching Steam store catalog for query: {Query}", query);
            return [];
        }
    }

    /// <inheritdoc />
    public Task<GameMetadata?> GetGameDetailsAsync(uint appId, CancellationToken ct = default)
    {
        return _steamClient.GetMetadataAsync(appId, ct);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<DlcInfo>> GetDlcListAsync(uint appId, CancellationToken ct = default)
    {
        return _steamClient.GetDlcListAsync(appId, ct);
    }
}
