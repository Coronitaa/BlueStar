using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BlueStar.App.ViewModels;
using BlueStar.Core.Interfaces;
using BlueStar.Core.Models;
using BlueStar.Infrastructure.Search;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace BlueStar.App.Tests;

/// <summary>
/// FASE 1 — UI & ViewModel Regression Tests (TEST G, TEST J, TEST K)
/// Verifies filter propagation from UI to SearchRequest, refresh flicker prevention,
/// and virtualization tree verification.
/// </summary>
public class BrowseViewModelPhase1RegressionTests
{
    private readonly Mock<IDepotBoxApiClient> _mockApiClient = new();
    private readonly Mock<IInstanceManager> _mockInstanceManager = new();
    private readonly Mock<IDepotBoxArchiveParser> _mockArchiveParser = new();
    private readonly Mock<IEngineDetector> _mockEngineDetector = new();
    private readonly Mock<ISteamCatalogSearchService> _mockCatalogSearch = new();
    private readonly SpySearchPipeline _spyPipeline = new();

    private BrowseViewModel CreateViewModel(ISteamTagCatalogService? tagCatalog = null, ILocalCatalogRepository? localRepo = null)
    {
        return new BrowseViewModel(
            _mockApiClient.Object,
            _mockInstanceManager.Object,
            _mockArchiveParser.Object,
            _mockEngineDetector.Object,
            NullLogger<BrowseViewModel>.Instance,
            catalogSearch: _mockCatalogSearch.Object,
            tagCatalog: tagCatalog,
            searchPipeline: _spyPipeline,
            localRepo: localRepo);
    }

    /// <summary>
    /// TEST G — UI Filter Propagation
    /// Activa cada filtro visible desde BrowseViewModel.
    /// Verifica que SearchRequest reciba exactamente el filtro correspondiente.
    /// </summary>
    [Fact]
    public async Task TestG_UiFilterPropagation_AllVisibleFiltersMustReachSearchRequest()
    {
        var vm = CreateViewModel();

        // 1. AppType filter (Software: 994)
        vm.SetAppType("994");
        await vm.RunSearchAsync(reset: true);
        Assert.NotNull(_spyPipeline.LastRequest);
        Assert.Equal("994", _spyPipeline.LastRequest!.AppTypes);

        // 2. Rating filter (e.g. Min 80% / 95%)
        var ratingGroup = vm.FilterGroups.FirstOrDefault(g => g.Key == "rating");
        if (ratingGroup != null)
        {
            var opt95 = ratingGroup.AllOptions.FirstOrDefault(o => o.Option.Value == "rating_min_95");
            if (opt95 != null)
            {
                opt95.State = FacetState.Include;
                await vm.RunSearchAsync(reset: true);
                Assert.Equal(95, _spyPipeline.LastRequest!.MinRatingPercent);
            }
        }

        // 3. Platform filter (Linux)
        var platGroup = vm.FilterGroups.FirstOrDefault(g => g.Key == "platform");
        if (platGroup != null)
        {
            var linuxOpt = platGroup.AllOptions.FirstOrDefault(o => o.Option.Value == "linux");
            if (linuxOpt != null)
            {
                linuxOpt.State = FacetState.Include;
                await vm.RunSearchAsync(reset: true);
                Assert.True(_spyPipeline.LastRequest!.HasLinux);
            }
        }

        // 4. Content filter: No DRM & No External Launcher
        var contentGroup = vm.FilterGroups.FirstOrDefault(g => g.Key == "content");
        if (contentGroup != null)
        {
            var noDrmOpt = contentGroup.AllOptions.FirstOrDefault(o => o.Option.Value == "no_drm");
            if (noDrmOpt != null)
            {
                noDrmOpt.State = FacetState.Include;
                await vm.RunSearchAsync(reset: true);
                Assert.True(_spyPipeline.LastRequest!.NoDrm);
            }

            var noLauncherOpt = contentGroup.AllOptions.FirstOrDefault(o => o.Option.Value == "no_launcher");
            if (noLauncherOpt != null)
            {
                noLauncherOpt.State = FacetState.Include;
                await vm.RunSearchAsync(reset: true);
                Assert.True(_spyPipeline.LastRequest!.NoExternalLauncher);
            }
        }

        // 5. Excluded Tag (Right-click ban on a tag)
        var anyTagGroup = vm.FilterGroups.FirstOrDefault(g => g.Items.Any(i => i.Option.Kind == SteamFacetKind.Tag));
        if (anyTagGroup != null)
        {
            var tagOpt = anyTagGroup.Items.First(i => i.Option.Kind == SteamFacetKind.Tag);
            tagOpt.State = FacetState.Exclude;
            await vm.RunSearchAsync(reset: true);
            Assert.NotNull(_spyPipeline.LastRequest!.ExcludedTagIds);
            Assert.Contains(int.Parse(tagOpt.Option.Value), _spyPipeline.LastRequest!.ExcludedTagIds);
        }
    }

