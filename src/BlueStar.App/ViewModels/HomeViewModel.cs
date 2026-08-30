using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using BlueStar.Core.Helpers;
using BlueStar.Core.Interfaces;
using BlueStar.Core.Models;
using BlueStar.Infrastructure.Downloader;
using BlueStar.Infrastructure.Steam;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace BlueStar.App.ViewModels;

/// <summary>
/// Unified ViewModel for the Home Dashboard Hub (recent/active instances, direct catalog discovery & search, 1-click instance creation, active downloads, stats).
/// </summary>
public partial class HomeViewModel : ObservableObject
{
    private readonly IInstanceManager _instanceManager;
    private readonly DownloadQueueManager _downloadQueueManager;
    private readonly IGameLauncher _gameLauncher;
    private readonly IDepotBoxApiClient _apiClient;
    private readonly IDepotBoxArchiveParser _archiveParser;
    private readonly IEngineDetector _engineDetector;
    private readonly ICommunityStatsService? _statsService;
    private readonly IMetadataProvider? _metadataProvider;
    private readonly INotificationService? _notificationService;
    private readonly BlueStar.Infrastructure.Storage.AppSettingsService? _settingsService;
    private readonly ILogger<HomeViewModel> _logger;

    [ObservableProperty]
    private string _greetingText = "Welcome to BlueStar";

    [ObservableProperty]
    private ObservableCollection<GameInstance> _recentInstances = [];

    [ObservableProperty]
    private ObservableCollection<CatalogCategory> _categories = [];

    [ObservableProperty]
    private ObservableCollection<TrendingChipItem> _trendingSuggestionChips = [];

    [ObservableProperty]
    private DownloadJobItem? _currentActiveDownload;

    [ObservableProperty]
    private int _totalInstancesCount;

    [ObservableProperty]
    private int _readyInstancesCount;

    [ObservableProperty]
    private bool _isLoading;

    // ── Header "+" Import Menu ──
    [ObservableProperty]
    private bool _isImportMenuOpen;

    // ── 1. Steam Import Modal ──
    [ObservableProperty]
    private bool _isSteamImportModalOpen;

    [ObservableProperty]
    private bool _isScanningSteam;

    [ObservableProperty]
    private ObservableCollection<InstalledSteamGame> _installedSteamGames = [];

    [ObservableProperty]
    private string _steamSearchQuery = string.Empty;

    [ObservableProperty]
    private string? _steamScanError;

    [ObservableProperty]
    private bool _isDuplicateSteamPromptOpen;

    [ObservableProperty]
    private InstalledSteamGame? _duplicateSteamGame;

    [ObservableProperty]
    private ObservableCollection<uint> _importedSteamAppIds = [];

    // ── 2. Depot ZIP Import Modal ──
    private DepotBoxArchive? _pendingArchive;

    [ObservableProperty]
    private bool _isZipImportModalOpen;

    [ObservableProperty]
    private bool _isParsingZip;

    [ObservableProperty]
    private string? _pendingZipPath;

    [ObservableProperty]
    private string _zipGameName = string.Empty;

    [ObservableProperty]
    private uint _zipAppId;

    [ObservableProperty]
    private string _zipManifestDateFormatted = "Unknown";

    [ObservableProperty]
    private bool _isDepotsListExpanded;

    [ObservableProperty]
    private bool _isDlcsListExpanded;

    [ObservableProperty]
    private ObservableCollection<DepotPreviewItem> _previewDepots = [];

    [ObservableProperty]
    private ObservableCollection<DlcPreviewItem> _previewDlcs = [];

    [ObservableProperty]
    private string _zipInstallPath = string.Empty;

    [ObservableProperty]
    private string? _zipHeaderImageUrl;

    [ObservableProperty]
    private ObservableCollection<DepotBoxGame> _detectedZipGames = [];

    [ObservableProperty]
    private string? _zipErrorMessage;

    // ── 3. Folder Import Modal ──
    [ObservableProperty]
    private bool _isFolderImportModalOpen;

    [ObservableProperty]
    private bool _isScanningFolder;

    [ObservableProperty]
    private string _folderPath = string.Empty;

    [ObservableProperty]
    private string _folderGameName = string.Empty;

    [ObservableProperty]
    private uint _folderAppId;

    [ObservableProperty]
    private string _folderExecutablePath = string.Empty;

    [ObservableProperty]
    private EngineInfo? _folderEngine;

    [ObservableProperty]
    private string? _folderHeaderImageUrl;

    [ObservableProperty]
    private string? _folderErrorMessage;

    // ── 4. Steam Search Sub-Modal for Folder Import ──
    [ObservableProperty]
    private bool _isSteamSearchModalOpen;

