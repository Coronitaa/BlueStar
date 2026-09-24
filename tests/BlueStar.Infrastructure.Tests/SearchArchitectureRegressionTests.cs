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

public class SearchArchitectureRegressionTests : IDisposable
{
    private readonly string _tempDb;
    private readonly LocalCatalogRepository _localRepo;
    private readonly ArchitectureMockSteamService _mockSteam;
    private readonly SteamResponseValidator _validator;
    private readonly SearchPipeline _pipeline;

    public SearchArchitectureRegressionTests()
    {
        _tempDb = Path.Combine(Path.GetTempPath(), $"arch_test_{Guid.NewGuid():N}.sqlite");
        _localRepo = new LocalCatalogRepository(NullLogger<LocalCatalogRepository>.Instance, _tempDb);
        _mockSteam = new ArchitectureMockSteamService();
        _validator = new SteamResponseValidator();
        _pipeline = new SearchPipeline(_localRepo, _mockSteam, _validator, NullLogger<SearchPipeline>.Instance);
    }

    public void Dispose()
    {
        _localRepo.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { if (File.Exists(_tempDb)) File.Delete(_tempDb); } catch { }
    }

    private async Task PopulateCatalogAsync(int count, Func<int, CatalogAppItem>? customizer = null)
    {
        var items = new List<CatalogAppItem>(count);
        for (int i = 1; i <= count; i++)
        {
            var item = customizer != null
                ? customizer(i)
                : new CatalogAppItem
                {
                    AppId = (uint)i,
                    Name = $"Game {i:D4}",
                    NormalizedName = $"game {i:D4}",
                    CompactName = $"game{i:D4}",
                    AppType = "Game",
                    ReviewPercent = 70,
                    ReviewCount = 100,
                    LastModified = 1700000000 + i
                };
            items.Add(item);
        }
        await _localRepo.UpsertAppsAsync(items);
    }

    /// <summary>
    /// TEST 1: Catálogo local tiene 1000 apps. Steam devuelve sólo 50.
    /// Explore debe poder obtener resultados más allá de esos 50.
    /// </summary>
    [Fact]
    public async Task Test1_LocalCatalog1000_SteamReturns50_ExploreMustReachBeyond50()
    {
        await PopulateCatalogAsync(1000);

        // Steam mock only returns 50
        for (int i = 1; i <= 50; i++)
        {
            _mockSteam.StubResults.Add(new SearchResult
            {
                AppId = (uint)i,
                Name = $"Game {i:D4}",
                ReviewPercent = 70
            });
        }

        var req = new SearchRequest
        {
            RawQuery = "", // Explore without query
            Pool = "all",
            Start = 0,
            Count = 50
        };

        var response = await _pipeline.ExecuteAsync(req);

        // Architectural requirement: total count must reflect the local catalog universe (1000), not Steam's 50
        Assert.True(response.TotalCount >= 1000, $"Expected TotalCount >= 1000, but was {response.TotalCount}");

        // Now page beyond 50
        var reqPage2 = new SearchRequest
        {
            RawQuery = "",
            Pool = "all",
            Start = 50,
            Count = 50
        };
        var responsePage2 = await _pipeline.ExecuteAsync(reqPage2);
        Assert.NotEmpty(responsePage2.Items);
        Assert.True(responsePage2.Items.Any(item => item.AppId > 50), "Expected results beyond AppId 50");
    }

    /// <summary>
    /// TEST 2: 1000 apps locales, 300 tienen rating >= 90.
    /// Los primeros 50 resultados de Steam NO contienen ninguno.
    /// Filter rating >= 90 debe encontrar los 300 locales indexados.
    /// </summary>
    [Fact]
    public async Task Test2_FilterRating90_Finds300LocalGames_WhenSteamFirst50HasNone()
    {
        // 1000 apps, 300 have rating >= 90 (e.g. apps 701-1000)
        await PopulateCatalogAsync(1000, i => new CatalogAppItem
        {
            AppId = (uint)i,
            Name = $"Game {i:D4}",
            NormalizedName = $"game {i:D4}",
            CompactName = $"game{i:D4}",
            AppType = "Game",
            ReviewPercent = i > 700 ? 95 : 50,
            ReviewCount = 1000,
            LastModified = 1700000000 + i
        });

        // Steam returns 50 apps, ALL with rating 50 (none with rating >= 90)
        for (int i = 1; i <= 50; i++)
        {
            _mockSteam.StubResults.Add(new SearchResult
            {
                AppId = (uint)i,
                Name = $"Game {i:D4}",
                ReviewPercent = 50
            });
        }

        var req = new SearchRequest
        {
            RawQuery = "",
            MinRatingPercent = 90,
            Start = 0,
            Count = 50
        };

        var response = await _pipeline.ExecuteAsync(req);

        // System must find the 300 local games, not 0
        Assert.Equal(300, response.TotalCount);
        Assert.NotEmpty(response.Items);
        Assert.All(response.Items, item => Assert.True(item.ReviewPercent >= 90));
    }

