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
using BlueStar.Infrastructure.Mods;
using BlueStar.Infrastructure.Services;
using BlueStar.Infrastructure.Storage;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace BlueStar.App.ViewModels;

/// <summary>
/// Model for editing a depot manifest in custom build configurations.
/// </summary>
public partial class CustomDepotManifestItem : ObservableObject
{
    public uint DepotId { get; init; }
    public string DepotName { get; init; } = string.Empty;
    public ulong CurrentManifestId { get; init; }

    [ObservableProperty]
    private string _manifestIdText = string.Empty;

    [ObservableProperty]
    private string? _statusText;
}

/// <summary>
/// Model for a depot item with selection state in the UI.
/// </summary>
public partial class SelectableDepotItem : ObservableObject
{
    public Action? OnSelectionChanged { get; set; }
    public Action<uint, ulong>? OnManifestUpdated { get; set; }

    [ObservableProperty]
    private DepotInfo _depot = null!;

    [ObservableProperty]
    private bool _isSelected = true;

    [ObservableProperty]
    private bool _isEditingManifest;

    [ObservableProperty]
    private string _editingManifestId = string.Empty;

    partial void OnIsSelectedChanged(bool value) => OnSelectionChanged?.Invoke();

    public bool IsDownloaded => Depot?.IsDownloaded ?? false;

    public void NotifyDownloadedChanged() => OnPropertyChanged(nameof(IsDownloaded));

    public string ManifestIdText => Depot?.ManifestId > 0 ? Depot.ManifestId.ToString() : "Latest";

    [RelayCommand]
    public void StartEditManifest()
    {
        EditingManifestId = Depot?.ManifestId > 0 ? Depot.ManifestId.ToString() : string.Empty;
        IsEditingManifest = true;
    }

    [RelayCommand]
    public void CancelEditManifest()
    {
        IsEditingManifest = false;
    }

    [RelayCommand]
    public void SaveEditManifest()
    {
        if (ulong.TryParse(EditingManifestId?.Trim(), out var parsedId) && parsedId > 0 && parsedId != Depot.ManifestId)
        {
            Depot = Depot with { ManifestId = parsedId, IsDownloaded = false };
            NotifyDownloadedChanged();
            OnPropertyChanged(nameof(ManifestIdText));
            OnManifestUpdated?.Invoke(Depot.DepotId, parsedId);
        }
        IsEditingManifest = false;
    }

    public string FormattedSize => Depot.SizeBytes switch
    {
        > 1024 * 1024 * 1024 => $"{Depot.SizeBytes / (1024.0 * 1024.0 * 1024.0):F2} GB",
        > 1024 * 1024 => $"{Depot.SizeBytes / (1024.0 * 1024.0):F1} MB",
        _ => $"{Depot.SizeBytes / 1024.0:F0} KB"
    };

    /// <summary>
    /// Returns the best available display name: SteamDB name → DepotBox name → fallback "Depot {id}".
    /// </summary>
    public string DisplayName
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(Depot.SteamDbName))
                return Depot.SteamDbName;
            if (!string.IsNullOrWhiteSpace(Depot.Name) && !Depot.Name.StartsWith("Depot "))
                return Depot.Name;
            return $"Depot {Depot.DepotId}";
        }
    }

    public string CategoryTag => string.IsNullOrWhiteSpace(Depot.Category) ? "Base Game" : Depot.Category;
    public string PlatformTag => string.IsNullOrWhiteSpace(Depot.Platform) ? "Universal" : Depot.Platform;
    public string? ArchitectureTag => Depot.Architecture;

    /// <summary>True when Steam auto-installs this depot for the current OS (base game content).</summary>
    public bool IsRecommended => Depot.IsRecommended;

    /// <summary>Steam CDN header image URL for this depot's associated app (for DLC depots). Null for base-game depots.</summary>
    public string? ImageUrl =>
        Depot.Category?.Equals("DLC", StringComparison.OrdinalIgnoreCase) == true && Depot.DepotId > 0
            ? $"https://cdn.cloudflare.steamstatic.com/steam/apps/{Depot.DepotId}/header.jpg"
            : null;

    /// <summary>
    /// Fires property-changed notifications for all depot-enrichment–derived properties
    /// (DisplayName, IsRecommended, ImageUrl). Call this after setting <see cref="Depot"/>
    /// with updated SteamDB data.
    /// </summary>
    public void NotifyEnrichmentChanged()
    {
        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(IsRecommended));
        OnPropertyChanged(nameof(ImageUrl));
    }
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
    private string _category = "Base Game";

    [ObservableProperty]
    private string _platform = "Universal";

    [ObservableProperty]
    private string? _architecture;

    [ObservableProperty]
    private bool _isSelected = true;

    public string FormattedSize => SizeBytes switch
    {
        > 1024 * 1024 * 1024 => $"{SizeBytes / (1024.0 * 1024.0 * 1024.0):F2} GB",
        > 1024 * 1024 => $"{SizeBytes / (1024.0 * 1024.0):F1} MB",
        _ => $"{SizeBytes / 1024.0:F0} KB"
    };

    public string CategoryTag => string.IsNullOrWhiteSpace(Category) ? "Base Game" : Category;
    public string PlatformTag => string.IsNullOrWhiteSpace(Platform) ? "Universal" : Platform;
    public string? ArchitectureTag => Architecture;

    public string DisplayCurrentManifest => CurrentManifestId > 0 ? CurrentManifestId.ToString() : "Not downloaded";
    public string DisplayNewManifest => NewManifestId.ToString();
}

