using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BlueStar.App.ViewModels;
using BlueStar.Core.Interfaces;
using BlueStar.Core.Models;
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

    private BrowseViewModel CreateViewModel()
    {
        return new BrowseViewModel(
            _mockApiClient.Object,
            _mockInstanceManager.Object,
            _mockArchiveParser.Object,
            _mockEngineDetector.Object,
            NullLogger<BrowseViewModel>.Instance,
            catalogSearch: _mockCatalogSearch.Object,
            searchPipeline: _spyPipeline);
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
