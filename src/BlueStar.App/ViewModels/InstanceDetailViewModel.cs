using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using BlueStar.Core.Helpers;
using BlueStar.Core.Interfaces;
using BlueStar.Core.Models;
using BlueStar.Infrastructure.Downloader;
using BlueStar.Infrastructure.Emulators;
using BlueStar.Infrastructure.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace BlueStar.App.ViewModels;

/// <summary>
/// Model for a depot item with selection state in the UI.
/// </summary>
public partial class SelectableDepotItem : ObservableObject
{
    public Action? OnSelectionChanged { get; set; }

    [ObservableProperty]
    private DepotInfo _depot = null!;

    [ObservableProperty]
    private bool _isSelected = true;

    partial void OnIsSelectedChanged(bool value) => OnSelectionChanged?.Invoke();

    public bool IsDownloaded => Depot?.IsDownloaded ?? false;

    public void NotifyDownloadedChanged() => OnPropertyChanged(nameof(IsDownloaded));

    public string FormattedSize => Depot.SizeBytes switch
    {
        > 1024 * 1024 * 1024 => $"{Depot.SizeBytes / (1024.0 * 1024.0 * 1024.0):F2} GB",
        > 1024 * 1024 => $"{Depot.SizeBytes / (1024.0 * 1024.0):F1} MB",
        _ => $"{Depot.SizeBytes / 1024.0:F0} KB"
    };

    public string DisplayName => !string.IsNullOrWhiteSpace(Depot.Name) && !Depot.Name.StartsWith("Depot ")
        ? Depot.Name
        : $"Depot {Depot.DepotId}";

    public string CategoryTag => string.IsNullOrWhiteSpace(Depot.Category) ? "Base Game" : Depot.Category;
    public string PlatformTag => string.IsNullOrWhiteSpace(Depot.Platform) ? "Universal" : Depot.Platform;
    public string? ArchitectureTag => Depot.Architecture;
}

/// <summary>
/// Model for a DLC item with selection state and tags in the UI.
/// </summary>
public partial class SelectableDlcItem : ObservableObject
{
    public Action? OnSelectionChanged { get; set; }

    [ObservableProperty]
    private DlcInfo _dlc = null!;

    [ObservableProperty]
    private bool _isSelected = true;

    partial void OnIsSelectedChanged(bool value) => OnSelectionChanged?.Invoke();

    public bool IsDownloaded => Dlc.IsInstalled || (Dlc.Depots.Count > 0 && Dlc.Depots.All(d => d.IsDownloaded));

    public void NotifyDownloadedChanged() => OnPropertyChanged(nameof(IsDownloaded));

    public string DisplayName => !string.IsNullOrWhiteSpace(Dlc.Name) ? Dlc.Name : $"DLC {Dlc.AppId}";
    public string CategoryTag => string.IsNullOrWhiteSpace(Dlc.Category) ? "DLC" : Dlc.Category;
    public string PlatformTag => string.IsNullOrWhiteSpace(Dlc.Platform) ? "Universal" : Dlc.Platform;
    public string HeaderImageUrl => Dlc.HeaderImageUrl;

    public string FormattedSize
    {
        get
        {
            var size = Dlc.TotalSizeBytes;
            return size switch
            {
                > 1024 * 1024 * 1024 => $"{size / (1024.0 * 1024.0 * 1024.0):F2} GB",
                > 1024 * 1024 => $"{size / (1024.0 * 1024.0):F1} MB",
                > 0 => $"{size / 1024.0:F0} KB",
                _ => $"AppID: {Dlc.AppId}"
            };
        }
    }
}

/// <summary>
/// Model for a depot update item with comparison of current vs new manifest.
/// </summary>
public partial class DepotUpdateItem : ObservableObject
{
    [ObservableProperty]
    private uint _depotId;

    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private ulong _currentManifestId;

    [ObservableProperty]
    private ulong _newManifestId;

    [ObservableProperty]
    private long _sizeBytes;

    [ObservableProperty]
    private bool _isSelected = true;

    public string FormattedSize => SizeBytes switch
    {
        > 1024 * 1024 * 1024 => $"{SizeBytes / (1024.0 * 1024.0 * 1024.0):F2} GB",
        > 1024 * 1024 => $"{SizeBytes / (1024.0 * 1024.0):F1} MB",
        _ => $"{SizeBytes / 1024.0:F0} KB"
    };

    public string DisplayCurrentManifest => CurrentManifestId > 0 ? CurrentManifestId.ToString() : "Not downloaded";
    public string DisplayNewManifest => NewManifestId.ToString();
}

