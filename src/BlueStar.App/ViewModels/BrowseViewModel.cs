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
public partial class BrowseViewModel : ObservableObject
{
    private readonly IDepotBoxApiClient _apiClient;
    private readonly IInstanceManager _instanceManager;
    private readonly IDepotBoxArchiveParser _archiveParser;
    private readonly IMetadataProvider? _metadataProvider;
    private readonly IEngineDetector _engineDetector;
    private readonly INotificationService? _notificationService;
    private readonly ICommunityStatsService? _statsService;
    private readonly ILogger<BrowseViewModel> _logger;

    [ObservableProperty]
    private string _searchQuery = string.Empty;

    [ObservableProperty]
    private ObservableCollection<SearchResult> _results = [];

    [ObservableProperty]
    private ObservableCollection<SearchResult> _filteredResults = [];

    [ObservableProperty]
    private ObservableCollection<CatalogCategory> _categories = [];

    [ObservableProperty]
    private ObservableCollection<string> _trendingSuggestionChips = [];

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
        INotificationService? notificationService = null)
    {
        _apiClient = apiClient;
        _instanceManager = instanceManager;
        _archiveParser = archiveParser;
        _engineDetector = engineDetector;
        _logger = logger;
        _statsService = statsService;
        _metadataProvider = metadataProvider;
        _notificationService = notificationService;

        _ = LoadCategoryFeedsAsync();
    }

    public Action<string>? OnScrollToCategoryRequested;

    public void ExpandCategory(string categoryId)
    {
        HasSearched = false;
        SearchQuery = string.Empty;

        foreach (var cat in Categories)
        {
            cat.IsExpanded = string.Equals(cat.Id, categoryId, StringComparison.OrdinalIgnoreCase);
        }

        OnScrollToCategoryRequested?.Invoke(categoryId);
    }

    [RelayCommand]
    public void ToggleCategoryExpand(CatalogCategory category)
    {
        if (category == null) return;
        category.IsExpanded = !category.IsExpanded;
    }

    public async Task LoadCategoryFeedsAsync()
    {
        var catTrending = new CatalogCategory("bluestar_trending_7d", "Trending on BlueStar", "Más agregados a instancias en los últimos 7 días", "IconFlame", "#3B82F6", "7 DÍAS");
        var catMostPlayed = new CatalogCategory("bluestar_most_played_alltime", "Most Added in BlueStar", "Títulos con más instancias creadas históricamente", "IconTrophy", "#8B5CF6", "GLOBAL");
        var catSteamDbMostPlayed = new CatalogCategory("steamdb_most_played", "Steam: Most Played", "Top jugadores concurrentes en tiempo real", "IconUsers", "#10B981", "STEAM");
        var catSteamDbTrending = new CatalogCategory("steamdb_trending", "Steam: Trending Games", "Títulos con mayor crecimiento de actividad reciente", "IconTrending", "#F59E0B", "STEAM");
        var catSteamDbTopSellers = new CatalogCategory("steamdb_top_sellers", "Steam: Top Sellers & Popular", "Los lanzamientos y ofertas más vendidos a nivel global", "IconTag", "#EC4899", "STEAM");
        var catSteamDbTopRated = new CatalogCategory("steamdb_top_rated", "Steam: Top Rated & Anticipated", "Mejor calificados por la crítica y jugadores", "IconStar", "#6366F1", "STEAM");
        var catDepotBoxNew = new CatalogCategory("depotbox_new_games", "New Games in DepotBox", "Paquetes recién agregados vía DepotBox Webhook", "IconSparkles", "#06B6D4", "DEPOTBOX");
        var catDepotBoxUpdated = new CatalogCategory("depotbox_updated_games", "Updated Games in DepotBox", "Actualizaciones recientes de manifiestos y builds", "IconRefresh", "#14B8A6", "DEPOTBOX");

        Categories = new ObservableCollection<CatalogCategory>
        {
            catTrending,
            catMostPlayed,
            catSteamDbMostPlayed,
            catSteamDbTrending,
            catSteamDbTopSellers,
            catSteamDbTopRated,
            catDepotBoxNew,
            catDepotBoxUpdated
        };

        TrendingSuggestionChips = new ObservableCollection<string>
        {
            "Cyberpunk 2077", "ELDEN RING", "Baldur's Gate 3", "Black Myth: Wukong", "HELLDIVERS 2", "Palworld", "Manor Lords", "Hades II"
        };

        if (_statsService == null)
        {
            foreach (var c in Categories) c.IsLoading = false;
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                var trendingItems = await _statsService.GetTrendingBlueStarAsync().ConfigureAwait(false);
                catTrending.Items = new ObservableCollection<SearchResult>(trendingItems);
                catTrending.IsLoading = false;

                if (trendingItems.Count > 0)
                {
                    var chips = trendingItems.Take(8).Select(t => t.Name).Where(n => !string.IsNullOrWhiteSpace(n)).ToList();
                    App.Current.Dispatcher.Invoke(() =>
                    {
                        TrendingSuggestionChips = new ObservableCollection<string>(chips);
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
                catMostPlayed.Items = new ObservableCollection<SearchResult>(items);
                catMostPlayed.IsLoading = false;
            }
            catch { catMostPlayed.IsLoading = false; }
        });

        _ = Task.Run(async () =>
        {
            try
            {
                var items = await _statsService.GetSteamDbListAsync("most_played").ConfigureAwait(false);
                catSteamDbMostPlayed.Items = new ObservableCollection<SearchResult>(items);
                catSteamDbMostPlayed.IsLoading = false;
            }
            catch { catSteamDbMostPlayed.IsLoading = false; }
        });

        _ = Task.Run(async () =>
        {
            try
            {
                var items = await _statsService.GetSteamDbListAsync("trending").ConfigureAwait(false);
                catSteamDbTrending.Items = new ObservableCollection<SearchResult>(items);
                catSteamDbTrending.IsLoading = false;
            }
            catch { catSteamDbTrending.IsLoading = false; }
        });

        _ = Task.Run(async () =>
        {
            try
            {
                var items = await _statsService.GetSteamDbListAsync("top_sellers").ConfigureAwait(false);
                catSteamDbTopSellers.Items = new ObservableCollection<SearchResult>(items);
                catSteamDbTopSellers.IsLoading = false;
            }
            catch { catSteamDbTopSellers.IsLoading = false; }
        });

        _ = Task.Run(async () =>
        {
            try
            {
                var items = await _statsService.GetSteamDbListAsync("top_rated").ConfigureAwait(false);
                catSteamDbTopRated.Items = new ObservableCollection<SearchResult>(items);
                catSteamDbTopRated.IsLoading = false;
            }
            catch { catSteamDbTopRated.IsLoading = false; }
        });

        _ = Task.Run(async () =>
        {
            try
            {
                var items = await _statsService.GetDepotBoxFeedAsync("added").ConfigureAwait(false);
                catDepotBoxNew.Items = new ObservableCollection<SearchResult>(items);
                catDepotBoxNew.IsLoading = false;
            }
            catch { catDepotBoxNew.IsLoading = false; }
        });

        _ = Task.Run(async () =>
        {
            try
            {
                var items = await _statsService.GetDepotBoxFeedAsync("updated").ConfigureAwait(false);
                catDepotBoxUpdated.Items = new ObservableCollection<SearchResult>(items);
                catDepotBoxUpdated.IsLoading = false;
            }
            catch { catDepotBoxUpdated.IsLoading = false; }
        });
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

        IEnumerable<SearchResult> filtered = Results;

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
            _logger.LogInformation("Searching DepotBox for: {Query}", SearchQuery);
            var searchResults = await _apiClient.SearchGamesAsync(SearchQuery, CancellationToken.None).ConfigureAwait(true);
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

    private async Task EnrichResultsAsync(IEnumerable<SearchResult> results)
    {
        var items = results.ToList();
        await Parallel.ForEachAsync(items, new ParallelOptions { MaxDegreeOfParallelism = 4 }, async (result, ct) =>
        {
            try
            {
                // 1. Enrich from Steam Store / Web API (DLC count, OS compatibility, high-res artwork, release/update date, app type)
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
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Background metadata enrichment error for AppId={AppId}", result.AppId);
            }
        }).ConfigureAwait(false);
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

        _notificationService?.ShowInfo("Preparando instancia", $"Obteniendo manifiestos y preparando {result.Name}...");

        try
        {
            _logger.LogInformation("Adding game {Name} ({AppId}) to library", result.Name, result.AppId);

            var defaultRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "BlueStar", "games");
            var installPath = PathHelper.EnsureGameSubfolder(defaultRoot, result.Name);

            // Fetch rich Steam metadata if available
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

            // Check if instance with this AppId already exists
            var existing = await _instanceManager.GetAllAsync(CancellationToken.None).ConfigureAwait(true);
            var found = existing.FirstOrDefault(i => i.AppId == result.AppId && result.AppId > 0);

            if (found != null)
            {
                _notificationService?.ShowInfo("Existing Instance", $"{result.Name} is already in your library.");
                result.IsCreating = false;
                result.CreationStatus = null;
                OnManageInstanceRequested?.Invoke(found);
                return;
            }

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

            GameInstance newInstance;
            if (archive != null && archive.Games.Count > 0 && !string.IsNullOrWhiteSpace(archivePath))
            {
                var mainGame = archive.Games.FirstOrDefault(g => !g.IsDlc) ?? archive.Games[0];
                var cleanMainName = CleanName(mainGame.Name) ?? result.Name;

                newInstance = new GameInstance
                {
                    Name = cleanMainName,
                    AppId = result.AppId,
                    InstallPath = PathHelper.EnsureGameSubfolder(defaultRoot, cleanMainName),
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
            }
            else
            {
                newInstance = new GameInstance
                {
                    Name = result.Name,
                    AppId = result.AppId,
                    InstallPath = installPath,
                    Status = InstanceStatus.NotInstalled,
                    Metadata = meta,
                    Engine = engine
                };
            }

            var created = await _instanceManager.CreateAsync(newInstance, CancellationToken.None).ConfigureAwait(true);
            if (!string.IsNullOrWhiteSpace(archivePath) && File.Exists(archivePath))
            {
                ExtractManifestsToInstanceStorage(archivePath, created.Id);
            }

            _logger.LogInformation("Created new instance: {Id}", created.Id);
            _notificationService?.ShowSuccess("Instance Created", $"Configured {newInstance.Name} with {newInstance.Depots.Count} available depot(s).");
            _ = _statsService?.ReportInstanceAddedAsync(created.AppId, created.Name);

            result.CreationStatus = "Instance ready";
            OnManageInstanceRequested?.Invoke(created);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create instance for {Name}", result.Name);
            ErrorMessage = $"Failed to add game: {ex.Message}";
            _notificationService?.ShowError("Failed to Create Instance", $"Could not add {result.Name}: {ex.Message}");
        }
        finally
        {
            result.IsCreating = false;
            result.CreationStatus = null;
            IsCreatingInstance = false;
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
}
