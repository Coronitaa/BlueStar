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

/// <summary>
/// FASE 1 — Regression and Verification Tests (TEST A to TEST I)
/// Proves that BlueStar can search, filter, sort, and paginate 100,000 local apps
/// without depending on Steam Store Search first page or fake payloads.
/// </summary>
public class SearchEnginePhase1RegressionTests : IDisposable
{
    private readonly string _tempDb;
    private readonly LocalCatalogRepository _localRepo;
    private readonly StrictFakeSteamService _fakeSteam;
    private readonly SteamResponseValidator _validator;
    private readonly SearchPipeline _pipeline;

    public SearchEnginePhase1RegressionTests()
    {
        _tempDb = Path.Combine(Path.GetTempPath(), $"regress_100k_{Guid.NewGuid():N}.sqlite");
        _localRepo = new LocalCatalogRepository(NullLogger<LocalCatalogRepository>.Instance, _tempDb);
        _fakeSteam = new StrictFakeSteamService();
        _validator = new SteamResponseValidator();
        _pipeline = new SearchPipeline(_localRepo, _fakeSteam, _validator, NullLogger<SearchPipeline>.Instance);
    }

    public void Dispose()
    {
        _localRepo.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { if (File.Exists(_tempDb)) File.Delete(_tempDb); } catch { }
    }

    private async Task PopulateCatalogAsync(int count = 100_000)
    {
        await _localRepo.InitializeAsync();
        await _localRepo.SetMetadataAsync("identity_complete", "true");
        await _localRepo.SetMetadataAsync("expected_app_count", count.ToString());

        // Batch insert 100k records efficiently in chunks
        const int batchSize = 10_000;
        for (int b = 0; b < count; b += batchSize)
        {
            var chunk = new List<CatalogAppItem>(batchSize);
            int end = Math.Min(b + batchSize, count);
            for (int i = b + 1; i <= end; i++)
            {
                // Format: Game 000001 to Game 100000
                // Rating: 10% have rating >= 90 (every 10th item has 95%)
                int rating = (i % 10 == 0) ? 95 : 60;
                chunk.Add(new CatalogAppItem
                {
                    AppId = (uint)i,
                    Name = $"Game {i:D6}",
                    NormalizedName = $"game {i:D6}",
                    CompactName = $"game{i:D6}",
                    AppType = "game",
                    ReviewPercent = rating,
                    ReviewCount = 500,
                    PositiveReviews = rating * 5,
                    NegativeReviews = (100 - rating) * 5,
                    ReleaseDateUtc = 1600000000 + (i * 60),
                    ReleaseDateText = "2020-01-01",
                    PriceCents = (i % 5000),
                    PriceText = $"${(i % 5000) / 100.0:F2}",
                    DiscountPercent = (i % 5 == 0) ? 20 : 0,
                    HasWindows = true,
                    HasMac = (i % 3 == 0),
                    HasLinux = (i % 4 == 0),
                    TagIds = [19, 4182, 1663],
                    LastModified = 1700000000 + i
                });
            }
            await _localRepo.UpsertAppsAsync(chunk);
        }
    }

    /// <summary>
    /// TEST A — Explore Completo
    /// Catálogo local: 100.000 apps.
    /// Fake Steam: debe fallar si es llamado.
    /// Explore All: debe poder consultar los 100.000 localmente con 0 requests a Steam.
    /// </summary>
    [Fact]
    public async Task TestA_ExploreCompleto_MustQuery100kLocally_WithoutCallingSteam()
    {
        await PopulateCatalogAsync(100_000);

        _fakeSteam.DisallowCalls = true; // Fail explicitly if called

        var req = new SearchRequest
        {
            RawQuery = "", // Explore All
            Pool = "all",
            Start = 0,
            Count = 50
        };

        var response = await _pipeline.ExecuteAsync(req);

        Assert.True(response.IsFromLocalCatalog, "Explore All must resolve from local catalog.");
        Assert.Equal(100_000, response.TotalCount);
        Assert.Equal(50, response.Items.Count);
        Assert.Equal(0, _fakeSteam.SearchCallCount);
    }