    [ObservableProperty]
    private string _steamSearchStoreQuery = string.Empty;

    [ObservableProperty]
    private bool _isSearchingSteamStore;

    [ObservableProperty]
    private ObservableCollection<SteamStoreSearchItem> _steamStoreSearchResults = [];

    // ── Unified Catalog Exploration & Quick Add in Home ──
    [ObservableProperty]
    private string _catalogSearchQuery = string.Empty;

    [ObservableProperty]
    private ObservableCollection<SearchResult> _discoveredGames = [];

    [ObservableProperty]
    private bool _isSearchingCatalog;

    [ObservableProperty]
    private bool _hasSearchedCatalog;

    [ObservableProperty]
    private string? _catalogErrorMessage;

    public Action<string>? OnNavigateRequested { get; set; }
    public Action<string>? OnNavigateToCategoryRequested { get; set; }
    public Action<GameInstance>? OnManageInstanceRequested { get; set; }
    public Action<GameInstance, bool>? OnManageInstanceRequestedWithUpdate { get; set; }

    public HomeViewModel(
        IInstanceManager instanceManager,
        DownloadQueueManager downloadQueueManager,
        IGameLauncher gameLauncher,
        IDepotBoxApiClient apiClient,
        IDepotBoxArchiveParser archiveParser,
        IEngineDetector engineDetector,
        ILogger<HomeViewModel> logger,
        ICommunityStatsService? statsService = null,
        IMetadataProvider? metadataProvider = null,
        INotificationService? notificationService = null,
        BlueStar.Infrastructure.Storage.AppSettingsService? settingsService = null)
    {
        _instanceManager = instanceManager;
        _downloadQueueManager = downloadQueueManager;
        _gameLauncher = gameLauncher;
        _apiClient = apiClient;
        _archiveParser = archiveParser;
        _engineDetector = engineDetector;
        _logger = logger;
        _statsService = statsService;
        _metadataProvider = metadataProvider;
        _notificationService = notificationService;
        _settingsService = settingsService;

        if (_settingsService != null)
        {
            _settingsService.SettingsChanged += (_, _) =>
            {
                App.Current?.Dispatcher?.Invoke(() =>
                {
                    _ = LoadCategoryFeedsAsync();
                });
            };
        }

        SetTimeBasedGreeting();
        _downloadQueueManager.Queue.CollectionChanged += (_, _) => UpdateActiveDownload();

        _ = LoadDashboardDataAsync();
        _ = LoadCategoryFeedsAsync();
    }

