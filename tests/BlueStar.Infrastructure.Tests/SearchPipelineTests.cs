using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BlueStar.Core.Interfaces;
using BlueStar.Core.Models;
using BlueStar.Infrastructure.Catalog;
using BlueStar.Infrastructure.Search;
using BlueStar.Infrastructure.Steam;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BlueStar.Infrastructure.Tests;

public class SearchPipelineTests : IDisposable
{
    private readonly string _tempDb;
    private readonly LocalCatalogRepository _localRepo;
    private readonly FakeSteamCatalogSearchService _fakeSteam;
    private readonly SearchPipeline _pipeline;

    public SearchPipelineTests()
    {
        _tempDb = Path.Combine(Path.GetTempPath(), $"test_pipeline_{Guid.NewGuid():N}.sqlite");
        _localRepo = new LocalCatalogRepository(NullLogger<LocalCatalogRepository>.Instance, _tempDb);
        _fakeSteam = new FakeSteamCatalogSearchService();
        _pipeline = new SearchPipeline(_localRepo, _fakeSteam, new SteamResponseValidator(), NullLogger<SearchPipeline>.Instance);
    }

    public void Dispose()
    {
        _localRepo.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { if (File.Exists(_tempDb)) File.Delete(_tempDb); } catch { }
    }

    [Fact]
    public async Task ExecuteAsync_AppIdInLocalCatalog_ReturnsInstantlyWithoutCallingSteam()
    {
        await _localRepo.UpsertAppsAsync([
            new CatalogAppItem
            {
                AppId = 730,
                Name = "Counter-Strike 2",
                NormalizedName = "counter strike 2",
                CompactName = "counterstrike2",
                ReviewPercent = 88
            }
        ]);

        var request = new SearchRequest { RawQuery = "730" };
        var response = await _pipeline.ExecuteAsync(request);

        Assert.Equal(1, response.TotalCount);
        Assert.Single(response.Items);
        Assert.Equal((uint)730, response.Items[0].AppId);
        Assert.Equal(SearchResolutionType.ExactAppId, response.ResolutionType);
        Assert.True(response.IsFromLocalCatalog);
        Assert.Equal(0, _fakeSteam.SearchCallCount);
    }

    [Fact]
    public async Task ExecuteAsync_AppIdNotInLocalCatalog_CallsSteamAndCachesLocally()
    {
        _fakeSteam.StubResults.Add(new SearchResult
        {
            AppId = 570,
            Name = "Dota 2",
            ReviewPercent = 82
        });

        var request = new SearchRequest { RawQuery = "appid:570" };
        var response = await _pipeline.ExecuteAsync(request);

        Assert.Equal(1, response.TotalCount);
        Assert.Single(response.Items);
        Assert.Equal((uint)570, response.Items[0].AppId);
        Assert.Equal(SearchResolutionType.ExactAppId, response.ResolutionType);
        Assert.False(response.IsFromLocalCatalog);
        Assert.Equal(1, _fakeSteam.SearchCallCount);

        // Allow async background caching to complete
        await Task.Delay(100);

        var cached = await _localRepo.GetByAppIdAsync(570);
        Assert.NotNull(cached);
        Assert.Equal("Dota 2", cached.Name);
    }

    [Fact]
    public async Task ExecuteAsync_FtsMatchInLocalCatalog_ResolvesLocallyWithDeterministicRanking()
    {
        await _localRepo.UpsertAppsAsync([
            new CatalogAppItem
            {
                AppId = 10,
                Name = "Counter-Strike",
                NormalizedName = "counter strike",
                CompactName = "counterstrike",
                ReviewPercent = 97
            },
            new CatalogAppItem
            {
                AppId = 730,
                Name = "Counter-Strike: Global Offensive",
                NormalizedName = "counter strike global offensive",
                CompactName = "counterstrikeglobaloffensive",
                ReviewPercent = 88
            },
            new CatalogAppItem
            {
                AppId = 400,
                Name = "Portal",
                NormalizedName = "portal",
                CompactName = "portal",
                ReviewPercent = 98
            }
        ]);

        var request = new SearchRequest { RawQuery = "counter strike", SortBy = "name", Descending = false };
        var response = await _pipeline.ExecuteAsync(request);

        Assert.Equal(2, response.TotalCount);
        Assert.Equal(2, response.Items.Count);
        Assert.True(response.IsFromLocalCatalog);
        Assert.Equal(0, _fakeSteam.SearchCallCount);
        Assert.Equal("Counter-Strike", response.Items[0].Name);
        Assert.Equal("Counter-Strike: Global Offensive", response.Items[1].Name);
    }

    [Fact]
    public async Task ExecuteAsync_CuratedPoolSearch_CallsSteamCuratedFeedAndAppliesLocalSortIfIgnored()
    {
        _fakeSteam.StubResults.AddRange([
            new SearchResult { AppId = 1, Name = "Zeta Game", ReviewPercent = 50 },
            new SearchResult { AppId = 2, Name = "Alpha Game", ReviewPercent = 95 },
            new SearchResult { AppId = 3, Name = "Beta Game", ReviewPercent = 80 }
        ]);

        var request = new SearchRequest
        {
            Pool = "popularnew",
            SortBy = "name",
            Descending = false
        };

        var response = await _pipeline.ExecuteAsync(request);

        Assert.Equal(3, response.TotalCount);
        Assert.False(response.IsFromLocalCatalog);
        Assert.Equal(1, _fakeSteam.SearchCallCount);
    }

    [Fact]
    public async Task ExecuteAsync_WhenCanceledDuringSteamFallback_ReturnsEmptyGracefully()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var request = new SearchRequest { RawQuery = "unknown non existent game" };
        var response = await _pipeline.ExecuteAsync(request, cts.Token);

        Assert.Equal(0, response.TotalCount);
        Assert.Empty(response.Items);
    }

    [Fact]
    public async Task ExecuteAsync_WhenCanceledDuringStoreBrowse_ReturnsEmptyGracefully()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var request = new SearchRequest { RawQuery = "", Pool = "popularnew" };
        var response = await _pipeline.ExecuteAsync(request, cts.Token);

        Assert.Equal(0, response.TotalCount);
        Assert.Empty(response.Items);
    }

    private sealed class FakeSteamCatalogSearchService : ISteamCatalogSearchService
    {
        public int SearchCallCount { get; private set; }
        public List<SearchResult> StubResults { get; } = [];

        public Task<SteamSearchPage> SearchAsync(SteamSearchQuery query, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            SearchCallCount++;
            var matched = StubResults.AsEnumerable();
            if (query.RestrictToAppIds != null && query.RestrictToAppIds.Count > 0)
            {
                matched = matched.Where(r => query.RestrictToAppIds.Contains(r.AppId));
            }
            var list = matched.ToList();
            return Task.FromResult(new SteamSearchPage(list, list.Count, query.Start));
        }

        public Task<int?> GetMatchCountAsync(SteamSearchQuery query, CancellationToken ct = default) =>
            Task.FromResult<int?>(StubResults.Count);

        public Task<IReadOnlyList<SteamStoreEvent>> GetStoreEventsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<SteamStoreEvent>>([]);

        public Task<IReadOnlyList<SteamTitleSuggestion>> SuggestTitlesAsync(string term, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<SteamTitleSuggestion>>([]);
    }
}
