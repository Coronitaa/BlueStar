using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using BlueStar.Core.Helpers;
using BlueStar.Core.Interfaces;
using BlueStar.Core.Models;
using BlueStar.Infrastructure.Steam;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace BlueStar.App.ViewModels;

/// <summary>
/// ViewModel for the instance library view with search, compact dropdown filters, and unified expandable Add Instance modal.
/// </summary>
public partial class LibraryViewModel : ObservableObject, IDisposable
{
    private readonly IInstanceManager _instanceManager;
    private readonly IDepotBoxArchiveParser _archiveParser;
    private readonly IMetadataProvider? _metadataProvider;
    private readonly IEngineDetector _engineDetector;
    private readonly IGameLauncher _gameLauncher;
    private readonly ITagsService? _tagsService;
    private readonly IDepotBoxApiClient? _depotBoxApiClient;
    private readonly IBackgroundTaskService? _backgroundTaskService;
    private readonly ISteamStatusService? _steamStatusService;
    private readonly INotificationService? _notificationService;
    private readonly ILogger<LibraryViewModel> _logger;
    private readonly SynchronizationContext _uiContext;
    private readonly CancellationTokenSource _cts = new();
    private bool _isDisposed;

    [ObservableProperty]
    private ObservableCollection<GameInstance> _instances = [];

    [ObservableProperty]
    private ObservableCollection<InstanceCardItem> _filteredInstances = [];

    [ObservableProperty]
    private string _searchFilter = string.Empty;

    // ── Dropdown Filters State ──
    [ObservableProperty]
    private string _selectedEngineFilter = "All";

    [ObservableProperty]
    private string _selectedStatusFilter = "All";

    [ObservableProperty]
    private string _selectedSort = "Alphabetical";

    [ObservableProperty]
    private bool _isSortAscending = true;

    [ObservableProperty]
    private string _selectedGroupFilter = "All";

    [ObservableProperty]
    private bool _isSortDropdownOpen;

    [ObservableProperty]
    private bool _isGroupDropdownOpen;

    [ObservableProperty]
    private bool _isFilterDropdownOpen;

    public string SelectedSortDisplayName => SelectedSort switch
    {
        "Alphabetical" => "Name",
        "Recent" => "Last played",
        "PlayTime" => "Hours played",
        "Oldest" => "Date created",
        "AlphabeticalDesc" => "Date modified",
        "Loader" => "Loader",
        "GameVersion" => "Game version",
        _ => "Name"
    };

    public string SelectedGroupDisplayName => string.IsNullOrWhiteSpace(SelectedGroupFilter) || SelectedGroupFilter == "All"
        ? "Custom group"
        : SelectedGroupFilter;

    public bool HasActiveFilter => SelectedEngineFilter != "All" || SelectedStatusFilter != "All";

    [ObservableProperty]
    private bool _isCreateGroupOpen;

    [ObservableProperty]
    private string _newGroupName = "Group 1";

    [ObservableProperty]
    private ObservableCollection<string> _customGroups = new();

    [ObservableProperty]
    private ObservableCollection<GroupItemSelection> _groupSelectableInstances = new();

    private readonly Dictionary<string, HashSet<Guid>> _groupMemberships = new();

    public ObservableCollection<Guid> SelectedGroupInstanceIds { get; } = new();

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string? _errorMessage;

    // ── Unified Add Game Instance Modal State (Multi-step with Expandable Views) ──
    [ObservableProperty]
    private bool _isAddInstanceModalOpen;

    [ObservableProperty]
    private string _addModalStep = "SelectSource"; // "SelectSource", "Steam", "Zip", "Folder"

    // 1. Steam Sub-View
    [ObservableProperty]
    private bool _isScanningSteam;

    [ObservableProperty]
    private string _steamSearchQuery = string.Empty;

    [ObservableProperty]
    private string? _steamScanError;

    [ObservableProperty]
    private ObservableCollection<InstalledSteamGame> _installedSteamGames = [];

    private readonly List<InstalledSteamGame> _allDetectedSteamGames = [];

    // Steam duplicate check & imported trackers
    [ObservableProperty]
    private bool _isDuplicateSteamPromptOpen;

    [ObservableProperty]
    private InstalledSteamGame? _duplicateSteamGame;

    [ObservableProperty]
    private ObservableCollection<uint> _importedSteamAppIds = [];

    // 2. Depot ZIP Sub-View
    [ObservableProperty]
    private bool _isParsingZip;

    [ObservableProperty]
    private string? _pendingZipPath;

    [ObservableProperty]
    private string _previewGameName = string.Empty;

    [ObservableProperty]
    private uint _previewAppId;

    [ObservableProperty]
    private EngineInfo? _previewEngine;

    [ObservableProperty]
    private string _previewManifestDateFormatted = "Unknown";

    [ObservableProperty]
    private int _previewDepotsCount;

    [ObservableProperty]
    private int _previewDlcsCount;

    [ObservableProperty]
    private bool _isDepotsListExpanded;

    [ObservableProperty]
    private bool _isDlcsListExpanded;

    [ObservableProperty]
    private ObservableCollection<DepotPreviewItem> _previewDepots = [];