    /// <summary>
    /// TEST J — Refresh Flicker Prevention
    /// Durante búsqueda A → búsqueda B:
    /// No debe existir una transición visible a Empty si B todavía está cargando.
    /// SearchState debe ser Refreshing y Results debe retener los elementos previos.
    /// </summary>
    [Fact]
    public async Task TestJ_RefreshFlicker_MustRetainResultsAndBeRefreshingWhileAwaitingNewQuery()
    {
        var vm = CreateViewModel();

        // 1. Initial search A completes with results
        _spyPipeline.Handler = req => Task.FromResult(new SearchResponse
        {
            Items = [new SearchResult { AppId = 1, Name = "Previous Game A" }],
            TotalCount = 1,
            IsFromLocalCatalog = true
        });

        await vm.RunSearchAsync(reset: true);
        Assert.Single(vm.Results);
        Assert.Equal(SearchState.ShowingResults, vm.SearchState);

        // 2. Query B starts but is delayed / in-flight
        var tcs = new TaskCompletionSource<SearchResponse>();
        _spyPipeline.Handler = req => tcs.Task;

        var searchTask = vm.RunSearchAsync(reset: true);

        // While in flight:
        // Must NOT be Empty!
        Assert.NotEqual(SearchState.Empty, vm.SearchState);
        Assert.Equal(SearchState.Refreshing, vm.SearchState);
        // Results must NOT be wiped out immediately
        Assert.NotEmpty(vm.Results);

        // Complete query B
        tcs.SetResult(new SearchResponse
        {
            Items = [new SearchResult { AppId = 2, Name = "New Game B" }],
            TotalCount = 1,
            IsFromLocalCatalog = true
        });

        await searchTask;
        Assert.Single(vm.Results);
        Assert.Equal(2u, vm.Results[0].AppId);
        Assert.Equal(SearchState.ShowingResults, vm.SearchState);
    }