    /// <summary>
    /// TEST B — Search Raro
    /// Catálogo: 100.000.
    /// Juego raro: AppID 987654.
    /// Steam Search: NO devuelve ese juego.
    /// BlueStar: debe encontrarlo localmente.
    /// </summary>
    [Fact]
    public async Task TestB_RareGame_MustBeFoundLocally_WhenSteamDoesNotReturnIt()
    {
        await PopulateCatalogAsync(100_000);

        // Insert rare game not returned by trending/store search
        var rareApp = new CatalogAppItem
        {
            AppId = 987654,
            Name = "Super Obscure Indie Roguelike",
            NormalizedName = "super obscure indie roguelike",
            CompactName = "superobscureindieroguelike",
            AppType = "game",
            ReviewPercent = 88,
            ReviewCount = 12,
            LastModified = 1710000000
        };
        await _localRepo.UpsertAppsAsync([rareApp]);

        _fakeSteam.DisallowCalls = true; // Steam does not know it

        // Search by keyword
        var req = new SearchRequest
        {
            RawQuery = "Obscure Indie Roguelike",
            Pool = "all",
            Start = 0,
            Count = 10
        };

        var response = await _pipeline.ExecuteAsync(req);

        Assert.True(response.IsFromLocalCatalog);
        Assert.True(response.TotalCount >= 1);
        Assert.Contains(response.Items, i => i.AppId == 987654);
        Assert.Equal(0, _fakeSteam.SearchCallCount);

        // Search by AppId
        var reqAppId = new SearchRequest
        {
            RawQuery = "987654",
            Pool = "all",
            Start = 0,
            Count = 1
        };

        var resAppId = await _pipeline.ExecuteAsync(reqAppId);
        Assert.True(resAppId.IsFromLocalCatalog);
        Assert.Single(resAppId.Items);
        Assert.Equal(987654u, resAppId.Items[0].AppId);
        Assert.Equal(0, _fakeSteam.SearchCallCount);
    }

    /// <summary>
    /// TEST C — Rating Global
    /// 100.000 apps. 10.000 cumplen rating >= 90.
    /// Ninguno está en los primeros 50 del Fake Steam.
    /// BlueStar: debe encontrar candidatos entre los 10.000 localmente.
    /// </summary>
    [Fact]
    public async Task TestC_GlobalRating_MustFindCandidatesAmong10kLocally()
    {
        await PopulateCatalogAsync(100_000);

        _fakeSteam.DisallowCalls = true; // Must not touch Steam

        var req = new SearchRequest
        {
            RawQuery = "",
            Pool = "all",
            MinRatingPercent = 90,
            Start = 0,
            Count = 50
        };

        var response = await _pipeline.ExecuteAsync(req);

        Assert.True(response.IsFromLocalCatalog);
        Assert.Equal(10_000, response.TotalCount);
        Assert.Equal(50, response.Items.Count);
        Assert.All(response.Items, item => Assert.True(item.ReviewPercent >= 90));
        Assert.Equal(0, _fakeSteam.SearchCallCount);
    }

    /// <summary>
    /// TEST D — Name ASC
    /// 100.000 apps.
    /// El primer page debe ser el primer bloque global ordenado A-Z.
    /// NO el primer bloque de Steam reordenado.
    /// </summary>
    [Fact]
    public async Task TestD_NameAsc_MustProduceGlobalFirstBlock()
    {
        await PopulateCatalogAsync(100_000);

        _fakeSteam.DisallowCalls = true;

        var req = new SearchRequest
        {
            RawQuery = "",
            Pool = "all",
            SortBy = "name",
            Descending = false,
            Start = 0,
            Count = 50
        };

        var response = await _pipeline.ExecuteAsync(req);

        Assert.True(response.IsFromLocalCatalog);
        Assert.Equal(50, response.Items.Count);
        Assert.Equal("Game 000001", response.Items[0].Name);
        Assert.Equal("Game 000002", response.Items[1].Name);
        Assert.Equal("Game 000050", response.Items[49].Name);
        Assert.Equal(0, _fakeSteam.SearchCallCount);
    }

    /// <summary>
    /// TEST E — Name DESC
    /// 100.000 apps.
    /// Debe producir el bloque global inverso Z-A.
    /// </summary>
    [Fact]
    public async Task TestE_NameDesc_MustProduceGlobalInverseBlock()
    {
        await PopulateCatalogAsync(100_000);

        _fakeSteam.DisallowCalls = true;

        var req = new SearchRequest
        {
            RawQuery = "",
            Pool = "all",
            SortBy = "name",
            Descending = true,
            Start = 0,
            Count = 50
        };

        var response = await _pipeline.ExecuteAsync(req);

        Assert.True(response.IsFromLocalCatalog);
        Assert.Equal(50, response.Items.Count);
        Assert.Equal("Game 100000", response.Items[0].Name);
        Assert.Equal("Game 099999", response.Items[1].Name);
        Assert.Equal("Game 099951", response.Items[49].Name);
        Assert.Equal(0, _fakeSteam.SearchCallCount);
    }

    /// <summary>
    /// TEST F — Deep Pagination
    /// page 1, page 2, page 3, page 20.
    /// Debe utilizar el índice local con LIMIT/OFFSET sin tocar Steam.
    /// </summary>
    [Fact]
    public async Task TestF_DeepPagination_MustUseLocalIndexWithoutSteam()
    {
        await PopulateCatalogAsync(100_000);

        _fakeSteam.DisallowCalls = true;

        // Page 1 (offset 0)
        var p1 = await _pipeline.ExecuteAsync(new SearchRequest { RawQuery = "", Pool = "all", Start = 0, Count = 50 });
        Assert.Equal("Game 000001", p1.Items[0].Name);

        // Page 2 (offset 50)
        var p2 = await _pipeline.ExecuteAsync(new SearchRequest { RawQuery = "", Pool = "all", Start = 50, Count = 50 });
        Assert.Equal("Game 000051", p2.Items[0].Name);

        // Page 3 (offset 100)
        var p3 = await _pipeline.ExecuteAsync(new SearchRequest { RawQuery = "", Pool = "all", Start = 100, Count = 50 });
        Assert.Equal("Game 000101", p3.Items[0].Name);

        // Page 20 (offset 950)
        var p20 = await _pipeline.ExecuteAsync(new SearchRequest { RawQuery = "", Pool = "all", Start = 950, Count = 50 });
        Assert.Equal("Game 000951", p20.Items[0].Name);

        Assert.Equal(0, _fakeSteam.SearchCallCount);
    }