/// <summary>
/// ViewModel for the complete Instance Dashboard (Overview, Depots, DLCs, Mods, Emulator, Settings, Logs).
/// </summary>
public partial class InstanceDetailViewModel : ObservableObject, IDisposable
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
    private readonly CancellationTokenSource _cts = new();
    private bool _isDisposed;
    private readonly EventHandler _settingsChangedHandler;

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

    partial void OnSelectedTabChanged(string value)
    {
        if (value == "Mods")
        {
            _ = LoadModsAsync();
        }
        else if (value == "Emulator")
        {
            _ = LoadEmulatorsAsync();
        }
        else if (value == "Prerequisites")
        {
            _ = ScanPrerequisitesAsync();
        }
    }

    [ObservableProperty]
    private ObservableCollection<SelectableDepotItem> _depots = [];

    [ObservableProperty]
    private ObservableCollection<SelectableDlcItem> _dlcs = [];

    [ObservableProperty]
    private string? _statusMessage;

    public bool IsStatusMessageVisible => !string.IsNullOrWhiteSpace(StatusMessage) && !HasActiveJob;

    partial void OnStatusMessageChanged(string? value)
    {
        OnPropertyChanged(nameof(IsStatusMessageVisible));
    }

    [ObservableProperty]
    private bool _isProcessing;


    [ObservableProperty]
    private bool _isDlcUnlocked;

    [ObservableProperty]
    private string _totalSelectedSizeFormatted = "0 KB";

    [ObservableProperty]
    private long _totalSelectedSizeBytes;

    /// <summary>Number of currently checked depots (base + DLC) shown in the bottom bar.</summary>
    public int SelectedDepotsCount =>
        Depots.Count(d => d.IsSelected) + Dlcs.Where(d => d.IsSelected).Sum(d => d.Dlc.Depots.Count);

    /// <summary>Formatted total size of selected depots — alias used by the bottom summary bar.</summary>
    public string SelectedDepotsSize => TotalSelectedSizeFormatted;



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
            if (!_appSettings.EnableExperimentalMods) return false;
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

    // ── DepotBox Game Fixes / Emulators State ──
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSpecificFixesSectionVisible))]
    private ObservableCollection<GameFixInfo> _availableGameFixes = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSpecificFixesSectionVisible))]
    private bool _hasAvailableGameFixes;

    [ObservableProperty]
    private ObservableCollection<GameFixInfo> _onlineGameFixes = [];

    [ObservableProperty]
    private bool _hasOnlineGameFixes;

    [ObservableProperty]
    private ObservableCollection<GameFixInfo> _bypassGameFixes = [];

    [ObservableProperty]
    private bool _hasBypassGameFixes;

    [ObservableProperty]
    private ObservableCollection<GameFixInfo> _hypervisorGameFixes = [];

    [ObservableProperty]
    private bool _hasHypervisorGameFixes;

    [ObservableProperty]
    private ObservableCollection<GameFixInfo> _otherGameFixes = [];

    [ObservableProperty]
    private bool _hasOtherGameFixes;

    public bool IsSpecificFixesSectionVisible => HasAvailableGameFixes || IsLoadingGameFixes;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSpecificFixesSectionVisible))]
    private bool _isLoadingGameFixes;

    [ObservableProperty]
    private bool _hasGameFixesError;

    [ObservableProperty]
    private string? _gameFixesErrorMessage;

    [ObservableProperty]
    private ObservableCollection<FixLayerInfo> _installedFixLayers = [];

    [ObservableProperty]
    private ObservableCollection<FixLayerInfo> _nonOnlineInstalledFixLayers = [];

    [ObservableProperty]
    private bool _hasInstalledFixLayers;

    [ObservableProperty]
    private bool _isOnlineFixWarningModalOpen;

    [ObservableProperty]
    private GameFixInfo? _pendingOnlineFixToInstall;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanDeployEmulator))]
    private bool _isDeployingGameFix;


    [ObservableProperty]
    private double _gameFixDeployProgress;

    [ObservableProperty]
    private string _gameFixDeployProgressMessage = string.Empty;

    [ObservableProperty]
    private bool _isGameFixDeployProgressVisible;

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

    // ── Instance Deletion & Uninstall State ──
    [ObservableProperty]
    private bool _isDeleteModalOpen;

    [ObservableProperty]
    private bool _isDeletingInstance;

    [ObservableProperty]
    private bool _isUninstallModalOpen;

    [ObservableProperty]
    private bool _isUninstallingGameFiles;

    // ── Game Builds & Version Switching State ──
    [ObservableProperty]
    private ObservableCollection<GameBuildInfo> _availableBuilds = [];

    [ObservableProperty]
    private GameBuildInfo? _selectedBuild;

    [ObservableProperty]
    private bool _isLoadingBuilds;

    [ObservableProperty]
    private string? _activeBuildBadgeText;

    [ObservableProperty]
    private bool _isCustomBuildModalOpen;

    [ObservableProperty]
    private string _customBuildIdInput = string.Empty;

    [ObservableProperty]
    private string _customBuildNameInput = string.Empty;

    [ObservableProperty]
    private ObservableCollection<CustomDepotManifestItem> _customBuildDepots = [];

    [ObservableProperty]
    private bool _isCommunityLinksMenuOpen;

    [ObservableProperty]
    private bool _enableAdvancedBuildOptions;

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

    public string DownloadButtonText => AreAllSelectedDepotsDownloaded ? "🔄 Reinstall Selected" : "⬇ Download Selected";

    /// <summary>Whether a download can be initiated (at least 1 depot checked and not currently downloading).</summary>
    public bool CanStartDownload =>
        SelectedDepotsCount > 0 &&
        (!IsInstanceDownloading || ActiveJob?.IsPaused == true || ActiveJob?.IsCompleted == true || ActiveJob?.IsFailed == true);

    [RelayCommand]
    public async Task DownloadSelectedDepotsAsync()
    {
        await StartDownloadAsync().ConfigureAwait(true);
    }

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
    private readonly IGameFixDeployService? _gameFixDeployService;
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
        IGameFixDeployService? gameFixDeployService = null,
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
        _gameFixDeployService = gameFixDeployService;
        _backgroundTaskService = backgroundTaskService;
        _steamStatusService = steamStatusService;
        _uiContext = SynchronizationContext.Current ?? new SynchronizationContext();

        _downloadQueueManager.Queue.CollectionChanged += OnQueueChanged;
        _instanceManager.InstancesChanged += OnInstanceManagerInstancesChanged;
        _gameLauncher.RunningStateChanged += OnGameRunningStateChanged;
        _gameLauncher.LogReceived += OnGameLogReceived;

        EnableAdvancedBuildOptions = _appSettings.EnableAdvancedBuildOptions;
        _settingsChangedHandler = (_, _) =>
        {
            App.Current?.Dispatcher?.Invoke(() =>
            {
                if (_isDisposed) return;
                EnableAdvancedBuildOptions = _appSettings.EnableAdvancedBuildOptions;
                SupportsMods = _appSettings.EnableExperimentalMods;
                OnPropertyChanged(nameof(IsModsTabVisible));
                if (!IsModsTabVisible && SelectedTab == "Mods")
                {
                    SelectedTab = "Overview";
                }
            });
        };
        _appSettings.SettingsChanged += _settingsChangedHandler;
    }


    private void OnQueueChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_isDisposed || Instance is null) return;
        ActiveJob = _downloadQueueManager.Queue.FirstOrDefault(j => j.Instance.Id == Instance.Id);
        _uiContext.Post(async _ =>
        {
            if (_isDisposed || Instance is null) return;
            var refreshed = await _instanceManager.GetByIdAsync(Instance.Id, CancellationToken.None).ConfigureAwait(true);
            if (refreshed != null && (refreshed.Status != Instance.Status || refreshed.Depots.Any(d => d.IsDownloaded != Instance.Depots.FirstOrDefault(x => x.DepotId == d.DepotId)?.IsDownloaded)))
            {
                await LoadInstanceAsync(refreshed).ConfigureAwait(true);
            }
            NotifyDownloadProps();
            OnPropertyChanged(nameof(HeroTags));
            OnPropertyChanged(nameof(IsInstalled));
            OnPropertyChanged(nameof(CanDeployEmulator));
        }, null);
    }

    private void OnInstanceManagerInstancesChanged(object? sender, EventArgs e)
    {
        if (_isDisposed || Instance is null) return;
        _uiContext.Post(async _ =>
        {
            if (_isDisposed || Instance is null) return;
            var refreshed = await _instanceManager.GetByIdAsync(Instance.Id, CancellationToken.None).ConfigureAwait(true);
            if (refreshed != null && refreshed.Status != Instance.Status)
            {
                await LoadInstanceAsync(refreshed).ConfigureAwait(true);
            }
        }, null);
    }

    private void OnGameRunningStateChanged(object? sender, (Guid InstanceId, bool IsRunning) e)
    {
        if (_isDisposed) return;
        if (Instance?.Id == e.InstanceId)
        {
            _uiContext.Post(_ =>
            {
                if (_isDisposed) return;
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
        if (_isDisposed || Instance == null) return;
        var isInstalled = ReFixEmulator.IsEmulatorInstalled(Instance.InstallPath) || !string.IsNullOrWhiteSpace(Instance.EmulatorId);
        if (!isInstalled) return;

        var onlineLayer = Instance.InstalledFixLayers?.FirstOrDefault(l => l.IsOnline);
        string optionId;
        string displayName;
        string? emulatorVersion;

        if (onlineLayer != null || Instance.EmulatorId == "gamefix_online")
        {
            optionId = onlineLayer?.FixId ?? Instance.InstalledEmulatorVersion ?? "gamefix_online";
            displayName = onlineLayer?.DisplayName ?? "Online Multiplayer Fix";
            emulatorVersion = onlineLayer?.Version ?? Instance.InstalledEmulatorVersion ?? "1.0";
        }
        else
        {
            optionId = Instance.EmulatorId ?? (InstalledEmulatorMode?.Contains("Goldberg", StringComparison.OrdinalIgnoreCase) == true ? "refix_goldberg" : "refix_valve");
            displayName = InstalledEmulatorMode ?? (optionId == "refix_goldberg" ? "Re:Goldberg LAN" : "ReFix Online (Steam)");
            emulatorVersion = Instance.InstalledEmulatorVersion ?? "1.0";
        }

        if (!_emulatorRatingService.HasUserVoted(Instance, optionId, emulatorVersion))
        {
            FeedbackOptionId = optionId;
            FeedbackEmulatorName = displayName;
            ShowFeedbackUninstallPrompt = false;
            IsFeedbackModalOpen = true;
        }
    }

    private void OnGameLogReceived(object? sender, (Guid InstanceId, string LogLine) e)
    {
        if (_isDisposed) return;
        if (Instance?.Id == e.InstanceId)
        {
            _uiContext.Post(_ =>
            {
                if (_isDisposed) return;
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
        OnPropertyChanged(nameof(CanStartDownload));
        OnPropertyChanged(nameof(SelectedDepotsCount));
        OnPropertyChanged(nameof(SelectedDepotsSize));
        OnPropertyChanged(nameof(IsStatusMessageVisible));
    }


    /// <summary>
    /// Loads details for the target game instance.
    /// </summary>
    public async Task LoadInstanceAsync(GameInstance instance, bool autoCheckDepotUpdates = false)
    {
        try
        {
            var cleanGameName = CleanName(instance.Name) ?? instance.Name;

            bool isInstanceInstalled = instance.Status == InstanceStatus.Ready || instance.Status == InstanceStatus.Running;

            var depotList = (instance.Depots ?? []).Select(d => d with
            {
                Name = CleanName(d.Name) ?? d.Name,
                IsDownloaded = d.IsDownloaded
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

            var installPath = instance.InstallPath;
            if (!string.IsNullOrWhiteSpace(installPath) && !Directory.Exists(installPath))
            {
                var candidate = PathHelper.EnsureGameSubfolder(installPath, cleanGameName);
                if (Directory.Exists(candidate))
                {
                    installPath = candidate;
                }
            }

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

            var status = instance.Status;
            if (status == InstanceStatus.NotInstalled && !string.IsNullOrWhiteSpace(installPath) && Directory.Exists(installPath))
            {
                var exes = ShortcutHelper.FindGameExecutables(installPath, cleanGameName);
                bool hasDownloadedDepots = depotList.Count > 0 && depotList.All(d => d.IsDownloaded);
                if (exes.Count > 0 || hasDownloadedDepots || (!string.IsNullOrWhiteSpace(exe) && File.Exists(exe)))
                {
                    status = InstanceStatus.Ready;
                }
            }

            Instance = instance with
            {
                Name = cleanGameName,
                InstallPath = installPath,
                Depots = depotList.AsReadOnly(),
                Dlcs = cleanDlcs,
                Engine = engine,
                ExecutablePath = exe,
                Status = status
            };

            if (status != instance.Status)
            {
                _ = _instanceManager.UpdateAsync(Instance, CancellationToken.None);
            }

            ConfiguredExecutablePath = Instance.ExecutablePath ?? string.Empty;
            InstanceAlias = Instance.Name ?? string.Empty;
            CustomLaunchArgs = Instance.LaunchArguments ?? string.Empty;
            IsUnityEngine = Instance.Engine?.Type == EngineType.Unity;

            var selectableDepots = Instance.Depots
                .Where(d => d.SizeBytes > 0 || !Instance.Depots.Any(other => other.SizeBytes > 0))
                .Select(d => new SelectableDepotItem
                {
                    Depot = d,
                    IsSelected = IsDepotCompatibleWithCurrentOS(d),
                    OnSelectionChanged = RecalculateSelectedSize,
                    OnManifestUpdated = HandleDepotManifestUpdated
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

            SelectedTab = "Overview";

            // Check capabilities
            SupportsMods = _appSettings.EnableExperimentalMods;
            SupportsEmulation = true;

            // Load mods, BepInEx, Workshop, emulators, and prerequisites in background
            if (_appSettings.EnableExperimentalMods)
            {
                _ = LoadModsAsync();
                if (IsUnityEngine) _ = CheckAndLoadBepInExAsync();
                _ = CheckWorkshopSupportAsync();
            }
            _ = LoadEmulatorsAsync();
            _ = ScanPrerequisitesAsync();

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
            _ = LoadAvailableBuildsAsync();
            _ = EnrichDepotsFromSteamDbAsync();


            if (autoCheckDepotUpdates)
            {
                _ = CheckAndOpenDepotUpdateModalAsync();
            }

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
            _logger.LogError(ex, "Failed to load instance details for {Name}", instance.Name);
            _notificationService?.ShowError("Failed to Load Game", ex.Message);
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

    /// <summary>
    /// Fetches per-depot names, OS lists, and optional flags from the SteamCMD API and
    /// enriches the loaded <see cref="Depots"/> collection with <see cref="DepotInfo.SteamDbName"/>
    /// and <see cref="DepotInfo.IsRecommended"/>.  Runs in the background — UI updates on the UI thread.
    /// </summary>
    private async Task EnrichDepotsFromSteamDbAsync()
    {
        if (Instance is null || Instance.AppId == 0 || Depots.Count == 0) return;

        // Only SteamStoreApiClient exposes GetDepotEnrichmentAsync
        if (_metadataProvider is not BlueStar.Infrastructure.Metadata.SteamStoreApiClient steamClient) return;

        try
        {
            var enrichment = await steamClient.GetDepotEnrichmentAsync(Instance.AppId, CancellationToken.None)
                                              .ConfigureAwait(false);

            if (enrichment.Count == 0) return;

            // Determine current OS for recommended-flag logic (Windows is always current for this app)
            const string currentOsKey = "windows";

            // Switch back to UI thread for collection mutations
            _uiContext.Post(_ =>
            {
                bool anyChange = false;

                for (int i = 0; i < Depots.Count; i++)
                {
                    var item = Depots[i];
                    if (!enrichment.TryGetValue(item.Depot.DepotId, out var meta)) continue;

                    // Determine recommended: non-optional, not shared, and OS matches (or no OS restriction)
                    bool osMatch = string.IsNullOrWhiteSpace(meta.OsList) ||
                                   meta.OsList.Split(',', StringSplitOptions.RemoveEmptyEntries)
                                       .Any(os => os.Trim().Equals(currentOsKey, StringComparison.OrdinalIgnoreCase));

                    bool isRecommended = !meta.IsOptional && !meta.IsShared && osMatch;

                    string? newSteamDbName = string.IsNullOrWhiteSpace(meta.Name) ? null : meta.Name.Trim();

                    // Skip if nothing changed
                    if (item.Depot.SteamDbName == newSteamDbName && item.Depot.IsRecommended == isRecommended)
                        continue;

                    item.Depot = item.Depot with
                    {
                        SteamDbName = newSteamDbName,
                        IsRecommended = isRecommended
                    };

                    // Auto-select recommended depots that were not yet selected
                    if (isRecommended && !item.IsSelected)
                        item.IsSelected = true;

                    item.NotifyEnrichmentChanged();
                    anyChange = true;

                }

                if (anyChange)
                    RecalculateSelectedSize();

            }, null);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to enrich depots from SteamDB for AppId={AppId}", Instance?.AppId);
        }
    }


    private async Task CheckSteamVersionDateAsync()
    {
        if (_isDisposed || Instance == null || Instance.AppId == 0) return;
        try
        {
            if (_metadataProvider is BlueStar.Infrastructure.Metadata.SteamStoreApiClient steamClient)
            {
                var (status, _, latestDate, latestDateText, installedDate, installedDateText) =
                    await BlueStar.Infrastructure.Services.GameUpdateDetectionHelper.CheckInstanceUpdateDetailsAsync(Instance, steamClient, _cts.Token).ConfigureAwait(true);

                if (_isDisposed) return;

                if (latestDate.HasValue)
                {
                    LatestVersionDate = latestDate.Value;
                    LatestVersionText = latestDateText ?? "Unknown";
                }
                else if (!string.IsNullOrWhiteSpace(latestDateText))
                {
                    LatestVersionText = latestDateText;
                }

                if (installedDate.HasValue)
                {
                    InstalledVersionDate = installedDate.Value;
                    InstalledVersionText = installedDateText ?? "Unknown";
                }
                else if (!string.IsNullOrWhiteSpace(installedDateText))
                {
                    InstalledVersionText = installedDateText;
                }

                if (status == UpdateCheckStatus.UpdateAvailable)
                {
                    HasGameUpdateAvailable = true;
                    if (!Instance.HasUpdateAvailable)
                    {
                        Instance = Instance with { HasUpdateAvailable = true };
                        await _instanceManager.UpdateAsync(Instance, CancellationToken.None).ConfigureAwait(true);
                    }
                }
                else if (status == UpdateCheckStatus.UpToDate)
                {
                    HasGameUpdateAvailable = false;
                    if (Instance.HasUpdateAvailable)
                    {
                        Instance = Instance with { HasUpdateAvailable = false };
                        await _instanceManager.UpdateAsync(Instance, CancellationToken.None).ConfigureAwait(true);
                    }
                }
                // When status is UpdateCheckStatus.Unknown, preserve existing known state
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

        if (_backgroundTaskService != null)
        {
            _backgroundTaskService.QueueTask(
                $"Searching Depot Updates: {Instance.Name}",
                Instance.Name,
                async (progress, ct) =>
                {
                    try
                    {
                        await CheckDepotUpdatesInternalAsync(progress, ct).ConfigureAwait(false);
                    }
                    finally
                    {
                        _uiContext.Post(_ =>
                        {
                            IsCheckingGameUpdate = false;
                        }, null);
                    }
                },
                Instance.Id);
        }
        else
        {
            try
            {
                var dummyProgress = new Progress<BackgroundTaskProgress>();
                await CheckDepotUpdatesInternalAsync(dummyProgress, CancellationToken.None).ConfigureAwait(true);
            }
            finally
            {
                IsCheckingGameUpdate = false;
            }
        }
    }

    private async Task CheckDepotUpdatesInternalAsync(
        IProgress<BackgroundTaskProgress> progress,
        CancellationToken ct)
    {
        progress.Report(new BackgroundTaskProgress(15, "Connecting to DepotBox API...", "Searching"));

        try
        {
            var manifests = await _apiClient!.GetManifestsAsync(Instance!.AppId, ct).ConfigureAwait(false);
            if (manifests.Count == 0)
            {
                progress.Report(new BackgroundTaskProgress(100, "No pending updates on DepotBox.", "Complete"));
                _uiContext.Post(_ =>
                {
                    if (HasGameUpdateAvailable)
                    {
                        _notificationService?.ShowWarning(
                            "Update Pending on DepotBox",
                            $"A newer build was detected on Steam ({LatestVersionText ?? "latest release"}), but DepotBox contributors have not uploaded updated manifests for this game yet. Please check back later.",
                            TimeSpan.FromSeconds(8));
                    }
                    else
                    {
                        _notificationService?.ShowInfo(
                            "Depots Up to Date",
                            "No pending updates found on DepotBox.",
                            TimeSpan.FromSeconds(5));
                    }
                }, null);
                return;
            }

            progress.Report(new BackgroundTaskProgress(65, "Analyzing depot manifests and version dates...", "Analyzing"));

            var outdatedDepots = new List<DepotUpdateItem>();
            foreach (var man in manifests)
            {
                var local = Instance.Depots.FirstOrDefault(d => d.DepotId == man.DepotId);
                var size = man.SizeBytes > 0 ? man.SizeBytes : (local?.SizeBytes ?? 0);
                if (size <= 0) continue; // Hide 0kb depots from update list

                if (local == null || (local.ManifestId != man.ManifestId && man.ManifestId > 0))
                {
                    outdatedDepots.Add(new DepotUpdateItem
                    {
                        DepotId = man.DepotId,
                        Name = local?.Name ?? $"Depot {man.DepotId}",
                        Category = local?.Category ?? "Base Game",
                        Platform = local?.Platform ?? "Universal",
                        Architecture = local?.Architecture,
                        CurrentManifestId = local?.ManifestId ?? 0,
                        NewManifestId = man.ManifestId,
                        SizeBytes = size,
                        IsSelected = true
                    });
                }
            }

            if (outdatedDepots.Count > 0)
            {
                progress.Report(new BackgroundTaskProgress(100, $"Found {outdatedDepots.Count} updated depot(s).", "Complete"));
                _uiContext.Post(_ =>
                {
                    UpdateAvailableDepots = new ObservableCollection<DepotUpdateItem>(outdatedDepots);
                    IsUpdateModalOpen = true;
                }, null);
            }
            else
            {
                progress.Report(new BackgroundTaskProgress(100, "All depots match installed version.", "Complete"));
                _uiContext.Post(_ =>
                {
                    if (HasGameUpdateAvailable)
                    {
                        _notificationService?.ShowWarning(
                            "Update Pending on DepotBox",
                            $"Steam detected a newer build for {Instance.Name} ({LatestVersionText ?? "latest release"}), but the manifests currently hosted on DepotBox match your installed version. The new update has not been uploaded to DepotBox yet.",
                            TimeSpan.FromSeconds(8));
                    }
                    else
                    {
                        HasGameUpdateAvailable = false;
                        if (Instance.HasUpdateAvailable)
                        {
                            Instance = Instance with { HasUpdateAvailable = false, UpdateDescription = null };
                            _ = _instanceManager.UpdateAsync(Instance, CancellationToken.None);
                        }

                        _notificationService?.ShowInfo(
                            "Depots Up to Date",
                            "Depot manifests on DepotBox match the versions already installed on your instance. No new files pending download.",
                            TimeSpan.FromSeconds(5));
                    }
                }, null);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to check depot updates for {Name}", Instance?.Name);
            _uiContext.Post(_ =>
            {
                _notificationService?.ShowError("Failed to Check for Updates", ex.Message);
            }, null);
            throw;
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

        if (_backgroundTaskService != null)
        {
            _backgroundTaskService.QueueTask(
                $"Downloading Updated Depots: {Instance.Name}",
                Instance.Name,
                async (progress, ct) =>
                {
                    try
                    {
                        await ProcessApplyDepotUpdateInternalAsync(selected, progress, ct).ConfigureAwait(false);
                    }
                    finally
                    {
                        _uiContext.Post(_ =>
                        {
                            IsApplyingGameUpdate = false;
                        }, null);
                    }
                },
                Instance.Id);
        }
        else
        {
            try
            {
                var dummyProgress = new Progress<BackgroundTaskProgress>();
                await ProcessApplyDepotUpdateInternalAsync(selected, dummyProgress, CancellationToken.None).ConfigureAwait(true);
            }
            finally
            {
                IsApplyingGameUpdate = false;
            }
        }
    }

    private async Task ProcessApplyDepotUpdateInternalAsync(
        List<DepotUpdateItem> selected,
        IProgress<BackgroundTaskProgress> progress,
        CancellationToken ct)
    {
        progress.Report(new BackgroundTaskProgress(5, "Downloading updated manifests and keys from DepotBox...", "Downloading"));

        var workDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BlueStar", "DepotWork", Instance!.Id.ToString());
        Directory.CreateDirectory(workDir);

        var instanceManifestDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "BlueStar", "instances", Instance.Id.ToString(), "manifests");
        Directory.CreateDirectory(instanceManifestDir);

        string? archivePath = null;
        DepotBoxArchive? parsedArchive = null;

        if (_apiClient != null)
        {
            var dlProgress = new Progress<DownloadProgress>(p =>
            {
                var mb = p.DownloadedBytes / (1024.0 * 1024.0);
                var totalMb = p.TotalBytes > 0 ? $" / {p.TotalBytes / (1024.0 * 1024.0):F1} MB" : " MB";
                var pct = 5.0 + (p.Percentage * 0.80);
                progress.Report(new BackgroundTaskProgress(
                    pct,
                    $"Downloading depot archive ({mb:F1}{totalMb})...",
                    "Downloading"));
            });

            archivePath = await _apiClient.DownloadArchiveAsync(Instance.AppId, workDir, dlProgress, ct).ConfigureAwait(false);

            if (_archiveParser != null && File.Exists(archivePath))
            {
                progress.Report(new BackgroundTaskProgress(88, "Extracting updated manifests...", "Extracting"));
                await _archiveParser.ExtractManifestsAsync(archivePath, instanceManifestDir, ct).ConfigureAwait(false);
                await _archiveParser.ExtractManifestsAsync(archivePath, workDir, ct).ConfigureAwait(false);
                parsedArchive = await _archiveParser.ParseAsync(archivePath, ct).ConfigureAwait(false);
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
            SourceArchivePath = archivePath ?? Instance.SourceArchivePath
        };

        await _instanceManager.UpdateAsync(updatedInstance, ct).ConfigureAwait(false);

        // Reset all community emulation ratings and user vote flags for this game AppID due to new game update/build
        if (_emulatorRatingService != null)
        {
            await _emulatorRatingService.ResetRatingsForGameAsync(Instance.AppId, ct).ConfigureAwait(false);
        }

        progress.Report(new BackgroundTaskProgress(95, "Enqueuing updated depots for download...", "Finalizing"));

        _uiContext.Post(_ =>
        {
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
        }, null);

        progress.Report(new BackgroundTaskProgress(100, $"Updated {selected.Count} depot(s) configuration.", "Complete"));
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
                    var dlProgress = new Progress<DownloadProgress>(p =>
                    {
                        StatusMessage = $"⏳ Downloading depot archive ({p.Percentage:F0}%)...";
                    });
                    archivePath = await _apiClient.DownloadArchiveAsync(candidate.AppId, archivesDir, dlProgress, CancellationToken.None).ConfigureAwait(true);
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

        // Directly purge target path if available
        if (!string.IsNullOrWhiteSpace(mod.FilePath))
        {
            try
            {
                if (File.Exists(mod.FilePath))
                {
                    File.Delete(mod.FilePath);
                    var dir = Path.GetDirectoryName(mod.FilePath);
                    var baseName = Path.GetFileNameWithoutExtension(mod.FilePath);
                    if (dir != null)
                    {
                        var companionJson = Path.Combine(dir, $"{baseName}_info.json");
                        if (File.Exists(companionJson)) try { File.Delete(companionJson); } catch { }

                        var thumbPng = Path.Combine(dir, $"{baseName}.png");
                        if (File.Exists(thumbPng)) try { File.Delete(thumbPng); } catch { }
                    }
                }
                else if (Directory.Exists(mod.FilePath))
                {
                    Directory.Delete(mod.FilePath, recursive: true);
                }
            }
            catch { }
        }

        await manager.UninstallModAsync(Instance, mod.Id, CancellationToken.None).ConfigureAwait(true);
        await LoadModsAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    public void OpenModsFolder()
    {
        try
        {
            var target = ModsDirectoryPath;
            if (string.IsNullOrWhiteSpace(target) && Instance != null)
            {
                var res = GameModPathResolver.ResolveModPaths(Instance);
                target = res.PrimaryDirectory;
            }

            if (!string.IsNullOrWhiteSpace(target))
            {
                Directory.CreateDirectory(target);
                Process.Start(new ProcessStartInfo { FileName = target, UseShellExecute = true });
            }
            else if (Instance != null && !string.IsNullOrWhiteSpace(Instance.InstallPath) && Directory.Exists(Instance.InstallPath))
            {
                Process.Start(new ProcessStartInfo { FileName = Instance.InstallPath, UseShellExecute = true });
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to open mods folder in Explorer");
        }
    }

    // ── In-Memory Session Cache for Emulators & Fixes (resets when BlueStar application restarts) ──
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, (IReadOnlyList<EmulatorOptionInfo> Options, IReadOnlyList<IEmulator> Emulators, IReadOnlyList<GameFixInfo> Fixes)> _emulatorSessionCache = new();

    // ── Emulators Management ──
    [RelayCommand]
    public async Task LoadEmulatorsAsync() => await LoadEmulatorsCoreAsync(forceReload: false).ConfigureAwait(true);

    [RelayCommand]
    public async Task ForceRefreshEmulatorsAsync() => await LoadEmulatorsCoreAsync(forceReload: true).ConfigureAwait(true);

    public async Task LoadEmulatorsCoreAsync(bool forceReload = false)
    {
        if (Instance == null) return;

        try
        {
            var allLayers = Instance.InstalledFixLayers ?? [];
            InstalledFixLayers = new ObservableCollection<FixLayerInfo>(allLayers);
            NonOnlineInstalledFixLayers = new ObservableCollection<FixLayerInfo>(allLayers.Where(l => !l.IsOnline));
            HasInstalledFixLayers = NonOnlineInstalledFixLayers.Count > 0;

            var onlineLayer = allLayers.FirstOrDefault(l => l.IsOnline);
            if (onlineLayer != null || Instance.EmulatorId == "gamefix_online")
            {
                IsEmulatorInstalled = true;
                if (onlineLayer != null)
                {
                    var cleanName = System.Text.RegularExpressions.Regex.Replace(
                        onlineLayer.DisplayName,
                        @"\s*[\(\[](Online(\s*Fix)?|Bypass|Hypervisor)[\)\]]\s*",
                        "",
                        System.Text.RegularExpressions.RegexOptions.IgnoreCase).Trim();
                    InstalledEmulatorMode = !string.IsNullOrWhiteSpace(cleanName) ? $"{cleanName} (Online Fix)" : "Game-Specific (Online Fix)";
                }
                else
                {
                    InstalledEmulatorMode = "Game-Specific (Online Fix)";
                }
            }
            else
            {
                IsEmulatorInstalled = ReFixEmulator.IsEmulatorInstalled(Instance.InstallPath);
                InstalledEmulatorMode = ReFixEmulator.GetInstalledMode(Instance.InstallPath);
            }

            if (_refixUpdateService != null && onlineLayer == null && Instance.EmulatorId != "gamefix_online" && (Instance.EmulatorId == null || Instance.EmulatorId.StartsWith("refix", StringComparison.OrdinalIgnoreCase)))
            {
                CurrentGlobalReFixVersion = _refixUpdateService.GetCurrentInstalledVersion();
                InstanceReFixVersion = Instance.InstalledEmulatorVersion ?? "1.0";
                IsReFixUpdateAvailableForInstance = _refixUpdateService.IsInstanceReFixOutdated(Instance);
            }
            else
            {
                IsReFixUpdateAvailableForInstance = false;
            }

            // Session Cache check: if emulators for this instance were already loaded during this app session, reuse without hitting API
            if (!forceReload && _emulatorSessionCache.TryGetValue(Instance.Id, out var cached))
            {
                AvailableEmulatorOptions = new ObservableCollection<EmulatorOptionInfo>(cached.Options);
                RecommendedEmulatorOption = cached.Options.FirstOrDefault(o => o.IsRecommended && o.TotalVotes >= 10);
                AvailableEmulators = new ObservableCollection<IEmulator>(cached.Emulators);
                SelectedEmulator = cached.Emulators.FirstOrDefault(e => e.Id == (Instance.EmulatorId ?? "refix")) ?? cached.Emulators.FirstOrDefault();
                UpdateCategorizedGameFixes(cached.Fixes);

                if (SelectedEmulator != null)
                {
                    EmulatorStatus = await SelectedEmulator.GetStatusAsync(Instance, CancellationToken.None).ConfigureAwait(true);
                }
                return;
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

            await LoadGameFixesCoreAsync(forceReload).ConfigureAwait(true);

            // Store in session cache
            _emulatorSessionCache[Instance.Id] = (
                AvailableEmulatorOptions.ToList().AsReadOnly(),
                AvailableEmulators.ToList().AsReadOnly(),
                AvailableGameFixes.ToList().AsReadOnly()
            );
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error loading emulator options for {Name}", Instance.Name);
        }
    }

    private void UpdateCategorizedGameFixes(IReadOnlyList<GameFixInfo> fixesList)
    {
        var enrichedList = fixesList.Select(fix =>
        {
            if (fix.IsOnline && _emulatorRatingService != null && Instance != null)
            {
                var (pos, neg) = _emulatorRatingService.GetRatings(Instance.AppId, fix.Id);
                var hasVoted = _emulatorRatingService.HasUserVoted(Instance, fix.Id, fix.Name);
                return fix with
                {
                    PositiveVotes = pos,
                    NegativeVotes = neg,
                    HasUserVoted = hasVoted
                };
            }
            return fix;
        }).ToList();

        AvailableGameFixes = new ObservableCollection<GameFixInfo>(enrichedList);
        HasAvailableGameFixes = AvailableGameFixes.Count > 0;

        var onlineList = enrichedList.Where(f => f.IsOnline)
            .OrderByDescending(f => f.ScorePercentage)
            .ThenByDescending(f => f.PositiveVotes)
            .ThenByDescending(f => f.TotalVotes)
            .ToList();

        OnlineGameFixes = new ObservableCollection<GameFixInfo>(onlineList);
        HasOnlineGameFixes = OnlineGameFixes.Count > 0;

        BypassGameFixes = new ObservableCollection<GameFixInfo>(enrichedList.Where(f => f.IsBypass && !f.IsOnline));
        HasBypassGameFixes = BypassGameFixes.Count > 0;

        HypervisorGameFixes = new ObservableCollection<GameFixInfo>(enrichedList.Where(f => f.IsHypervisor && !f.IsOnline && !f.IsBypass));
        HasHypervisorGameFixes = HypervisorGameFixes.Count > 0;

        OtherGameFixes = new ObservableCollection<GameFixInfo>(enrichedList.Where(f => !f.IsOnline && !f.IsBypass && !f.IsHypervisor));
        HasOtherGameFixes = OtherGameFixes.Count > 0;
    }

    [RelayCommand]
    public async Task LoadGameFixesAsync() => await LoadGameFixesCoreAsync(forceReload: true).ConfigureAwait(true);

    public async Task LoadGameFixesCoreAsync(bool forceReload = false)
    {
        if (Instance == null || _apiClient == null) return;

        try
        {
            IsLoadingGameFixes = true;
            HasGameFixesError = false;
            GameFixesErrorMessage = null;

            IReadOnlyList<GameFixInfo>? fixes = null;

            // 1. Try searching by exact AppId first if available
            if (Instance.AppId > 0)
            {
                fixes = await _apiClient.GetGameFixesAsync(query: Instance.AppId.ToString(), ct: CancellationToken.None).ConfigureAwait(true);
            }

            // 2. If empty, try searching by clean game name
            var cleanName = CleanName(Instance.Name) ?? Instance.Name;
            if ((fixes == null || fixes.Count == 0) && !string.IsNullOrWhiteSpace(cleanName))
            {
                fixes = await _apiClient.GetGameFixesAsync(query: cleanName, ct: CancellationToken.None).ConfigureAwait(true);
            }

            // 3. If still empty, try searching by raw instance name
            if ((fixes == null || fixes.Count == 0) && !string.IsNullOrWhiteSpace(Instance.Name) && !string.Equals(cleanName, Instance.Name, StringComparison.OrdinalIgnoreCase))
            {
                fixes = await _apiClient.GetGameFixesAsync(query: Instance.Name, ct: CancellationToken.None).ConfigureAwait(true);
            }

            UpdateCategorizedGameFixes(fixes ?? []);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load DepotBox game fixes for {Game}", Instance.Name);
            HasGameFixesError = true;
            GameFixesErrorMessage = ex.Message;
            UpdateCategorizedGameFixes([]);
        }
        finally
        {
            IsLoadingGameFixes = false;
        }
    }


    [RelayCommand]
    public async Task DeployGameFixAsync(GameFixInfo? fix)
    {
        if (Instance == null || fix == null || IsDeployingGameFix || IsDeployingEmulator) return;

        if (!IsInstalled)
        {
            StatusMessage = "⚠ You must install or download the game before configuring a fix.";
            _notificationService?.ShowWarning("Game Not Installed", "You must install or download the game before configuring a fix.");
            return;
        }

        // If it's an online fix, display the security warning confirmation modal first
        if (fix.IsOnline)
        {
            PendingOnlineFixToInstall = fix;
            IsOnlineFixWarningModalOpen = true;
            return;
        }

        await ExecuteDeployGameFixAsync(fix).ConfigureAwait(true);
    }

    [RelayCommand]
    public void CloseOnlineFixWarningModal()
    {
        IsOnlineFixWarningModalOpen = false;
        PendingOnlineFixToInstall = null;
    }

    [RelayCommand]
    public async Task ConfirmInstallOnlineFixAsync()
    {
        var fix = PendingOnlineFixToInstall;
        IsOnlineFixWarningModalOpen = false;
        PendingOnlineFixToInstall = null;

        if (Instance == null || fix == null || IsDeployingGameFix || IsDeployingEmulator) return;

        // 1. If ReFix or another emulator is currently installed, cleanly uninstall it first (mutual exclusion)
        if (ReFixEmulator.IsEmulatorInstalled(Instance.InstallPath) || (Instance.EmulatorId != null && Instance.EmulatorId != "gamefix_online"))
        {
            StatusMessage = "⏳ Removing existing ReFix emulator before deploying Online fix...";
            try
            {
                if (_emulatorLifecycleService != null)
                {
                    await _emulatorLifecycleService.UninstallEmulatorWithDlcPreservationAsync(
                        Instance, null, CancellationToken.None).ConfigureAwait(true);
                }
                else
                {
                    var refix = _emulatorRegistry.GetById("refix") as ReFixEmulator
                        ?? new ReFixEmulator(_logger as ILogger<ReFixEmulator> ?? LoggerFactory.Create(_ => {}).CreateLogger<ReFixEmulator>());
                    await refix.UninstallAsync(Instance, CancellationToken.None).ConfigureAwait(true);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to cleanly uninstall previous emulator before online fix deployment");
            }
        }

        // 2. Request UAC elevation and add Windows Defender exclusions for game, download cache, and temp deploy directories
        var pathsToExclude = new List<string>();
        if (!string.IsNullOrWhiteSpace(Instance.InstallPath))
        {
            pathsToExclude.Add(Instance.InstallPath);
        }

        var cacheDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BlueStar", "cache", "game_fixes");
        pathsToExclude.Add(cacheDir);

        var tempDeployDir = Path.Combine(Path.GetTempPath(), "BlueStar_FixDeploy");
        pathsToExclude.Add(tempDeployDir);

        StatusMessage = "🛡️ Adding Windows Defender exclusion for download and game directories...";
        _logger.LogInformation("Requesting Windows Defender exclusion for {Count} paths: {Paths}", pathsToExclude.Count, string.Join(", ", pathsToExclude));
        try
        {
            await AntivirusExclusionHelper.AddFolderExclusionAsync(pathsToExclude, CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed adding Windows Defender exclusion");
        }

        // 3. Deploy the online fix
        await ExecuteDeployGameFixAsync(fix, isOnlineFix: true).ConfigureAwait(true);
    }

    private async Task ExecuteDeployGameFixAsync(GameFixInfo fix, bool isOnlineFix = false)
    {
        if (Instance == null || fix == null || IsDeployingGameFix || IsDeployingEmulator) return;

        IsDeployingGameFix = true;
        IsGameFixDeployProgressVisible = true;
        GameFixDeployProgress = 0;
        GameFixDeployProgressMessage = $"Starting deployment of {fix.Name}...";
        StatusMessage = $"⏳ Deploying {fix.Name}...";

        string? lastReportMessage = null;
        try
        {
            var progressReporter = new Progress<DeployProgress>(p =>
            {
                GameFixDeployProgress = p.Percentage;
                GameFixDeployProgressMessage = p.Message;
                StatusMessage = $"⏳ {p.Message}";
                lastReportMessage = p.Message;
            });

            if (_gameFixDeployService == null)
            {
                StatusMessage = "❌ Game fix deploy service is not available.";
                _notificationService?.ShowError("Service Error", "Game fix deploy service is not registered.");
                return;
            }

            var success = await _gameFixDeployService.DeployFixAsync(Instance, fix, progressReporter, CancellationToken.None).ConfigureAwait(true);

            if (success)
            {
                var refreshed = await _instanceManager.GetByIdAsync(Instance.Id, CancellationToken.None).ConfigureAwait(true);
                if (refreshed != null)
                {
                    if (isOnlineFix || fix.IsOnline)
                    {
                        refreshed = refreshed with
                        {
                            EmulatorEnabled = true,
                            EmulatorId = "gamefix_online",
                            InstalledEmulatorVersion = fix.Name
                        };
                        await _instanceManager.UpdateAsync(refreshed, CancellationToken.None).ConfigureAwait(true);
                    }
                    Instance = refreshed;
                }

                StatusMessage = $"✅ {fix.Name} installed successfully.";
                _notificationService?.ShowSuccess("Fix Installed", $"{fix.Name} ({fix.TagsSummary}) deployed to {Instance.Name}.");
            }
            else
            {
                var failureDetail = !string.IsNullOrWhiteSpace(lastReportMessage) && !lastReportMessage.StartsWith("Starting", StringComparison.OrdinalIgnoreCase)
                    ? lastReportMessage
                    : $"Could not deploy {fix.Name}.";
                StatusMessage = $"❌ {failureDetail}";
                _notificationService?.ShowError("Deployment Error", failureDetail);
            }

            await LoadEmulatorsCoreAsync(forceReload: true).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deploying game fix {Fix}", fix.Id);
            StatusMessage = $"❌ Error deploying fix: {ex.Message}";
            _notificationService?.ShowError("Fix Error", ex.Message);
        }
        finally
        {
            IsDeployingGameFix = false;
            await Task.Delay(1200).ConfigureAwait(true);
            IsGameFixDeployProgressVisible = false;
        }
    }

    [RelayCommand]
    public async Task UninstallFixLayerAsync(FixLayerInfo? layer)
    {
        if (Instance == null || layer == null || IsDeployingGameFix) return;

        IsDeployingGameFix = true;
        IsGameFixDeployProgressVisible = true;
        GameFixDeployProgress = 0;
        GameFixDeployProgressMessage = $"Removing {layer.DisplayName}...";
        StatusMessage = $"⏳ Removing {layer.DisplayName}...";

        try
        {
            var progressReporter = new Progress<DeployProgress>(p =>
            {
                GameFixDeployProgress = p.Percentage;
                GameFixDeployProgressMessage = p.Message;
                StatusMessage = $"⏳ {p.Message}";
            });

            if (_gameFixDeployService == null)
            {
                StatusMessage = "❌ Game fix deploy service is not available.";
                return;
            }

            // If it was an online fix layer, remove Windows Defender folder exclusions
            if (layer.IsOnline && !string.IsNullOrWhiteSpace(Instance.InstallPath))
            {
                try
                {
                    await AntivirusExclusionHelper.RemoveFolderExclusionAsync(new[]
                    {
                        Instance.InstallPath,
                        Path.Combine(Path.GetTempPath(), "BlueStar_FixDeploy")
                    }, CancellationToken.None).ConfigureAwait(true);
                }
                catch { }
            }

            var success = await _gameFixDeployService.UninstallFixLayerAsync(Instance, layer, progressReporter, CancellationToken.None).ConfigureAwait(true);

            if (success)
            {
                var refreshed = await _instanceManager.GetByIdAsync(Instance.Id, CancellationToken.None).ConfigureAwait(true);
                if (refreshed != null)
                {
                    if (layer.IsOnline)
                    {
                        refreshed = refreshed with
                        {
                            EmulatorEnabled = false,
                            EmulatorId = null,
                            InstalledEmulatorVersion = null
                        };
                        await _instanceManager.UpdateAsync(refreshed, CancellationToken.None).ConfigureAwait(true);
                    }
                    Instance = refreshed;
                }

                StatusMessage = $"✅ {layer.DisplayName} removed.";
                _notificationService?.ShowInfo("Fix Removed", $"{layer.DisplayName} uninstalled and original files restored.");
            }
            else
            {
                StatusMessage = $"❌ Failed to remove {layer.DisplayName}.";
            }

            await LoadEmulatorsCoreAsync(forceReload: true).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error removing fix layer {LayerId}", layer.LayerId);
            StatusMessage = $"❌ Error removing fix: {ex.Message}";
        }
        finally
        {
            IsDeployingGameFix = false;
            await Task.Delay(800).ConfigureAwait(true);
            IsGameFixDeployProgressVisible = false;
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

        // Mutual Exclusion: If an Online game-specific fix is currently installed, remove it first
        var onlineLayer = Instance.InstalledFixLayers?.FirstOrDefault(l => l.IsOnline);
        if (onlineLayer != null || Instance.EmulatorId == "gamefix_online")
        {
            StatusMessage = "⏳ Removing existing Online fix before installing ReFix...";
            if (!string.IsNullOrWhiteSpace(Instance.InstallPath))
            {
                try
                {
                    await AntivirusExclusionHelper.RemoveFolderExclusionAsync(Instance.InstallPath, CancellationToken.None).ConfigureAwait(true);
                }
                catch { }
            }

            if (onlineLayer != null && _gameFixDeployService != null)
            {
                await _gameFixDeployService.UninstallFixLayerAsync(Instance, onlineLayer, null, CancellationToken.None).ConfigureAwait(true);
                var refreshed = await _instanceManager.GetByIdAsync(Instance.Id, CancellationToken.None).ConfigureAwait(true);
                if (refreshed != null) Instance = refreshed;
            }
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

            await LoadEmulatorsCoreAsync(forceReload: true).ConfigureAwait(true);
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

            await LoadEmulatorsCoreAsync(forceReload: true).ConfigureAwait(true);
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

            // Check if active emulator is an Online Fix layer
            var onlineLayer = Instance.InstalledFixLayers?.FirstOrDefault(l => l.IsOnline);
            if (onlineLayer != null || Instance.EmulatorId == "gamefix_online")
            {
                if (!string.IsNullOrWhiteSpace(Instance.InstallPath))
                {
                    try
                    {
                        await AntivirusExclusionHelper.RemoveFolderExclusionAsync(new[]
                        {
                            Instance.InstallPath,
                            Path.Combine(Path.GetTempPath(), "BlueStar_FixDeploy")
                        }, CancellationToken.None).ConfigureAwait(true);
                    }
                    catch { }
                }

                if (onlineLayer != null && _gameFixDeployService != null)
                {
                    await _gameFixDeployService.UninstallFixLayerAsync(Instance, onlineLayer, progressReporter, CancellationToken.None).ConfigureAwait(true);
                }
            }
            else
            {
                if (_emulatorLifecycleService != null)
                {
                    await _emulatorLifecycleService.UninstallEmulatorWithDlcPreservationAsync(
                        Instance, progressReporter, CancellationToken.None).ConfigureAwait(true);
                }
                else
                {
                    var refix = _emulatorRegistry.GetById("refix") as ReFixEmulator
                        ?? new ReFixEmulator(_logger as ILogger<ReFixEmulator> ?? LoggerFactory.Create(_ => {}).CreateLogger<ReFixEmulator>());
                    await refix.UninstallAsync(Instance, CancellationToken.None).ConfigureAwait(true);
                }
            }

            var refreshed = await _instanceManager.GetByIdAsync(Instance.Id, CancellationToken.None).ConfigureAwait(true);
            if (refreshed != null)
            {
                Instance = refreshed with
                {
                    EmulatorEnabled = false,
                    EmulatorId = null,
                    InstalledEmulatorVersion = null
                };
            }
            else
            {
                Instance = Instance with
                {
                    EmulatorEnabled = false,
                    EmulatorId = null,
                    InstalledEmulatorVersion = null
                };
            }
            await _instanceManager.UpdateAsync(Instance, CancellationToken.None).ConfigureAwait(true);

            StatusMessage = "✅ Emulator uninstalled. Original files restored.";
            _notificationService?.ShowInfo("Emulator Uninstalled", $"Original files restored in {Instance.Name}.");
            await LoadEmulatorsCoreAsync(forceReload: true).ConfigureAwait(true);
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
        var onlineLayer = Instance.InstalledFixLayers?.FirstOrDefault(l => l.IsOnline);
        var emuVer = onlineLayer?.Version ?? Instance.InstalledEmulatorVersion ?? "1.0";

        await _emulatorRatingService.SubmitVoteAsync(Instance.AppId, FeedbackOptionId, true, CancellationToken.None).ConfigureAwait(true);
        await _emulatorRatingService.RecordUserVoteFlagAsync(Instance, FeedbackOptionId, emuVer, CancellationToken.None).ConfigureAwait(true);
        IsFeedbackModalOpen = false;
        StatusMessage = "👍 Thank you for your feedback! Upvote recorded in community statistics.";
        await LoadEmulatorsCoreAsync(forceReload: true).ConfigureAwait(true);
    }

    [RelayCommand]
    public async Task SubmitFeedbackNegativeAsync()
    {
        if (Instance == null) return;
        var onlineLayer = Instance.InstalledFixLayers?.FirstOrDefault(l => l.IsOnline);
        var emuVer = onlineLayer?.Version ?? Instance.InstalledEmulatorVersion ?? "1.0";

        await _emulatorRatingService.SubmitVoteAsync(Instance.AppId, FeedbackOptionId, false, CancellationToken.None).ConfigureAwait(true);
        await _emulatorRatingService.RecordUserVoteFlagAsync(Instance, FeedbackOptionId, emuVer, CancellationToken.None).ConfigureAwait(true);
        ShowFeedbackUninstallPrompt = true;
        await LoadEmulatorsCoreAsync(forceReload: true).ConfigureAwait(true);
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
        if (Instance != null && !string.IsNullOrWhiteSpace(FeedbackOptionId))
        {
            var onlineLayer = Instance.InstalledFixLayers?.FirstOrDefault(l => l.IsOnline);
            var emuVer = onlineLayer?.Version ?? Instance.InstalledEmulatorVersion ?? "1.0";
            _ = _emulatorRatingService.RecordUserVoteFlagAsync(Instance, FeedbackOptionId, emuVer, CancellationToken.None);
        }
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

    /// <summary>
    /// Generates a unique instance name using Windows-style numbering: "Name (2)", "Name (3)", etc.
    /// </summary>
    private async Task<string> GenerateUniqueInstanceNameAsync(string baseName)
    {
        var allInstances = await _instanceManager.GetAllAsync(CancellationToken.None).ConfigureAwait(true);
        var existingNames = new HashSet<string>(allInstances.Select(i => i.Name), StringComparer.OrdinalIgnoreCase);

        // If the base name itself is available, use it
        if (!existingNames.Contains(baseName))
            return baseName;

        // Find next available number (2), (3), etc.
        int counter = 2;
        while (existingNames.Contains($"{baseName} ({counter})"))
        {
            counter++;
        }
        return $"{baseName} ({counter})";
    }

    /// <summary>
    /// Removes all emulator-related files (ReFix, Goldberg, SmokeAPI) from a game directory.
    /// </summary>
    private static void RemoveEmulatorFiles(string installPath)
    {
        if (string.IsNullOrWhiteSpace(installPath) || !Directory.Exists(installPath)) return;

        var searchOpts = new EnumerationOptions
        {
            MaxRecursionDepth = 6,
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            ReturnSpecialDirectories = false
        };

        // Remove steam_settings directories
        try
        {
            foreach (var dir in Directory.GetDirectories(installPath, "steam_settings", searchOpts))
            {
                try { Directory.Delete(dir, true); } catch { }
            }
        }
        catch { }

        // Remove emulator-specific files
        var emulatorFilePatterns = new[]
        {
            "ReFix.ini",
            "steam_api64_valve.dll", "steam_api_valve.dll",
            "steam_api64_o.dll", "steam_api_o.dll",
            "goldberg_steam_api64.dll", "goldberg_steam_api.dll",
            "local_save.txt",
            "CreamAPI.ini", "cream_api.ini",
            "SmokeAPI.config.json", "SmokeAPI64.dll", "SmokeAPI.dll"
        };

        foreach (var pattern in emulatorFilePatterns)
        {
            try
            {
                foreach (var file in Directory.GetFiles(installPath, pattern, searchOpts))
                {
                    try { File.Delete(file); } catch { }
                }
            }
            catch { }
        }

        // Restore original Steam API DLLs from backups if they exist
        try
        {
            foreach (var backupFile in Directory.GetFiles(installPath, "steam_api*_valve.dll", searchOpts))
            {
                var dir = Path.GetDirectoryName(backupFile)!;
                var originalName = Path.GetFileName(backupFile).Replace("_valve", "");
                var originalPath = Path.Combine(dir, originalName);
                try
                {
                    File.Copy(backupFile, originalPath, overwrite: true);
                    File.Delete(backupFile);
                }
                catch { }
            }
        }
        catch { }
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
            var cloneName = await GenerateUniqueInstanceNameAsync(baseName).ConfigureAwait(true);

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
            var cleanName = await GenerateUniqueInstanceNameAsync($"{baseName} - Limpia").ConfigureAwait(true);

            StatusMessage = $"⏳ Creando instancia limpia para '{baseName}'...";

            var baseDepot = _instanceManager.GetBaseDepotPath(Instance.AppId);
            if (Directory.Exists(baseDepot) && Directory.EnumerateFileSystemEntries(baseDepot).Any())
            {
                var cleanInstance = await _instanceManager.CreateInstanceFromDepotAsync(
                    Instance.AppId,
                    cleanName,
                    baseDepot,
                    ct: CancellationToken.None).ConfigureAwait(true);

                // Ensure clean instance has no emulator or DLC unlocker
                RemoveEmulatorFiles(cleanInstance.InstallPath);
                var cleanedFromDepot = cleanInstance with
                {
                    EmulatorEnabled = false,
                    EmulatorId = null,
                    InstalledEmulatorVersion = null,
                    DlcUnlockerInstalled = false,
                    UnlockedDlcIds = []
                };
                await _instanceManager.UpdateAsync(cleanedFromDepot, CancellationToken.None).ConfigureAwait(true);

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
                        foreach (var f in Directory.GetFiles(modsDir))
                        {
                            try { File.Delete(f); } catch { }
                        }
                    }

                    var modsUpper = Path.Combine(cloned.InstallPath, "Mods");
                    if (Directory.Exists(modsUpper))
                    {
                        foreach (var d in Directory.GetDirectories(modsUpper))
                        {
                            try { Directory.Delete(d, true); } catch { }
                        }
                        foreach (var f in Directory.GetFiles(modsUpper))
                        {
                            try { File.Delete(f); } catch { }
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

                    var ugcContent = Path.Combine(cloned.InstallPath, "steamapps", "workshop", "content");
                    if (Directory.Exists(ugcContent))
                    {
                        try { Directory.Delete(ugcContent, true); } catch { }
                    }

                    // Remove emulator files from the clean clone
                    RemoveEmulatorFiles(cloned.InstallPath);
                }
                catch { }

                // Reset emulator and DLC unlocker state for clean instance
                var cleanedInstance = cloned with
                {
                    EmulatorEnabled = false,
                    EmulatorId = null,
                    InstalledEmulatorVersion = null,
                    DlcUnlockerInstalled = false,
                    UnlockedDlcIds = []
                };
                await _instanceManager.UpdateAsync(cleanedInstance, CancellationToken.None).ConfigureAwait(true);

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

        OnPropertyChanged(nameof(SelectedDepotsCount));
        OnPropertyChanged(nameof(SelectedDepotsSize));

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
                var dlProgress = new Progress<DownloadProgress>(p =>
                {
                    StatusMessage = $"⏳ Fetching depot archive ({p.Percentage:F0}%)...";
                });
                var archivePath = await _apiClient.DownloadArchiveAsync(Instance.AppId, archivesDir, dlProgress, CancellationToken.None).ConfigureAwait(true);
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
            _notificationService?.ShowWarning("No Depots Selected", "Please select at least one depot to download.");
            return;
        }

        var downloadInstance = Instance with { Depots = combinedDepots.AsReadOnly() };
        StatusMessage = null;
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
        NotifyDownloadProps();
    }

    [RelayCommand]
    private async Task ResumeDownloadAsync()
    {
        if (Instance is null) return;
        await _downloadQueueManager.ResumeAsync(Instance.Id).ConfigureAwait(true);
        NotifyDownloadProps();
    }

    [RelayCommand]
    private async Task CancelDownloadAsync()
    {
        if (Instance is null) return;
        await _downloadQueueManager.CancelAsync(Instance.Id).ConfigureAwait(true);
        NotifyDownloadProps();
    }

    [RelayCommand]
    private async Task RetryDownloadAsync()
    {
        if (Instance is null) return;
        await _downloadQueueManager.RetryAsync(Instance.Id).ConfigureAwait(true);
        NotifyDownloadProps();
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
            var targetPath = dialog.FolderName;
            var cleanGameName = CleanName(Instance.Name) ?? Instance.Name;
            var exes = ShortcutHelper.FindGameExecutables(targetPath, cleanGameName);
            var exe = Instance.ExecutablePath;
            if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe))
            {
                exe = _engineDetector.FindPrimaryExecutable(targetPath, cleanGameName);
            }
            var status = (exes.Count > 0 || (exe != null && File.Exists(exe))) ? InstanceStatus.Ready : Instance.Status;
            var engine = await _engineDetector.DetectEngineAsync(targetPath, CancellationToken.None).ConfigureAwait(true) ?? Instance.Engine;

            Instance = Instance with
            {
                InstallPath = targetPath,
                ExecutablePath = exe,
                Status = status,
                Engine = engine
            };
            await _instanceManager.UpdateAsync(Instance, CancellationToken.None).ConfigureAwait(true);
            await _appSettings.SetLastInstallDirectoryAsync(dialog.FolderName).ConfigureAwait(true);
            StatusMessage = $"✅ Installation directory updated: {targetPath}";
            await LoadInstanceAsync(Instance).ConfigureAwait(true);
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
            customHeaderUrl: Instance?.HeaderImageUrl ?? Instance?.Metadata?.HeaderImageUrl,
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

    // ── Instance Game Files Uninstall Commands ──
    [RelayCommand]
    public void OpenUninstallModal() => IsUninstallModalOpen = true;

    [RelayCommand]
    public void CloseUninstallModal() => IsUninstallModalOpen = false;

    [RelayCommand]
    public async Task ConfirmUninstallGameFilesAsync()
    {
        if (Instance == null) return;

        IsUninstallingGameFiles = true;
        StatusMessage = "🗑 Uninstalling game files...";

        try
        {
            // 1. Cancel running download job for this instance
            _ = _downloadQueueManager.CancelAsync(Instance.Id);

            // 2. Remove all deployed fix layers
            if (_gameFixDeployService != null)
            {
                try
                {
                    await _gameFixDeployService.UninstallAllFixLayersAsync(Instance, null, CancellationToken.None).ConfigureAwait(true);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to uninstall fix layers during game uninstall for {Name}", Instance.Name);
                }
            }

            // 3. Remove DLC unlockers
            if (_dlcInstaller != null && Instance.Dlcs.Count > 0)
            {
                try
                {
                    await _dlcInstaller.UninstallDlcAsync(Instance, Instance.Dlcs[0], CancellationToken.None).ConfigureAwait(true);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to uninstall DLC unlocker during game uninstall for {Name}", Instance.Name);
                }
            }

            // 4. Delete game files on disk
            if (!string.IsNullOrWhiteSpace(Instance.InstallPath) && Directory.Exists(Instance.InstallPath))
            {
                var fullPath = Path.GetFullPath(Instance.InstallPath);
                var root = Path.GetPathRoot(fullPath);

                if (!string.Equals(fullPath, root, StringComparison.OrdinalIgnoreCase) && fullPath.Length > 4)
                {
                    _logger.LogInformation("Clearing game install directory on disk: {Path}", fullPath);
                    var di = new DirectoryInfo(fullPath);
                    foreach (var file in di.GetFiles("*", SearchOption.AllDirectories))
                    {
                        try
                        {
                            if ((file.Attributes & FileAttributes.ReadOnly) != 0)
                                file.Attributes &= ~FileAttributes.ReadOnly;
                            file.Delete();
                        }
                        catch { }
                    }
                    foreach (var subDir in di.GetDirectories())
                    {
                        try { subDir.Delete(recursive: true); } catch { }
                    }
                }
            }

            // 5. Reset downloaded status on instance depots and DLCs while preserving instance metadata
            var uninstalledDepots = (Instance.Depots ?? []).Select(d => d with { IsDownloaded = false }).ToList();
            var uninstalledDlcs = (Instance.Dlcs ?? []).Select(d => d with
            {
                IsInstalled = false,
                Depots = (d.Depots ?? []).Select(dp => dp with { IsDownloaded = false }).ToList().AsReadOnly()
            }).ToList();

            var updatedInstance = Instance with
            {
                Status = InstanceStatus.NotInstalled,
                Depots = uninstalledDepots.AsReadOnly(),
                Dlcs = uninstalledDlcs.AsReadOnly(),
                InstalledFixLayers = [],
                DlcUnlockerInstalled = false,
                UnlockedDlcIds = [],
                EmulatorEnabled = false,
                EmulatorId = null,
                UpdatedAt = DateTimeOffset.UtcNow
            };

            await _instanceManager.UpdateAsync(updatedInstance, CancellationToken.None).ConfigureAwait(true);
            await LoadInstanceAsync(updatedInstance).ConfigureAwait(true);

            IsUninstallModalOpen = false;
            _notificationService?.ShowSuccess(
                "Game Files Uninstalled",
                $"{Instance.Name} files deleted from disk. Instance remains configured in your BlueStar library.");
            StatusMessage = "🗑 Game files uninstalled successfully. Instance is ready for reinstallation.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to uninstall game files for {Name}", Instance.Name);
            StatusMessage = $"❌ Failed to uninstall game files: {ex.Message}";
            _notificationService?.ShowError("Uninstall Error", ex.Message);
        }
        finally
        {
            IsUninstallingGameFiles = false;
        }
    }

    #region Builds and Branches Management

    partial void OnSelectedBuildChanged(GameBuildInfo? oldValue, GameBuildInfo? newValue)
    {
        if (newValue == null || Instance == null) return;
        UpdateBuildBadge();

        if (newValue.DepotManifests.Count > 0)
        {
            var updated = (Instance.Depots ?? []).Select(d =>
            {
                if (newValue.DepotManifests.TryGetValue(d.DepotId, out var newManifestId) && newManifestId > 0)
                {
                    bool isStillDownloaded = d.IsDownloaded && (d.ManifestId == newManifestId);
                    return d with
                    {
                        ManifestId = newManifestId,
                        IsDownloaded = isStillDownloaded
                    };
                }
                return d;
            }).ToList();

            Instance = Instance with
            {
                Depots = updated.AsReadOnly()
            };

            for (int i = 0; i < Depots.Count; i++)
            {
                var dItem = Depots[i];
                var u = updated.FirstOrDefault(x => x.DepotId == dItem.Depot.DepotId);
                if (u != null)
                {
                    dItem.Depot = u;
                    dItem.NotifyDownloadedChanged();
                }
            }

            RecalculateSelectedSize();
            NotifyDownloadProps();

            if (oldValue != null && !IsLoadingBuilds)
            {
                SetTransientStatusMessage($"🎮 Build changed: {newValue.DisplayName}.", 3500);
            }
        }
    }

    private CancellationTokenSource? _statusMessageCts;

    private void SetTransientStatusMessage(string message, int durationMs = 3500)
    {
        _statusMessageCts?.Cancel();
        _statusMessageCts = new CancellationTokenSource();
        var token = _statusMessageCts.Token;

        StatusMessage = message;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(durationMs, token);
                if (!token.IsCancellationRequested)
                {
                    App.Current?.Dispatcher?.Invoke(() =>
                    {
                        if (StatusMessage == message)
                        {
                            StatusMessage = null;
                        }
                    });
                }
            }
            catch (TaskCanceledException) { }
        });
    }

    private void HandleDepotManifestUpdated(uint depotId, ulong newManifestId)
    {
        if (Instance == null) return;
        var updated = Instance.Depots.Select(d => d.DepotId == depotId ? d with { ManifestId = newManifestId, IsDownloaded = false } : d).ToList();
        Instance = Instance with { Depots = updated.AsReadOnly() };
        _ = _instanceManager.UpdateAsync(Instance, CancellationToken.None);
        RecalculateSelectedSize();
        NotifyDownloadProps();
        SetTransientStatusMessage($"✏ Updated Manifest ID for Depot {depotId} to {newManifestId}.", 3500);
    }

    [RelayCommand]
    public void OpenAddCustomBuildModal()
    {
        if (Instance == null) return;

        CustomBuildIdInput = string.Empty;
        CustomBuildNameInput = string.Empty;

        var items = Instance.Depots.Select(d => new CustomDepotManifestItem
        {
            DepotId = d.DepotId,
            DepotName = !string.IsNullOrWhiteSpace(d.Name) ? d.Name : $"Depot {d.DepotId}",
            CurrentManifestId = d.ManifestId,
            ManifestIdText = d.ManifestId > 0 ? d.ManifestId.ToString() : string.Empty
        }).ToList();

        CustomBuildDepots = new ObservableCollection<CustomDepotManifestItem>(items);
        IsCustomBuildModalOpen = true;
    }

    [RelayCommand]
    public void CloseAddCustomBuildModal()
    {
        IsCustomBuildModalOpen = false;
    }

    [RelayCommand]
    public void ImportManifestFilesForCustomBuild()
    {
        if (Instance == null) return;

        var ofd = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Import Steam .manifest Files for Custom Build",
            Filter = "Steam Manifest Files (*.manifest)|*.manifest|All Files (*.*)|*.*",
            Multiselect = true
        };

        if (ofd.ShowDialog() != true || ofd.FileNames.Length == 0) return;

        var instanceManifestDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "BlueStar", "instances", Instance.Id.ToString(), "manifests");
        Directory.CreateDirectory(instanceManifestDir);

        foreach (var file in ofd.FileNames)
        {
            var fileName = Path.GetFileName(file);
            var dest = Path.Combine(instanceManifestDir, fileName);
            try
            {
                File.Copy(file, dest, overwrite: true);
            }
            catch { }

            var match = System.Text.RegularExpressions.Regex.Match(fileName, @"^(\d+)_(\d+)\.manifest$");
            if (match.Success &&
                uint.TryParse(match.Groups[1].Value, out var dId) &&
                ulong.TryParse(match.Groups[2].Value, out var mId))
            {
                var target = CustomBuildDepots.FirstOrDefault(x => x.DepotId == dId);
                if (target != null)
                {
                    target.ManifestIdText = mId.ToString();
                    target.StatusText = "✓ Imported from file";
                }
            }
        }
    }

    [RelayCommand]
    public void SaveCustomBuild()
    {
        if (Instance == null) return;

        var map = new Dictionary<uint, ulong>();
        foreach (var d in CustomBuildDepots)
        {
            if (ulong.TryParse(d.ManifestIdText?.Trim(), out var mid) && mid > 0)
            {
                map[d.DepotId] = mid;
            }
            else if (d.CurrentManifestId > 0)
            {
                map[d.DepotId] = d.CurrentManifestId;
            }
        }

        var buildId = !string.IsNullOrWhiteSpace(CustomBuildIdInput) ? CustomBuildIdInput.Trim() : "Custom";
        var displayName = !string.IsNullOrWhiteSpace(CustomBuildNameInput)
            ? CustomBuildNameInput.Trim()
            : $"Build {buildId} (Custom)";

        var newBuild = new GameBuildInfo
        {
            BuildId = buildId,
            BranchName = "custom",
            DisplayName = displayName,
            UpdatedAt = DateTimeOffset.UtcNow,
            Description = "User configured build version & manifests",
            Source = "Custom",
            DepotManifests = map
        };

        AvailableBuilds.Insert(0, newBuild);
        SelectedBuild = newBuild;
        IsCustomBuildModalOpen = false;
        StatusMessage = $"✅ Switched to custom build: {newBuild.DisplayName}.";
    }

    [RelayCommand]
    public async Task ImportManifestFilesDirectlyAsync()
    {
        if (Instance == null) return;

        var ofd = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Import Steam .manifest Files",
            Filter = "Steam Manifest Files (*.manifest)|*.manifest|All Files (*.*)|*.*",
            Multiselect = true
        };

        if (ofd.ShowDialog() != true || ofd.FileNames.Length == 0) return;

        var instanceManifestDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "BlueStar", "instances", Instance.Id.ToString(), "manifests");
        Directory.CreateDirectory(instanceManifestDir);

        var importedDepots = new Dictionary<uint, ulong>();

        foreach (var file in ofd.FileNames)
        {
            var fileName = Path.GetFileName(file);
            var dest = Path.Combine(instanceManifestDir, fileName);
            try
            {
                File.Copy(file, dest, overwrite: true);
            }
            catch { }

            var match = System.Text.RegularExpressions.Regex.Match(fileName, @"^(\d+)_(\d+)\.manifest$");
            if (match.Success &&
                uint.TryParse(match.Groups[1].Value, out var dId) &&
                ulong.TryParse(match.Groups[2].Value, out var mId))
            {
                importedDepots[dId] = mId;
            }
        }

        if (importedDepots.Count > 0)
        {
            var updatedDepots = Instance.Depots.Select(d =>
            {
                if (importedDepots.TryGetValue(d.DepotId, out var newMId))
                {
                    return d with { ManifestId = newMId, IsDownloaded = false };
                }
                return d;
            }).ToList();

            Instance = Instance with { Depots = updatedDepots.AsReadOnly() };
            await _instanceManager.UpdateAsync(Instance, CancellationToken.None).ConfigureAwait(true);
            await LoadInstanceAsync(Instance).ConfigureAwait(true);

            _notificationService?.ShowSuccess(
                "Manifests Imported",
                $"Imported {importedDepots.Count} manifest file(s) for {Instance.Name}. Ready to download or switch.");
            StatusMessage = $"📁 Imported {importedDepots.Count} manifest file(s).";
        }
    }

    [RelayCommand]
    public void ToggleCommunityLinksMenu()
    {
        IsCommunityLinksMenuOpen = !IsCommunityLinksMenuOpen;
    }

    [RelayCommand]
    public void CloseCommunityLinksMenu()
    {
        IsCommunityLinksMenuOpen = false;
    }

    [RelayCommand]
    public void OpenCommunityLink(string? linkType)
    {
        IsCommunityLinksMenuOpen = false;
        if (Instance == null) return;

        try
        {
            switch (linkType?.ToLowerInvariant())
            {
                case "steamdb_depots":
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo($"https://steamdb.info/app/{Instance.AppId}/depots/") { UseShellExecute = true });
                    break;
                case "steamdb_patches":
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo($"https://steamdb.info/app/{Instance.AppId}/patchnotes/") { UseShellExecute = true });
                    break;
                case "csrinru":
                    var query = Uri.EscapeDataString($"{Instance.Name} manifest");
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo($"https://cs.rin.ru/forum/search.php?keywords={query}&terms=all&author=&sc=1&sf=all&sk=t&sd=d&sr=topics&st=0&ch=300&t=0&submit=Search") { UseShellExecute = true });
                    break;
                case "depotbox":
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://depotbox.org") { UseShellExecute = true });
                    break;
                case "manifest_dir":
                    var manifestDir = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                        "BlueStar", "instances", Instance.Id.ToString(), "manifests");
                    Directory.CreateDirectory(manifestDir);
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(manifestDir) { UseShellExecute = true });
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to open link: {LinkType}", linkType);
        }
    }

    [RelayCommand]
    public async Task ImportDepotZipPackageAsync(string? zipPath = null)
    {
        if (Instance == null) return;

        if (string.IsNullOrWhiteSpace(zipPath))
        {
            var ofd = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Import Depots / Manifests ZIP Package",
                Filter = "Depot ZIP Archive (*.zip)|*.zip|Steam Manifest (*.manifest)|*.manifest|All Files (*.*)|*.*",
                Multiselect = false
            };

            if (ofd.ShowDialog() != true || string.IsNullOrWhiteSpace(ofd.FileName)) return;
            zipPath = ofd.FileName;
        }

        var instanceManifestDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "BlueStar", "instances", Instance.Id.ToString(), "manifests");
        Directory.CreateDirectory(instanceManifestDir);

        if (zipPath.EndsWith(".manifest", StringComparison.OrdinalIgnoreCase))
        {
            // Single manifest file drop/selection
            var fileName = Path.GetFileName(zipPath);
            var dest = Path.Combine(instanceManifestDir, fileName);
            try { File.Copy(zipPath, dest, overwrite: true); } catch { }

            var match = System.Text.RegularExpressions.Regex.Match(fileName, @"^(\d+)_(\d+)\.manifest$");
            if (match.Success &&
                uint.TryParse(match.Groups[1].Value, out var dId) &&
                ulong.TryParse(match.Groups[2].Value, out var mId))
            {
                HandleDepotManifestUpdated(dId, mId);
                _notificationService?.ShowSuccess(
                    "Manifest Imported",
                    $"Manifest for Depot {dId} imported ({mId}).");
            }
            return;
        }

        IsLoadingBuilds = true;
        StatusMessage = "⏳ Extracting and analyzing depot package...";

        try
        {
            var result = await BlueStar.Infrastructure.Services.DepotPackageZipImporter.ImportZipAsync(
                zipPath, instanceManifestDir, CancellationToken.None).ConfigureAwait(true);

            if (!result.Success)
            {
                _notificationService?.ShowWarning(
                    "Import Failed",
                    result.ErrorMessage ?? "No manifests or depot information found inside the ZIP package.");
                StatusMessage = "⚠️ Could not find manifests in ZIP package.";
                return;
            }

            // Update Instance Depots with manifests and keys
            var updatedDepots = Instance.Depots.Select(d =>
            {
                ulong newMId = d.ManifestId;
                string? newKey = d.DepotKey;
                bool changed = false;

                if (result.ManifestMap.TryGetValue(d.DepotId, out var parsedMId) && parsedMId > 0)
                {
                    newMId = parsedMId;
                    changed = true;
                }
                if (result.DepotKeys.TryGetValue(d.DepotId, out var parsedKey) && !string.IsNullOrWhiteSpace(parsedKey))
                {
                    newKey = parsedKey;
                    changed = true;
                }

                return changed ? d with { ManifestId = newMId, DepotKey = newKey, IsDownloaded = false } : d;
            }).ToList();

            // Also check for any new depots in result not in current list
            foreach (var (mDepotId, mId) in result.ManifestMap)
            {
                if (!updatedDepots.Any(d => d.DepotId == mDepotId))
                {
                    result.DepotKeys.TryGetValue(mDepotId, out var dKey);
                    updatedDepots.Add(new DepotInfo
                    {
                        DepotId = mDepotId,
                        ManifestId = mId,
                        Name = $"Depot {mDepotId}",
                        DepotKey = dKey,
                        IsDownloaded = false
                    });
                }
            }

            Instance = Instance with { Depots = updatedDepots.AsReadOnly() };
            await _instanceManager.UpdateAsync(Instance, CancellationToken.None).ConfigureAwait(true);

            // Create and activate build
            var newBuild = new GameBuildInfo
            {
                BuildId = result.BuildId ?? "Custom",
                BranchName = "imported_zip",
                DisplayName = $"📦 {result.BuildName}",
                UpdatedAt = DateTimeOffset.UtcNow,
                Description = $"{result.ExtractedManifestFiles.Count} manifests extracted from {Path.GetFileName(zipPath)}",
                Source = "Imported ZIP",
                DepotManifests = result.ManifestMap
            };

            AvailableBuilds.Insert(0, newBuild);
            SelectedBuild = newBuild;
            UpdateBuildBadge();

            await LoadInstanceAsync(Instance).ConfigureAwait(true);

            _notificationService?.ShowSuccess(
                "Package Imported Successfully",
                $"Imported {result.ExtractedManifestFiles.Count} manifests and {result.DepotKeys.Count} keys from {Path.GetFileName(zipPath)}. Target Build: {result.BuildId}");
            StatusMessage = $"📦 Ready to download {result.BuildName} ({result.ExtractedManifestFiles.Count} manifests).";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to import depot zip package {Path}", zipPath);
            _notificationService?.ShowError("Import Error", $"Failed to import ZIP: {ex.Message}");
            StatusMessage = $"❌ Error importing ZIP: {ex.Message}";
        }
        finally
        {
            IsLoadingBuilds = false;
        }
    }

    private void UpdateBuildBadge()
    {
        if (SelectedBuild != null)
        {
            ActiveBuildBadgeText = string.IsNullOrWhiteSpace(SelectedBuild.BuildId)
                ? SelectedBuild.BranchName
                : $"Build {SelectedBuild.BuildId} ({SelectedBuild.BranchName})";
        }
        else
        {
            ActiveBuildBadgeText = null;
        }
    }

    [RelayCommand]
    public async Task LoadAvailableBuildsAsync()
    {
        if (Instance == null || Instance.AppId == 0) return;

        IsLoadingBuilds = true;
        try
        {
            var list = new List<GameBuildInfo>();

            // 1. Fetch steam branches/builds from SteamStoreApiClient
            if (_metadataProvider is BlueStar.Infrastructure.Metadata.SteamStoreApiClient steamClient)
            {
                var steamBuilds = await steamClient.GetAppBuildsAsync(Instance.AppId, CancellationToken.None).ConfigureAwait(true);
                list.AddRange(steamBuilds);
            }

            // 2. Fetch DepotBox manifests build
            if (_apiClient != null)
            {
                try
                {
                    var depotBoxManifests = await _apiClient.GetManifestsAsync(Instance.AppId, CancellationToken.None).ConfigureAwait(true);
                    if (depotBoxManifests.Count > 0)
                    {
                        var map = depotBoxManifests.ToDictionary(m => m.DepotId, m => m.ManifestId);
                        list.Add(new GameBuildInfo
                        {
                            BuildId = "DepotBox",
                            BranchName = "depotbox",
                            DisplayName = "DepotBox Archive Build (Verified)",
                            UpdatedAt = DateTimeOffset.UtcNow,
                            Description = $"{depotBoxManifests.Count} verified manifests hosted on DepotBox",
                            Source = "DepotBox",
                            DepotManifests = map
                        });
                    }
                }
                catch { }
            }

            // 3. Scan local instance manifests directory
            var instanceManifestDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "BlueStar", "instances", Instance.Id.ToString(), "manifests");

            if (Directory.Exists(instanceManifestDir))
            {
                var files = Directory.GetFiles(instanceManifestDir, "*.manifest");
                var localManifestMap = new Dictionary<uint, ulong>();
                foreach (var f in files)
                {
                    var match = System.Text.RegularExpressions.Regex.Match(Path.GetFileName(f), @"^(\d+)_(\d+)\.manifest$");
                    if (match.Success &&
                        uint.TryParse(match.Groups[1].Value, out var dId) &&
                        ulong.TryParse(match.Groups[2].Value, out var mId))
                    {
                        localManifestMap[dId] = mId;
                    }
                }

                if (localManifestMap.Count > 0 && !list.Any(b => b.BranchName == "local_archive"))
                {
                    list.Add(new GameBuildInfo
                    {
                        BuildId = "Archive",
                        BranchName = "local_archive",
                        DisplayName = "Local Imported Manifests Archive",
                        UpdatedAt = DateTimeOffset.UtcNow,
                        Description = $"{localManifestMap.Count} local .manifest files present in instance storage",
                        Source = "Local Storage",
                        DepotManifests = localManifestMap
                    });
                }
            }

            // 4. Fallback: Add current instance configuration as a build if list is empty
            if (list.Count == 0 && Instance.Depots.Count > 0)
            {
                var map = Instance.Depots.ToDictionary(d => d.DepotId, d => d.ManifestId);
                list.Add(new GameBuildInfo
                {
                    BuildId = "Current",
                    BranchName = "public",
                    DisplayName = "Installed / Configured Build",
                    UpdatedAt = BlueStar.Infrastructure.Services.GameUpdateDetectionHelper.GetInstalledManifestDate(Instance),
                    Source = "Local",
                    DepotManifests = map
                });
            }

            AvailableBuilds = new ObservableCollection<GameBuildInfo>(list);

            // Select current build or first
            var current = list.FirstOrDefault(b => b.BranchName == "public") ?? list.FirstOrDefault();
            SelectedBuild = current;
            UpdateBuildBadge();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load available builds for {AppId}", Instance.AppId);
        }
        finally
        {
            IsLoadingBuilds = false;
        }
    }

    #endregion

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

        _downloadQueueManager.Queue.CollectionChanged -= OnQueueChanged;
        _instanceManager.InstancesChanged -= OnInstanceManagerInstancesChanged;
        _gameLauncher.RunningStateChanged -= OnGameRunningStateChanged;
        _gameLauncher.LogReceived -= OnGameLogReceived;
        if (_settingsChangedHandler != null)
        {
            _appSettings.SettingsChanged -= _settingsChangedHandler;
        }

        if (ActiveJob != null)
        {
            ActiveJob.PropertyChanged -= OnActiveJobPropertyChanged;
        }
    }
}
