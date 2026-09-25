using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using BlueStar.Core.Helpers;
using BlueStar.Core.Interfaces;
using BlueStar.Core.Models;
using BlueStar.Infrastructure.Search;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace BlueStar.App.ViewModels;

/// <summary>
/// ViewModel for searching and exploring games in DepotBox catalog with live Steam/SteamDB enrichment and instance installation notifications.
/// </summary>
public partial class BrowseViewModel : ObservableObject, ISharedViewModel, IDisposable
{
    private readonly IDepotBoxApiClient _apiClient;
    private readonly IInstanceManager _instanceManager;
    private readonly IDepotBoxArchiveParser _archiveParser;
    private readonly IMetadataProvider? _metadataProvider;
    private readonly IEngineDetector _engineDetector;
    private readonly INotificationService? _notificationService;
    private readonly ICommunityStatsService? _statsService;
    private readonly BlueStar.Infrastructure.Storage.AppSettingsService? _settingsService;
    private readonly IBackgroundTaskService? _backgroundTaskService;
    private readonly IGameCatalogProvider? _catalogProvider;
    private readonly IBuildResolver? _buildResolver;
    private readonly ILogger<BrowseViewModel> _logger;
    private readonly CancellationTokenSource _cts = new();
    private bool _isDisposed;
    private readonly EventHandler? _settingsChangedHandler;

    private readonly object _resultsLock = new();
    private readonly object _activeFiltersLock = new();

    [ObservableProperty]
    private string _searchQuery = string.Empty;

    [ObservableProperty]
    private ObservableCollection<SearchResult> _results = [];

    [ObservableProperty]
    private ObservableCollection<FilterGroupViewModel> _filterGroups = [];

    [ObservableProperty]
    private ObservableCollection<FilterOptionItem> _activeFilters = [];

    /// <summary>
    /// Names of the tags currently being filtered on. Every card's bubbles are marked against
    /// this, so a game shows at a glance which of the chosen tags it carries.
    /// </summary>
    private HashSet<string> _activeTagNames = new(StringComparer.CurrentCultureIgnoreCase);

    [ObservableProperty]
    private ObservableCollection<SortOptionItem> _sortOptions = [];

    /// <summary>
    /// Steam's curated lists, offered in the toolbar next to the sort. These used to be a group
    /// in the filter panel, but they are a way of looking at the whole catalog rather than a
    /// filter you combine with others, so they belong up here.
    /// </summary>
    [ObservableProperty]
    private ObservableCollection<SortOptionItem> _storeListOptions = [];

    [ObservableProperty]
    private SortOptionItem? _selectedSort;

    /// <summary>
    /// Which way the chosen sort runs. The store takes the direction as part of the sort token
    /// (<c>Released_DESC</c>), so the selector offers the field and this button the direction,
    /// rather than listing every field twice.
    /// </summary>
    [ObservableProperty]
    private bool _isSortDescending = true;

    [ObservableProperty]
    private SortOptionItem? _selectedStoreList;

    /// <summary>How many results matched every selected tag, before any were relaxed.</summary>
    [ObservableProperty]
    private int _exactMatchCount;

    /// <summary>Whether the list has started including partial tag matches.</summary>
    [ObservableProperty]
    private bool _isShowingRelated;

    /// <summary>
    /// The full title the search fell back to when the typed one matched nothing, or
    /// <c>null</c> when the typed one was enough.
    /// </summary>
    /// <remarks>
    /// Shown above the results so a person who typed "phasmo" and is looking at Phasmophobia
    /// knows the list answers a different word than the one in the box.
    /// </remarks>
    [ObservableProperty]
    private string? _suggestedTerm;

    [ObservableProperty]
    private string _selectedAppType = SteamStoreFacets.AppTypeAll;

    /// <summary>
    /// Results asked for per request. Fixed now that the toolbar slot it used to occupy shows
    /// Steam's curated lists instead; the list grows by scrolling, not by page size.
    /// </summary>
    public const int PageSize = 50;

    [ObservableProperty]
    private int _currentPage = 1;

    [ObservableProperty]
    private int _totalResults;

    [ObservableProperty]
    private int _totalPages = 1;

    [ObservableProperty]
    private bool _isGridView = true;

    [ObservableProperty]
    private bool _isSearching;

    [ObservableProperty]
    private SearchState _searchState = SearchState.Idle;

    [ObservableProperty]
    private bool _isFirstLoad = true;

    [ObservableProperty]
    private string _loadingStatusText = "Loading Explore Catalog…";

    private Task? _initTask;