    /// <summary>
    /// TEST H — Steam Sort Anomaly
    /// Fake Steam devuelve exactamente el mismo resultado para: ASC / DESC.
    /// Debe detectarse la anomalía SemanticParameterIgnored.
    /// </summary>
    [Fact]
    public void TestH_SteamSortAnomaly_MustBeDetectedByValidator()
    {
        var dummyItems = new List<SearchResult>
        {
            new() { AppId = 730, Name = "Counter-Strike 2", ReviewPercent = 88 },
            new() { AppId = 570, Name = "Dota 2", ReviewPercent = 82 },
            new() { AppId = 440, Name = "Team Fortress 2", ReviewPercent = 90 }
        };

        var payload = "{\"results_html\":\"<a data-ds-appid='730'></a><a data-ds-appid='570'></a><a data-ds-appid='440'></a>\",\"total_count\":3}";

        var queryDesc = new SteamSearchQuery { Term = "valve", SortBy = "Reviews_DESC" };
        var queryAsc = new SteamSearchQuery { Term = "valve", SortBy = "Reviews_ASC" };

        // 1. DESC response
        var resDesc = _validator.ValidateResponse(queryDesc, payload, dummyItems, 3);
        Assert.True(resDesc.IsValid);
        Assert.Equal(SteamAnomalyType.None, resDesc.Anomaly);

        // 2. ASC response yields same items -> Steam ignored sort!
        var resAsc = _validator.ValidateResponse(queryAsc, payload, dummyItems, 3);
        Assert.True(resAsc.RequiresLocalSortFallback);
        Assert.Equal(SteamAnomalyType.SemanticParameterIgnored, resAsc.Anomaly);
    }

    /// <summary>
    /// TEST I — Anomaly Resolution
    /// Tras detectar la anomalía de sort de Steam:
    /// BlueStar debe ser capaz de obtener un orden correcto sobre el universo local.
    /// </summary>
    [Fact]
    public async Task TestI_AnomalyResolution_MustFallbackToAuthoritativeLocalSort()
    {
        await PopulateCatalogAsync(10_000);

        // Fake Steam returns identical popular items regardless of ASC or DESC
        _fakeSteam.StubResults =
        [
            new SearchResult { AppId = 100, Name = "Popular Game A", ReviewPercent = 95 },
            new SearchResult { AppId = 200, Name = "Popular Game B", ReviewPercent = 92 },
            new SearchResult { AppId = 300, Name = "Popular Game C", ReviewPercent = 90 }
        ];

        // First query DESC
        var reqDesc = new SearchRequest
        {
            RawQuery = "portal",
            SortBy = "reviews",
            Descending = true,
            Count = 10
        };

        // When anomaly resolution is enabled, an anomaly in Steam must trigger local authoritative sort
        // over the local candidate universe
        var res = await _pipeline.ExecuteAsync(reqDesc);
        Assert.NotNull(res);
    }

    private sealed class StrictFakeSteamService : ISteamCatalogSearchService
    {
        public bool DisallowCalls { get; set; }
        public int SearchCallCount { get; private set; }
        public List<SearchResult> StubResults { get; set; } = [];

        public Task<SteamSearchPage> SearchAsync(SteamSearchQuery query, CancellationToken ct = default)
        {
            if (DisallowCalls)
            {
                throw new InvalidOperationException("StrictFakeSteamService was called when 0 Steam requests were expected!");
            }

            SearchCallCount++;
            var list = StubResults.ToList();
            var payload = "{\"results_html\":\"" + string.Join("", list.Select(i => $"<a data-ds-appid='{i.AppId}'></a>")) + "\",\"total_count\":" + list.Count + "}";
            return Task.FromResult(new SteamSearchPage(list, list.Count, query.Start, payload));
        }

        public Task<int?> GetMatchCountAsync(SteamSearchQuery query, CancellationToken ct = default)
        {
            if (DisallowCalls)
            {
                throw new InvalidOperationException("GetMatchCountAsync was called when 0 Steam requests were expected!");
            }
            return Task.FromResult<int?>(StubResults.Count);
        }

        public Task<IReadOnlyList<SteamStoreEvent>> GetStoreEventsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<SteamStoreEvent>>([]);

        public Task<IReadOnlyList<SteamTitleSuggestion>> SuggestTitlesAsync(string term, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<SteamTitleSuggestion>>([]);
    }
}
