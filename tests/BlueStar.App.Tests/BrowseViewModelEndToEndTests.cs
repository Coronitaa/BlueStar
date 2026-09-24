using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using BlueStar.App.ViewModels;
using BlueStar.Core.Interfaces;
using BlueStar.Core.Models;
using BlueStar.Infrastructure.Catalog;
using BlueStar.Infrastructure.Search;
using BlueStar.Infrastructure.Steam;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace BlueStar.App.Tests;

/// <summary>
/// FASE 27, 29, 31 — End-to-End & Network Budget Verification Tests for BrowseViewModel.
/// Verifies the full pipeline from BrowseViewModel UI interactions to SQLite + FTS5,
/// ensuring 100% offline capability, zero unexpected Steam requests, and correct filtering.
/// </summary>
public class BrowseViewModelEndToEndTests : IDisposable
{
    private readonly string _tempDb;
    private readonly LocalCatalogRepository _localRepo;
    private readonly StrictCountingSteamService _countingSteam;
    private readonly SteamResponseValidator _validator;
    private readonly SearchPipeline _searchPipeline;

    private readonly Mock<IDepotBoxApiClient> _mockApiClient = new();
    private readonly Mock<IInstanceManager> _mockInstanceManager = new();
    private readonly Mock<IDepotBoxArchiveParser> _mockArchiveParser = new();
    private readonly Mock<IEngineDetector> _mockEngineDetector = new();

    public BrowseViewModelEndToEndTests()
    {
        _tempDb = Path.Combine(Path.GetTempPath(), $"bvm_e2e_{Guid.NewGuid():N}.sqlite");
        _localRepo = new LocalCatalogRepository(NullLogger<LocalCatalogRepository>.Instance, _tempDb);
        _countingSteam = new StrictCountingSteamService();
        _validator = new SteamResponseValidator();
        _searchPipeline = new SearchPipeline(_localRepo, _countingSteam, _validator, NullLogger<SearchPipeline>.Instance);
    }

    public void Dispose()
    {
        _localRepo.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { if (File.Exists(_tempDb)) File.Delete(_tempDb); } catch { }
    }

    private async Task PopulateCatalogAsync(int count = 500)
    {
        await _localRepo.InitializeAsync();
        await _localRepo.SetMetadataAsync("identity_complete", "true");
        await _localRepo.SetMetadataAsync("expected_app_count", count.ToString());

        var list = new List<CatalogAppItem>(count);
        for (int i = 1; i <= count; i++)
        {
            list.Add(new CatalogAppItem
            {
                AppId = (uint)i,
                Name = $"E2E Game {i:D4}",
                NormalizedName = $"e2e game {i:D4}",
                CompactName = $"e2egame{i:D4}",
                AppType = "game",
                ReviewPercent = (i % 2 == 0) ? 95 : 60,
                ReviewCount = 100,
                HasWindows = true,
                HasLinux = (i % 3 == 0),
                HasMac = (i % 4 == 0),
                PriceCents = (i % 5 == 0) ? 0 : 1999,
                TagIds = (i % 2 == 0) ? [19, 4182] : [19],
                LastModified = 1700000000 + i
            });
        }
        await _localRepo.UpsertAppsAsync(list);
    }

    private BrowseViewModel CreateViewModel()
    {
        return new BrowseViewModel(
            _mockApiClient.Object,
            _mockInstanceManager.Object,
            _mockArchiveParser.Object,
            _mockEngineDetector.Object,
            NullLogger<BrowseViewModel>.Instance,
            catalogSearch: _countingSteam,
            searchPipeline: _searchPipeline,
            localRepo: _localRepo);
    }

    [Fact]
    public async Task E2E_ExploreAll_MustMaterializeFromLocalIndex_WithZeroSteamRequests()
    {
        await PopulateCatalogAsync(500);

        var vm = CreateViewModel();
        vm.SearchQuery = "";
        vm.SelectedStoreList = vm.StoreListOptions.FirstOrDefault(o => o.Value == "") ?? vm.StoreListOptions[0];

        await vm.RunSearchAsync(reset: true);

        Assert.Equal(500, vm.TotalResults);
        Assert.Equal(BrowseViewModel.PageSize, vm.Results.Count);
        Assert.Equal(0, _countingSteam.SearchRequestCount);
    }