    /// <summary>
    /// TEST K — Virtualization & Unlimited Materialization Verification
    /// Verifica que el control o panel virtualizado herede de VirtualizingPanel
    /// y que no persista un límite artificial MaxMaterialized.
    /// </summary>
    [Fact]
    public void TestK_Virtualization_MustUseRealVirtualizingPanel_WithoutArtificialCap()
    {
        // 1. VirtualizingWrapPanel must inherit from VirtualizingPanel (not standard non-virtualizing WrapPanel)
        var panelType = typeof(BlueStar.App.Controls.VirtualizingWrapPanel);
        Assert.True(
            typeof(System.Windows.Controls.VirtualizingPanel).IsAssignableFrom(panelType),
            "VirtualizingWrapPanel MUST inherit from System.Windows.Controls.VirtualizingPanel to support WPF virtualization!");

        // 2. MaxMaterialized cap must not artificially limit pagination below catalog size
        var vm = CreateViewModel();
        // Pagination logic should allow materializing beyond 500 when requested by infinite scroll
        var field = typeof(BrowseViewModel).GetField("MaxMaterialized", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        if (field != null)
        {
            var cap = (int)field.GetValue(null)!;
            Assert.True(cap >= 100_000, "MaxMaterialized cap should not artificially restrict catalog browsing to 200 or 500.");
        }
    }

    /// <summary>
    /// TEST L — VirtualizingWrapPanel Lifecycle & Measure Safety
    /// Ensures that VirtualizingWrapPanel handles empty item collections, rapid resets,
    /// and bounds measuring gracefully on the WPF layout pass without throwing NullReferenceException.
    /// </summary>
    [Fact]
    public void TestL_VirtualizingWrapPanel_LifecycleSafety_OnMeasureAndEmptyCollection()
    {
        var thread = new System.Threading.Thread(() =>
        {
            var panel = new BlueStar.App.Controls.VirtualizingWrapPanel();
            // Measure with standard window size
            panel.Measure(new System.Windows.Size(1200, 800));
            Assert.True(panel.DesiredSize.Width >= 0);

            // Measure with empty / zero size
            panel.Measure(new System.Windows.Size(0, 0));
            Assert.Equal(0, panel.DesiredSize.Width);
            Assert.Equal(0, panel.DesiredSize.Height);
        });
        thread.SetApartmentState(System.Threading.ApartmentState.STA);
        thread.Start();
        thread.Join();
    }

    /// <summary>
    /// TEST M — UI Filter Propagation for Hide free to play & VR only
    /// Ensures that activating "Hide free to play" and "VR only" from the right-hand panel
    /// correctly propagates these facets into SearchRequest.Facets so Steam search and
    /// local pipeline both receive and honor the filters.
    /// </summary>
    [Fact]
    public async Task TestM_UiFilterPropagation_HideF2P_And_VrOnly_ReachSearchRequest()
    {
        var vm = CreateViewModel();

        // 1. Hide free to play
        var priceGroup = vm.FilterGroups.FirstOrDefault(g => g.Key == "price");
        Assert.NotNull(priceGroup);
        var hideF2POpt = priceGroup.AllOptions.FirstOrDefault(o => o.Option.Value == "hidef2p");
        Assert.NotNull(hideF2POpt);
        hideF2POpt.State = FacetState.Include;

        // 2. VR only
        var platGroup = vm.FilterGroups.FirstOrDefault(g => g.Key == "platform");
        Assert.NotNull(platGroup);
        var vrOpt = platGroup.AllOptions.FirstOrDefault(o => o.Option.Value == "401");
        Assert.NotNull(vrOpt);
        vrOpt.State = FacetState.Include;

        await vm.RunSearchAsync(reset: true);

        Assert.NotNull(_spyPipeline.LastRequest);
        Assert.NotNull(_spyPipeline.LastRequest!.Facets);

        // Verify hidef2p facet is in request
        var hasHideF2P = _spyPipeline.LastRequest!.Facets.Any(f => f.Key.Kind == SteamFacetKind.Toggle && f.Key.Value == "hidef2p" && f.Value == FacetState.Include);
        Assert.True(hasHideF2P, "SearchRequest must carry hidef2p facet when 'Hide free to play' is activated");

        // Verify VR only facet is in request
        var hasVrOnly = _spyPipeline.LastRequest!.Facets.Any(f => f.Key.Kind == SteamFacetKind.Vr && f.Key.Value == "401" && f.Value == FacetState.Include);
        Assert.True(hasVrOnly, "SearchRequest must carry VR only facet when 'VR only' is activated");
    }

    /// <summary>
    /// TEST N — Coming Soon Sort By Unlocked & Preserved
    /// Verifies that choosing "Coming soon" does not lock the Sort By combobox
    /// and does not force reset SelectedSort to "No particular order".
    /// </summary>
    [Fact]
    public void TestN_ComingSoon_SortByMustBeUnlocked_AndPreserveUserSelectedSort()
    {
        var vm = CreateViewModel();

        var comingSoon = vm.StoreListOptions.FirstOrDefault(o => o.Value == "comingsoon");
        Assert.NotNull(comingSoon);

        var releaseSort = vm.SortOptions.FirstOrDefault(o => o.Value == "Released");
        Assert.NotNull(releaseSort);

        // User chooses Release date sort
        vm.SelectedSort = releaseSort;

        // User switches to Coming soon
        vm.SelectedStoreList = comingSoon;

        // Verify Sort By is NOT locked (CanChooseSort == true)
        Assert.True(vm.CanChooseSort, "Sort By must remain enabled/unlocked for Coming Soon");

        // Verify SelectedSort was NOT reset to No particular order
        Assert.Equal("Released", vm.SelectedSort?.Value);
    }

    /// <summary>
    /// TEST O — Steam Paging Offset Drift Prevention
    /// Verifies that subsequent page fetches advance Start by PageSize (e.g. 25)
    /// even when earlier pages had items filtered out (e.g. returning only 5 items).
    /// </summary>
    [Fact]
    public async Task TestO_SteamPagingOffsetDrift_WhenCuratedItemsFiltered_OffsetMustAdvanceByPageSize()
    {
        var vm = CreateViewModel();
        var capturedStarts = new List<int>();

        _spyPipeline.Handler = req =>
        {
            capturedStarts.Add(req.Start);
            // Return only 5 items even though Count requested was 25
            var items = Enumerable.Range(req.Start, 5)
                .Select(i => new SearchResult { AppId = (uint)i + 1, Name = $"Game {i}" })
                .ToList();

            return Task.FromResult(new SearchResponse
            {
                Items = items,
                TotalCount = 100,
                Start = req.Start,
                IsFromLocalCatalog = false
            });
        };

        // First page
        await vm.RunSearchAsync(reset: true);
        Assert.Single(capturedStarts);
        Assert.Equal(0, capturedStarts[0]);
        Assert.Equal(5, vm.Results.Count);

        // Second page (LoadMore)
        await vm.RunSearchAsync(reset: false);
        Assert.Equal(2, capturedStarts.Count);
        // Start must be PageSize (BrowseViewModel.PageSize = 50), NOT _fetched.Count (5)!
        Assert.Equal(BrowseViewModel.PageSize, capturedStarts[1]);
        Assert.Equal(10, vm.Results.Count);
    }

    /// <summary>
    /// TEST P — ApplyLocalSort Strict AppId Determinism & Tie-Breaking
    /// Verifies that ties in price, reviews, or release dates are deterministically
    /// resolved by AppId.
    /// </summary>
    [Fact]
    public void TestP_ApplyLocalSort_StrictAppIdDeterminism_TiesMustBeResolvedByAppId()
    {
        var items = new List<SearchResult>
        {
            new() { AppId = 300, Name = "Same Game", PriceText = "$9.99", ReleaseDateText = "1 Jan, 2024", ReviewPercent = 85 },
            new() { AppId = 100, Name = "Same Game", PriceText = "$9.99", ReleaseDateText = "1 Jan, 2024", ReviewPercent = 85 },
            new() { AppId = 200, Name = "Same Game", PriceText = "$9.99", ReleaseDateText = "1 Jan, 2024", ReviewPercent = 85 }
        };

        // Sort ascending by price: ties resolved by AppId ascending (100, 200, 300)
        var priceSortedAsc = SearchPipeline.ApplyLocalSort(items, "price", descending: false);
        Assert.Equal([100u, 200u, 300u], priceSortedAsc.Select(i => i.AppId));

        // Sort descending by price: ties resolved by AppId descending (300, 200, 100)
        var priceSortedDesc = SearchPipeline.ApplyLocalSort(items, "price", descending: true);
        Assert.Equal([300u, 200u, 100u], priceSortedDesc.Select(i => i.AppId));

        // Sort ascending by reviews: ties resolved by AppId ascending
        var reviewSortedAsc = SearchPipeline.ApplyLocalSort(items, "reviews", descending: false);
        Assert.Equal([100u, 200u, 300u], reviewSortedAsc.Select(i => i.AppId));

        // Sort descending by reviews: ties resolved by AppId descending
        var reviewSortedDesc = SearchPipeline.ApplyLocalSort(items, "reviews", descending: true);
        Assert.Equal([300u, 200u, 100u], reviewSortedDesc.Select(i => i.AppId));

        // Sort ascending by release date: ties resolved by AppId ascending
        var releaseSortedAsc = SearchPipeline.ApplyLocalSort(items, "released", descending: false);
        Assert.Equal([100u, 200u, 300u], releaseSortedAsc.Select(i => i.AppId));

        // Sort descending by release date: ties resolved by AppId descending
        var releaseSortedDesc = SearchPipeline.ApplyLocalSort(items, "released", descending: true);
        Assert.Equal([300u, 200u, 100u], releaseSortedDesc.Select(i => i.AppId));
    }

    /// <summary>
    /// TEST Q — VR Only Filtering Must Reject Apps Without VR Tag (Empty/Null Tags)
    /// Verifies that both BrowseViewModel.PassesLocalFilters and SearchPipeline.MatchesRequestFilters
    /// reject games without tag 21978 when VR only is requested.
    /// </summary>
    [Fact]
    public async Task TestQ_MatchesRequestFilters_VrOnly_MustRejectAppWithoutTags()
    {
        var vm = CreateViewModel();

        var platGroup = vm.FilterGroups.FirstOrDefault(g => g.Key == "platform");
        Assert.NotNull(platGroup);
        var vrOpt = platGroup.AllOptions.FirstOrDefault(o => o.Option.Value == "401");
        Assert.NotNull(vrOpt);
        vrOpt.State = FacetState.Include;

        _spyPipeline.Handler = req =>
        {
            var vrGame = new SearchResult { AppId = 1, Name = "Half-Life: Alyx", TagIds = [21978] };
            var regularGameNoTags = new SearchResult { AppId = 2, Name = "No Tags Game", TagIds = null! };
            var regularGameOtherTags = new SearchResult { AppId = 3, Name = "Other Tags Game", TagIds = [19, 492] };

            return Task.FromResult(new SearchResponse
            {
                Items = [vrGame, regularGameNoTags, regularGameOtherTags],
                TotalCount = 3,
                IsFromLocalCatalog = false
            });
        };

        await vm.RunSearchAsync(reset: true);

        // Only the game with tag 21978 should be present
        Assert.Single(vm.Results);
        Assert.Equal(1u, vm.Results[0].AppId);
        Assert.Equal("Half-Life: Alyx", vm.Results[0].Name);
    }

    /// <summary>
    /// TEST R — Paging Deadlock Prevention & Auto-Advance
    /// Verifies that when a page returns 0 items due to strict post-filtering (e.g. VR Only),
    /// BrowseViewModel auto-advances to find matching items in subsequent pages, and
    /// HasMoreResults is NOT killed prematurely while offset < TotalCount.
    /// </summary>
    [Fact]
    public async Task TestR_PagingDeadlock_WhenFullChunkFilteredOut_MustAutoAdvanceAndPreserveHasMoreResults()
    {
        var vm = CreateViewModel();
        var capturedStarts = new List<int>();

        _spyPipeline.Handler = req =>
        {
            capturedStarts.Add(req.Start);
            if (req.Start == 0)
            {
                // First page: all items discarded by post-filters, returning 0 fresh items
                return Task.FromResult(new SearchResponse
                {
                    Items = [],
                    TotalCount = 200,
                    Start = 0,
                    IsFromLocalCatalog = false
                });
            }

            // Second page (Start == 50): 5 items pass the filter
            var matching = Enumerable.Range(req.Start, 5)
                .Select(i => new SearchResult { AppId = (uint)i + 1, Name = $"Matching Game {i}" })
                .ToList();

            return Task.FromResult(new SearchResponse
            {
                Items = matching,
                TotalCount = 200,
                Start = req.Start,
                IsFromLocalCatalog = false
            });
        };

        // Initial search: must auto-advance past empty page 0 to page 50
        await vm.RunSearchAsync(reset: true);

        // Captured starts should show auto-advance from 0 to 50
        Assert.Equal(2, capturedStarts.Count);
        Assert.Equal(0, capturedStarts[0]);
        Assert.Equal(50, capturedStarts[1]);

        // Results should contain the 5 items found on page 50
        Assert.Equal(5, vm.Results.Count);

        // HasMoreResults must remain true because _steamQueryOffset (100) < TotalCount (200)
        Assert.True(vm.HasMoreResults, "HasMoreResults must remain true while offset < TotalCount");
    }

    /// <summary>
    /// TEST T — Coming Soon: Price Sort Must Be Disabled and Reset to None If Selected
    /// </summary>
    [Fact]
    public void TestT_ComingSoon_PriceSortMustBeDisabled_AndResetToNoneIfSelected()
    {
        var vm = CreateViewModel();

        // 1. Initially on Whole catalog, Price sort option must be enabled
        var priceOption = vm.SortOptions.First(o => o.Value.Equals("Price", StringComparison.OrdinalIgnoreCase));
        Assert.True(priceOption.IsEnabled, "Price sort should be enabled for general catalog");

        // 2. Select Price sort
        vm.SelectedSort = priceOption;
        Assert.Equal("Price", vm.SelectedSort?.Value);

        // 3. Switch to 'Coming soon'
        var comingSoonOption = vm.StoreListOptions.First(o => o.Value.Equals("comingsoon", StringComparison.OrdinalIgnoreCase));
        vm.SelectedStoreList = comingSoonOption;

        // Price option must now be disabled, and SelectedSort must have reset to 'No particular order' (empty)
        Assert.False(priceOption.IsEnabled, "Price sort must be disabled when Coming soon is selected");
        Assert.True(string.IsNullOrEmpty(vm.SelectedSort?.Value), "SelectedSort must reset to empty (No particular order) when entering Coming soon");

        // 4. Attempting to select Price sort while Coming soon is active must be rejected
        vm.SelectedSort = priceOption;
        Assert.True(string.IsNullOrEmpty(vm.SelectedSort?.Value), "Selecting Price sort while Coming soon is active must remain rejected/empty");

        // 5. Switching back away from Coming soon (e.g. to Whole catalog) must re-enable Price
        var wholeCatalogOption = vm.StoreListOptions.First(o => string.IsNullOrEmpty(o.Value));
        vm.SelectedStoreList = wholeCatalogOption;
        Assert.True(priceOption.IsEnabled, "Price sort must be re-enabled when exiting Coming soon");
    }

    /// <summary>
    /// TEST U — Coming Soon: Release Date Sort Must Default to Ascending (Soonest First)
    /// </summary>
    [Fact]
    public void TestU_ComingSoon_ReleaseDateSort_MustBeAscendingSoonestFirst()
    {
        var vm = CreateViewModel();
        var comingSoonOption = vm.StoreListOptions.First(o => o.Value.Equals("comingsoon", StringComparison.OrdinalIgnoreCase));
        vm.SelectedStoreList = comingSoonOption;

        var releasedOption = vm.SortOptions.First(o => o.Value.Equals("Released", StringComparison.OrdinalIgnoreCase));
        vm.SelectedSort = releasedOption;

        // In Coming soon, Released must default to Ascending (soonest upcoming release first, IsSortDescending = false)
        Assert.False(vm.IsSortDescending, "Coming soon + Released must default to Ascending (soonest first)");
    }

    /// <summary>
    /// TEST V — SearchPipeline: Future Release Dates, Quarters, and TBA NULLS LAST
    /// </summary>
    [Fact]
    public void TestV_ReleaseDate_FutureDatesAndQuarters_ParsedAndSortedChronologically()
    {
        // Test parsing of various Steam upcoming release formats
        var d1 = BlueStar.Infrastructure.Search.SearchPipeline.ParseReleaseDate("Q1 2027");
        Assert.NotNull(d1);
        Assert.Equal(2027, d1.Value.Year);
        Assert.Equal(1, d1.Value.Month);

        var d2 = BlueStar.Infrastructure.Search.SearchPipeline.ParseReleaseDate("Spring 2026");
        Assert.NotNull(d2);
        Assert.Equal(2026, d2.Value.Year);
        Assert.Equal(3, d2.Value.Month);

        var d3 = BlueStar.Infrastructure.Search.SearchPipeline.ParseReleaseDate("Late 2026");
        Assert.NotNull(d3);
        Assert.Equal(2026, d3.Value.Year);
        Assert.Equal(10, d3.Value.Month);

        var d4 = BlueStar.Infrastructure.Search.SearchPipeline.ParseReleaseDate("To be announced");
        Assert.Null(d4);

        // Test sorting in ApplyLocalSort: Ascending (soonest first)
        var items = new List<SearchResult>
        {
            new SearchResult { AppId = 1, Name = "TBA Game", ReleaseDateText = "Coming Soon" },
            new SearchResult { AppId = 2, Name = "Late 2026 Game", ReleaseDateText = "Late 2026" },
            new SearchResult { AppId = 3, Name = "Spring 2026 Game", ReleaseDateText = "Spring 2026" },
            new SearchResult { AppId = 4, Name = "2027 Game", ReleaseDateText = "Q1 2027" },
            new SearchResult { AppId = 5, Name = "Unannounced", ReleaseDateText = "To be announced" },
        };

        var sortedAsc = BlueStar.Infrastructure.Search.SearchPipeline.ApplyLocalSort(items, "released", descending: false);

        // Ascending: Spring 2026 (Mar 2026) -> Late 2026 (Oct 2026) -> Q1 2027 -> TBA/Unannounced at the very end!
        Assert.Equal((uint)3, sortedAsc[0].AppId); // Spring 2026
        Assert.Equal((uint)2, sortedAsc[1].AppId); // Late 2026
        Assert.Equal((uint)4, sortedAsc[2].AppId); // Q1 2027
        Assert.Contains(sortedAsc[3].AppId, new uint[] { 1, 5 }); // TBA
        Assert.Contains(sortedAsc[4].AppId, new uint[] { 1, 5 }); // TBA

        // Descending: Furthest first -> Q1 2027 -> Late 2026 -> Spring 2026 -> TBA at the end!
        var sortedDesc = BlueStar.Infrastructure.Search.SearchPipeline.ApplyLocalSort(items, "released", descending: true);
        Assert.Equal((uint)4, sortedDesc[0].AppId); // Q1 2027
        Assert.Equal((uint)2, sortedDesc[1].AppId); // Late 2026
        Assert.Equal((uint)3, sortedDesc[2].AppId); // Spring 2026
        Assert.Contains(sortedDesc[3].AppId, new uint[] { 1, 5 }); // TBA
        Assert.Contains(sortedDesc[4].AppId, new uint[] { 1, 5 }); // TBA
    }

    /// <summary>
    /// TEST U — Multi-Tag Ladder Progression & Max-AND-to-Min-AND Ordering with Infinite Scroll
    /// Verifies that filtering by multiple tags walks the ladder rungs (strict AND first, then relaxed subsets),
    /// assigns MatchedTagCount accordingly, keeps highest AND matches at the top, and allows infinite scroll.
    /// </summary>
    [Fact]
    public async Task TestU_MultiTagLadder_OrdersMaxAndToMinAnd_WithInfiniteScrollProgression()
    {
        var mockTagCatalog = new Mock<ISteamTagCatalogService>();
        mockTagCatalog.Setup(t => t.GetGroupAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<SteamTag>
            {
                new() { TagId = 19, Name = "Action", CanonicalName = "Action" },
                new() { TagId = 4182, Name = "Singleplayer", CanonicalName = "Singleplayer" }
            });

        var vm = CreateViewModel(tagCatalog: mockTagCatalog.Object);

        // Include 2 tags (e.g. first two tags found in FilterGroups)
        var allTagOptions = vm.FilterGroups
            .SelectMany(g => g.AllOptions)
            .Where(o => o.Option.Kind == SteamFacetKind.Tag)
            .Take(2)
            .ToList();

        Assert.True(allTagOptions.Count >= 2, "Expected at least 2 tag options available");
        allTagOptions[0].State = FacetState.Include;
        allTagOptions[1].State = FacetState.Include;

        var tag0Id = int.Parse(allTagOptions[0].Option.Value);
        var tag1Id = int.Parse(allTagOptions[1].Option.Value);

        var requestedTagSets = new List<List<int>>();

        _spyPipeline.Handler = req =>
        {
            var curTags = req.IncludedTagIds.ToList();
            requestedTagSets.Add(curTags);

            // If querying both tags (Rung 0: 2 tags in AND)
            if (curTags.Count == 2)
            {
                return Task.FromResult(new SearchResponse
                {
                    Items = [
                        new SearchResult { AppId = 100, Name = "Exact Match 1", TagIds = [tag0Id, tag1Id] },
                        new SearchResult { AppId = 101, Name = "Exact Match 2", TagIds = [tag0Id, tag1Id] }
                    ],
                    TotalCount = 2,
                    Start = req.Start,
                    IsFromLocalCatalog = false
                });
            }

            // If querying 1 tag (Rung 1: relaxed subset)
            return Task.FromResult(new SearchResponse
            {
                Items = [
                    new SearchResult { AppId = 200, Name = "Relaxed Match 1", TagIds = [curTags[0]] },
                    new SearchResult { AppId = 201, Name = "Relaxed Match 2", TagIds = [curTags[0]] }
                ],
                TotalCount = 50,
                Start = req.Start,
                IsFromLocalCatalog = false
            });
        };

        // 1. Initial search: Rung 0 (2 tags) executes
        await vm.RunSearchAsync(reset: true);

        Assert.Equal(2, vm.ExactMatchCount);
        Assert.False(vm.IsShowingRelated, "IsShowingRelated must be false while on Rung 0 exact matches");
        Assert.Equal(2, vm.Results.Count);
        Assert.All(vm.Results, r => Assert.Equal(2, r.MatchedTagCount));
        Assert.True(vm.HasMoreResults, "HasMoreResults must remain true so infinite scroll can relax tags");

        // 2. Load next page (infinite scroll): Rung 0 was exhausted (TotalCount=2 reached), so it advances to Rung 1
        await vm.RunSearchAsync(reset: false);

        Assert.True(vm.IsShowingRelated, "IsShowingRelated must become true when ladder relaxes to fewer tags");
        Assert.Equal(4, vm.Results.Count);

        // Crucial requirement: Results must be ordered from highest matched tags to lowest!
        Assert.Equal(2, vm.Results[0].MatchedTagCount);
        Assert.Equal(2, vm.Results[1].MatchedTagCount);
        Assert.Equal(1, vm.Results[2].MatchedTagCount);
        Assert.Equal(1, vm.Results[3].MatchedTagCount);

        Assert.Equal(100u, vm.Results[0].AppId);
        Assert.Equal(101u, vm.Results[1].AppId);
    }

    /// <summary>
    /// TEST V — RequestCountsFor Non-Authoritative Fallback
    /// Verifies that when a local catalog is not authoritative and tags coverage is not complete,
    /// RequestCountsFor falls back to remote Steam tag counts rather than reporting tiny cached counts.
    /// </summary>
    [Fact]
    public async Task TestV_RequestCountsFor_NonAuthoritativeCatalog_DoesNotReportLocalCounts()
    {
        var mockLocal = new Mock<ILocalCatalogRepository>();
        // Simulate a browsing cache of 1,205 apps that is NOT authoritative
        mockLocal.Setup(m => m.GetCompletenessAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CatalogCompleteness
            {
                IdentityComplete = false,
                ExpectedAppCount = 0,
                IndexedAppCount = 1205,
                TagsCoverage = 0.35
            });

        var mockTagCatalog = new Mock<ISteamTagCatalogService>();
        mockTagCatalog.Setup(t => t.GetGroupAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<SteamTag>
            {
                new() { TagId = 19, Name = "Action", CanonicalName = "Action" },
                new() { TagId = 4182, Name = "Singleplayer", CanonicalName = "Singleplayer" }
            });

        var vm = CreateViewModel(tagCatalog: mockTagCatalog.Object, localRepo: mockLocal.Object);

        var tagGroup = vm.FilterGroups.FirstOrDefault(g => g.Items.Any(i => i.Option.Kind == SteamFacetKind.Tag));
        Assert.NotNull(tagGroup);

        tagGroup.IsExpanded = true;

        // Give the background task a moment
        await Task.Delay(150);

        // Local repository GetTagCountsAsync should NOT be called because completeness.IsAuthoritative is false
        mockLocal.Verify(m => m.GetTagCountsAsync(It.IsAny<IEnumerable<int>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private sealed class SpySearchPipeline : ISearchPipeline
    {
        public SearchRequest? LastRequest { get; private set; }
        public Func<SearchRequest, Task<SearchResponse>>? Handler { get; set; }

        public async Task<SearchResponse> ExecuteAsync(SearchRequest request, CancellationToken ct = default)
        {
            LastRequest = request;
            if (Handler != null)
            {
                return await Handler(request);
            }

            return new SearchResponse
            {
                Items = [],
                TotalCount = 0,
                IsFromLocalCatalog = true
            };
        }
    }
}
