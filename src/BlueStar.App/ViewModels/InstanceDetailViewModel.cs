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
using BlueStar.App.Services;

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
/// A previously installed build offered for rollback, together with whether it can still be
/// restored from the manifests that remain in the cache.
/// </summary>
public partial class RollbackBuildItem : ObservableObject
{
    public required InstalledBuildSnapshot Snapshot { get; init; }

    /// <summary>Gets how many of the build's manifests are still available locally.</summary>
    public int CachedManifestCount { get; init; }

    /// <summary>Gets how many manifests the build needs in total.</summary>
    public int TotalManifestCount { get; init; }

    /// <summary>Gets whether this entry is the build currently installed.</summary>
    public bool IsCurrentBuild { get; init; }

    /// <summary>Gets the depots whose manifest could not be found.</summary>
    public IReadOnlyList<uint> MissingDepotIds { get; init; } = [];

    public string BuildId => Snapshot.BuildId;
    public string DateText => Snapshot.FormattedDate;
    public string SizeText => Snapshot.FormattedSize;
    public string InstalledAtText => Snapshot.InstalledAt.LocalDateTime.ToString("d MMM yyyy");

    public string TitleText => $"{Snapshot.BuildId} · {Snapshot.FormattedDate}";

    /// <summary>Gets whether every manifest this build needs is recoverable.</summary>
    public bool IsFullyCached => TotalManifestCount > 0 && CachedManifestCount >= TotalManifestCount;

    /// <summary>Gets whether the rollback button is offered for this entry.</summary>
    public bool CanRestore => IsFullyCached && !IsCurrentBuild;

    public string CacheBadgeText => IsCurrentBuild
        ? "current"
        : $"{CachedManifestCount}/{TotalManifestCount} cached";

    public string DetailText => IsCurrentBuild
        ? $"installed {InstalledAtText} · {TotalManifestCount} depots · {SizeText}"
        : IsFullyCached
            ? $"installed {InstalledAtText} · {TotalManifestCount} depots · {SizeText}"
            : $"missing manifests for depot(s) {string.Join(", ", MissingDepotIds)}";
}

/// <summary>
/// Represents a known manifest revision option for a depot in the custom build editor.
/// </summary>
public record DepotManifestOption
{
    public ulong ManifestId { get; init; }
    public string DisplayText { get; init; } = string.Empty;
    public string Source { get; init; } = string.Empty;
    public string BranchName { get; init; } = string.Empty;
    public string BuildId { get; init; } = string.Empty;

    /// <summary>
    /// Gets whether this manifest can be obtained right now — it is in the local cache or some
    /// provider advertises a route to it. Options WITHOUT backing are still listed on purpose:
    /// the user can pick one and then go looking for it.
    /// </summary>
    public bool IsBacked { get; init; }

    /// <summary>
    /// Gets the bare numeric id. This is what an editable ComboBox must put in its text box —
    /// binding the text to <see cref="DisplayText"/> wrote "12345 (Current - Steam)" into the
    /// field and made every manifest look invalid.
    /// </summary>
    public string ManifestIdString => ManifestId.ToString();

    /// <summary>Gets a short note about where the manifest would come from.</summary>
    public string AvailabilityNote => IsBacked ? Source : "not downloaded yet";

    /// <summary>
    /// An editable ComboBox falls back to ToString() for the text it puts in its edit box, and
    /// that text is bound straight to the value we validate. Returning the label here is what made
    /// a freshly opened Custom tab report "Current build · from Steam" as an invalid manifest id,
    /// so the bare id is what this returns; the dropdown keeps using DisplayText via its template.
    /// </summary>
    public override string ToString() => ManifestIdString;
}