    [ObservableProperty]
    private ObservableCollection<DlcPreviewItem> _previewDlcs = [];

    [ObservableProperty]
    private string _previewInstallPath = string.Empty;

    [ObservableProperty]
    private string _previewHeaderImageUrl = string.Empty;

    [ObservableProperty]
    private string? _zipErrorMessage;

    private DepotBoxArchive? _pendingArchive;

    // 3. Existing Folder Sub-View
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

    // 4. Steam Search Sub-Modal for Folder Import
    [ObservableProperty]
    private bool _isSteamSearchModalOpen;

    [ObservableProperty]
    private string _steamSearchStoreQuery = string.Empty;

    [ObservableProperty]
    private bool _isSearchingSteamStore;

    [ObservableProperty]
    private ObservableCollection<SteamStoreSearchItem> _steamStoreSearchResults = [];

    // ── Steam Required for Online Modal State ──
    [ObservableProperty]
    private bool _isSteamRequiredModalOpen;

    [ObservableProperty]
    private bool _isStartingSteam;

    [ObservableProperty]
    private string? _steamLaunchStatusText;

    [ObservableProperty]
    private GameInstance? _pendingLaunchInstance;

    public Action<GameInstance>? OnManageInstanceRequested { get; set; }
    public Action<GameInstance, bool>? OnManageInstanceRequestedWithUpdate { get; set; }

    public LibraryViewModel(
        IInstanceManager instanceManager,
        IDepotBoxArchiveParser archiveParser,
        IEngineDetector engineDetector,
        IGameLauncher gameLauncher,
        ILogger<LibraryViewModel> logger,
        IMetadataProvider? metadataProvider = null,
        ITagsService? tagsService = null,
        IDepotBoxApiClient? depotBoxApiClient = null,
        IBackgroundTaskService? backgroundTaskService = null,
        ISteamStatusService? steamStatusService = null,
        INotificationService? notificationService = null)
    {
        _instanceManager = instanceManager;
        _archiveParser = archiveParser;
        _engineDetector = engineDetector;
        _gameLauncher = gameLauncher;
        _logger = logger;
        _metadataProvider = metadataProvider;
        _tagsService = tagsService;
        _depotBoxApiClient = depotBoxApiClient;
        _backgroundTaskService = backgroundTaskService;
        _steamStatusService = steamStatusService;
        _notificationService = notificationService;
        _uiContext = SynchronizationContext.Current ?? new SynchronizationContext();

        _gameLauncher.RunningStateChanged += OnRunningStateChanged;

        _ = LoadInstancesAsync();
    }

    private void OnRunningStateChanged(object? sender, (Guid InstanceId, bool IsRunning) e)
    {
        if (_isDisposed) return;
        _uiContext.Post(_ =>
        {
            if (_isDisposed) return;
            var inst = Instances.FirstOrDefault(i => i.Id == e.InstanceId);
            if (inst != null)
            {
                var updated = inst with { Status = e.IsRunning ? InstanceStatus.Running : InstanceStatus.Ready };
                var idx = Instances.IndexOf(inst);
                if (idx >= 0) Instances[idx] = updated;
                ApplyFilters();
            }
        }, null);
    }

    [RelayCommand]
    public void ClearSearch()
    {
        SearchFilter = string.Empty;
    }

    partial void OnSearchFilterChanged(string value) => ApplyFilters();
    partial void OnSelectedEngineFilterChanged(string value) => ApplyFilters();
    partial void OnSelectedStatusFilterChanged(string value) => ApplyFilters();
    partial void OnSelectedGroupFilterChanged(string value) => ApplyFilters();
    partial void OnSelectedSortChanged(string value) => ApplyFilters();
    partial void OnIsSortAscendingChanged(bool value) => ApplyFilters();