    [RelayCommand]
    public void ViewMoreCategory(CatalogCategory category)
    {
        if (category == null) return;
        OnNavigateToCategoryRequested?.Invoke(category.Id);
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

        // 1. Initialize category shells with IsLoading=true for immediate skeleton rendering
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

        // Fallback default suggestions if empty
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

                // Update search suggestion chips dynamically from top trending games
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


    private void SetTimeBasedGreeting()
    {
        var hour = DateTime.Now.Hour;
        GreetingText = hour switch
        {
            >= 5 and < 12 => "Good morning",
            >= 12 and < 18 => "Good afternoon",
            _ => "Good evening"
        };
    }

    [RelayCommand]
    public void ToggleImportMenu()
    {
        IsImportMenuOpen = !IsImportMenuOpen;
    }

    [RelayCommand]
    public void CloseImportMenu()
    {
        IsImportMenuOpen = false;
    }

    [RelayCommand]
    public async Task LoadDashboardDataAsync()
    {
        IsLoading = true;
        try
        {
            var instances = await _instanceManager.GetAllAsync(CancellationToken.None).ConfigureAwait(true);
            TotalInstancesCount = instances.Count;
            ReadyInstancesCount = instances.Count(i => i.Status is InstanceStatus.Ready or InstanceStatus.Running);

            // Order by LastPlayedAt descending or CreatedAt
            var sorted = instances
                .OrderByDescending(i => i.LastPlayedAt ?? DateTimeOffset.MinValue)
                .ThenByDescending(i => i.CreatedAt)
                .Take(6)
                .ToList();

            RecentInstances = new ObservableCollection<GameInstance>(sorted);
            UpdateActiveDownload();

            if (_statsService != null && instances.Count > 0)
            {
                _ = _statsService.SyncInstancesAsync(instances, CancellationToken.None);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load dashboard data");
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    public void ClearCatalogSearch()
    {
        CatalogSearchQuery = string.Empty;
        DiscoveredGames.Clear();
        HasSearchedCatalog = false;
        CatalogErrorMessage = null;
    }

    [RelayCommand]
    public async Task QuickSearchCatalogAsync(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return;
        CatalogSearchQuery = query;
        await SearchCatalogAsync();
    }

    [RelayCommand]
    public async Task SearchCatalogAsync()
    {
        if (string.IsNullOrWhiteSpace(CatalogSearchQuery))
        {
            ClearCatalogSearch();
            return;
        }

        IsSearchingCatalog = true;
        CatalogErrorMessage = null;
        HasSearchedCatalog = true;
        DiscoveredGames.Clear();

        try
        {
            _logger.LogInformation("Home searching DepotBox for: {Query}", CatalogSearchQuery);
            var searchResults = await _apiClient.SearchGamesAsync(CatalogSearchQuery, CancellationToken.None).ConfigureAwait(true);
            var filtered = FilterBySettings(searchResults).Take(12).ToList();
            DiscoveredGames = new ObservableCollection<SearchResult>(filtered);
            _ = EnrichResultsAsync(filtered, null, DiscoveredGames);
        }
        catch (UnauthorizedAccessException)
        {
            CatalogErrorMessage = "Configure your DepotBox API key in Settings to search the full catalog.";
        }
        catch (HttpRequestException ex)
        {
            CatalogErrorMessage = $"Connection error: {ex.Message}";
        }
        catch (Exception ex)
        {
            CatalogErrorMessage = $"Search error: {ex.Message}";
        }
        finally
        {
            IsSearchingCatalog = false;
        }
    }

    private async Task EnrichResultsAsync(IEnumerable<SearchResult> results, CatalogCategory? parentCategory = null, ObservableCollection<SearchResult>? targetCollection = null)
    {
        var items = results.ToList();
        var allowNsfw = _settingsService?.ShowNsfwContent ?? false;
        var allowDrm = _settingsService?.ShowDrmContent ?? true;

        await Parallel.ForEachAsync(items, new ParallelOptions { MaxDegreeOfParallelism = 1 }, async (result, ct) =>

        {
            try
            {
                if (_metadataProvider != null && result.AppId > 0)
                {
                    await _metadataProvider.EnrichSearchResultAsync(result, ct).ConfigureAwait(false);
                }

                // If after enrichment, this game is NSFW or DRM and is part of a category carousel, replenish it
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
    /// Direct 1-click instance creation directly from Home Discovery Hub.
    /// </summary>
    [RelayCommand]
    public async Task AddCatalogGameAsync(SearchResult result)
    {
        if (result == null) return;

        result.IsCreating = true;
        result.CreationStatus = "Preparing...";
        CatalogErrorMessage = null;

        _notificationService?.ShowInfo("Preparing Instance", $"Fetching manifests for {result.Name}...");

        try
        {
            var defaultRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "BlueStar", "games");
            var installPath = PathHelper.EnsureGameSubfolder(defaultRoot, result.Name);

            GameMetadata? meta = null;
            if (_metadataProvider != null)
            {
                try
                {
                    meta = await _metadataProvider.GetMetadataAsync(result.AppId, CancellationToken.None).ConfigureAwait(true);
                }
                catch { }
            }

            var engine = await _engineDetector.DetectEngineAsync(installPath, CancellationToken.None).ConfigureAwait(true);

            // Attempt to download and parse DepotBox archive for this game
            string? archivePath = null;
            DepotBoxArchive? archive = null;
            var archivesDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "BlueStar", "archives");
            Directory.CreateDirectory(archivesDir);

            result.CreationStatus = "Downloading depots...";

            try
            {
                archivePath = await _apiClient.DownloadArchiveAsync(result.AppId, archivesDir, null, CancellationToken.None).ConfigureAwait(true);
                if (File.Exists(archivePath))
                {
                    archive = await _archiveParser.ParseAsync(archivePath, CancellationToken.None).ConfigureAwait(true);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not pre-download DepotBox archive for {AppId}", result.AppId);
            }

            // Verify if depots exist in DepotBox for this game
            bool hasDepots = archive != null && archive.Games.Count > 0 && archive.Games.Any(g => g.Depots.Count > 0);
            if (!hasDepots)
            {
                _logger.LogWarning("No depots found in DepotBox for {Name} ({AppId})", result.Name, result.AppId);
                CatalogErrorMessage = $"No depots found for \"{result.Name}\" (AppID: {result.AppId}) in DepotBox.";
                _notificationService?.ShowError("Depots Not Found", $"No depots were found for \"{result.Name}\" (AppID: {result.AppId}) in DepotBox.");
                return;
            }

            // Generate unique instance name and non-colliding installation path
            var allExisting = await _instanceManager.GetAllAsync(CancellationToken.None).ConfigureAwait(true);
            var existingNames = allExisting.Select(i => i.Name).ToList();
            var existingPaths = allExisting.Select(i => i.InstallPath).Where(p => !string.IsNullOrWhiteSpace(p)).ToList();

            var mainGame = archive!.Games.FirstOrDefault(g => !g.IsDlc) ?? archive.Games[0];
            var rawName = CleanName(mainGame.Name) ?? result.Name;
            var uniqueName = PathHelper.GenerateUniqueInstanceName(existingNames, rawName);
            var uniqueInstallPath = PathHelper.GenerateUniqueInstallPath(defaultRoot, uniqueName, existingPaths);

            var newInstance = new GameInstance
            {
                Name = uniqueName,
                AppId = result.AppId,
                InstallPath = uniqueInstallPath,
                SourceArchivePath = archivePath,
                Status = InstanceStatus.NotInstalled,
                Metadata = meta,
                Engine = engine,
                Depots = archive.Games.SelectMany(g => g.Depots.Select(d => new DepotInfo
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
                })).DistinctBy(d => d.DepotId).ToList().AsReadOnly(),
                Dlcs = archive.Games.Where(g => g.IsDlc).Select(dlc => new DlcInfo
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
                }).ToList().AsReadOnly()
            };

            var created = await _instanceManager.CreateAsync(newInstance, CancellationToken.None).ConfigureAwait(true);
            if (!string.IsNullOrWhiteSpace(archivePath) && File.Exists(archivePath))
            {
                ExtractManifestsToInstanceStorage(archivePath, created.Id);
            }

            _logger.LogInformation("Created new instance from Home: {Id} ({Name})", created.Id, created.Name);
            _notificationService?.ShowSuccess("Instance Created", $"Configured {newInstance.Name} with {newInstance.Depots.Count} depot(s).");
            _ = _statsService?.ReportInstanceAddedAsync(created.AppId, created.Name);

            result.CreationStatus = "Ready";
            await LoadDashboardDataAsync();
            OnManageInstanceRequested?.Invoke(created);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create instance for {Name}", result.Name);
            CatalogErrorMessage = $"Error adding game: {ex.Message}";
            _notificationService?.ShowError("Error Creating Instance", $"Could not add {result.Name}: {ex.Message}");
        }
        finally
        {
            result.IsCreating = false;
            result.CreationStatus = null;
        }
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

    // ═══════════════════════════════════════════════════════════════
    // MODAL 1: STEAM GAMES IMPORT
    // ═══════════════════════════════════════════════════════════════

    private readonly List<InstalledSteamGame> _allDetectedSteamGames = [];

    partial void OnSteamSearchQueryChanged(string value)
    {
        ApplySteamFilter();
    }

    private void ApplySteamFilter()
    {
        InstalledSteamGames.Clear();
        var q = SteamSearchQuery?.Trim() ?? string.Empty;
        var filtered = string.IsNullOrWhiteSpace(q)
            ? _allDetectedSteamGames
            : _allDetectedSteamGames.Where(g => g.Name.Contains(q, StringComparison.OrdinalIgnoreCase) || g.AppId.ToString().Contains(q));

        foreach (var g in filtered)
            InstalledSteamGames.Add(g);
    }

    [RelayCommand]
    public async Task OpenSteamImportModalAsync()
    {
        IsImportMenuOpen = false;
        IsSteamImportModalOpen = true;
        IsScanningSteam = true;
        SteamScanError = null;
        SteamSearchQuery = string.Empty;
        InstalledSteamGames.Clear();
        _allDetectedSteamGames.Clear();

        try
        {
            var games = await SteamLibraryScanner.ScanInstalledGamesAsync(CancellationToken.None).ConfigureAwait(true);
            if (games.Count == 0)
            {
                SteamScanError = "No installed Steam games detected in local library folders.";
            }
            else
            {
                _allDetectedSteamGames.AddRange(games);
                ApplySteamFilter();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to scan Steam games");
            SteamScanError = $"Error scanning Steam library: {ex.Message}";
        }
        finally
        {
            IsScanningSteam = false;
        }
    }

    [RelayCommand]
    public void CloseSteamImportModal()
    {
        IsSteamImportModalOpen = false;
    }

    [RelayCommand]
    public async Task ImportSteamGameAsync(InstalledSteamGame? steamGame)
    {
        if (steamGame == null) return;

        var instances = await _instanceManager.GetAllAsync(CancellationToken.None).ConfigureAwait(true);
        if (instances.Any(i => i.AppId == steamGame.AppId))
        {
            DuplicateSteamGame = steamGame;
            IsDuplicateSteamPromptOpen = true;
            return;
        }

        await ExecuteImportSteamGameInternalAsync(steamGame);
    }

    [RelayCommand]
    public async Task ConfirmDuplicateSteamGameAsync()
    {
        var targetGame = DuplicateSteamGame;
        IsDuplicateSteamPromptOpen = false;
        DuplicateSteamGame = null;
        if (targetGame != null)
        {
            await ExecuteImportSteamGameInternalAsync(targetGame);
        }
    }

    [RelayCommand]
    public void CancelDuplicateSteamGame()
    {
        IsDuplicateSteamPromptOpen = false;
        DuplicateSteamGame = null;
    }

    private async Task ExecuteImportSteamGameInternalAsync(InstalledSteamGame steamGame)
    {
        IsLoading = true;
        try
        {
            var engine = await _engineDetector.DetectEngineAsync(steamGame.FullPath, CancellationToken.None).ConfigureAwait(true);
            var exe = _engineDetector.FindPrimaryExecutable(steamGame.FullPath, steamGame.Name);

            GameMetadata? meta = null;
            if (_metadataProvider != null && steamGame.AppId > 0)
            {
                try
                {
                    meta = await _metadataProvider.GetMetadataAsync(steamGame.AppId, CancellationToken.None).ConfigureAwait(true);
                }
                catch { }
            }

            var instance = new GameInstance
            {
                Name = steamGame.Name,
                AppId = steamGame.AppId,
                InstallPath = steamGame.FullPath,
                ExecutablePath = exe,
                Status = File.Exists(exe) ? InstanceStatus.Ready : InstanceStatus.NotInstalled,
                Metadata = meta,
                Engine = engine
            };

            var created = await _instanceManager.CreateAsync(instance, CancellationToken.None).ConfigureAwait(true);

            if (!ImportedSteamAppIds.Contains(steamGame.AppId))
            {
                ImportedSteamAppIds.Add(steamGame.AppId);
            }

            _notificationService?.ShowSuccess("Steam Game Imported", $"{created.Name} added to BlueStar instances.");
            IsSteamImportModalOpen = false;
            await LoadDashboardDataAsync();
            OnManageInstanceRequested?.Invoke(created);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to import Steam game: {Name}", steamGame.Name);
            _notificationService?.ShowError("Import Error", ex.Message);
        }
        finally
        {
            IsLoading = false;
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // MODAL 2: DEPOT ZIP ARCHIVE IMPORT
    // ═══════════════════════════════════════════════════════════════

    [RelayCommand]
    public void OpenZipImportModal()
    {
        IsImportMenuOpen = false;
        IsZipImportModalOpen = true;
        PendingZipPath = null;
        _pendingArchive = null;
        ZipGameName = string.Empty;
        ZipAppId = 0;
        ZipManifestDateFormatted = "Unknown";
        IsDepotsListExpanded = false;
        IsDlcsListExpanded = false;
        PreviewDepots.Clear();
        PreviewDlcs.Clear();
        ZipInstallPath = string.Empty;
        ZipHeaderImageUrl = null;
        DetectedZipGames.Clear();
        ZipErrorMessage = null;
    }

    [RelayCommand]
    public void CloseZipImportModal()
    {
        IsZipImportModalOpen = false;
        PendingZipPath = null;
        _pendingArchive = null;
        DetectedZipGames.Clear();
    }

    [RelayCommand]
    public void ToggleDepotsList() => IsDepotsListExpanded = !IsDepotsListExpanded;

    [RelayCommand]
    public void ToggleDlcsList() => IsDlcsListExpanded = !IsDlcsListExpanded;

    [RelayCommand]
    public async Task BrowseZipFileAsync()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select DepotBox ZIP Archive",
            Filter = "DepotBox ZIP Archives (*.zip)|*.zip|All Files (*.*)|*.*",
            Multiselect = false
        };

        if (dialog.ShowDialog() != true) return;
        await LoadZipFileAsync(dialog.FileName);
    }

    public async Task LoadZipFileAsync(string zipPath)
    {
        if (string.IsNullOrWhiteSpace(zipPath) || !File.Exists(zipPath)) return;

        IsParsingZip = true;
        ZipErrorMessage = null;
        DetectedZipGames.Clear();

        try
        {
            var archive = await _archiveParser.ParseAsync(zipPath, CancellationToken.None).ConfigureAwait(true);
            if (archive.Games.Count == 0)
            {
                ZipErrorMessage = "No games found inside the selected ZIP archive.";
                return;
            }

            PendingZipPath = zipPath;
            _pendingArchive = archive;

            foreach (var g in archive.Games)
                DetectedZipGames.Add(g);

            var mainGame = archive.Games.FirstOrDefault(g => !g.IsDlc) ?? archive.Games[0];
            ZipGameName = CleanName(mainGame.Name) ?? $"App {mainGame.AppId}";
            ZipAppId = mainGame.AppId;

            // Extract manifest date
            DateTimeOffset? manifestDate = null;
            try
            {
                using var zipStream = new FileStream(zipPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                using var zip = new ZipArchive(zipStream, ZipArchiveMode.Read);
                var manifestEntry = zip.Entries.FirstOrDefault(e => e.Name.EndsWith(".manifest", StringComparison.OrdinalIgnoreCase));
                if (manifestEntry != null && manifestEntry.LastWriteTime.Year >= 2005)
                {
                    manifestDate = manifestEntry.LastWriteTime;
                }
                else if (zip.Entries.Count > 0)
                {
                    manifestDate = zip.Entries.Max(e => e.LastWriteTime);
                }
            }
            catch { }

            ZipManifestDateFormatted = manifestDate.HasValue
                ? $"{manifestDate.Value.LocalDateTime:d MMM yyyy}"
                : "Latest Build";

            var allDepots = archive.Games
                .SelectMany(g => g.Depots.Select(d => new DepotPreviewItem(d.DepotId, g.Name ?? $"Depot {d.DepotId}", d.ManifestFileName)))
                .ToList();
            PreviewDepots = new ObservableCollection<DepotPreviewItem>(allDepots);

            var allDlcs = archive.Games
                .Where(g => g.IsDlc)
                .Select(g => new DlcPreviewItem(g.AppId, CleanName(g.Name) ?? $"DLC {g.AppId}"))
                .ToList();
            PreviewDlcs = new ObservableCollection<DlcPreviewItem>(allDlcs);

            var defaultRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "BlueStar", "games");
            ZipInstallPath = PathHelper.EnsureGameSubfolder(defaultRoot, ZipGameName);
            ZipHeaderImageUrl = $"https://cdn.cloudflare.steamstatic.com/steam/apps/{ZipAppId}/header.jpg";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to parse ZIP archive");
            ZipErrorMessage = $"Error reading ZIP archive: {ex.Message}";
        }
        finally
        {
            IsParsingZip = false;
        }
    }

    [RelayCommand]
    public async Task ConfirmZipImportAsync()
    {
        if (_pendingArchive == null || string.IsNullOrWhiteSpace(PendingZipPath))
        {
            ZipErrorMessage = "Please select a valid ZIP archive first.";
            return;
        }

        IsLoading = true;
        try
        {
            var mainGame = _pendingArchive.Games.FirstOrDefault(g => !g.IsDlc) ?? _pendingArchive.Games[0];
            var finalName = string.IsNullOrWhiteSpace(ZipGameName) ? (CleanName(mainGame.Name) ?? $"App {mainGame.AppId}") : ZipGameName.Trim();
            var installPath = string.IsNullOrWhiteSpace(ZipInstallPath)
                ? PathHelper.EnsureGameSubfolder(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BlueStar", "games"), finalName)
                : ZipInstallPath;

            var engine = await _engineDetector.DetectEngineAsync(installPath, CancellationToken.None).ConfigureAwait(true);

            GameMetadata? meta = null;
            if (_metadataProvider != null && ZipAppId > 0)
            {
                try
                {
                    meta = await _metadataProvider.GetMetadataAsync(ZipAppId, CancellationToken.None).ConfigureAwait(true);
                }
                catch { }
            }

            var newInstance = new GameInstance
            {
                Name = finalName,
                AppId = ZipAppId,
                InstallPath = installPath,
                SourceArchivePath = PendingZipPath,
                Status = InstanceStatus.NotInstalled,
                Metadata = meta,
                Engine = engine,
                Depots = _pendingArchive.Games.SelectMany(g => g.Depots.Select(d => new DepotInfo
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
                })).DistinctBy(d => d.DepotId).ToList().AsReadOnly(),
                Dlcs = _pendingArchive.Games.Where(g => g.IsDlc).Select(dlc => new DlcInfo
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
                }).ToList().AsReadOnly()
            };

            var created = await _instanceManager.CreateAsync(newInstance, CancellationToken.None).ConfigureAwait(true);
            ExtractManifestsToInstanceStorage(PendingZipPath, created.Id);

            _notificationService?.ShowSuccess("Depot Instance Imported", $"{created.Name} is ready to download and configure.");
            IsZipImportModalOpen = false;
            await LoadDashboardDataAsync();
            OnManageInstanceRequested?.Invoke(created);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to import ZIP archive");
            ZipErrorMessage = ex.Message;
        }
        finally
        {
            IsLoading = false;
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // MODAL 3: EXISTING FOLDER IMPORT
    // ═══════════════════════════════════════════════════════════════

    [RelayCommand]
    public void OpenFolderImportModal()
    {
        IsImportMenuOpen = false;
        IsFolderImportModalOpen = true;
        FolderPath = string.Empty;
        FolderGameName = string.Empty;
        FolderAppId = 0;
        FolderExecutablePath = string.Empty;
        FolderEngine = null;
        FolderHeaderImageUrl = null;
        FolderErrorMessage = null;
    }

    [RelayCommand]
    public void CloseFolderImportModal()
    {
        IsFolderImportModalOpen = false;
    }

    [RelayCommand]
    public async Task BrowseFolderAsync()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Select Game Folder"
        };

        if (dialog.ShowDialog() != true) return;
        await LoadFolderAsync(dialog.FolderName);
    }

    [RelayCommand]
    public void BrowseFolderExecutable()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select Game Executable",
            Filter = "Executable Files (*.exe)|*.exe|All Files (*.*)|*.*",
            InitialDirectory = Directory.Exists(FolderPath) ? FolderPath : null
        };

        if (dialog.ShowDialog() == true)
        {
            FolderExecutablePath = dialog.FileName;
        }
    }

    // ── Steam Search Sub-Modal for Folder ──

    [RelayCommand]
    public async Task OpenSteamSearchModalAsync()
    {
        SteamSearchStoreQuery = !string.IsNullOrWhiteSpace(FolderGameName) ? FolderGameName : "";
        IsSteamSearchModalOpen = true;
        if (!string.IsNullOrWhiteSpace(SteamSearchStoreQuery))
        {
            await SearchSteamStoreAsync();
        }
    }

    [RelayCommand]
    public void CloseSteamSearchModal()
    {
        IsSteamSearchModalOpen = false;
    }

    [RelayCommand]
    public async Task SearchSteamStoreAsync()
    {
        if (string.IsNullOrWhiteSpace(SteamSearchStoreQuery) || _metadataProvider == null)
            return;

        IsSearchingSteamStore = true;
        try
        {
            var results = await _metadataProvider.SearchStoreAsync(SteamSearchStoreQuery, CancellationToken.None).ConfigureAwait(true);
            SteamStoreSearchResults = new ObservableCollection<SteamStoreSearchItem>(results);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to search Steam Store");
        }
        finally
        {
            IsSearchingSteamStore = false;
        }
    }

    [RelayCommand]
    public void SelectSteamSearchResult(SteamStoreSearchItem? item)
    {
        if (item == null) return;
        FolderAppId = item.AppId;
        FolderGameName = item.Name;
        FolderHeaderImageUrl = item.HeaderImageUrl;
        IsSteamSearchModalOpen = false;
    }

    partial void OnFolderAppIdChanged(uint value)
    {
        if (value > 0 && value != 480)
        {
            FolderHeaderImageUrl = $"https://cdn.cloudflare.steamstatic.com/steam/apps/{value}/header.jpg";
        }
        else
        {
            FolderHeaderImageUrl = null;
        }
    }

    public async Task LoadFolderAsync(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return;

        IsScanningFolder = true;
        FolderErrorMessage = null;
        FolderPath = folder;

        try
        {
            var folderName = Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            FolderGameName = folderName;

            var engine = await _engineDetector.DetectEngineAsync(folder, CancellationToken.None).ConfigureAwait(true);
            FolderEngine = engine;

            var exe = _engineDetector.FindPrimaryExecutable(folder, folderName);
            FolderExecutablePath = exe ?? string.Empty;

            var detectedAppId = DetectAppIdFromFolder(folder);
            if (detectedAppId == 0 || detectedAppId == 480)
            {
                // Auto-search Steam by folderName
                if (_metadataProvider != null)
                {
                    try
                    {
                        var searchRes = await _metadataProvider.SearchStoreAsync(folderName, CancellationToken.None).ConfigureAwait(true);
                        if (searchRes.Count > 0)
                        {
                            detectedAppId = searchRes[0].AppId;
                            if (!string.IsNullOrWhiteSpace(searchRes[0].Name))
                            {
                                FolderGameName = searchRes[0].Name;
                            }
                        }
                    }
                    catch { }
                }
            }

            FolderAppId = detectedAppId;
            FolderHeaderImageUrl = detectedAppId > 0 && detectedAppId != 480
                ? $"https://cdn.cloudflare.steamstatic.com/steam/apps/{detectedAppId}/header.jpg"
                : null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to inspect game folder");
            FolderErrorMessage = $"Error reading folder: {ex.Message}";
        }
        finally
        {
            IsScanningFolder = false;
        }
    }

    private static uint DetectAppIdFromFolder(string folderPath)
    {
        try
        {
            // 1. steam_appid.txt
            var appIdFiles = Directory.GetFiles(folderPath, "steam_appid.txt", SearchOption.AllDirectories);
            foreach (var f in appIdFiles)
            {
                var text = File.ReadAllText(f).Trim();
                if (uint.TryParse(text, out var id) && id > 0 && id != 480)
                    return id;
            }

            // 2. ReFix.ini, steam_emu.ini, etc.
            var iniFiles = Directory.GetFiles(folderPath, "*.ini", SearchOption.AllDirectories);
            foreach (var f in iniFiles)
            {
                var text = File.ReadAllText(f);
                var match = Regex.Match(text, @"(?i)^\s*(?:AppId|SteamAppId|MaskAppId)\s*=\s*(\d+)", RegexOptions.Multiline);
                if (match.Success && uint.TryParse(match.Groups[1].Value, out var id) && id > 0 && id != 480)
                    return id;
            }

            // 3. SmokeAPI.config.json, SmokeAPI.json
            var jsonFiles = Directory.GetFiles(folderPath, "*SmokeAPI*.json", SearchOption.AllDirectories);
            foreach (var f in jsonFiles)
            {
                var text = File.ReadAllText(f);
                var match = Regex.Match(text, @"(?i)""(?:appid|app_id|SteamAppId)""\s*:\s*(\d+)");
                if (match.Success && uint.TryParse(match.Groups[1].Value, out var id) && id > 0 && id != 480)
                    return id;
            }
        }
        catch { }
        return 0;
    }

    [RelayCommand]
    public async Task ConfirmFolderImportAsync()
    {
        if (string.IsNullOrWhiteSpace(FolderPath) || !Directory.Exists(FolderPath))
        {
            FolderErrorMessage = "Please select a valid game folder first.";
            return;
        }

        if (string.IsNullOrWhiteSpace(FolderGameName))
        {
            FolderErrorMessage = "Please provide a valid game name.";
            return;
        }

        IsLoading = true;
        try
        {
            GameMetadata? meta = null;
            if (_metadataProvider != null && FolderAppId > 0)
            {
                try
                {
                    meta = await _metadataProvider.GetMetadataAsync(FolderAppId, CancellationToken.None).ConfigureAwait(true);
                }
                catch { }
            }

            var newInstance = new GameInstance
            {
                Name = FolderGameName.Trim(),
                AppId = FolderAppId,
                InstallPath = FolderPath,
                ExecutablePath = File.Exists(FolderExecutablePath) ? FolderExecutablePath : null,
                Engine = FolderEngine,
                Status = File.Exists(FolderExecutablePath) ? InstanceStatus.Ready : InstanceStatus.NotInstalled,
                Metadata = meta
            };

            var created = await _instanceManager.CreateAsync(newInstance, CancellationToken.None).ConfigureAwait(true);
            _notificationService?.ShowSuccess("Folder Instance Added", $"{created.Name} configured successfully.");
            IsFolderImportModalOpen = false;
            await LoadDashboardDataAsync();
            OnManageInstanceRequested?.Invoke(created);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to confirm folder import");
            FolderErrorMessage = ex.Message;
        }
        finally
        {
            IsLoading = false;
        }
    }

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

    private void UpdateActiveDownload()
    {
        CurrentActiveDownload = _downloadQueueManager.Queue.FirstOrDefault(j => j.IsActive);
    }

    [RelayCommand]
    private void NavigateTo(string targetPage)
    {
        OnNavigateRequested?.Invoke(targetPage);
    }

    [RelayCommand]
    private void ManageInstance(GameInstance instance)
    {
        OnManageInstanceRequested?.Invoke(instance);
    }

    [RelayCommand]
    private void CheckDepotBoxUpdates(GameInstance? instance)
    {
        if (instance == null) return;
        if (instance.Origin == InstanceOrigin.Steam) return;

        if (OnManageInstanceRequestedWithUpdate != null)
        {
            OnManageInstanceRequestedWithUpdate.Invoke(instance, true);
        }
        else
        {
            OnManageInstanceRequested?.Invoke(instance);
        }
    }

    [RelayCommand]
    private async Task PlayInstanceAsync(GameInstance instance)
    {
        if (instance == null) return;
        _logger.LogInformation("Launching instance {Name} from Dashboard", instance.Name);
        var result = await _gameLauncher.LaunchAsync(instance, null, CancellationToken.None).ConfigureAwait(true);
        if (!result.Success)
        {
            _logger.LogWarning("Launch failed: {Message}", result.Message);
        }
    }
}