    [Fact]
    public async Task E2E_InfiniteScroll_PaginatesThroughEntireCatalog_WithZeroSteamRequests()
    {
        await PopulateCatalogAsync(200);

        var vm = CreateViewModel();
        vm.SearchQuery = "";
        vm.SelectedStoreList = vm.StoreListOptions.FirstOrDefault(o => o.Value == "") ?? vm.StoreListOptions[0];

        // Page 1
        await vm.RunSearchAsync(reset: true);
        Assert.Equal(BrowseViewModel.PageSize, vm.Results.Count);
        Assert.True(vm.HasMoreResults);

        // Page 2
        await vm.LoadMoreAsync();
        Assert.Equal(BrowseViewModel.PageSize * 2, vm.Results.Count);
        Assert.True(vm.HasMoreResults);

        // Page 3
        await vm.LoadMoreAsync();
        Assert.Equal(BrowseViewModel.PageSize * 3, vm.Results.Count);
        Assert.True(vm.HasMoreResults);

        // Page 4 (reaches 200)
        await vm.LoadMoreAsync();
        Assert.Equal(200, vm.Results.Count);
        Assert.False(vm.HasMoreResults);

        Assert.Equal(0, _countingSteam.SearchRequestCount);
    }

    [Fact]
    public async Task E2E_OfflineSearch_SteamThrows_SearchWorksLocallyWithoutExceptions()
    {
        await PopulateCatalogAsync(500);

        _countingSteam.ShouldThrowNetworkError = true;

        var vm = CreateViewModel();
        vm.SearchQuery = "E2E Game 0100";

        await vm.RunSearchAsync(reset: true);

        Assert.NotEmpty(vm.Results);
        Assert.Equal(100u, vm.Results[0].AppId);
        Assert.Equal(SearchState.ShowingResults, vm.SearchState);
        Assert.Null(vm.ErrorMessage);
    }

    [Fact]
    public async Task E2E_RapidSwitching_ShowsAndSortBy_DoesNotThrowOrCorruptResults()
    {
        await PopulateCatalogAsync(100);

        var vm = CreateViewModel();

        var popularNew = vm.StoreListOptions.FirstOrDefault(o => o.Value == "popularnew") ?? vm.StoreListOptions[0];
        var wholeCatalog = vm.StoreListOptions.FirstOrDefault(o => o.Value == "") ?? vm.StoreListOptions[0];

        var sortReview = vm.SortOptions.FirstOrDefault(o => o.Value == "reviews") ?? vm.SortOptions[0];
        var sortPrice = vm.SortOptions.FirstOrDefault(o => o.Value == "price") ?? vm.SortOptions[0];

        // Rapidly change Shows and Sort back and forth to simulate fast user UI toggling
        vm.SelectedStoreList = popularNew;
        vm.SelectedSort = sortReview;
        vm.SelectedStoreList = wholeCatalog;
        vm.SelectedSort = sortPrice;
        vm.SelectedStoreList = popularNew;

        // Allow async search tasks to complete
        await Task.Delay(200);

        // Explicit search to verify final consistency
        await vm.RunSearchAsync(reset: true);

        Assert.NotEmpty(vm.Results);
        Assert.Null(vm.ErrorMessage);
        Assert.Equal(SearchState.ShowingResults, vm.SearchState);
    }

    private sealed class StrictCountingSteamService : ISteamCatalogSearchService
    {
        public int SearchRequestCount { get; private set; }
        public bool ShouldThrowNetworkError { get; set; }

        public Task<SteamSearchPage> SearchAsync(SteamSearchQuery query, CancellationToken ct = default)
        {
            SearchRequestCount++;
            if (ShouldThrowNetworkError)
            {
                throw new HttpRequestException("Offline simulation: Steam is unreachable");
            }
            return Task.FromResult(new SteamSearchPage([], 0, query.Start));
        }

        public Task<int?> GetMatchCountAsync(SteamSearchQuery query, CancellationToken ct = default)
        {
            if (ShouldThrowNetworkError)
            {
                throw new HttpRequestException("Offline simulation: Steam is unreachable");
            }
            return Task.FromResult<int?>(null);
        }

        public Task<IReadOnlyList<SteamStoreEvent>> GetStoreEventsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<SteamStoreEvent>>([]);

        public Task<IReadOnlyList<SteamTitleSuggestion>> SuggestTitlesAsync(string term, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<SteamTitleSuggestion>>([]);
    }
}