    [ObservableProperty]
    private bool _isCreatingInstance;

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    private bool _hasActiveFilters;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEnrichmentVisible))]
    private bool _isEnriching;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEnrichmentVisible))]
    private int _enrichedCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEnrichmentVisible))]
    private int _enrichTotal;

    /// <summary>
    /// Indicates whether DRM/DLC enrichment is actively running and has unfinished items.
    /// Evaluates to false immediately when EnrichedCount reaches EnrichTotal (e.g. 50/50), hiding the badge.
    /// </summary>
    public bool IsEnrichmentVisible => IsEnriching && EnrichTotal > 0 && EnrichedCount < EnrichTotal;

    [ObservableProperty]
    private int _hiddenByContentFilters;

    [ObservableProperty]
    private bool _hasMoreResults;

    [ObservableProperty]
    private bool _isLoadingMore;

    [ObservableProperty]
    private bool _isDetailOpen;

    [ObservableProperty]
    private SearchResult? _detailTarget;

    [ObservableProperty]
    private string? _detailUrl;

    /// <summary>Raised when the person asks to open the detail page of a created instance.</summary>
    public Action<GameInstance>? OnManageInstanceRequested { get; set; }

    private readonly ISteamCatalogSearchService? _catalogSearch;
    private readonly ISearchPipeline? _searchPipeline;
    private readonly ILocalCatalogRepository? _localRepo;
    private readonly ISteamTagCatalogService? _tagCatalog;
    private readonly ICacheService? _cache;
    private readonly Dictionary<string, int> _facetUsage = new(StringComparer.Ordinal);

    private CancellationTokenSource? _searchDebounce;
    private bool _suppressSearch;

    /// <summary>
    /// Bumped every time a fresh query starts. A page that comes back carrying an older number
    /// is thrown away instead of being merged into results it no longer belongs to.
    /// </summary>
    private int _searchGeneration;

    private CancellationTokenSource? _searchCts;

    /// <summary>The adult-content tag group, kept to hand so settings can hide it.</summary>
    private FilterGroupViewModel? _adultGroup;

    /// <summary>Everything loaded for the current query, before the local filters.</summary>
    private readonly List<SearchResult> _fetched = [];

    /// <summary>
    /// Upper safety limit on loaded search cards. Allows deep scrolling through
    /// many pages of titles while preventing unbounded memory consumption.
    /// </summary>
    private const int MaxMaterialized = 100_000;

    /// <summary>
    /// One step of the search: a set of tags to require, and the term to require with them.
    /// </summary>
    /// <param name="Tags">Tags that must all be present. Empty means the tags do not narrow.</param>
    /// <param name="Term">Free-text term, or <c>null</c> to browse.</param>
    /// <param name="IsSuggestion">Whether the term came from autocomplete rather than the person.</param>
    private sealed record SearchRung(
        IReadOnlyList<SteamFacetOption> Tags, string? Term, bool IsSuggestion = false);

    /// <summary>
    /// How many steps the ladder may hold. Every subset of the chosen tags would be 2^n, which
    /// is a lot of requests for the fourth or fifth tag; the sizes that match the most tags are
    /// generated first, so the cap only ever cuts the least exact ones.
    /// </summary>
    private const int MaxLadderRungs = 64;

    /// <summary>How many empty steps one call may walk through before giving the screen back.</summary>
    private const int MaxEmptyHops = 32;

    private readonly List<SearchRung> _ladder = [];

    private bool _suggestionsResolved;

    private List<int> _selectedTagIds = [];
    private List<int> _selectedExcludedTagIds = [];
    private int _ladderRung;
    private int _rungStart;

    private readonly PriorityQueue<SearchResult, int> _priorityEnrichQueue = new();
    private readonly HashSet<uint> _enqueuedEnrichAppIds = [];
    private readonly object _enrichLock = new();
    private Task? _enrichWorker;

    private readonly System.Collections.Concurrent.ConcurrentQueue<FilterOptionItem> _countQueue = new();
    private readonly HashSet<string> _countSeen = new(StringComparer.Ordinal);
    private readonly object _countLock = new();
    private Task? _countWorker;

    private const string UsageCacheKey = "browse_facet_usage_v1";
    private const string CategoryUsageCacheKey = "browse_category_usage_v1";

    public sealed class CategoryUsageSnapshot
    {
        public Dictionary<string, int> Counters { get; set; } = new(StringComparer.Ordinal);
        public Dictionary<string, Dictionary<string, int>> LastSelected { get; set; } = new(StringComparer.Ordinal);
    }

    private CategoryUsageSnapshot _categoryUsage = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="BrowseViewModel"/> class.
    /// </summary>
    public BrowseViewModel(
        IDepotBoxApiClient apiClient,
        IInstanceManager instanceManager,
        IDepotBoxArchiveParser archiveParser,
        IEngineDetector engineDetector,
        ILogger<BrowseViewModel> logger,
        ICommunityStatsService? statsService = null,
        IMetadataProvider? metadataProvider = null,
        INotificationService? notificationService = null,
        BlueStar.Infrastructure.Storage.AppSettingsService? settingsService = null,
        IBackgroundTaskService? backgroundTaskService = null,
        IGameCatalogProvider? catalogProvider = null,
        IBuildResolver? buildResolver = null,
        ISteamCatalogSearchService? catalogSearch = null,
        ISteamTagCatalogService? tagCatalog = null,
        ICacheService? cache = null,
        ISearchPipeline? searchPipeline = null,
        ILocalCatalogRepository? localRepo = null)
    {
        _apiClient = apiClient;
        _instanceManager = instanceManager;
        _archiveParser = archiveParser;
        _engineDetector = engineDetector;
        _logger = logger;
        _statsService = statsService;
        _metadataProvider = metadataProvider;
        _notificationService = notificationService;
        _settingsService = settingsService;
        _backgroundTaskService = backgroundTaskService;
        _catalogProvider = catalogProvider;
        _buildResolver = buildResolver;
        _catalogSearch = catalogSearch;
        _tagCatalog = tagCatalog;
        _cache = cache;
        _searchPipeline = searchPipeline;
        _localRepo = localRepo;

        // Restore the grid-or-list choice before anything can observe it, so switching to
        // Explore does not flash the grid on the way to the list.
        _isGridView = settingsService?.ExploreGridView ?? true;

        // Both toolbar lists start with an empty entry, because "no particular order" and
        // "the whole catalog" have to be expressible. They are otherwise independent: Steam
        // reads filter= and sort_by= together, so neither selector clears the other.
        SortOptions = new ObservableCollection<SortOptionItem>(
            [new SortOptionItem(string.Empty, Localize("SortNone", "No particular order")),
                .. SteamStoreFacets.SortOptions.Select(o => new SortOptionItem(o.Value, Localize("Sort_" + o.Value, o.FallbackName)))]);

        StoreListOptions = new ObservableCollection<SortOptionItem>(
            [new SortOptionItem(string.Empty, Localize("StoreListNone", "Whole catalog")),
                .. SteamStoreFacets.StoreLists.Select(o => new SortOptionItem(o.Value, Localize("Facet_StoreList_" + o.Value, o.FallbackName)))]);

        // Setting this would otherwise fire a search before the filter panel exists, spending a
        // request on a query InitializeAsync is about to replace.
        _suppressSearch = true;
        var defaultSortVal = _settingsService?.DefaultExploreSort ?? "Reviews";
        var defaultSortDesc = _settingsService?.DefaultExploreSortDescending ?? true;

        SelectedSort = SortOptions.FirstOrDefault(o => string.Equals(o.Value, defaultSortVal, StringComparison.OrdinalIgnoreCase)) ?? SortOptions.FirstOrDefault();
        IsSortDescending = defaultSortDesc;
        _suppressSearch = false;

        if (_settingsService != null)
        {
            _settingsChangedHandler = (_, _) =>
            {
                App.Current?.Dispatcher?.Invoke(() =>
                {
                    if (_isDisposed) return;

                    // Adult content is negated in the query itself, so a change there means the
                    // page has to be fetched again rather than merely re-filtered.
                    if (ApplyAdultVisibility()) _ = RunSearchAsync();
                    else RebuildVisible();
                });
            };
            _settingsService.SettingsChanged += _settingsChangedHandler;
        }

        System.Windows.Data.BindingOperations.EnableCollectionSynchronization(_results, _resultsLock);
        System.Windows.Data.BindingOperations.EnableCollectionSynchronization(_activeFilters, _activeFiltersLock);

        _initTask = InitializeAsync();
    }

    private static void Dispatch(Action action)
    {
        var dispatcher = App.Current?.Dispatcher;
        if (dispatcher != null && !dispatcher.CheckAccess())
        {
            dispatcher.Invoke(action);
        }
        else
        {
            action();
        }
    }

    /// <summary>
    /// Builds the filter panel, loads the store events, and runs the opening query.
    /// Explore opens on Steam's popular new releases rather than an empty page.
    /// </summary>
    private async Task InitializeAsync()
    {
        Dispatch(() =>
        {
            IsFirstLoad = true;
            LoadingStatusText = Localize("ExploreLoadingFilters", "Loading filters and catalog…");
        });

        var minDisplayTask = Task.Delay(1300, _cts.Token);
        try
        {
            await LoadUsageAsync().ConfigureAwait(false);
            await BuildFilterGroupsAsync().ConfigureAwait(false);

            Dispatch(() =>
            {
                LoadingStatusText = Localize("ExploreLoadingDiscover", "Discovering games…");
            });

            // Opening state: If local catalog is authoritative, open on whole local catalog ("").
            // Otherwise, open on Steam's popular new releases.
            _suppressSearch = true;
            var isLocalAuth = false;
            if (_localRepo != null)
            {
                try
                {
                    var comp = await _localRepo.GetCompletenessAsync(_cts.Token).ConfigureAwait(false);
                    isLocalAuth = comp.IsAuthoritative;
                }
                catch { }
            }

            Dispatch(() =>
            {
                var prefStoreList = _settingsService?.DefaultExploreStoreList ?? (isLocalAuth ? "" : "popularnew");
                var prefSort = _settingsService?.DefaultExploreSort ?? "Reviews";
                var prefSortDesc = _settingsService?.DefaultExploreSortDescending ?? true;

                SelectedStoreList = StoreListOptions.FirstOrDefault(o => string.Equals(o.Value, prefStoreList, StringComparison.OrdinalIgnoreCase))
                                    ?? StoreListOptions.FirstOrDefault();
                SelectedSort = SortOptions.FirstOrDefault(o => string.Equals(o.Value, prefSort, StringComparison.OrdinalIgnoreCase))
                               ?? SortOptions.FirstOrDefault();
                IsSortDescending = prefSortDesc;
                _suppressSearch = false;
            });

            var dispatcher = App.Current?.Dispatcher;
            if (dispatcher != null && !dispatcher.CheckAccess())
            {
                await dispatcher.InvokeAsync(async () => await RunSearchAsync().ConfigureAwait(true)).Task.Unwrap();
            }
            else
            {
                await RunSearchAsync().ConfigureAwait(true);
            }

            try
            {
                await minDisplayTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize Explore catalog");
        }
        finally
        {
            Dispatch(() =>
            {
                IsFirstLoad = false;
            });
        }
    }

    /// <summary>
    /// Assembles every filter group: Steam's own facets, plus the tag groups resolved against
    /// the live tag catalog.
    /// </summary>
    private async Task BuildFilterGroupsAsync()
    {
        var groups = new List<FilterGroupViewModel>();

        FilterGroupViewModel Group(
            string key, string fallbackTitle, string iconKey, int visible, bool open,
            IEnumerable<SteamFacetOption> options, bool single = false, bool exclude = true)
        {
            var vm = new FilterGroupViewModel(
                key, Localize("Filter_" + key, fallbackTitle), iconKey, visible, open,
                _facetUsage, OnFilterChanged, RequestCountsFor, single, exclude);

            vm.SetOptions(options.Select(o => new FilterOptionItem(o, Localize("Facet_" + o.Kind + "_" + o.Value, o.FallbackName))));
            if (_categoryUsage.Counters.TryGetValue(key, out var count))
            {
                _categoryUsage.LastSelected.TryGetValue(key, out var lastMap);
                vm.RestoreCategoryUsage(count, lastMap);
            }
            return vm;
        }

        // The tag groups come from the live catalog, so they are built before being placed.
        var tagGroups = new List<FilterGroupViewModel>();

        if (_tagCatalog != null)
        {
            foreach (var definition in SteamTagGroups.All)
            {
                try
                {
                    var tags = await _tagCatalog.GetGroupAsync(definition.Key, _cts.Token).ConfigureAwait(true);
                    if (tags.Count == 0) continue;

                    var vm = new FilterGroupViewModel(
                        definition.Key,
                        Localize("Filter_" + definition.Key, definition.FallbackTitle),
                        definition.IconKey,
                        definition.VisibleCount,
                        definition.OpenByDefault,
                        _facetUsage, OnFilterChanged, RequestCountsFor);

                    vm.SetOptions(tags.Select(t => new FilterOptionItem(t.ToFacet(), t.Name, t.ProductCount)));
                    if (_categoryUsage.Counters.TryGetValue(definition.Key, out var count))
                    {
                        _categoryUsage.LastSelected.TryGetValue(definition.Key, out var lastMap);
                        vm.RestoreCategoryUsage(count, lastMap);
                    }
                    tagGroups.Add(vm);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Could not build the {Group} tag group", definition.Key);
                }
            }
        }

        FilterGroupViewModel? Tag(string key) => tagGroups.FirstOrDefault(g => g.Key == key);

        void AddTag(string key)
        {
            var group = Tag(key);
            if (group != null) groups.Add(group);
        }

        AddTag("genre");
        AddTag("setting");
        AddTag("gameplay");
        AddTag("pace");
        AddTag("style");

        groups.Add(Group("mode", "Game mode", "IconTarget", 12, true, SteamStoreFacets.PlayerSupport, exclude: false));
        groups.Add(Group("platform", "Platform", "IconMonitor", 9, true, SteamStoreFacets.Platform, exclude: false));
        groups.Add(Group("features", "Steam features", "IconSliders", 10, false, SteamStoreFacets.Features, exclude: false));

        // No "My PC" group: judging a result against the machine needs detected hardware and a
        // parsed pc_requirements, and nothing produces either yet. A group of bubbles that can
        // never answer is worse than no group, so it is gone until the verdict exists.

        groups.Add(Group("language", "Language", "IconLanguage", 8, false, SteamStoreFacets.Languages, exclude: false));

        // Review standing reads as a floor rather than a set, so only one band at a time.
        groups.Add(Group("rating", "Review rating", "IconStar", 6, false, SteamStoreFacets.Ratings, single: true));

        groups.Add(Group("controller", "Controller support", "IconGamepad", 8, false, SteamStoreFacets.Controller, exclude: false));
        groups.Add(Group("accessibility", "Accessibility", "IconAccessibility", 8, false, SteamStoreFacets.Accessibility, exclude: false));
        groups.Add(Group("price", "Price", "IconMoney", 6, false, SteamStoreFacets.Price, exclude: false));
        groups.Add(Group("content", "Content", "IconShield", 6, false, SteamStoreFacets.Content));

        // Adult content goes last, and only exists while settings allow it.
        _adultGroup = tagGroups.FirstOrDefault(g => g.Key == SteamTagGroups.AdultGroupKey);
        if (_adultGroup != null) groups.Add(_adultGroup);

        FilterGroups = new ObservableCollection<FilterGroupViewModel>(groups);
        ApplyAdultVisibility();
    }

    /// <summary>
    /// Hides the adult-content group while adult content is switched off in settings.
    /// </summary>
    /// <remarks>
    /// Hiding is not enough on its own: with the setting off those tags are also pushed into the
    /// query as exclusions by <see cref="BuildFacets"/>, so the results never contain them in the
    /// first place rather than being filtered out after the fact.
    /// </remarks>
    /// <returns><c>true</c> when the setting had actually changed.</returns>
    private bool ApplyAdultVisibility()
    {
        var allowed = _settingsService?.ShowNsfwContent ?? false;

        if (_adultGroup is null) return false;
        if (_adultGroup.IsVisible == allowed) return false;

        _adultGroup.IsVisible = allowed;

        if (!allowed)
        {
            var previous = _suppressSearch;
            _suppressSearch = true;
            _adultGroup.ClearSelection();
            _suppressSearch = previous;
        }

        _adultGroup.IsExpanded = false;
        RefreshActiveFilters();
        return true;
    }

    private string Localize(string resourceKey, string fallback)
    {
        try
        {
            var app = App.Current;
            if (app != null)
            {
                if (app.Dispatcher == null || app.Dispatcher.CheckAccess())
                {
                    if (app.TryFindResource("String_" + resourceKey) is string localized &&
                        !string.IsNullOrWhiteSpace(localized))
                    {
                        return localized;
                    }
                }
                else
                {
                    var localized = app.Dispatcher.Invoke(() => app.TryFindResource("String_" + resourceKey) as string);
                    if (!string.IsNullOrWhiteSpace(localized)) return localized;
                }
            }
        }
        catch
        {
            // Falls through to the English label.
        }

        return fallback;
    }

    /// <summary>
    /// Queues the product counts for the bubbles a group just made visible.
    /// </summary>
    /// <remarks>
    /// One probe per tag, and there are hundreds of tags. Firing them as they appear is what got
    /// the address blocked by Steam, so they go into a single queue drained by one worker, one at
    /// a time, and only while nothing more important is talking to Steam. Each count is cached
    /// for a day, so this is a first-run cost.
    /// </remarks>
    private void RequestCountsFor(FilterGroupViewModel group)
    {
        if (_isDisposed) return;

        // A collapsed group's bubbles are not on screen; their counts can wait until it opens.
        if (!group.IsExpanded) return;

        // If local catalog repository is available, query tag counts locally in a single fast batch
        if (_localRepo != null)
        {
            var uncounted = group.Items
                .Where(i => i.ProductCount == null && i.Option.Kind == SteamFacetKind.Tag)
                .ToList();

            if (uncounted.Count > 0)
            {
                var tagMap = new Dictionary<int, FilterOptionItem>();
                foreach (var item in uncounted)
                {
                    if (int.TryParse(item.Option.Value, out var tagId))
                    {
                        tagMap[tagId] = item;
                    }
                }

                if (tagMap.Count > 0)
                {
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            var comp = await _localRepo.GetCompletenessAsync(_cts.Token).ConfigureAwait(false);
                            if (comp.IsAuthoritative && comp.TagsComplete)
                            {
                                var counts = await _localRepo.GetTagCountsAsync(tagMap.Keys, _cts.Token).ConfigureAwait(false);
                                if (counts.Count > 0)
                                {
                                    App.Current?.Dispatcher?.Invoke(() =>
                                    {
                                        foreach (var (tagId, count) in counts)
                                        {
                                            if (tagMap.TryGetValue(tagId, out var opt))
                                            {
                                                opt.ProductCount = count;
                                            }
                                        }
                                    });
                                    return;
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogDebug(ex, "Failed to resolve tag counts locally");
                        }

                        EnqueueRemoteCounts(group);
                    });
                    return;
                }
            }
        }

        EnqueueRemoteCounts(group);
    }

    private void EnqueueRemoteCounts(FilterGroupViewModel group)
    {
        if (_tagCatalog is null || _catalogSearch is null || _isDisposed) return;

        var queued = false;

        lock (_countLock)
        {
            foreach (var item in group.Items)
            {
                if (item.ProductCount is not null) continue;
                if (item.Option.Kind != SteamFacetKind.Tag) continue;
                if (!_countSeen.Add(item.Key)) continue;

                _countQueue.Enqueue(item);
                queued = true;
            }

            if (queued && _countWorker is null)
            {
                _countWorker = Task.Run(DrainCountQueueAsync);
            }
        }
    }

    private async Task DrainCountQueueAsync()
    {
        var idleRounds = 0;

        while (!_isDisposed && !_cts.IsCancellationRequested)
        {
            if (!_countQueue.TryDequeue(out var item))
            {
                // Nothing to do for a while: stand down and let the next Refresh restart us.
                if (++idleRounds > 20) break;

                try
                {
                    await Task.Delay(500, _cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                continue;
            }

            idleRounds = 0;

            // A search in flight is what the person is waiting for, so counts step aside for it.
            // They no longer wait on enrichment: both are background work behind the same
            // request gate, and yielding to it meant the numbers next to the tags never filled
            // in while a page of results was being checked, which takes minutes.
            while (!_isDisposed && IsSearching)
            {
                try
                {
                    await Task.Delay(400, _cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }

            try
            {
                var probe = new SteamSearchQuery
                {
                    Count = 1,
                    AppTypes = SteamStoreFacets.AppTypeAll,
                    Facets = new Dictionary<SteamFacetOption, FacetState> { [item.Option] = FacetState.Include }
                };

                var count = await _catalogSearch!.GetMatchCountAsync(probe, _cts.Token).ConfigureAwait(false);

                if (count.HasValue)
                {
                    App.Current?.Dispatcher?.Invoke(() => item.ProductCount = count.Value);
                }
                else
                {
                    // Steam is refusing us. Drop what is left rather than hammering, and forget
                    // these keys so they can be asked for again later.
                    DiscardPendingCounts();
                    return;
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not count products for facet {Facet}", item.Option.Value);
            }
        }

        lock (_countLock)
        {
            _countWorker = null;
        }
    }

    private void DiscardPendingCounts()
    {
        lock (_countLock)
        {
            while (_countQueue.TryDequeue(out var dropped))
            {
                _countSeen.Remove(dropped.Key);
            }

            _countWorker = null;
        }
    }

    private void OnFilterChanged()
    {
        if (_suppressSearch) return;

        CurrentPage = 1;
        RefreshActiveFilters();
        ScheduleSaveUsage();
        _ = RunSearchAsync();
    }

    private void RefreshActiveFilters()
    {
        var dispatcher = App.Current?.Dispatcher;
        if (dispatcher != null && !dispatcher.CheckAccess())
        {
            dispatcher.Invoke(RefreshActiveFilters);
            return;
        }

        var active = FilterGroups.SelectMany(g => g.ActiveOptions).ToList();
        ActiveFilters = new ObservableCollection<FilterOptionItem>(active);
        System.Windows.Data.BindingOperations.EnableCollectionSynchronization(ActiveFilters, _activeFiltersLock);
        HasActiveFilters = active.Count > 0 || !string.IsNullOrWhiteSpace(SearchQuery);

        _activeTagNames = active
            .Where(o => o.Option.Kind == SteamFacetKind.Tag && o.State == FacetState.Include)
            .Select(o => o.DisplayName)
            .ToHashSet(StringComparer.CurrentCultureIgnoreCase);

        MarkActiveTags(_fetched);
    }

    /// <summary>
    /// Lights up the bubbles a card shares with the query.
    /// </summary>
    private void MarkActiveTags(IEnumerable<SearchResult> items)
    {
        foreach (var item in items)
        {
            foreach (var tag in item.StoreTags)
            {
                tag.IsActive = _activeTagNames.Contains(tag.Name);
            }
        }
    }

    /// <summary>
    /// <summary>
    /// Runs the current query. Pages accumulate: the first call replaces what is on screen, and
    /// every call after that appends, which is what the scroll-to-bottom loading relies on.
    /// Preserves existing results during Refreshing to eliminate UI flicker.
    /// </summary>
    public async Task RunSearchAsync(bool reset = true)
    {
        if (_catalogSearch is null || _isDisposed) return;

        if (reset)
        {
            // Whatever was in flight belongs to a query nobody asked for any more.
            _searchCts?.Cancel();
            _searchCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            _searchGeneration++;

            _fetched.Clear();

            // DO NOT clear Results here! Preserving Results during Refreshing prevents visual flicker.
            if (Results.Count == 0)
            {
                SearchState = SearchState.LoadingInitial;
            }
            else
            {
                SearchState = SearchState.Refreshing;
            }

            BuildLadder();
            _ladderRung = 0;
            _rungStart = 0;
            ExactMatchCount = 0;
            IsShowingRelated = false;
            HasMoreResults = true;
        }

        var generation = _searchGeneration;
        var token = (_searchCts ?? _cts).Token;

        if (reset) IsSearching = true; else IsLoadingMore = true;
        ErrorMessage = null;

        // Deterministic AppID lookup: bypass store search ladder if a numeric AppID or Steam URL was entered
        if (SteamQueryParser.TryParseAppId(SearchQuery, out var parsedAppId))
        {
            await ResolveAppIdSearchAsync(parsedAppId, generation, token).ConfigureAwait(true);
            return;
        }

        try
        {
            // If ISearchPipeline is present, execute queries through the layered pipeline (offline-first, FTS5, deterministic AppID)
            if (_searchPipeline != null)
            {
                var poolName = string.IsNullOrEmpty(SelectedStoreList?.Value) ? null : SelectedStoreList!.Value;
                var sortName = SelectedSort?.Value ?? string.Empty;

                ExtractActiveFilters(
                    out var includedTags,
                    out var excludedTags,
                    out var hasWin,
                    out var hasMac,
                    out var hasLinux,
                    out var minRating,
                    out var maxRating,
                    out var noDrm,
                    out var noLauncher,
                    out var noAnticheat,
                    out var noAccount,
                    out var noEula,
                    out var discounted,
                    out var maxPriceCents);

                _selectedTagIds = includedTags;
                _selectedExcludedTagIds = excludedTags;

                var pipelineLanded = 0;

                for (var hop = 0; hop <= MaxEmptyHops; hop++)
                {
                    if (_ladderRung >= _ladder.Count)
                    {
                        // The tags and the typed term are spent. Before calling it empty, ask Steam
                        // what that half-typed title might have been.
                        if (pipelineLanded > 0 || !await TryAppendSuggestionRungsAsync(token).ConfigureAwait(true))
                        {
                            break;
                        }

                        if (_isDisposed || generation != _searchGeneration) return;
                    }

                    var rung = _ladder[_ladderRung];
                    var rungTagIds = rung.Tags
                        .Select(t => int.TryParse(t.Value, out var id) ? id : 0)
                        .Where(id => id > 0)
                        .ToList();

                    var queryStart = _rungStart;

                    var req = new SearchRequest
                    {
                        RawQuery = rung.Term,
                        Pool = poolName ?? "all",
                        SortBy = sortName,
                        Descending = IsSortDescending,
                        AppTypes = SelectedAppType,
                        Start = queryStart,
                        Count = PageSize,
                        IncludedTagIds = rungTagIds,
                        ExcludedTagIds = excludedTags,
                        HasWindows = hasWin,
                        HasMac = hasMac,
                        HasLinux = hasLinux,
                        MinRatingPercent = minRating,
                        MaxRatingPercent = maxRating,
                        NoDrm = noDrm,
                        NoExternalLauncher = noLauncher,
                        NoAntiCheat = noAnticheat,
                        NoAccount = noAccount,
                        NoEula = noEula,
                        DiscountedOnly = discounted,
                        MaxPriceCents = maxPriceCents,
                        HideAdult = !(_settingsService?.ShowNsfwContent ?? false),
                        Facets = BuildFacets(rung.Tags)
                    };

                    var res = await _searchPipeline.ExecuteAsync(req, token).ConfigureAwait(true);
                    if (_isDisposed || generation != _searchGeneration) return;

                    if (_ladderRung == 0) ExactMatchCount = res.TotalCount;

                    var known = _fetched.Select(f => f.AppId).ToHashSet();
                    var fresh = res.Items.Where(i => known.Add(i.AppId)).ToList();

                    foreach (var item in fresh)
                    {
                        var directMatch = _selectedTagIds.Count == 0
                            ? 0
                            : item.TagIds.Count(id => _selectedTagIds.Contains(id));
                        item.MatchedTagCount = Math.Max(directMatch, rungTagIds.Count);
                    }

                    _fetched.AddRange(fresh);
                    _rungStart = queryStart + PageSize;
                    pipelineLanded += fresh.Count;

                    var isRungDone = res.TotalCount == 0 || _rungStart >= res.TotalCount;

                    if (isRungDone)
                    {
                        if (rung.IsSuggestion) SuggestedTerm = rung.Term;

                        _ladderRung++;
                        _rungStart = 0;
                    }
                    else if (rung.IsSuggestion)
                    {
                        SuggestedTerm = rung.Term;
                    }

                    if (_ladderRung > 0 && _ladder.Count > 1 && _fetched.Count > ExactMatchCount)
                    {
                        IsShowingRelated = true;
                    }

                    if (_ladder.Count <= 1)
                    {
                        TotalResults = res.TotalCount;
                    }
                    else
                    {
                        TotalResults = Math.Max(_fetched.Count, res.TotalCount);
                    }

                    HasMoreResults = _ladderRung < _ladder.Count && _fetched.Count < MaxMaterialized;

                    RebuildVisible();
                    SearchState = Results.Count > 0 ? SearchState.ShowingResults : SearchState.Empty;

                    await PreFillFromCatalogAsync(fresh, token).ConfigureAwait(true);
                    _ = ResolveResultTagsAsync(fresh);
                    QueueEnrichment(fresh);

                    if (pipelineLanded > 0)
                    {
                        if (_ladder.Count <= 1) break;
                        if (!isRungDone || pipelineLanded >= 6) break;
                    }
                }

                if (_ladderRung >= _ladder.Count) HasMoreResults = false;

                if (generation == _searchGeneration && (SearchState == SearchState.Refreshing || SearchState == SearchState.LoadingInitial))
                {
                    SearchState = Results.Count > 0 ? SearchState.ShowingResults : SearchState.Empty;
                }

                return;
            }

            // Both go into the query: the list picks the pool, the sort orders it.
            var storeList = string.IsNullOrEmpty(SelectedStoreList?.Value) ? null : SelectedStoreList!.Value;
            var sortBy = SteamStoreFacets.ComposeSort(SelectedSort?.Value, IsSortDescending);

            if (string.IsNullOrEmpty(sortBy) && string.IsNullOrWhiteSpace(SearchQuery))
            {
                sortBy = SteamStoreFacets.DefaultSortFor(storeList);
            }

            // Coming soon carries the only order its products can be put in.
            if (!SteamStoreFacets.AllowsSorting(storeList)) sortBy = string.Empty;

            var landed = 0;

            for (var hop = 0; hop <= MaxEmptyHops; hop++)
            {
                if (_ladderRung >= _ladder.Count)
                {
                    // The tags and the typed term are spent. Before calling it empty, ask Steam
                    // what that half-typed title might have been.
                    if (landed > 0 || !await TryAppendSuggestionRungsAsync(token).ConfigureAwait(true))
                    {
                        break;
                    }

                    if (_isDisposed || generation != _searchGeneration) return;
                }

                var rung = _ladder[_ladderRung];

                var query = new SteamSearchQuery
                {
                    Term = rung.Term,
                    AppTypes = SelectedAppType,
                    SortBy = sortBy,
                    StoreList = storeList,
                    Start = _rungStart,
                    Count = PageSize,
                    Facets = BuildFacets(rung.Tags)
                };

                var page = await Task.Run(() => _catalogSearch.SearchAsync(query, token), token).ConfigureAwait(true);

                // A newer query started while this one was on the wire: its results are the ones
                // on screen, so this page is dropped rather than mixed in with them.
                if (_isDisposed || generation != _searchGeneration) return;

                if (_ladderRung == 0) ExactMatchCount = page.TotalCount;

                var known = _fetched.Select(f => f.AppId).ToHashSet();
                var fresh = page.Items.Where(i => known.Add(i.AppId)).ToList();

                foreach (var item in fresh)
                {
                    item.MatchedTagCount = _selectedTagIds.Count == 0
                        ? 0
                        : item.TagIds.Count(id => _selectedTagIds.Contains(id));
                }

                _fetched.AddRange(fresh);
                _rungStart += Math.Max(page.Items.Count, 1);
                landed += fresh.Count;

                var isRungDone = page.Items.Count == 0 || _rungStart >= page.TotalCount;

                if (isRungDone)
                {
                    if (rung.IsSuggestion) SuggestedTerm = rung.Term;

                    _ladderRung++;
                    _rungStart = 0;
                }
                else if (rung.IsSuggestion)
                {
                    SuggestedTerm = rung.Term;
                }

                if (_ladderRung > 0 && _ladder.Count > 1 && _fetched.Count > ExactMatchCount)
                {
                    IsShowingRelated = true;
                }

                if (_ladder.Count <= 1)
                {
                    TotalResults = page.TotalCount;
                }
                else
                {
                    TotalResults = Math.Max(_fetched.Count, page.TotalCount);
                }

                HasMoreResults = _ladderRung < _ladder.Count && _fetched.Count < MaxMaterialized;

                RebuildVisible();
                SearchState = Results.Count > 0 ? SearchState.ShowingResults : SearchState.Empty;

                await PreFillFromCatalogAsync(fresh, token).ConfigureAwait(true);
                _ = ResolveResultTagsAsync(fresh);
                QueueEnrichment(fresh);

                if (landed > 0)
                {
                    if (_ladder.Count <= 1) break;
                    if (!isRungDone || landed >= 6) break;
                }
            }

            if (_ladderRung >= _ladder.Count) HasMoreResults = false;

            if (generation == _searchGeneration && (SearchState == SearchState.Refreshing || SearchState == SearchState.LoadingInitial))
            {
                SearchState = Results.Count > 0 ? SearchState.ShowingResults : SearchState.Empty;
            }
        }
        catch (OperationCanceledException)
        {
            // A newer query replaced this one.
        }
        catch (Exception ex)
        {
            if (generation == _searchGeneration)
            {
                ErrorMessage = ex.Message;
                SearchState = SearchState.Error;
            }
            _logger.LogError(ex, "Steam catalog search failed");
        }
        finally
        {
            if (generation == _searchGeneration)
            {
                IsSearching = false;
                IsLoadingMore = false;
                RefreshActiveFilters();
            }
        }
    }

    /// <summary>
    /// Resolves an exact AppID deterministically using local cache / appdetails metadata first,
    /// falling back to a single store search query only if necessary.
    /// </summary>
    private async Task ResolveAppIdSearchAsync(uint appId, int generation, CancellationToken token)
    {
        SearchResult? resolved = null;

        // 1. Try resolving via IMetadataProvider (hits L1 memory, L2 disk cache, and single-flight appdetails)
        if (_metadataProvider != null)
        {
            try
            {
                var meta = await _metadataProvider.GetMetadataAsync(appId, token).ConfigureAwait(true);
                if (meta != null)
                {
                    resolved = new SearchResult
                    {
                        AppId = meta.AppId,
                        Name = meta.Name,
                        AppType = "Game",
                        HeaderImageUrl = meta.HeaderImageUrl ?? $"https://shared.cloudflare.steamstatic.com/store_item_assets/steam/apps/{appId}/header.jpg",
                        ReleaseDateText = meta.ReleaseDate,
                        Version = meta.ReleaseDate,
                        HasWindows = meta.Platforms.Contains("Windows", StringComparer.OrdinalIgnoreCase) || meta.Platforms.Count == 0,
                        HasMac = meta.Platforms.Contains("macOS", StringComparer.OrdinalIgnoreCase) || meta.Platforms.Contains("Mac", StringComparer.OrdinalIgnoreCase),
                        HasLinux = meta.Platforms.Contains("Linux", StringComparer.OrdinalIgnoreCase),
                        StoreTags = meta.StoreTags.Take(8).Select(t => new StoreTagRef(t, _activeTagNames.Contains(t))).ToList()
                    };

                    await _metadataProvider.EnrichSearchResultAsync(resolved, token).ConfigureAwait(true);
                    resolved.IsEnriched = true;
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "AppId {AppId} resolution via metadata provider failed", appId);
            }
        }

        if (_isDisposed || generation != _searchGeneration) return;

        // 2. If metadata provider couldn't find it, fallback to single store search query
        if (resolved == null && _catalogSearch != null)
        {
            try
            {
                var query = new SteamSearchQuery
                {
                    Term = appId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    AppTypes = SelectedAppType,
                    Start = 0,
                    Count = 10
                };
                var page = await _catalogSearch.SearchAsync(query, token).ConfigureAwait(true);
                if (_isDisposed || generation != _searchGeneration) return;

                resolved = page.Items.FirstOrDefault(i => i.AppId == appId);
                if (resolved != null)
                {
                    await PreFillFromCatalogAsync([resolved], token).ConfigureAwait(true);
                    _ = ResolveResultTagsAsync([resolved]);
                    QueueEnrichment([resolved]);
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "AppId {AppId} fallback search failed", appId);
            }
        }

        if (_isDisposed || generation != _searchGeneration) return;

        _fetched.Clear();
        if (resolved != null)
        {
            _fetched.Add(resolved);
            TotalResults = 1;
            ExactMatchCount = 1;
            HasMoreResults = false;
            RebuildVisible();
            SearchState = SearchState.ShowingResults;
        }
        else
        {
            TotalResults = 0;
            ExactMatchCount = 0;
            HasMoreResults = false;
            RebuildVisible();
            SearchState = SearchState.Empty;
        }
    }

    private void ExtractActiveFilters(
        out List<int> includedTags,
        out List<int> excludedTags,
        out bool? hasWin,
        out bool? hasMac,
        out bool? hasLinux,
        out int? minRating,
        out int? maxRating,
        out bool? noDrm,
        out bool? noLauncher,
        out bool? noAnticheat,
        out bool? noAccount,
        out bool? noEula,
        out bool? discounted,
        out int? maxPriceCents)
    {
        var allTagOptions = FilterGroups
            .SelectMany(g => g.AllOptions)
            .Where(o => o.Option.Kind == SteamFacetKind.Tag)
            .ToList();

        includedTags = allTagOptions
            .Where(o => o.State == FacetState.Include)
            .Select(o => int.TryParse(o.Option.Value, out var id) ? id : 0)
            .Where(id => id > 0)
            .Distinct()
            .ToList();

        excludedTags = allTagOptions
            .Where(o => o.State == FacetState.Exclude)
            .Select(o => int.TryParse(o.Option.Value, out var id) ? id : 0)
            .Where(id => id > 0)
            .Distinct()
            .ToList();

        var ratingGroup = FilterGroups.FirstOrDefault(g => g.Key == "rating");
        var chosenRating = ratingGroup?.AllOptions.FirstOrDefault(o => o.State == FacetState.Include)?.Option.Value;
        minRating = chosenRating switch
        {
            "rating_min_95" => 95,
            "rating_min_80" => 80,
            "rating_min_70" => 70,
            "rating_min_40" => 40,
            _ => null
        };
        maxRating = chosenRating == "rating_below_40" ? 40 : null;

        var platGroup = FilterGroups.FirstOrDefault(g => g.Key == "platform");
        hasWin = platGroup?.AllOptions.FirstOrDefault(o => o.Option.Value == "win")?.State == FacetState.Include ? true : null;
        hasMac = platGroup?.AllOptions.FirstOrDefault(o => o.Option.Value == "mac")?.State == FacetState.Include ? true : null;
        hasLinux = platGroup?.AllOptions.FirstOrDefault(o => o.Option.Value == "linux")?.State == FacetState.Include ? true : null;

        var contentGroup = FilterGroups.FirstOrDefault(g => g.Key == "content");
        noDrm = contentGroup?.AllOptions.FirstOrDefault(o => o.Option.Value == "no_drm")?.State switch
        {
            FacetState.Include => true,
            FacetState.Exclude => false,
            _ => null
        };
        noLauncher = contentGroup?.AllOptions.FirstOrDefault(o => o.Option.Value == "no_launcher")?.State switch
        {
            FacetState.Include => true,
            FacetState.Exclude => false,
            _ => null
        };
        noAnticheat = contentGroup?.AllOptions.FirstOrDefault(o => o.Option.Value == "no_anticheat")?.State switch
        {
            FacetState.Include => true,
            FacetState.Exclude => false,
            _ => null
        };
        noAccount = contentGroup?.AllOptions.FirstOrDefault(o => o.Option.Value == "no_account")?.State switch
        {
            FacetState.Include => true,
            FacetState.Exclude => false,
            _ => null
        };
        noEula = contentGroup?.AllOptions.FirstOrDefault(o => o.Option.Value == "no_eula")?.State switch
        {
            FacetState.Include => true,
            FacetState.Exclude => false,
            _ => null
        };

        var priceGroup = FilterGroups.FirstOrDefault(g => g.Key == "price");
        discounted = priceGroup?.AllOptions.FirstOrDefault(o => o.Option.Value == "specials")?.State == FacetState.Include ? true : null;
        var chosenPrice = priceGroup?.AllOptions.FirstOrDefault(o => o.Option.Value != "specials" && o.State == FacetState.Include)?.Option.Value;
        maxPriceCents = chosenPrice switch
        {
            "free" => 0,
            "under_5" => 500,
            "under_10" => 1000,
            "under_15" => 1500,
            _ => null
        };
    }

    /// <summary>
    /// Collects every active facet across all groups (tags, platform, VR, toggle, game mode, features, etc.)
    /// to send with the SearchRequest.
    /// </summary>
    private Dictionary<SteamFacetOption, FacetState> GetAllActiveFacets()
    {
        var facets = new Dictionary<SteamFacetOption, FacetState>();

        foreach (var group in FilterGroups)
        {
            foreach (var (option, state) in group.ActiveFacets)
            {
                if (option.Kind is SteamFacetKind.StoreList or SteamFacetKind.LocalRequirements)
                {
                    continue;
                }

                facets[option] = state;
            }
        }

        if (!(_settingsService?.ShowNsfwContent ?? false))
        {
            foreach (var id in SteamTagGroups.AdultTagIds)
            {
                var option = new SteamFacetOption(
                    SteamFacetKind.Tag, id.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    string.Empty, SupportsExclude: true);

                if (!facets.ContainsKey(option)) facets[option] = FacetState.Exclude;
            }
        }

        return facets;
    }

    /// <summary>
    /// Collects every non-tag facet, then adds the tag set for the rung being queried.
    /// </summary>
    private Dictionary<SteamFacetOption, FacetState> BuildFacets(IReadOnlyList<SteamFacetOption> includedTags)
    {
        var facets = new Dictionary<SteamFacetOption, FacetState>();

        foreach (var group in FilterGroups)
        {
            foreach (var (option, state) in group.ActiveFacets)
            {
                if (option.Kind is SteamFacetKind.StoreList
                    or SteamFacetKind.LocalPostFilter
                    or SteamFacetKind.LocalRequirements)
                {
                    continue;
                }

                // Included tags come from the ladder; exclusions always apply.
                if (option.Kind == SteamFacetKind.Tag && state == FacetState.Include) continue;

                facets[option] = state;
            }
        }

        foreach (var tag in includedTags)
        {
            facets[tag] = FacetState.Include;
        }

        // With adult content switched off, Steam is asked not to return it at all. Doing this in
        // the query rather than in the local pass means the page is not silently half empty.
        if (!(_settingsService?.ShowNsfwContent ?? false))
        {
            foreach (var id in SteamTagGroups.AdultTagIds)
            {
                var option = new SteamFacetOption(
                    SteamFacetKind.Tag, id.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    string.Empty, SupportsExclude: true);

                if (!facets.ContainsKey(option)) facets[option] = FacetState.Exclude;
            }
        }

        return facets;
    }

    /// <summary>
    /// Works out the sequence of queries to walk, most exact first.
    /// </summary>
    /// <remarks>
    /// Steam's <c>tags=</c> is an AND, so asking for four tags at once returns only the products
    /// carrying all four — often nothing at all. Rather than trade that precision for a plain
    /// OR, the search walks down: every tag together, then every set one tag smaller, then every
    /// set smaller again, down to the single tags. A product carrying three of the four is
    /// therefore fetched before one carrying two, and one carrying two before one carrying one.
    /// <para>
    /// The old ladder jumped straight from all-four to each-one, so a product matching three of
    /// the four was never queried as such — it only turned up under whichever single tag it
    /// shared, mixed in with everything else carrying that one tag. The middle sets are what
    /// was missing.
    /// </para>
    /// <para>
    /// <see cref="RebuildVisible"/> then orders what came back by how many of the chosen tags
    /// each product actually carries, so the AND-heavy ones stay at the top even once pages from
    /// several steps are mixed together.
    /// </para>
    /// </remarks>
    private void BuildLadder()
    {
        _ladder.Clear();
        _suggestionsResolved = false;
        SuggestedTerm = null;

        var included = FilterGroups
            .SelectMany(g => g.ActiveOptions)
            .Where(o => o.Option.Kind == SteamFacetKind.Tag && o.State == FacetState.Include)
            .Select(o => o.Option)
            .ToList();

        _selectedTagIds = included
            .Select(o => int.TryParse(o.Value, out var id) ? id : 0)
            .Where(id => id > 0)
            .ToList();

        var term = string.IsNullOrWhiteSpace(SearchQuery) ? null : SearchQuery.Trim();

        if (included.Count == 0)
        {
            _ladder.Add(new SearchRung([], term));
            return;
        }

        for (var size = included.Count; size >= 1; size--)
        {
            foreach (var combination in Combinations(included, size))
            {
                _ladder.Add(new SearchRung(combination, term));
                if (_ladder.Count >= MaxLadderRungs) return;
            }
        }
    }

    /// <summary>
    /// Every subset of <paramref name="source"/> of exactly <paramref name="size"/> items, in
    /// the order the tags were chosen.
    /// </summary>
    private static IEnumerable<IReadOnlyList<T>> Combinations<T>(IReadOnlyList<T> source, int size)
    {
        if (size <= 0 || size > source.Count) yield break;

        var indices = new int[size];
        for (var i = 0; i < size; i++) indices[i] = i;

        while (true)
        {
            var combination = new List<T>(size);
            foreach (var index in indices) combination.Add(source[index]);
            yield return combination;

            var pivot = size - 1;
            while (pivot >= 0 && indices[pivot] == source.Count - size + pivot) pivot--;
            if (pivot < 0) yield break;

            indices[pivot]++;
            for (var j = pivot + 1; j < size; j++) indices[j] = indices[j - 1] + 1;
        }
    }

    /// <summary>
    /// Turns a half-typed title into steps the faceted search can actually run.
    /// </summary>
    /// <remarks>
    /// Steam's faceted search matches whole words, so "phasmo" answers with nothing while
    /// "phasmophobia" answers with seventeen products. Its autocomplete does match prefixes, so
    /// when the typed term runs the ladder dry the titles it offers become further steps,
    /// carrying the same tags and filters as the rest. Only the first of them is usually
    /// fetched: the walk stops as soon as a step has something to show.
    /// </remarks>
    /// <returns><c>true</c> when at least one step was added.</returns>
    private async Task<bool> TryAppendSuggestionRungsAsync(CancellationToken token)
    {
        if (_suggestionsResolved || _catalogSearch is null || _isDisposed) return false;

        _suggestionsResolved = true;

        var term = string.IsNullOrWhiteSpace(SearchQuery) ? null : SearchQuery.Trim();
        if (term is null) return false;

        IReadOnlyList<SteamTitleSuggestion> suggestions;

        try
        {
            suggestions = await _catalogSearch.SuggestTitlesAsync(term, token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not resolve title suggestions for {Term}", term);
            return false;
        }

        if (_isDisposed || suggestions.Count == 0) return false;

        // The tags stay exactly as they were: a suggestion widens the title, never the filters.
        IReadOnlyList<SteamFacetOption> tags = _ladder.Count > 0 ? _ladder[0].Tags : [];
        var added = 0;
        var seen = new HashSet<string>(StringComparer.CurrentCultureIgnoreCase) { term };

        foreach (var suggestion in suggestions)
        {
            if (string.IsNullOrWhiteSpace(suggestion.Name)) continue;
            if (!seen.Add(suggestion.Name)) continue;

            _ladder.Add(new SearchRung(tags, suggestion.Name, IsSuggestion: true));

            if (++added >= MaxEmptyHops) break;
        }

        return added > 0;
    }

    /// <summary>
    /// Loads the next page onto the end of the list. Bound to the scroll reaching the bottom.
    /// </summary>
    [RelayCommand]
    public async Task LoadMoreAsync()
    {
        if (!HasMoreResults || IsLoadingMore || IsSearching || _isDisposed) return;
        await RunSearchAsync(reset: false).ConfigureAwait(true);
    }

    /// <summary>
    /// Rebuilds what is on screen from everything fetched so far.
    /// </summary>
    /// <remarks>
    /// The local filters — the Content group, the review band and the person's NSFW and
    /// DRM display settings — hide results instead of discarding them, so turning a filter back
    /// off brings its results back without going to Steam again.
    /// </remarks>
    private void RebuildVisible()
    {
        var dispatcher = App.Current?.Dispatcher;
        if (dispatcher != null && !dispatcher.CheckAccess())
        {
            dispatcher.Invoke(RebuildVisible);
            return;
        }

        var kept = _fetched.Where(PassesLocalFilters);

        // Steam's own order is the order, and it is the one the sort selector asked for. The
        // only time it gets rearranged is when the search has had to relax an AND into partial
        // matches: then pages from different rungs are mixed together and the results carrying
        // more of the chosen tags belong above the ones carrying one.
        var visible = _ladder.Count > 1
            ? kept.Select((item, index) => (item, index))
                  .OrderByDescending(p => p.item.MatchedTagCount)
                  .ThenBy(p => p.index)
                  .Select(p => p.item)
                  .ToList()
            : kept.ToList();

        HiddenByContentFilters = _fetched.Count - visible.Count;

        SyncCollection(Results, visible);
    }

    /// <summary>
    /// Brings the bound collection in line with the list that should be on screen.
    /// </summary>
    /// <remarks>
    /// Two shapes cover almost every call. Nothing changed — the common case while enrichment
    /// ticks along — costs one walk and no notifications at all. A page arriving on the end is
    /// appended, so the cards already rendered are left alone. Anything else (a filter changing
    /// the order) replaces the collection once: a single reset beats a few hundred Move and
    /// Insert notifications, each of which made the panel re-measure the whole wrap layout.
    /// </remarks>
    private void SyncCollection(ObservableCollection<SearchResult> target, List<SearchResult> desired)
    {
        var dispatcher = App.Current?.Dispatcher;
        if (dispatcher != null && !dispatcher.CheckAccess())
        {
            dispatcher.Invoke(() => SyncCollection(target, desired));
            return;
        }

        var shared = Math.Min(target.Count, desired.Count);
        var prefixMatches = true;

        for (var i = 0; i < shared; i++)
        {
            if (!ReferenceEquals(target[i], desired[i]))
            {
                prefixMatches = false;
                break;
            }
        }

        if (prefixMatches)
        {
            if (desired.Count == target.Count) return;

            if (desired.Count > target.Count)
            {
                for (var i = target.Count; i < desired.Count; i++) target.Add(desired[i]);
                return;
            }

            for (var i = target.Count - 1; i >= desired.Count; i--) target.RemoveAt(i);
            return;
        }

        Results = new ObservableCollection<SearchResult>(desired);
        System.Windows.Data.BindingOperations.EnableCollectionSynchronization(Results, _resultsLock);
    }

    private bool PassesLocalFilters(SearchResult item)
    {
        var allowNsfw = _settingsService?.ShowNsfwContent ?? false;
        var allowDrm = _settingsService?.ShowDrmContent ?? true;

        if (!allowNsfw && item.IsNsfw) return false;
        if (!allowDrm && item.HasDrm) return false;

        var content = FilterGroups.FirstOrDefault(g => g.Key == "content");
        var rating = FilterGroups.FirstOrDefault(g => g.Key == "rating");
        var priceGroup = FilterGroups.FirstOrDefault(g => g.Key == "price");
        var platGroup = FilterGroups.FirstOrDefault(g => g.Key == "platform");

        FacetState StateOf(FilterGroupViewModel? group, string value) =>
            group?.AllOptions.FirstOrDefault(o => o.Option.Value == value)?.State ?? FacetState.Neutral;

        if (!PassesRating(item, rating)) return false;

        // Hide free to play
        if (StateOf(priceGroup, "hidef2p") == FacetState.Include)
        {
            var price = (item.PriceText ?? string.Empty).Trim().ToLowerInvariant();
            if (price.Contains("free") || price.Contains("gratis") || price == "$0" || price == "$0.00" || price == "0€"
                || (item.TagIds != null && item.TagIds.Contains(113)))
            {
                return false;
            }
        }

        // Discounted only
        if (StateOf(priceGroup, "specials") == FacetState.Include && item.DiscountPercent <= 0)
        {
            return false;
        }

        // VR only
        if (StateOf(platGroup, "401") == FacetState.Include)
        {
            if (item.TagIds == null || !item.TagIds.Contains(21978))
            {
                return false;
            }
        }

        // Platform requirements
        if (StateOf(platGroup, "win") == FacetState.Include && !item.HasWindows) return false;
        if (StateOf(platGroup, "mac") == FacetState.Include && !item.HasMac) return false;
        if (StateOf(platGroup, "linux") == FacetState.Include && !item.HasLinux) return false;

        // Excluded tags
        if (_selectedExcludedTagIds != null && _selectedExcludedTagIds.Count > 0 && item.TagIds != null)
        {
            if (item.TagIds.Any(t => _selectedExcludedTagIds.Contains(t))) return false;
        }



        var noDrm = StateOf(content, "no_drm");
        var noLauncher = StateOf(content, "no_launcher");
        var noAnticheat = StateOf(content, "no_anticheat");
        var noAccount = StateOf(content, "no_account");
        var noEula = StateOf(content, "no_eula");
        var hasDlc = StateOf(content, "has_dlc");
        var hideAdult = StateOf(content, "hide_adult");

        var anyContentFilter = noDrm != FacetState.Neutral || noLauncher != FacetState.Neutral
                               || noAnticheat != FacetState.Neutral || noAccount != FacetState.Neutral || noEula != FacetState.Neutral
                               || hasDlc != FacetState.Neutral || hideAdult != FacetState.Neutral;

        // These answers only exist once appdetails has been read for this result. Until then the
        // result stays visible and carries its pending badge rather than being judged blind.
        if (anyContentFilter && !item.IsEnriched) return true;

        if (noDrm == FacetState.Include && item.HasDrm) return false;
        if (noDrm == FacetState.Exclude && !item.HasDrm) return false;

        if (noLauncher == FacetState.Include && item.HasExternalLauncher) return false;
        if (noLauncher == FacetState.Exclude && !item.HasExternalLauncher) return false;

        if (noAnticheat == FacetState.Include && item.HasAntiCheat) return false;
        if (noAnticheat == FacetState.Exclude && !item.HasAntiCheat) return false;

        if (noAccount == FacetState.Include && item.HasAccount) return false;
        if (noAccount == FacetState.Exclude && !item.HasAccount) return false;

        if (noEula == FacetState.Include && item.HasEula) return false;
        if (noEula == FacetState.Exclude && !item.HasEula) return false;

        var carriesDlc = item.DlcCount is > 0;
        if (hasDlc == FacetState.Include && !carriesDlc) return false;
        if (hasDlc == FacetState.Exclude && carriesDlc) return false;

        if (hideAdult == FacetState.Include && item.IsNsfw) return false;

        return true;
    }

    /// <summary>
    /// Applies the review band, when one is chosen.
    /// </summary>
    /// <remarks>
    /// The band is read from the positive-review percentage rather than from the summary text,
    /// because the summary arrives in the store language and would stop matching the moment
    /// someone browses in Spanish. A product Steam gives no percentage for has no standing to
    /// judge, so it steps aside while a band is active.
    /// </remarks>
    private static bool PassesRating(SearchResult item, FilterGroupViewModel? rating)
    {
        var chosen = rating?.AllOptions.FirstOrDefault(o => o.State == FacetState.Include);
        if (chosen is null) return true;

        if (item.ReviewPercent is not { } percent) return false;

        return chosen.Option.Value switch
        {
            "rating_min_95" => percent >= 95,
            "rating_min_80" => percent >= 80,
            "rating_min_70" => percent >= 70,
            "rating_min_40" => percent >= 40,
            "rating_below_40" => percent < 40,
            _ => true
        };
    }

    /// <summary>
    /// Pre-populates search results from the local SQLite catalog (DRM, DLC, tags, price, reviews, release date)
    /// to avoid slow and rate-limited Steam API calls for items already known.
    /// </summary>
    private async Task PreFillFromCatalogAsync(IReadOnlyList<SearchResult> items, CancellationToken ct)
    {
        if (_localRepo is null || items.Count == 0) return;

        try
        {
            var ids = items.Select(i => i.AppId).Distinct().ToList();
            var query = new LocalCatalogQuery
            {
                RestrictToAppIds = ids,
                Limit = ids.Count + 10
            };

            var (catalogItems, _) = await _localRepo.QueryAsync(query, ct).ConfigureAwait(false);
            if (catalogItems.Count == 0) return;

            var byId = catalogItems.ToDictionary(c => c.AppId);
            foreach (var item in items)
            {
                if (!byId.TryGetValue(item.AppId, out var cat)) continue;

                if ((item.TagIds == null || item.TagIds.Count == 0) && cat.TagIds.Count > 0)
                    item.TagIds = cat.TagIds;

                if (!item.IsNsfw && cat.IsNsfw)
                    item.IsNsfw = true;

                if (!item.ReviewPercent.HasValue && cat.ReviewPercent.HasValue)
                {
                    item.ReviewPercent = cat.ReviewPercent;
                    item.ReviewSummary = RatingEngine.GetReviewSummary(cat.ReviewPercent.Value, cat.ReviewCount ?? 100);
                }

                if (string.IsNullOrWhiteSpace(item.PriceText) && !string.IsNullOrWhiteSpace(cat.PriceText))
                    item.PriceText = cat.PriceText;
                if (!item.PriceCents.HasValue && cat.PriceCents.HasValue)
                    item.PriceCents = cat.PriceCents;

                if (string.IsNullOrWhiteSpace(item.ReleaseDateText) && !string.IsNullOrWhiteSpace(cat.ReleaseDateText))
                    item.ReleaseDateText = cat.ReleaseDateText;
                if (!item.ReleaseDateUtc.HasValue && cat.ReleaseDateUtc.HasValue)
                    item.ReleaseDateUtc = cat.ReleaseDateUtc;

                if (!string.IsNullOrWhiteSpace(cat.DrmName))
                {
                    item.HasDrm = true;
                    item.DrmName = cat.DrmName;
                }
                else if (cat.HasDrm)
                {
                    item.HasDrm = true;
                }

                if (!string.IsNullOrWhiteSpace(cat.LauncherName))
                {
                    item.HasExternalLauncher = true;
                    item.LauncherName = cat.LauncherName;
                }

                if (!string.IsNullOrWhiteSpace(cat.AntiCheatName))
                {
                    item.HasAntiCheat = true;
                    item.AntiCheatName = cat.AntiCheatName;
                }

                if (!string.IsNullOrWhiteSpace(cat.AccountName))
                {
                    item.HasAccount = true;
                    item.AccountName = cat.AccountName;
                }

                if (!string.IsNullOrWhiteSpace(cat.EulaName))
                {
                    item.HasEula = true;
                    item.EulaName = cat.EulaName;
                }

                if (cat.DlcCount > 0 && (item.DlcCount ?? 0) == 0)
                    item.DlcCount = cat.DlcCount;

                item.IsEnriched = true;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "PreFillFromCatalogAsync skipped for current page");
        }
    }
    /// <summary>
    /// Turns the tag ids Steam ships with each row into display names for the card bubbles.
    /// </summary>
    private async Task ResolveResultTagsAsync(IReadOnlyList<SearchResult> items)
    {
        if (_tagCatalog is null || items.Count == 0) return;

        try
        {
            foreach (var item in items)
            {
                if (item.TagIds.Count == 0) continue;

                var names = await _tagCatalog.ResolveNamesAsync(item.TagIds, _cts.Token).ConfigureAwait(true);
                if (_isDisposed) return;

                item.StoreTags = names
                    .Take(8)
                    .Select(n => new StoreTagRef(n, _activeTagNames.Contains(n)))
                    .ToList();
            }
        }
        catch (OperationCanceledException)
        {
            // Ignored.
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not resolve store tag names");
        }
    }

    /// <summary>
    /// Reads DRM, external launcher and DLC count for newly loaded results.
    /// </summary>
    /// <remarks>
    /// Steam exposes none of these as a search facet, so each one is an appdetails request. They
    /// go one at a time behind everything else, and they only happen at all when the person has
    /// asked for something that depends on them, or is idly browsing — never as a burst behind a
    /// page load, which is what got the address blocked.
    /// </remarks>
    /// <summary>
    /// Reads DRM, external launcher and DLC count for newly loaded results using prioritized background processing.
    /// Priority 0: Explicitly opened or requested item.
    /// Priority 1: Required for active DRM/Launcher filter.
    /// Priority 2: Visible in current view.
    /// Priority 3: Pre-fetched background items.
    /// </summary>
    private void QueueEnrichment(IReadOnlyList<SearchResult> items, int priority = 2)
    {
        if (_metadataProvider is null || items.Count == 0 || _isDisposed) return;

        var anyContentFilter = FilterGroups
            .FirstOrDefault(g => g.Key == "content")?.ActiveOptions.Any() == true;

        lock (_enrichLock)
        {
            foreach (var item in items)
            {
                if (item.IsEnriched) continue;
                if (_enqueuedEnrichAppIds.Add(item.AppId))
                {
                    var itemPriority = anyContentFilter ? 1 : priority;
                    _priorityEnrichQueue.Enqueue(item, itemPriority);
                }
            }

            _enrichWorker ??= Task.Run(DrainEnrichQueueAsync);
        }

        EnrichTotal = _fetched.Count;
        EnrichedCount = _fetched.Count(f => f.IsEnriched);
    }

    /// <summary>
    /// Promotes an item to the highest priority (P0) when opened or hovered by the user.
    /// </summary>
    public void PrioritizeEnrichment(SearchResult item)
    {
        if (item.IsEnriched || _metadataProvider is null || _isDisposed) return;

        lock (_enrichLock)
        {
            _priorityEnrichQueue.Enqueue(item, 0); // P0: Highest priority
            _enrichWorker ??= Task.Run(DrainEnrichQueueAsync);
        }
    }

    private async Task DrainEnrichQueueAsync()
    {
        var idle = 0;

        while (!_isDisposed && !_cts.IsCancellationRequested)
        {
            SearchResult? item = null;
            lock (_enrichLock)
            {
                if (_priorityEnrichQueue.TryDequeue(out var dequeued, out _))
                {
                    item = dequeued;
                    _enqueuedEnrichAppIds.Remove(item.AppId);
                }
            }

            if (item is null)
            {
                App.Current?.Dispatcher?.Invoke(() =>
                {
                    if (!_isDisposed) IsEnriching = false;
                });

                if (++idle > 20) break;

                try
                {
                    await Task.Delay(500, _cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                continue;
            }

            idle = 0;
            App.Current?.Dispatcher?.Invoke(() =>
            {
                if (!_isDisposed && EnrichTotal > 0 && EnrichedCount < EnrichTotal)
                {
                    IsEnriching = true;
                }
            });

            try
            {
                await _metadataProvider!.EnrichSearchResultAsync(item, _cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Enrichment failed for AppId={AppId}", item.AppId);
            }

            App.Current?.Dispatcher?.Invoke(() =>
            {
                if (_isDisposed) return;

                item.IsEnriched = true;
                EnrichedCount = _fetched.Count(f => f.IsEnriched);
                lock (_enrichLock)
                {
                    IsEnriching = _priorityEnrichQueue.Count > 0 && EnrichedCount < EnrichTotal;
                }

                RebuildVisible();
            });
        }

        lock (_enrichLock)
        {
            _enrichWorker = null;
        }

        App.Current?.Dispatcher?.Invoke(() => IsEnriching = false);
    }

    /// <summary>
    /// Runs the search immediately, without waiting for the typing pause.
    /// </summary>
    [RelayCommand]
    public async Task SearchAsync()
    {
        CurrentPage = 1;
        await RunSearchAsync().ConfigureAwait(true);
    }

    partial void OnSearchQueryChanged(string value)
    {
        if (_suppressSearch) return;

        _searchDebounce?.Cancel();
        _searchDebounce = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        var token = _searchDebounce.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(450, token).ConfigureAwait(false);
                if (token.IsCancellationRequested) return;

                await App.Current!.Dispatcher.InvokeAsync(async () =>
                {
                    CurrentPage = 1;
                    RefreshActiveFilters();
                    await RunSearchAsync().ConfigureAwait(true);
                });
            }
            catch (OperationCanceledException)
            {
                // Superseded by a newer keystroke.
            }
        }, token);
    }

    /// <summary>
    /// Applies a newly chosen order.
    /// </summary>
    /// <remarks>
    /// The curated list is left exactly as it was. Steam's search reads <c>filter=</c> and
    /// <c>sort_by=</c> together — the list decides which products are in the pool, the sort
    /// decides the order they come back in — so there is nothing to clear.
    /// </remarks>
    partial void OnSelectedSortChanged(SortOptionItem? value)
    {
        var isComingSoon = string.Equals(SelectedStoreList?.Value, "comingsoon", StringComparison.OrdinalIgnoreCase);

        // Under "Coming soon", Price sort is deactivated
        if (isComingSoon && string.Equals(value?.Value, "Price", StringComparison.OrdinalIgnoreCase))
        {
            _suppressSearch = true;
            SelectedSort = SortOptions.FirstOrDefault(o => string.IsNullOrEmpty(o.Value))
                           ?? SortOptions.FirstOrDefault();
            _suppressSearch = false;
            return;
        }

        if (!CanChooseSort && !string.IsNullOrEmpty(value?.Value))
        {
            _suppressSearch = true;
            SelectedSort = SortOptions.FirstOrDefault(o => string.IsNullOrEmpty(o.Value))
                           ?? SortOptions.FirstOrDefault();
            _suppressSearch = false;
            return;
        }

        OnPropertyChanged(nameof(CanInvertSort));

        // Each field has a direction people mean by default: newest first, best reviewed first,
        // but A-Z and cheapest first. The button is there to disagree.
        // For Coming soon with Released, default to soonest upcoming first (ascending).
        if (!string.IsNullOrEmpty(value?.Value))
        {
            if (isComingSoon && string.Equals(value.Value, "Released", StringComparison.OrdinalIgnoreCase))
            {
                IsSortDescending = false;
            }
            else
            {
                IsSortDescending = SteamStoreFacets.PrefersDescending(value.Value);
            }
        }

        if (_suppressSearch) return;

        CurrentPage = 1;
        _ = RunSearchAsync();
    }

    /// <summary>
    /// Applies a newly chosen curated list. If the curated list disables custom sorting (e.g. Coming soon),
    /// any previously active sort is automatically reset to 'No particular order'.
    /// </summary>
    partial void OnSelectedStoreListChanged(SortOptionItem? value)
    {
        var isComingSoon = string.Equals(value?.Value, "comingsoon", StringComparison.OrdinalIgnoreCase);

        // Under "Coming soon", Price sort option is disabled
        var priceOption = SortOptions.FirstOrDefault(o => o.Value.Equals("Price", StringComparison.OrdinalIgnoreCase));
        if (priceOption != null)
        {
            priceOption.IsEnabled = !isComingSoon;
        }

        // If Price was selected before switching to Coming soon, reset to 'No particular order'
        if (isComingSoon && string.Equals(SelectedSort?.Value, "Price", StringComparison.OrdinalIgnoreCase))
        {
            _suppressSearch = true;
            SelectedSort = SortOptions.FirstOrDefault(o => string.IsNullOrEmpty(o.Value))
                           ?? SortOptions.FirstOrDefault();
            _suppressSearch = false;
        }

        // When switching to Coming soon with Released selected, default to soonest upcoming first (ascending)
        if (isComingSoon && string.Equals(SelectedSort?.Value, "Released", StringComparison.OrdinalIgnoreCase))
        {
            IsSortDescending = false;
        }

        var allowsSort = SteamStoreFacets.AllowsSorting(value?.Value);
        if (!allowsSort && !string.IsNullOrEmpty(SelectedSort?.Value))
        {
            _suppressSearch = true;
            SelectedSort = SortOptions.FirstOrDefault(o => string.IsNullOrEmpty(o.Value))
                           ?? SortOptions.FirstOrDefault();
            _suppressSearch = false;
        }

        OnPropertyChanged(nameof(CanChooseSort));
        OnPropertyChanged(nameof(CanInvertSort));

        if (_suppressSearch) return;

        CurrentPage = 1;
        _ = RunSearchAsync();
    }

    /// <summary>
    /// Whether the chosen sort has an opposite.
    /// </summary>
    /// <remarks>
    /// Price is the only field Steam sorts both ways. Asking it for <c>Reviews_ASC</c> or
    /// <c>Released_ASC</c> is not an error there and not a reversal either — it is an unknown
    /// token, and an unknown token gets the unsorted catalogue back, which is what made the
    /// invert button on "user reviews" look like it returned well-reviewed games. So the button
    /// stands down on every other field rather than lying about what it does.
    /// </remarks>
    public bool CanInvertSort
    {
        get
        {
            if (!CanChooseSort) return false;

            var value = SelectedSort?.Value;
            return !string.IsNullOrEmpty(value);
        }
    }

    /// <summary>
    /// Whether the order can be chosen at all, given the curated list in play.
    /// </summary>
    /// <remarks>
    /// See <see cref="SteamStoreFacets.AllowsSorting"/>: coming soon has no order but its own.
    /// The selector goes grey rather than staying live over a value the query throws away.
    /// </remarks>
    public bool CanChooseSort => SteamStoreFacets.AllowsSorting(SelectedStoreList?.Value);

    /// <summary>
    /// Flips the chosen sort between ascending and descending.
    /// </summary>
    [RelayCommand]
    public void ToggleSortDirection()
    {
        if (!CanInvertSort) return;

        IsSortDescending = !IsSortDescending;
        CurrentPage = 1;
        _ = RunSearchAsync();
    }

    /// <summary>
    /// Switches between all products, games only and software only.
    /// </summary>
    [RelayCommand]
    public void SetAppType(string? appType)
    {
        SelectedAppType = string.IsNullOrWhiteSpace(appType) ? SteamStoreFacets.AppTypeAll : appType;
        if (SelectedAppType == SteamStoreFacets.AppTypeSoftware && SelectedStoreList?.Value == "popularnew")
        {
            _suppressSearch = true;
            SelectedStoreList = StoreListOptions.FirstOrDefault(o => string.IsNullOrEmpty(o.Value))
                                ?? StoreListOptions.FirstOrDefault();
            _suppressSearch = false;
        }
        CurrentPage = 1;
        _ = RunSearchAsync();
    }

    /// <summary>
    /// Switches between the card grid and the row list, and remembers the choice.
    /// </summary>
    [RelayCommand]
    public void SetView(string? mode)
        => IsGridView = !string.Equals(mode, "list", StringComparison.OrdinalIgnoreCase);

    partial void OnIsGridViewChanged(bool value)
    {
        if (_settingsService is null) return;

        // Fire and forget: the file write is small, and nothing here waits on it.
        _ = _settingsService.SetExploreGridViewAsync(value);
    }

    /// <summary>
    /// Jumps back to the top of the results and reloads from the first page.
    /// </summary>
    [RelayCommand]
    public async Task ResetToFirstPageAsync()
    {
        CurrentPage = 1;
        await RunSearchAsync().ConfigureAwait(true);
    }

    /// <summary>
    /// Removes one active filter from the chip bar.
    /// </summary>
    [RelayCommand]
    public void RemoveFilter(FilterOptionItem? item)
    {
        if (item is null) return;

        item.State = FacetState.Neutral;
        item.IsPinned = false;

        // Only the group that owns the bubble needs its list rebuilt; the rest just update
        // their badge. Rebuilding all sixteen was what made the panel stutter.
        foreach (var group in FilterGroups)
        {
            if (group.AllOptions.Contains(item)) group.Refresh();
            else group.RefreshBadgeOnly();
        }

        OnFilterChanged();
    }

    /// <summary>
    /// Empties the search box and leaves every filter exactly where it is.
    /// </summary>
    /// <remarks>
    /// The only way to drop a search used to be "Clear all", which also threw away whatever
    /// tags had been chosen to go with it. Retyping those is the expensive half.
    /// </remarks>
    [RelayCommand]
    public void ClearSearch()
    {
        if (string.IsNullOrEmpty(SearchQuery)) return;

        // Assigning it runs the debounced search, so the results follow on their own.
        SearchQuery = string.Empty;
    }

    /// <summary>
    /// Clears every filter, the event narrowing and the search term.
    /// </summary>
    [RelayCommand]
    public void ClearAllFilters()
    {
        _suppressSearch = true;

        foreach (var group in FilterGroups) group.ClearSelection();
        SearchQuery = string.Empty;
        CurrentPage = 1;
        HiddenByContentFilters = 0;

        _suppressSearch = false;

        RefreshActiveFilters();
        _ = RunSearchAsync();
    }

    /// <summary>
    /// Activates a store tag from a result card, so clicking a bubble on a card filters by it.
    /// </summary>
    [RelayCommand]
    public async Task FilterByTag(string? tagName)
    {
        if (string.IsNullOrWhiteSpace(tagName)) return;

        if (_initTask != null) await _initTask.ConfigureAwait(true);

        _suppressSearch = true;
        // Search across the whole catalog
        SelectedStoreList = StoreListOptions.FirstOrDefault(o => string.IsNullOrEmpty(o.Value));
        SearchQuery = string.Empty;

        var applied = FilterGroups.Any(g => g.ActivateByName(tagName));

        // If not found in loaded groups, resolve ad-hoc from tag catalog
        if (!applied && _tagCatalog != null)
        {
            try
            {
                var cat = await _tagCatalog.GetCatalogAsync(_cts.Token).ConfigureAwait(true);
                var clean = FilterGroupViewModel.CleanKey(tagName);
                var found = cat.Values.FirstOrDefault(t =>
                    string.Equals(t.Name, tagName, StringComparison.OrdinalIgnoreCase) ||
                    FilterGroupViewModel.CleanKey(t.Name) == clean);

                if (found != null)
                {
                    var targetGroup = FilterGroups.FirstOrDefault(g => g.Key == "genre") ?? FilterGroups.FirstOrDefault();
                    if (targetGroup != null)
                    {
                        var opt = new FilterOptionItem(found.ToFacet(), found.Name, found.ProductCount);
                        targetGroup.AddCustomOption(opt);
                        applied = true;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not resolve ad-hoc tag {Tag}", tagName);
            }
        }

        _suppressSearch = false;

        CurrentPage = 1;
        RefreshActiveFilters();
        ScheduleSaveUsage();
        await RunSearchAsync().ConfigureAwait(true);
    }

    /// <summary>
    /// Opens the store page of a result inside the app.
    /// </summary>
    [RelayCommand]
    public void OpenDetail(SearchResult? result)
    {
        if (result is null || result.AppId == 0) return;

        DetailTarget = result;
        DetailUrl = result.StorePageUrl;
        IsDetailOpen = true;
    }

    /// <summary>Closes the detail panel.</summary>
    [RelayCommand]
    public void CloseDetail()
    {
        IsDetailOpen = false;
        DetailTarget = null;
        DetailUrl = null;
    }

    /// <summary>
    /// Closes the detail panel and filters by the featured tags of the product that was open,
    /// which is what "similar games" means here.
    /// </summary>
    [RelayCommand]
    public async Task FindSimilarAsync(SearchResult? result)
    {
        var target = result ?? DetailTarget;
        if (target is null) return;

        CloseDetail();

        if (_initTask != null) await _initTask.ConfigureAwait(true);

        var token = _cts.Token;
        var featured = await ResolveFeaturedTagsAsync(target, token).ConfigureAwait(true);

        if (_isDisposed) return;

        // Filter out connectivity, multiplayer, hardware, and technical tags
        var contentCandidates = featured
            .Where(t => !SteamTagFilterHelper.IsConnectivityOrTechnicalTag(t))
            .ToList();

        if (contentCandidates.Count == 0 && featured.Count > 0)
        {
            contentCandidates = featured.ToList();
        }

        if (contentCandidates.Count == 0)
        {
            _notificationService?.ShowInfo(
                "No tags to match",
                $"Steam lists no store tags for {target.Name}, so there is nothing to find similar games by.");
            return;
        }

        _suppressSearch = true;
        // Search across the whole catalog
        SelectedStoreList = StoreListOptions.FirstOrDefault(o => string.IsNullOrEmpty(o.Value));
        SearchQuery = string.Empty;
        foreach (var group in FilterGroups) group.ClearSelection();

        // Only search and activate in content/genre/theme/gameplay/pace/style groups
        var contentGroupKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "genre", "setting", "gameplay", "pace", "style"
        };

        var applied = new List<string>();

        foreach (var tag in contentCandidates)
        {
            var activated = false;
            foreach (var group in FilterGroups)
            {
                if (!contentGroupKeys.Contains(group.Key)) continue;

                if (group.ActivateByName(tag))
                {
                    applied.Add(tag);
                    activated = true;
                    break;
                }
            }

            if (!activated && _tagCatalog != null)
            {
                try
                {
                    var catalog = await _tagCatalog.GetCatalogAsync(token).ConfigureAwait(true);
                    var cleanTag = FilterGroupViewModel.CleanKey(tag);
                    var found = catalog.Values.FirstOrDefault(t =>
                        string.Equals(t.Name, tag, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(FilterGroupViewModel.CleanKey(t.Name), cleanTag, StringComparison.OrdinalIgnoreCase));

                    if (found != null && !SteamTagFilterHelper.IsConnectivityOrTechnicalTag(found.Name))
                    {
                        var targetGroup = FilterGroups.FirstOrDefault(g => g.Key == "gameplay") ?? FilterGroups.First();
                        var opt = new FilterOptionItem(found.ToFacet(), found.Name, found.ProductCount);
                        targetGroup.AddCustomOption(opt);
                        applied.Add(found.Name);
                    }
                }
                catch
                {
                    // best effort
                }
            }

            if (applied.Count == 4) break;
        }

        _suppressSearch = false;

        if (applied.Count == 0)
        {
            _notificationService?.ShowInfo(
                "No matching filters",
                $"None of the tags on {target.Name} are in the filter panel, so Explore has nothing to narrow by.");
            return;
        }

        CurrentPage = 1;
        RefreshActiveFilters();
        ScheduleSaveUsage();
        await RunSearchAsync().ConfigureAwait(true);
    }

    /// <summary>
    /// Finds the store tags of a product, wherever they happen to be available.
    /// </summary>
    /// <returns>Tag names in Steam's own order of prominence; empty when none could be read.</returns>
    private async Task<IReadOnlyList<string>> ResolveFeaturedTagsAsync(SearchResult target, CancellationToken token)
    {
        // Already resolved: a card that came from a search here.
        if (target.StoreTags.Count > 0)
        {
            return target.StoreTags.Select(t => t.Name).ToList();
        }

        // Steam ships tag ids as data attributes on every search row, so this costs nothing but
        // a catalogue lookup.
        if (target.TagIds.Count > 0 && _tagCatalog != null)
        {
            try
            {
                var names = await _tagCatalog.ResolveNamesAsync(target.TagIds, token).ConfigureAwait(true);
                if (names.Count > 0) return names;
            }
            catch (OperationCanceledException)
            {
                return [];
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not resolve tag names for AppId {AppId}", target.AppId);
            }
        }

        // Nothing local to go on: read the store page. One request, cached for a week.
        if (target.AppId != 0 && _metadataProvider != null)
        {
            try
            {
                return await _metadataProvider.GetStoreTagsAsync(target.AppId, token).ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                return [];
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not read store tags for AppId {AppId}", target.AppId);
            }
        }

        return [];
    }

    /// <summary>
    /// Kept for the navigation history: an Explore entry that used to point at a carousel now
    /// resolves to the equivalent Steam store list in the toolbar.
    /// </summary>
    public void ExpandCategory(string categoryId)
    {
        if (string.IsNullOrWhiteSpace(categoryId)) return;

        var listValue = categoryId.ToLowerInvariant() switch
        {
            var id when id.Contains("top_seller", StringComparison.Ordinal) => "globaltopsellers",
            var id when id.Contains("trending", StringComparison.Ordinal) => "popularnew",
            var id when id.Contains("most_played", StringComparison.Ordinal) => "globaltopsellers",
            var id when id.Contains("coming", StringComparison.Ordinal) => "comingsoon",
            _ => "popularnew"
        };

        // A chip naming a list the toolbar no longer offers falls back to the whole catalogue
        // rather than clearing the selector to nothing.
        SelectedStoreList = StoreListOptions.FirstOrDefault(o => o.Value == listValue)
                            ?? StoreListOptions.FirstOrDefault();
    }

    private static void OpenUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;

        try
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
        }
        catch
        {
            // Opening a browser is best effort.
        }
    }

    private async Task LoadUsageAsync()
    {
        if (_cache is null) return;

        try
        {
            var stored = await _cache.GetAsync<Dictionary<string, int>>(UsageCacheKey, _cts.Token).ConfigureAwait(false);
            if (stored is not null)
            {
                foreach (var (key, value) in stored) _facetUsage[key] = value;
            }

            var catStored = await _cache.GetAsync<CategoryUsageSnapshot>(CategoryUsageCacheKey, _cts.Token).ConfigureAwait(false);
            if (catStored is not null)
            {
                _categoryUsage = catStored;
            }
        }
        catch
        {
            // A missing usage history just means no bubble is pinned yet.
        }
    }

    private void ScheduleSaveUsage()
    {
        if (_cache is null) return;

        // Take snapshot synchronously on the UI thread to prevent concurrent modification during background I/O
        var facetUsageSnapshot = new Dictionary<string, int>(_facetUsage);
        CategoryUsageSnapshot? categorySnapshot = null;

        if (FilterGroups != null)
        {
            categorySnapshot = new CategoryUsageSnapshot();
            foreach (var group in FilterGroups)
            {
                categorySnapshot.Counters[group.Key] = group.CategorySelectionCounter;
                categorySnapshot.LastSelected[group.Key] = new Dictionary<string, int>(group.LastSelectedAt);
            }
            _categoryUsage = categorySnapshot;
        }

        var token = _cts.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                await _cache.SetAsync(UsageCacheKey, facetUsageSnapshot,
                    TimeSpan.FromDays(365), token).ConfigureAwait(false);

                if (categorySnapshot != null)
                {
                    await _cache.SetAsync(CategoryUsageCacheKey, categorySnapshot,
                        TimeSpan.FromDays(365), token).ConfigureAwait(false);
                }
            }
            catch
            {
                // Non-critical persistence
            }
        }, token);
    }

    /// <summary>
    /// Opens the SteamDB page for the specified search result in the default browser.
    /// </summary>
    [RelayCommand]
    public static void OpenSteamDb(SearchResult? result)
    {
        if (result == null || result.AppId == 0) return;
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = result.SteamDbUrl,
                UseShellExecute = true
            });
        }
        catch { }
    }

    /// <summary>
    /// Adds the game to the library and navigates to its detail page with live notifications.
    /// </summary>
    [RelayCommand]
    public async Task AddToLibraryAsync(SearchResult result)
    {
        if (result == null) return;

        result.IsCreating = true;
        result.CreationStatus = "Preparando...";
        IsCreatingInstance = true;
        ErrorMessage = null;

        _notificationService?.ShowInfo("Preparing Instance", $"Fetching manifests and preparing {result.Name}...");

        if (_backgroundTaskService != null)
        {
            _backgroundTaskService.QueueTask(
                $"Downloading Depots: {result.Name}",
                result.Name,
                async (progress, ct) =>
                {
                    try
                    {
                        await ProcessAddToLibraryInternalAsync(result, progress, ct).ConfigureAwait(false);
                    }
                    finally
                    {
                        App.Current?.Dispatcher?.Invoke(() =>
                        {
                            result.IsCreating = false;
                            result.CreationStatus = null;
                            IsCreatingInstance = false;
                        });
                    }
                });
        }
        else
        {
            try
            {
                var dummyProgress = new Progress<BackgroundTaskProgress>();
                await ProcessAddToLibraryInternalAsync(result, dummyProgress, CancellationToken.None).ConfigureAwait(true);
            }
            finally
            {
                result.IsCreating = false;
                result.CreationStatus = null;
                IsCreatingInstance = false;
            }
        }
    }

    private async Task ProcessAddToLibraryInternalAsync(
        SearchResult result,
        IProgress<BackgroundTaskProgress> progress,
        CancellationToken ct)
    {
        progress.Report(new BackgroundTaskProgress(0, "Fetching metadata...", "Preparing"));

        _logger.LogInformation("Adding game {Name} ({AppId}) to library", result.Name, result.AppId);

        var defaultRoot = !string.IsNullOrWhiteSpace(_settingsService?.DefaultDownloadDirectory)
            ? _settingsService.DefaultDownloadDirectory
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BlueStar", "games");
        var installPath = PathHelper.EnsureGameSubfolder(defaultRoot, result.Name);

        // Fetch rich Steam metadata if available
        GameMetadata? meta = null;
        if (_metadataProvider != null)
        {
            try
            {
                meta = await _metadataProvider.GetMetadataAsync(result.AppId, ct).ConfigureAwait(false);
            }
            catch { }
        }

        var engine = await _engineDetector.DetectEngineAsync(installPath, ct).ConfigureAwait(false);

        // Attempt to download and parse DepotBox archive for this game
        string? archivePath = null;
        DepotBoxArchive? archive = null;
        var archivesDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "BlueStar", "archives");
        Directory.CreateDirectory(archivesDir);

        progress.Report(new BackgroundTaskProgress(5, "Downloading depot archive from DepotBox...", "Downloading"));

        var dlProgress = new Progress<DownloadProgress>(p =>
        {
            var mb = p.DownloadedBytes / (1024.0 * 1024.0);
            var totalMb = p.TotalBytes > 0 ? $" / {p.TotalBytes / (1024.0 * 1024.0):F1} MB" : " MB";
            var pct = 5.0 + (p.Percentage * 0.85); // scales 5% -> 90%
            progress.Report(new BackgroundTaskProgress(
                pct,
                $"Downloading depot archive ({mb:F1}{totalMb})...",
                "Downloading"));

            App.Current?.Dispatcher?.Invoke(() =>
            {
                result.CreationStatus = $"Downloading ({p.Percentage:F0}%)...";
            });
        });

        try
        {
            archivePath = await _apiClient.DownloadArchiveAsync(result.AppId, archivesDir, dlProgress, ct).ConfigureAwait(false);
            if (File.Exists(archivePath))
            {
                progress.Report(new BackgroundTaskProgress(90, "Parsing depot archive manifests...", "Parsing"));
                archive = await _archiveParser.ParseAsync(archivePath, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not pre-download DepotBox archive for {AppId}", result.AppId);
        }

        progress.Report(new BackgroundTaskProgress(94, "Configuring game instance and depots...", "Configuring"));

        // Generate unique instance name and non-colliding installation path
        var allExisting = await _instanceManager.GetAllAsync(ct).ConfigureAwait(false);
        var existingNames = allExisting.Select(i => i.Name).ToList();
        var existingPaths = allExisting.Select(i => i.InstallPath).Where(p => !string.IsNullOrWhiteSpace(p)).ToList();

        var rawName = result.Name;
        if (archive != null && archive.Games.Count > 0)
        {
            var mainGame = archive.Games.FirstOrDefault(g => !g.IsDlc) ?? archive.Games[0];
            rawName = CleanName(mainGame.Name) ?? result.Name;
        }

        var uniqueName = PathHelper.GenerateUniqueInstanceName(existingNames, rawName);
        var uniqueInstallPath = PathHelper.GenerateUniqueInstallPath(defaultRoot, uniqueName, existingPaths);

        IReadOnlyList<DepotInfo> depots = [];
        IReadOnlyList<DlcInfo> dlcs = [];
        string? activeBuildId = null;
        string? activeBranch = null;

        if (archive != null && archive.Games.Count > 0 && archive.Games.Any(g => g.Depots.Count > 0))
        {
            depots = archive.Games.SelectMany(g => g.Depots.Select(d => new DepotInfo
            {
                DepotId = d.DepotId,
                ManifestId = d.ManifestId,
                SizeBytes = d.SizeBytes,
                DepotKey = g.DepotKey,
                Name = d.Name ?? (g.IsDlc ? $"{CleanName(g.Name)} Depot" : "Base Game Content"),
                Category = d.Category,
                Platform = d.Platform,
                Architecture = d.Architecture,
                IsSharedDepot = false
            })).DistinctBy(d => d.DepotId).ToList().AsReadOnly();

            dlcs = archive.Games.Where(g => g.IsDlc).Select(dlc => new DlcInfo
            {
                AppId = dlc.AppId,
                Name = CleanName(dlc.Name) ?? $"DLC {dlc.AppId}",
                Category = "DLC",
                Platform = dlc.Depots.FirstOrDefault(d => !string.IsNullOrWhiteSpace(d.Platform))?.Platform ?? "Universal",
                Depots = dlc.Depots.Select(d => new DepotInfo
                {
                    DepotId = d.DepotId,
                    ManifestId = d.ManifestId,
                    SizeBytes = d.SizeBytes,
                    DepotKey = dlc.DepotKey,
                    Name = d.Name ?? $"{CleanName(dlc.Name)} Depot",
                    Category = "DLC",
                    Platform = d.Platform,
                    Architecture = d.Architecture,
                    IsSharedDepot = false
                }).ToList().AsReadOnly(),
                IsInstalled = false
            }).ToList().AsReadOnly();
        }
        else if (_buildResolver != null)
        {
            try
            {
                progress.Report(new BackgroundTaskProgress(92, "Resolving builds and manifests across providers...", "Resolving"));
                var recommendedVersion = await _buildResolver.ResolveRecommendedVersionAsync(result.AppId, ct).ConfigureAwait(false);
                if (recommendedVersion != null && recommendedVersion.Depots.Count > 0)
                {
                    depots = recommendedVersion.Depots.Select(d => d.ToDepotInfo()).ToList().AsReadOnly();
                    activeBuildId = recommendedVersion.BuildId;
                    activeBranch = recommendedVersion.BranchName;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not resolve build automatically for AppId {AppId}", result.AppId);
            }
        }

        // If DLCs not yet populated and metadata provider is available, fetch them
        if (dlcs.Count == 0 && _metadataProvider != null)
        {
            try
            {
                dlcs = await _metadataProvider.GetDlcListAsync(result.AppId, ct).ConfigureAwait(false);
            }
            catch { }
        }

        var newInstance = new GameInstance
        {
            Name = uniqueName,
            AppId = result.AppId,
            InstallPath = uniqueInstallPath,
            SourceArchivePath = archivePath,
            Status = InstanceStatus.NotInstalled,
            Metadata = meta,
            Engine = engine,
            ActiveBuildId = activeBuildId,
            ActiveBranch = activeBranch,
            Depots = depots,
            Dlcs = dlcs
        };


        var created = await _instanceManager.CreateAsync(newInstance, ct).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(archivePath) && File.Exists(archivePath))
        {
            progress.Report(new BackgroundTaskProgress(98, "Extracting manifests...", "Extracting"));
            ExtractManifestsToInstanceStorage(archivePath, created.Id);
        }

        _logger.LogInformation("Created new instance: {Id} ({Name})", created.Id, created.Name);
        _notificationService?.ShowSuccess("Instance Created", $"Configured {newInstance.Name} with {newInstance.Depots.Count} available depot(s).");
        _ = _statsService?.ReportInstanceAddedAsync(created.AppId, created.Name);

        progress.Report(new BackgroundTaskProgress(100, $"Ready ({newInstance.Depots.Count} depots configured)", "Complete"));

        App.Current?.Dispatcher?.Invoke(() =>
        {
            result.CreationStatus = "Instance ready";
            OnManageInstanceRequested?.Invoke(created);
        });
    }

    private static void ExtractManifestsToInstanceStorage(string zipPath, Guid instanceId)
    {
        if (string.IsNullOrWhiteSpace(zipPath) || !File.Exists(zipPath)) return;

        try
        {
            var manifestDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "BlueStar", "instances", instanceId.ToString(), "manifests");
            Directory.CreateDirectory(manifestDir);

            using var archive = System.IO.Compression.ZipFile.OpenRead(zipPath);
            foreach (var entry in archive.Entries)
            {
                if (!entry.Name.EndsWith(".manifest", StringComparison.OrdinalIgnoreCase)) continue;

                var dest = Path.Combine(manifestDir, entry.Name);
                using var entryStream = entry.Open();
                using var fileStream = File.Create(dest);
                entryStream.CopyTo(fileStream);
            }
        }
        catch { }
    }

    private static string? CleanName(string? rawName)
    {
        if (string.IsNullOrWhiteSpace(rawName)) return rawName;
        var name = rawName.Trim();
        var prefixes = new[] { "Gamename ", "Gamename", "Dlcname ", "Dlcname", "Game Name:", "Game Name ", "Game:", "Name:", "App:" };
        foreach (var p in prefixes)
        {
            if (name.StartsWith(p, StringComparison.OrdinalIgnoreCase))
                name = name[p.Length..].Trim();
        }
        return name;
    }

    /// <summary>
    /// Releases all event subscriptions and cancels active operations.
    /// </summary>
    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        try
        {
            _cts.Cancel();
            _cts.Dispose();
        }
        catch { }

        if (_settingsService != null && _settingsChangedHandler != null)
        {
            _settingsService.SettingsChanged -= _settingsChangedHandler;
        }
    }
}
