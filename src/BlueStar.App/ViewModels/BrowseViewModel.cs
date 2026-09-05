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
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace BlueStar.App.ViewModels;

/// <summary>
/// ViewModel for searching and exploring games in DepotBox catalog with live Steam/SteamDB enrichment and instance installation notifications.
/// </summary>
public partial class BrowseViewModel : ObservableObject, IDisposable
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

    [ObservableProperty]
    private string _searchQuery = string.Empty;

    [ObservableProperty]
    private ObservableCollection<SearchResult> _results = [];

    [ObservableProperty]
    private ObservableCollection<SearchResult> _filteredResults = [];

    [ObservableProperty]
    private ObservableCollection<CatalogCategory> _categories = [];

    [ObservableProperty]
    private ObservableCollection<TrendingChipItem> _trendingSuggestionChips = [];

    [ObservableProperty]
    private string _selectedTypeFilter = "All";

    [ObservableProperty]
    private bool _hasActiveTypeFilter;

    [ObservableProperty]
    private bool _isSearching;

    [ObservableProperty]
    private bool _isCreatingInstance;

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    private bool _hasSearched;

    public Action<GameInstance>? OnManageInstanceRequested { get; set; }

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
        IBuildResolver? buildResolver = null)
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


        if (_settingsService != null)
        {
            _settingsChangedHandler = (_, _) =>
            {
                App.Current?.Dispatcher?.Invoke(() =>
                {
                    if (_isDisposed) return;
                    _ = LoadCategoryFeedsAsync();
                    ApplyFilter();
                });
            };
            _settingsService.SettingsChanged += _settingsChangedHandler;
        }

        _ = LoadCategoryFeedsAsync();
    }

    private string? _pendingExpandedCategoryId;
    public Action<string>? OnScrollToCategoryRequested;

    public void ExpandCategory(string categoryId)
    {
        _pendingExpandedCategoryId = categoryId;
        HasSearched = false;
        SearchQuery = string.Empty;

        if (Categories != null && Categories.Count > 0)
        {
            foreach (var cat in Categories)
            {
                cat.IsExpanded = string.Equals(cat.Id, categoryId, StringComparison.OrdinalIgnoreCase);
            }
        }

        OnScrollToCategoryRequested?.Invoke(categoryId);
    }

    [RelayCommand]
    public void ToggleCategoryExpand(CatalogCategory category)
    {
        if (category == null) return;
        category.IsExpanded = !category.IsExpanded;
    }

    private List<SearchResult> FilterBySettings(IEnumerable<SearchResult> source)
    {
        if (source == null) return [];
        var allowNsfw = _settingsService?.ShowNsfwContent ?? false;
        var allowDrm = _settingsService?.ShowDrmContent ?? true;

        return source.Where(item =>
            (allowNsfw || !item.IsNsfw) &&
            (allowDrm || !item.HasDrm)).ToList();
    }

    public async Task LoadCategoryFeedsAsync()
    {
        await Task.Yield();

        var catTrending = new CatalogCategory("bluestar_trending_7d", "Trending on BlueStar", "Most added to instances in the last 7 days", "IconFlame", "#3B82F6", "LAST WEEK");
        var catMostPlayed = new CatalogCategory("bluestar_most_played_alltime", "Most Added in BlueStar", "Titles with the most instances created of all time", "IconTrophy", "#8B5CF6", "ALL TIME");
        var catSteamDbMostPlayed = new CatalogCategory("steamdb_most_played", "Most Played", "Top concurrent players in real time", "IconUsers", "#10B981", "STEAM");
        var catSteamDbTrending = new CatalogCategory("steamdb_trending", "Trending Games", "Titles with highest recent activity growth", "IconTrending", "#F59E0B", "STEAM");
        var catSteamDbTopSellers = new CatalogCategory("steamdb_top_sellers", "Top Sellers & Popular", "Top selling releases and deals worldwide", "IconTag", "#EC4899", "STEAM");
        var catSteamDbTopRated = new CatalogCategory("steamdb_top_rated", "Top Rated & Anticipated", "Top rated by community and critics", "IconStar", "#6366F1", "STEAM");
        Categories = new ObservableCollection<CatalogCategory>
        {
            catTrending,
            catMostPlayed,
            catSteamDbMostPlayed,
            catSteamDbTrending,
            catSteamDbTopSellers,
            catSteamDbTopRated
        };

        if (!string.IsNullOrWhiteSpace(_pendingExpandedCategoryId))
        {
            foreach (var cat in Categories)
            {
                cat.IsExpanded = string.Equals(cat.Id, _pendingExpandedCategoryId, StringComparison.OrdinalIgnoreCase);
            }
            OnScrollToCategoryRequested?.Invoke(_pendingExpandedCategoryId);
        }

        var defaultTrendingNames = new[] { "Counter-Strike 2", "Cyberpunk 2077", "ELDEN RING", "Baldur's Gate 3", "Hades II" };
        TrendingSuggestionChips = new ObservableCollection<TrendingChipItem>(
            defaultTrendingNames.Select((name, idx) => TrendingChipItem.Create(name, idx + 1)));

        if (_statsService == null)
        {
            foreach (var c in Categories) c.IsLoading = false;
            return;
        }

        // 2. Load feeds asynchronously & progressively
        _ = Task.Run(async () =>
        {
            try
            {
                var trendingItems = await _statsService.GetTrendingBlueStarAsync().ConfigureAwait(false);
                catTrending.PoolItems = trendingItems.ToList();
                var filtered = FilterBySettings(catTrending.PoolItems);
                var initial = filtered.Take(catTrending.DisplayLimit).ToList();
                catTrending.Items = new ObservableCollection<SearchResult>(initial);
                catTrending.IsLoading = false;
                catTrending.HasMoreItems = filtered.Count > initial.Count;
                _ = EnrichResultsAsync(initial, catTrending);

                if (filtered.Count > 0)
                {
                    var chips = filtered.Take(5)
                        .Select(t => t.Name)
                        .Where(n => !string.IsNullOrWhiteSpace(n))
                        .Select((name, idx) => TrendingChipItem.Create(name, idx + 1))
                        .ToList();

                    App.Current?.Dispatcher?.Invoke(() =>
                    {
                        TrendingSuggestionChips = new ObservableCollection<TrendingChipItem>(chips);
                    });
                }
            }
            catch { catTrending.IsLoading = false; }
        });

        _ = Task.Run(async () =>
        {
            try
            {
                var items = await _statsService.GetMostPlayedBlueStarAsync().ConfigureAwait(false);
                catMostPlayed.PoolItems = items.ToList();
                var filtered = FilterBySettings(catMostPlayed.PoolItems);
                var initial = filtered.Take(catMostPlayed.DisplayLimit).ToList();
                catMostPlayed.Items = new ObservableCollection<SearchResult>(initial);
                catMostPlayed.IsLoading = false;
                catMostPlayed.HasMoreItems = filtered.Count > initial.Count;
                _ = EnrichResultsAsync(initial, catMostPlayed);
            }
            catch { catMostPlayed.IsLoading = false; }
        });

        _ = Task.Run(() => LoadSteamCategoryFeedAsync(catSteamDbMostPlayed, "most_played"));
        _ = Task.Run(() => LoadSteamCategoryFeedAsync(catSteamDbTrending, "trending"));
        _ = Task.Run(() => LoadSteamCategoryFeedAsync(catSteamDbTopSellers, "top_sellers"));
        _ = Task.Run(() => LoadSteamCategoryFeedAsync(catSteamDbTopRated, "top_rated"));
    }


    private async Task LoadSteamCategoryFeedAsync(CatalogCategory cat, string type)
    {
        if (_statsService == null || cat == null) return;
        try
        {
            var items = await _statsService.GetSteamDbListAsync(type, 0, 30).ConfigureAwait(false);
            cat.PoolItems = items.ToList();
            var filtered = FilterBySettings(cat.PoolItems);

            // If some items were filtered out by tags and we have less than DisplayLimit (9 slots), fetch more from Steam
            while (filtered.Count < cat.DisplayLimit)
            {
                var offset = cat.PoolItems.Count;
                var more = await _statsService.GetSteamDbListAsync(type, offset, 25).ConfigureAwait(false);
                if (more == null || more.Count == 0) break;
                var newUnique = more.Where(m => m.AppId > 0 && !cat.PoolItems.Any(p => p.AppId == m.AppId)).ToList();
                if (newUnique.Count == 0) break;
                cat.PoolItems.AddRange(newUnique);
                filtered = FilterBySettings(cat.PoolItems);
            }

            var initial = filtered.Take(cat.DisplayLimit).ToList();
            cat.Items = new ObservableCollection<SearchResult>(initial);
            cat.IsLoading = false;
            cat.HasMoreItems = cat.PoolItems.Count > 0;
        }
        catch { cat.IsLoading = false; }
    }


    [RelayCommand]
    public void ClearSearch()
    {
        SearchQuery = string.Empty;
        Results.Clear();
        FilteredResults.Clear();
        HasSearched = false;
        ErrorMessage = null;
        SelectedTypeFilter = "All";
        HasActiveTypeFilter = false;
    }

    [RelayCommand]
    public async Task QuickSearchAsync(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return;
        SearchQuery = query;
        await SearchAsync();
    }

    [RelayCommand]
    public void SelectTypeFilter(string filter)
    {
        SelectedTypeFilter = filter ?? "All";
        HasActiveTypeFilter = !string.Equals(SelectedTypeFilter, "All", StringComparison.OrdinalIgnoreCase);
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        if (Results.Count == 0)
        {
            FilteredResults = [];
            return;
        }

        IEnumerable<SearchResult> filtered = FilterBySettings(Results);

        if (string.Equals(SelectedTypeFilter, "Games", StringComparison.OrdinalIgnoreCase))
        {
            filtered = filtered.Where(r => string.Equals(r.AppType, "Game", StringComparison.OrdinalIgnoreCase));
        }
        else if (string.Equals(SelectedTypeFilter, "Tools", StringComparison.OrdinalIgnoreCase))
        {
            filtered = filtered.Where(r => string.Equals(r.AppType, "Tool", StringComparison.OrdinalIgnoreCase) ||
                                           string.Equals(r.AppType, "Application", StringComparison.OrdinalIgnoreCase));
        }
        else if (string.Equals(SelectedTypeFilter, "DLCs", StringComparison.OrdinalIgnoreCase))
        {
            filtered = filtered.Where(r => r.DlcCount.HasValue && r.DlcCount.Value > 0);
        }

        FilteredResults = new ObservableCollection<SearchResult>(filtered);
    }

    /// <summary>
    /// Searches for games matching the current query and enriches results in the background.
    /// </summary>
    [RelayCommand]
    public async Task SearchAsync()
    {
        if (string.IsNullOrWhiteSpace(SearchQuery))
        {
            ClearSearch();
            return;
        }

        IsSearching = true;
        ErrorMessage = null;
        HasSearched = true;
        Results.Clear();
        FilteredResults.Clear();

        try
        {
            _logger.LogInformation("Searching catalog for: {Query}", SearchQuery);
            IReadOnlyList<SearchResult> searchResults = [];

            if (_catalogProvider != null)
            {
                try
                {
                    searchResults = await _catalogProvider.SearchGamesAsync(SearchQuery, CancellationToken.None).ConfigureAwait(true);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Catalog provider search failed, falling back to DepotBox");
                }
            }

            if (searchResults.Count == 0 && _apiClient != null)
            {
                searchResults = await _apiClient.SearchGamesAsync(SearchQuery, CancellationToken.None).ConfigureAwait(true);
            }

            Results = new ObservableCollection<SearchResult>(searchResults);
            ApplyFilter();
            _logger.LogInformation("Found {Count} results", Results.Count);

            // Enrich search results in parallel (DLCs, OS, Depot Version)
            _ = EnrichResultsAsync(searchResults);
        }

        catch (UnauthorizedAccessException)
        {
            ErrorMessage = "Invalid or missing API key. Please configure your DepotBox API key in Settings.";
        }
        catch (HttpRequestException ex)
        {
            ErrorMessage = $"Network or API error: {ex.Message}";
            _logger.LogError(ex, "DepotBox API search failed");
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Search failed: {ex.Message}";
            _logger.LogError(ex, "Unexpected search error");
        }
        finally
        {
            IsSearching = false;
        }
    }

    private async Task EnrichResultsAsync(IEnumerable<SearchResult> results, CatalogCategory? parentCategory = null)
    {
        var items = results.ToList();
        var allowNsfw = _settingsService?.ShowNsfwContent ?? false;
        var allowDrm = _settingsService?.ShowDrmContent ?? true;

        await Parallel.ForEachAsync(items, new ParallelOptions { MaxDegreeOfParallelism = 4 }, async (result, ct) =>
        {
            if (_isDisposed) return;
            try
            {
                // 1. Enrich from Steam Store / Web API (DLC count, OS compatibility, high-res artwork, release/update date, app type, NSFW, DRM)
                if (_metadataProvider != null && result.AppId > 0)
                {
                    await _metadataProvider.EnrichSearchResultAsync(result, ct).ConfigureAwait(false);
                }

                // 2. If version date is still empty, attempt fallback to latest app update date
                if (result.AppId > 0 && string.IsNullOrWhiteSpace(result.Version) && _metadataProvider is BlueStar.Infrastructure.Metadata.SteamStoreApiClient steamClient)
                {
                    try
                    {
                        var updateDate = await steamClient.GetLatestAppUpdateDateAsync(result.AppId, ct).ConfigureAwait(false);
                        if (updateDate.HasValue)
                        {
                            result.Version = $"{updateDate.Value.LocalDateTime:d MMM yyyy}";
                        }
                    }
                    catch { }
                }

                // 3. If after enrichment this game is NSFW or DRM and is part of a catalog category carousel, replenish it
                if ((!allowNsfw && result.IsNsfw) || (!allowDrm && result.HasDrm))
                {
                    App.Current?.Dispatcher?.Invoke(() =>
                    {
                        if (parentCategory != null)
                        {
                            parentCategory.Items?.Remove(result);
                            _ = ReplenishCategoryAsync(parentCategory);
                        }
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Background metadata enrichment error for AppId={AppId}", result.AppId);
            }
        }).ConfigureAwait(false);
    }

    private async Task ReplenishCategoryAsync(CatalogCategory category)
    {
        if (category == null || _statsService == null) return;
        var allowNsfw = _settingsService?.ShowNsfwContent ?? false;
        var allowDrm = _settingsService?.ShowDrmContent ?? true;

        var needed = category.DisplayLimit - category.Items.Count;
        if (needed <= 0) return;

        var existingIds = new HashSet<uint>(category.Items.Select(i => i.AppId));
        var candidates = category.PoolItems
            .Where(i => i.AppId > 0 && !existingIds.Contains(i.AppId))
            .Where(i => (allowNsfw || !i.IsNsfw) && (allowDrm || !i.HasDrm))
            .Take(needed)
            .ToList();

        // If it's a Steam ranking category and pool has fewer candidates than needed, fetch more from Steam Store
        if (candidates.Count < needed && IsSteamCategory(category.Id))
        {
            try
            {
                var listType = GetListTypeForCategory(category.Id);
                while (candidates.Count < needed)
                {
                    var offset = category.Items.Count + category.PoolItems.Count;
                    var more = await _statsService.GetSteamDbListAsync(listType, offset, 25).ConfigureAwait(false);
                    if (more == null || more.Count == 0) break;

                    var newUnique = more.Where(m => m.AppId > 0 && !existingIds.Contains(m.AppId) && !category.PoolItems.Any(p => p.AppId == m.AppId)).ToList();
                    if (newUnique.Count == 0) break;

                    category.PoolItems.AddRange(newUnique);

                    var extra = newUnique
                        .Where(i => (allowNsfw || !i.IsNsfw) && (allowDrm || !i.HasDrm))
                        .Take(needed - candidates.Count)
                        .ToList();

                    candidates.AddRange(extra);
                }
            }
            catch { }
        }

        if (candidates.Count > 0)
        {
            // Pre-enrich candidates before UI insertion
            await Parallel.ForEachAsync(candidates, new ParallelOptions { MaxDegreeOfParallelism = 4 }, async (candidate, ct) =>
            {
                try
                {
                    if (_metadataProvider != null && candidate.AppId > 0)
                    {
                        await _metadataProvider.EnrichSearchResultAsync(candidate, ct).ConfigureAwait(false);
                    }
                }
                catch { }
            }).ConfigureAwait(false);

            var clean = candidates.Where(i => (allowNsfw || !i.IsNsfw) && (allowDrm || !i.HasDrm)).ToList();
            if (clean.Count > 0)
            {
                App.Current?.Dispatcher?.Invoke(() =>
                {
                    foreach (var item in clean)
                    {
                        if (!category.Items.Any(i => i.AppId == item.AppId))
                        {
                            category.Items.Add(item);
                        }
                    }
                });
            }

            // If any candidate was filtered out post-enrichment, replenish again to ensure 9 slots stay full
            if (clean.Count < candidates.Count && category.Items.Count < category.DisplayLimit)
            {
                _ = ReplenishCategoryAsync(category);
            }
        }
    }

    private static bool IsSteamCategory(string categoryId)
    {
        return !string.IsNullOrWhiteSpace(categoryId) &&
               categoryId.StartsWith("steamdb_", StringComparison.OrdinalIgnoreCase);
    }

    private static string GetListTypeForCategory(string categoryId)
    {
        if (string.IsNullOrWhiteSpace(categoryId)) return "top_sellers";
        if (categoryId.Contains("most_played", StringComparison.OrdinalIgnoreCase)) return "most_played";
        if (categoryId.Contains("trending", StringComparison.OrdinalIgnoreCase)) return "trending";
        if (categoryId.Contains("top_rated", StringComparison.OrdinalIgnoreCase)) return "top_rated";
        if (categoryId.Contains("top_sellers", StringComparison.OrdinalIgnoreCase)) return "top_sellers";
        return "top_sellers";
    }

    /// <summary>
    /// Loads more games for the given expanded category.
    /// Uses skeleton placeholder during background loading and metadata enrichment,
    /// then cleanly appends the verified games in a single UI dispatch.
    /// </summary>
    [RelayCommand]
    public async Task LoadMoreCategoryItemsAsync(CatalogCategory category)
    {
        if (category == null || category.IsLoadingMore || _statsService == null) return;
        category.IsLoadingMore = true;

        try
        {
            var allowNsfw = _settingsService?.ShowNsfwContent ?? false;
            var allowDrm = _settingsService?.ShowDrmContent ?? true;

            const int batchSize = 9;
            var existingIds = new HashSet<uint>(category.Items.Select(i => i.AppId));
            var candidates = new List<SearchResult>();

            // 1. Take unadded candidates from local PoolItems first
            var poolCandidates = category.PoolItems
                .Where(i => i.AppId > 0 && !existingIds.Contains(i.AppId))
                .Where(i => (allowNsfw || !i.IsNsfw) && (allowDrm || !i.HasDrm))
                .Take(batchSize)
                .ToList();

            candidates.AddRange(poolCandidates);

            // 2. If it's a Steam ranking category and we need more items, query Steam Store API with pagination
            if (candidates.Count < batchSize && IsSteamCategory(category.Id))
            {
                try
                {
                    var listType = GetListTypeForCategory(category.Id);
                    var offset = category.Items.Count + category.PoolItems.Count;
                    var more = await _statsService.GetSteamDbListAsync(listType, offset, 25).ConfigureAwait(false);

                    var newUnique = more
                        .Where(m => m.AppId > 0 && !existingIds.Contains(m.AppId) && !category.PoolItems.Any(p => p.AppId == m.AppId))
                        .ToList();

                    category.PoolItems.AddRange(newUnique);

                    var extra = newUnique
                        .Where(i => (allowNsfw || !i.IsNsfw) && (allowDrm || !i.HasDrm))
                        .Take(batchSize - candidates.Count)
                        .ToList();

                    candidates.AddRange(extra);
                    category.HasMoreItems = more.Count > 0;
                }
                catch { }
            }
            else if (!IsSteamCategory(category.Id))
            {
                // For BlueStar and DepotBox, HasMoreItems depends purely on authentic pool items remaining
                var remainingInPool = category.PoolItems.Count(i => i.AppId > 0 && !existingIds.Contains(i.AppId) && !candidates.Contains(i));
                category.HasMoreItems = remainingInPool > 0;
            }

            if (candidates.Count > 0)
            {
                var cleanCandidates = candidates
                    .Where(i => (allowNsfw || !i.IsNsfw) && (allowDrm || !i.HasDrm))
                    .ToList();

                if (cleanCandidates.Count > 0)
                {
                    category.DisplayLimit += cleanCandidates.Count;
                    App.Current?.Dispatcher?.Invoke(() =>
                    {
                        foreach (var item in cleanCandidates)
                        {
                            if (!category.Items.Any(i => i.AppId == item.AppId))
                            {
                                category.Items.Add(item);
                            }
                        }
                    });

                    // Non-blocking background metadata enrichment (DLCs, tags) without holding up UI
                    _ = EnrichResultsAsync(cleanCandidates, category);
                }
            }
            else if (IsSteamCategory(category.Id))
            {
                category.HasMoreItems = false;
            }
        }
        finally
        {
            category.IsLoadingMore = false;
        }
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