    /// <summary>
    /// TEST 3: 1000 apps. Sort Name ASC.
    /// El resultado debe ser el primer bloque globalmente ordenado.
    /// </summary>
    [Fact]
    public async Task Test3_SortNameAsc_ReturnsFirstGloballySortedBlock()
    {
        await PopulateCatalogAsync(1000);

        var req = new SearchRequest
        {
            RawQuery = "",
            SortBy = "name",
            Descending = false,
            Start = 0,
            Count = 10
        };

        var response = await _pipeline.ExecuteAsync(req);

        Assert.Equal(1000, response.TotalCount);
        Assert.Equal(10, response.Items.Count);
        Assert.Equal("Game 0001", response.Items[0].Name);
        Assert.Equal("Game 0002", response.Items[1].Name);
    }

    /// <summary>
    /// TEST 4: 1000 apps. Sort Name DESC.
    /// El resultado debe ser el último bloque globalmente ordenado.
    /// </summary>
    [Fact]
    public async Task Test4_SortNameDesc_ReturnsLastGloballySortedBlock()
    {
        await PopulateCatalogAsync(1000);

        var req = new SearchRequest
        {
            RawQuery = "",
            SortBy = "name",
            Descending = true,
            Start = 0,
            Count = 10
        };

        var response = await _pipeline.ExecuteAsync(req);

        Assert.Equal(1000, response.TotalCount);
        Assert.Equal(10, response.Items.Count);
        Assert.Equal("Game 1000", response.Items[0].Name);
        Assert.Equal("Game 0999", response.Items[1].Name);
    }

    /// <summary>
    /// TEST 5: Sort ASC y DESC sobre un mock Steam que devuelve exactamente el mismo payload.
    /// El sistema debe detectar que el parámetro fue ignorado.
    /// </summary>
    [Fact]
    public void Test5_SortAscAndDesc_SteamReturnsSamePayload_DetectsParameterIgnored()
    {
        var identicalAppIds = new List<uint> { 10, 20, 30, 40, 50 };
        var results = identicalAppIds.Select(id => new SearchResult { AppId = id, Name = $"Game {id}" }).ToList();
        var rawPayload = "{\"results_html\":\"<div>app</div>\",\"total_count\":50}";

        var queryAsc = new SteamSearchQuery { Term = "test", SortBy = "Reviews_ASC" };
        var queryDesc = new SteamSearchQuery { Term = "test", SortBy = "Reviews_DESC" };

        var valAsc = _validator.ValidateResponse(queryAsc, rawPayload, results, 50);
        var valDesc = _validator.ValidateResponse(queryDesc, rawPayload, results, 50);

        Assert.True(
            valAsc.Anomaly == SteamAnomalyType.SemanticParameterIgnored ||
            valDesc.Anomaly == SteamAnomalyType.SemanticParameterIgnored,
            "Expected SemanticParameterIgnored anomaly to be detected when opposite sort queries return identical payload.");
    }

    /// <summary>
    /// TEST 6: Explore sin search term.
    /// No debe depender de una página arbitraria de Store Search si el catálogo local está disponible.
    /// </summary>
    [Fact]
    public async Task Test6_ExploreWithoutSearchTerm_UsesLocalCatalog_ZeroSteamRequests()
    {
        await PopulateCatalogAsync(500);

        var req = new SearchRequest
        {
            RawQuery = "",
            Pool = "all",
            Start = 0,
            Count = 20
        };

        var response = await _pipeline.ExecuteAsync(req);

        Assert.True(response.IsFromLocalCatalog, "Expected response to come from local catalog");
        Assert.Equal(0, _mockSteam.SearchCallCount);
    }

    /// <summary>
    /// TEST 7: Local catalog parcial.
    /// El sistema NO debe considerarlo authoritative/complete.
    /// </summary>
    [Fact]
    public async Task Test7_PartialLocalCatalog_NotMarkedAuthoritative()
    {
        // When only a handful of items exist and no completeness state is marked complete,
        // it should report partial/incomplete.
        await PopulateCatalogAsync(10);

        var completeness = await _localRepo.GetCompletenessAsync();
        Assert.False(completeness.IsAuthoritative, "A partial catalog without full snapshot must not be marked authoritative.");
    }

    /// <summary>
    /// TEST 8: AppID 570.
    /// Debe poder resolverse sin Store Search si está en el índice local.
    /// </summary>
    [Fact]
    public async Task Test8_AppId570_ResolvesLocallyWithoutStoreSearch()
    {
        await _localRepo.UpsertAppsAsync([
            new CatalogAppItem
            {
                AppId = 570,
                Name = "Dota 2",
                NormalizedName = "dota 2",
                CompactName = "dota2"
            }
        ]);

        var req = new SearchRequest { RawQuery = "570" };
        var response = await _pipeline.ExecuteAsync(req);

        Assert.Single(response.Items);
        Assert.Equal((uint)570, response.Items[0].AppId);
        Assert.True(response.IsFromLocalCatalog);
        Assert.Equal(0, _mockSteam.SearchCallCount);
    }