    private void ApplyFilters()
    {
        var rawSearch = (SearchFilter ?? string.Empty).Trim();
        var query = Instances.AsEnumerable();

        if (!string.IsNullOrWhiteSpace(rawSearch))
        {
            query = query.Where(i =>
                (!string.IsNullOrEmpty(i.Name) && i.Name.Contains(rawSearch, StringComparison.OrdinalIgnoreCase)) ||
                i.AppId.ToString().Contains(rawSearch, StringComparison.OrdinalIgnoreCase) ||
                (i.Engine != null && !string.IsNullOrEmpty(i.Engine.Name) && i.Engine.Name.Contains(rawSearch, StringComparison.OrdinalIgnoreCase)) ||
                (i.Engine != null && !string.IsNullOrEmpty(i.Engine.DisplayText) && i.Engine.DisplayText.Contains(rawSearch, StringComparison.OrdinalIgnoreCase)) ||
                (i.Metadata != null && !string.IsNullOrEmpty(i.Metadata.Developer) && i.Metadata.Developer.Contains(rawSearch, StringComparison.OrdinalIgnoreCase)) ||
                (i.Metadata != null && !string.IsNullOrEmpty(i.Metadata.Publisher) && i.Metadata.Publisher.Contains(rawSearch, StringComparison.OrdinalIgnoreCase)) ||
                (!string.IsNullOrEmpty(i.InstallPath) && i.InstallPath.Contains(rawSearch, StringComparison.OrdinalIgnoreCase)));
        }

        if (!string.IsNullOrWhiteSpace(SelectedGroupFilter) && SelectedGroupFilter != "All")
        {
            if (_groupMemberships.TryGetValue(SelectedGroupFilter, out var memberIds))
            {
                query = query.Where(i => memberIds.Contains(i.Id));
            }
        }

        if (SelectedEngineFilter != "All")
        {
            query = SelectedEngineFilter switch
            {
                "Unreal" => query.Where(i => i.Engine?.Type == EngineType.UnrealEngine),
                "Unity" => query.Where(i => i.Engine?.Type == EngineType.Unity),
                "Godot" => query.Where(i => i.Engine?.Type == EngineType.Godot),
                "Source" => query.Where(i => i.Engine?.Type is EngineType.Source or EngineType.Source2),
                "Other" => query.Where(i => i.Engine?.Type is not (EngineType.UnrealEngine or EngineType.Unity or EngineType.Godot or EngineType.Source or EngineType.Source2)),
                _ => query
            };
        }

        if (SelectedStatusFilter != "All")
        {
            query = SelectedStatusFilter switch
            {
                "Ready" => query.Where(i => i.Status is InstanceStatus.Ready or InstanceStatus.Running),
                "Running" => query.Where(i => i.Status == InstanceStatus.Running),
                "Downloading" => query.Where(i => i.Status == InstanceStatus.Downloading),
                "NotInstalled" => query.Where(i => i.Status == InstanceStatus.NotInstalled),
                "UpdateAvailable" => query.Where(i => i.HasUpdateAvailable),
                _ => query
            };
        }

        // Apply Sorting with Invert Direction Support
        query = SelectedSort switch
        {
            "Alphabetical" => IsSortAscending
                ? query.OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
                : query.OrderByDescending(i => i.Name, StringComparer.OrdinalIgnoreCase),

            "Recent" => IsSortAscending
                ? query.OrderBy(i => i.LastPlayedAt.HasValue)
                       .ThenBy(i => i.LastPlayedAt ?? i.CreatedAt)
                : query.OrderByDescending(i => i.LastPlayedAt.HasValue)
                       .ThenByDescending(i => i.LastPlayedAt ?? i.CreatedAt)
                       .ThenByDescending(i => i.CreatedAt),

            "PlayTime" => IsSortAscending
                ? query.OrderBy(i => i.TotalPlayTime)
                : query.OrderByDescending(i => i.TotalPlayTime),

            "Oldest" => IsSortAscending
                ? query.OrderBy(i => i.CreatedAt)
                : query.OrderByDescending(i => i.CreatedAt),

            "AlphabeticalDesc" => IsSortAscending
                ? query.OrderByDescending(i => i.Name, StringComparer.OrdinalIgnoreCase)
                : query.OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase),

            "Loader" => IsSortAscending
                ? query.OrderBy(i => i.Engine?.DisplayText ?? string.Empty)
                : query.OrderByDescending(i => i.Engine?.DisplayText ?? string.Empty),

            "GameVersion" => IsSortAscending
                ? query.OrderBy(i => i.Engine?.Version ?? i.InstalledEmulatorVersion ?? string.Empty)
                : query.OrderByDescending(i => i.Engine?.Version ?? i.InstalledEmulatorVersion ?? string.Empty),

            _ => IsSortAscending
                ? query.OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
                : query.OrderByDescending(i => i.Name, StringComparer.OrdinalIgnoreCase)
        };

        var cardItems = query.Select(inst =>
        {
            var tags = _tagsService != null
                ? _tagsService.GetInstanceTags(inst, inst.HasUpdateAvailable)
                : [];
            return new InstanceCardItem(inst, tags);
        }).ToList();