/// <summary>
/// ViewModel for the complete Instance Dashboard (Overview, Depots, DLCs, Mods, Emulator, Settings, Logs).
/// </summary>
public partial class InstanceDetailViewModel : ObservableObject
{
    private readonly IInstanceManager _instanceManager;
    private readonly IDlcInstaller _dlcInstaller;
    private readonly DownloadQueueManager _downloadQueueManager;
    private readonly AppSettingsService _appSettings;
    private readonly IEngineDetector _engineDetector;
    private readonly IModManagerRegistry _modManagerRegistry;
    private readonly IBepInExService _bepInExService;
    private readonly IWorkshopService _workshopService;
    private readonly IEmulatorRegistry _emulatorRegistry;
    private readonly IGameLauncher _gameLauncher;
    private readonly IMetadataProvider? _metadataProvider;
    private readonly ILogger<InstanceDetailViewModel> _logger;
    private readonly SynchronizationContext _uiContext;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HeroTags))]
    [NotifyPropertyChangedFor(nameof(IsDepotBoxInstance))]
    [NotifyPropertyChangedFor(nameof(IsSteamInstance))]
    [NotifyPropertyChangedFor(nameof(IsImportedUnassociatedInstance))]
    [NotifyPropertyChangedFor(nameof(IsDepotBoxTabsVisible))]
    private GameInstance _instance = null!;

    public IReadOnlyList<GameTag> HeroTags => _tagsService?.GetInstanceDetailHeroTags(Instance, HasGameUpdateAvailable) ?? [];
    public bool IsDepotBoxInstance => Instance != null && (Instance.Origin == InstanceOrigin.DepotBox || Instance.IsDepotBoxAssociated);
    public bool IsSteamInstance => Instance != null && Instance.Origin == InstanceOrigin.Steam;
    public bool IsImportedUnassociatedInstance => Instance != null && Instance.Origin == InstanceOrigin.ImportedFolder && !Instance.IsDepotBoxAssociated;
    public bool IsDepotBoxTabsVisible => IsDepotBoxInstance;

    // ── Association Modal State (ImportedFolder -> DepotBox) ──
    [ObservableProperty]
    private bool _isAssociationModalOpen;

    [ObservableProperty]
    private bool _isSearchingDepotBoxCandidate;

    [ObservableProperty]
    private SearchResult? _depotBoxCandidate;

    [ObservableProperty]
    private string _associationSearchQuery = string.Empty;

    [ObservableProperty]
    private ObservableCollection<SearchResult> _associationSearchResults = [];

    // ── DLC Warning Modal State ──
    [ObservableProperty]
    private bool _isDlcWarningModalOpen;

    // ── Steam Online Launch Required Modal State ──
    [ObservableProperty]
    private bool _isSteamRequiredModalOpen;

    [ObservableProperty]
    private bool _isStartingSteam;

    [ObservableProperty]
    private string? _steamLaunchStatusText;

    [ObservableProperty]
    private string _selectedTab = "Overview";

    [ObservableProperty]
    private ObservableCollection<SelectableDepotItem> _depots = [];

    [ObservableProperty]
    private ObservableCollection<SelectableDlcItem> _dlcs = [];

    [ObservableProperty]
    private string? _statusMessage;

    [ObservableProperty]
    private bool _isProcessing;

    [ObservableProperty]
    private bool _isDlcUnlocked;

    [ObservableProperty]
    private string _totalSelectedSizeFormatted = "0 KB";

    [ObservableProperty]
    private long _totalSelectedSizeBytes;

    // ── Version & Update Comparison State ──
    [ObservableProperty]
    private string _installedVersionText = "Not installed";

    [ObservableProperty]
    private string _latestVersionText = "Checking...";

    [ObservableProperty]
    private DateTimeOffset? _installedVersionDate;

    [ObservableProperty]
    private DateTimeOffset? _latestVersionDate;

    [ObservableProperty]
    private bool _hasGameUpdateAvailable;

    [ObservableProperty]
    private bool _isCheckingGameUpdate;

    [ObservableProperty]
    private bool _isUpdateModalOpen;

    [ObservableProperty]
    private ObservableCollection<DepotUpdateItem> _updateAvailableDepots = [];

    [ObservableProperty]
    private bool _isApplyingGameUpdate;

    // ── Launching & Process State ──
    [ObservableProperty]
    private bool _isGameRunning;

    // ── Mods Management State ──
    [ObservableProperty]
    private ObservableCollection<ModItem> _installedMods = [];

    [ObservableProperty]
    private bool _supportsMods;

    public bool IsModsTabVisible
    {
        get
        {
            if (!IsInstalled) return false;
            if (Instance == null) return false;

            if (Instance.Engine != null)
            {
                if (Instance.Engine.Supports(EngineCapabilities.Mods) ||
                    Instance.Engine.Supports(EngineCapabilities.WorkshopSupported) ||
                    Instance.Engine.Supports(EngineCapabilities.BepInExSupported))
                {
                    return true;
                }
            }

            if (Instance.AppId > 0) return true;

            if (_modManagerRegistry != null)
            {
                var mgr = _modManagerRegistry.GetManagerForInstance(Instance);
                if (mgr != null && mgr.Id != "generic") return true;
                if (mgr != null && Instance.Engine?.Supports(EngineCapabilities.Mods) == true) return true;
            }

            return false;
        }
    }

    [ObservableProperty]
    private string _modsDirectoryPath = string.Empty;

    // ── BepInEx (Unity) State ──
    [ObservableProperty]
    private bool _isUnityEngine;

    [ObservableProperty]
    private bool _isBepInExInstalled;

    [ObservableProperty]
    private string? _installedBepInExVersion;

    [ObservableProperty]
    private ObservableCollection<BepInExRelease> _availableBepInExVersions = [];

    [ObservableProperty]
    private BepInExRelease? _selectedBepInExVersion;

    [ObservableProperty]
    private bool _isLoadingBepInEx;

    // ── Steam Workshop State ──
    [ObservableProperty]
    private bool _hasWorkshopSupport;

    [ObservableProperty]
    private bool _isWorkshopModalOpen;

    [ObservableProperty]
    private string _workshopSearchInput = string.Empty;

    [ObservableProperty]
    private WorkshopItemInfo? _workshopPreviewItem;

    [ObservableProperty]
    private bool _isLoadingWorkshopPreview;

    [ObservableProperty]
    private bool _isDownloadingWorkshopMod;

    [ObservableProperty]
    private string? _workshopStatusMessage;

    [ObservableProperty]
    private bool _isWorkshopStatusError;

    [ObservableProperty]
    private double _workshopDownloadProgress;

    [ObservableProperty]
    private string _workshopDownloadStatusText = string.Empty;

    // ── Emulator State ──
    [ObservableProperty]
    private bool _supportsEmulation = true;

    [ObservableProperty]
    private ObservableCollection<EmulatorOptionInfo> _availableEmulatorOptions = [];

    [ObservableProperty]
    private EmulatorOptionInfo? _recommendedEmulatorOption;

    [ObservableProperty]
    private bool _isEmulatorInstalled;

    [ObservableProperty]
    private string? _installedEmulatorMode;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanDeployEmulator))]
    private bool _isDeployingEmulator;

    [ObservableProperty]
    private double _deployProgress;

    [ObservableProperty]
    private string _deployProgressMessage = string.Empty;

    [ObservableProperty]
    private bool _isDeployProgressVisible;

    [ObservableProperty]
    private bool _isReFixUpdateAvailableForInstance;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ReFixUpdateButtonText))]
    private string _currentGlobalReFixVersion = "1.0";

    public string ReFixUpdateButtonText => $"⚡ Update ReFix emulator v{CurrentGlobalReFixVersion}";

    [ObservableProperty]
    private string _instanceReFixVersion = "1.0";

    [ObservableProperty]
    private ObservableCollection<IEmulator> _availableEmulators = [];

    [ObservableProperty]
    private IEmulator? _selectedEmulator;

    [ObservableProperty]
    private EmulatorStatus? _emulatorStatus;

    // ── Post-Game Feedback Modal State ──
    [ObservableProperty]
    private bool _isFeedbackModalOpen;

    [ObservableProperty]
    private string _feedbackEmulatorName = "ReFix Online (Steam)";

    [ObservableProperty]
    private string _feedbackOptionId = "refix_valve";

    [ObservableProperty]
    private bool _showFeedbackUninstallPrompt;

    // ── Logs Console State ──
    [ObservableProperty]
    private ObservableCollection<string> _consoleLogs = [];

    // ── Settings Tab State ──
    [ObservableProperty]
    private string _instanceAlias = string.Empty;

    [ObservableProperty]
    private string _customLaunchArgs = string.Empty;

    [ObservableProperty]
    private string _configuredExecutablePath = string.Empty;

    [ObservableProperty]
    private bool _isDuplicatingInstance;

    // ── Instance Deletion State ──
    [ObservableProperty]
    private bool _isDeleteModalOpen;

    [ObservableProperty]
    private bool _isDeletingInstance;

    // ── Download progress exposed to UI ──
    [ObservableProperty]
    private DownloadJobItem? _activeJob;

    public bool IsInstanceDownloading => ActiveJob?.IsActive ?? false;
    public bool HasActiveJob => ActiveJob is not null && !ActiveJob.IsCompleted;
    public bool ShowDownloadButton => ActiveJob is null || ActiveJob.IsCompleted;
    public bool IsInstalled => Instance?.Status == InstanceStatus.Ready ||
                               Instance?.Status == InstanceStatus.Running ||
                               (ActiveJob is not null && ActiveJob.IsCompleted);

    public bool CanDeployEmulator => IsInstalled && !IsDeployingEmulator;

    public double InstanceDownloadPercentage => ActiveJob?.Percentage ?? 0;

    public string InstanceDownloadLabel => ActiveJob?.JobStatus switch
    {
        DownloadJobStatus.Downloading => $"{ActiveJob.Percentage:F0}%",
        DownloadJobStatus.Queued      => "Queued...",
        DownloadJobStatus.Paused      => "Paused",
        DownloadJobStatus.Completed   => "Done ✅",
        DownloadJobStatus.Failed      => "Failed ❌",
        DownloadJobStatus.Canceled    => "Canceled",
        _                             => AreAllSelectedDepotsDownloaded ? "Reinstall" : "Start Download"
    };

    public List<DepotInfo> GetSelectedCombinedDepots()
    {
        var selectedDepots = Depots.Where(d => d.IsSelected).Select(d => d.Depot).ToList();
        var selectedDlcDepots = Dlcs.Where(d => d.IsSelected).SelectMany(d => d.Dlc.Depots).ToList();
        return selectedDepots.Concat(selectedDlcDepots).DistinctBy(d => d.DepotId).ToList();
    }

    public bool AreAllSelectedDepotsDownloaded
    {
        get
        {
            var combined = GetSelectedCombinedDepots();
            if (combined.Count == 0) return false;
            return combined.All(d => d.IsDownloaded);
        }
    }

    public string DownloadButtonText => AreAllSelectedDepotsDownloaded ? "🔄 Reinstall Selected" : "📥 Start Download";

    // ── Shortcut Modal State ──
    [ObservableProperty]
    private bool _isShortcutModalOpen;

    [ObservableProperty]
    private ObservableCollection<string> _availableExecutables = [];

    [ObservableProperty]
    private string? _selectedExecutable;

    [ObservableProperty]
    private string _shortcutName = string.Empty;

    [ObservableProperty]
    private bool _createDesktopShortcut = true;

    [ObservableProperty]
    private bool _createStartMenuShortcut = true;

    [ObservableProperty]
    private bool _createSteamShortcut = false;

    [ObservableProperty]
    private bool _isSteamInstalled;

    [ObservableProperty]
    private bool _showRestartSteamButton;

    [ObservableProperty]
    private string? _shortcutStatusMessage;

    [ObservableProperty]
    private bool _isShortcutStatusSuccess;

    // ── Prerequisites State ──
    [ObservableProperty]
    private ObservableCollection<PrerequisiteItem> _prerequisites = [];

    [ObservableProperty]
    private bool _isScanningPrerequisites;

    [ObservableProperty]
    private bool _isInstallingPrerequisites;

    [ObservableProperty]
    private string? _prerequisiteStatusMessage;

    private readonly IEmulatorRatingService _emulatorRatingService;
    private readonly INotificationService? _notificationService;
    private readonly IReFixUpdateService? _refixUpdateService;
    private readonly IDepotBoxApiClient? _apiClient;
    private readonly IDepotBoxArchiveParser? _archiveParser;
    private readonly IPrerequisiteService? _prerequisiteService;
    private readonly ITagsService? _tagsService;
    private readonly IEmulatorLifecycleService? _emulatorLifecycleService;
    private readonly IBackgroundTaskService? _backgroundTaskService;
    private readonly ISteamStatusService? _steamStatusService;

    public Action? OnNavigateBack { get; set; }

    public InstanceDetailViewModel(
        IInstanceManager instanceManager,
        IDlcInstaller dlcInstaller,
        DownloadQueueManager downloadQueueManager,
        AppSettingsService appSettings,
        IEngineDetector engineDetector,
        IModManagerRegistry modManagerRegistry,
        IBepInExService bepInExService,
        IWorkshopService workshopService,
        IEmulatorRegistry emulatorRegistry,
        IEmulatorRatingService emulatorRatingService,
        IGameLauncher gameLauncher,
        ILogger<InstanceDetailViewModel> logger,
        INotificationService? notificationService = null,
        IReFixUpdateService? refixUpdateService = null,
        IDepotBoxApiClient? apiClient = null,
        IDepotBoxArchiveParser? archiveParser = null,
        IPrerequisiteService? prerequisiteService = null,
        IMetadataProvider? metadataProvider = null,
        ITagsService? tagsService = null,
        IEmulatorLifecycleService? emulatorLifecycleService = null,
        IBackgroundTaskService? backgroundTaskService = null,
        ISteamStatusService? steamStatusService = null)
    {
        _instanceManager = instanceManager;
        _dlcInstaller = dlcInstaller;
        _downloadQueueManager = downloadQueueManager;
        _appSettings = appSettings;
        _engineDetector = engineDetector;
        _modManagerRegistry = modManagerRegistry;
        _bepInExService = bepInExService;
        _workshopService = workshopService;
        _emulatorRegistry = emulatorRegistry;
        _emulatorRatingService = emulatorRatingService;
        _gameLauncher = gameLauncher;
        _logger = logger;
        _notificationService = notificationService;
        _refixUpdateService = refixUpdateService;
        _apiClient = apiClient;
        _archiveParser = archiveParser;
        _prerequisiteService = prerequisiteService;
        _metadataProvider = metadataProvider;
        _tagsService = tagsService;
        _emulatorLifecycleService = emulatorLifecycleService;
        _backgroundTaskService = backgroundTaskService;
        _steamStatusService = steamStatusService;
        _uiContext = SynchronizationContext.Current ?? new SynchronizationContext();

        _downloadQueueManager.Queue.CollectionChanged += OnQueueChanged;
        _gameLauncher.RunningStateChanged += OnGameRunningStateChanged;
        _gameLauncher.LogReceived += OnGameLogReceived;
    }

    private void OnQueueChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (Instance is null) return;
        ActiveJob = _downloadQueueManager.Queue.FirstOrDefault(j => j.Instance.Id == Instance.Id);
    }

    private void OnGameRunningStateChanged(object? sender, (Guid InstanceId, bool IsRunning) e)
    {
        if (Instance?.Id == e.InstanceId)
        {
            _uiContext.Post(_ =>
            {
                bool wasRunning = IsGameRunning;
                IsGameRunning = e.IsRunning;
                Instance = Instance with { Status = e.IsRunning ? InstanceStatus.Running : InstanceStatus.Ready };
                NotifyDownloadProps();

                // If game was running and just stopped, check if emulator feedback should be displayed
                if (wasRunning && !e.IsRunning)
                {
                    CheckAndPromptEmulatorFeedback();
                }
            }, null);
        }
    }

    private void CheckAndPromptEmulatorFeedback()
    {
        if (Instance == null) return;
        var isInstalled = ReFixEmulator.IsEmulatorInstalled(Instance.InstallPath) || !string.IsNullOrWhiteSpace(Instance.EmulatorId);
        if (!isInstalled) return;

        var optionId = Instance.EmulatorId ?? (InstalledEmulatorMode?.Contains("Goldberg", StringComparison.OrdinalIgnoreCase) == true ? "refix_goldberg" : "refix_valve");

        if (!_emulatorRatingService.HasUserVoted(Instance.Id, optionId))
        {
            FeedbackOptionId = optionId;
            FeedbackEmulatorName = InstalledEmulatorMode ?? (optionId == "refix_goldberg" ? "Re:Goldberg LAN" : "ReFix Online (Steam)");
            ShowFeedbackUninstallPrompt = false;
            IsFeedbackModalOpen = true;
        }
    }

    private void OnGameLogReceived(object? sender, (Guid InstanceId, string LogLine) e)
    {
        if (Instance?.Id == e.InstanceId)
        {
            _uiContext.Post(_ =>
            {
                ConsoleLogs.Add($"[{DateTime.Now:HH:mm:ss}] {e.LogLine}");
                if (ConsoleLogs.Count > 1000) ConsoleLogs.RemoveAt(0);
            }, null);
        }
    }

    partial void OnActiveJobChanged(DownloadJobItem? oldValue, DownloadJobItem? newValue)
    {
        if (oldValue is not null) oldValue.PropertyChanged -= OnActiveJobPropertyChanged;
        if (newValue is not null) newValue.PropertyChanged += OnActiveJobPropertyChanged;
        NotifyDownloadProps();
    }

    private void OnActiveJobPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DownloadJobItem.JobStatus) && ActiveJob?.JobStatus == DownloadJobStatus.Completed)
        {
            var downloadedDepotIds = ActiveJob.Instance.Depots.Select(d => d.DepotId).ToHashSet();
            foreach (var d in Depots)
            {
                if (downloadedDepotIds.Contains(d.Depot.DepotId))
                {
                    d.Depot = d.Depot with { IsDownloaded = true };
                    d.NotifyDownloadedChanged();
                }
            }
            foreach (var dlcItem in Dlcs)
            {
                var updatedDlcDepots = dlcItem.Dlc.Depots
                    .Select(d => downloadedDepotIds.Contains(d.DepotId) ? d with { IsDownloaded = true } : d)
                    .ToList();
                dlcItem.Dlc = dlcItem.Dlc with
                {
                    Depots = updatedDlcDepots.AsReadOnly(),
                    IsInstalled = dlcItem.Dlc.IsInstalled || (updatedDlcDepots.Count > 0 && updatedDlcDepots.All(d => d.IsDownloaded))
                };
                dlcItem.NotifyDownloadedChanged();
            }
        }

        NotifyDownloadProps();
    }

    private void NotifyDownloadProps()
    {
        OnPropertyChanged(nameof(IsInstanceDownloading));
        OnPropertyChanged(nameof(InstanceDownloadPercentage));
        OnPropertyChanged(nameof(InstanceDownloadLabel));
        OnPropertyChanged(nameof(HasActiveJob));
        OnPropertyChanged(nameof(ShowDownloadButton));
        OnPropertyChanged(nameof(IsInstalled));
        OnPropertyChanged(nameof(CanDeployEmulator));
        OnPropertyChanged(nameof(AreAllSelectedDepotsDownloaded));
        OnPropertyChanged(nameof(DownloadButtonText));
    }

    /// <summary>
    /// Loads details for the target game instance.
    /// </summary>
    public async Task LoadInstanceAsync(GameInstance instance)
    {
        try
        {
            var cleanGameName = CleanName(instance.Name) ?? instance.Name;

            bool isInstanceInstalled = instance.Status == InstanceStatus.Ready || instance.Status == InstanceStatus.Running;

            var depotList = (instance.Depots ?? []).Select(d => d with
            {
                Name = CleanName(d.Name) ?? d.Name,
                IsDownloaded = d.IsDownloaded || (isInstanceInstalled && string.Equals(d.Category, "Base Game", StringComparison.OrdinalIgnoreCase))
            }).ToList();

            if (depotList.Count == 0)
            {
                depotList.Add(new DepotInfo
                {
                    DepotId = instance.AppId,
                    Name = $"{cleanGameName} Content",
                    Category = "Base Game",
                    Platform = "Windows",
                    SizeBytes = 0,
                    IsDownloaded = isInstanceInstalled
                });
            }

            var cleanDlcs = (instance.Dlcs ?? []).Select(d =>
            {
                var dlcDepots = (d.Depots ?? []).Select(dep => dep with
                {
                    Name = CleanName(dep.Name) ?? dep.Name
                }).ToList().AsReadOnly();

                bool allDepotsDownloaded = dlcDepots.Count > 0 && dlcDepots.All(dep => dep.IsDownloaded);

                return d with
                {
                    Name = CleanName(d.Name) ?? d.Name,
                    Depots = dlcDepots,
                    IsInstalled = d.IsInstalled || allDepotsDownloaded
                };
            }).ToList().AsReadOnly();

            var installPath = PathHelper.EnsureGameSubfolder(instance.InstallPath, cleanGameName);

            // Engine detection if missing or generic
            var engine = instance.Engine;
            if ((engine == null || engine.Type == EngineType.Generic) && 
                !string.IsNullOrWhiteSpace(installPath) && 
                Directory.Exists(installPath) && 
                Directory.EnumerateFileSystemEntries(installPath).Any())
            {
                var detected = await _engineDetector.DetectEngineAsync(installPath, CancellationToken.None).ConfigureAwait(true);
                if (detected != null && (engine == null || detected.Type != EngineType.Generic))
                {
                    engine = detected;
                }
            }

            var exe = instance.ExecutablePath;
            if (string.IsNullOrWhiteSpace(exe) && !string.IsNullOrWhiteSpace(installPath))
            {
                exe = _engineDetector.FindPrimaryExecutable(installPath, cleanGameName);
            }

            Instance = instance with
            {
                Name = cleanGameName,
                InstallPath = installPath,
                Dlcs = cleanDlcs,
                Depots = depotList.AsReadOnly(),
                Engine = engine,
                ExecutablePath = exe
            };

            ConfiguredExecutablePath = Instance.ExecutablePath ?? string.Empty;
            InstanceAlias = Instance.Name ?? string.Empty;
            CustomLaunchArgs = Instance.LaunchArguments ?? string.Empty;
            IsUnityEngine = Instance.Engine?.Type == EngineType.Unity;

            var selectableDepots = Instance.Depots.Select(d => new SelectableDepotItem
            {
                Depot = d,
                IsSelected = IsDepotCompatibleWithCurrentOS(d),
                OnSelectionChanged = RecalculateSelectedSize
            }).ToList();

            var selectableDlcs = Instance.Dlcs.Select(d => new SelectableDlcItem
            {
                Dlc = d,
                IsSelected = true,
                OnSelectionChanged = RecalculateSelectedSize
            }).ToList();

            Depots = new ObservableCollection<SelectableDepotItem>(selectableDepots);
            Dlcs = new ObservableCollection<SelectableDlcItem>(selectableDlcs);

            RecalculateSelectedSize();

            // Check capabilities: all instances support generic mods/workshop and emulators
            SupportsMods = true;
            SupportsEmulation = true;

            // Load mods, BepInEx, Workshop, emulators, and prerequisites in background
            _ = LoadModsAsync();
            _ = LoadEmulatorsAsync();
            _ = ScanPrerequisitesAsync();
            if (IsUnityEngine) _ = CheckAndLoadBepInExAsync();
            _ = CheckWorkshopSupportAsync();

            if (Dlcs.Count > 0)
            {
                IsDlcUnlocked = await _dlcInstaller.IsDlcInstalledAsync(Instance, Dlcs[0].Dlc, CancellationToken.None).ConfigureAwait(true);
            }

            if (IsInstalled)
            {
                var depotDate = GetInstalledDepotReleaseDate(Instance);
                if (depotDate.HasValue)
                {
                    InstalledVersionDate = depotDate.Value;
                    InstalledVersionText = $"{depotDate.Value:d MMM yyyy}";
                }
                else if (!string.IsNullOrWhiteSpace(Instance.Metadata?.ReleaseDate))
                {
                    InstalledVersionText = Instance.Metadata.ReleaseDate;
                    if (DateTimeOffset.TryParse(Instance.Metadata.ReleaseDate, out var parsedRelDate))
                        InstalledVersionDate = parsedRelDate;
                }
                else
                {
                    InstalledVersionDate = Instance.UpdatedAt > DateTimeOffset.MinValue ? Instance.UpdatedAt : Instance.CreatedAt;
                    InstalledVersionText = $"{InstalledVersionDate:d MMM yyyy}";
                }
            }
            else
            {
                InstalledVersionDate = null;
                InstalledVersionText = "Not installed";
            }

            _ = CheckSteamVersionDateAsync();
            _ = LoadDlcsFromMetadataIfEmptyAsync();

            OnPropertyChanged(nameof(IsModsTabVisible));
            if (!IsModsTabVisible && SelectedTab == "Mods")
            {
                SelectedTab = "Overview";
            }

            IsGameRunning = _gameLauncher.IsRunning(Instance.Id);
            ActiveJob = _downloadQueueManager.Queue.FirstOrDefault(j => j.Instance.Id == Instance.Id);
            NotifyDownloadProps();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading game instance details");
            StatusMessage = $"❌ Error loading instance: {ex.Message}";
        }
    }

    private async Task LoadDlcsFromMetadataIfEmptyAsync()
    {
        if (Instance == null || Instance.AppId == 0 || Instance.AppId == 480 || _metadataProvider == null) return;
        if (Dlcs.Count > 0) return;

        try
        {
            var fetchedDlcs = await _metadataProvider.GetDlcListAsync(Instance.AppId, CancellationToken.None).ConfigureAwait(true);
            if (fetchedDlcs != null && fetchedDlcs.Count > 0)
            {
                var selectableDlcs = fetchedDlcs.Select(d => new SelectableDlcItem
                {
                    Dlc = d,
                    IsSelected = true,
                    OnSelectionChanged = RecalculateSelectedSize
                }).ToList();

                _uiContext.Post(async _ =>
                {
                    Dlcs = new ObservableCollection<SelectableDlcItem>(selectableDlcs);
                    Instance = Instance with { Dlcs = fetchedDlcs };
                    OnPropertyChanged(nameof(HeroTags));
                    RecalculateSelectedSize();

                    if (Dlcs.Count > 0)
                    {
                        IsDlcUnlocked = await _dlcInstaller.IsDlcInstalledAsync(Instance, Dlcs[0].Dlc, CancellationToken.None).ConfigureAwait(true);
                    }

                    try
                    {
                        await _instanceManager.UpdateAsync(Instance, CancellationToken.None).ConfigureAwait(false);
                    }
                    catch { }
                }, null);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load DLC list for {Name} ({AppId})", Instance.Name, Instance.AppId);
        }
    }

    private static DateTimeOffset? GetInstalledDepotReleaseDate(GameInstance instance)
    {
        if (instance == null) return null;

        var candidateDirs = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BlueStar", "instances", instance.Id.ToString(), "manifests"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BlueStar", "DepotWork", instance.Id.ToString()),
            Path.Combine(instance.InstallPath ?? string.Empty, ".DepotDownloader"),
            instance.InstallPath ?? string.Empty
        };

        DateTimeOffset? latestDepotDate = null;

        foreach (var dir in candidateDirs)
        {
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) continue;

            foreach (var depot in instance.Depots)
            {
                var manifestFiles = Directory.GetFiles(dir, $"*{depot.DepotId}*.manifest", SearchOption.TopDirectoryOnly)
                    .Concat(Directory.GetFiles(dir, $"*{depot.ManifestId}*.manifest", SearchOption.TopDirectoryOnly))
                    .Distinct();

                foreach (var mf in manifestFiles)
                {
                    var date = BlueStar.Infrastructure.Downloader.SteamManifestDateHelper.GetManifestCreationDate(mf);
                    if (date.HasValue && (latestDepotDate == null || date.Value > latestDepotDate.Value))
                    {
                        latestDepotDate = date.Value;
                    }
                }
            }

            if (latestDepotDate.HasValue)
                return latestDepotDate;
        }

        return null;
    }

    private async Task CheckSteamVersionDateAsync()
    {
        if (Instance == null || Instance.AppId == 0) return;
        try
        {
            if (_metadataProvider is BlueStar.Infrastructure.Metadata.SteamStoreApiClient steamClient)
            {
                var depotInfo = await steamClient.GetAppDepotInfoAsync(Instance.AppId, CancellationToken.None).ConfigureAwait(true);
                var latestDate = depotInfo?.LatestBuildDate ?? await steamClient.GetLatestAppUpdateDateAsync(Instance.AppId, CancellationToken.None).ConfigureAwait(true);

                if (latestDate.HasValue)
                {
                    LatestVersionDate = latestDate.Value;
                    LatestVersionText = $"{latestDate.Value:d MMM yyyy}";
                }
                else if (Instance.Metadata != null && !string.IsNullOrWhiteSpace(Instance.Metadata.ReleaseDate))
                {
                    if (DateTimeOffset.TryParse(Instance.Metadata.ReleaseDate, out var relDate))
                    {
                        LatestVersionDate = relDate;
                        LatestVersionText = Instance.Metadata.ReleaseDate;
                    }
                    else
                    {
                        LatestVersionText = Instance.Metadata.ReleaseDate;
                    }
                }

                if (IsInstalled && depotInfo != null && Instance.Depots.Count > 0)
                {
                    bool allDepotsMatch = true;
                    bool hasCheckedDepot = false;

                    foreach (var depot in Instance.Depots)
                    {
                        if (depotInfo.PublicManifests.TryGetValue(depot.DepotId, out var publicGid))
                        {
                            hasCheckedDepot = true;
                            if (depot.ManifestId != publicGid)
                            {
                                allDepotsMatch = false;
                                break;
                            }
                        }
                    }

                    if (hasCheckedDepot && allDepotsMatch)
                    {
                        // Installed version matches the latest Steam public release!
                        if (LatestVersionDate.HasValue)
                        {
                            InstalledVersionDate = LatestVersionDate.Value;
                            InstalledVersionText = LatestVersionText;
                        }
                        HasGameUpdateAvailable = false;
                    }
                    else if (hasCheckedDepot && !allDepotsMatch)
                    {
                        // Installed depot is older than current public Steam branch
                        HasGameUpdateAvailable = true;
                    }
                }
                else if (InstalledVersionDate.HasValue && LatestVersionDate.HasValue)
                {
                    if (LatestVersionDate.Value > InstalledVersionDate.Value.AddDays(1))
                    {
                        HasGameUpdateAvailable = true;
                    }
                    else
                    {
                        HasGameUpdateAvailable = false;
                    }
                }
            }
        }
        catch { }
    }

    /// <summary>
    /// Checks DepotBox API for updated depot manifests and opens the update modal.
    /// </summary>
    [RelayCommand]
    public async Task CheckAndOpenDepotUpdateModalAsync()
    {
        if (Instance == null || _apiClient == null) return;

        IsCheckingGameUpdate = true;
        StatusMessage = "🔍 Checking for depot updates on DepotBox...";

        try
        {
            var manifests = await _apiClient.GetManifestsAsync(Instance.AppId, CancellationToken.None).ConfigureAwait(true);
            if (manifests.Count == 0)
            {
                _notificationService?.ShowInfo(
                    "Update not available on DepotBox",
                    "A newer build was detected on Steam, but DepotBox does not have updated manifests uploaded for this game yet. Please check back later.");
                return;
            }

            var outdatedDepots = new List<DepotUpdateItem>();
            foreach (var man in manifests)
            {
                var local = Instance.Depots.FirstOrDefault(d => d.DepotId == man.DepotId);
                if (local == null || (local.ManifestId != man.ManifestId && man.ManifestId > 0))
                {
                    outdatedDepots.Add(new DepotUpdateItem
                    {
                        DepotId = man.DepotId,
                        Name = local?.Name ?? $"Depot {man.DepotId}",
                        CurrentManifestId = local?.ManifestId ?? 0,
                        NewManifestId = man.ManifestId,
                        SizeBytes = man.SizeBytes > 0 ? man.SizeBytes : (local?.SizeBytes ?? 0),
                        IsSelected = true
                    });
                }
            }

            if (outdatedDepots.Count > 0)
            {
                UpdateAvailableDepots = new ObservableCollection<DepotUpdateItem>(outdatedDepots);
                IsUpdateModalOpen = true;
            }
            else
            {
                _notificationService?.ShowInfo(
                    "Depots Up to Date",
                    "Depot manifests on DepotBox match the versions already installed on your instance. No new files pending download.");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to check depot updates for {Name}", Instance.Name);
            _notificationService?.ShowError("Failed to Check for Updates", ex.Message);
        }
        finally
        {
            IsCheckingGameUpdate = false;
        }
    }

    /// <summary>
    /// Closes the depot update modal.
    /// </summary>
    [RelayCommand]
    public void CloseUpdateModal()
    {
        IsUpdateModalOpen = false;
    }

    /// <summary>
    /// Confirms and downloads selected updated depots.
    /// </summary>
    [RelayCommand]
    public async Task ConfirmApplyDepotUpdateAsync()
    {
        if (Instance == null) return;

        if (IsGameRunning)
        {
            _notificationService?.ShowWarning("Game is Running", "Please close the game before installing the update.");
            return;
        }

        var selected = UpdateAvailableDepots.Where(d => d.IsSelected).ToList();
        if (selected.Count == 0)
        {
            _notificationService?.ShowWarning("No Selection", "Please select at least one depot to update.");
            return;
        }

        IsApplyingGameUpdate = true;
        StatusMessage = "📥 Downloading new manifests and update keys from DepotBox...";
        _notificationService?.ShowInfo("Downloading Update", "Fetching updated manifests and keys from DepotBox...");

        try
        {
            var workDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "BlueStar", "DepotWork", Instance.Id.ToString());
            Directory.CreateDirectory(workDir);

            var instanceManifestDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "BlueStar", "instances", Instance.Id.ToString(), "manifests");
            Directory.CreateDirectory(instanceManifestDir);

            string? archivePath = null;
            DepotBoxArchive? parsedArchive = null;

            if (_apiClient != null)
            {
                archivePath = await _apiClient.DownloadArchiveAsync(Instance.AppId, workDir, null, CancellationToken.None).ConfigureAwait(true);

                if (_archiveParser != null && File.Exists(archivePath))
                {
                    await _archiveParser.ExtractManifestsAsync(archivePath, instanceManifestDir, CancellationToken.None).ConfigureAwait(true);
                    await _archiveParser.ExtractManifestsAsync(archivePath, workDir, CancellationToken.None).ConfigureAwait(true);
                    parsedArchive = await _archiveParser.ParseAsync(archivePath, CancellationToken.None).ConfigureAwait(true);
                }
            }

            var allArchiveDepots = parsedArchive?.Games.SelectMany(g => g.Depots.Select(d => new { Depot = d, Game = g })).ToList() ?? [];

            // Update instance depots with new Manifest IDs and Keys
            var updatedDepots = Instance.Depots.Select(d =>
            {
                var sel = selected.FirstOrDefault(s => s.DepotId == d.DepotId);
                var archiveEntry = allArchiveDepots.FirstOrDefault(a => a.Depot.DepotId == d.DepotId);

                if (sel != null || archiveEntry != null)
                {
                    return d with
                    {
                        ManifestId = sel?.NewManifestId ?? archiveEntry?.Depot.ManifestId ?? d.ManifestId,
                        DepotKey = archiveEntry?.Game.DepotKey ?? d.DepotKey,
                        SizeBytes = (archiveEntry?.Depot.SizeBytes > 0 ? archiveEntry.Depot.SizeBytes : sel?.SizeBytes) ?? d.SizeBytes,
                        IsDownloaded = false
                    };
                }
                return d;
            }).ToList();

            var updatedInstance = Instance with
            {
                Depots = updatedDepots.AsReadOnly(),
                SourceArchivePath = archivePath ?? Instance.SourceArchivePath,
                UpdatedAt = DateTimeOffset.UtcNow
            };

            await _instanceManager.UpdateAsync(updatedInstance, CancellationToken.None).ConfigureAwait(true);
            Instance = updatedInstance;

            IsUpdateModalOpen = false;

            // Enqueue updated depots for download
            var downloadInstance = Instance with
            {
                Depots = selected.Select(s =>
                {
                    var updated = updatedDepots.FirstOrDefault(d => d.DepotId == s.DepotId);
                    return updated ?? new DepotInfo
                    {
                        DepotId = s.DepotId,
                        ManifestId = s.NewManifestId,
                        SizeBytes = s.SizeBytes,
                        Name = s.Name
                    };
                }).ToList().AsReadOnly()
            };

            _ = _downloadQueueManager.StartDownloadAsync(downloadInstance);
            _notificationService?.ShowSuccess(
                "Update Download Started",
                $"Enqueued {selected.Count} depot(s) to update {Instance.Name}. User mods, BepInEx, emulators, and save files will remain intact.");

            _ = CheckSteamVersionDateAsync();
            ActiveJob = _downloadQueueManager.Queue.FirstOrDefault(j => j.Instance.Id == Instance.Id);
            NotifyDownloadProps();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error starting depot update for {Name}", Instance.Name);
            _notificationService?.ShowError("Failed to Start Update", ex.Message);
        }
        finally
        {
            IsApplyingGameUpdate = false;
        }
    }

    [RelayCommand]
    public void SwitchTab(string tabName) => SelectedTab = tabName;

    // ── Launch Game Command ──
    [RelayCommand]
    public async Task LaunchGameAsync()
    {
        if (Instance == null) return;

        if (Instance.Status == InstanceStatus.NotInstalled && Instance.Origin != InstanceOrigin.Steam)
        {
            SelectedTab = "Files";
            return;
        }

        if (IsGameRunning)
        {
            StatusMessage = "Stopping game process...";
            await _gameLauncher.KillAsync(Instance.Id).ConfigureAwait(true);
            return;
        }

        // Check if emulator is online and Steam is not running
        var isOnlineEmulator = Instance.EmulatorEnabled &&
            (string.Equals(Instance.EmulatorId, "refix_valve", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(Instance.EmulatorId, "refix", StringComparison.OrdinalIgnoreCase));

        if (isOnlineEmulator && _steamStatusService != null && !_steamStatusService.CurrentStatus.IsRunning)
        {
            IsSteamRequiredModalOpen = true;
            return;
        }

        await LaunchGameInternalAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    public async Task StartSteamAndLaunchAsync()
    {
        IsStartingSteam = true;
        SteamLaunchStatusText = "Starting Steam and waiting for user profile to load...";
        StatusMessage = "⏳ Starting Steam and waiting for user profile to load...";

        try
        {
            if (_steamStatusService != null)
            {
                var progress = new Progress<string>(msg =>
                {
                    SteamLaunchStatusText = msg;
                    StatusMessage = $"⏳ {msg}";
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
                        Process.Start(new ProcessStartInfo(steamExe) { UseShellExecute = true });
                    else
                        Process.Start(new ProcessStartInfo("steam://open/main") { UseShellExecute = true });
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

        await LaunchGameInternalAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    public async Task LaunchAnywayAsync()
    {
        IsSteamRequiredModalOpen = false;
        await LaunchGameInternalAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    public void CloseSteamRequiredModal()
    {
        IsSteamRequiredModalOpen = false;
    }

    private async Task LaunchGameInternalAsync()
    {
        StatusMessage = "🚀 Launching game...";
        ConsoleLogs.Add($"[{DateTime.Now:HH:mm:ss}] Launching {Instance.Name}...");

        var result = await _gameLauncher.LaunchAsync(Instance, log =>
        {
            _uiContext.Post(_ => ConsoleLogs.Add($"[{DateTime.Now:HH:mm:ss}] {log}"), null);
        }, CancellationToken.None).ConfigureAwait(true);

        if (!result.Success)
        {
            StatusMessage = $"❌ {result.Message}";
            ConsoleLogs.Add($"[{DateTime.Now:HH:mm:ss}] Launch error: {result.Message}");
        }
        else
        {
            StatusMessage = "🎮 Game is running.";
        }
    }

    // ── DepotBox Association Workflow (ImportedFolder -> DepotBox) ──
    [RelayCommand]
    public async Task OpenDepotAssociationModalAsync()
    {
        if (Instance == null) return;
        IsAssociationModalOpen = true;
        IsSearchingDepotBoxCandidate = true;
        DepotBoxCandidate = null;
        AssociationSearchResults.Clear();
        AssociationSearchQuery = Instance.Name;

        try
        {
            if (_apiClient != null)
            {
                // 1. Try finding by AppID first if valid
                if (Instance.AppId > 0 && Instance.AppId != 480)
                {
                    var results = await _apiClient.SearchGamesAsync(Instance.AppId.ToString(), CancellationToken.None).ConfigureAwait(true);
                    var match = results.FirstOrDefault(r => r.AppId == Instance.AppId);
                    if (match != null)
                    {
                        DepotBoxCandidate = match;
                    }
                }

                // 2. Fallback to searching by game name
                if (DepotBoxCandidate == null && !string.IsNullOrWhiteSpace(Instance.Name))
                {
                    var results = await _apiClient.SearchGamesAsync(Instance.Name, CancellationToken.None).ConfigureAwait(true);
                    if (results.Count > 0)
                    {
                        DepotBoxCandidate = results[0];
                        foreach (var r in results) AssociationSearchResults.Add(r);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error searching DepotBox candidate for {Name}", Instance.Name);
        }
        finally
        {
            IsSearchingDepotBoxCandidate = false;
        }
    }

    [RelayCommand]
    public async Task SearchDepotBoxManuallyAsync()
    {
        if (string.IsNullOrWhiteSpace(AssociationSearchQuery) || _apiClient == null) return;

        IsSearchingDepotBoxCandidate = true;
        AssociationSearchResults.Clear();

        try
        {
            var results = await _apiClient.SearchGamesAsync(AssociationSearchQuery.Trim(), CancellationToken.None).ConfigureAwait(true);
            foreach (var r in results)
            {
                AssociationSearchResults.Add(r);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Manual DepotBox search failed for {Query}", AssociationSearchQuery);
        }
        finally
        {
            IsSearchingDepotBoxCandidate = false;
        }
    }

    [RelayCommand]
    public async Task ConfirmAssociationWithCandidateAsync()
    {
        if (DepotBoxCandidate != null)
        {
            await AssociateWithCandidateAsync(DepotBoxCandidate).ConfigureAwait(true);
        }
    }

    [RelayCommand]
    public async Task AssociateWithCandidateAsync(SearchResult? candidate)
    {
        if (candidate == null || Instance == null) return;

        IsProcessing = true;
        StatusMessage = $"⏳ Associating with DepotBox package '{candidate.Name}'...";

        try
        {
            var archivesDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "BlueStar", "archives");
            Directory.CreateDirectory(archivesDir);

            string? archivePath = null;
            if (_apiClient != null && candidate.AppId > 0)
            {
                try
                {
                    archivePath = await _apiClient.DownloadArchiveAsync(candidate.AppId, archivesDir, null, CancellationToken.None).ConfigureAwait(true);
                }
                catch { }
            }

            var updatedDepots = new List<DepotInfo>();
            var updatedDlcs = new List<DlcInfo>();

            if (!string.IsNullOrWhiteSpace(archivePath) && File.Exists(archivePath) && _archiveParser != null)
            {
                var parsed = await _archiveParser.ParseAsync(archivePath, CancellationToken.None).ConfigureAwait(true);
                ExtractManifestsToInstanceStorage(archivePath, Instance.Id);

                updatedDepots = parsed.Games.SelectMany(g => g.Depots.Select(d => new DepotInfo
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
                })).DistinctBy(d => d.DepotId).ToList();

                updatedDlcs = parsed.Games.Where(g => g.IsDlc).Select(dlc => new DlcInfo
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
                }).ToList();
            }

            var newAppId = candidate.AppId > 0 ? candidate.AppId : Instance.AppId;
            GameMetadata? meta = Instance.Metadata;
            if (_metadataProvider != null && newAppId > 0 && meta == null)
            {
                try
                {
                    meta = await _metadataProvider.GetMetadataAsync(newAppId, CancellationToken.None).ConfigureAwait(true);
                }
                catch { }
            }

            var updatedInstance = Instance with
            {
                AppId = newAppId,
                Metadata = meta,
                SourceArchivePath = archivePath ?? Instance.SourceArchivePath,
                Depots = updatedDepots.Count > 0 ? updatedDepots.AsReadOnly() : Instance.Depots,
                Dlcs = updatedDlcs.Count > 0 ? updatedDlcs.AsReadOnly() : Instance.Dlcs,
                IsDepotBoxAssociated = true
            };

            await _instanceManager.UpdateAsync(updatedInstance, CancellationToken.None).ConfigureAwait(true);
            await LoadInstanceAsync(updatedInstance).ConfigureAwait(true);

            IsAssociationModalOpen = false;
            _notificationService?.ShowSuccess("DepotBox Associated", $"{Instance.Name} is now linked to DepotBox!");
            StatusMessage = "✅ Associated with DepotBox successfully.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to associate instance with candidate");
            StatusMessage = $"❌ Association failed: {ex.Message}";
            _notificationService?.ShowError("Association Failed", ex.Message);
        }
        finally
        {
            IsProcessing = false;
        }
    }

    [RelayCommand]
    public void CloseAssociationModal()
    {
        IsAssociationModalOpen = false;
        DepotBoxCandidate = null;
        AssociationSearchResults.Clear();
    }

    // ── BepInEx Management for Unity ──
    [RelayCommand]
    public async Task CheckAndLoadBepInExAsync()
    {
        if (Instance == null || string.IsNullOrWhiteSpace(Instance.InstallPath)) return;

        IsLoadingBepInEx = true;
        try
        {
            IsBepInExInstalled = _bepInExService.IsInstalled(Instance.InstallPath);
            InstalledBepInExVersion = await _bepInExService.GetInstalledVersionAsync(Instance.InstallPath, CancellationToken.None).ConfigureAwait(true);

            var releases = await _bepInExService.GetAvailableReleasesAsync(false, CancellationToken.None).ConfigureAwait(true);
            AvailableBepInExVersions = new ObservableCollection<BepInExRelease>(releases);
            SelectedBepInExVersion = AvailableBepInExVersions.FirstOrDefault();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to inspect BepInEx status");
        }
        finally
        {
            IsLoadingBepInEx = false;
        }
    }

    [RelayCommand]
    public async Task InstallOrUpdateBepInExAsync()
    {
        if (Instance == null || string.IsNullOrWhiteSpace(Instance.InstallPath) || SelectedBepInExVersion == null)
        {
            StatusMessage = "⚠ Please select a BepInEx version.";
            return;
        }

        IsLoadingBepInEx = true;
        StatusMessage = $"⏳ Installing BepInEx {SelectedBepInExVersion.Version}...";

        try
        {
            var success = await _bepInExService.InstallAsync(Instance.InstallPath, SelectedBepInExVersion, null, CancellationToken.None).ConfigureAwait(true);
            if (success)
            {
                StatusMessage = $"✅ BepInEx {SelectedBepInExVersion.Version} installed successfully!";
                await CheckAndLoadBepInExAsync().ConfigureAwait(true);
                await LoadModsAsync().ConfigureAwait(true);
            }
            else
            {
                StatusMessage = "❌ Failed to install BepInEx.";
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to install BepInEx");
            StatusMessage = $"❌ Error: {ex.Message}";
        }
        finally
        {
            IsLoadingBepInEx = false;
        }
    }

    [RelayCommand]
    public async Task UninstallBepInExAsync()
    {
        if (Instance == null || string.IsNullOrWhiteSpace(Instance.InstallPath)) return;

        IsLoadingBepInEx = true;
        StatusMessage = "⏳ Uninstalling BepInEx...";

        try
        {
            var success = await _bepInExService.UninstallAsync(Instance.InstallPath, keepPluginsFolder: true, CancellationToken.None).ConfigureAwait(true);
            if (success)
            {
                StatusMessage = "✅ BepInEx uninstalled successfully (plugins folder preserved).";
                await CheckAndLoadBepInExAsync().ConfigureAwait(true);
                await LoadModsAsync().ConfigureAwait(true);
            }
            else
            {
                StatusMessage = "❌ Failed to uninstall BepInEx.";
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to uninstall BepInEx");
            StatusMessage = $"❌ Error: {ex.Message}";
        }
        finally
        {
            IsLoadingBepInEx = false;
        }
    }

    [RelayCommand]
    public void OpenBepInExFolder()
    {
        if (Instance == null || string.IsNullOrWhiteSpace(Instance.InstallPath)) return;
        var bepDir = Path.Combine(Instance.InstallPath, "BepInEx");
        if (Directory.Exists(bepDir))
        {
            Process.Start(new ProcessStartInfo { FileName = bepDir, UseShellExecute = true });
        }
    }

    // ── Steam Workshop Integration ──
    private async Task CheckWorkshopSupportAsync()
    {
        if (Instance == null || Instance.AppId == 0)
        {
            HasWorkshopSupport = false;
            return;
        }

        // Check if metadata categories or engine capabilities already explicitly declare Workshop
        bool metaSaysWorkshop = Instance.Metadata?.Categories?.Any(c => c.Contains("Workshop", StringComparison.OrdinalIgnoreCase)) == true;
        bool engineSaysWorkshop = Instance.Engine?.Supports(EngineCapabilities.WorkshopSupported) == true;

        if (metaSaysWorkshop || engineSaysWorkshop)
        {
            HasWorkshopSupport = true;
            return;
        }

        try
        {
            HasWorkshopSupport = await _workshopService.HasWorkshopSupportAsync(Instance.AppId, CancellationToken.None).ConfigureAwait(true);
        }
        catch
        {
            // Default to true for any valid Steam AppId so the user is never blocked from using Workshop
            HasWorkshopSupport = true;
        }
    }

    [RelayCommand]
    public void OpenSteamWorkshopInBrowser()
    {
        if (Instance == null || Instance.AppId == 0) return;
        var url = $"https://steamcommunity.com/app/{Instance.AppId}/workshop/";
        Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
    }

    [RelayCommand]
    public void OpenWorkshopModal()
    {
        WorkshopSearchInput = string.Empty;
        WorkshopPreviewItem = null;
        WorkshopStatusMessage = null;
        IsWorkshopStatusError = false;
        WorkshopDownloadProgress = 0;
        WorkshopDownloadStatusText = string.Empty;
        IsDownloadingWorkshopMod = false;
        IsWorkshopModalOpen = true;
    }

    [RelayCommand]
    public void CloseWorkshopModal()
    {
        IsWorkshopModalOpen = false;
        WorkshopPreviewItem = null;
        WorkshopStatusMessage = null;
        IsWorkshopStatusError = false;
        IsDownloadingWorkshopMod = false;
    }

    [RelayCommand]
    public async Task FetchWorkshopPreviewAsync()
    {
        WorkshopStatusMessage = null;
        IsWorkshopStatusError = false;

        var id = _workshopService.ParsePublishedFileId(WorkshopSearchInput);
        if (!id.HasValue)
        {
            WorkshopStatusMessage = "⚠ Invalid Steam Workshop URL or ID.";
            IsWorkshopStatusError = true;
            return;
        }

        IsLoadingWorkshopPreview = true;
        WorkshopStatusMessage = "🔍 Searching for addon details on Steam Workshop...";
        try
        {
            WorkshopPreviewItem = await _workshopService.GetItemDetailsAsync(id.Value, CancellationToken.None).ConfigureAwait(true);
            if (WorkshopPreviewItem == null)
            {
                WorkshopStatusMessage = "⚠ Addon not found or is private. Check the ID or link.";
                IsWorkshopStatusError = true;
            }
            else
            {
                WorkshopStatusMessage = null;
                IsWorkshopStatusError = false;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to preview Workshop item");
            WorkshopStatusMessage = $"❌ Error searching addon: {ex.Message}";
            IsWorkshopStatusError = true;
        }
        finally
        {
            IsLoadingWorkshopPreview = false;
        }
    }

    [RelayCommand]
    public async Task DownloadWorkshopModAsync()
    {
        if (WorkshopPreviewItem == null || Instance == null) return;

        IsDownloadingWorkshopMod = true;
        WorkshopDownloadProgress = 5;
        WorkshopDownloadStatusText = "⏳ Connecting to Steam Workshop...";
        WorkshopStatusMessage = null;
        IsWorkshopStatusError = false;

        try
        {
            var progress = new Progress<double>(p =>
            {
                WorkshopDownloadProgress = p;
                WorkshopDownloadStatusText = $"⏳ Downloading and installing addon... {p:F0}%";
            });

            var success = await _workshopService.DownloadAndInstallItemAsync(
                Instance,
                WorkshopPreviewItem.PublishedFileId,
                progress,
                CancellationToken.None).ConfigureAwait(true);

            if (success)
            {
                WorkshopDownloadProgress = 100;
                WorkshopDownloadStatusText = $"✅ Addon '{WorkshopPreviewItem.Title}' installed successfully!";
                StatusMessage = $"✅ Addon '{WorkshopPreviewItem.Title}' installed successfully.";
                await LoadModsAsync().ConfigureAwait(true);
                await Task.Delay(1400);
                IsWorkshopModalOpen = false;
            }
            else
            {
                WorkshopStatusMessage = "❌ Could not download addon from Steam Workshop.";
                IsWorkshopStatusError = true;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to download Workshop mod");
            WorkshopStatusMessage = $"❌ Failed to download: {ex.Message}";
            IsWorkshopStatusError = true;
        }
        finally
        {
            IsDownloadingWorkshopMod = false;
        }
    }

    // ── Mods Management ──
    [RelayCommand]
    public async Task LoadModsAsync()
    {
        if (Instance == null) return;
        ModsDirectoryPath = _workshopService.ResolveModDirectory(Instance);
        var manager = _modManagerRegistry.GetManagerForInstance(Instance);
        if (manager != null)
        {
            if (string.IsNullOrWhiteSpace(ModsDirectoryPath))
            {
                ModsDirectoryPath = manager.GetModsDirectory(Instance);
            }
            var mods = await manager.GetInstalledModsAsync(Instance, CancellationToken.None).ConfigureAwait(true);
            InstalledMods = new ObservableCollection<ModItem>(mods);
        }
    }

    [RelayCommand]
    public async Task InstallModFileAsync()
    {
        if (Instance == null) return;
        var manager = _modManagerRegistry.GetManagerForInstance(Instance);
        if (manager == null) return;

        var dialog = new OpenFileDialog
        {
            Title = "Select Mod File or Package",
            Filter = "Mod Files (*.zip, *.pak, *.dll)|*.zip;*.pak;*.dll;*.ucas;*.utoc|All Files (*.*)|*.*"
        };

        if (dialog.ShowDialog() != true) return;

        StatusMessage = "📦 Installing mod...";
        var success = await manager.InstallModAsync(Instance, dialog.FileName, CancellationToken.None).ConfigureAwait(true);
        if (success)
        {
            StatusMessage = "✅ Mod installed successfully.";
            await LoadModsAsync().ConfigureAwait(true);
        }
        else
        {
            StatusMessage = "❌ Failed to install mod.";
        }
    }

    [RelayCommand]
    public async Task ToggleModAsync(ModItem mod)
    {
        if (Instance == null || mod == null) return;
        var manager = _modManagerRegistry.GetManagerForInstance(Instance);
        if (manager == null) return;

        var newStatus = !mod.IsEnabled;
        await manager.ToggleModAsync(Instance, mod.Id, newStatus, CancellationToken.None).ConfigureAwait(true);
        await LoadModsAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    public async Task DeleteModAsync(ModItem mod)
    {
        if (Instance == null || mod == null) return;
        var manager = _modManagerRegistry.GetManagerForInstance(Instance);
        if (manager == null) return;

        await manager.UninstallModAsync(Instance, mod.Id, CancellationToken.None).ConfigureAwait(true);
        await LoadModsAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    public void OpenModsFolder()
    {
        if (!string.IsNullOrWhiteSpace(ModsDirectoryPath) && Directory.Exists(ModsDirectoryPath))
        {
            Process.Start(new ProcessStartInfo { FileName = ModsDirectoryPath, UseShellExecute = true });
        }
    }

    // ── Emulators Management ──
    [RelayCommand]
    public async Task LoadEmulatorsAsync()
    {
        if (Instance == null) return;

        try
        {
            SupportsEmulation = true;
            IsEmulatorInstalled = ReFixEmulator.IsEmulatorInstalled(Instance.InstallPath);
            InstalledEmulatorMode = ReFixEmulator.GetInstalledMode(Instance.InstallPath);

            if (_refixUpdateService != null)
            {
                CurrentGlobalReFixVersion = _refixUpdateService.GetCurrentInstalledVersion();
                InstanceReFixVersion = Instance.InstalledEmulatorVersion ?? "1.0";
                IsReFixUpdateAvailableForInstance = _refixUpdateService.IsInstanceReFixOutdated(Instance);
            }

            var options = await _emulatorRatingService.GetOptionsForInstanceAsync(Instance, CancellationToken.None).ConfigureAwait(true);
            AvailableEmulatorOptions = new ObservableCollection<EmulatorOptionInfo>(options);

            RecommendedEmulatorOption = options.FirstOrDefault(o => o.IsRecommended && o.TotalVotes >= 10);

            var emus = _emulatorRegistry.GetSupportedEmulators(Instance);
            AvailableEmulators = new ObservableCollection<IEmulator>(emus);
            SelectedEmulator = emus.FirstOrDefault(e => e.Id == (Instance.EmulatorId ?? "refix")) ?? emus.FirstOrDefault();

            if (SelectedEmulator != null)
            {
                EmulatorStatus = await SelectedEmulator.GetStatusAsync(Instance, CancellationToken.None).ConfigureAwait(true);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error loading emulator options for {Name}", Instance.Name);
        }
    }

    [RelayCommand]
    public async Task DeployEmulatorOptionAsync(EmulatorOptionInfo? option)
    {
        if (Instance == null || option == null || IsDeployingEmulator) return;

        if (!IsInstalled)
        {
            StatusMessage = "⚠ You must install or download the game before configuring an emulator.";
            _notificationService?.ShowWarning("Game Not Installed", "You must install or download the game before configuring an emulator.");
            return;
        }

        IsDeployingEmulator = true;
        IsDeployProgressVisible = true;
        DeployProgress = 0;
        DeployProgressMessage = $"Starting installation of {option.Name}...";
        StatusMessage = $"⏳ Installing {option.Name}...";

        try
        {
            var progressReporter = new Progress<DeployProgress>(p =>
            {
                DeployProgress = p.Percentage;
                DeployProgressMessage = p.Message;
                StatusMessage = $"⏳ {p.Message}";
            });

            bool success;
            if (_emulatorLifecycleService != null)
            {
                success = await _emulatorLifecycleService.DeployOrUpdateEmulatorWithDlcPreservationAsync(
                    Instance, option.Id, progressReporter, CancellationToken.None).ConfigureAwait(true);
            }
            else
            {
                var refix = _emulatorRegistry.GetById("refix") as ReFixEmulator
                    ?? new ReFixEmulator(_logger as ILogger<ReFixEmulator> ?? LoggerFactory.Create(_ => {}).CreateLogger<ReFixEmulator>());
                success = await refix.DeployOptionAsync(Instance, option.Id, progressReporter, CancellationToken.None).ConfigureAwait(true);
            }

            if (success)
            {
                var globalVer = _refixUpdateService?.GetCurrentInstalledVersion() ?? ReFixEmulator.GetCurrentVersion();
                var updatedInstance = Instance with
                {
                    EmulatorEnabled = true,
                    EmulatorId = option.Id,
                    InstalledEmulatorVersion = globalVer
                };

                var targetDir = Instance.InstallPath?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (!string.IsNullOrEmpty(targetDir))
                {
                    var deployPath = ReFixEmulator.GetReFixDeployPath();
                    if (deployPath != null)
                    {
                        var binDir = Path.Combine(deployPath, "bin");
                        var (_, _, gameExePath, _, _) = await ReFixEmulator.RunGameDetectorAsync(targetDir, binDir, Instance.ExecutablePath, CancellationToken.None).ConfigureAwait(true);
                        if (!string.IsNullOrEmpty(gameExePath) && File.Exists(gameExePath))
                        {
                            updatedInstance = updatedInstance with { ExecutablePath = gameExePath };
                        }
                    }
                }

                Instance = updatedInstance;
                await _instanceManager.UpdateAsync(Instance, CancellationToken.None).ConfigureAwait(true);
                StatusMessage = $"✅ {option.Name} installed successfully.";
                _notificationService?.ShowSuccess("Emulator Installed", $"{option.Name} (v{globalVer}) configured for {Instance.Name}.");
            }
            else
            {
                var deployPath = ReFixEmulator.GetReFixDeployPath();
                var reason = deployPath == null
                    ? "ReFix_deploy folder not found"
                    : $"Ensure the game is installed in: {Instance.InstallPath}";
                StatusMessage = $"❌ Failed to install {option.Name}. {reason}.";
                _notificationService?.ShowError("Installation Error", $"Could not install {option.Name} in {Instance.Name}. {reason}");
            }

            await LoadEmulatorsAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deploying emulator option {Option}", option.Id);
            StatusMessage = $"❌ Failed to install emulator: {ex.Message}";
            _notificationService?.ShowError("Emulator Error", ex.Message);
        }
        finally
        {
            IsDeployingEmulator = false;
            await Task.Delay(1200).ConfigureAwait(true);
            IsDeployProgressVisible = false;
        }
    }

    /// <summary>
    /// Updates the installed ReFix files in this instance to the latest suite version.
    /// </summary>
    [RelayCommand]
    public async Task UpdateInstanceReFixAsync()
    {
        if (Instance == null || IsDeployingEmulator) return;

        if (!IsInstalled)
        {
            StatusMessage = "⚠ You must install or download the game before updating the emulator.";
            _notificationService?.ShowWarning("Game Not Installed", "You must install or download the game before updating the emulator.");
            return;
        }

        IsDeployingEmulator = true;
        IsDeployProgressVisible = true;
        DeployProgress = 0;
        var globalVer = _refixUpdateService?.GetCurrentInstalledVersion() ?? ReFixEmulator.GetCurrentVersion();
        DeployProgressMessage = $"Updating ReFix to v{globalVer}...";
        StatusMessage = $"⏳ Updating ReFix to v{globalVer} in {Instance.Name}...";

        try
        {
            var optionId = Instance.EmulatorId ?? (InstalledEmulatorMode?.Contains("Goldberg", StringComparison.OrdinalIgnoreCase) == true ? "refix_goldberg" : "refix_valve");

            var progressReporter = new Progress<DeployProgress>(p =>
            {
                DeployProgress = p.Percentage;
                DeployProgressMessage = p.Message;
                StatusMessage = $"⏳ {p.Message}";
            });

            bool success;
            if (_emulatorLifecycleService != null)
            {
                success = await _emulatorLifecycleService.DeployOrUpdateEmulatorWithDlcPreservationAsync(
                    Instance, optionId, progressReporter, CancellationToken.None).ConfigureAwait(true);
            }
            else
            {
                var refix = _emulatorRegistry.GetById("refix") as ReFixEmulator
                    ?? new ReFixEmulator(_logger as ILogger<ReFixEmulator> ?? LoggerFactory.Create(_ => {}).CreateLogger<ReFixEmulator>());
                success = await refix.DeployOptionAsync(Instance, optionId, progressReporter, CancellationToken.None).ConfigureAwait(true);
            }

            if (success)
            {
                var updatedInstance = Instance with
                {
                    EmulatorEnabled = true,
                    EmulatorId = optionId,
                    InstalledEmulatorVersion = globalVer
                };

                var targetDir = Instance.InstallPath?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (!string.IsNullOrEmpty(targetDir))
                {
                    var deployPath = ReFixEmulator.GetReFixDeployPath();
                    if (deployPath != null)
                    {
                        var binDir = Path.Combine(deployPath, "bin");
                        var (_, _, gameExePath, _, _) = await ReFixEmulator.RunGameDetectorAsync(targetDir, binDir, Instance.ExecutablePath, CancellationToken.None).ConfigureAwait(true);
                        if (!string.IsNullOrEmpty(gameExePath) && File.Exists(gameExePath))
                        {
                            updatedInstance = updatedInstance with { ExecutablePath = gameExePath };
                        }
                    }
                }

                Instance = updatedInstance;
                await _instanceManager.UpdateAsync(Instance, CancellationToken.None).ConfigureAwait(true);

                if (_emulatorRatingService != null)
                {
                    try
                    {
                        await _emulatorRatingService.ResetRatingsForEmulatorAsync("refix", CancellationToken.None).ConfigureAwait(true);
                    }
                    catch { }
                }

                StatusMessage = $"✅ ReFix updated to v{globalVer} in {Instance.Name}.";
                _notificationService?.ShowSuccess("ReFix Updated", $"ReFix updated to v{globalVer} in {Instance.Name}.");
            }
            else
            {
                StatusMessage = "❌ Failed to update ReFix in this instance.";
                _notificationService?.ShowError("Update Error", $"Could not update ReFix in {Instance.Name}.");
            }

            await LoadEmulatorsAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating ReFix for {Name}", Instance.Name);
            StatusMessage = $"❌ Failed to update: {ex.Message}";
            _notificationService?.ShowError("Update Error", ex.Message);
        }
        finally
        {
            IsDeployingEmulator = false;
            await Task.Delay(1200).ConfigureAwait(true);
            IsDeployProgressVisible = false;
        }
    }

    [RelayCommand]
    public async Task UninstallEmulatorAsync()
    {
        if (Instance == null || IsDeployingEmulator) return;

        IsDeployingEmulator = true;
        StatusMessage = "⏳ Uninstalling emulator and restoring original files...";

        try
        {
            var progressReporter = new Progress<DeployProgress>(p =>
            {
                DeployProgress = p.Percentage;
                DeployProgressMessage = p.Message;
                StatusMessage = $"⏳ {p.Message}";
            });

            bool success;
            if (_emulatorLifecycleService != null)
            {
                success = await _emulatorLifecycleService.UninstallEmulatorWithDlcPreservationAsync(
                    Instance, progressReporter, CancellationToken.None).ConfigureAwait(true);
            }
            else
            {
                var refix = _emulatorRegistry.GetById("refix") as ReFixEmulator
                    ?? new ReFixEmulator(_logger as ILogger<ReFixEmulator> ?? LoggerFactory.Create(_ => {}).CreateLogger<ReFixEmulator>());
                success = await refix.UninstallAsync(Instance, CancellationToken.None).ConfigureAwait(true);
            }

            Instance = Instance with
            {
                EmulatorEnabled = false,
                EmulatorId = null,
                InstalledEmulatorVersion = null
            };
            await _instanceManager.UpdateAsync(Instance, CancellationToken.None).ConfigureAwait(true);

            StatusMessage = "✅ Emulator uninstalled. Original files restored.";
            _notificationService?.ShowInfo("Emulator Uninstalled", $"Original files restored in {Instance.Name}.");
            await LoadEmulatorsAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error uninstalling emulator for {Name}", Instance.Name);
            StatusMessage = $"❌ Failed to uninstall emulator: {ex.Message}";
        }
        finally
        {
            IsDeployingEmulator = false;
        }
    }

    [RelayCommand]
    public async Task ToggleEmulatorAsync()
    {
        if (Instance == null) return;
        if (IsEmulatorInstalled)
        {
            await UninstallEmulatorAsync().ConfigureAwait(true);
        }
        else if (!IsInstalled)
        {
            StatusMessage = "⚠ You must install or download the game before configuring an emulator.";
            _notificationService?.ShowWarning("Game Not Installed", "You must install or download the game before configuring an emulator.");
        }
        else if (RecommendedEmulatorOption != null)
        {
            await DeployEmulatorOptionAsync(RecommendedEmulatorOption).ConfigureAwait(true);
        }
    }

    [RelayCommand]
    public async Task SubmitFeedbackPositiveAsync()
    {
        if (Instance == null) return;
        await _emulatorRatingService.SubmitVoteAsync(Instance.AppId, FeedbackOptionId, true, CancellationToken.None).ConfigureAwait(true);
        await _emulatorRatingService.RecordUserVoteFlagAsync(Instance.Id, FeedbackOptionId, CancellationToken.None).ConfigureAwait(true);
        IsFeedbackModalOpen = false;
        StatusMessage = "👍 Thank you for your feedback! Upvote recorded in community statistics.";
        await LoadEmulatorsAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    public async Task SubmitFeedbackNegativeAsync()
    {
        if (Instance == null) return;
        await _emulatorRatingService.SubmitVoteAsync(Instance.AppId, FeedbackOptionId, false, CancellationToken.None).ConfigureAwait(true);
        await _emulatorRatingService.RecordUserVoteFlagAsync(Instance.Id, FeedbackOptionId, CancellationToken.None).ConfigureAwait(true);
        ShowFeedbackUninstallPrompt = true;
        await LoadEmulatorsAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    public async Task ConfirmUninstallAndTryAnotherAsync()
    {
        await UninstallEmulatorAsync().ConfigureAwait(true);
        IsFeedbackModalOpen = false;
        ShowFeedbackUninstallPrompt = false;
        SelectedTab = "Emulator";
    }

    [RelayCommand]
    public void CloseFeedbackModal()
    {
        IsFeedbackModalOpen = false;
        ShowFeedbackUninstallPrompt = false;
    }

    public static bool IsDepotCompatibleWithCurrentOS(DepotInfo depot)
    {
        if (depot == null) return false;
        var plat = (depot.Platform ?? "Universal").Trim();

        // Incompatible platforms on Windows
        if (plat.Equals("Linux", StringComparison.OrdinalIgnoreCase) ||
            plat.Equals("macOS", StringComparison.OrdinalIgnoreCase) ||
            plat.Equals("OSX", StringComparison.OrdinalIgnoreCase) ||
            plat.Equals("Ubuntu", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // Dedicated 32-bit filter on 64-bit Windows
        if (Environment.Is64BitOperatingSystem && string.Equals(depot.Architecture, "32-bit", StringComparison.OrdinalIgnoreCase))
        {
            if (depot.Name?.Contains("32-bit", StringComparison.OrdinalIgnoreCase) == true ||
                depot.Name?.Contains("Win32", StringComparison.OrdinalIgnoreCase) == true)
            {
                return false;
            }
        }

        return true;
    }

    // ── Settings Save & Duplication ──
    [RelayCommand]
    public async Task SaveSettingsAsync()
    {
        if (Instance == null) return;

        var customName = !string.IsNullOrWhiteSpace(InstanceAlias) ? InstanceAlias.Trim() : Instance.Name;

        Instance = Instance with
        {
            Name = customName,
            ExecutablePath = ConfiguredExecutablePath,
            LaunchArguments = CustomLaunchArgs
        };

        await _instanceManager.UpdateAsync(Instance, CancellationToken.None).ConfigureAwait(true);
        StatusMessage = $"✅ Configuración y alias guardados correctamente: '{customName}'";
    }

    [RelayCommand]
    public async Task DuplicateInstanceAsync()
    {
        if (Instance == null || IsDuplicatingInstance) return;

        IsDuplicatingInstance = true;
        try
        {
            var baseName = !string.IsNullOrWhiteSpace(InstanceAlias) ? InstanceAlias.Trim() : Instance.Name;
            var cloneName = $"{baseName} (Clon)";

            StatusMessage = $"⏳ Duplicando instancia '{baseName}' con enlace NTFS Zero-Copy...";
            var cloned = await _instanceManager.CloneInstanceAsync(Instance.Id, cloneName, CancellationToken.None).ConfigureAwait(true);

            StatusMessage = $"✅ Instancia duplicada con éxito: '{cloned.Name}'";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error al duplicar la instancia {Id}", Instance.Id);
            StatusMessage = $"❌ Error al duplicar instancia: {ex.Message}";
        }
        finally
        {
            IsDuplicatingInstance = false;
        }
    }

    [RelayCommand]
    public async Task DuplicateCleanInstanceAsync()
    {
        if (Instance == null || IsDuplicatingInstance) return;

        IsDuplicatingInstance = true;
        try
        {
            var baseName = !string.IsNullOrWhiteSpace(InstanceAlias) ? InstanceAlias.Trim() : Instance.Name;
            var cleanName = $"{baseName} (Limpia)";

            StatusMessage = $"⏳ Creando instancia limpia para '{baseName}'...";

            var baseDepot = _instanceManager.GetBaseDepotPath(Instance.AppId);
            if (Directory.Exists(baseDepot))
            {
                var cleanInstance = await _instanceManager.CreateInstanceFromDepotAsync(
                    Instance.AppId,
                    cleanName,
                    baseDepot,
                    ct: CancellationToken.None).ConfigureAwait(true);

                StatusMessage = $"✨ Instancia limpia creada desde base depot: '{cleanInstance.Name}'";
            }
            else
            {
                // Zero-copy clone game binaries and reset isolated configs/mods
                var cloned = await _instanceManager.CloneInstanceAsync(Instance.Id, cleanName, CancellationToken.None).ConfigureAwait(true);
                
                try
                {
                    var modsDir = Path.Combine(cloned.InstallPath, "mods");
                    if (Directory.Exists(modsDir))
                    {
                        foreach (var d in Directory.GetDirectories(modsDir))
                        {
                            try { Directory.Delete(d, true); } catch { }
                        }
                    }

                    var bepPlugins = Path.Combine(cloned.InstallPath, "BepInEx", "plugins");
                    if (Directory.Exists(bepPlugins))
                    {
                        foreach (var d in Directory.GetDirectories(bepPlugins))
                        {
                            try { Directory.Delete(d, true); } catch { }
                        }
                    }
                }
                catch { }

                StatusMessage = $"✨ Instancia limpia creada con éxito: '{cloned.Name}'";
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error al crear instancia limpia para {Id}", Instance.Id);
            StatusMessage = $"❌ Error al crear instancia limpia: {ex.Message}";
        }
        finally
        {
            IsDuplicatingInstance = false;
        }
    }

    // ── Logs Console ──
    [RelayCommand]
    public void ClearLogs() => ConsoleLogs.Clear();

    [RelayCommand]
    public void CopyLogs()
    {
        var fullLog = string.Join(Environment.NewLine, ConsoleLogs);
        Clipboard.SetText(fullLog);
        StatusMessage = "📋 Logs copied to clipboard.";
    }

    private void RecalculateSelectedSize()
    {
        long depotSum = Depots.Where(d => d.IsSelected).Sum(d => d.Depot.SizeBytes);
        long dlcSum = Dlcs.Where(d => d.IsSelected).Sum(d => d.Dlc.TotalSizeBytes);
        long total = depotSum + dlcSum;

        TotalSelectedSizeBytes = total;
        TotalSelectedSizeFormatted = total switch
        {
            > 1024 * 1024 * 1024 => $"{total / (1024.0 * 1024.0 * 1024.0):F2} GB",
            > 1024 * 1024 => $"{total / (1024.0 * 1024.0):F1} MB",
            > 0 => $"{total / 1024.0:F0} KB",
            _ => "0 KB"
        };

        NotifyDownloadProps();
    }

    [RelayCommand]
    private void SelectAllDepots()
    {
        foreach (var depot in Depots) depot.IsSelected = true;
        RecalculateSelectedSize();
    }

    [RelayCommand]
    private void DeselectAllDepots()
    {
        foreach (var depot in Depots) depot.IsSelected = false;
        RecalculateSelectedSize();
    }

    [RelayCommand]
    private void SelectAllDlcs()
    {
        foreach (var dlc in Dlcs) dlc.IsSelected = true;
        RecalculateSelectedSize();
    }

    [RelayCommand]
    private void DeselectAllDlcs()
    {
        foreach (var dlc in Dlcs) dlc.IsSelected = false;
        RecalculateSelectedSize();
    }

    [RelayCommand]
    private async Task StartDownloadAsync()
    {
        if (Instance is null) return;

        // Check if manifests or source archive exist
        var instanceManifestDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "BlueStar", "instances", Instance.Id.ToString(), "manifests");

        bool hasValidManifests = Directory.Exists(instanceManifestDir) &&
            Directory.GetFiles(instanceManifestDir, "*.manifest").Any(f => new FileInfo(f).Length > 32);

        bool hasSourceArchive = !string.IsNullOrWhiteSpace(Instance.SourceArchivePath) && File.Exists(Instance.SourceArchivePath);

        if (!hasValidManifests && !hasSourceArchive && Instance.AppId > 0 && _apiClient is not null && _archiveParser is not null)
        {
            StatusMessage = "⏳ Fetching manifests and archive from DepotBox...";
            var archivesDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "BlueStar", "archives");
            Directory.CreateDirectory(archivesDir);

            try
            {
                var archivePath = await _apiClient.DownloadArchiveAsync(Instance.AppId, archivesDir, null, CancellationToken.None).ConfigureAwait(true);
                if (File.Exists(archivePath))
                {
                    var archive = await _archiveParser.ParseAsync(archivePath, CancellationToken.None).ConfigureAwait(true);
                    ExtractManifestsToInstanceStorage(archivePath, Instance.Id);

                    var updatedDepots = archive.Games.SelectMany(g => g.Depots.Select(d => new DepotInfo
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
                    })).DistinctBy(d => d.DepotId).ToList();

                    var updatedDlcs = archive.Games.Where(g => g.IsDlc).Select(dlc => new DlcInfo
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
                    }).ToList();

                    var updatedInstance = Instance with
                    {
                        SourceArchivePath = archivePath,
                        Depots = updatedDepots.AsReadOnly(),
                        Dlcs = updatedDlcs.AsReadOnly()
                    };

                    await _instanceManager.UpdateAsync(updatedInstance, CancellationToken.None).ConfigureAwait(true);
                    await LoadInstanceAsync(updatedInstance).ConfigureAwait(true);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not automatically download DepotBox archive for {AppId}", Instance.AppId);
            }
        }

        var combinedDepots = GetSelectedCombinedDepots();
        if (combinedDepots.Count == 0)
        {
            StatusMessage = "⚠ Please select at least one depot or DLC to download.";
            return;
        }

        var downloadInstance = Instance with { Depots = combinedDepots.AsReadOnly() };
        StatusMessage = "📥 Download queued...";
        _ = _downloadQueueManager.StartDownloadAsync(downloadInstance);

        ActiveJob = _downloadQueueManager.Queue.FirstOrDefault(j => j.Instance.Id == Instance.Id);
        NotifyDownloadProps();
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

    [RelayCommand]
    private async Task PauseDownloadAsync()
    {
        if (Instance is null) return;
        await _downloadQueueManager.PauseAsync(Instance.Id).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task ResumeDownloadAsync()
    {
        if (Instance is null) return;
        await _downloadQueueManager.ResumeAsync(Instance.Id).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task CancelDownloadAsync()
    {
        if (Instance is null) return;
        await _downloadQueueManager.CancelAsync(Instance.Id).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task RetryDownloadAsync()
    {
        if (Instance is null) return;
        await _downloadQueueManager.RetryAsync(Instance.Id).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task UnlockDlcsAsync()
    {
        if (Dlcs.Count == 0 && !IsDlcUnlocked)
        {
            IsDlcWarningModalOpen = true;
            return;
        }

        await ExecuteToggleDlcUnlockerInternalAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    public async Task ConfirmUnlockDlcsAsync()
    {
        IsDlcWarningModalOpen = false;
        await ExecuteToggleDlcUnlockerInternalAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    public void CancelUnlockDlcs()
    {
        IsDlcWarningModalOpen = false;
        StatusMessage = "ℹ DLC Unlocker installation cancelled.";
    }

    private async Task ExecuteToggleDlcUnlockerInternalAsync()
    {
        IsProcessing = true;
        StatusMessage = "⏳ Processing DLC unlocker...";

        var dispatcher = Application.Current?.Dispatcher;
        var progress = new Progress<string>(msg =>
        {
            if (dispatcher is not null && !dispatcher.CheckAccess())
                dispatcher.Invoke(() => StatusMessage = msg);
            else
                StatusMessage = msg;
        });

        try
        {
            var targetDlc = Dlcs.FirstOrDefault(d => d.IsSelected)?.Dlc
                ?? (Dlcs.Count > 0 ? Dlcs[0].Dlc : new DlcInfo { AppId = Instance.AppId, Name = "Generic DLC Wrapper", Depots = [], IsInstalled = false });

            if (IsDlcUnlocked)
            {
                StatusMessage = "⏳ Removing DLC unlocker...";
                var success = await _dlcInstaller
                    .UninstallDlcAsync(Instance, targetDlc, CancellationToken.None, progress)
                    .ConfigureAwait(true);
                IsDlcUnlocked = !success;
                if (success)
                {
                    Instance = Instance with { DlcUnlockerInstalled = false, UnlockedDlcIds = [] };
                    await _instanceManager.UpdateAsync(Instance, CancellationToken.None).ConfigureAwait(true);
                    _notificationService?.ShowInfo("DLC Unlocker Uninstalled", $"DLC wrapper was successfully removed for {Instance.Name}.");
                    StatusMessage = "✅ DLC unlocker uninstalled successfully.";
                }
            }
            else
            {
                StatusMessage = "⏳ Installing DLC unlocker...";
                var success = await _dlcInstaller
                    .InstallDlcAsync(Instance, targetDlc, CancellationToken.None, progress)
                    .ConfigureAwait(true);
                IsDlcUnlocked = success;
                if (success)
                {
                    var selectedIds = Dlcs.Where(d => d.IsSelected).Select(d => d.Dlc.AppId).ToList();
                    Instance = Instance with { DlcUnlockerInstalled = true, UnlockedDlcIds = selectedIds };
                    await _instanceManager.UpdateAsync(Instance, CancellationToken.None).ConfigureAwait(true);
                    _notificationService?.ShowSuccess("DLC Unlocker Installed", $"DLC Unlocker configured successfully for {Instance.Name}.");
                    StatusMessage = "✅ DLC unlocker installed successfully.";
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to toggle DLC unlocker");
            StatusMessage = $"❌ Error: {ex.Message}";
        }
        finally
        {
            IsProcessing = false;
        }
    }

    [RelayCommand]
    private async Task ChangeInstallPathAsync()
    {
        var initialDir = Directory.Exists(Instance.InstallPath)
            ? Instance.InstallPath
            : Directory.Exists(_appSettings.LastInstallDirectory)
                ? _appSettings.LastInstallDirectory
                : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        var dialog = new OpenFolderDialog
        {
            Title = "Select Game Installation Directory",
            InitialDirectory = initialDir
        };

        if (dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.FolderName))
        {
            var targetPath = PathHelper.EnsureGameSubfolder(dialog.FolderName, Instance.Name);
            Instance = Instance with { InstallPath = targetPath };
            await _instanceManager.UpdateAsync(Instance, CancellationToken.None).ConfigureAwait(true);
            await _appSettings.SetLastInstallDirectoryAsync(dialog.FolderName).ConfigureAwait(true);
            StatusMessage = $"✅ Installation directory updated: {targetPath}";
        }
    }

    [RelayCommand]
    private void OpenFolder()
    {
        if (!string.IsNullOrWhiteSpace(Instance.InstallPath) && Directory.Exists(Instance.InstallPath))
        {
            Process.Start(new ProcessStartInfo { FileName = Instance.InstallPath, UseShellExecute = true });
        }
        else
        {
            StatusMessage = "⚠ Folder does not exist yet.";
        }
    }

    [RelayCommand]
    private void OpenShortcutModal()
    {
        if (Instance is null) return;

        ShortcutStatusMessage = null;
        ShowRestartSteamButton = false;
        ShortcutName = Instance.Name ?? "Game";

        IsSteamInstalled = ShortcutHelper.IsSteamInstalled();
        CreateSteamShortcut = IsSteamInstalled;

        var exes = ShortcutHelper.FindGameExecutables(Instance.InstallPath ?? string.Empty, Instance.Name);
        AvailableExecutables = new ObservableCollection<string>(exes);
        SelectedExecutable = AvailableExecutables.FirstOrDefault();

        IsShortcutModalOpen = true;
    }

    [RelayCommand]
    private void CloseShortcutModal()
    {
        IsShortcutModalOpen = false;
        ShortcutStatusMessage = null;
        ShowRestartSteamButton = false;
    }

    [RelayCommand]
    private void BrowseExecutable()
    {
        var initialDir = Directory.Exists(Instance.InstallPath)
            ? Instance.InstallPath
            : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        var dialog = new OpenFileDialog
        {
            Title = "Select Game Executable",
            Filter = "Executable Files (*.exe)|*.exe|All Files (*.*)|*.*",
            InitialDirectory = initialDir
        };

        if (dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.FileName))
        {
            if (!AvailableExecutables.Contains(dialog.FileName))
            {
                AvailableExecutables.Insert(0, dialog.FileName);
            }
            SelectedExecutable = dialog.FileName;
            ConfiguredExecutablePath = dialog.FileName;
        }
    }

    [RelayCommand]
    private async Task CreateShortcutAsync()
    {
        if (string.IsNullOrWhiteSpace(SelectedExecutable))
        {
            ShortcutStatusMessage = "⚠ Please select or browse for a game executable (.exe).";
            IsShortcutStatusSuccess = false;
            return;
        }

        if (!CreateDesktopShortcut && !CreateStartMenuShortcut && !CreateSteamShortcut)
        {
            ShortcutStatusMessage = "⚠ Please check at least one location (Desktop, Start Menu, or Steam).";
            IsShortcutStatusSuccess = false;
            return;
        }

        ShortcutStatusMessage = "⏳ Creating shortcuts & downloading artwork...";
        IsShortcutStatusSuccess = true;

        var result = await ShortcutHelper.CreateShortcutsAsync(
            SelectedExecutable,
            ShortcutName,
            CreateDesktopShortcut,
            CreateStartMenuShortcut,
            CreateSteamShortcut,
            originalGameAppId: Instance?.AppId ?? 0,
            customHeaderUrl: Instance?.Metadata?.HeaderImageUrl,
            customCapsuleUrl: Instance?.Metadata?.CapsuleImageUrl);

        ShortcutStatusMessage = result.Message;
        IsShortcutStatusSuccess = result.Success;
        ShowRestartSteamButton = result.Success && CreateSteamShortcut;
    }

    [RelayCommand]
    private async Task RestartSteamAsync()
    {
        ShortcutStatusMessage = "⏳ Restarting Steam client...";
        IsShortcutStatusSuccess = true;

        var success = await Task.Run(() => ShortcutHelper.RestartSteam());

        if (success)
        {
            ShortcutStatusMessage = "✅ Steam restarted successfully! Your game and artwork are now loaded in your library.";
            IsShortcutStatusSuccess = true;
            ShowRestartSteamButton = false;
        }
        else
        {
            ShortcutStatusMessage = "⚠ Could not restart Steam automatically. Please launch Steam manually.";
            IsShortcutStatusSuccess = false;
        }
    }

    [RelayCommand]
    private void GoBack() => OnNavigateBack?.Invoke();

    // ── Instance Deletion Commands ──
    [RelayCommand]
    public void OpenDeleteModal() => IsDeleteModalOpen = true;

    [RelayCommand]
    public void CloseDeleteModal() => IsDeleteModalOpen = false;

    [RelayCommand]
    public async Task ConfirmDeleteInstanceAsync(string deleteFilesOption)
    {
        if (Instance == null) return;

        bool deleteFiles = string.Equals(deleteFilesOption, "true", StringComparison.OrdinalIgnoreCase) ||
                           string.Equals(deleteFilesOption, "with_data", StringComparison.OrdinalIgnoreCase);

        IsDeletingInstance = true;
        StatusMessage = deleteFiles
            ? "🗑 Deleting instance and purging game files..."
            : "🗑 Unlinking instance from BlueStar...";

        try
        {
            if (deleteFiles && !string.IsNullOrWhiteSpace(Instance.InstallPath) && Directory.Exists(Instance.InstallPath))
            {
                var fullPath = Path.GetFullPath(Instance.InstallPath);
                var root = Path.GetPathRoot(fullPath);

                // Safety guard: do not delete root of drive or short paths
                if (!string.Equals(fullPath, root, StringComparison.OrdinalIgnoreCase) && fullPath.Length > 4)
                {
                    _logger.LogInformation("Deleting game install directory on disk: {Path}", fullPath);
                    Directory.Delete(fullPath, recursive: true);
                }
            }

            await _instanceManager.DeleteAsync(Instance.Id, CancellationToken.None).ConfigureAwait(true);
            _logger.LogInformation("Deleted instance {Name} ({Id}) from BlueStar (DeletedFiles={DeletedFiles})", Instance.Name, Instance.Id, deleteFiles);

            IsDeleteModalOpen = false;
            IsDeletingInstance = false;

            OnNavigateBack?.Invoke();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete instance {Name}", Instance.Name);
            StatusMessage = $"❌ Failed to delete instance: {ex.Message}";
            IsDeletingInstance = false;
        }
    }

    #region Prerequisites Management

    /// <summary>
    /// Scans the host system and game directory for prerequisite runtimes.
    /// </summary>
    [RelayCommand]
    public async Task ScanPrerequisitesAsync()
    {
        if (Instance == null || _prerequisiteService == null) return;

        IsScanningPrerequisites = true;
        try
        {
            var items = await _prerequisiteService.DetectPrerequisitesAsync(Instance, CancellationToken.None).ConfigureAwait(true);
            Prerequisites = new ObservableCollection<PrerequisiteItem>(items);
            _logger.LogInformation("Scanned prerequisites for {Name}: found {Count} components", Instance.Name, items.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error scanning prerequisites for {Name}", Instance.Name);
        }
        finally
        {
            IsScanningPrerequisites = false;
        }
    }

    /// <summary>
    /// Installs an individual prerequisite runtime.
    /// </summary>
    [RelayCommand]
    public async Task InstallPrerequisiteAsync(PrerequisiteItem? item)
    {
        if (Instance == null || item == null || _prerequisiteService == null || IsInstallingPrerequisites) return;

        IsInstallingPrerequisites = true;
        StatusMessage = $"⏳ Installing {item.Name}...";
        var progress = new Progress<string>(msg =>
        {
            StatusMessage = msg;
            _uiContext.Post(_ => ConsoleLogs.Add($"[{DateTime.Now:HH:mm:ss}] {msg}"), null);
        });

        try
        {
            var success = await _prerequisiteService.InstallPrerequisiteAsync(Instance, item, progress, CancellationToken.None).ConfigureAwait(true);
            if (success)
            {
                StatusMessage = $"✅ {item.Name} installed successfully.";
                _notificationService?.ShowSuccess("Prerequisite Installed", $"{item.Name} installed successfully.");
            }
            else
            {
                StatusMessage = $"❌ Failed to install {item.Name}.";
                _notificationService?.ShowError("Prerequisite Error", $"Could not install {item.Name}.");
            }

            await ScanPrerequisitesAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error installing prerequisite {Name}", item.Name);
            StatusMessage = $"❌ Error: {ex.Message}";
        }
        finally
        {
            IsInstallingPrerequisites = false;
        }
    }

    /// <summary>
    /// Installs all missing or available game prerequisites in 1-click.
    /// </summary>
    [RelayCommand]
    public async Task InstallAllPrerequisitesAsync()
    {
        if (Instance == null || _prerequisiteService == null || IsInstallingPrerequisites) return;

        IsInstallingPrerequisites = true;
        StatusMessage = "⏳ Installing all prerequisites in 1-click...";
        var progress = new Progress<string>(msg =>
        {
            StatusMessage = msg;
            _uiContext.Post(_ => ConsoleLogs.Add($"[{DateTime.Now:HH:mm:ss}] {msg}"), null);
        });

        try
        {
            int count = await _prerequisiteService.InstallAllPrerequisitesAsync(Instance, progress, CancellationToken.None).ConfigureAwait(true);
            StatusMessage = $"✅ {count} prerequisite(s) configured successfully.";
            _notificationService?.ShowSuccess("Prerequisites Ready", $"Verified and installed required components for {Instance.Name}.");
            await ScanPrerequisitesAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error installing all prerequisites for {Name}", Instance.Name);
            StatusMessage = $"❌ Failed to install prerequisites: {ex.Message}";
        }
        finally
        {
            IsInstallingPrerequisites = false;
        }
    }

    #endregion

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
