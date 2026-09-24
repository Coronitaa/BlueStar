using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
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

public class NetworkGateAndGovernorTests : IDisposable
{
    private readonly string _tempDb;
    private readonly LocalCatalogRepository _localRepo;
    private readonly CountingMockSteamService _mockSteam;
    private readonly SteamResponseValidator _validator;
    private readonly SearchPipeline _pipeline;

    public NetworkGateAndGovernorTests()
    {
        _tempDb = Path.Combine(Path.GetTempPath(), $"net_test_{Guid.NewGuid():N}.sqlite");
        _localRepo = new LocalCatalogRepository(NullLogger<LocalCatalogRepository>.Instance, _tempDb);
        _mockSteam = new CountingMockSteamService();
        _validator = new SteamResponseValidator();
        _pipeline = new SearchPipeline(_localRepo, _mockSteam, _validator, NullLogger<SearchPipeline>.Instance);
    }

    public void Dispose()
    {
        _localRepo.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { if (File.Exists(_tempDb)) File.Delete(_tempDb); } catch { }
    }

    private async Task SeedAuthoritativeCatalogAsync(int count = 500)
    {
        var items = new List<CatalogAppItem>(count);
        for (int i = 1; i <= count; i++)
        {
            items.Add(new CatalogAppItem
            {
                AppId = (uint)i,
                Name = $"Title {i:D4}",
                NormalizedName = $"title {i:D4}",
                CompactName = $"title{i:D4}",
                AppType = "Game",
                ReviewPercent = 70 + (i % 25),
                ReviewCount = 500,
                TagIds = [19, 4182],
                ReleaseDateUtc = 1700000000 + i,
                PriceCents = 1999
            });
        }
        await _localRepo.UpsertAppsAsync(items);
    }

    [Fact]
    public async Task AuthoritativeLocalCatalog_StoreBrowse_ZeroHttpRequests()
    {
        await SeedAuthoritativeCatalogAsync(500);

        var req = new SearchRequest
        {
            RawQuery = "",
            Pool = "all",
            Start = 0,
            Count = 50
        };

        var resp = await _pipeline.ExecuteAsync(req);

        Assert.True(resp.IsFromLocalCatalog);
        Assert.Equal(0, _mockSteam.RequestCount);
        Assert.Equal(50, resp.Items.Count);
        Assert.Equal(500, resp.TotalCount);
    }

    [Fact]
    public async Task AuthoritativeLocalCatalog_PaginationAndSorting_ZeroHttpRequests()
    {
        await SeedAuthoritativeCatalogAsync(500);

        // Page 1
        var p1 = await _pipeline.ExecuteAsync(new SearchRequest { RawQuery = "", Start = 0, Count = 25, SortBy = "name", Descending = false });
        // Page 2
        var p2 = await _pipeline.ExecuteAsync(new SearchRequest { RawQuery = "", Start = 25, Count = 25, SortBy = "name", Descending = false });
        // Page 3
        var p3 = await _pipeline.ExecuteAsync(new SearchRequest { RawQuery = "", Start = 50, Count = 25, SortBy = "name", Descending = false });

        Assert.Equal(0, _mockSteam.RequestCount);
        Assert.True(p1.IsFromLocalCatalog && p2.IsFromLocalCatalog && p3.IsFromLocalCatalog);
        Assert.NotEqual(p1.Items[0].AppId, p2.Items[0].AppId);
    }

    [Fact]
    public async Task OfflineMode_SteamThrows_ReturnsLocalResultsSeamlessly()
    {
        await SeedAuthoritativeCatalogAsync(200);

        _mockSteam.ShouldThrowNetworkError = true;

        var req = new SearchRequest
        {
            RawQuery = "Title 0050",
            Start = 0,
            Count = 10
        };

        var resp = await _pipeline.ExecuteAsync(req);

        Assert.True(resp.IsFromLocalCatalog);
        Assert.NotEmpty(resp.Items);
        Assert.Equal((uint)50, resp.Items[0].AppId);
    }

    [Fact]
    public async Task SteamTagCatalogService_LocalCounts_ZeroHttpRequests()
    {
        await SeedAuthoritativeCatalogAsync(200);

        var mockSearch = new CountingMockSteamService();
        var tagService = new SteamTagCatalogService(
            new HttpClient(new FakeHttpMessageHandler()),
            mockSearch,
            NullLogger<SteamTagCatalogService>.Instance,
            cache: null,
            localRepo: _localRepo);

        var tags = new List<SteamTag>
        {
            new SteamTag { TagId = 19, Name = "Action" },
            new SteamTag { TagId = 4182, Name = "Singleplayer" }
        };

        await tagService.ResolveCountsAsync(tags);

        // All 200 items in SeedAuthoritativeCatalogAsync have tags 19 and 4182
        Assert.Equal(200, tags[0].ProductCount);
        Assert.Equal(200, tags[1].ProductCount);
        Assert.Equal(0, mockSearch.RequestCount); // ZERO HTTP search requests!
    }

    private sealed class CountingMockSteamService : ISteamCatalogSearchService
    {
        public int RequestCount { get; private set; }
        public bool ShouldThrowNetworkError { get; set; }

        public Task<SteamSearchPage> SearchAsync(SteamSearchQuery query, CancellationToken ct = default)
        {
            RequestCount++;
            if (ShouldThrowNetworkError)
            {
                throw new HttpRequestException("Simulated Steam server unreachable");
            }
            return Task.FromResult(new SteamSearchPage([], 0, query.Start));
        }

        public Task<int?> GetMatchCountAsync(SteamSearchQuery query, CancellationToken ct = default)
        {
            RequestCount++;
            if (ShouldThrowNetworkError)
            {
                throw new HttpRequestException("Simulated Steam server unreachable");
            }
            return Task.FromResult<int?>(null);
        }

        public Task<IReadOnlyList<SteamTitleSuggestion>> SuggestTitlesAsync(string term, CancellationToken ct = default)
        {
            RequestCount++;
            return Task.FromResult<IReadOnlyList<SteamTitleSuggestion>>([]);
        }

        public Task<IReadOnlyList<SteamStoreEvent>> GetStoreEventsAsync(CancellationToken ct = default)
        {
            return Task.FromResult<IReadOnlyList<SteamStoreEvent>>([]);
        }
    }

    private sealed class FakeHttpMessageHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("[]")
            });
        }
    }
}
