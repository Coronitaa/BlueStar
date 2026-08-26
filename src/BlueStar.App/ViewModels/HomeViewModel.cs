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
    private readonly IMetadataProvider? _metadataProvider;
    private readonly INotificationService? _notificationService;
    private readonly ILogger<HomeViewModel> _logger;

    [ObservableProperty]
    private string _greetingText = "Welcome to BlueStar";

    [ObservableProperty]
    private ObservableCollection<GameInstance> _recentInstances = [];

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
    public Action<GameInstance>? OnManageInstanceRequested { get; set; }

    public HomeViewModel(
        IInstanceManager instanceManager,
        DownloadQueueManager downloadQueueManager,
        IGameLauncher gameLauncher,
        IDepotBoxApiClient apiClient,
        IDepotBoxArchiveParser archiveParser,
        IEngineDetector engineDetector,
        ILogger<HomeViewModel> logger,
        IMetadataProvider? metadataProvider = null,
        INotificationService? notificationService = null)
    {
        _instanceManager = instanceManager;
        _downloadQueueManager = downloadQueueManager;
        _gameLauncher = gameLauncher;
        _apiClient = apiClient;
        _archiveParser = archiveParser;
        _engineDetector = engineDetector;
        _logger = logger;
        _metadataProvider = metadataProvider;
        _notificationService = notificationService;

        SetTimeBasedGreeting();
        _downloadQueueManager.Queue.CollectionChanged += (_, _) => UpdateActiveDownload();

        _ = LoadDashboardDataAsync();
        _ = LoadInitialCatalogDiscoveryAsync();
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

    private async Task LoadInitialCatalogDiscoveryAsync()
    {
        // Pre-load discovery games if list is empty
        if (DiscoveredGames.Count > 0) return;

        try
        {
            IsSearchingCatalog = true;
            CatalogErrorMessage = null;
            var results = await _apiClient.SearchGamesAsync("Steam", CancellationToken.None).ConfigureAwait(true);
            if (results.Count > 0)
            {
                var subset = results.Take(6).ToList();
                DiscoveredGames = new ObservableCollection<SearchResult>(subset);
                _ = EnrichResultsAsync(subset);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Initial catalog discovery preview could not be loaded");
        }
        finally
        {
            IsSearchingCatalog = false;
        }
    }

    [RelayCommand]
    public async Task SearchCatalogAsync()
    {
        if (string.IsNullOrWhiteSpace(CatalogSearchQuery))
            return;

        IsSearchingCatalog = true;
        CatalogErrorMessage = null;
        HasSearchedCatalog = true;

        try
        {
            _logger.LogInformation("Home searching DepotBox for: {Query}", CatalogSearchQuery);
            var searchResults = await _apiClient.SearchGamesAsync(CatalogSearchQuery, CancellationToken.None).ConfigureAwait(true);
            var subset = searchResults.Take(12).ToList();
            DiscoveredGames = new ObservableCollection<SearchResult>(subset);
            _ = EnrichResultsAsync(subset);
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

    private async Task EnrichResultsAsync(IEnumerable<SearchResult> results)
    {
        var items = results.ToList();
        await Parallel.ForEachAsync(items, new ParallelOptions { MaxDegreeOfParallelism = 4 }, async (result, ct) =>
        {
            try
            {
                if (_metadataProvider != null && result.AppId > 0)
                {
                    await _metadataProvider.EnrichSearchResultAsync(result, ct).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Background metadata enrichment error for AppId={AppId}", result.AppId);
            }
        }).ConfigureAwait(false);
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

            // Check if instance already exists
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

            _logger.LogInformation("Created new instance from Home: {Id}", created.Id);
            _notificationService?.ShowSuccess("Instance Created", $"Configured {newInstance.Name} with {newInstance.Depots.Count} depot(s).");

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