/// <summary>
/// Model for a depot item with selection state and manifest versioning in the UI.
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

    [ObservableProperty]
    private bool _isManifestLocked;

    [ObservableProperty]
    private bool _isKeyAvailable;

    [ObservableProperty]
    private bool _isCachedLocally;

    [ObservableProperty]
    private string _sourceProviderName = "Auto";

    [ObservableProperty]
    private ObservableCollection<DepotManifestOption> _knownManifestVersions = [];

    [ObservableProperty]
    private DepotManifestOption? _selectedManifestOption;

    [ObservableProperty]
    private string _manifestInputText = string.Empty;

    [ObservableProperty]
    private string _availabilityStatus = "-";

    [ObservableProperty]
    private string _availabilityBadgeColor = "#94A3B8";

    [ObservableProperty]
    private bool _isCheckingAvailability;

    public Func<uint, ulong, Task<(bool IsCached, string ProviderName, bool IsAvailable)>>? AvailabilityChecker;

    partial void OnIsSelectedChanged(bool value) => OnSelectionChanged?.Invoke();

    partial void OnSelectedManifestOptionChanged(DepotManifestOption? value)
    {
        if (value != null && value.ManifestId > 0)
        {
            ManifestInputText = value.ManifestId.ToString();
            ApplyManifestId(value.ManifestId);
            _ = CheckAvailabilityForIdAsync(value.ManifestId);
        }
    }

    /// <summary>Raised so the owning view model can re-run custom-build validation as you type.</summary>
    public Action? OnManifestTextChanged { get; set; }

    partial void OnManifestInputTextChanged(string value)
    {
        OnManifestTextChanged?.Invoke();

        if (InstanceDetailViewModel.TryParseManifestId(value, out var parsedId))
        {
            var match = KnownManifestVersions.FirstOrDefault(o => o.ManifestId == parsedId);
            if (match != null && SelectedManifestOption != match)
            {
                _selectedManifestOption = match;
                OnPropertyChanged(nameof(SelectedManifestOption));
            }
        }
    }

    public bool IsDownloaded => Depot?.IsDownloaded ?? false;

    public void NotifyDownloadedChanged() => OnPropertyChanged(nameof(IsDownloaded));

    /// <summary>Re-raises the platform/architecture badges after a depot correction.</summary>
    public void NotifyPlatformChanged()
    {
        OnPropertyChanged(nameof(PlatformTag));
        OnPropertyChanged(nameof(ArchitectureTag));
        OnPropertyChanged(nameof(CategoryTag));
    }

    public ulong ManifestId => Depot?.ManifestId ?? 0;
    public string ManifestIdText => Depot?.ManifestId > 0 ? Depot.ManifestId.ToString() : "Latest";
    public string ManifestIdHex => Depot?.ManifestId > 0 ? $"0x{Depot.ManifestId:X16}" : "-";

    public string LockIcon => IsManifestLocked ? "🔒" : "🔓";
    public string LockTooltip => IsManifestLocked ? "Manifest locked (won't be changed by auto-updates)" : "Manifest unlocked (click to pin)";
    public string KeyStatusBadge => IsKeyAvailable ? "🔑 AES Key: OK" : "⚠ Key Required";
    public string CacheStatusBadge => IsCachedLocally ? "💾 Local Cache" : $"🌐 {SourceProviderName}";

    [RelayCommand]
    public void ToggleManifestLock()
    {
        IsManifestLocked = !IsManifestLocked;
        OnPropertyChanged(nameof(LockIcon));
        OnPropertyChanged(nameof(LockTooltip));
    }

    [RelayCommand]
    public async Task VerifyAvailabilityAsync()
    {
        if (ulong.TryParse(ManifestInputText?.Trim(), out var parsedId) && parsedId > 0)
        {
            ApplyManifestId(parsedId);
            await CheckAvailabilityForIdAsync(parsedId).ConfigureAwait(true);
        }
        else if (Depot?.ManifestId > 0)
        {
            await CheckAvailabilityForIdAsync(Depot.ManifestId).ConfigureAwait(true);
        }
    }

    public async Task CheckAvailabilityForIdAsync(ulong manifestId)
    {
        if (manifestId == 0 || AvailabilityChecker == null)
        {
            AvailabilityStatus = "-";
            AvailabilityBadgeColor = "#94A3B8";
            return;
        }

        IsCheckingAvailability = true;
        try
        {
            var (isCached, providerName, isAvailable) = await AvailabilityChecker(Depot.DepotId, manifestId).ConfigureAwait(true);
            IsCachedLocally = isCached;
            if (isCached)
            {
                AvailabilityStatus = "💾 Local Cache";
                AvailabilityBadgeColor = "#10B981";
                SourceProviderName = "Local Cache";
            }
            else if (isAvailable)
            {
                AvailabilityStatus = $"🌐 {(!string.IsNullOrWhiteSpace(providerName) ? providerName : "Available")}";
                AvailabilityBadgeColor = "#3B82F6";
                SourceProviderName = providerName;
            }
            else
            {
                AvailabilityStatus = "❌ Not in Providers";
                AvailabilityBadgeColor = "#EF4444";
                SourceProviderName = "None";
            }
        }
        catch
        {
            AvailabilityStatus = "❓ Unknown";
            AvailabilityBadgeColor = "#F59E0B";
        }
        finally
        {
            IsCheckingAvailability = false;
            OnPropertyChanged(nameof(CacheStatusBadge));
        }
    }

    public void ApplyManifestId(ulong newManifestId)
    {
        if (Depot != null && newManifestId > 0 && newManifestId != Depot.ManifestId)
        {
            Depot = Depot with { ManifestId = newManifestId, IsDownloaded = false };
            NotifyDownloadedChanged();
            OnPropertyChanged(nameof(ManifestId));
            OnPropertyChanged(nameof(ManifestIdText));
            OnPropertyChanged(nameof(ManifestIdHex));
            OnManifestUpdated?.Invoke(Depot.DepotId, newManifestId);
        }
    }

    [RelayCommand]
    public void SelectManifestVersion(ulong version)
    {
        if (version > 0)
        {
            ApplyManifestId(version);
            _ = CheckAvailabilityForIdAsync(version);
        }
    }

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
        if (ulong.TryParse(EditingManifestId?.Trim(), out var parsedId) && parsedId > 0)
        {
            ApplyManifestId(parsedId);
            _ = CheckAvailabilityForIdAsync(parsedId);
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

    [ObservableProperty]
    private bool _isUnlocked;

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
                _ => string.Empty
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
    private string _sourceProvider = "Auto";

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
    private readonly ILocalizationService? _localizationService;
    private readonly EventHandler? _languageChangedHandler;

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
    public bool IsDepotBoxTabsVisible => Instance != null && Instance.CanManageDepots;


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

    partial void OnIsDlcUnlockedChanged(bool value)
    {
        OnPropertyChanged(nameof(DlcActionButtonText));
        OnPropertyChanged(nameof(CanDownloadAndInstallDlcs));
    }

    /// <summary>Dynamic button text for DLC installation, download, or uninstallation.</summary>
    public string DlcActionButtonText
    {
        get
        {
            if (IsDlcUnlocked)
            {
                return GetResourceString("String_Uninstall", "Uninstall");
            }
            if (!HasDlcsWithDepotsToDownload)
            {
                return GetResourceString("String_InstallDlcs", "Install DLCs");
            }
            return GetResourceString("String_DownloadAndInstallDlcs", "Download & Install DLCs");
        }
    }

    private string GetResourceString(string key, string fallback)
    {
        if (_localizationService != null)
        {
            return _localizationService.GetString(key, fallback);
        }
        try
        {
            if (Application.Current?.TryFindResource(key) is string val)
                return val;
        }
        catch { }
        return fallback;
    }

    [ObservableProperty]
    private string _totalSelectedSizeFormatted = "0 KB";

    [ObservableProperty]
    private long _totalSelectedSizeBytes;

    /// <summary>Number of currently checked base depots shown in the Files &amp; Depots bottom bar.</summary>
    public int SelectedDepotsCount => Depots.Count(d => d.IsSelected);

    /// <summary>Formatted total size of selected base depots — alias used by the bottom summary bar.</summary>
    public string SelectedDepotsSize => TotalSelectedSizeFormatted;

    /// <summary>Number of currently selected DLCs shown in the DLCs tab.</summary>
    public int SelectedDlcsCount => Dlcs.Count(d => d.IsSelected);

    /// <summary>Formatted total size of selected DLCs.</summary>
    public string SelectedDlcsSize => FormatFileSize(Dlcs.Where(d => d.IsSelected).Sum(d => d.Dlc.TotalSizeBytes));

    /// <summary>Whether any selected DLC has content depots not yet downloaded.</summary>
    public bool HasDlcsWithDepotsToDownload =>
        Dlcs.Any(d => d.IsSelected && d.Dlc.Depots.Any(dep => !dep.IsDownloaded && dep.SizeBytes > 0));

    /// <summary>Whether DLC download and installation can proceed.</summary>
    public bool CanDownloadAndInstallDlcs =>
        Dlcs.Count > 0 && SelectedDlcsCount > 0 && !IsProcessing &&
        (!IsInstanceDownloading || ActiveJob?.IsPaused == true || ActiveJob?.IsCompleted == true || ActiveJob?.IsFailed == true);



    // ── Version & Update Comparison State ──
    [ObservableProperty]
    private string _installedVersionText = "Not installed";

    [ObservableProperty]
    private string _latestVersionText = CheckingSentinel;

    [ObservableProperty]
    private DateTimeOffset? _installedVersionDate;

    [ObservableProperty]
    private DateTimeOffset? _latestVersionDate;

    [ObservableProperty]
    private bool _hasGameUpdateAvailable;

    [ObservableProperty]
    private bool _isCheckingGameUpdate;

    /// <summary>
    /// True once a depot scan has found manifests newer than the installed ones. Survives closing
    /// the update modal, so dismissing the dialog no longer counts as having installed the update.
    /// </summary>
    [ObservableProperty]
    private bool _hasPendingDepotUpdate;

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
    [NotifyPropertyChangedFor(nameof(LatestBuildIdText))]
    [NotifyPropertyChangedFor(nameof(LatestBuildBranchText))]
    [NotifyPropertyChangedFor(nameof(LatestBuildDateText))]
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

    /// <summary>
    /// When true, BlueStar stops looking for new builds for this instance (background library scan,
    /// automatic detail-view checks and update badges). Manual checks stay available.
    /// </summary>
    [ObservableProperty]
    private bool _disableUpdateChecks;

    partial void OnDisableUpdateChecksChanged(bool value)
    {
        if (Instance == null || Instance.DisableUpdateChecks == value) return;

        Instance = Instance with
        {
            DisableUpdateChecks = value,
            // Clearing the flag as soon as checks are disabled stops stale "update available"
            // badges from sticking around forever in the library.
            HasUpdateAvailable = value ? false : Instance.HasUpdateAvailable,
            UpdateDescription = value ? null : Instance.UpdateDescription
        };

        if (value)
        {
            HasGameUpdateAvailable = false;
        }

        _ = _instanceManager.UpdateAsync(Instance, CancellationToken.None);

        StatusMessage = value
            ? "🔕 Update checks disabled for this instance."
            : "🔔 Update checks enabled for this instance.";
    }

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
        return Depots.Where(d => d.IsSelected).Select(d => d.Depot).DistinctBy(d => d.DepotId).ToList();
    }

    public bool AreAllSelectedDepotsDownloaded
    {
        get
        {
            var selected = Depots.Where(d => d.IsSelected).ToList();
            if (selected.Count == 0) return false;
            return selected.All(d => d.Depot.IsDownloaded);
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

    // ── One-Click Install Modal State ──

    [ObservableProperty]
    private bool _isInstallModalOpen;

    [ObservableProperty]
    private string _installModalPath = string.Empty;

    [ObservableProperty]
    private string _installModalFreeSpaceText = string.Empty;

    [ObservableProperty]
    private bool _installModalHasEnoughSpace = true;

    [ObservableProperty]
    private string _installModalRequiredSpaceText = string.Empty;

    [ObservableProperty]
    private string _installModalSelectedBranch = "public";

    [ObservableProperty]
    private string _installModalTargetVersionMode = "Latest";

    public bool InstallModalHasRecommendedVersion => HasCurationRecommendation;

    public bool IsInstallModalVersionRecommended
    {
        get => string.Equals(InstallModalTargetVersionMode, "Recommended", StringComparison.OrdinalIgnoreCase);
        set
        {
            if (value && !string.Equals(InstallModalTargetVersionMode, "Recommended", StringComparison.OrdinalIgnoreCase))
            {
                InstallModalTargetVersionMode = "Recommended";
                NotifyInstallModalVersionModeChanged();
            }
            else if (!value && string.Equals(InstallModalTargetVersionMode, "Recommended", StringComparison.OrdinalIgnoreCase))
            {
                // Clicked off: no mode pinned, which installs the public branch as it comes.
                InstallModalTargetVersionMode = string.Empty;
                NotifyInstallModalVersionModeChanged();
            }
        }
    }

    public bool IsInstallModalVersionLatest
    {
        get => string.Equals(InstallModalTargetVersionMode, "Latest", StringComparison.OrdinalIgnoreCase);
        set
        {
            if (value && !string.Equals(InstallModalTargetVersionMode, "Latest", StringComparison.OrdinalIgnoreCase))
            {
                InstallModalTargetVersionMode = "Latest";
                NotifyInstallModalVersionModeChanged();
            }
            else if (!value && string.Equals(InstallModalTargetVersionMode, "Latest", StringComparison.OrdinalIgnoreCase))
            {
                // Clicked off: no mode pinned, which installs the public branch as it comes.
                InstallModalTargetVersionMode = string.Empty;
                NotifyInstallModalVersionModeChanged();
            }
        }
    }

    public bool IsInstallModalVersionCustom
    {
        get => string.Equals(InstallModalTargetVersionMode, "Custom", StringComparison.OrdinalIgnoreCase);
        set
        {
            if (value && !string.Equals(InstallModalTargetVersionMode, "Custom", StringComparison.OrdinalIgnoreCase))
            {
                InstallModalTargetVersionMode = "Custom";
                NotifyInstallModalVersionModeChanged();
            }
            else if (!value && string.Equals(InstallModalTargetVersionMode, "Custom", StringComparison.OrdinalIgnoreCase))
            {
                // Clicked off: no mode pinned, which installs the public branch as it comes.
                InstallModalTargetVersionMode = string.Empty;
                NotifyInstallModalVersionModeChanged();
            }
        }
    }

    public void NotifyInstallModalVersionModeChanged()
    {
        OnPropertyChanged(nameof(InstallModalTargetVersionMode));
        OnPropertyChanged(nameof(IsInstallModalVersionRecommended));
        OnPropertyChanged(nameof(IsInstallModalVersionLatest));
        OnPropertyChanged(nameof(IsInstallModalVersionCustom));
        UpdateInstallModalDiskSpace();
    }

    [ObservableProperty]
    private ObservableCollection<string> _installModalAvailableBranches = [];

    [ObservableProperty]
    private ObservableCollection<InstallModalDlcItem> _installModalDlcs = [];

    [ObservableProperty]
    private bool _installModalSelectAllDlcs = true;

    [ObservableProperty]
    private string _installModalDlcMethod = string.Empty;

    /// <summary>True when at least one optional DLC is ticked in the install modal.</summary>
    public bool InstallModalHasSelectedDlcs => InstallModalDlcs != null && InstallModalDlcs.Any(d => d.IsSelected);

    /// <summary>True when the title has no depots at all, so nothing can be downloaded.</summary>
    [ObservableProperty]
    private bool _installModalHasNoDepots;

    // ── "Force DLC unlocker without detected DLCs" confirmation ──

    [ObservableProperty]
    private bool _isForceDlcUnlockerConfirmOpen;

    /// <summary>The method the person picked while no DLC was selected, pending confirmation.</summary>
    private string? _pendingForcedDlcMethod;

    /// <summary>Set while the confirmation flow writes the method, so the radio setters do not re-ask.</summary>
    private bool _suppressDlcMethodConfirm;

    /// <summary>
    /// Applies a DLC unlocker method, asking first when the game has no DLC selected: installing a
    /// wrapper for a title with no detected DLC is a deliberate choice, not a default.
    /// </summary>
    private void RequestDlcUnlockerMethod(string method)
    {
        if (_suppressDlcMethodConfirm)
        {
            InstallModalDlcMethod = method;
            return;
        }

        if (InstallModalHasSelectedDlcs)
        {
            InstallModalDlcMethod = method;
            return;
        }

        _pendingForcedDlcMethod = method;
        IsForceDlcUnlockerConfirmOpen = true;

        // Nothing is selected until the person confirms, so bounce the radio back.
        NotifyDlcMethodChanged();
    }

    [RelayCommand]
    public void ConfirmForceDlcUnlocker()
    {
        var method = _pendingForcedDlcMethod;
        _pendingForcedDlcMethod = null;
        IsForceDlcUnlockerConfirmOpen = false;

        if (string.IsNullOrWhiteSpace(method)) return;

        _suppressDlcMethodConfirm = true;
        try
        {
            InstallModalDlcMethod = method;
            _forcedDlcMethodAccepted = true;
        }
        finally
        {
            _suppressDlcMethodConfirm = false;
        }

        NotifyDlcMethodChanged();
    }

    [RelayCommand]
    public void CancelForceDlcUnlocker()
    {
        _pendingForcedDlcMethod = null;
        IsForceDlcUnlockerConfirmOpen = false;
        NotifyDlcMethodChanged();
    }

    private void NotifyDlcMethodChanged()
    {
        OnPropertyChanged(nameof(IsModalCreamApiSelected));
        OnPropertyChanged(nameof(IsModalSmokeApiSelected));
        OnPropertyChanged(nameof(InstallModalHasSelectedDlcs));
    }

    [ObservableProperty]
    private string _installModalSelectedEmulator = "refix_valve";

    public bool IsInstallModalEmulatorNone
    {
        get => string.Equals(InstallModalSelectedEmulator, "none", StringComparison.OrdinalIgnoreCase);
        set
        {
            if (value && !string.Equals(InstallModalSelectedEmulator, "none", StringComparison.OrdinalIgnoreCase))
            {
                InstallModalSelectedEmulator = "none";
                NotifyEmulatorSelectionChanged();
            }
            else if (!value && string.Equals(InstallModalSelectedEmulator, "none", StringComparison.OrdinalIgnoreCase))
            {
                InstallModalSelectedEmulator = string.Empty;
                NotifyEmulatorSelectionChanged();
            }
        }
    }

    public bool IsInstallModalEmulatorValve
    {
        get => string.Equals(InstallModalSelectedEmulator, "refix_valve", StringComparison.OrdinalIgnoreCase);
        set
        {
            if (value && !string.Equals(InstallModalSelectedEmulator, "refix_valve", StringComparison.OrdinalIgnoreCase))
            {
                InstallModalSelectedEmulator = "refix_valve";
                NotifyEmulatorSelectionChanged();
            }
            else if (!value && string.Equals(InstallModalSelectedEmulator, "refix_valve", StringComparison.OrdinalIgnoreCase))
            {
                InstallModalSelectedEmulator = string.Empty;
                NotifyEmulatorSelectionChanged();
            }
        }
    }

    public bool IsInstallModalEmulatorGoldberg
    {
        get => string.Equals(InstallModalSelectedEmulator, "refix_goldberg", StringComparison.OrdinalIgnoreCase);
        set
        {
            if (value && !string.Equals(InstallModalSelectedEmulator, "refix_goldberg", StringComparison.OrdinalIgnoreCase))
            {
                InstallModalSelectedEmulator = "refix_goldberg";
                NotifyEmulatorSelectionChanged();
            }
            else if (!value && string.Equals(InstallModalSelectedEmulator, "refix_goldberg", StringComparison.OrdinalIgnoreCase))
            {
                InstallModalSelectedEmulator = string.Empty;
                NotifyEmulatorSelectionChanged();
            }
        }
    }

    public bool IsInstallModalEmulatorSpecific
    {
        get => string.Equals(InstallModalSelectedEmulator, "gamefix_online", StringComparison.OrdinalIgnoreCase);
        set
        {
            if (value && !string.Equals(InstallModalSelectedEmulator, "gamefix_online", StringComparison.OrdinalIgnoreCase))
            {
                InstallModalSelectedEmulator = "gamefix_online";
                NotifyEmulatorSelectionChanged();
            }
            else if (!value && string.Equals(InstallModalSelectedEmulator, "gamefix_online", StringComparison.OrdinalIgnoreCase))
            {
                InstallModalSelectedEmulator = string.Empty;
                NotifyEmulatorSelectionChanged();
            }
        }
    }

    public bool HasModalSpecificOnlineFix => OnlineGameFixes.Count > 0;

    [ObservableProperty]
    private GameFixInfo? _selectedModalSpecificGameFix;

    /// <summary>
    /// The fixes that are not online fixes, offered as tick boxes in the install modal.
    /// </summary>
    /// <remarks>
    /// An online fix stands in for the emulator, so it belongs in the radio group and only one
    /// can win. A bypass or a hypervisor fix is a layer: it goes on top of whatever emulator was
    /// picked, ReFix included, and several can apply together. The modal had nowhere to say that
    /// — the only specific fixes it offered were the online ones — so installing a bypass meant
    /// finishing the install and then going to the Emulator tab to do it by hand.
    /// </remarks>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(InstallModalHasExtraFixes))]
    private ObservableCollection<InstallModalFixItem> _installModalExtraFixes = [];

    /// <summary>Whether this title has any layerable fix to offer.</summary>
    public bool InstallModalHasExtraFixes => InstallModalExtraFixes.Count > 0;

    /// <summary>
    /// Rebuilds the tick-box list from the fixes already loaded for this title.
    /// </summary>
    /// <remarks>
    /// Reuses whatever <see cref="LoadGameFixesCoreAsync"/> put in the grouped collections, so
    /// opening the modal costs no extra request.
    /// </remarks>
    private void RebuildInstallModalExtraFixes()
    {
        var alreadyInstalled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var layer in Instance?.InstalledFixLayers ?? [])
        {
            if (layer.IsOnline || string.IsNullOrWhiteSpace(layer.FixId)) continue;
            alreadyInstalled.Add(layer.FixId);
        }

        var rows = BypassGameFixes
            .Concat(HypervisorGameFixes)
            .Concat(OtherGameFixes)
            .Where(f => !f.IsOnline)
            .GroupBy(f => f.Id, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .Select(f => new InstallModalFixItem(f, alreadyInstalled.Contains(f.Id)))
            .ToList();

        InstallModalExtraFixes = new ObservableCollection<InstallModalFixItem>(rows);
    }

    public bool IsValveRecommendedForGame =>
        (CuratedRecommendation != null && !string.IsNullOrWhiteSpace(CuratedRecommendation.RecommendedEmulator) &&
         (CuratedRecommendation.RecommendedEmulator.Contains("valve", StringComparison.OrdinalIgnoreCase) ||
          CuratedRecommendation.RecommendedEmulator.Contains("refix", StringComparison.OrdinalIgnoreCase))) ||
        (RecommendedEmulatorOption?.Id == "refix_valve");

    public bool IsGoldbergRecommendedForGame =>
        (CuratedRecommendation != null && !string.IsNullOrWhiteSpace(CuratedRecommendation.RecommendedEmulator) &&
         CuratedRecommendation.RecommendedEmulator.Contains("goldberg", StringComparison.OrdinalIgnoreCase)) ||
        (RecommendedEmulatorOption?.Id == "refix_goldberg");

    private void NotifyEmulatorSelectionChanged()
    {
        InstallModalEnableReFix = !string.IsNullOrWhiteSpace(InstallModalSelectedEmulator)
            && !string.Equals(InstallModalSelectedEmulator, "none", StringComparison.OrdinalIgnoreCase);
        OnPropertyChanged(nameof(InstallModalSelectedEmulator));
        OnPropertyChanged(nameof(IsInstallModalEmulatorNone));
        OnPropertyChanged(nameof(IsInstallModalEmulatorValve));
        OnPropertyChanged(nameof(IsInstallModalEmulatorGoldberg));
        OnPropertyChanged(nameof(IsInstallModalEmulatorSpecific));
        OnPropertyChanged(nameof(HasModalSpecificOnlineFix));
        OnPropertyChanged(nameof(SelectedModalSpecificGameFix));
        OnPropertyChanged(nameof(InstallModalEnableReFix));
        OnPropertyChanged(nameof(IsValveRecommendedForGame));
        OnPropertyChanged(nameof(IsGoldbergRecommendedForGame));
    }

    [ObservableProperty]
    private bool _installModalEnableReFix = true;

    [ObservableProperty]
    private bool _installModalCreateDesktopShortcut = true;

    [ObservableProperty]
    private bool _installModalCreateStartMenuShortcut = true;

    [ObservableProperty]
    private bool _installModalInstallPrerequisites = true;

    [ObservableProperty]
    private ObservableCollection<PrerequisiteItem> _installModalPrerequisites = [];

    [ObservableProperty]
    private string _installModalPrereqStatusText = string.Empty;

    [ObservableProperty]
    private bool _isInstallingModalPrerequisites;

    [ObservableProperty]
    private string _installModalProgressStatusText = string.Empty;

    public string InstallModalGameTitle => CleanName(Instance?.Name) ?? Instance?.Name ?? "Game";

    public string InstallModalHeaderImage => Instance?.HeaderImageUrl ?? string.Empty;

    public string InstallModalSubtitle =>
        string.Format(GetResourceString("String_InstallModalSubtitle", "Configure and install {0} in one single step."), InstallModalGameTitle);

    public string InstallModalShortDescription
    {
        get
        {
            var desc = Instance?.Metadata?.Description;
            if (string.IsNullOrWhiteSpace(desc))
            {
                return GetResourceString("String_NoDescriptionProvided", "No description provided for this game.");
            }
            // Strip any HTML tags from description snippet
            var plain = System.Text.RegularExpressions.Regex.Replace(desc, "<.*?>", string.Empty);
            plain = System.Net.WebUtility.HtmlDecode(plain).Trim();
            return plain.Length > 240 ? plain[..237] + "..." : plain;
        }
    }

    public string InstanceDlcUnlockerMethod
    {
        get => Instance?.DlcUnlockerMethod ?? "CreamAPI";
        set
        {
            if (Instance != null && Instance.DlcUnlockerMethod != value)
            {
                Instance = Instance with { DlcUnlockerMethod = value };
                _ = _instanceManager.UpdateAsync(Instance, CancellationToken.None);
                OnPropertyChanged(nameof(InstanceDlcUnlockerMethod));
                OnPropertyChanged(nameof(IsCreamApiSelected));
                OnPropertyChanged(nameof(IsSmokeApiSelected));
            }
        }
    }

    public bool IsCreamApiSelected
    {
        get => string.Equals(InstanceDlcUnlockerMethod, "CreamAPI", StringComparison.OrdinalIgnoreCase);
        set { if (value) InstanceDlcUnlockerMethod = "CreamAPI"; }
    }

    public bool IsSmokeApiSelected
    {
        get => string.Equals(InstanceDlcUnlockerMethod, "SmokeAPI", StringComparison.OrdinalIgnoreCase);
        set { if (value) InstanceDlcUnlockerMethod = "SmokeAPI"; }
    }

    public bool IsModalCreamApiSelected
    {
        get => string.Equals(InstallModalDlcMethod, "CreamAPI", StringComparison.OrdinalIgnoreCase);
        set
        {
            if (value)
            {
                RequestDlcUnlockerMethod("CreamAPI");
            }
            else if (string.Equals(InstallModalDlcMethod, "CreamAPI", StringComparison.OrdinalIgnoreCase))
            {
                // Clicked off: back to no unlocker at all.
                _forcedDlcMethodAccepted = false;
                _suppressDlcMethodConfirm = true;
                try
                {
                    InstallModalDlcMethod = string.Empty;
                }
                finally
                {
                    _suppressDlcMethodConfirm = false;
                }
            }

            OnPropertyChanged(nameof(IsModalCreamApiSelected));
            OnPropertyChanged(nameof(IsModalSmokeApiSelected));
        }
    }

    public bool IsModalSmokeApiSelected
    {
        get => string.Equals(InstallModalDlcMethod, "SmokeAPI", StringComparison.OrdinalIgnoreCase);
        set
        {
            if (value)
            {
                RequestDlcUnlockerMethod("SmokeAPI");
            }
            else if (string.Equals(InstallModalDlcMethod, "SmokeAPI", StringComparison.OrdinalIgnoreCase))
            {
                // Clicked off: back to no unlocker at all.
                _forcedDlcMethodAccepted = false;
                _suppressDlcMethodConfirm = true;
                try
                {
                    InstallModalDlcMethod = string.Empty;
                }
                finally
                {
                    _suppressDlcMethodConfirm = false;
                }
            }

            OnPropertyChanged(nameof(IsModalCreamApiSelected));
            OnPropertyChanged(nameof(IsModalSmokeApiSelected));
        }
    }

    partial void OnInstallModalPathChanged(string value)
    {
        UpdateInstallModalDiskSpace();
    }

    partial void OnInstallModalDlcMethodChanged(string value)
    {
        OnPropertyChanged(nameof(IsModalCreamApiSelected));
        OnPropertyChanged(nameof(IsModalSmokeApiSelected));
        OnPropertyChanged(nameof(InstallModalHasSelectedDlcs));
    }

    partial void OnInstallModalSelectAllDlcsChanged(bool value)
    {
        if (_isUpdatingSelectAllInternally) return;
        _isUpdatingSelectAllInternally = true;
        try
        {
            foreach (var item in InstallModalDlcs)
            {
                item.IsSelected = value;
            }
        }
        finally
        {
            _isUpdatingSelectAllInternally = false;
        }
        UpdateInstallModalDiskSpace();
        SyncDlcMethodWithSelection();
    }

    private bool _isUpdatingSelectAllInternally;

    /// <summary>
    /// Keeps the unlocker method in step with the DLC list: a method is only pre-selected once at
    /// least one DLC is ticked, and clearing every DLC clears the method again (unless the person
    /// deliberately forced one through the confirmation).
    /// </summary>
    private void SyncDlcMethodWithSelection()
    {
        if (_suppressDlcMethodConfirm) return;

        var hasSelection = InstallModalHasSelectedDlcs;

        if (hasSelection && string.IsNullOrWhiteSpace(InstallModalDlcMethod))
        {
            _suppressDlcMethodConfirm = true;
            try
            {
                InstallModalDlcMethod = !string.IsNullOrWhiteSpace(Instance?.DlcUnlockerMethod)
                    ? Instance!.DlcUnlockerMethod
                    : "CreamAPI";
            }
            finally
            {
                _suppressDlcMethodConfirm = false;
            }
        }
        else if (!hasSelection && !_forcedDlcMethodAccepted && !string.IsNullOrWhiteSpace(InstallModalDlcMethod))
        {
            _suppressDlcMethodConfirm = true;
            try
            {
                InstallModalDlcMethod = string.Empty;
            }
            finally
            {
                _suppressDlcMethodConfirm = false;
            }
        }

        NotifyDlcMethodChanged();
    }

    /// <summary>Set once the person confirms a wrapper for a title with no detected DLC.</summary>
    private bool _forcedDlcMethodAccepted;

    [RelayCommand]
    public void ToggleSelectAllModalDlcs()
    {
        InstallModalSelectAllDlcs = !InstallModalSelectAllDlcs;
    }

    private void UpdateModalSelectAllState()
    {
        if (_isUpdatingSelectAllInternally) return;
        _isUpdatingSelectAllInternally = true;
        try
        {
            InstallModalSelectAllDlcs = InstallModalDlcs.Count > 0 && InstallModalDlcs.All(d => d.IsSelected);
        }
        finally
        {
            _isUpdatingSelectAllInternally = false;
        }

        SyncDlcMethodWithSelection();
    }

    public void UpdateInstallModalDiskSpace()
    {
        if (Instance == null) return;

        long requiredBytes = 0;
        if (Instance.Depots != null)
        {
            requiredBytes += Instance.Depots.Where(d => d.SizeBytes > 0).Sum(d => (long)d.SizeBytes);
        }

        if (InstallModalDlcs != null)
        {
            foreach (var dlcItem in InstallModalDlcs.Where(d => d.IsSelected))
            {
                if (dlcItem.Dlc.Depots != null)
                {
                    requiredBytes += dlcItem.Dlc.Depots.Where(d => d.SizeBytes > 0).Sum(d => (long)d.SizeBytes);
                }
            }
        }

        InstallModalRequiredSpaceText = FormatBytes(requiredBytes);

        try
        {
            var path = InstallModalPath;
            var root = !string.IsNullOrWhiteSpace(path) ? Path.GetPathRoot(path) : null;
            if (!string.IsNullOrWhiteSpace(root) && Directory.Exists(root))
            {
                var drive = new DriveInfo(root);
                long freeBytes = drive.AvailableFreeSpace;
                InstallModalHasEnoughSpace = requiredBytes == 0 || freeBytes >= requiredBytes;
                InstallModalFreeSpaceText = string.Format(
                    GetResourceString("String_InstallModalFreeSpaceFormat", "Free space: {0} · Required: {1}"),
                    FormatBytes(freeBytes),
                    InstallModalRequiredSpaceText);
            }
            else
            {
                InstallModalHasEnoughSpace = true;
                InstallModalFreeSpaceText = string.Format(
                    GetResourceString("String_InstallModalFreeSpaceFormat", "Free space: {0} · Required: {1}"),
                    "—",
                    InstallModalRequiredSpaceText);
            }
        }
        catch
        {
            InstallModalHasEnoughSpace = true;
            InstallModalFreeSpaceText = string.Empty;
        }
    }

    [RelayCommand]
    public async Task OpenInstallModalAsync()
    {
        if (Instance == null) return;

        var cleanGameName = CleanName(Instance.Name) ?? Instance.Name ?? "Game";
        var defaultFolder = !string.IsNullOrWhiteSpace(_appSettings.LastInstallDirectory)
            ? _appSettings.LastInstallDirectory
            : _appSettings.DefaultDownloadDirectory;
        InstallModalPath = !string.IsNullOrWhiteSpace(Instance.InstallPath)
            ? Instance.InstallPath
            : Path.Combine(defaultFolder, cleanGameName);

        InstallModalAvailableBranches.Clear();
        if (AvailableBuilds != null && AvailableBuilds.Count > 0)
        {
            foreach (var b in AvailableBuilds)
            {
                var branch = b.BranchName;
                if (!string.IsNullOrWhiteSpace(branch) && !InstallModalAvailableBranches.Contains(branch))
                {
                    InstallModalAvailableBranches.Add(branch);
                }
            }
        }
        if (!InstallModalAvailableBranches.Contains("public"))
        {
            InstallModalAvailableBranches.Insert(0, "public");
        }
        InstallModalSelectedBranch = !string.IsNullOrWhiteSpace(Instance.ActiveBranch) && InstallModalAvailableBranches.Contains(Instance.ActiveBranch)
            ? Instance.ActiveBranch
            : "public";

        InstallModalDlcs.Clear();
        if (Instance.Dlcs != null && Instance.Dlcs.Count > 0)
        {
            foreach (var dlc in Instance.Dlcs)
            {
                var item = new InstallModalDlcItem
                {
                    Dlc = dlc,
                    IsSelected = true,
                    OnSelectionChanged = () =>
                    {
                        UpdateInstallModalDiskSpace();
                        UpdateModalSelectAllState();
                    }
                };
                InstallModalDlcs.Add(item);
            }
        }
        InstallModalSelectAllDlcs = InstallModalDlcs.Count > 0;

        // No unlocker method is pre-selected: one is only offered once a DLC is actually ticked,
        // and choosing one for a title with no DLC goes through an explicit confirmation.
        _forcedDlcMethodAccepted = false;
        _pendingForcedDlcMethod = null;
        IsForceDlcUnlockerConfirmOpen = false;
        _suppressDlcMethodConfirm = true;
        try
        {
            InstallModalDlcMethod = InstallModalDlcs.Any(d => d.IsSelected)
                ? (!string.IsNullOrWhiteSpace(Instance.DlcUnlockerMethod) ? Instance.DlcUnlockerMethod : "CreamAPI")
                : string.Empty;
        }
        finally
        {
            _suppressDlcMethodConfirm = false;
        }
        NotifyDlcMethodChanged();

        // Warn up front when the title resolved no depots at all: nothing can be downloaded.
        InstallModalHasNoDepots = Instance.Depots == null || Instance.Depots.Count == 0;

        InstallModalEnableReFix = Instance.EmulatorEnabled;

        if (_recommendationProvider != null && Instance.AppId > 0 && CuratedRecommendation == null)
        {
            try
            {
                await LoadCurationRecommendationAsync().ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to load curation recommendation for install modal");
            }
        }

        if (HasCurationRecommendation)
        {
            InstallModalTargetVersionMode = "Recommended";
        }
        else if (!string.IsNullOrWhiteSpace(Instance.ActiveBranch) && !string.Equals(Instance.ActiveBranch, "public", StringComparison.OrdinalIgnoreCase))
        {
            InstallModalTargetVersionMode = "Custom";
        }
        else
        {
            InstallModalTargetVersionMode = "Latest";
        }
        NotifyInstallModalVersionModeChanged();

        // The Custom mode edits the same per-depot manifest table the Game Version tab uses, so
        // it needs the build id and the validation strip seeded here too.
        if (string.IsNullOrWhiteSpace(CustomVersionBuildId))
        {
            CustomVersionBuildId = Instance.ActiveBuildId ?? string.Empty;
        }
        RecomputeCustomBuildValidation();

        // Query game-specific fixes (Online-Fix / DepotBox catalog) for this title
        if (_apiClient != null && OnlineGameFixes.Count == 0)
        {
            try
            {
                await LoadGameFixesCoreAsync().ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to load game fixes for install modal");
            }
        }

        if (OnlineGameFixes.Count > 0 && SelectedModalSpecificGameFix == null)
        {
            SelectedModalSpecificGameFix = OnlineGameFixes[0];
        }

        RebuildInstallModalExtraFixes();

        // Initialize specific emulator selection
        if (Instance.EmulatorEnabled && !string.IsNullOrWhiteSpace(Instance.EmulatorId))
        {
            if (string.Equals(Instance.EmulatorId, "gamefix_online", StringComparison.OrdinalIgnoreCase) ||
                Instance.InstalledFixLayers?.Any(l => l.IsOnline) == true)
            {
                InstallModalSelectedEmulator = "gamefix_online";
            }
            else
            {
                InstallModalSelectedEmulator = Instance.EmulatorId.Contains("goldberg", StringComparison.OrdinalIgnoreCase)
                    ? "refix_goldberg"
                    : "refix_valve";
            }
        }
        else if (!Instance.EmulatorEnabled && Instance.Status != InstanceStatus.NotInstalled)
        {
            InstallModalSelectedEmulator = "none";
        }
        else if (CuratedRecommendation != null && !string.IsNullOrWhiteSpace(CuratedRecommendation.RecommendedEmulator))
        {
            if (CuratedRecommendation.RecommendedEmulator.Contains("goldberg", StringComparison.OrdinalIgnoreCase))
                InstallModalSelectedEmulator = "refix_goldberg";
            else if (string.Equals(CuratedRecommendation.RecommendedEmulator, "none", StringComparison.OrdinalIgnoreCase))
                InstallModalSelectedEmulator = "none";
            else if (CuratedRecommendation.RecommendedEmulator.Contains("onlinefix", StringComparison.OrdinalIgnoreCase) && OnlineGameFixes.Count > 0)
                InstallModalSelectedEmulator = "gamefix_online";
            else
                InstallModalSelectedEmulator = "refix_valve";
        }
        else if (RecommendedEmulatorOption != null)
        {
            InstallModalSelectedEmulator = string.Equals(RecommendedEmulatorOption.Id, "refix_goldberg", StringComparison.OrdinalIgnoreCase)
                ? "refix_goldberg"
                : "refix_valve";
        }
        else
        {
            InstallModalSelectedEmulator = "refix_valve";
        }
        NotifyEmulatorSelectionChanged();

        InstallModalCreateDesktopShortcut = true;
        InstallModalCreateStartMenuShortcut = true;

        UpdateInstallModalDiskSpace();

        InstallModalInstallPrerequisites = true;
        InstallModalPrerequisites.Clear();
        IsInstallingModalPrerequisites = false;
        InstallModalProgressStatusText = string.Empty;
        InstallModalPrereqStatusText = GetResourceString("String_ScanningPrerequisites", "Scanning prerequisites...");

        IsInstallModalOpen = true;

        if (_prerequisiteService != null)
        {
            try
            {
                var detected = await _prerequisiteService.DetectPrerequisitesAsync(Instance, CancellationToken.None).ConfigureAwait(true);
                InstallModalPrerequisites = new ObservableCollection<PrerequisiteItem>(detected);
                var missing = detected.Count(p => p.Status != PrerequisiteStatus.InstalledInSystem && p.Status != PrerequisiteStatus.InstalledSuccess);
                if (missing == 0)
                {
                    InstallModalPrereqStatusText = GetResourceString("String_InstallModalPrereqsAllGood", "All required components are installed on your system ✅");
                }
                else
                {
                    InstallModalPrereqStatusText = string.Format(
                        GetResourceString("String_InstallModalPrereqsMissingCountFormat", "{0} missing component(s) detected and will be configured automatically."),
                        missing);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to scan prerequisites in install modal");
                InstallModalPrereqStatusText = "Prerequisites scanning completed.";
            }
        }
        else
        {
            InstallModalPrereqStatusText = string.Empty;
        }
    }

    [RelayCommand]
    public void CloseInstallModal()
    {
        if (IsInstallingModalPrerequisites) return;
        IsInstallModalOpen = false;
    }

    [RelayCommand]
    public void BrowseInstallModalPath()
    {
        if (Instance == null) return;

        var initialDir = !string.IsNullOrWhiteSpace(InstallModalPath) && Directory.Exists(Path.GetDirectoryName(InstallModalPath))
            ? Path.GetDirectoryName(InstallModalPath)!
            : Directory.Exists(_appSettings.LastInstallDirectory)
                ? _appSettings.LastInstallDirectory
                : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = GetResourceString("String_SelectFolderTitle", "Select Installation Directory"),
            InitialDirectory = initialDir
        };

        if (dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.FolderName))
        {
            var cleanGameName = CleanName(Instance.Name) ?? Instance.Name;
            var selectedRoot = dialog.FolderName;
            InstallModalPath = PathHelper.EnsureGameSubfolder(selectedRoot, cleanGameName);
        }
    }

    [RelayCommand]
    public async Task StartOneClickInstallAsync()
    {
        if (Instance == null || IsInstallingModalPrerequisites) return;

        if (string.IsNullOrWhiteSpace(InstallModalPath))
        {
            StatusMessage = "⚠ Please specify a valid installation folder.";
            return;
        }

        // 1. Install missing prerequisites if selected
        if (InstallModalInstallPrerequisites && _prerequisiteService != null)
        {
            IsInstallingModalPrerequisites = true;
            InstallModalProgressStatusText = GetResourceString("String_InstallModalInstallingPrereqs", "⚙️ Installing prerequisites...");
            try
            {
                var progress = new Progress<string>(msg =>
                {
                    InstallModalProgressStatusText = msg;
                    StatusMessage = msg;
                    _uiContext.Post(_ => ConsoleLogs.Add($"[{DateTime.Now:HH:mm:ss}] {msg}"), null);
                });

                await _prerequisiteService.InstallAllPrerequisitesAsync(Instance, progress, CancellationToken.None).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error installing prerequisites during One-Click Install for {Game}", Instance.Name);
            }
            finally
            {
                IsInstallingModalPrerequisites = false;
            }
        }

        // 2. Configure target installation directory and instance parameters
        var targetPath = InstallModalPath.Trim();
        try
        {
            Directory.CreateDirectory(targetPath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not pre-create directory {Path}", targetPath);
        }

        var selectedDlcIds = InstallModalDlcs.Where(d => d.IsSelected).Select(d => d.AppId).ToHashSet();

        // Base game depots + selected DLC depots
        var selectedDepots = new List<DepotInfo>();
        if (Instance.Depots != null)
        {
            selectedDepots.AddRange(Instance.Depots);
        }
        if (Instance.Dlcs != null)
        {
            foreach (var dlc in Instance.Dlcs)
            {
                if (selectedDlcIds.Contains(dlc.AppId) && dlc.Depots != null)
                {
                    selectedDepots.AddRange(dlc.Depots);
                }
            }
        }
        var distinctDepots = selectedDepots.DistinctBy(d => d.DepotId).ToList();

        var targetBranch = "public";
        string? targetBuildId = null;
        bool isBuildPinned = false;
        bool disableUpdates = false;
        var manifestMap = new Dictionary<uint, ulong>(Instance.InstalledManifestMap ?? new Dictionary<uint, ulong>());

        if (IsInstallModalVersionRecommended && CuratedRecommendation != null)
        {
            var rec = CuratedRecommendation;
            targetBranch = rec.RecommendedBranch ?? "public";
            targetBuildId = rec.RecommendedBuildId;
            isBuildPinned = true;
            disableUpdates = true;

            // Pin base game and DLC depots to the community-verified manifests
            distinctDepots = distinctDepots.Select(d =>
                rec.PinnedManifests.TryGetValue(d.DepotId, out var pinned) && pinned > 0
                    ? d with { ManifestId = pinned, IsDownloaded = false }
                    : d).ToList();

            foreach (var kv in rec.PinnedManifests)
            {
                manifestMap[kv.Key] = kv.Value;
            }
        }
        else if (IsInstallModalVersionCustom)
        {
            targetBranch = !string.IsNullOrWhiteSpace(InstallModalSelectedBranch)
                ? InstallModalSelectedBranch.Trim()
                : "public";

            // Custom here means the same as Custom in the Game Version tab: the branch plus
            // whatever manifest id was typed or picked for each depot.
            var customManifests = new Dictionary<uint, ulong>();
            foreach (var item in Depots)
            {
                var text = (item.ManifestInputText ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(text) && item.Depot.ManifestId > 0)
                {
                    text = item.Depot.ManifestId.ToString();
                }
                if (TryParseManifestId(text, out var manifestId) && manifestId > 0)
                {
                    customManifests[item.Depot.DepotId] = manifestId;
                }
            }

            if (customManifests.Count > 0)
            {
                distinctDepots = distinctDepots.Select(d =>
                    customManifests.TryGetValue(d.DepotId, out var pinned) && pinned > 0
                        ? d with { ManifestId = pinned, IsDownloaded = false }
                        : d).ToList();

                foreach (var kv in customManifests)
                {
                    manifestMap[kv.Key] = kv.Value;
                }
            }

            targetBuildId = !string.IsNullOrWhiteSpace(CustomVersionBuildId)
                ? CustomVersionBuildId.Trim()
                : Instance.ActiveBuildId;
            isBuildPinned = customManifests.Count > 0
                || !string.Equals(targetBranch, "public", StringComparison.OrdinalIgnoreCase);
            disableUpdates = isBuildPinned;
        }
        else
        {
            targetBranch = "public";
            targetBuildId = null;
            isBuildPinned = false;
            disableUpdates = false;
        }

        // Update depots and dlcs in the viewmodel
        foreach (var dep in distinctDepots)
        {
            var existing = Depots.FirstOrDefault(d => d.Depot.DepotId == dep.DepotId);
            if (existing != null)
            {
                existing.Depot = dep;
                existing.IsSelected = true;
                existing.ManifestInputText = dep.ManifestId > 0 ? dep.ManifestId.ToString() : string.Empty;
            }
            else
            {
                Depots.Add(new SelectableDepotItem
                {
                    Depot = dep,
                    IsSelected = true,
                    IsKeyAvailable = !string.IsNullOrWhiteSpace(dep.DepotKey),
                    IsCachedLocally = _manifestCacheService?.HasManifest(dep.DepotId, dep.ManifestId) ?? false,
                    SourceProviderName = "Auto",
                    ManifestInputText = dep.ManifestId > 0 ? dep.ManifestId.ToString() : string.Empty,
                    AvailabilityChecker = CheckDepotManifestAvailabilityAsync,
                    OnSelectionChanged = RecalculateSelectedSize,
                    OnManifestUpdated = HandleDepotManifestUpdated,
                    OnManifestTextChanged = RecomputeCustomBuildValidation
                });
            }
        }

        foreach (var d in Dlcs)
        {
            d.IsSelected = selectedDlcIds.Contains(d.Dlc.AppId);
        }

        bool isSpecificFix = string.Equals(InstallModalSelectedEmulator, "gamefix_online", StringComparison.OrdinalIgnoreCase);
        bool enableEmulator = !string.IsNullOrWhiteSpace(InstallModalSelectedEmulator)
            && !string.Equals(InstallModalSelectedEmulator, "none", StringComparison.OrdinalIgnoreCase);
        string? emulatorId = enableEmulator ? InstallModalSelectedEmulator : null;
        string? pendingFixId = isSpecificFix ? SelectedModalSpecificGameFix?.Id : null;

        // Every layer to put down after the download, most important first. The online fix, when
        // one was chosen, leads: it stands in for the emulator. The tick boxes follow, and they
        // coexist with it and with ReFix rather than replacing either.
        var fixLayerIds = new List<string>();
        if (!string.IsNullOrWhiteSpace(pendingFixId)) fixLayerIds.Add(pendingFixId);

        foreach (var extra in InstallModalExtraFixes)
        {
            if (!extra.IsSelected) continue;
            if (string.IsNullOrWhiteSpace(extra.Fix.Id)) continue;
            if (fixLayerIds.Contains(extra.Fix.Id, StringComparer.OrdinalIgnoreCase)) continue;

            fixLayerIds.Add(extra.Fix.Id);
        }
        string? installedEmulatorVersion = isSpecificFix
            ? (SelectedModalSpecificGameFix?.Name ?? "Online Fix")
            : (enableEmulator ? "1.0" : null);

        var updatedInstance = Instance with
        {
            InstallPath = targetPath,
            ActiveBranch = targetBranch,
            ActiveBuildId = targetBuildId ?? Instance.ActiveBuildId,
            InstalledManifestMap = manifestMap,
            IsBuildPinned = isBuildPinned,
            DisableUpdateChecks = disableUpdates,
            DlcUnlockerMethod = !string.IsNullOrWhiteSpace(InstallModalDlcMethod)
                ? InstallModalDlcMethod
                : Instance.DlcUnlockerMethod,
            PendingCreateDesktopShortcut = InstallModalCreateDesktopShortcut,
            PendingCreateStartMenuShortcut = InstallModalCreateStartMenuShortcut,
            AwaitingPostUpdateRedeploy = true,
            PendingRedeployDlcUnlocker = !string.IsNullOrWhiteSpace(InstallModalDlcMethod),
            PendingRedeployEmulatorId = isSpecificFix ? null : emulatorId,
            PendingRedeployGameFixId = fixLayerIds.Count > 0 ? fixLayerIds[0] : null,
            PendingRedeployFixLayerIds = fixLayerIds,
            EmulatorEnabled = enableEmulator,
            EmulatorId = emulatorId,
            InstalledEmulatorVersion = installedEmulatorVersion,
            UnlockedDlcIds = selectedDlcIds.ToList(),
            Depots = distinctDepots.AsReadOnly(),
            Status = InstanceStatus.NotInstalled // Strictly remain NotInstalled until download completes!
        };

        await _instanceManager.UpdateAsync(updatedInstance, CancellationToken.None).ConfigureAwait(true);
        Instance = updatedInstance;

        // Remember directory for future installs
        _ = _appSettings.SetLastInstallDirectoryAsync(Directory.GetParent(targetPath)?.FullName ?? targetPath);

        // Close modal
        IsInstallModalOpen = false;

        // 3. Initiate download
        await StartDownloadAsync().ConfigureAwait(true);
    }

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
    private readonly IBuildResolver? _buildResolver;
    private readonly IManifestRegistry? _manifestRegistry;
    private readonly IInstallationPlanner? _installationPlanner;
    private readonly IDepotKeyRepository? _depotKeyRepository;
    private readonly IManifestCacheService? _manifestCacheService;
    private readonly ICacheService? _cacheService;
    private readonly IRecommendationProvider? _recommendationProvider;

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, InstanceSessionVerificationState> _verifiedInstancesThisSession = new();

    public sealed record InstanceSessionVerificationState(
        DateTimeOffset VerifiedAt,
        UpdateCheckStatus Status,
        string? Description,
        DateTimeOffset? LatestDate,
        string? LatestDateText,
        DateTimeOffset? InstalledDate,
        string? InstalledDateText);

    public Action? OnNavigateBack { get; set; }
    public Action<string>? OnOpenTagRequested { get; set; }
    public Action<SearchResult>? OnFindSimilarRequested { get; set; }

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
        ISteamStatusService? steamStatusService = null,
        IBuildResolver? buildResolver = null,
        IManifestRegistry? manifestRegistry = null,
        IInstallationPlanner? installationPlanner = null,
        IDepotKeyRepository? depotKeyRepository = null,
        IManifestCacheService? manifestCacheService = null,
        ILocalizationService? localizationService = null,
        ICacheService? cacheService = null,
        IRecommendationProvider? recommendationProvider = null)
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
        _buildResolver = buildResolver;
        _manifestRegistry = manifestRegistry;
        _installationPlanner = installationPlanner;
        _depotKeyRepository = depotKeyRepository;
        _manifestCacheService = manifestCacheService;
        _localizationService = localizationService;
        _cacheService = cacheService;
        _recommendationProvider = recommendationProvider;
        _uiContext = SynchronizationContext.Current ?? new SynchronizationContext();

        if (_localizationService != null)
        {
            _languageChangedHandler = (_, _) =>
            {
                App.Current?.Dispatcher?.Invoke(() =>
                {
                    if (_isDisposed) return;
                    OnPropertyChanged(nameof(DlcActionButtonText));
                });
            };
            _localizationService.LanguageChanged += _languageChangedHandler;
        }

        _downloadQueueManager.Queue.CollectionChanged += OnQueueChanged;
        _instanceManager.InstancesChanged += OnInstanceManagerInstancesChanged;
        _gameLauncher.RunningStateChanged += OnGameRunningStateChanged;
        _gameLauncher.LogReceived += OnGameLogReceived;

        _settingsChangedHandler = (_, _) =>
        {
            App.Current?.Dispatcher?.Invoke(() =>
            {
                if (_isDisposed) return;
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
            DisableUpdateChecks = Instance.DisableUpdateChecks;

            IReadOnlyDictionary<uint, string> knownKeys = new Dictionary<uint, string>();
            if (_depotKeyRepository != null && Instance.Depots.Count > 0)
            {
                try
                {
                    knownKeys = await _depotKeyRepository.GetKeysAsync(Instance.Depots.Select(d => d.DepotId)).ConfigureAwait(false);
                }
                catch { }
            }

            var selectableDepots = Instance.Depots
                .Where(d => d.SizeBytes > 0 || !Instance.Depots.Any(other => other.SizeBytes > 0))
                .Select(d =>
                {
                    bool hasKey = !string.IsNullOrWhiteSpace(d.DepotKey) || (knownKeys != null && knownKeys.ContainsKey(d.DepotId));
                    bool isCached = _manifestCacheService?.HasManifest(d.DepotId, d.ManifestId) ?? false;
                    return new SelectableDepotItem
                    {
                        Depot = d,
                        IsSelected = IsDepotCompatibleWithCurrentOS(d),
                        IsKeyAvailable = hasKey,
                        IsCachedLocally = isCached,
                        SourceProviderName = isCached ? "Local Cache" : "Auto",
                        ManifestInputText = d.ManifestId > 0 ? d.ManifestId.ToString() : string.Empty,
                        AvailabilityStatus = isCached ? "💾 Local Cache" : "Checking...",
                        AvailabilityBadgeColor = isCached ? "#10B981" : "#94A3B8",
                        AvailabilityChecker = CheckDepotManifestAvailabilityAsync,
                        OnSelectionChanged = RecalculateSelectedSize,
                        OnManifestUpdated = HandleDepotManifestUpdated,
                        OnManifestTextChanged = RecomputeCustomBuildValidation
                    };
                }).ToList();


            var selectableDlcs = Instance.Dlcs.Select(d => new SelectableDlcItem
            {
                Dlc = d,
                IsSelected = true,
                OnSelectionChanged = RecalculateSelectedSize
            }).ToList();

            Depots = new ObservableCollection<SelectableDepotItem>(selectableDepots);
            Dlcs = new ObservableCollection<SelectableDlcItem>(selectableDlcs);

            _ = PopulateDepotsKnownManifestVersionsAsync();

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
                IsDlcUnlocked = Instance.DlcUnlockerInstalled ||
                    await _dlcInstaller.IsDlcInstalledAsync(Instance, Dlcs[0].Dlc, CancellationToken.None).ConfigureAwait(true);
            }
            else
            {
                IsDlcUnlocked = Instance.DlcUnlockerInstalled;
            }

            foreach (var dlcItem in Dlcs)
            {
                dlcItem.IsUnlocked = IsDlcUnlocked && (Instance.UnlockedDlcIds.Count == 0 || Instance.UnlockedDlcIds.Contains(dlcItem.Dlc.AppId));
            }

            if (IsInstalled)
            {
                var depotDate = GetInstalledDepotReleaseDate(Instance);
                if (depotDate.HasValue)
                {
                    InstalledVersionDate = depotDate.Value;
                    InstalledVersionText = $"{depotDate.Value:d MMM yyyy}";
                }
                else if (Instance.InstalledVersionDate.HasValue)
                {
                    InstalledVersionDate = Instance.InstalledVersionDate.Value;
                    InstalledVersionText = $"{Instance.InstalledVersionDate.Value:d MMM yyyy}";
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

            // Restore last known verification info from L2 persistent disk cache across sessions
            if (_cacheService != null)
            {
                try
                {
                    var cachedState = await _cacheService.GetAsync<InstanceSessionVerificationState>($"instance_update_state_{Instance.Id}").ConfigureAwait(true);
                    if (cachedState != null)
                    {
                        if (cachedState.LatestDate.HasValue)
                        {
                            LatestVersionDate = cachedState.LatestDate.Value;
                            LatestVersionText = cachedState.LatestDateText ?? "Unknown";
                        }
                        else if (!IsCheckingPlaceholder(cachedState.LatestDateText))
                        {
                            LatestVersionText = cachedState.LatestDateText!;
                        }

                        if (cachedState.InstalledDate.HasValue && !InstalledVersionDate.HasValue)
                        {
                            InstalledVersionDate = cachedState.InstalledDate.Value;
                            InstalledVersionText = cachedState.InstalledDateText ?? $"{cachedState.InstalledDate.Value:d MMM yyyy}";
                        }

                        HasGameUpdateAvailable = cachedState.Status == UpdateCheckStatus.UpdateAvailable;
                    }
                }
                catch { }
            }

            // Single verification per instance per session
            // A session entry without a real date is not a verification — re-check instead of
            // replaying a placeholder.
            bool alreadyVerifiedThisSession =
                _verifiedInstancesThisSession.TryGetValue(Instance.Id, out var sessionState) &&
                sessionState is { LatestDate: not null } &&
                !IsCheckingPlaceholder(sessionState.LatestDateText);

            if (alreadyVerifiedThisSession && sessionState != null)
            {
                if (sessionState.LatestDate.HasValue)
                {
                    LatestVersionDate = sessionState.LatestDate.Value;
                    LatestVersionText = IsCheckingPlaceholder(sessionState.LatestDateText)
                        ? GetString("String_VersionLookupUnknown", "Unknown")
                        : sessionState.LatestDateText!;
                }
                if (sessionState.InstalledDate.HasValue)
                {
                    InstalledVersionDate = sessionState.InstalledDate.Value;
                    InstalledVersionText = sessionState.InstalledDateText ?? $"{sessionState.InstalledDate.Value:d MMM yyyy}";
                }
                HasGameUpdateAvailable = sessionState.Status == UpdateCheckStatus.UpdateAvailable;
            }
            else
            {
                _ = CheckSteamVersionDateAsync();
            }

            _ = LoadDlcsFromMetadataIfEmptyAsync();
            _ = LoadAvailableBuildsAsync();
            _ = EnrichDepotsFromSteamDbAsync();

            // ── Overview: Steam showcase + on-disk size ──
            _ = LoadSteamShowcaseAsync();
            _ = RefreshInstalledSizeAsync();
            NotifyOverviewProps();

            // ── Version tab: prime the four modes ──
            CustomVersionBuildId = Instance.ActiveBuildId ?? string.Empty;
            RefreshRollbackBuilds();
            RefreshSavedCustomBuilds();
            RecomputeCustomBuildValidation();
            _ = LoadCurationRecommendationAsync();

            // A pinned instance opens on the mode that pinned it, so the UI reflects reality
            // instead of offering "Latest" over a build the user deliberately froze.
            if (Instance.IsBuildPinned && VersionMode == VersionModeLatest)
            {
                VersionMode = string.Equals(Instance.ActiveBranch, "custom", StringComparison.OrdinalIgnoreCase)
                    ? VersionModeCustom
                    : VersionModeRollback;
            }
            else if (VersionMode == VersionModeLatest)
            {
                ApplyLatestAutoSelection();
            }

            if (autoCheckDepotUpdates && !alreadyVerifiedThisSession && !Instance.DisableUpdateChecks)
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
            var fetchedDlcs = await _metadataProvider.GetDlcListAsync(Instance.AppId, _cts.Token).ConfigureAwait(true);
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
                        IsDlcUnlocked = Instance.DlcUnlockerInstalled ||
                            await _dlcInstaller.IsDlcInstalledAsync(Instance, Dlcs[0].Dlc, _cts.Token).ConfigureAwait(true);
                    }
                    else
                    {
                        IsDlcUnlocked = Instance.DlcUnlockerInstalled;
                    }

                    foreach (var dlcItem in Dlcs)
                    {
                        dlcItem.IsUnlocked = IsDlcUnlocked && (Instance.UnlockedDlcIds.Count == 0 || Instance.UnlockedDlcIds.Contains(dlcItem.Dlc.AppId));
                    }

                    try
                    {
                        await _instanceManager.UpdateAsync(Instance, _cts.Token).ConfigureAwait(false);
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
        if (instance.InstalledVersionDate.HasValue) return instance.InstalledVersionDate.Value;
        return BlueStar.Infrastructure.Services.GameUpdateDetectionHelper.GetInstalledManifestDate(instance);
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
            var enrichment = await steamClient.GetDepotEnrichmentAsync(Instance.AppId, _cts.Token)
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

                    // SteamCMD's config.oslist is authoritative, so let it correct a platform that
                    // was guessed from the depot's DepotBox comment. This also repairs instances
                    // saved before the comment parser learned to match whole words.
                    var newPlatform = MapOsListToPlatform(meta.OsList)
                        ?? PlatformFromDepotName(newSteamDbName)
                        ?? PlatformFromDepotName(item.Depot.Name)
                        ?? item.Depot.Platform;

                    // Skip if nothing changed
                    if (item.Depot.SteamDbName == newSteamDbName &&
                        item.Depot.IsRecommended == isRecommended &&
                        string.Equals(item.Depot.Platform, newPlatform, StringComparison.Ordinal))
                        continue;

                    item.Depot = item.Depot with
                    {
                        SteamDbName = newSteamDbName,
                        IsRecommended = isRecommended,
                        Platform = newPlatform
                    };

                    item.NotifyPlatformChanged();

                    // Auto-select recommended depots that were not yet selected
                    if (isRecommended && !item.IsSelected)
                        item.IsSelected = true;

                    item.NotifyEnrichmentChanged();
                    anyChange = true;

                }

                if (anyChange)
                {
                    RecalculateSelectedSize();

                    // Persist the corrected depot metadata, otherwise the wrong platform comes
                    // back from storage on the next launch.
                    if (Instance != null)
                    {
                        var corrected = Instance.Depots
                            .Select(d => Depots.FirstOrDefault(x => x.Depot.DepotId == d.DepotId)?.Depot ?? d)
                            .ToList();

                        Instance = Instance with { Depots = corrected.AsReadOnly() };
                        _ = _instanceManager.UpdateAsync(Instance, CancellationToken.None);
                    }
                }

            }, null);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to enrich depots from SteamDB for AppId={AppId}", Instance?.AppId);
        }
    }


    /// <param name="allowClearingUpdateFlag">
    /// When false the lookup may raise "update available" but never lower it. The depot-manifest
    /// check is the authoritative source for whether an update can actually be applied; letting
    /// this coarser date comparison clear the flag is what made opening and closing the update
    /// modal mark the game as already updated.
    /// </param>
    private async Task CheckSteamVersionDateAsync(bool userInitiated = false, bool allowClearingUpdateFlag = true)
    {
        if (_isDisposed || Instance == null || Instance.AppId == 0) return;

        // Per-instance opt-out: never poll providers automatically for this game.
        if (Instance.DisableUpdateChecks && !userInitiated)
        {
            HasGameUpdateAvailable = false;
            LatestVersionText = GetString("String_VersionLookupDisabled", "Checks disabled");
            return;
        }

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

                if (installedDate.HasValue && Instance.InstalledVersionDate != installedDate.Value)
                {
                    Instance = Instance with { InstalledVersionDate = installedDate.Value };
                    await _instanceManager.UpdateAsync(Instance, CancellationToken.None).ConfigureAwait(true);
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
                    // Providers can hold newer manifests for a build Steam already dates as
                    // current, so "same date" does not mean "nothing to download".
                    bool depotUpdatePending = HasPendingDepotUpdate || UpdateAvailableDepots.Count > 0;

                    if (allowClearingUpdateFlag && !depotUpdatePending)
                    {
                        HasGameUpdateAvailable = false;
                        if (Instance.HasUpdateAvailable)
                        {
                            Instance = Instance with { HasUpdateAvailable = false };
                            await _instanceManager.UpdateAsync(Instance, CancellationToken.None).ConfigureAwait(true);
                        }
                    }
                }
                // When status is UpdateCheckStatus.Unknown, preserve existing known state

                // Record verification state for this session and persist to disk cache.
                // A lookup that produced no date is not an answer worth remembering for 30 days —
                // caching it is what made the placeholder stick permanently.
                bool worthCaching = latestDate.HasValue && !IsCheckingPlaceholder(latestDateText);

                var state = new InstanceSessionVerificationState(
                    DateTimeOffset.UtcNow,
                    status,
                    null,
                    latestDate,
                    latestDate.HasValue ? LatestVersionText : null,
                    installedDate,
                    InstalledVersionText);

                if (worthCaching)
                {
                    _verifiedInstancesThisSession[Instance.Id] = state;

                    if (_cacheService != null)
                    {
                        _ = _cacheService.SetAsync($"instance_update_state_{Instance.Id}", state, TimeSpan.FromDays(30), CancellationToken.None);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Steam version lookup failed for AppId {AppId}", Instance?.AppId);
        }
        finally
        {
            if (!_isDisposed) SettleVersionTexts();
        }
    }

    /// <summary>
    /// Checks DepotBox API for updated depot manifests and opens the update modal.
    /// </summary>
    [RelayCommand]
    public async Task CheckAndOpenDepotUpdateModalAsync()
    {
        if (Instance == null || _apiClient == null) return;

        // User explicitly triggered update check: Invalidate session and disk cache to force live check
        _verifiedInstancesThisSession.TryRemove(Instance.Id, out _);
        _apiClient.InvalidateAppCache(Instance.AppId);
        if (_cacheService != null)
        {
            _ = _cacheService.RemoveAsync($"instance_update_state_{Instance.Id}");
            _ = _cacheService.RemoveAsync($"steamcmd_depotinfo_{Instance.AppId}_v3");
            _ = _cacheService.RemoveAsync($"steam_latest_update_{Instance.AppId}_v3");
        }

        // A manual check is always honoured, even when automatic checks are disabled. It refreshes
        // the version labels only — the depot scan below decides whether an update exists.
        _ = CheckSteamVersionDateAsync(userInitiated: true, allowClearingUpdateFlag: false);

        IsCheckingGameUpdate = true;
        StatusMessage = "🔍 Searching for updates across providers...";

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
        progress.Report(new BackgroundTaskProgress(10, "Searching for updates across providers...", "Searching"));

        try
        {
            // 1. Resolve newest build / version
            GameVersion? latestVersion = null;
            if (_buildResolver != null && Instance!.AppId > 0)
            {
                try
                {
                    var versions = await _buildResolver.GetAvailableVersionsAsync(Instance.AppId, ct).ConfigureAwait(false);
                    latestVersion = versions.FirstOrDefault(v => v.BranchName == "public") ?? versions.FirstOrDefault();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to resolve available versions via BuildResolver for {AppId}", Instance!.AppId);
                }
            }

            // 2. Map target build depots & manifests
            var targetManifests = new Dictionary<uint, ulong>();
            if (latestVersion != null && latestVersion.Depots.Count > 0)
            {
                foreach (var d in latestVersion.Depots)
                {
                    if (d.ManifestId > 0)
                    {
                        targetManifests[d.DepotId] = d.ManifestId;
                    }
                }
            }

            // Fallback: If no build returned from build resolver, check DepotBox API as last resort only for DepotBox-originated instances
            if (targetManifests.Count == 0 && _apiClient != null && Instance!.AppId > 0 && Instance.Origin == InstanceOrigin.DepotBox)
            {
                try
                {
                    var depotBoxManifests = await _apiClient.GetManifestsAsync(Instance.AppId, ct).ConfigureAwait(false);
                    foreach (var m in depotBoxManifests)
                    {
                        if (m.ManifestId > 0)
                        {
                            targetManifests[m.DepotId] = m.ManifestId;
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to query fallback manifests from DepotBox API for {AppId}", Instance!.AppId);
                }
            }

            if (targetManifests.Count == 0)
            {
                progress.Report(new BackgroundTaskProgress(100, "Manifest check complete.", "Complete"));
                _uiContext.Post(_ =>
                {
                    if (HasGameUpdateAvailable || Instance!.HasUpdateAvailable)
                    {
                        _notificationService?.ShowInfo(
                            "Latest Version Not Available Yet",
                            "A newer version was detected on Steam, but its depot manifests are not yet available across configured providers. Installed files remain unchanged.",
                            TimeSpan.FromSeconds(6));
                    }
                    else
                    {
                        _notificationService?.ShowInfo("Depots Up to Date", "No updates found across providers.");
                    }
                }, null);
                return;
            }

            progress.Report(new BackgroundTaskProgress(40, "Comparing manifests with installed state...", "Comparing"));

            // 3. Find only depots that actually changed!
            var changedDepots = new List<(uint DepotId, ulong CurrentManifestId, ulong NewManifestId, DepotInfo? LocalDepot)>();
            foreach (var (depotId, newManifestId) in targetManifests)
            {
                var local = Instance!.Depots.FirstOrDefault(d => d.DepotId == depotId);
                ulong installedManifest = 0;
                if (Instance.InstalledManifestMap.TryGetValue(depotId, out var im))
                {
                    installedManifest = im;
                }
                else if (local != null)
                {
                    installedManifest = local.ManifestId;
                }

                if (local == null || installedManifest != newManifestId)
                {
                    changedDepots.Add((depotId, installedManifest, newManifestId, local));
                }
            }

            if (changedDepots.Count == 0)
            {
                progress.Report(new BackgroundTaskProgress(100, "All depots match installed version.", "Complete"));
                _uiContext.Post(_ =>
                {
                    if (HasGameUpdateAvailable || Instance!.HasUpdateAvailable)
                    {
                        // Steam has a newer version, but providers do not have newer manifests yet.
                        // Keep HasGameUpdateAvailable = true so the user is informed!
                        _notificationService?.ShowInfo(
                            "Latest Version Not Available Yet",
                            "A newer version was detected on Steam, but its depot manifests are not yet available across configured providers. Installed files remain unchanged.",
                            TimeSpan.FromSeconds(6));
                    }
                    else
                    {
                        HasGameUpdateAvailable = false;
                        HasPendingDepotUpdate = false;
                        UpdateAvailableDepots = [];
                        if (Instance!.HasUpdateAvailable)
                        {
                            Instance = Instance with { HasUpdateAvailable = false, UpdateDescription = null };
                            _ = _instanceManager.UpdateAsync(Instance, CancellationToken.None);
                        }

                        _notificationService?.ShowInfo(
                            "Depots Up to Date",
                            "Installed depots match the latest version. No new files pending download.",
                            TimeSpan.FromSeconds(5));
                    }
                }, null);
                return;
            }

            progress.Report(new BackgroundTaskProgress(60, "Resolving providers for updated depots...", "Resolving"));

            IReadOnlyList<ManifestArtifact> discoveredArtifacts = Array.Empty<ManifestArtifact>();
            if (_manifestRegistry != null && Instance != null)
            {
                try
                {
                    discoveredArtifacts = await _manifestRegistry.DiscoverManifestsAsync(Instance.AppId, ct).ConfigureAwait(false);
                }
                catch { }
            }

            var outdatedDepots = new List<DepotUpdateItem>();
            foreach (var item in changedDepots)
            {
                string providerName = "Community";
                long size = item.LocalDepot?.SizeBytes ?? 0;

                if (discoveredArtifacts.Count > 0)
                {
                    var artifact = discoveredArtifacts.FirstOrDefault(a => a.DepotId == item.DepotId && a.ManifestId == item.NewManifestId);
                    if (artifact != null && artifact.Routes.Count > 0)
                    {
                        var bestRoute = artifact.Routes.OrderByDescending(r => r.Priority).First();
                        providerName = !string.IsNullOrWhiteSpace(bestRoute.ProviderName) ? bestRoute.ProviderName : "Community";
                    }
                }

                outdatedDepots.Add(new DepotUpdateItem
                {
                    DepotId = item.DepotId,
                    Name = item.LocalDepot?.Name ?? $"Depot {item.DepotId}",
                    Category = item.LocalDepot?.Category ?? "Base Game",
                    Platform = item.LocalDepot?.Platform ?? "Universal",
                    Architecture = item.LocalDepot?.Architecture,
                    CurrentManifestId = item.CurrentManifestId,
                    NewManifestId = item.NewManifestId,
                    SizeBytes = size,
                    SourceProvider = providerName,
                    IsSelected = true
                });
            }

            progress.Report(new BackgroundTaskProgress(100, $"Found {outdatedDepots.Count} updated depot(s).", "Complete"));
            _uiContext.Post(_ =>
            {
                UpdateAvailableDepots = new ObservableCollection<DepotUpdateItem>(outdatedDepots);
                HasPendingDepotUpdate = true;
                HasGameUpdateAvailable = true;

                // Persist it: the update stays offered after closing the dialog, and across restarts.
                if (Instance != null && !Instance.HasUpdateAvailable)
                {
                    Instance = Instance with
                    {
                        HasUpdateAvailable = true,
                        UpdateDescription = string.Format(
                            GetString("String_DepotUpdatePendingFormat", "{0} depot(s) have newer manifests available."),
                            outdatedDepots.Count)
                    };
                    _ = _instanceManager.UpdateAsync(Instance, CancellationToken.None);
                }

                IsUpdateModalOpen = true;
            }, null);
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
        StatusMessage = "📥 Downloading updated manifests and keys across providers...";
        _notificationService?.ShowInfo("Downloading Update", "Fetching updated manifests and keys...");

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
        progress.Report(new BackgroundTaskProgress(5, "Acquiring manifests for updated depots...", "Downloading"));

        var instanceManifestDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "BlueStar", "instances", Instance!.Id.ToString(), "manifests");
        Directory.CreateDirectory(instanceManifestDir);

        var updatedDepots = new List<DepotInfo>(Instance.Depots);
        var updatedManifestMap = new Dictionary<uint, ulong>(Instance.InstalledManifestMap);

        // 1. Acquire manifests & keys for selected depots
        for (int i = 0; i < selected.Count; i++)
        {
            var item = selected[i];
            var pct = 5.0 + (45.0 * (i + 1) / selected.Count);
            progress.Report(new BackgroundTaskProgress(pct, $"Acquiring manifest for Depot {item.DepotId}...", "Acquiring"));

            string? manifestFilePath = null;

            // Try from ManifestRegistry (LocalCache [1000] -> ManifestHub [400] -> DepotBox [100])
            if (_manifestRegistry != null)
            {
                try
                {
                    manifestFilePath = await _manifestRegistry.AcquireManifestAsync(item.DepotId, item.NewManifestId, Instance.AppId, preferredProviderId: null, ct: ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to acquire manifest for Depot {DepotId} from registry", item.DepotId);
                }
            }

            // Copy acquired manifest to instance manifests directory
            if (!string.IsNullOrWhiteSpace(manifestFilePath) && File.Exists(manifestFilePath))
            {
                var destPath = Path.Combine(instanceManifestDir, $"{item.DepotId}_{item.NewManifestId}.manifest");
                File.Copy(manifestFilePath, destPath, overwrite: true);
            }

            // Resolve encryption key
            string? depotKey = null;
            if (_depotKeyRepository != null)
            {
                depotKey = await _depotKeyRepository.GetKeyAsync(item.DepotId, ct).ConfigureAwait(false);
            }

            // Update depot info in instance
            var idx = updatedDepots.FindIndex(d => d.DepotId == item.DepotId);
            if (idx >= 0)
            {
                var existing = updatedDepots[idx];
                updatedDepots[idx] = existing with
                {
                    ManifestId = item.NewManifestId,
                    DepotKey = !string.IsNullOrWhiteSpace(depotKey) ? depotKey : existing.DepotKey,
                    IsDownloaded = false
                };
            }
            else
            {
                updatedDepots.Add(new DepotInfo
                {
                    DepotId = item.DepotId,
                    ManifestId = item.NewManifestId,
                    Name = item.Name,
                    Category = item.Category,
                    Platform = item.Platform,
                    Architecture = item.Architecture,
                    DepotKey = depotKey,
                    IsDownloaded = false
                });
            }

            updatedManifestMap[item.DepotId] = item.NewManifestId;
        }

        // 2. Roll the instance back to a pristine state.
        //
        // DepotDownloader writes the official Steam binaries straight over the install folder.
        // Anything layered on top of steam_api64.dll (ReFix / Re:Goldberg, SmokeAPI/CreamAPI) is
        // destroyed by that write, while its *_valve.dll / *_o.dll backup still holds the OLD
        // build's DLL — so a later "uninstall emulator" would restore a stale DLL over the new
        // game files and corrupt the installation. Strip both cleanly first, then redeploy once
        // the download finishes (handled by DownloadQueueManager via AwaitingPostUpdateRedeploy).
        progress.Report(new BackgroundTaskProgress(55, "Removing emulator and DLC unlocker before updating...", "Cleaning"));

        var preparedInstance = Instance;
        if (_emulatorLifecycleService != null)
        {
            try
            {
                var deployProgress = new Progress<DeployProgress>(dp =>
                    progress.Report(new BackgroundTaskProgress(55 + (dp.Percentage * 0.05), dp.Message ?? "Cleaning...", "Cleaning")));

                preparedInstance = await _emulatorLifecycleService
                    .PrepareForGameUpdateAsync(Instance, deployProgress, ct)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to strip emulator/DLC unlocker before updating {Game}", Instance.Name);
            }
        }

        // 3. Prepare differential installation plan
        progress.Report(new BackgroundTaskProgress(60, "Generating differential update plan...", "Planning"));

        // Snapshot the build we are LEAVING before its manifest map is overwritten. Without this
        // the previous version becomes unreachable: its .manifest files stay on disk, but nothing
        // records which set of them belonged together.
        HasPendingDepotUpdate = false;

        var buildHistory = PushBuildHistory(preparedInstance, "update");

        var updatedInstance = preparedInstance with
        {
            Depots = updatedDepots.AsReadOnly(),
            InstalledManifestMap = updatedManifestMap,
            BuildHistory = buildHistory,
            IsBuildPinned = false,
            HasUpdateAvailable = false,
            UpdateDescription = null,
            InstalledVersionDate = LatestVersionDate ?? DateTimeOffset.UtcNow
        };

        if (_installationPlanner != null)
        {
            var targetVersion = new GameVersion
            {
                BuildId = Instance.ActiveBuildId ?? "Updated",
                BranchName = Instance.ActiveBranch ?? "public",
                DisplayName = "Updated Build",
                Depots = updatedDepots.Select(d => new DepotVersion
                {
                    DepotId = d.DepotId,
                    ManifestId = d.ManifestId,
                    Name = d.Name,
                    DepotKey = d.DepotKey
                }).ToList().AsReadOnly()
            };

            var plan = await _installationPlanner.CreateUpdatePlanAsync(Instance, targetVersion, ct).ConfigureAwait(false);
            _logger.LogInformation("Differential update plan generated: {DepotsToDownload} depot(s) to download, {ReusedDepots} depot(s) reused.",
                plan.DepotsToDownload.Count, plan.ReusedDepots.Count);
        }

        await _instanceManager.UpdateAsync(updatedInstance, ct).ConfigureAwait(false);

        _verifiedInstancesThisSession.TryRemove(Instance.Id, out _);
        _apiClient?.InvalidateAppCache(Instance.AppId);
        if (_cacheService != null)
        {
            _ = _cacheService.RemoveAsync($"instance_update_state_{Instance.Id}");
        }

        // Reset all community emulation ratings and user vote flags for this game AppID due to new game update/build
        if (_emulatorRatingService != null)
        {
            await _emulatorRatingService.ResetRatingsForGameAsync(Instance.AppId, ct).ConfigureAwait(false);
        }

        progress.Report(new BackgroundTaskProgress(90, "Enqueuing updated depots for download...", "Finalizing"));


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
            var redeployNote = Instance.AwaitingPostUpdateRedeploy
                ? " The emulator and DLC unlocker were removed and will be reinstalled automatically once the download finishes."
                : string.Empty;

            _notificationService?.ShowSuccess(
                "Update Download Started",
                $"Enqueued {selected.Count} depot(s) to update {Instance.Name}. User mods, BepInEx and save files will remain intact.{redeployNote}");

            _ = CheckSteamVersionDateAsync();
            ActiveJob = _downloadQueueManager.Queue.FirstOrDefault(j => j.Instance.Id == Instance.Id);
            NotifyDownloadProps();
            RefreshRollbackBuilds();
        }, null);

        progress.Report(new BackgroundTaskProgress(100, $"Updated {selected.Count} depot(s) configuration.", "Complete"));
    }



    // ══════════════════════════════════════════════════════════════════════════
    //  OVERVIEW TAB — Steam showcase (long description, screenshots, patch notes)
    // ══════════════════════════════════════════════════════════════════════════

    [ObservableProperty]
    private ObservableCollection<string> _screenshots = [];

    public bool HasScreenshots => Screenshots.Count > 0;

    /// <summary>
    /// The screenshot shown large at the top of the gallery. Leading with one big frame reads far
    /// better than a uniform strip of small ones.
    /// </summary>
    [ObservableProperty]
    private string? _leadScreenshot;

    /// <summary>The remaining screenshots, shown as a thumbnail row under the lead frame.</summary>
    [ObservableProperty]
    private ObservableCollection<string> _thumbScreenshots = [];

    public bool HasThumbScreenshots => ThumbScreenshots.Count > 0;

    /// <summary>Engine name for the facts list, e.g. "Unity 6000.4 LTS (Mono x64)".</summary>
    public string EngineText => Instance?.Engine?.DisplayText is { Length: > 0 } n ? n : "—";

    [ObservableProperty]
    private ObservableCollection<SteamNewsItem> _newsItems = [];

    /// <summary>Publisher-declared genres.</summary>
    [ObservableProperty]
    private ObservableCollection<string> _genreTags = [];

    /// <summary>Steam feature categories (Single-player, Achievements, Cloud Saves…).</summary>
    [ObservableProperty]
    private ObservableCollection<string> _featureTags = [];

    /// <summary>Community tags from the store page, most applied first.</summary>
    [ObservableProperty]
    private ObservableCollection<string> _communityTags = [];

    public bool HasGenreTags => GenreTags.Count > 0;
    public bool HasFeatureTags => FeatureTags.Count > 0;
    public bool HasCommunityTags => CommunityTags.Count > 0;
    public bool HasAnyTags => HasGenreTags || HasFeatureTags || HasCommunityTags;

    public bool HasNewsItems => NewsItems.Count > 0;

    [ObservableProperty]
    private bool _isLoadingSteamShowcase;

    /// <summary>Gets the long store copy, falling back to the one-line summary.</summary>
    public string AboutGameText =>
        !string.IsNullOrWhiteSpace(Instance?.Metadata?.AboutTheGame) ? Instance!.Metadata!.AboutTheGame!
        : !string.IsNullOrWhiteSpace(Instance?.Metadata?.Description) ? Instance!.Metadata!.Description!
        : GetString("String_NoDescriptionProvided", "No description provided for this game instance.");

    public string DeveloperText => Instance?.Metadata?.Developer ?? "—";
    public string PublisherText => Instance?.Metadata?.Publisher ?? "—";
    public string ReleaseDateText => Instance?.Metadata?.ReleaseDate ?? "—";
    public string GenresText => Instance?.Metadata?.Genres is { Count: > 0 } g ? string.Join(" · ", g) : "—";

    public bool HasMetacriticScore => Instance?.Metadata?.MetacriticScore is > 0;
    public string MetacriticText => Instance?.Metadata?.MetacriticScore?.ToString() ?? string.Empty;

    public bool HasStoreWebsite => !string.IsNullOrWhiteSpace(Instance?.Metadata?.Website);

    /// <summary>Gets the on-disk size of the install folder, formatted, or an em dash.</summary>
    [ObservableProperty]
    private string _installedSizeText = "—";

    // ── Storage meter ─────────────────────────────────────────────────────────
    // A number on its own ("615 MB") says nothing about whether that is a lot. The meter puts the
    // game's footprint next to what the rest of the drive is doing.

    /// <summary>Drive letter or mount point the game lives on, e.g. "D:".</summary>
    [ObservableProperty]
    private string _driveLabel = string.Empty;

    /// <summary>Share of the whole drive taken by everything OTHER than this game, 0-100.</summary>
    [ObservableProperty]
    private double _driveOtherPercent;

    /// <summary>Share of the whole drive taken by this game, 0-100.</summary>
    [ObservableProperty]
    private double _gameSharePercent;

    /// <summary>Free space and capacity, already formatted for display.</summary>
    [ObservableProperty]
    private string _driveFreeText = string.Empty;

    /// <summary>Share of the whole drive still free, 0-100.</summary>
    [ObservableProperty]
    private double _driveFreePercent;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStorageMeter))]
    private bool _hasDriveInfo;

    /// <summary>True once the meter has real numbers to draw.</summary>
    public bool HasStorageMeter => HasDriveInfo;

    /// <summary>Formats a byte count the way the rest of the app does.</summary>
    private static string FormatBytes(long bytes) => bytes switch
    {
        >= 1024L * 1024 * 1024 * 1024 => $"{bytes / (1024.0 * 1024 * 1024 * 1024):F2} TB",
        >= 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024 * 1024):F2} GB",
        >= 1024L * 1024 => $"{bytes / (1024.0 * 1024):F0} MB",
        > 0 => $"{bytes / 1024.0:F0} KB",
        _ => "—"
    };

    /// <summary>Gets a one-line summary of the emulator state for the Overview status card.</summary>
    public string EmulatorSummaryText
    {
        get
        {
            if (Instance == null) return "—";
            if (!IsEmulatorInstalled && string.IsNullOrWhiteSpace(Instance.EmulatorId))
                return GetString("String_OverviewNoEmulator", "None installed");

            var mode = !string.IsNullOrWhiteSpace(InstalledEmulatorMode) ? InstalledEmulatorMode : Instance.EmulatorId;
            var version = !string.IsNullOrWhiteSpace(Instance.InstalledEmulatorVersion)
                ? $" · v{Instance.InstalledEmulatorVersion}"
                : string.Empty;
            return $"{mode}{version}";
        }
    }

    /// <summary>Gets a one-line summary of the DLC unlocker state.</summary>
    public string DlcUnlockerSummaryText
    {
        get
        {
            if (Instance == null) return "—";
            if (!Instance.DlcUnlockerInstalled && !IsDlcUnlocked)
                return GetString("String_OverviewNoUnlocker", "Not installed");

            var count = Instance.UnlockedDlcIds?.Count ?? 0;
            var total = Instance.Dlcs?.Count ?? 0;
            return total > 0
                ? string.Format(GetString("String_OverviewUnlockerFormat", "SmokeAPI · {0} of {1} DLC"), count, total)
                : "SmokeAPI";
        }
    }

    /// <summary>Gets a one-line summary of the mod state.</summary>
    public string ModsSummaryText
    {
        get
        {
            if (Instance == null) return "—";
            var installed = InstalledMods?.Count ?? 0;
            if (installed == 0 && !IsBepInExInstalled)
                return GetString("String_OverviewNoMods", "None installed");

            var loader = IsBepInExInstalled && !string.IsNullOrWhiteSpace(InstalledBepInExVersion)
                ? $"BepInEx {InstalledBepInExVersion} · "
                : string.Empty;
            return string.Format(GetString("String_OverviewModsFormat", "{0}{1} mod(s)"), loader, installed);
        }
    }

    /// <summary>
    /// Loads the Steam store showcase for the Overview tab: long description, screenshots and the
    /// recent patch notes. All of it is cached, so reopening an instance does not re-hit Steam.
    /// </summary>
    public async Task LoadSteamShowcaseAsync()
    {
        if (_isDisposed || Instance == null || Instance.AppId == 0) return;
        if (_metadataProvider is not BlueStar.Infrastructure.Metadata.SteamStoreApiClient steamClient) return;

        IsLoadingSteamShowcase = true;
        try
        {
            // Refresh metadata when the stored copy predates the richer fields (no screenshots yet).
            var meta = Instance.Metadata;
            if (meta == null || meta.Screenshots.Count == 0 || string.IsNullOrWhiteSpace(meta.AboutTheGame))
            {
                try
                {
                    var fetched = await steamClient.GetMetadataAsync(Instance.AppId, _cts.Token).ConfigureAwait(true);
                    if (fetched != null)
                    {
                        meta = fetched;
                        if (_isDisposed) return;

                        Instance = Instance with { Metadata = fetched };
                        await _instanceManager.UpdateAsync(Instance, CancellationToken.None).ConfigureAwait(true);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Could not refresh Steam metadata for AppId {AppId}", Instance.AppId);
                }
            }

            if (_isDisposed) return;

            IReadOnlyList<string> shots = meta?.Screenshots ?? [];
            Screenshots = new ObservableCollection<string>(shots.Take(12));
            LeadScreenshot = Screenshots.FirstOrDefault();
            ThumbScreenshots = new ObservableCollection<string>(Screenshots.Skip(1));
            OnPropertyChanged(nameof(HasScreenshots));
            OnPropertyChanged(nameof(HasThumbScreenshots));

            IReadOnlyList<string> genres = meta?.Genres ?? [];
            IReadOnlyList<string> features = meta?.Categories ?? [];

            // Community tags are scraped from the store page, so they are fetched separately and
            // only for the instance being looked at — the library and browse views call
            // GetMetadataAsync constantly and must not pay for a full page load.
            IReadOnlyList<string> community = meta?.StoreTags ?? [];
            if (community.Count == 0)
            {
                try
                {
                    community = await steamClient.GetStoreTagsAsync(Instance.AppId, _cts.Token).ConfigureAwait(true);
                    if (_isDisposed) return;

                    if (community.Count > 0 && meta != null)
                    {
                        meta = meta with { StoreTags = community };
                        Instance = Instance with { Metadata = meta };
                        await _instanceManager.UpdateAsync(Instance, CancellationToken.None).ConfigureAwait(true);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Could not read store tags for AppId {AppId}", Instance.AppId);
                }
            }

            GenreTags = new ObservableCollection<string>(genres);
            FeatureTags = new ObservableCollection<string>(features);
            CommunityTags = new ObservableCollection<string>(community.Take(24));

            OnPropertyChanged(nameof(HasGenreTags));
            OnPropertyChanged(nameof(HasFeatureTags));
            OnPropertyChanged(nameof(HasCommunityTags));
            OnPropertyChanged(nameof(HasAnyTags));

            NotifyOverviewProps();

            try
            {
                var news = await steamClient.GetNewsAsync(Instance.AppId, 8, _cts.Token).ConfigureAwait(true);
                if (_isDisposed) return;
                NewsItems = new ObservableCollection<SteamNewsItem>(news.Take(6));
                OnPropertyChanged(nameof(HasNewsItems));
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not fetch Steam news for AppId {AppId}", Instance.AppId);
            }
        }
        finally
        {
            IsLoadingSteamShowcase = false;
        }
    }

    /// <summary>Raises change notification for every computed Overview field.</summary>
    public void NotifyOverviewProps()
    {
        OnPropertyChanged(nameof(AboutGameText));
        OnPropertyChanged(nameof(DeveloperText));
        OnPropertyChanged(nameof(PublisherText));
        OnPropertyChanged(nameof(ReleaseDateText));
        OnPropertyChanged(nameof(GenresText));
        OnPropertyChanged(nameof(HasMetacriticScore));
        OnPropertyChanged(nameof(MetacriticText));
        OnPropertyChanged(nameof(HasStoreWebsite));
        OnPropertyChanged(nameof(EmulatorSummaryText));
        OnPropertyChanged(nameof(DlcUnlockerSummaryText));
        OnPropertyChanged(nameof(ModsSummaryText));
        OnPropertyChanged(nameof(HasScreenshots));
        OnPropertyChanged(nameof(HasThumbScreenshots));
        OnPropertyChanged(nameof(EngineText));
        OnPropertyChanged(nameof(HasNewsItems));
        OnPropertyChanged(nameof(HasGenreTags));
        OnPropertyChanged(nameof(HasFeatureTags));
        OnPropertyChanged(nameof(HasCommunityTags));
        OnPropertyChanged(nameof(HasAnyTags));
    }

    /// <summary>Measures the install folder so the Overview can show what it occupies on disk.</summary>
    public async Task RefreshInstalledSizeAsync()
    {
        var installPath = Instance?.InstallPath;
        if (string.IsNullOrWhiteSpace(installPath) || !Directory.Exists(installPath))
        {
            InstalledSizeText = "—";
            HasDriveInfo = false;
            return;
        }

        // Non-null copy for the background lambda: flow analysis does not carry the check above
        // across the closure boundary.
        string path = installPath;

        try
        {
            long total = await Task.Run(() =>
            {
                long sum = 0;
                try
                {
                    foreach (var f in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                    {
                        try { sum += new FileInfo(f).Length; } catch { }
                    }
                }
                catch { }
                return sum;
            }).ConfigureAwait(true);

            if (_isDisposed) return;

            InstalledSizeText = FormatBytes(total);

            // Drive figures for the meter.
            try
            {
                var root = Path.GetPathRoot(Path.GetFullPath(path));
                if (!string.IsNullOrWhiteSpace(root))
                {
                    var drive = new DriveInfo(root);
                    if (drive.IsReady && drive.TotalSize > 0)
                    {
                        var capacity = drive.TotalSize;
                        var free = drive.AvailableFreeSpace;
                        var used = Math.Max(0, capacity - free);
                        var game = Math.Min(total, used);

                        GameSharePercent  = Math.Round(game / (double)capacity * 100.0, 3);
                        DriveOtherPercent = Math.Round((used - game) / (double)capacity * 100.0, 3);

                        DriveFreePercent  = Math.Round(free / (double)capacity * 100.0, 3);
                        DriveLabel = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                        DriveFreeText = string.Format(
                            GetString("String_OverviewDriveFreeFormat", "{0} free of {1}"),
                            FormatBytes(free), FormatBytes(capacity));
                        HasDriveInfo = true;
                    }
                    else
                    {
                        HasDriveInfo = false;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not read drive figures for {Path}", path);
                HasDriveInfo = false;
            }
        }
        catch
        {
            InstalledSizeText = "—";
            HasDriveInfo = false;
        }
    }

    /// <summary>Promotes a thumbnail to the large frame, so the gallery is browsable in place.</summary>
    [RelayCommand]
    public void ShowScreenshot(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || url == LeadScreenshot) return;

        var previousLead = LeadScreenshot;
        LeadScreenshot = url;

        var thumbs = Screenshots.Where(u => u != url).ToList();
        ThumbScreenshots = new ObservableCollection<string>(thumbs);
        OnPropertyChanged(nameof(HasThumbScreenshots));

        _ = previousLead;
    }

    /// <summary>Opens a Steam news entry in the default browser.</summary>
    [RelayCommand]
    public void OpenNewsItem(SteamNewsItem? item)
    {
        if (item == null || string.IsNullOrWhiteSpace(item.Url)) return;
        try
        {
            Process.Start(new ProcessStartInfo { FileName = item.Url, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not open news link {Url}", item.Url);
        }
    }

    /// <summary>Opens the game's Steam store page.</summary>
    [RelayCommand]
    public void OpenStorePage()
    {
        if (Instance == null || Instance.AppId == 0) return;
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = $"https://store.steampowered.com/app/{Instance.AppId}",
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not open the store page for AppId {AppId}", Instance?.AppId);
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  VERSION TAB — four intent-based modes replacing the raw depot checklist
    // ══════════════════════════════════════════════════════════════════════════

    public const string VersionModeLatest   = "Latest";
    public const string VersionModeCurated  = "Curated";
    public const string VersionModeRollback = "Rollback";
    public const string VersionModeCustom   = "Custom";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsVersionModeLatest))]
    [NotifyPropertyChangedFor(nameof(IsVersionModeCurated))]
    [NotifyPropertyChangedFor(nameof(IsVersionModeRollback))]
    [NotifyPropertyChangedFor(nameof(IsVersionModeCustom))]
    private string _versionMode = VersionModeLatest;

    public bool IsVersionModeLatest   => VersionMode == VersionModeLatest;
    public bool IsVersionModeCurated  => VersionMode == VersionModeCurated;
    public bool IsVersionModeRollback => VersionMode == VersionModeRollback;
    public bool IsVersionModeCustom   => VersionMode == VersionModeCustom;

    partial void OnVersionModeChanged(string value)
    {
        if (value == VersionModeLatest)
        {
            ApplyLatestAutoSelection();
        }
        else if (value == VersionModeRollback)
        {
            RefreshRollbackBuilds();
        }
        else if (value == VersionModeCustom)
        {
            RecomputeCustomBuildValidation();
        }
    }

    [RelayCommand]
    public void SelectVersionMode(string? mode)
    {
        if (string.IsNullOrWhiteSpace(mode)) return;
        VersionMode = mode;
    }

    // ── Mode 1: Latest ────────────────────────────────────────────────────────

    [ObservableProperty]
    private bool _isLatestDepotDetailExpanded;

    [RelayCommand]
    public void ToggleLatestDepotDetail() => IsLatestDepotDetailExpanded = !IsLatestDepotDetailExpanded;

    [ObservableProperty]
    private int _autoSelectedDepotCount;

    [ObservableProperty]
    private int _autoSkippedDepotCount;

    [ObservableProperty]
    private string _autoSelectionSummary = string.Empty;

    [ObservableProperty]
    private string _autoSelectionSkippedSummary = string.Empty;

    /// <summary>
    /// Gets the depot ids that belong to a DLC rather than the base game. Those are downloaded
    /// from the DLCs tab, so the Latest mode must leave them alone.
    /// </summary>
    private HashSet<uint> GetDlcDepotIds()
    {
        var ids = new HashSet<uint>();
        foreach (var dlc in Instance?.Dlcs ?? [])
        {
            foreach (var d in dlc.Depots ?? [])
            {
                ids.Add(d.DepotId);
            }
        }
        return ids;
    }

    /// <summary>
    /// Selects exactly the base-game depots that match the current OS and architecture, and
    /// deselects everything else. This is what makes "Latest" a one-click action instead of a
    /// checklist the user has to reason about.
    /// </summary>
    public void ApplyLatestAutoSelection()
    {
        if (Depots.Count == 0) return;

        var dlcDepotIds = GetDlcDepotIds();
        int selected = 0, skipped = 0;

        foreach (var item in Depots)
        {
            bool isDlcDepot = dlcDepotIds.Contains(item.Depot.DepotId);
            bool compatible = IsDepotCompatibleWithCurrentOS(item.Depot);
            bool take = compatible && !isDlcDepot;

            item.IsSelected = take;
            if (take) selected++; else skipped++;
        }

        AutoSelectedDepotCount = selected;
        AutoSkippedDepotCount = skipped;

        var osLabel = Environment.Is64BitOperatingSystem ? "Windows x64" : "Windows x86";
        AutoSelectionSummary = string.Format(
            GetString("String_VersionAutoSelectedFormat", "{0} depot(s) selected for {1}"),
            selected, osLabel);

        AutoSelectionSkippedSummary = skipped > 0
            ? string.Format(
                GetString("String_VersionAutoSkippedFormat", "{0} depot(s) skipped: other platforms, other architectures and DLC content"),
                skipped)
            : GetString("String_VersionAutoSkippedNone", "Every depot of this game applies to your system.");

        RecalculateSelectedSize();
        OnPropertyChanged(nameof(SelectedDepotsCount));
        OnPropertyChanged(nameof(SelectedDepotsSize));
    }

    public string LatestBuildIdText =>
        !string.IsNullOrWhiteSpace(SelectedBuild?.BuildId) ? SelectedBuild!.BuildId
        : !string.IsNullOrWhiteSpace(Instance?.ActiveBuildId) ? Instance!.ActiveBuildId!
        : GetString("String_VersionUnknownBuild", "latest available");

    public string LatestBuildBranchText => SelectedBuild?.BranchName ?? Instance?.ActiveBranch ?? "public";

    public string LatestBuildDateText =>
        SelectedBuild?.UpdatedAt?.ToString("d MMM yyyy") ?? LatestVersionText ?? "—";

    // ── Mode 2: Curated ───────────────────────────────────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCurationRecommendation))]
    [NotifyPropertyChangedFor(nameof(InstallModalHasRecommendedVersion))]
    [NotifyPropertyChangedFor(nameof(CuratedBuildIdText))]
    [NotifyPropertyChangedFor(nameof(CuratedSummaryText))]
    [NotifyPropertyChangedFor(nameof(CuratedNotesText))]
    [NotifyPropertyChangedFor(nameof(HasCuratedNotes))]
    [NotifyPropertyChangedFor(nameof(CuratedEmulatorText))]
    [NotifyPropertyChangedFor(nameof(CuratedSourceText))]
    [NotifyPropertyChangedFor(nameof(CuratedPinnedCountText))]
    private CurationRecommendation? _curatedRecommendation;

    /// <summary>
    /// Gets whether a curated build exists for this AppID. The whole "Recommended" mode button is
    /// hidden when it does not — there is nothing to recommend.
    /// </summary>
    public bool HasCurationRecommendation => CuratedRecommendation != null;

    public string CuratedBuildIdText => CuratedRecommendation?.RecommendedBuildId ?? "—";
    public string CuratedSummaryText => CuratedRecommendation?.Summary
        ?? GetString("String_VersionCuratedNoSummary", "A build the community verified as stable for this game.");
    public string CuratedNotesText => CuratedRecommendation?.CompatibilityNotes ?? string.Empty;
    public bool HasCuratedNotes => !string.IsNullOrWhiteSpace(CuratedRecommendation?.CompatibilityNotes);
    public string CuratedEmulatorText => string.IsNullOrWhiteSpace(CuratedRecommendation?.RecommendedEmulator)
        ? GetString("String_VersionCuratedNoEmulator", "none required")
        : CuratedRecommendation!.RecommendedEmulator!;
    public string CuratedSourceText => CuratedRecommendation == null
        ? string.Empty
        : $"{CuratedRecommendation.Source} · {CuratedRecommendation.CuratedAt.LocalDateTime:d MMM yyyy}";
    public string CuratedPinnedCountText => CuratedRecommendation == null
        ? "0"
        : CuratedRecommendation.PinnedManifests.Count.ToString();

    /// <summary>
    /// Loads the curated recommendation for this AppID, if any provider has one.
    /// </summary>
    public async Task LoadCurationRecommendationAsync()
    {
        if (_recommendationProvider == null || Instance == null || Instance.AppId == 0) return;

        try
        {
            var rec = await _recommendationProvider
                .GetRecommendationAsync(Instance.AppId, _cts.Token)
                .ConfigureAwait(true);

            if (_isDisposed) return;
            CuratedRecommendation = rec;

            // A mode that is not offered must not stay selected.
            if (rec == null && VersionMode == VersionModeCurated)
            {
                VersionMode = VersionModeLatest;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "No curated recommendation for AppId {AppId}", Instance?.AppId);
        }
    }

    /// <summary>
    /// Pins the instance to the curated build and turns update checks off, which is the entire
    /// point of choosing a curated version.
    /// </summary>
    [RelayCommand]
    public async Task ApplyCuratedBuildAsync()
    {
        if (Instance == null || CuratedRecommendation == null) return;

        var rec = CuratedRecommendation;

        try
        {
            var updatedDepots = Instance.Depots.Select(d =>
                rec.PinnedManifests.TryGetValue(d.DepotId, out var pinned) && pinned > 0
                    ? d with { ManifestId = pinned, IsDownloaded = false }
                    : d).ToList();

            var map = new Dictionary<uint, ulong>(Instance.InstalledManifestMap);
            foreach (var kv in rec.PinnedManifests) map[kv.Key] = kv.Value;

            Instance = Instance with
            {
                Depots = updatedDepots.AsReadOnly(),
                ActiveBuildId = rec.RecommendedBuildId,
                ActiveBranch = rec.RecommendedBranch,
                InstalledManifestMap = map,
                IsBuildPinned = true,
                DisableUpdateChecks = true,
                HasUpdateAvailable = false,
                UpdateDescription = null
            };

            DisableUpdateChecks = true;
            HasGameUpdateAvailable = false;

            await _instanceManager.UpdateAsync(Instance, CancellationToken.None).ConfigureAwait(true);
            await LoadInstanceAsync(Instance).ConfigureAwait(true);

            StatusMessage = string.Format(
                GetString("String_VersionCuratedAppliedFormat", "✅ Pinned to curated build {0}. Update checks are now off."),
                rec.RecommendedBuildId);

            _notificationService?.ShowSuccess(
                GetString("String_VersionCuratedAppliedTitle", "Build Pinned"),
                StatusMessage);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to apply curated build for {Name}", Instance?.Name);
            StatusMessage = $"❌ {ex.Message}";
        }
    }

    // ── Mode 3: Rollback to a previously installed build ──────────────────────

    [ObservableProperty]
    private ObservableCollection<RollbackBuildItem> _rollbackBuilds = [];

    public bool HasRollbackBuilds => RollbackBuilds.Count > 0;

    /// <summary>
    /// Rebuilds the rollback list from the instance's build history, checking for each entry
    /// whether its manifests are still recoverable from the local cache and whether the depot
    /// keys are known. An entry that is not fully recoverable is shown but not offered.
    /// </summary>
    public void RefreshRollbackBuilds()
    {
        var list = new ObservableCollection<RollbackBuildItem>();

        if (Instance != null)
        {
            var instanceManifestDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "BlueStar", "instances", Instance.Id.ToString(), "manifests");

            foreach (var snap in Instance.BuildHistory ?? [])
            {
                int total = snap.ManifestMap.Count;
                int cached = 0;
                var missing = new List<uint>();

                foreach (var kv in snap.ManifestMap)
                {
                    bool inCache = _manifestCacheService?.HasManifest(kv.Key, kv.Value) ?? false;

                    if (!inCache)
                    {
                        // The instance folder is the other place a manifest can legitimately live.
                        var local = Path.Combine(instanceManifestDir, $"{kv.Key}_{kv.Value}.manifest");
                        try { inCache = File.Exists(local) && new FileInfo(local).Length > 32; }
                        catch { inCache = false; }
                    }

                    if (inCache) cached++; else missing.Add(kv.Key);
                }

                bool isCurrent = !string.IsNullOrWhiteSpace(Instance.ActiveBuildId) &&
                                 string.Equals(Instance.ActiveBuildId, snap.BuildId, StringComparison.OrdinalIgnoreCase);

                list.Add(new RollbackBuildItem
                {
                    Snapshot = snap,
                    CachedManifestCount = cached,
                    TotalManifestCount = total,
                    IsCurrentBuild = isCurrent,
                    MissingDepotIds = missing.AsReadOnly()
                });
            }
        }

        RollbackBuilds = list;
        OnPropertyChanged(nameof(HasRollbackBuilds));
    }

    /// <summary>
    /// Re-applies a previous build's manifest map and queues the download that restores it.
    /// </summary>
    [RelayCommand]
    public async Task RollbackToBuildAsync(RollbackBuildItem? item)
    {
        if (Instance == null || item == null || !item.CanRestore) return;

        if (IsGameRunning)
        {
            _notificationService?.ShowWarning(
                GetString("String_GameRunningTitle", "Game is Running"),
                GetString("String_GameRunningRollbackDesc", "Close the game before rolling back to another version."));
            return;
        }

        var snap = item.Snapshot;

        try
        {
            IsApplyingGameUpdate = true;

            // Same rule as a forward update: strip the layers first so the depot write lands on
            // pristine files and the emulator/unlocker are rebuilt on top afterwards.
            var prepared = Instance;
            if (_emulatorLifecycleService != null)
            {
                prepared = await _emulatorLifecycleService
                    .PrepareForGameUpdateAsync(Instance, null, CancellationToken.None)
                    .ConfigureAwait(true);
            }

            var rolledDepots = prepared.Depots.Select(d =>
                snap.ManifestMap.TryGetValue(d.DepotId, out var mid) && mid > 0
                    ? d with { ManifestId = mid, IsDownloaded = false }
                    : d).ToList();

            var history = PushBuildHistory(prepared, "rollback");

            Instance = prepared with
            {
                Depots = rolledDepots.AsReadOnly(),
                InstalledManifestMap = new Dictionary<uint, ulong>(snap.ManifestMap),
                ActiveBuildId = snap.BuildId,
                ActiveBranch = snap.BranchName,
                InstalledVersionDate = snap.BuildDate ?? snap.InstalledAt,
                BuildHistory = history,
                IsBuildPinned = true,
                DisableUpdateChecks = true,
                HasUpdateAvailable = false,
                UpdateDescription = null
            };

            DisableUpdateChecks = true;
            HasGameUpdateAvailable = false;

            await _instanceManager.UpdateAsync(Instance, CancellationToken.None).ConfigureAwait(true);

            var downloadInstance = Instance with
            {
                Depots = rolledDepots.Where(d => snap.ManifestMap.ContainsKey(d.DepotId)).ToList().AsReadOnly()
            };

            await _downloadQueueManager.StartDownloadAsync(downloadInstance).ConfigureAwait(true);

            StatusMessage = string.Format(
                GetString("String_VersionRollbackStartedFormat", "⏪ Rolling back to build {0}. Update checks were turned off."),
                snap.BuildId);

            _notificationService?.ShowInfo(
                GetString("String_VersionRollbackStartedTitle", "Rolling Back"),
                StatusMessage);

            ActiveJob = _downloadQueueManager.Queue.FirstOrDefault(j => j.Instance.Id == Instance.Id);
            NotifyDownloadProps();
            RefreshRollbackBuilds();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Rollback failed for {Name}", Instance?.Name);
            StatusMessage = $"❌ {ex.Message}";
            _notificationService?.ShowError(GetString("String_VersionRollbackFailedTitle", "Rollback Failed"), ex.Message);
        }
        finally
        {
            IsApplyingGameUpdate = false;
        }
    }

    /// <summary>
    /// Records the instance's CURRENT build into its history before it is overwritten, so it can
    /// be rolled back to later. Newest first, capped at <see cref="MaxBuildHistoryEntries"/>.
    /// </summary>
    public const int MaxBuildHistoryEntries = 6;

    private static IReadOnlyList<InstalledBuildSnapshot> PushBuildHistory(GameInstance instance, string reason)
    {
        // Nothing installed yet, or no manifests to remember: nothing worth recording.
        if (instance.InstalledManifestMap == null || instance.InstalledManifestMap.Count == 0)
        {
            return instance.BuildHistory ?? [];
        }

        var buildId = !string.IsNullOrWhiteSpace(instance.ActiveBuildId)
            ? instance.ActiveBuildId!
            : instance.InstalledVersionDate?.ToString("yyyyMMdd") ?? "unknown";

        var snapshot = new InstalledBuildSnapshot
        {
            BuildId = buildId,
            BranchName = instance.ActiveBranch ?? "public",
            DisplayName = reason == "rollback" ? null : instance.UpdateDescription,
            ManifestMap = new Dictionary<uint, ulong>(instance.InstalledManifestMap),
            InstalledAt = instance.UpdatedAt,
            BuildDate = instance.InstalledVersionDate,
            SizeBytes = instance.Depots?.Sum(d => d.SizeBytes) ?? 0
        };

        var history = new List<InstalledBuildSnapshot>();
        history.Add(snapshot);

        foreach (var old in instance.BuildHistory ?? [])
        {
            // Never keep two entries for the same build.
            if (string.Equals(old.BuildId, snapshot.BuildId, StringComparison.OrdinalIgnoreCase)) continue;
            history.Add(old);
            if (history.Count >= MaxBuildHistoryEntries) break;
        }

        return history.AsReadOnly();
    }

    // ── Mode 4: Custom manifests ──────────────────────────────────────────────

    [ObservableProperty]
    private string _customVersionBuildId = string.Empty;

    [ObservableProperty]
    private string _customVersionBuildName = string.Empty;

    [ObservableProperty]
    private string _customBuildValidationMessage = string.Empty;

    [ObservableProperty]
    private bool _isCustomBuildValid;

    [ObservableProperty]
    private string _customBuildReadySummary = string.Empty;

    /// <summary>
    /// Validates the custom manifest table and produces the message that gates the download
    /// button. Steam manifest ids are 19-digit unsigned 64-bit values; the most common mistake by
    /// far is a truncated paste, which used to fail only once the download had already started.
    /// </summary>
    public void RecomputeCustomBuildValidation()
    {
        if (Depots.Count == 0)
        {
            IsCustomBuildValid = false;
            CustomBuildValidationMessage = GetString("String_VersionCustomNoDepots", "This instance has no depots to configure.");
            CustomBuildReadySummary = string.Empty;
            return;
        }

        int ready = 0;
        var problems = new List<string>();

        foreach (var item in Depots)
        {
            var text = (item.ManifestInputText ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(text))
            {
                text = item.Depot.ManifestId > 0 ? item.Depot.ManifestId.ToString() : string.Empty;
            }

            if (!TryParseManifestId(text, out var manifestId))
            {
                // The field holds something with no id in it at all (a stale label, a cleared box)
                // while the depot itself has a perfectly good manifest. That is not a user error:
                // the download would use the depot's id, so accept it silently.
                if (!text.Any(char.IsDigit) && item.Depot.ManifestId > 0)
                {
                    item.ManifestInputText = item.Depot.ManifestId.ToString();
                    ready++;
                    continue;
                }

                // Only a genuinely unusable value gets flagged. Anything containing a real id —
                // a pasted option label, an id with stray spaces — is accepted above.
                var digits = new string((text ?? string.Empty).Where(char.IsDigit).ToArray());
                if (digits.Length > 0 && digits.Length < 10)
                {
                    problems.Add(string.Format(
                        GetString("String_VersionCustomShortIdFormat", "Depot {0}: manifest id has {1} digits; Steam ids have 19. Looks like a truncated paste."),
                        item.Depot.DepotId, digits.Length));
                }
                else
                {
                    problems.Add(string.Format(
                        GetString("String_VersionCustomBadIdFormat", "Depot {0}: “{1}” is not a valid manifest id."),
                        item.Depot.DepotId, string.IsNullOrWhiteSpace(text) ? "—" : text));
                }
                continue;
            }

            ready++;
        }

        IsCustomBuildValid = problems.Count == 0;
        CustomBuildReadySummary = string.Format(
            GetString("String_VersionCustomReadyFormat", "{0} of {1} depots ready"),
            ready, Depots.Count);

        CustomBuildValidationMessage = problems.Count == 0
            ? GetString("String_VersionCustomAllValid", "Every depot has a valid manifest id.")
            : problems[0];
    }

    /// <summary>
    /// Applies the custom manifest ids to the instance and queues the download.
    /// </summary>
    [RelayCommand]
    public async Task DownloadCustomBuildAsync()
    {
        if (Instance == null) return;

        RecomputeCustomBuildValidation();
        if (!IsCustomBuildValid)
        {
            _notificationService?.ShowWarning(
                GetString("String_VersionCustomInvalidTitle", "Manifest Configuration Invalid"),
                CustomBuildValidationMessage);
            return;
        }

        try
        {
            var map = new Dictionary<uint, ulong>();
            foreach (var item in Depots)
            {
                var text = (item.ManifestInputText ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(text) && item.Depot.ManifestId > 0)
                {
                    text = item.Depot.ManifestId.ToString();
                }
                if (TryParseManifestId(text, out var mid))
                {
                    map[item.Depot.DepotId] = mid;
                }
            }

            if (map.Count == 0) return;

            var prepared = Instance;
            if (_emulatorLifecycleService != null)
            {
                prepared = await _emulatorLifecycleService
                    .PrepareForGameUpdateAsync(Instance, null, CancellationToken.None)
                    .ConfigureAwait(true);
            }

            var history = PushBuildHistory(prepared, "custom");
            var buildId = !string.IsNullOrWhiteSpace(CustomVersionBuildId) ? CustomVersionBuildId.Trim() : "custom";

            var updatedDepots = prepared.Depots.Select(d =>
                map.TryGetValue(d.DepotId, out var mid)
                    ? d with { ManifestId = mid, IsDownloaded = false }
                    : d).ToList();

            Instance = prepared with
            {
                Depots = updatedDepots.AsReadOnly(),
                InstalledManifestMap = map,
                ActiveBuildId = buildId,
                ActiveBranch = "custom",
                BuildHistory = history,
                IsBuildPinned = true,
                DisableUpdateChecks = true,
                HasUpdateAvailable = false,
                UpdateDescription = null
            };

            DisableUpdateChecks = true;
            HasGameUpdateAvailable = false;

            await _instanceManager.UpdateAsync(Instance, CancellationToken.None).ConfigureAwait(true);

            var downloadInstance = Instance with
            {
                Depots = updatedDepots.Where(d => map.ContainsKey(d.DepotId)).ToList().AsReadOnly()
            };

            await _downloadQueueManager.StartDownloadAsync(downloadInstance).ConfigureAwait(true);

            StatusMessage = string.Format(
                GetString("String_VersionCustomStartedFormat", "⬇ Downloading custom build {0} ({1} depot(s))."),
                buildId, map.Count);

            ActiveJob = _downloadQueueManager.Queue.FirstOrDefault(j => j.Instance.Id == Instance.Id);
            NotifyDownloadProps();
            RefreshRollbackBuilds();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Custom build download failed for {Name}", Instance?.Name);
            StatusMessage = $"❌ {ex.Message}";
            _notificationService?.ShowError(GetString("String_VersionCustomFailedTitle", "Custom Build Failed"), ex.Message);
        }
    }

    /// <summary>
    /// Translates SteamCMD's <c>config.oslist</c> ("windows", "linux,macos", …) into the platform
    /// label the UI shows. Returns null when the depot declares no OS restriction, in which case
    /// whatever was already inferred is kept.
    /// </summary>
    /// <summary>
    /// Reads the platform out of a depot's own name, for the depots SteamCMD reports no
    /// <c>oslist</c> for. A depot literally called "Main Windows Depot …" must never end up tagged
    /// macOS just because an older parse guessed wrong and the guess was persisted.
    /// </summary>
    private static string? PlatformFromDepotName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;

        // Whole-word matches only: "Machine" must not read as "mac".
        bool Has(string pattern) => System.Text.RegularExpressions.Regex.IsMatch(
            name, pattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        if (Has(@"\b(?:windows|win)\s*(?:depot|build|content|binaries)\b") || Has(@"\bwin(?:32|64)\b"))
            return "Windows";
        if (Has(@"\b(?:linux|steamos)\s*(?:depot|build|content|binaries)\b"))
            return "Linux";
        if (Has(@"\b(?:mac|macos|osx|darwin)\s*(?:depot|build|content|binaries)\b"))
            return "macOS";

        if (Has(@"\bwindows\b")) return "Windows";
        if (Has(@"\b(?:linux|steamos)\b")) return "Linux";
        if (Has(@"\b(?:macos|mac\s?os|osx|os\s?x|darwin)\b")) return "macOS";

        return null;
    }

    private static string? MapOsListToPlatform(string? osList)
    {
        if (string.IsNullOrWhiteSpace(osList)) return null;

        var entries = osList
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(e => e.ToLowerInvariant())
            .ToList();

        if (entries.Count == 0) return null;
        if (entries.Count > 1) return "Universal";

        return entries[0] switch
        {
            "windows" => "Windows",
            "linux" => "Linux",
            "macos" or "mac" or "osx" => "macOS",
            _ => null
        };
    }

    /// <summary>
    /// Placeholder shown while the Steam version lookup is in flight.
    /// <para>
    /// It is a transient state, never an answer, so it must never be persisted or left on screen:
    /// a lookup that failed once used to write this text into the 30-day state cache, and every
    /// later visit replayed it — which is how "Latest on Steam" got stuck on "Checking…" forever.
    /// </para>
    /// </summary>
    public const string CheckingSentinel = "Checking...";

    /// <summary>True when the given text is the in-flight placeholder rather than a real answer.</summary>
    private static bool IsCheckingPlaceholder(string? text) =>
        string.IsNullOrWhiteSpace(text) ||
        string.Equals(text, CheckingSentinel, StringComparison.Ordinal);

    /// <summary>
    /// Makes sure the version labels never stay on the placeholder once a lookup has finished,
    /// whatever the outcome.
    /// </summary>
    private void SettleVersionTexts()
    {
        if (IsCheckingPlaceholder(LatestVersionText))
        {
            LatestVersionText = GetString("String_VersionLookupUnavailable", "Not available");
        }

        if (IsCheckingPlaceholder(InstalledVersionText))
        {
            InstalledVersionText = GetString("String_VersionLookupUnknown", "Unknown");
        }
    }

    /// <summary>
    /// Extracts the manifest id from whatever ended up in a depot's text field.
    /// <para>
    /// The editable ComboBox can hand back the option's full display text
    /// ("3889140805796509645 (Current - Steam)") rather than the bare id, and users paste ids with
    /// stray spaces or surrounding punctuation. Rather than rejecting all of that as invalid, pull
    /// out the first long run of digits — a Steam manifest id is a 64-bit value, so 15+ digits is
    /// unambiguous within any of these strings.
    /// </para>
    /// </summary>
    public static bool TryParseManifestId(string? text, out ulong manifestId)
    {
        manifestId = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var trimmed = text.Trim();
        if (ulong.TryParse(trimmed, out manifestId) && manifestId > 0) return true;

        var match = System.Text.RegularExpressions.Regex.Match(trimmed, @"\d{10,20}");
        if (match.Success && ulong.TryParse(match.Value, out manifestId) && manifestId > 0)
        {
            return true;
        }

        manifestId = 0;
        return false;
    }

    /// <summary>
    /// Resolves a localized string by resource key, falling back to the supplied English text so
    /// nothing ever renders blank if a key is missing from a dictionary.
    /// </summary>
    private static string GetString(string key, string fallback)
    {
        try
        {
            var value = System.Windows.Application.Current?.TryFindResource(key) as string;
            return string.IsNullOrWhiteSpace(value) ? fallback : value;
        }
        catch
        {
            return fallback;
        }
    }

    [RelayCommand]
    public void SwitchTab(string tabName) => SelectedTab = tabName;

    // ── Launch Game Command ──
    [RelayCommand]
    public async Task LaunchGameAsync()
    {
        if (Instance == null) return;

        if (Instance.Status == InstanceStatus.NotInstalled)
        {
            await OpenInstallModalAsync().ConfigureAwait(true);
            return;
        }

        if (IsGameRunning)
        {
            StatusMessage = "Stopping game process...";
            await _gameLauncher.KillAsync(Instance.Id).ConfigureAwait(true);
            return;
        }

        // Check if emulator is online and Steam is not running
        var isOnlineEmulator = (Instance.EmulatorEnabled || (Instance.InstalledFixLayers != null && Instance.InstalledFixLayers.Count > 0)) &&
            (string.Equals(Instance.EmulatorId, "refix_valve", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(Instance.EmulatorId, "refix", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(Instance.EmulatorId, "gamefix_online", StringComparison.OrdinalIgnoreCase) ||
             (Instance.EmulatorId != null && Instance.EmulatorId.Contains("online", StringComparison.OrdinalIgnoreCase)) ||
             (Instance.InstalledEmulatorVersion != null && Instance.InstalledEmulatorVersion.Contains("online", StringComparison.OrdinalIgnoreCase)) ||
             (Instance.InstalledFixLayers != null && Instance.InstalledFixLayers.Count > 0));

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
        SteamLaunchStatusText = GetResourceString("String_SteamStartingAndWaiting", "Iniciando Steam y esperando a que cargue por completo...");
        StatusMessage = "⏳ Iniciando Steam y esperando a que cargue por completo...";

        bool steamLoaded = false;
        try
        {
            if (_steamStatusService != null)
            {
                var progress = new Progress<string>(msg =>
                {
                    SteamLaunchStatusText = msg;
                    StatusMessage = $"⏳ {msg}";
                });

                steamLoaded = await _steamStatusService.LaunchAndWaitForSteamFullyLoadedAsync(
                    TimeSpan.FromSeconds(60),
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

                await Task.Delay(4000).ConfigureAwait(true);
                steamLoaded = true;
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

        if (steamLoaded)
        {
            await LaunchGameInternalAsync().ConfigureAwait(true);
        }
        else
        {
            StatusMessage = "❌ Steam no terminó de cargar a tiempo. Ejecución cancelada.";
            _notificationService?.ShowWarning(
                GetResourceString("String_SteamRequiredModalTitle", "Steam Necesario"),
                "Steam no completó su carga o inicio de sesión a tiempo. Asegúrate de que Steam esté abierto y vuelve a intentar.");
        }
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
                if (IsEmulatorInstalled)
                {
                    InstalledEmulatorMode = ReFixEmulator.GetInstalledMode(Instance.InstallPath)
                        ?? (Instance.EmulatorId?.Contains("goldberg", StringComparison.OrdinalIgnoreCase) == true ? "Re:Goldberg LAN" : "ReFix Online (Steam)");
                }
                else
                {
                    InstalledEmulatorMode = null;
                }
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

            // 1. Search by exact AppId if available; if not available, fallback to searching by clean name once
            if (Instance.AppId > 0)
            {
                fixes = await _apiClient.GetGameFixesAsync(query: Instance.AppId.ToString(), ct: CancellationToken.None).ConfigureAwait(true);
            }
            else if (!string.IsNullOrWhiteSpace(Instance.Name))
            {
                var cleanName = CleanName(Instance.Name) ?? Instance.Name;
                fixes = await _apiClient.GetGameFixesAsync(query: cleanName, ct: CancellationToken.None).ConfigureAwait(true);
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
    public async Task UninstallFromFeedbackAsync()
    {
        await ConfirmUninstallAndTryAnotherAsync().ConfigureAwait(true);
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
            LaunchArguments = CustomLaunchArgs,
            DisableUpdateChecks = DisableUpdateChecks
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

    private static string FormatFileSize(long bytes) => bytes switch
    {
        > 1024 * 1024 * 1024 => $"{bytes / (1024.0 * 1024.0 * 1024.0):F2} GB",
        > 1024 * 1024 => $"{bytes / (1024.0 * 1024.0):F1} MB",
        > 0 => $"{bytes / 1024.0:F0} KB",
        _ => "0 KB"
    };

    private void RecalculateSelectedSize()
    {
        long depotSum = Depots.Where(d => d.IsSelected).Sum(d => d.Depot.SizeBytes);

        TotalSelectedSizeBytes = depotSum;
        TotalSelectedSizeFormatted = FormatFileSize(depotSum);

        OnPropertyChanged(nameof(SelectedDepotsCount));
        OnPropertyChanged(nameof(SelectedDepotsSize));
        OnPropertyChanged(nameof(SelectedDlcsCount));
        OnPropertyChanged(nameof(SelectedDlcsSize));
        OnPropertyChanged(nameof(HasDlcsWithDepotsToDownload));
        OnPropertyChanged(nameof(CanDownloadAndInstallDlcs));
        OnPropertyChanged(nameof(DlcActionButtonText));
        OnPropertyChanged(nameof(AreAllSelectedDepotsDownloaded));
        OnPropertyChanged(nameof(DownloadButtonText));

        NotifyDownloadProps();
    }


    [RelayCommand]
    public void SelectAllDepots()
    {
        foreach (var depot in Depots) depot.IsSelected = true;
        RecalculateSelectedSize();
    }

    [RelayCommand]
    public void DeselectAllDepots()
    {
        foreach (var depot in Depots) depot.IsSelected = false;
        RecalculateSelectedSize();
    }

    [RelayCommand]
    public void SelectAllDlcs()
    {
        foreach (var dlc in Dlcs) dlc.IsSelected = true;
        RecalculateSelectedSize();
    }

    [RelayCommand]
    public void DeselectAllDlcs()
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
            StatusMessage = "⚠ Please select at least one depot to download.";
            _notificationService?.ShowWarning("No Depots Selected", "Please select at least one depot to download.");
            return;
        }

        // Ensure missing manifests and keys are acquired via ManifestRegistry across providers.
        // Both of these used to swallow every failure, so a game for which NOTHING could be
        // resolved (no manifest from any provider, no depot key) was still handed to the download
        // queue, which then "completed" it in a couple of seconds without downloading a byte.
        var depotsMissingManifest = new List<uint>();

        if (_manifestRegistry != null && Instance.AppId > 0)
        {
            for (int i = 0; i < combinedDepots.Count; i++)
            {
                var d = combinedDepots[i];
                var manifestFile = Path.Combine(instanceManifestDir, $"{d.DepotId}_{d.ManifestId}.manifest");
                if (!File.Exists(manifestFile) && d.ManifestId > 0)
                {
                    try
                    {
                        var acquired = await _manifestRegistry.AcquireManifestAsync(d.DepotId, d.ManifestId, Instance.AppId).ConfigureAwait(true);
                        if (!string.IsNullOrWhiteSpace(acquired) && File.Exists(acquired))
                        {
                            File.Copy(acquired, manifestFile, overwrite: true);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Could not acquire manifest {DepotId}_{ManifestId} for {Game}", d.DepotId, d.ManifestId, Instance.Name);
                    }
                }

                if (string.IsNullOrWhiteSpace(d.DepotKey) && _depotKeyRepository != null)
                {
                    try
                    {
                        var key = await _depotKeyRepository.GetKeyAsync(d.DepotId).ConfigureAwait(true);
                        if (!string.IsNullOrWhiteSpace(key))
                        {
                            combinedDepots[i] = d with { DepotKey = key };
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Could not resolve depot key for depot {DepotId}", d.DepotId);
                    }
                }
            }
        }

        // A depot is downloadable if we hold its manifest locally, or if we hold its decryption
        // key (in which case DepotDownloader can still pull the manifest from the Steam CDN).
        // With neither, the download is guaranteed to produce nothing.
        var usableDepots = 0;
        foreach (var d in combinedDepots)
        {
            var manifestFile = Path.Combine(instanceManifestDir, $"{d.DepotId}_{d.ManifestId}.manifest");
            var hasManifest = File.Exists(manifestFile) && new FileInfo(manifestFile).Length > 32;
            var hasKey = !string.IsNullOrWhiteSpace(d.DepotKey);

            if (hasManifest || hasKey) usableDepots++;
            else depotsMissingManifest.Add(d.DepotId);
        }

        if (usableDepots == 0)
        {
            var depotList = string.Join(", ", depotsMissingManifest.Take(6));
            var detail =
                $"No manifest or depot key could be found for {Instance.Name} (AppId {Instance.AppId}). " +
                $"None of the configured providers (local cache, ManifestHub, DepotBox) has data for depot(s) {depotList}. " +
                "Starting the download would install nothing.";

            _logger.LogError("Refusing to queue {Game} (AppId {AppId}): no usable depots. Missing: {Depots}",
                Instance.Name, Instance.AppId, depotList);

            StatusMessage = "⚠ No depots available for this game.";
            _notificationService?.ShowError("Cannot Download", detail);
            return;
        }

        if (depotsMissingManifest.Count > 0)
        {
            _logger.LogWarning("{Count} depot(s) of {Game} have neither a manifest nor a key and will be skipped: {Depots}",
                depotsMissingManifest.Count, Instance.Name, string.Join(", ", depotsMissingManifest));
            _notificationService?.ShowWarning(
                "Some Depots Unavailable",
                $"{depotsMissingManifest.Count} depot(s) of {Instance.Name} have no manifest or key available and will be skipped.");
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
    public async Task DownloadAndInstallDlcsAsync()
    {
        if (Instance is null) return;

        var selectedDlcs = Dlcs.Where(d => d.IsSelected).ToList();
        if (selectedDlcs.Count == 0)
        {
            if (Dlcs.Count == 0 && !IsDlcUnlocked)
            {
                IsDlcWarningModalOpen = true;
                return;
            }
            StatusMessage = "⚠ Please select at least one DLC.";
            _notificationService?.ShowWarning("No DLCs Selected", "Please select at least one DLC to download or install.");
            return;
        }

        IsProcessing = true;
        StatusMessage = "⏳ Processing DLCs...";

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
            // 1. Download missing DLC depot files if any
            var dlcDepotsToDownload = selectedDlcs
                .SelectMany(d => d.Dlc.Depots)
                .Where(dep => !dep.IsDownloaded && dep.SizeBytes > 0)
                .DistinctBy(dep => dep.DepotId)
                .ToList();

            if (dlcDepotsToDownload.Count > 0)
            {
                StatusMessage = $"⏳ Downloading content for {dlcDepotsToDownload.Count} DLC depot(s)...";

                var instanceManifestDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "BlueStar", "instances", Instance.Id.ToString(), "manifests");
                Directory.CreateDirectory(instanceManifestDir);

                if (_manifestRegistry != null && Instance.AppId > 0)
                {
                    for (int i = 0; i < dlcDepotsToDownload.Count; i++)
                    {
                        var d = dlcDepotsToDownload[i];
                        var manifestFile = Path.Combine(instanceManifestDir, $"{d.DepotId}_{d.ManifestId}.manifest");
                        if (!File.Exists(manifestFile) && d.ManifestId > 0)
                        {
                            try
                            {
                                var acquired = await _manifestRegistry.AcquireManifestAsync(d.DepotId, d.ManifestId, Instance.AppId).ConfigureAwait(true);
                                if (!string.IsNullOrWhiteSpace(acquired) && File.Exists(acquired))
                                {
                                    File.Copy(acquired, manifestFile, overwrite: true);
                                }
                            }
                            catch { }
                        }

                        if (string.IsNullOrWhiteSpace(d.DepotKey) && _depotKeyRepository != null)
                        {
                            try
                            {
                                var key = await _depotKeyRepository.GetKeyAsync(d.DepotId).ConfigureAwait(true);
                                if (!string.IsNullOrWhiteSpace(key))
                                {
                                    dlcDepotsToDownload[i] = d with { DepotKey = key };
                                }
                            }
                            catch { }
                        }
                    }
                }

                var downloadInstance = Instance with { Depots = dlcDepotsToDownload.AsReadOnly() };
                _ = _downloadQueueManager.StartDownloadAsync(downloadInstance);
                ActiveJob = _downloadQueueManager.Queue.FirstOrDefault(j => j.Instance.Id == Instance.Id);
                NotifyDownloadProps();
            }

            // 2. Install / Configure DLC unlocker for selected DLCs (SmokeAPI / CreamAPI / emulator config)
            var selectedIds = selectedDlcs.Select(d => d.Dlc.AppId).ToList();
            Instance = Instance with { UnlockedDlcIds = selectedIds };
            await _instanceManager.UpdateAsync(Instance, CancellationToken.None).ConfigureAwait(true);

            var targetDlc = selectedDlcs[0].Dlc;
            StatusMessage = "⏳ Configuring DLC unlocker & emulator integration...";
            var success = await _dlcInstaller
                .InstallDlcAsync(Instance, targetDlc, CancellationToken.None, progress)
                .ConfigureAwait(true);

            IsDlcUnlocked = success;
            if (success)
            {
                Instance = Instance with { DlcUnlockerInstalled = true, UnlockedDlcIds = selectedIds };
                await _instanceManager.UpdateAsync(Instance, CancellationToken.None).ConfigureAwait(true);

                foreach (var item in Dlcs)
                {
                    item.IsUnlocked = selectedIds.Contains(item.Dlc.AppId);
                }

                string successMsg = dlcDepotsToDownload.Count > 0
                    ? $"DLCs queued for download and unlocker configured successfully for {Instance.Name}."
                    : $"DLC unlocker configured successfully for {selectedDlcs.Count} DLC(s) on {Instance.Name}.";

                _notificationService?.ShowSuccess("DLCs Configured", successMsg);
                StatusMessage = $"✅ {successMsg}";
            }
            else
            {
                StatusMessage = "❌ Failed to configure DLC unlocker.";
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to download and install DLCs");
            StatusMessage = $"❌ Error: {ex.Message}";
        }
        finally
        {
            IsProcessing = false;
        }
    }

    [RelayCommand]
    public async Task UninstallDlcUnlockerAsync()
    {
        if (Instance == null || !IsDlcUnlocked) return;

        IsProcessing = true;
        StatusMessage = "⏳ Removing DLC unlocker...";

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

            var success = await _dlcInstaller
                .UninstallDlcAsync(Instance, targetDlc, CancellationToken.None, progress)
                .ConfigureAwait(true);

            IsDlcUnlocked = !success;
            if (success)
            {
                Instance = Instance with { DlcUnlockerInstalled = false, UnlockedDlcIds = [] };
                await _instanceManager.UpdateAsync(Instance, CancellationToken.None).ConfigureAwait(true);

                foreach (var item in Dlcs)
                {
                    item.IsUnlocked = false;
                }

                _notificationService?.ShowInfo("DLC Unlocker Uninstalled", $"DLC wrapper was successfully removed for {Instance.Name}.");
                StatusMessage = "✅ DLC unlocker uninstalled successfully.";
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to uninstall DLC unlocker");
            StatusMessage = $"❌ Error: {ex.Message}";
        }
        finally
        {
            IsProcessing = false;
        }
    }

    [RelayCommand]
    public async Task UnlockDlcsAsync()
    {
        await DownloadAndInstallDlcsAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    public async Task ConfirmUnlockDlcsAsync()
    {
        IsDlcWarningModalOpen = false;
        await DownloadAndInstallDlcsAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    public void CancelUnlockDlcs()
    {
        IsDlcWarningModalOpen = false;
        StatusMessage = "ℹ DLC Unlocker installation cancelled.";
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
            var cleanGameName = CleanName(Instance.Name) ?? Instance.Name;
            var selectedRoot = dialog.FolderName;

            // The picker returns the *parent* folder the user chose. Never install into its root:
            // always give the game its own subfolder, exactly like the initial install flow does.
            // EnsureGameSubfolder is a no-op when the user already picked the game's own folder.
            var targetPath = PathHelper.EnsureGameSubfolder(selectedRoot, cleanGameName);

            // Avoid silently sharing a folder with another instance.
            try
            {
                var allInstances = await _instanceManager.GetAllAsync(CancellationToken.None).ConfigureAwait(true);
                var clash = allInstances.FirstOrDefault(i =>
                    i.Id != Instance.Id &&
                    !string.IsNullOrWhiteSpace(i.InstallPath) &&
                    string.Equals(
                        i.InstallPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                        targetPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                        StringComparison.OrdinalIgnoreCase));

                if (clash != null)
                {
                    StatusMessage = $"⚠ '{clash.Name}' already uses that folder. Pick a different location.";
                    _notificationService?.ShowWarning("Folder In Use", $"'{clash.Name}' is already installed in:\n{targetPath}");
                    return;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not verify install path collisions for {Name}", Instance.Name);
            }

            Directory.CreateDirectory(targetPath);

            var exes = ShortcutHelper.FindGameExecutables(targetPath, cleanGameName);

            // Re-point the executable at the NEW directory. Keeping the previous value only makes
            // sense when it already lives under the new path — otherwise the launcher would keep
            // starting the game from the old folder after the move.
            string? exe = null;
            var previousExe = Instance.ExecutablePath;
            if (!string.IsNullOrWhiteSpace(previousExe))
            {
                if (IsPathInside(previousExe, targetPath) && File.Exists(previousExe))
                {
                    exe = previousExe;
                }
                else
                {
                    // Try to keep the same executable, relative to the new root.
                    var oldRoot = Instance.InstallPath;
                    if (!string.IsNullOrWhiteSpace(oldRoot) && IsPathInside(previousExe, oldRoot))
                    {
                        var relative = Path.GetRelativePath(oldRoot, previousExe);
                        var rebased = Path.GetFullPath(Path.Combine(targetPath, relative));
                        if (File.Exists(rebased)) exe = rebased;
                    }

                    // Fall back to matching just the file name anywhere under the new root.
                    if (exe == null)
                    {
                        var exeName = Path.GetFileName(previousExe);
                        exe = exes.FirstOrDefault(candidate =>
                            string.Equals(Path.GetFileName(candidate), exeName, StringComparison.OrdinalIgnoreCase));
                    }
                }
            }

            exe ??= _engineDetector.FindPrimaryExecutable(targetPath, cleanGameName);
            exe ??= exes.FirstOrDefault();

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

            // Persist the PARENT folder as the "last used" root, so the next game does not get
            // nested inside this game's folder.
            await _appSettings.SetLastInstallDirectoryAsync(selectedRoot).ConfigureAwait(true);

            // Keep the Settings tab textbox in sync with the re-resolved executable.
            ConfiguredExecutablePath = exe ?? string.Empty;

            StatusMessage = string.IsNullOrWhiteSpace(exe)
                ? $"✅ Installation directory updated: {targetPath} — no executable found yet, set it in Settings."
                : $"✅ Installation directory updated: {targetPath}";

            await LoadInstanceAsync(Instance).ConfigureAwait(true);
        }
    }

    /// <summary>
    /// Returns true when <paramref name="candidate"/> resolves to a location inside <paramref name="root"/>.
    /// </summary>
    private static bool IsPathInside(string? candidate, string? root)
    {
        if (string.IsNullOrWhiteSpace(candidate) || string.IsNullOrWhiteSpace(root)) return false;
        try
        {
            var fullRoot = Path.GetFullPath(root)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var fullCandidate = Path.GetFullPath(candidate);
            return fullCandidate.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
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

    [RelayCommand]
    public void OpenTagInExplore(string? tagName)
    {
        if (string.IsNullOrWhiteSpace(tagName)) return;
        OnOpenTagRequested?.Invoke(tagName);
    }

    [RelayCommand]
    public void FindSimilarGames()
    {
        if (Instance == null || Instance.AppId == 0) return;

        var tagList = new List<string>();
        if (CommunityTags != null && CommunityTags.Count > 0)
            tagList.AddRange(CommunityTags);
        else if (Instance.Metadata?.StoreTags != null && Instance.Metadata.StoreTags.Count > 0)
            tagList.AddRange(Instance.Metadata.StoreTags);

        if (GenreTags != null && GenreTags.Count > 0)
            tagList.AddRange(GenreTags.Where(g => !tagList.Contains(g, StringComparer.OrdinalIgnoreCase)));
        else if (Instance.Metadata?.Genres != null)
            tagList.AddRange(Instance.Metadata.Genres.Where(g => !tagList.Contains(g, StringComparer.OrdinalIgnoreCase)));

        if (FeatureTags != null && FeatureTags.Count > 0)
            tagList.AddRange(FeatureTags.Where(f => !tagList.Contains(f, StringComparer.OrdinalIgnoreCase)));
        else if (Instance.Metadata?.Categories != null)
            tagList.AddRange(Instance.Metadata.Categories.Where(f => !tagList.Contains(f, StringComparer.OrdinalIgnoreCase)));

        var tags = tagList.Select(t => new StoreTagRef(t, false)).ToList();

        var target = new SearchResult
        {
            AppId = Instance.AppId,
            Name = Instance.Name,
            HeaderImageUrl = Instance.HeaderImageUrl,
            StoreTags = tags
        };

        OnFindSimilarRequested?.Invoke(target);
    }

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

            // 5. Remove shortcuts from Desktop and Start Menu
            ShortcutHelper.RemoveGameShortcuts(Instance.Name, Instance.ExecutablePath, Instance.InstallPath);

            // 6. Reset downloaded status on instance depots and DLCs while preserving instance metadata
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

    private IReadOnlyList<ManifestArtifact>? _discoveredManifestArtifacts;

    private async Task<(bool IsCached, string ProviderName, bool IsAvailable)> CheckDepotManifestAvailabilityAsync(uint depotId, ulong manifestId)
    {
        if (manifestId == 0) return (false, string.Empty, false);

        // 1. Check persistent global cache
        if (_manifestCacheService?.HasManifest(depotId, manifestId) == true)
        {
            return (true, "Local Cache", true);
        }

        // Check instance manifests directory
        if (Instance != null)
        {
            var instanceManifestDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "BlueStar", "instances", Instance.Id.ToString(), "manifests");
            if (File.Exists(Path.Combine(instanceManifestDir, $"{depotId}_{manifestId}.manifest")))
            {
                return (true, "Local Cache", true);
            }
        }

        // 2. Check cached discovered artifacts
        if (_discoveredManifestArtifacts != null && _discoveredManifestArtifacts.Count > 0)
        {
            var match = _discoveredManifestArtifacts.FirstOrDefault(a => a.DepotId == depotId && a.ManifestId == manifestId);
            if (match != null)
            {
                var prov = match.Routes.FirstOrDefault()?.ProviderId ?? "Provider";
                return (false, prov, true);
            }
        }

        // 3. If not yet discovered, run discovery once across providers
        if (_manifestRegistry != null && Instance != null && Instance.AppId > 0 && (_discoveredManifestArtifacts == null || _discoveredManifestArtifacts.Count == 0))
        {
            try
            {
                _discoveredManifestArtifacts = await _manifestRegistry.DiscoverManifestsAsync(Instance.AppId, CancellationToken.None).ConfigureAwait(false);
                var match = _discoveredManifestArtifacts.FirstOrDefault(a => a.DepotId == depotId && a.ManifestId == manifestId);
                if (match != null)
                {
                    var prov = match.Routes.FirstOrDefault()?.ProviderId ?? "Provider";
                    return (false, prov, true);
                }
            }
            catch { }
        }

        return (false, string.Empty, false);
    }

    private async Task PopulateDepotsKnownManifestVersionsAsync()
    {
        if (Instance == null || Instance.AppId == 0) return;

        try
        {
            // 1. Get available versions/builds from IBuildResolver (SteamCMD / SteamDB / Curated / Local)
            IReadOnlyList<GameVersion> versions = [];
            if (_buildResolver != null)
            {
                try
                {
                    versions = await _buildResolver.GetAvailableVersionsAsync(Instance.AppId, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to get available versions for populating depot versions");
                }
            }

            // 2. Discover manifests across registered providers
            if (_manifestRegistry != null && (_discoveredManifestArtifacts == null || _discoveredManifestArtifacts.Count == 0))
            {
                try
                {
                    _discoveredManifestArtifacts = await _manifestRegistry.DiscoverManifestsAsync(Instance.AppId, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to discover manifests across providers for populating depot versions");
                }
            }

            // 3. Ask DepotBox for every manifest it knows about for this app. These are listed even
            //    when nothing has been downloaded yet — the point of the Custom mode is to let the
            //    user pick a manifest first and then go acquire it.
            IReadOnlyList<ManifestInfo> depotBoxManifests = [];
            if (_apiClient != null)
            {
                try
                {
                    depotBoxManifests = await _apiClient.GetManifestsAsync(Instance.AppId, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "DepotBox has no manifest list for AppId {AppId}", Instance.AppId);
                }
            }

            // 4. Scan local instance manifests directory
            var localManifestIds = new Dictionary<uint, HashSet<ulong>>();
            var instanceManifestDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "BlueStar", "instances", Instance.Id.ToString(), "manifests");
            if (Directory.Exists(instanceManifestDir))
            {
                foreach (var f in Directory.GetFiles(instanceManifestDir, "*.manifest"))
                {
                    var name = Path.GetFileNameWithoutExtension(f);
                    var parts = name.Split('_');
                    if (parts.Length == 2 && uint.TryParse(parts[0], out var dId) && ulong.TryParse(parts[1], out var mId))
                    {
                        if (!localManifestIds.TryGetValue(dId, out var set))
                        {
                            set = [];
                            localManifestIds[dId] = set;
                        }
                        set.Add(mId);
                    }
                }
            }

            // 5. Update each SelectableDepotItem on UI thread
            _uiContext.Post(_ =>
            {
                foreach (var item in Depots)
                {
                    var depotId = item.Depot.DepotId;
                    var options = new List<DepotManifestOption>();
                    var seen = new HashSet<ulong>();

                    // Is this manifest sitting in a cache we can read right now?
                    bool IsLocallyAvailable(ulong mId) =>
                        (_manifestCacheService?.HasManifest(depotId, mId) == true) ||
                        (localManifestIds.TryGetValue(depotId, out var localHits) && localHits.Contains(mId));

                    // Current manifest
                    if (item.Depot.ManifestId > 0)
                    {
                        seen.Add(item.Depot.ManifestId);
                        bool isCached = IsLocallyAvailable(item.Depot.ManifestId);
                        options.Add(new DepotManifestOption
                        {
                            ManifestId = item.Depot.ManifestId,
                            DisplayText = $"Current build{(isCached ? " · in local cache" : " · from Steam")}",
                            Source = isCached ? "Local cache" : "Steam",
                            IsBacked = true
                        });
                    }

                    // Versions from IBuildResolver (SteamCMD branches & builds)
                    foreach (var v in versions)
                    {
                        var match = v.Depots.FirstOrDefault(d => d.DepotId == depotId);
                        if (match != null && match.ManifestId > 0 && seen.Add(match.ManifestId))
                        {
                            var branchLabel = !string.IsNullOrWhiteSpace(v.BranchName) ? v.BranchName : "build";
                            var dateLabel = v.UpdatedAt.HasValue ? $" · {v.UpdatedAt.Value:d MMM yyyy}" : string.Empty;
                            options.Add(new DepotManifestOption
                            {
                                ManifestId = match.ManifestId,
                                DisplayText = $"{branchLabel} · {v.DisplayName}{dateLabel}",
                                Source = v.Source,
                                BranchName = v.BranchName,
                                BuildId = v.BuildId,
                                IsBacked = IsLocallyAvailable(match.ManifestId)
                            });
                        }
                    }

                    // Artifacts discovered across providers
                    if (_discoveredManifestArtifacts != null)
                    {
                        foreach (var art in _discoveredManifestArtifacts.Where(a => a.DepotId == depotId))
                        {
                            if (seen.Add(art.ManifestId))
                            {
                                var providerLabel = string.Join(", ", art.Routes.Select(r => r.ProviderId));
                                options.Add(new DepotManifestOption
                                {
                                    ManifestId = art.ManifestId,
                                    DisplayText = string.IsNullOrEmpty(providerLabel) ? "Provider" : providerLabel,
                                    Source = string.IsNullOrEmpty(providerLabel) ? "Online" : providerLabel,
                                    IsBacked = art.Routes.Count > 0 || IsLocallyAvailable(art.ManifestId)
                                });
                            }
                        }
                    }

                    // Everything DepotBox lists for this depot, backed or not
                    foreach (var mi in depotBoxManifests.Where(m => m.DepotId == depotId))
                    {
                        if (mi.ManifestId > 0 && seen.Add(mi.ManifestId))
                        {
                            options.Add(new DepotManifestOption
                            {
                                ManifestId = mi.ManifestId,
                                DisplayText = "DepotBox catalogue",
                                Source = "DepotBox",
                                IsBacked = mi.IsDownloaded || IsLocallyAvailable(mi.ManifestId)
                            });
                        }
                    }

                    // Builds this instance previously had installed
                    foreach (var snap in Instance.BuildHistory ?? [])
                    {
                        if (snap.ManifestMap.TryGetValue(depotId, out var histId) && histId > 0 && seen.Add(histId))
                        {
                            options.Add(new DepotManifestOption
                            {
                                ManifestId = histId,
                                DisplayText = $"previously installed · {snap.FormattedDate}",
                                Source = "Build history",
                                BuildId = snap.BuildId,
                                IsBacked = IsLocallyAvailable(histId)
                            });
                        }
                    }

                    // Builds shown in the build picker (includes user-imported and custom ones)
                    foreach (var b in AvailableBuilds)
                    {
                        if (b.DepotManifests.TryGetValue(depotId, out var bId) && bId > 0 && seen.Add(bId))
                        {
                            options.Add(new DepotManifestOption
                            {
                                ManifestId = bId,
                                DisplayText = $"{b.DisplayName} · {b.Source}",
                                Source = b.Source,
                                BranchName = b.BranchName,
                                BuildId = b.BuildId,
                                IsBacked = IsLocallyAvailable(bId)
                            });
                        }
                    }

                    // Local manifest files
                    if (localManifestIds.TryGetValue(depotId, out var localSet))
                    {
                        foreach (var mId in localSet)
                        {
                            if (seen.Add(mId))
                            {
                                options.Add(new DepotManifestOption
                                {
                                    ManifestId = mId,
                                    DisplayText = "local archive",
                                    Source = "Local cache",
                                    IsBacked = true
                                });
                            }
                        }
                    }

                    item.KnownManifestVersions = new ObservableCollection<DepotManifestOption>(options);
                    item.SelectedManifestOption = options.FirstOrDefault(o => o.ManifestId == item.Depot.ManifestId) ?? options.FirstOrDefault();
                    if (item.SelectedManifestOption != null &&
                        !TryParseManifestId(item.ManifestInputText, out ulong _existingManifestId))
                    {
                        item.ManifestInputText = item.SelectedManifestOption.ManifestId.ToString();
                    }

                    _ = item.CheckAvailabilityForIdAsync(item.Depot.ManifestId);
                }
            }, null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to populate known manifest versions for instance depots");
        }
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

    /// <summary>
    /// Stores the custom manifest configuration as a selectable build WITHOUT downloading it.
    /// Reads the same depot rows the Version tab's Custom mode edits.
    /// </summary>
    [RelayCommand]
    public void SaveCustomBuild()
    {
        if (Instance == null) return;

        var map = new Dictionary<uint, ulong>();
        foreach (var item in Depots)
        {
            if (TryParseManifestId(item.ManifestInputText, out var mid))
            {
                map[item.Depot.DepotId] = mid;
            }
            else if (item.Depot.ManifestId > 0)
            {
                map[item.Depot.DepotId] = item.Depot.ManifestId;
            }
        }

        if (map.Count == 0)
        {
            StatusMessage = GetString("String_VersionCustomNoDepots", "This instance has no depots to configure.");
            return;
        }

        var buildId = !string.IsNullOrWhiteSpace(CustomVersionBuildId) ? CustomVersionBuildId.Trim()
                    : !string.IsNullOrWhiteSpace(CustomBuildIdInput) ? CustomBuildIdInput.Trim()
                    : "Custom";

        var displayName = !string.IsNullOrWhiteSpace(CustomVersionBuildName) ? CustomVersionBuildName.Trim()
                        : !string.IsNullOrWhiteSpace(CustomBuildNameInput) ? CustomBuildNameInput.Trim()
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

        // Keep it as a reusable preset on the instance, so the same manifest set can be recalled
        // later from the chips row instead of being retyped.
        var preset = new InstalledBuildSnapshot
        {
            BuildId = buildId,
            BranchName = "custom",
            DisplayName = displayName,
            ManifestMap = map,
            InstalledAt = DateTimeOffset.UtcNow,
            SizeBytes = Depots
                .Where(d => map.ContainsKey(d.Depot.DepotId))
                .Sum(d => d.Depot.SizeBytes)
        };

        var presets = new List<InstalledBuildSnapshot> { preset };
        foreach (var existing in Instance.SavedCustomBuilds ?? [])
        {
            // Saving under the same build id replaces the old preset rather than piling up.
            if (string.Equals(existing.BuildId, preset.BuildId, StringComparison.OrdinalIgnoreCase)) continue;
            presets.Add(existing);
            if (presets.Count >= MaxSavedCustomBuilds) break;
        }

        Instance = Instance with { SavedCustomBuilds = presets.AsReadOnly() };
        _ = _instanceManager.UpdateAsync(Instance, CancellationToken.None);
        RefreshSavedCustomBuilds();

        StatusMessage = string.Format(
            GetString("String_VersionCustomSavedFormat", "✅ Saved custom build “{0}”. Recall it any time from the chips above."),
            displayName);
    }

    /// <summary>Upper bound on how many custom presets an instance keeps.</summary>
    public const int MaxSavedCustomBuilds = 12;

    [ObservableProperty]
    private ObservableCollection<InstalledBuildSnapshot> _savedCustomBuilds = [];

    public bool HasSavedCustomBuilds => SavedCustomBuilds.Count > 0;

    /// <summary>Reloads the preset chips from the instance.</summary>
    public void RefreshSavedCustomBuilds()
    {
        SavedCustomBuilds = new ObservableCollection<InstalledBuildSnapshot>(Instance?.SavedCustomBuilds ?? []);
        OnPropertyChanged(nameof(HasSavedCustomBuilds));
    }

    /// <summary>Loads a saved preset's manifest ids back into the Custom mode table.</summary>
    [RelayCommand]
    public void ApplyCustomBuildPreset(InstalledBuildSnapshot? preset)
    {
        if (preset == null || Instance == null) return;

        CustomVersionBuildId = preset.BuildId;
        CustomVersionBuildName = preset.DisplayName ?? string.Empty;

        int applied = 0;
        foreach (var item in Depots)
        {
            if (preset.ManifestMap.TryGetValue(item.Depot.DepotId, out var mid) && mid > 0)
            {
                item.ManifestInputText = mid.ToString();
                item.SelectedManifestOption =
                    item.KnownManifestVersions.FirstOrDefault(o => o.ManifestId == mid);
                applied++;
            }
        }

        VersionMode = VersionModeCustom;
        RecomputeCustomBuildValidation();

        StatusMessage = string.Format(
            GetString("String_VersionCustomPresetAppliedFormat", "📋 Loaded “{0}” — {1} depot(s) filled in."),
            preset.DisplayName ?? preset.BuildId, applied);
    }

    /// <summary>Removes a saved preset.</summary>
    [RelayCommand]
    public async Task DeleteCustomBuildPresetAsync(InstalledBuildSnapshot? preset)
    {
        if (preset == null || Instance == null) return;

        var remaining = (Instance.SavedCustomBuilds ?? [])
            .Where(b => !string.Equals(b.BuildId, preset.BuildId, StringComparison.OrdinalIgnoreCase))
            .ToList();

        Instance = Instance with { SavedCustomBuilds = remaining.AsReadOnly() };
        await _instanceManager.UpdateAsync(Instance, CancellationToken.None).ConfigureAwait(true);
        RefreshSavedCustomBuilds();

        StatusMessage = string.Format(
            GetString("String_VersionCustomPresetDeletedFormat", "🗑 Removed the saved build “{0}”."),
            preset.DisplayName ?? preset.BuildId);
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

            // 1. Fetch available versions via IBuildResolver if available
            if (_buildResolver != null)
            {
                try
                {
                    var resolvedVersions = await _buildResolver.GetAvailableVersionsAsync(Instance.AppId, _cts.Token).ConfigureAwait(true);
                    foreach (var v in resolvedVersions)
                    {
                        var map = v.Depots.ToDictionary(d => d.DepotId, d => d.ManifestId);
                        list.Add(new GameBuildInfo
                        {
                            BuildId = v.BuildId,
                            BranchName = v.BranchName,
                            DisplayName = v.DisplayName,
                            UpdatedAt = v.UpdatedAt,
                            Description = v.Description,
                            Source = v.Source,
                            DepotManifests = map
                        });
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to resolve versions from BuildResolver for {AppId}", Instance.AppId);
                }
            }
            else if (_metadataProvider is BlueStar.Infrastructure.Metadata.SteamStoreApiClient steamClient)
            {
                var steamBuilds = await steamClient.GetAppBuildsAsync(Instance.AppId, _cts.Token).ConfigureAwait(true);
                list.AddRange(steamBuilds);
            }


            // 2. Fetch DepotBox manifests build (only if no builds resolved yet and instance originated from DepotBox)
            if (_apiClient != null && list.Count == 0 && Instance.Origin == InstanceOrigin.DepotBox)
            {
                try
                {
                    var depotBoxManifests = await _apiClient.GetManifestsAsync(Instance.AppId, _cts.Token).ConfigureAwait(true);
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

        if (_languageChangedHandler != null && _localizationService != null)
        {
            _localizationService.LanguageChanged -= _languageChangedHandler;
        }

        if (ActiveJob != null)
        {
            ActiveJob.PropertyChanged -= OnActiveJobPropertyChanged;
        }
    }
}