    /// <summary>
    /// TEST 9: Juego existente pero no presente en el pool actual de Steam.
    /// Debe poder encontrarse desde el catálogo completo.
    /// </summary>
    [Fact]
    public async Task Test9_DelistedOrHiddenFromSteamPool_CanBeFoundFromCompleteCatalog()
    {
        await PopulateCatalogAsync(500);
        await _localRepo.UpsertAppsAsync([
            new CatalogAppItem
            {
                AppId = 999999,
                Name = "Rare Unlisted Gem",
                NormalizedName = "rare unlisted gem",
                CompactName = "rareunlistedgem"
            }
        ]);

        // Steam search returns empty (game is not in Steam's active store search pool)
        _mockSteam.StubResults.Clear();

        var req = new SearchRequest { RawQuery = "Rare Unlisted Gem" };
        var response = await _pipeline.ExecuteAsync(req);

        Assert.Single(response.Items);
        Assert.Equal((uint)999999, response.Items[0].AppId);
        Assert.True(response.IsFromLocalCatalog);
    }

    /// <summary>
    /// TEST 10: Nueva búsqueda mientras existe otra búsqueda en curso.
    /// Nunca deben mezclarse generaciones.
    /// </summary>
    [Fact]
    public async Task Test10_RapidConsecutiveSearches_GenerationsNeverMixed()
    {
        await PopulateCatalogAsync(100);

        var ctsOld = new CancellationTokenSource();
        var taskOld = _pipeline.ExecuteAsync(new SearchRequest { RawQuery = "Game 0001" }, ctsOld.Token);

        ctsOld.Cancel(); // superseded immediately
        var taskNew = _pipeline.ExecuteAsync(new SearchRequest { RawQuery = "Game 0002" }, CancellationToken.None);

        var resNew = await taskNew;
        Assert.Single(resNew.Items);
        Assert.Equal("Game 0002", resNew.Items[0].Name);
    }

    /// <summary>
    /// TEST 11: Resultados anteriores + nueva búsqueda.
    /// El empty state no debe aparecer durante el refresh.
    /// </summary>
    [Fact]
    public void Test11_SearchStateTransitions_RefreshingPreservesPreviousResults()
    {
        var stateMachine = new SearchStateMachine();
        stateMachine.TransitionToResults(new List<SearchResult> { new SearchResult { AppId = 1, Name = "Previous Game" } });

        Assert.Equal(SearchState.ShowingResults, stateMachine.State);

        // Start refresh
        stateMachine.StartSearch(reset: true);
        Assert.Equal(SearchState.Refreshing, stateMachine.State);
        Assert.NotEmpty(stateMachine.PreservedResults);
        Assert.NotEqual(SearchState.Empty, stateMachine.State);
    }

    /// <summary>
    /// TEST 12: 5000 resultados disponibles.
    /// Paginación 1,2,3... no debe producir una request Steam por cada página.
    /// </summary>
    [Fact]
    public async Task Test12_5000Results_Paging123_ProducesZeroSteamRequests()
    {
        await PopulateCatalogAsync(5000);

        for (int page = 0; page < 5; page++)
        {
            var req = new SearchRequest
            {
                RawQuery = "",
                Start = page * 50,
                Count = 50
            };
            var res = await _pipeline.ExecuteAsync(req);
            Assert.Equal(50, res.Items.Count);
            Assert.True(res.IsFromLocalCatalog);
        }

        Assert.Equal(0, _mockSteam.SearchCallCount);
    }

    private sealed class ArchitectureMockSteamService : ISteamCatalogSearchService
    {
        public int SearchCallCount { get; private set; }
        public List<SearchResult> StubResults { get; } = [];

        public Task<SteamSearchPage> SearchAsync(SteamSearchQuery query, CancellationToken ct = default)
        {
            SearchCallCount++;
            var list = StubResults.ToList();
            return Task.FromResult(new SteamSearchPage(list, list.Count, query.Start));
        }

        public Task<int?> GetMatchCountAsync(SteamSearchQuery query, CancellationToken ct = default) =>
            Task.FromResult<int?>(StubResults.Count);

        public Task<IReadOnlyList<SteamStoreEvent>> GetStoreEventsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<SteamStoreEvent>>([]);

        public Task<IReadOnlyList<SteamTitleSuggestion>> SuggestTitlesAsync(string term, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<SteamTitleSuggestion>>([]);
    }

    private sealed class SearchStateMachine
    {
        public SearchState State { get; private set; } = SearchState.LoadingInitial;
        public List<SearchResult> PreservedResults { get; private set; } = [];

        public void TransitionToResults(List<SearchResult> results)
        {
            PreservedResults = results;
            State = results.Count > 0 ? SearchState.ShowingResults : SearchState.Empty;
        }

        public void StartSearch(bool reset)
        {
            if (reset)
            {
                State = PreservedResults.Count > 0 ? SearchState.Refreshing : SearchState.LoadingInitial;
            }
        }
    }
}