        FilteredInstances = new ObservableCollection<InstanceCardItem>(cardItems);
    }

    [RelayCommand]
    public async Task LoadInstancesAsync()
    {
        IsLoading = true;
        ErrorMessage = null;

        try
        {
            var all = await _instanceManager.GetAllAsync(CancellationToken.None).ConfigureAwait(true);
            var sanitized = all.Select(i =>
            {
                var status = i.Status;
                if (status == InstanceStatus.NotInstalled && !string.IsNullOrWhiteSpace(i.InstallPath) && Directory.Exists(i.InstallPath))
                {
                    var cleanName = CleanName(i.Name) ?? i.Name;
                    var exes = ShortcutHelper.FindGameExecutables(i.InstallPath, cleanName);
                    bool hasDownloadedDepots = i.Depots.Count > 0 && i.Depots.All(d => d.IsDownloaded);
                    if (exes.Count > 0 || hasDownloadedDepots || (!string.IsNullOrWhiteSpace(i.ExecutablePath) && File.Exists(i.ExecutablePath)))
                    {
                        status = InstanceStatus.Ready;
                        _ = _instanceManager.UpdateAsync(i with { Status = InstanceStatus.Ready }, CancellationToken.None);
                    }
                }

                return i with
                {
                    Name = CleanName(i.Name) ?? i.Name,
                    Status = status,
                    Dlcs = i.Dlcs.Select(d => d with { Name = CleanName(d.Name) ?? d.Name }).ToList().AsReadOnly()
                };
            }).ToList();

            // Display instances immediately on UI thread without blocking
            Instances = new ObservableCollection<GameInstance>(sanitized);
            ApplyFilters();
            _logger.LogInformation("Loaded {Count} instances", Instances.Count);

            // Perform engine detection and metadata enrichment in background
            _ = Task.Run(async () =>
            {
                foreach (var inst in sanitized)
                {
                    try
                    {
                        if (!string.IsNullOrWhiteSpace(inst.InstallPath) && Directory.Exists(inst.InstallPath))
                        {
                            var engine = await _engineDetector.DetectEngineAsync(inst.InstallPath, CancellationToken.None).ConfigureAwait(false);

                            if (engine != null && engine.Type != EngineType.Generic && (inst.Engine == null || inst.Engine.Type == EngineType.Generic))
                            {
                                var updated = inst with { Engine = engine };
                                await _instanceManager.UpdateAsync(updated, CancellationToken.None).ConfigureAwait(false);

                                _uiContext.Post(_ =>
                                {
                                    var existing = Instances.FirstOrDefault(x => x.Id == inst.Id);
                                    if (existing != null)
                                    {
                                        var idx = Instances.IndexOf(existing);
                                        if (idx >= 0)
                                        {
                                            Instances[idx] = updated;
                                            ApplyFilters();
                                        }
                                    }
                                }, null);
                            }
                        }
                    }
                    catch { }
                }

                await FetchMissingMetadataAsync(sanitized).ConfigureAwait(false);
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load instances");
            ErrorMessage = $"Failed to load instances: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    // ── Dropdown Filters Commands ──
    [RelayCommand]
    public void ToggleSortDirection()
    {
        IsSortAscending = !IsSortAscending;
    }

    [RelayCommand]
    public void ToggleSortDropdown()
    {
        IsSortDropdownOpen = !IsSortDropdownOpen;
        if (IsSortDropdownOpen)
        {
            IsGroupDropdownOpen = false;
            IsFilterDropdownOpen = false;
        }
    }

    [RelayCommand]
    public void CloseSortDropdown() => IsSortDropdownOpen = false;

    [RelayCommand]
    public void SelectSort(string sortKey)
    {
        SelectedSort = sortKey;
        OnPropertyChanged(nameof(SelectedSortDisplayName));
        IsSortDropdownOpen = false;
        ApplyFilters();
    }

    [RelayCommand]
    public void ToggleGroupDropdown()
    {
        IsGroupDropdownOpen = !IsGroupDropdownOpen;
        if (IsGroupDropdownOpen)
        {
            IsSortDropdownOpen = false;
            IsFilterDropdownOpen = false;
        }
    }

    [RelayCommand]
    public void CloseGroupDropdown() => IsGroupDropdownOpen = false;

    [RelayCommand]
    public void SelectGroup(string groupKey)
    {
        SelectedGroupFilter = groupKey;
        OnPropertyChanged(nameof(SelectedGroupDisplayName));
        IsGroupDropdownOpen = false;
        ApplyFilters();
    }

    [RelayCommand]
    public void DeleteGroup(string groupName)
    {
        if (CustomGroups.Contains(groupName))
        {
            CustomGroups.Remove(groupName);
            _groupMemberships.Remove(groupName);
            if (SelectedGroupFilter == groupName)
            {
                SelectedGroupFilter = "All";
                OnPropertyChanged(nameof(SelectedGroupDisplayName));
            }
            ApplyFilters();
        }
    }

    [RelayCommand]
    public void ToggleFilterDropdown()
    {
        IsFilterDropdownOpen = !IsFilterDropdownOpen;
        if (IsFilterDropdownOpen)
        {
            IsSortDropdownOpen = false;
            IsGroupDropdownOpen = false;
        }
    }

    [RelayCommand]
    public void CloseFilterDropdown() => IsFilterDropdownOpen = false;

    [RelayCommand]
    public void SelectEngineFilter(string engine)
    {
        SelectedEngineFilter = engine;
        OnPropertyChanged(nameof(HasActiveFilter));
        ApplyFilters();
    }

    [RelayCommand]
    public void SelectStatusFilter(string status)
    {
        SelectedStatusFilter = status;
        OnPropertyChanged(nameof(HasActiveFilter));
        ApplyFilters();
    }

    [RelayCommand]
    public void ClearFilters()
    {
        SelectedEngineFilter = "All";
        SelectedStatusFilter = "All";
        OnPropertyChanged(nameof(HasActiveFilter));
        ApplyFilters();
    }

    [RelayCommand]
    public void OpenCreateGroup()
    {
        NewGroupName = $"Group {CustomGroups.Count + 1}";
        GroupSelectableInstances = new ObservableCollection<GroupItemSelection>(
            Instances.Select(i => new GroupItemSelection { Instance = i, IsSelected = false }));
        IsCreateGroupOpen = true;
        IsGroupDropdownOpen = false;
    }

    [RelayCommand]
    public void CloseCreateGroup() => IsCreateGroupOpen = false;

    [RelayCommand]
    public void ToggleGroupItemSelection(GroupItemSelection item)
    {
        if (item != null)
        {
            item.IsSelected = !item.IsSelected;
        }
    }

    [RelayCommand]
    public void SaveNewGroup()
    {
        var name = (NewGroupName ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(name))
        {
            if (!CustomGroups.Contains(name))
            {
                CustomGroups.Add(name);
            }
            var selectedIds = GroupSelectableInstances.Where(x => x.IsSelected).Select(x => x.Instance.Id).ToHashSet();
            _groupMemberships[name] = selectedIds;
            SelectedGroupFilter = name;
            OnPropertyChanged(nameof(SelectedGroupDisplayName));
        }
        IsCreateGroupOpen = false;
        ApplyFilters();
    }

    // ── Unified Add Game Instance Modal Handlers ──

    [RelayCommand]
    public void OpenAddInstanceModal(string? step = "SelectSource")
    {
        AddModalStep = string.IsNullOrWhiteSpace(step) ? "SelectSource" : step;
        IsAddInstanceModalOpen = true;
        IsSortDropdownOpen = false;
        IsGroupDropdownOpen = false;
        IsFilterDropdownOpen = false;

        if (AddModalStep == "Steam")
        {
            _ = OpenSteamStepAsync();
        }
        else if (AddModalStep == "SelectSource")
        {
            ResetZipPreview();
        }
    }

    [RelayCommand]
    public void CloseAddInstanceModal()
    {
        IsAddInstanceModalOpen = false;
        AddModalStep = "SelectSource";
        ResetZipPreview();
    }

    public Action<string>? OnNavigateRequested { get; set; }

    [RelayCommand]
    public void ResetAddModal()
    {
        AddModalStep = "SelectSource";
    }

    [RelayCommand]
    public void ExplorePlatformGames()
    {
        IsAddInstanceModalOpen = false;
        OnNavigateRequested?.Invoke("Explore");
    }

    [RelayCommand]
    public void BackToAddInstanceSource()
    {
        AddModalStep = "SelectSource";
    }

    // ── 1. Steam Step ──

    partial void OnSteamSearchQueryChanged(string value) => ApplySteamFilter();

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
    public async Task OpenSteamStepAsync()
    {
        AddModalStep = "Steam";
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
    public async Task ImportSteamGameAsync(InstalledSteamGame? steamGame)
    {
        if (steamGame == null) return;

        // Check if duplicate instance exists
        if (Instances.Any(i => i.AppId == steamGame.AppId))
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
            IReadOnlyList<DlcInfo> dlcs = [];
            if (_metadataProvider != null && steamGame.AppId > 0)
            {
                try
                {
                    meta = await _metadataProvider.GetMetadataAsync(steamGame.AppId, CancellationToken.None).ConfigureAwait(true);
                    dlcs = await _metadataProvider.GetDlcListAsync(steamGame.AppId, CancellationToken.None).ConfigureAwait(true);
                }
                catch { }
            }

            var allExisting = await _instanceManager.GetAllAsync(CancellationToken.None).ConfigureAwait(true);
            var existingNames = allExisting.Select(i => i.Name).ToList();
            var uniqueName = PathHelper.GenerateUniqueInstanceName(existingNames, steamGame.Name);

            var instance = new GameInstance
            {
                Name = uniqueName,
                AppId = steamGame.AppId,
                InstallPath = steamGame.FullPath,
                ExecutablePath = exe,
                Status = File.Exists(exe) ? InstanceStatus.Ready : InstanceStatus.NotInstalled,
                Metadata = meta,
                Dlcs = dlcs,
                Engine = engine,
                Origin = InstanceOrigin.Steam
            };

            var created = await _instanceManager.CreateAsync(instance, CancellationToken.None).ConfigureAwait(true);
            Instances.Add(created);
            ApplyFilters();

            if (!ImportedSteamAppIds.Contains(steamGame.AppId))
            {
                ImportedSteamAppIds.Add(steamGame.AppId);
            }

            _notificationService?.ShowSuccess("Steam Game Imported", $"{steamGame.Name} is now ready in your library!");

            IsAddInstanceModalOpen = false;
            OnManageInstanceRequested?.Invoke(created);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to import Steam game: {Name}", steamGame.Name);
            ErrorMessage = ex.Message;
        }
        finally
        {
            IsLoading = false;
        }
    }

    // ── 2. Depot ZIP Step ──

    [RelayCommand]
    public void OpenZipStep()
    {
        AddModalStep = "Zip";
    }

    public void ResetZipPreview()
    {
        PendingZipPath = null;
        _pendingArchive = null;
        PreviewGameName = string.Empty;
        PreviewAppId = 0;
        PreviewEngine = null;
        PreviewManifestDateFormatted = "Unknown";
        PreviewDepotsCount = 0;
        PreviewDlcsCount = 0;
        IsDepotsListExpanded = false;
        IsDlcsListExpanded = false;
        PreviewDepots.Clear();
        PreviewDlcs.Clear();
        PreviewInstallPath = string.Empty;
        PreviewHeaderImageUrl = string.Empty;
        ZipErrorMessage = null;
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
            Title = "Import DepotBox Archive",
            Filter = "ZIP Archives (*.zip)|*.zip|All Files (*.*)|*.*",
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

        try
        {
            _logger.LogInformation("Parsing DepotBox archive: {Path}", zipPath);
            var archive = await _archiveParser.ParseAsync(zipPath, CancellationToken.None).ConfigureAwait(true);
            if (archive.Games.Count == 0)
            {
                ZipErrorMessage = "No games found inside the selected ZIP archive.";
                return;
            }

            var mainGame = archive.Games.FirstOrDefault(g => !g.IsDlc) ?? archive.Games[0];
            var cleanMainName = CleanName(mainGame.Name) ?? $"App {mainGame.AppId}";
            var baseDir = Path.GetDirectoryName(zipPath) ?? string.Empty;

            var allExisting = await _instanceManager.GetAllAsync(CancellationToken.None).ConfigureAwait(true);
            var existingNames = allExisting.Select(i => i.Name).ToList();
            var existingPaths = allExisting.Select(i => i.InstallPath).Where(p => !string.IsNullOrWhiteSpace(p)).ToList();

            var uniqueName = PathHelper.GenerateUniqueInstanceName(existingNames, cleanMainName);
            var installPath = PathHelper.GenerateUniqueInstallPath(baseDir, uniqueName, existingPaths);

            var engine = await _engineDetector.DetectEngineAsync(installPath, CancellationToken.None).ConfigureAwait(true);

            // Extract manifest creation date
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

            _pendingArchive = archive;
            PendingZipPath = zipPath;
            PreviewGameName = uniqueName;
            PreviewAppId = mainGame.AppId;
            PreviewEngine = engine;
            PreviewManifestDateFormatted = manifestDate.HasValue
                ? $"{manifestDate.Value.LocalDateTime:d MMM yyyy}"
                : "Latest Build";

            var allDepots = archive.Games
                .SelectMany(g => g.Depots.Select(d => new DepotPreviewItem(d.DepotId, g.Name ?? $"Depot {d.DepotId}", d.ManifestFileName)))
                .ToList();
            PreviewDepots = new ObservableCollection<DepotPreviewItem>(allDepots);
            PreviewDepotsCount = allDepots.Count;

            var allDlcs = archive.Games
                .Where(g => g.IsDlc)
                .Select(g => new DlcPreviewItem(g.AppId, CleanName(g.Name) ?? $"DLC {g.AppId}"))
                .ToList();
            PreviewDlcs = new ObservableCollection<DlcPreviewItem>(allDlcs);
            PreviewDlcsCount = allDlcs.Count;

            PreviewInstallPath = installPath;
            PreviewHeaderImageUrl = await ResolveBannerUrlAsync(mainGame.AppId).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to parse ZIP archive");
            ZipErrorMessage = $"Error reading ZIP: {ex.Message}";
        }
        finally
        {
            IsParsingZip = false;
        }
    }

    [RelayCommand]
    public void BrowsePreviewInstallPath()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Select Installation Directory",
            InitialDirectory = Directory.Exists(PreviewInstallPath) ? PreviewInstallPath : null
        };

        if (dialog.ShowDialog() != true || string.IsNullOrWhiteSpace(dialog.FolderName)) return;

        // The user picks a *library root* (e.g. D:\Games), not the game folder itself.
        // Without this the game would be extracted straight into the root, mixing its files
        // with everything else already there. EnsureGameSubfolder is a no-op if the user
        // already navigated into a folder named after the game.
        var gameName = string.IsNullOrWhiteSpace(PreviewGameName) ? "Game" : PreviewGameName;
        PreviewInstallPath = PathHelper.EnsureGameSubfolder(dialog.FolderName, gameName);
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
            var instance = BuildInstanceFromArchive(_pendingArchive, PendingZipPath) with
            {
                Name = PreviewGameName,
                InstallPath = PreviewInstallPath,
                Engine = PreviewEngine
            };

            var created = await _instanceManager.CreateAsync(instance, CancellationToken.None).ConfigureAwait(true);
            ExtractManifestsToInstanceStorage(PendingZipPath, created.Id);

            Instances.Add(created);
            ApplyFilters();

            _notificationService?.ShowSuccess("Depot Instance Imported", $"{created.Name} is ready in your library.");
            _logger.LogInformation("Successfully imported instance: {Name} ({AppId})", created.Name, created.AppId);
            _ = FetchMissingMetadataAsync(new List<GameInstance> { created });

            IsAddInstanceModalOpen = false;
            ResetZipPreview();
            OnManageInstanceRequested?.Invoke(created);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to confirm ZIP import");
            ZipErrorMessage = $"Import failed: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    // ── 3. Folder Step ──

    [RelayCommand]
    public void OpenFolderStep()
    {
        AddModalStep = "Folder";
        FolderPath = string.Empty;
        FolderGameName = string.Empty;
        FolderAppId = 0;
        FolderExecutablePath = string.Empty;
        FolderEngine = null;
        FolderHeaderImageUrl = null;
        FolderErrorMessage = null;
    }

    [RelayCommand]
    public async Task BrowseFolderAsync()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Select Game Installation Folder"
        };

        if (dialog.ShowDialog() != true) return;
        await LoadFolderAsync(dialog.FolderName);
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
                // Auto-search Steam by folder name
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
            _logger.LogError(ex, "Failed to inspect folder");
            FolderErrorMessage = ex.Message;
        }
        finally
        {
            IsScanningFolder = false;
        }
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

        IsLoading = true;
        try
        {
            var allExisting = await _instanceManager.GetAllAsync(CancellationToken.None).ConfigureAwait(true);
            var colliding = allExisting.FirstOrDefault(i => string.Equals(i.InstallPath?.TrimEnd('\\', '/'), FolderPath.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase));
            if (colliding != null)
            {
                FolderErrorMessage = $"Location collision: The selected folder is already in use by instance \"{colliding.Name}\".";
                _notificationService?.ShowError("Folder Collision", $"The folder is already used by instance \"{colliding.Name}\". Please select a different folder.");
                return;
            }

            var existingNames = allExisting.Select(i => i.Name).ToList();
            var rawName = string.IsNullOrWhiteSpace(FolderGameName) ? Path.GetFileName(FolderPath) : FolderGameName.Trim();
            var uniqueName = PathHelper.GenerateUniqueInstanceName(existingNames, rawName);

            GameMetadata? meta = null;
            IReadOnlyList<DlcInfo> dlcs = [];
            if (_metadataProvider != null && FolderAppId > 0 && FolderAppId != 480)
            {
                try
                {
                    meta = await _metadataProvider.GetMetadataAsync(FolderAppId, CancellationToken.None).ConfigureAwait(true);
                    dlcs = await _metadataProvider.GetDlcListAsync(FolderAppId, CancellationToken.None).ConfigureAwait(true);
                }
                catch { }
            }

            var instance = new GameInstance
            {
                Name = uniqueName,
                AppId = FolderAppId,
                InstallPath = FolderPath,
                ExecutablePath = File.Exists(FolderExecutablePath) ? FolderExecutablePath : null,
                Engine = FolderEngine,
                Metadata = meta,
                Dlcs = dlcs,
                Origin = InstanceOrigin.ImportedFolder,
                IsDepotBoxAssociated = false,
                Status = File.Exists(FolderExecutablePath) ? InstanceStatus.Ready : InstanceStatus.NotInstalled
            };

            var created = await _instanceManager.CreateAsync(instance, CancellationToken.None).ConfigureAwait(true);
            Instances.Add(created);
            ApplyFilters();

            _notificationService?.ShowSuccess("Game Folder Imported", $"{created.Name} is now added to your library!");

            IsAddInstanceModalOpen = false;
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

    private static Task<string> ResolveBannerUrlAsync(uint appId, CancellationToken ct = default)
    {
        if (appId == 0) return Task.FromResult(string.Empty);
        return Task.FromResult($"https://cdn.cloudflare.steamstatic.com/steam/apps/{appId}/header.jpg");
    }

    /// <summary>
    /// Adds an existing game install folder.
    /// </summary>
    [RelayCommand]
    public async Task AddExistingFolderAsync()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Select Existing Game Installation Folder"
        };

        if (dialog.ShowDialog() != true) return;

        var folder = dialog.FolderName;
        if (!Directory.Exists(folder)) return;

        IsLoading = true;
        try
        {
            var folderName = Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            var engine = await _engineDetector.DetectEngineAsync(folder, CancellationToken.None).ConfigureAwait(true);
            var exe = _engineDetector.FindPrimaryExecutable(folder, folderName);

            var newInstance = new GameInstance
            {
                Name = folderName,
                AppId = 0,
                InstallPath = folder,
                ExecutablePath = exe,
                Engine = engine,
                Status = File.Exists(exe) ? InstanceStatus.Ready : InstanceStatus.NotInstalled
            };

            var created = await _instanceManager.CreateAsync(newInstance, CancellationToken.None).ConfigureAwait(true);
            Instances.Add(created);
            ApplyFilters();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to add game folder");
            ErrorMessage = $"Failed to add folder: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task PlayInstanceAsync(object? item)
    {
        var instance = item is InstanceCardItem card ? card.Instance : item as GameInstance;
        if (instance == null) return;

        if (instance.Status == InstanceStatus.NotInstalled && instance.Origin != InstanceOrigin.Steam)
        {
            OnManageInstanceRequested?.Invoke(instance);
            return;
        }

        // Check if emulator is online and Steam is not running
        var isOnlineEmulator = instance.EmulatorEnabled &&
            (string.Equals(instance.EmulatorId, "refix_valve", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(instance.EmulatorId, "refix", StringComparison.OrdinalIgnoreCase));

        if (isOnlineEmulator && _steamStatusService != null && !_steamStatusService.CurrentStatus.IsRunning)
        {
            PendingLaunchInstance = instance;
            IsSteamRequiredModalOpen = true;
            return;
        }

        await LaunchInstanceInternalAsync(instance).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task StartSteamAndLaunchAsync()
    {
        var inst = PendingLaunchInstance;
        if (inst == null)
        {
            IsSteamRequiredModalOpen = false;
            return;
        }

        IsStartingSteam = true;
        SteamLaunchStatusText = "Starting Steam and waiting for user profile to load...";

        try
        {
            if (_steamStatusService != null)
            {
                var progress = new Progress<string>(msg =>
                {
                    SteamLaunchStatusText = msg;
                });

                await _steamStatusService.LaunchAndWaitForSteamFullyLoadedAsync(
                    TimeSpan.FromSeconds(50),
                    progress,
                    CancellationToken.None).ConfigureAwait(true);
            }
            else
            {
                var steamPath = ShortcutHelper.GetSteamPath();
                if (!string.IsNullOrWhiteSpace(steamPath))
                {
                    var steamExe = Path.Combine(steamPath, "steam.exe");
                    if (File.Exists(steamExe))
                    {
                        Process.Start(new ProcessStartInfo(steamExe) { UseShellExecute = true });
                    }
                    else
                    {
                        Process.Start(new ProcessStartInfo("steam://open/main") { UseShellExecute = true });
                    }
                }
                else
                {
                    Process.Start(new ProcessStartInfo("steam://open/main") { UseShellExecute = true });
                }

                await Task.Delay(3000).ConfigureAwait(true);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error while starting and waiting for Steam");
        }
        finally
        {
            IsStartingSteam = false;
            IsSteamRequiredModalOpen = false;
        }

        await LaunchInstanceInternalAsync(inst).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task LaunchAnywayAsync()
    {
        IsSteamRequiredModalOpen = false;
        var inst = PendingLaunchInstance;
        if (inst != null)
        {
            await LaunchInstanceInternalAsync(inst).ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private void CloseSteamRequiredModal()
    {
        IsSteamRequiredModalOpen = false;
        PendingLaunchInstance = null;
    }

    private async Task LaunchInstanceInternalAsync(GameInstance instance)
    {
        _logger.LogInformation("Launching instance {Name}", instance.Name);
        var result = await _gameLauncher.LaunchAsync(instance, null, CancellationToken.None).ConfigureAwait(true);
        if (!result.Success)
        {
            ErrorMessage = result.Message;
        }
    }

    [RelayCommand]
    private void CheckDepotBoxUpdates(object? item)
    {
        var instance = item is InstanceCardItem card ? card.Instance : item as GameInstance;
        if (instance == null) return;
        if (instance.Origin == InstanceOrigin.Steam) return;

        if (OnManageInstanceRequestedWithUpdate != null)
        {
            OnManageInstanceRequestedWithUpdate.Invoke(instance, true);
        }
        else
        {
            ManageInstance(instance);
        }
    }

    [RelayCommand]
    private void ManageInstance(object? item)
    {
        var instance = item is InstanceCardItem card ? card.Instance : item as GameInstance;
        if (instance != null)
        {
            OnManageInstanceRequested?.Invoke(instance);
        }
    }

    [RelayCommand]
    private void OpenFolder(object? item)
    {
        var instance = item is InstanceCardItem card ? card.Instance : item as GameInstance;
        if (instance != null && !string.IsNullOrWhiteSpace(instance.InstallPath) && Directory.Exists(instance.InstallPath))
        {
            Process.Start(new ProcessStartInfo { FileName = instance.InstallPath, UseShellExecute = true });
        }
    }

    [RelayCommand]
    private async Task DeleteInstanceAsync(Guid id)
    {
        try
        {
            await _instanceManager.DeleteAsync(id, CancellationToken.None).ConfigureAwait(true);
            var toRemove = Instances.FirstOrDefault(i => i.Id == id);
            if (toRemove is not null)
            {
                Instances.Remove(toRemove);
                ApplyFilters();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete instance {Id}", id);
            ErrorMessage = $"Delete failed: {ex.Message}";
        }
    }

    private async Task FetchMissingMetadataAsync(List<GameInstance> list)
    {
        if (_metadataProvider is null) return;

        foreach (var instance in list)
        {
            if (instance.Metadata is not null || instance.AppId == 0) continue;

            try
            {
                var meta = await _metadataProvider.GetMetadataAsync(instance.AppId, CancellationToken.None).ConfigureAwait(false);
                if (meta is not null)
                {
                    var updated = instance with { Metadata = meta };
                    await _instanceManager.UpdateAsync(updated, CancellationToken.None).ConfigureAwait(false);

                    _uiContext.Post(_ =>
                    {
                        var idx = Instances.IndexOf(instance);
                        if (idx >= 0)
                        {
                            Instances[idx] = updated;
                            ApplyFilters();
                        }
                    }, null);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not fetch Steam metadata for AppId={AppId}", instance.AppId);
            }
        }
    }

    private static GameInstance BuildInstanceFromArchive(DepotBoxArchive archive, string zipPath)
    {
        var mainGame = archive.Games.FirstOrDefault(g => !g.IsDlc) ?? archive.Games[0];
        var cleanMainName = CleanName(mainGame.Name) ?? $"App {mainGame.AppId}";
        var baseDir = Path.GetDirectoryName(zipPath) ?? string.Empty;

        return new GameInstance
        {
            Name = cleanMainName,
            AppId = mainGame.AppId,
            InstallPath = PathHelper.EnsureGameSubfolder(baseDir, cleanMainName),
            SourceArchivePath = zipPath,
            Status = InstanceStatus.NotInstalled,
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

        _gameLauncher.RunningStateChanged -= OnRunningStateChanged;
    }
}

public partial class GroupItemSelection : ObservableObject
{
    public GameInstance Instance { get; init; } = null!;

    [ObservableProperty]
    private bool _isSelected;
}
