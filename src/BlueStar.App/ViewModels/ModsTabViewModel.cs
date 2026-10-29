using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BlueStar.Core.Interfaces;
using BlueStar.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BlueStar.App.ViewModels;

public sealed record ModpackEntry(string Id, string Name, string? Version, bool IsEnabled);

public sealed partial class Modpack : ObservableObject
{
    public required string Name { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public string? IconPath { get; set; }
    public List<ModpackEntry> Mods { get; init; } = [];
    public int ModsCount => Mods.Count;

    [JsonIgnore]
    public string ModsCountText
    {
        get
        {
            var template = System.Windows.Application.Current?.TryFindResource("String_ModpackModsInProfileFormat") as string ?? "{0} mods en perfil";
            return string.Format(template, ModsCount);
        }
    }

    [JsonIgnore]
    public string ModsCountListText
    {
        get
        {
            var template = System.Windows.Application.Current?.TryFindResource("String_ModpackModsCountListFormat") as string ?? "  ·  {0} mods";
            return string.Format(template, ModsCount);
        }
    }

    [ObservableProperty]
    [property: JsonIgnore]
    private bool _isActive;
}

/// <summary>
/// State and commands of an instance's Mods tab. Installs go through <see cref="IModInstallPipeline"/>, which
/// records every file in the mod ledger; mods already on disk that BlueStar did not install are still listed
/// and handled by the legacy per-engine managers.
/// </summary>
public sealed partial class ModsTabViewModel : ObservableObject
{
    private readonly IModInstallPipeline? _pipeline;
    private readonly IModManagerRegistry? _legacyRegistry;
    private readonly ILocalizationService? _localization;
    private readonly ILogger _logger;
    private readonly IWorkshopModFetcher? _workshopFetcher;
    private readonly IWorkshopService? _workshopService;
    private readonly IBepInExService? _bepInExService;
    private readonly IThunderstoreService? _thunderstoreService;
    private readonly ICurseForgeService? _curseForgeService;
    private readonly INexusModsService? _nexusModsService;
    private ModPackage? _pendingPackage;
    private Task? _checkProvidersTask;

    public ModsTabViewModel(
        IModInstallPipeline? pipeline,
        IModManagerRegistry? legacyRegistry,
        ILocalizationService? localization,
        ILogger logger,
        IWorkshopModFetcher? workshopFetcher = null,
        IWorkshopService? workshopService = null,
        IBepInExService? bepInExService = null,
        IThunderstoreService? thunderstoreService = null,
        ICurseForgeService? curseForgeService = null,
        INexusModsService? nexusModsService = null)
    {
        _pipeline = pipeline;
        _legacyRegistry = legacyRegistry;
        _localization = localization;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _workshopFetcher = workshopFetcher;
        _workshopService = workshopService;
        _bepInExService = bepInExService;
        _thunderstoreService = thunderstoreService;
        _curseForgeService = curseForgeService;
        _nexusModsService = nexusModsService;

        var workshop = new WorkshopBrowserViewModel(InstallWorkshopItemAsync);
        ThunderstoreBrowserViewModel? thunderstore = null;
        if (_thunderstoreService != null)
        {
            thunderstore = new ThunderstoreBrowserViewModel(
                _thunderstoreService,
                package => InstallPackageAsync(package),
                localization,
                logger);
            thunderstore.CheckInstalledStatus = CheckIfThunderstorePackageInstalled;
            thunderstore.OnModInstalledCallback = async () => await LoadAsync().ConfigureAwait(true);
        }

        CurseForgeBrowserViewModel? curseForge = null;
        if (_curseForgeService != null)
        {
            curseForge = new CurseForgeBrowserViewModel(
                _curseForgeService,
                package => InstallPackageAsync(package),
                localization,
                logger);
            curseForge.CheckInstalledStatus = CheckIfCurseForgeModInstalled;
            curseForge.OnModInstalledCallback = async () => await LoadAsync().ConfigureAwait(true);
        }

        NexusBrowserViewModel? nexus = null;
        if (_nexusModsService != null)
        {
            nexus = new NexusBrowserViewModel(
                _nexusModsService,
                package => InstallPackageAsync(package),
                localization,
                logger);
            nexus.OnModInstalledCallback = async () => await LoadAsync().ConfigureAwait(true);
        }

        ModBrowser = new ModBrowserViewModel(workshop, thunderstore, localization, logger, curseForge, nexus);
        ModBrowser.OpenModPageAction = (title, url) => OpenModPage(title, url);
    }

    /// <summary>Unified Mod Explorer modal coordinator.</summary>
    public ModBrowserViewModel ModBrowser { get; }

    /// <summary>Steam Workshop browser modal of this tab.</summary>
    public WorkshopBrowserViewModel Workshop => ModBrowser.Workshop;

    /// <summary>Thunderstore package browser modal of this tab.</summary>
    public ThunderstoreBrowserViewModel? Thunderstore => ModBrowser.Thunderstore;

    /// <summary>CurseForge package browser modal of this tab.</summary>
    public CurseForgeBrowserViewModel? CurseForge => ModBrowser.CurseForge;

    /// <summary>Nexus Mods browser modal of this tab.</summary>
    public NexusBrowserViewModel? Nexus => ModBrowser.Nexus;

    [RelayCommand]
    public void OpenWorkshop()
    {
        if (Instance == null || !HasWorkshopSupport) return;
        ModBrowser.Open(Instance, HasWorkshopSupport, HasThunderstoreSupport, ThunderstoreCommunity,
            HasCurseForgeSupport, CurseForgeGameId, HasNexusSupport, NexusGameDomain, preferredProvider: "Workshop");
    }

    [RelayCommand]
    public void OpenThunderstore()
    {
        if (Instance == null || !HasThunderstoreSupport || string.IsNullOrWhiteSpace(ThunderstoreCommunity)) return;
        ModBrowser.Open(Instance, HasWorkshopSupport, HasThunderstoreSupport, ThunderstoreCommunity,
            HasCurseForgeSupport, CurseForgeGameId, HasNexusSupport, NexusGameDomain, preferredProvider: "Thunderstore");
    }

    [RelayCommand]
    public void OpenCurseForge()
    {
        if (Instance == null || !HasCurseForgeSupport || !CurseForgeGameId.HasValue) return;
        ModBrowser.Open(Instance, HasWorkshopSupport, HasThunderstoreSupport, ThunderstoreCommunity,
            HasCurseForgeSupport, CurseForgeGameId, HasNexusSupport, NexusGameDomain, preferredProvider: "CurseForge");
    }

    [RelayCommand]
    public void OpenNexus()
    {
        if (Instance == null || !HasNexusSupport || string.IsNullOrWhiteSpace(NexusGameDomain)) return;
        ModBrowser.Open(Instance, HasWorkshopSupport, HasThunderstoreSupport, ThunderstoreCommunity,
            HasCurseForgeSupport, CurseForgeGameId, HasNexusSupport, NexusGameDomain, preferredProvider: "Nexus");
    }

    [RelayCommand]
    public async Task OpenContentBrowserAsync()
    {
        if (Instance == null) return;

        if (_checkProvidersTask != null && !_checkProvidersTask.IsCompleted)
        {
            try
            {
                await _checkProvidersTask.ConfigureAwait(true);
            }
            catch { }
        }
        else if (!HasAnyProvider)
        {
            await CheckProvidersAsync(Instance).ConfigureAwait(true);
        }

        if (!HasModBrowserSupport) return;

        ModBrowser.Open(Instance, HasWorkshopSupport, HasThunderstoreSupport, ThunderstoreCommunity,
            HasCurseForgeSupport, CurseForgeGameId, HasNexusSupport, NexusGameDomain);
    }

    public void OpenContentBrowser() => _ = OpenContentBrowserAsync();

    /// <summary>
    /// Downloads a Workshop item and installs it through the pipeline. Games whose Workshop content must be
    /// converted on the way in (Tabletop Simulator saves, Klei mods) still use the dedicated legacy deployer.
    /// </summary>
    public async Task<bool> InstallWorkshopItemAsync(ulong publishedFileId, IProgress<double> progress)
    {
        var instance = Instance;
        if (instance == null) return false;

        try
        {
            if (_workshopService != null && NeedsLegacyWorkshopDeployment(instance))
            {
                Workshop.ReportStatus(T("String_WorkshopDownloading", "⏳ Downloading from Steam Workshop..."), isError: false);
                var legacyOk = await _workshopService.DownloadAndInstallItemAsync(instance, publishedFileId, progress, CancellationToken.None)
                    .ConfigureAwait(true);
                Workshop.ReportStatus(legacyOk
                    ? T("String_WorkshopInstalledLegacy", "✅ Installed with this game's own Workshop layout.")
                    : T("String_WorkshopDownloadFailedGeneric", "❌ The item could not be downloaded."), isError: !legacyOk);
                await LoadAsync().ConfigureAwait(true);
                return legacyOk;
            }

            if (_workshopFetcher == null)
            {
                Workshop.ReportStatus(T("String_ModsPipelineUnavailable", "❌ The mod installer is not available."), isError: true);
                return false;
            }

            Workshop.ReportStatus(T("String_WorkshopDownloading", "⏳ Downloading from Steam Workshop..."), isError: false);
            var fetched = await _workshopFetcher.FetchAsync(instance.AppId, publishedFileId, progress, CancellationToken.None)
                .ConfigureAwait(true);
            if (!fetched.Succeeded)
            {
                var errorMsg = fetched.RequiresOwnedAccount
                    ? T("String_WorkshopRequiresOwnedAccount", "🔒 Steam only serves this item to accounts that own the game. An anonymous download is not permitted for this game.")
                    : string.Format(T("String_WorkshopDownloadFailed", "❌ Could not download the item: {0}"), fetched.Error);
                Workshop.ReportStatus(errorMsg, isError: true);
                return false;
            }

            var result = await InstallPackageAsync(fetched.Package!).ConfigureAwait(true);
            switch (result?.Status)
            {
                case ModInstallStatus.Installed:
                    Workshop.ReportStatus(string.Format(T("String_ModInstalled", "✅ {0} installed ({1} files)."),
                        fetched.Package!.Name, result.Record?.Files.Count ?? 0), isError: false);
                    return true;
                case ModInstallStatus.NeedsConfirmation:
                    // The confirmation dialog cannot sit on top of the embedded browser, so the browser makes way.
                    Workshop.IsOpen = false;
                    return false;
                default:
                    Workshop.ReportStatus(StatusMessage, isError: true);
                    return false;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to install Workshop item {Id}", publishedFileId);
            Workshop.ReportStatus(string.Format(T("String_WorkshopDownloadFailed", "❌ Could not download the item: {0}"), ex.Message), isError: true);
            return false;
        }
    }

    /// <summary>Tabletop Simulator and Klei games convert Workshop content into their own formats.</summary>
    private static bool NeedsLegacyWorkshopDeployment(GameInstance instance)
    {
        var category = BlueStar.Infrastructure.Mods.GameModPathResolver.ResolveModPaths(instance).GameCategory;
        return category is "TabletopSimulator" or "Klei";
    }

    /// <summary>Raised after the list changes, so the parent can refresh its overview summary.</summary>
    public event EventHandler? ModsChanged;

    /// <summary>Asks the user for a mod file; returns null when cancelled. Replaceable in tests.</summary>
    public Func<string?> PickModFile { get; set; } = DefaultPickModFile;

    public GameInstance? Instance { get; private set; }

    public ObservableCollection<ModListItem> Items { get; } = [];

    /// <summary>Filtered view of items shown to the user based on search query and category tab.</summary>
    public ObservableCollection<ModListItem> FilteredItems { get; } = [];

    /// <summary>Destinations of the plan waiting for confirmation, relative to the game folder.</summary>
    public ObservableCollection<string> PendingDestinations { get; } = [];

    [ObservableProperty]
    private string _modsDirectoryPath = string.Empty;

    [ObservableProperty]
    private string? _statusMessage;

    [ObservableProperty]
    private bool _isStatusError;

    [ObservableProperty]
    private bool _isDraggingFilesOver;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(InstallFromFileCommand))]
    [NotifyCanExecuteChangedFor(nameof(ConfirmPendingPlanCommand))]
    private bool _isBusy;

    [ObservableProperty]
    private bool _isConfirmPlanOpen;

    [ObservableProperty]
    private string _pendingModName = string.Empty;

    [ObservableProperty]
    private string? _pendingPlanReason;

    [ObservableProperty]
    private string _searchText = string.Empty;

    [RelayCommand]
    public void ClearSearch()
    {
        SearchText = string.Empty;
    }

    [ObservableProperty]
    private string _selectedFilter = "All";

    // ═══ SORTING CONTROLS (LIKE INSTANCES TAB) ═══
    [ObservableProperty]
    private string _selectedSort = "Name";

    [ObservableProperty]
    private bool _isSortDescending;

    [ObservableProperty]
    private bool _isSortDropdownOpen;

    public string SelectedSortDisplayName => SelectedSort switch
    {
        "Status" => T("String_SortByStatus", "Estado"),
        "Source" => T("String_SortBySource", "Origen"),
        "Size" => T("String_SortBySize", "Tamaño"),
        _ => T("String_SortByName", "Nombre")
    };

    public string DefaultModpackActiveCountText =>
        string.Format(T("String_ModpackActiveCountFormat", "{0} mods activos"), Items.Count);

    public string DefaultModpackInstalledCountText =>
        string.Format(T("String_ModpackInstalledCountFormat", "  ·  {0} mods instalados"), Items.Count);

    [RelayCommand]
    public void ToggleSortDirection()
    {
        IsSortDescending = !IsSortDescending;
        RefreshFilteredItems();
                OnPropertyChanged(nameof(WorkshopModsCount));
                OnPropertyChanged(nameof(BepInExModsCount));
                OnPropertyChanged(nameof(WorkshopModsStatsSummary));
                OnPropertyChanged(nameof(BepInExModsStatsSummary));
                OnPropertyChanged(nameof(WorkshopCardTitle));
                OnPropertyChanged(nameof(IsWorkshopCardVisible));
                OnPropertyChanged(nameof(IsBepInExCardVisible));
                OnPropertyChanged(nameof(HasAnyModLoaderCard));
                OnPropertyChanged(nameof(HasBothModLoaderCards));
                OnPropertyChanged(nameof(NativeModsCount));
                OnPropertyChanged(nameof(NativeModsStatsSummary));
                OnPropertyChanged(nameof(NativeModsBlockTitle));
    }

    [RelayCommand]
    public void ToggleSortDropdown() => IsSortDropdownOpen = !IsSortDropdownOpen;

    [RelayCommand]
    public void SelectSort(string? sort)
    {
        SelectedSort = string.IsNullOrWhiteSpace(sort) ? "Name" : sort;
        IsSortDropdownOpen = false;
        OnPropertyChanged(nameof(SelectedSortDisplayName));
        RefreshFilteredItems();
    }

    // ═══ MOD MULTI-SELECTION & BATCH ACTIONS (REQUIREMENT 10) ═══
    [ObservableProperty]
    private bool _isSelectionModeActive;

    [ObservableProperty]
    private int _selectedModsCount;

    [ObservableProperty]
    private bool _hasSelectedMods;

    [ObservableProperty]
    private bool _isBulkDeleteModsModalOpen;

    private readonly HashSet<string> _selectedModIds = new(StringComparer.OrdinalIgnoreCase);

    public string SelectedModsCountText
    {
        get
        {
            var count = SelectedModsCount;
            var format = T("String_SelectedModsCount", "{0} seleccionados");
            return string.Format(format, count);
        }
    }

    public string BulkDeleteModsModalSubtitle
    {
        get
        {
            var count = SelectedModsCount;
            var format = T("String_BulkDeleteModsSubtitle", "¿Estás seguro de que deseas eliminar {0} mods seleccionados?");
            return string.Format(format, count);
        }
    }

    partial void OnIsSelectionModeActiveChanged(bool value)
    {
        if (!value)
        {
            ClearSelection();
        }
    }

    [RelayCommand]
    public void ToggleSelectionMode()
    {
        IsSelectionModeActive = !IsSelectionModeActive;
    }

    [RelayCommand]
    public void ExitSelectionMode()
    {
        IsSelectionModeActive = false;
    }

    [RelayCommand]
    public void ClearSelection()
    {
        _selectedModIds.Clear();
        foreach (var item in Items)
        {
            item.IsSelected = false;
        }
        UpdateSelectionCounts();
    }

    [RelayCommand]
    public void ToggleModSelection(ModListItem? item)
    {
        if (!IsSelectionModeActive || item == null) return;

        if (_selectedModIds.Contains(item.Id))
        {
            _selectedModIds.Remove(item.Id);
            item.IsSelected = false;
        }
        else
        {
            _selectedModIds.Add(item.Id);
            item.IsSelected = true;
        }

        UpdateSelectionCounts();
    }

    private void UpdateSelectionCounts()
    {
        SelectedModsCount = _selectedModIds.Count;
        HasSelectedMods = SelectedModsCount > 0;
        OnPropertyChanged(nameof(SelectedModsCountText));
        OnPropertyChanged(nameof(BulkDeleteModsModalSubtitle));
    }

    [RelayCommand]
    public async Task EnableSelectedModsAsync()
    {
        var instance = Instance;
        if (instance == null) return;
        var selected = Items.Where(i => _selectedModIds.Contains(i.Id) && !i.IsEnabled).ToList();
        if (selected.Count == 0) return;

        if (SelectedModpack != null)
        {
            SelectedModpack = null;
            IsDefaultModpackActive = true;
            foreach (var p in Modpacks) p.IsActive = false;
            OnPropertyChanged(nameof(SelectedModpackDisplayName));
        }

        var count = 0;
        foreach (var mod in selected)
        {
            if (await SetModEnabledInternalAsync(instance, mod, true).ConfigureAwait(true))
            {
                mod.IsEnabled = true;
                count++;
            }
        }
        ShowStatus(string.Format(T("String_BulkModsEnabled", "{0} mods activados"), count), false);
        await LoadAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    public async Task DisableSelectedModsAsync()
    {
        var instance = Instance;
        if (instance == null) return;
        var selected = Items.Where(i => _selectedModIds.Contains(i.Id) && i.IsEnabled).ToList();
        if (selected.Count == 0) return;

        if (SelectedModpack != null)
        {
            SelectedModpack = null;
            IsDefaultModpackActive = true;
            foreach (var p in Modpacks) p.IsActive = false;
            OnPropertyChanged(nameof(SelectedModpackDisplayName));
        }

        var count = 0;
        foreach (var mod in selected)
        {
            if (await SetModEnabledInternalAsync(instance, mod, false).ConfigureAwait(true))
            {
                mod.IsEnabled = false;
                count++;
            }
        }
        ShowStatus(string.Format(T("String_BulkModsDisabled", "{0} mods desactivados"), count), false);
        await LoadAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    public void OpenBulkDeleteModal()
    {
        if (SelectedModsCount > 0)
        {
            IsBulkDeleteModsModalOpen = true;
        }
    }

    [RelayCommand]
    public void CloseBulkDeleteModal()
    {
        IsBulkDeleteModsModalOpen = false;
    }

    [RelayCommand]
    public async Task ConfirmBulkDeleteModsAsync()
    {
        IsBulkDeleteModsModalOpen = false;
        var toDelete = Items.Where(i => _selectedModIds.Contains(i.Id)).ToList();
        if (toDelete.Count == 0) return;

        var count = 0;
        foreach (var mod in toDelete)
        {
            await UninstallAsync(mod).ConfigureAwait(true);
            count++;
        }
        ExitSelectionMode();
        ShowStatus(string.Format(T("String_BulkModsDeleted", "{0} mods eliminados"), count), false);
    }

    // ═══ MODPACK CREATOR & MANAGER ═══
    public ObservableCollection<Modpack> Modpacks { get; } = [];

    [ObservableProperty]
    private Modpack? _selectedModpack;

    [ObservableProperty]
    private bool _isDefaultModpackActive = true;

    [ObservableProperty]
    private bool _showModpackBlocks = true;

    [ObservableProperty]
    private bool _isModpackBlockView = true;

    public string ModpackViewModeButtonLabel => IsModpackBlockView
        ? T("String_ModpackCurrentViewBlocks", "Vista actual: En bloques (clic para cambiar a lista)")
        : T("String_ModpackCurrentViewList", "Vista actual: En lista (clic para cambiar a bloques)");
    public string ModpackViewIconGeometryKey => IsModpackBlockView ? "IconGridView" : "IconListView";

    [RelayCommand]
    public void ToggleModpackBlocks()
    {
        ShowModpackBlocks = !ShowModpackBlocks;
        IsModpackBlockView = ShowModpackBlocks;
    }

    [RelayCommand]
    public void ToggleModpackViewMode()
    {
        IsModpackBlockView = !IsModpackBlockView;
    }

    partial void OnIsModpackBlockViewChanged(bool value)
    {
        ShowModpackBlocks = value;
        OnPropertyChanged(nameof(ModpackViewModeButtonLabel));
        OnPropertyChanged(nameof(ModpackViewIconGeometryKey));
    }

    partial void OnSelectedModpackChanged(Modpack? value)
    {
        IsDefaultModpackActive = (value == null);
        foreach (var p in Modpacks)
        {
            p.IsActive = (value != null && (p == value || p.Name.Equals(value.Name, StringComparison.OrdinalIgnoreCase)));
        }
        OnPropertyChanged(nameof(SelectedModpackDisplayName));
        SaveActiveModpack(value?.Name);
    }

    [ObservableProperty]
    private bool _isModpackDropdownOpen;

    [ObservableProperty]
    private bool _isCreateModpackOpen;

    [ObservableProperty]
    private string _newModpackName = string.Empty;

    [ObservableProperty]
    private string? _newModpackIconPath;

    [ObservableProperty]
    private bool _isDeleteModpackConfirmOpen;

    [ObservableProperty]
    private Modpack? _modpackPendingDelete;

    [RelayCommand]
    public void RequestDeleteModpack(Modpack? pack)
    {
        if (pack == null) return;
        ModpackPendingDelete = pack;
        IsDeleteModpackConfirmOpen = true;
    }

    [RelayCommand]
    public void CancelDeleteModpack()
    {
        ModpackPendingDelete = null;
        IsDeleteModpackConfirmOpen = false;
    }

    [RelayCommand]
    public void ConfirmDeleteModpack()
    {
        if (ModpackPendingDelete != null)
        {
            DeleteModpackDirect(ModpackPendingDelete);
        }
        ModpackPendingDelete = null;
        IsDeleteModpackConfirmOpen = false;
        IsModpackDetailsOpen = false;
        ActiveModpackForDetails = null;
    }

    /// <summary>Allows testing or customizing modpack icon file selection without opening a Win32 dialog.</summary>
    public Func<string?>? PickModpackIconFile { get; set; }

    /// <summary>Allows testing or customizing modpack export file path without opening a Win32 dialog.</summary>
    public Func<Modpack?, string?>? PickExportModpackFile { get; set; }

    /// <summary>Allows testing or customizing modpack import file path without opening a Win32 dialog.</summary>
    public Func<string?>? PickImportModpackFile { get; set; }

    [RelayCommand]
    public void BrowseModpackIcon()
    {
        if (PickModpackIconFile != null)
        {
            var picked = PickModpackIconFile();
            if (!string.IsNullOrWhiteSpace(picked))
            {
                NewModpackIconPath = picked;
            }
            return;
        }

        var dlg = new OpenFileDialog
        {
            Title = T("String_SelectModpackIconDialogTitle", "Seleccionar imagen de perfil para el modpack"),
            Filter = "Imágenes (*.png;*.jpg;*.jpeg;*.webp;*.ico)|*.png;*.jpg;*.jpeg;*.webp;*.ico|Todos los archivos (*.*)|*.*"
        };
        if (dlg.ShowDialog() == true)
        {
            NewModpackIconPath = dlg.FileName;
        }
    }

    [RelayCommand]
    public void ClearModpackIcon() => NewModpackIconPath = null;

    public ObservableCollection<ModpackModSelectionItem> CreateModpackSelectableMods { get; } = [];

    [RelayCommand]
    public void SelectAllCreateModpackMods()
    {
        foreach (var m in CreateModpackSelectableMods) m.IsSelected = true;
    }

    [RelayCommand]
    public void DeselectAllCreateModpackMods()
    {
        foreach (var m in CreateModpackSelectableMods) m.IsSelected = false;
    }

    [RelayCommand]
    public void ImportModpackIntoCreator()
    {
        string? selectedFilePath = null;
        if (PickImportModpackFile != null)
        {
            selectedFilePath = PickImportModpackFile();
            if (string.IsNullOrWhiteSpace(selectedFilePath)) return;
        }
        else
        {
            var ofd = new OpenFileDialog
            {
                Title = T("String_ImportModpackDialogTitle", "Seleccionar archivo de modpack"),
                Filter = "BlueStar Modpack (*.zip;*.json)|*.zip;*.json|BlueStar Modpack Package (*.zip)|*.zip|BlueStar Modpack Legacy (*.json)|*.json|Todos los archivos (*.*)|*.*"
            };
            if (ofd.ShowDialog() != true) return;
            selectedFilePath = ofd.FileName;
        }

        try
        {
            var ext = Path.GetExtension(selectedFilePath);
            var isZip = string.Equals(ext, ".zip", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(ext, ".bsmodpack", StringComparison.OrdinalIgnoreCase);

            Modpack? pack = null;
            if (isZip)
            {
                using var zipStream = File.OpenRead(selectedFilePath);
                using var archive = new ZipArchive(zipStream, ZipArchiveMode.Read);
                var entry = archive.GetEntry("modpack.json");
                if (entry != null)
                {
                    using var reader = new StreamReader(entry.Open());
                    var json = reader.ReadToEnd();
                    pack = JsonSerializer.Deserialize<Modpack>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                }
            }
            else
            {
                var json = File.ReadAllText(selectedFilePath);
                pack = JsonSerializer.Deserialize<Modpack>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            }

                if (pack != null && !string.IsNullOrWhiteSpace(pack.Name))
                {
                    NewModpackName = pack.Name;
                    if (!string.IsNullOrWhiteSpace(pack.IconPath) && File.Exists(pack.IconPath))
                    {
                        NewModpackIconPath = pack.IconPath;
                    }
                    var packModIds = new HashSet<string>(pack.Mods.Select(m => m.Id), StringComparer.OrdinalIgnoreCase);
                    var packModNames = new HashSet<string>(pack.Mods.Select(m => m.Name), StringComparer.OrdinalIgnoreCase);

                    var matched = 0;
                    foreach (var item in CreateModpackSelectableMods)
                    {
                        var isMatch = packModIds.Contains(item.Id) || packModNames.Contains(item.Name);
                        item.IsSelected = isMatch;
                        if (isMatch) matched++;
                    }

                    ShowStatus(string.Format(T("String_ModpackImportedSuccess", "Modpack \"{0}\" importado ({1} mods seleccionados)"), pack.Name, matched), false);
                }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to import modpack from {File}", selectedFilePath);
            ShowStatus(string.Format(T("String_ModpackImportFailed", "Error al importar modpack: {0}"), ex.Message), true);
        }
    }

    // ═══ ACTIVE MODPACK DETAILS & MANAGEMENT MODAL (EDIT, DELETE, EXPORT) ═══
    [ObservableProperty]
    private bool _isModpackDetailsOpen;

    [ObservableProperty]
    private Modpack? _activeModpackForDetails;

    [ObservableProperty]
    private string _editModpackName = string.Empty;

    [ObservableProperty]
    private string? _editModpackIconPath;

    public ObservableCollection<ModpackModSelectionItem> EditModpackSelectableMods { get; } = [];

    [RelayCommand]
    public void OpenModpackDetails(Modpack? pack)
    {
        if (pack == null) return;
        ActiveModpackForDetails = pack;
        EditModpackName = pack.Name;
        EditModpackIconPath = pack.IconPath;

        EditModpackSelectableMods.Clear();
        foreach (var mod in Items)
        {
            var isIncluded = pack.Mods.Any(m => IsModMatch(mod, m) && m.IsEnabled);

            EditModpackSelectableMods.Add(new ModpackModSelectionItem
            {
                Id = mod.Id,
                Name = mod.Name,
                Version = mod.Version,
                Source = mod.SourceLabel,
                IconUrl = mod.IconUrl,
                IconGeometryKey = mod.IconGeometryKey,
                IsSelected = isIncluded
            });
        }

        IsModpackDetailsOpen = true;
    }

    [RelayCommand]
    public void CloseModpackDetails()
    {
        IsModpackDetailsOpen = false;
        ActiveModpackForDetails = null;
    }

    [RelayCommand]
    public void SelectAllEditModpackMods()
    {
        foreach (var m in EditModpackSelectableMods) m.IsSelected = true;
    }

    [RelayCommand]
    public void DeselectAllEditModpackMods()
    {
        foreach (var m in EditModpackSelectableMods) m.IsSelected = false;
    }

    [RelayCommand]
    public void BrowseEditModpackIcon()
    {
        if (PickModpackIconFile != null)
        {
            var picked = PickModpackIconFile();
            if (!string.IsNullOrWhiteSpace(picked)) EditModpackIconPath = picked;
            return;
        }

        var dlg = new OpenFileDialog
        {
            Title = T("String_SelectModpackIconDialogTitle", "Seleccionar imagen de perfil para el modpack"),
            Filter = "Imágenes (*.png;*.jpg;*.jpeg;*.webp;*.ico)|*.png;*.jpg;*.jpeg;*.webp;*.ico|Todos los archivos (*.*)|*.*"
        };
        if (dlg.ShowDialog() == true)
        {
            EditModpackIconPath = dlg.FileName;
        }
    }

    [RelayCommand]
    public void ClearEditModpackIcon() => EditModpackIconPath = null;

    [RelayCommand]
    public async Task SaveEditModpackAsync()
    {
        var pack = ActiveModpackForDetails;
        if (pack == null) return;

        var newName = EditModpackName?.Trim();
        if (string.IsNullOrWhiteSpace(newName))
        {
            ShowStatus(T("String_ModpackNameEmpty", "El nombre del modpack no puede estar vacío."), true);
            return;
        }

        var selectedMods = EditModpackSelectableMods.Where(m => m.IsSelected).ToList();
        if (selectedMods.Count == 0)
        {
            ShowStatus(T("String_ModpackNoModsSelected", "Selecciona al menos un mod para el modpack."), true);
            return;
        }

        try
        {
            var dir = GetModpacksDirectory();
            var oldSafeName = string.Join("_", pack.Name.Split(Path.GetInvalidFileNameChars()));
            var newSafeName = string.Join("_", newName.Split(Path.GetInvalidFileNameChars()));

            string? savedIconPath = EditModpackIconPath;
            if (!string.IsNullOrWhiteSpace(EditModpackIconPath) && !string.IsNullOrWhiteSpace(dir) && File.Exists(EditModpackIconPath))
            {
                if (!EditModpackIconPath.StartsWith(dir, StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        var ext = Path.GetExtension(EditModpackIconPath);
                        var iconFileName = $"{newSafeName}_icon{ext}";
                        var destPath = Path.Combine(dir, iconFileName);
                        File.Copy(EditModpackIconPath, destPath, overwrite: true);
                        savedIconPath = destPath;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to copy modpack icon file");
                    }
                }
            }

            // Remove old JSON if renamed
            if (!oldSafeName.Equals(newSafeName, StringComparison.OrdinalIgnoreCase))
            {
                var oldPath = Path.Combine(dir, $"{oldSafeName}.json");
                if (File.Exists(oldPath)) File.Delete(oldPath);
            }

            var entries = selectedMods.Select(i => new ModpackEntry(i.Id, i.Name, i.Version, true)).ToList();
            var updatedPack = new Modpack
            {
                Name = newName,
                CreatedAt = pack.CreatedAt,
                IconPath = savedIconPath,
                Mods = entries,
                IsActive = true
            };

            var newJsonPath = Path.Combine(dir, $"{newSafeName}.json");
            var json = JsonSerializer.Serialize(updatedPack, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(newJsonPath, json);

            var idx = Modpacks.IndexOf(pack);
            if (idx >= 0)
            {
                Modpacks[idx] = updatedPack;
            }
            else
            {
                Modpacks.Add(updatedPack);
            }
            SelectedModpack = updatedPack;
            OnPropertyChanged(nameof(SelectedModpackDisplayName));
            IsModpackDetailsOpen = false;

            // Apply new mod states according to the edited modpack
            await ApplyModpackCoreAsync(updatedPack).ConfigureAwait(true);
            ShowStatus(string.Format(T("String_ModpackSavedSuccess", "Modpack \"{0}\" guardado"), updatedPack.Name), false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save edited modpack {Name}", newName);
            ShowStatus(string.Format(T("String_ModpackSaveFailed", "Error al guardar modpack: {0}"), ex.Message), true);
        }
    }

    public string SelectedModpackDisplayName => SelectedModpack?.Name ?? (Modpacks.Count > 0 ? Modpacks[0].Name : "Por defecto");

    private string GetModpacksDirectory()
    {
        if (Instance == null) return string.Empty;
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "BlueStar", "instances", Instance.Id.ToString(), "modpacks");
        Directory.CreateDirectory(root);
        return root;
    }

    private void SaveActiveModpack(string? packName)
    {
        try
        {
            var dir = GetModpacksDirectory();
            if (string.IsNullOrWhiteSpace(dir)) return;
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, "active_modpack.txt");
            if (string.IsNullOrWhiteSpace(packName))
            {
                if (File.Exists(file)) File.Delete(file);
            }
            else
            {
                File.WriteAllText(file, packName.Trim());
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to save active modpack {Name}", packName);
        }
    }

    private string? LoadActiveModpackName()
    {
        try
        {
            var dir = GetModpacksDirectory();
            if (string.IsNullOrWhiteSpace(dir)) return null;
            var file = Path.Combine(dir, "active_modpack.txt");
            if (File.Exists(file))
            {
                var name = File.ReadAllText(file).Trim();
                return string.IsNullOrWhiteSpace(name) ? null : name;
            }
        }
        catch { }
        return null;
    }

    public void LoadModpacks()
    {
        Modpacks.Clear();
        var dir = GetModpacksDirectory();
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
        {
            IsDefaultModpackActive = (SelectedModpack == null);
            return;
        }

        var activeName = SelectedModpack?.Name ?? LoadActiveModpackName();

        foreach (var file in Directory.EnumerateFiles(dir, "*.json"))
        {
            try
            {
                var json = File.ReadAllText(file);
                var pack = JsonSerializer.Deserialize<Modpack>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (pack != null && !string.IsNullOrWhiteSpace(pack.Name))
                {
                    Modpacks.Add(pack);
                }
            }
            catch { }
        }

        if (!string.IsNullOrWhiteSpace(activeName))
        {
            var activePack = Modpacks.FirstOrDefault(p => p.Name.Equals(activeName, StringComparison.OrdinalIgnoreCase));
            if (activePack != null)
            {
                SelectedModpack = activePack;
                activePack.IsActive = true;
                IsDefaultModpackActive = false;
            }
            else
            {
                SelectedModpack = null;
                IsDefaultModpackActive = true;
                SaveActiveModpack(null);
            }
        }
        else
        {
            SelectedModpack = null;
            IsDefaultModpackActive = true;
        }

        foreach (var p in Modpacks)
        {
            p.IsActive = (SelectedModpack != null && p.Name.Equals(SelectedModpack.Name, StringComparison.OrdinalIgnoreCase));
        }

        OnPropertyChanged(nameof(SelectedModpackDisplayName));
    }

    [RelayCommand]
    public void ToggleModpackDropdown() => IsModpackDropdownOpen = !IsModpackDropdownOpen;

    [RelayCommand]
    public async Task SelectDefaultModpackAsync()
    {
        if (IsDefaultModpackActive && SelectedModpack == null) return;

        var instance = Instance;
        if (instance == null) return;

        try
        {
            _isApplyingModpack = true;
            SelectedModpack = null;
            IsModpackDropdownOpen = false;
            OnPropertyChanged(nameof(SelectedModpackDisplayName));

            var changed = 0;
            foreach (var mod in Items.ToList())
            {
                if (!mod.IsEnabled)
                {
                    var ok = await SetModEnabledInternalAsync(instance, mod, true).ConfigureAwait(true);
                    if (ok)
                    {
                        mod.IsEnabled = true;
                        changed++;
                    }
                }
            }
            ShowStatus(changed > 0
                ? string.Format(T("String_DefaultModpackRestored", "Perfil por defecto restaurado ({0} mods activados)"), changed)
                : T("String_DefaultModpackActive", "Perfil por defecto activo (todos los mods)"), false);
            await LoadAsync().ConfigureAwait(true);
        }
        finally
        {
            _isApplyingModpack = false;
        }
    }

    public Task SelectDefaultModpack() => SelectDefaultModpackAsync();

    [RelayCommand]
    public void OpenCreateModpack()
    {
        IsModpackDropdownOpen = false;
        NewModpackName = $"Modpack {Modpacks.Count + 1}";
        NewModpackIconPath = null;
        CreateModpackSelectableMods.Clear();
        foreach (var mod in Items)
        {
            CreateModpackSelectableMods.Add(new ModpackModSelectionItem
            {
                Id = mod.Id,
                Name = mod.Name,
                Version = mod.Version,
                Source = mod.SourceLabel,
                IconUrl = mod.IconUrl,
                IconGeometryKey = mod.IconGeometryKey,
                IsSelected = mod.IsEnabled
            });
        }
        IsCreateModpackOpen = true;
    }

    [RelayCommand]
    public void CancelCreateModpack() => IsCreateModpackOpen = false;

    [RelayCommand]
    public async Task ConfirmCreateModpackAsync()
    {
        var name = NewModpackName?.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            ShowStatus(T("String_ModpackNameEmpty", "El nombre del modpack no puede estar vacío."), true);
            return;
        }

        var selectedMods = CreateModpackSelectableMods.Where(m => m.IsSelected).ToList();
        if (selectedMods.Count == 0)
        {
            ShowStatus(T("String_ModpackNoModsSelected", "No puedes crear un modpack sin ningún mod. Selecciona al menos un mod."), true);
            return;
        }

        var entries = selectedMods.Select(i => new ModpackEntry(i.Id, i.Name, i.Version, true)).ToList();

        string? savedIconPath = null;
        try
        {
            var dir = GetModpacksDirectory();
            var safeName = string.Join("_", name.Split(Path.GetInvalidFileNameChars()));

            if (!string.IsNullOrWhiteSpace(NewModpackIconPath) && !string.IsNullOrWhiteSpace(dir))
            {
                if (File.Exists(NewModpackIconPath))
                {
                    try
                    {
                        var ext = Path.GetExtension(NewModpackIconPath);
                        var iconFileName = $"{safeName}_icon{ext}";
                        var destPath = Path.Combine(dir, iconFileName);
                        File.Copy(NewModpackIconPath, destPath, overwrite: true);
                        savedIconPath = destPath;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to copy modpack icon file");
                        savedIconPath = NewModpackIconPath;
                    }
                }
                else
                {
                    savedIconPath = NewModpackIconPath;
                }
            }

            var pack = new Modpack
            {
                Name = name,
                CreatedAt = DateTimeOffset.UtcNow,
                IconPath = savedIconPath,
                Mods = entries,
                IsActive = true
            };

            if (!string.IsNullOrWhiteSpace(dir))
            {
                var filePath = Path.Combine(dir, $"{safeName}.json");
                var json = JsonSerializer.Serialize(pack, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(filePath, json);
            }

            var existing = Modpacks.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (existing != null) Modpacks.Remove(existing);
            Modpacks.Add(pack);
            IsCreateModpackOpen = false;

            // Automatically apply the modpack to disable mods outside of it
            await ApplyModpackCoreAsync(pack).ConfigureAwait(true);
            ShowStatus(string.Format(T("String_ModpackSavedSuccessCount", "Modpack \"{0}\" guardado ({1} mods)"), pack.Name, pack.ModsCount), false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save modpack {Name}", name);
            ShowStatus(string.Format(T("String_ModpackSaveFailed", "Error al guardar modpack: {0}"), ex.Message), true);
        }
    }

    public Task ConfirmCreateModpack() => ConfirmCreateModpackAsync();

    private bool _isApplyingModpack;

    public static bool IsModMatch(ModListItem mod, ModpackEntry entry)
    {
        if (string.Equals(mod.Id, entry.Id, StringComparison.OrdinalIgnoreCase)) return true;
        if (!string.IsNullOrWhiteSpace(mod.Name) && string.Equals(mod.Name, entry.Name, StringComparison.OrdinalIgnoreCase)) return true;

        var modIdNorm = mod.Id.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase) ? mod.Id[..^9] : mod.Id;
        var entryIdNorm = entry.Id.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase) ? entry.Id[..^9] : entry.Id;
        if (string.Equals(modIdNorm, entryIdNorm, StringComparison.OrdinalIgnoreCase)) return true;

        var modFile = Path.GetFileName(modIdNorm);
        var entryFile = Path.GetFileName(entryIdNorm);
        if (!string.IsNullOrEmpty(modFile) && !string.IsNullOrEmpty(entryFile) &&
            string.Equals(modFile, entryFile, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(mod.LegacyPath))
        {
            var legacyNorm = mod.LegacyPath.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase) ? mod.LegacyPath[..^9] : mod.LegacyPath;
            var legacyFile = Path.GetFileName(legacyNorm);
            if (string.Equals(legacyFile, entryFile, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(legacyNorm, entryIdNorm, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    public async Task ApplyModpackCoreAsync(Modpack pack)
    {
        var instance = Instance;
        if (instance == null) return;

        try
        {
            _isApplyingModpack = true;
            SelectedModpack = pack;
            IsModpackDropdownOpen = false;
            foreach (var p in Modpacks)
            {
                p.IsActive = (p == pack || p.Name.Equals(pack.Name, StringComparison.OrdinalIgnoreCase));
            }
            IsDefaultModpackActive = false;
            OnPropertyChanged(nameof(SelectedModpackDisplayName));

            var applied = 0;
            foreach (var mod in Items.ToList())
            {
                var entry = pack.Mods.FirstOrDefault(m => IsModMatch(mod, m));

                // Requirement 2: Deactivate mods not listed in the modpack
                var shouldBeEnabled = entry != null && entry.IsEnabled;

                if (mod.IsEnabled != shouldBeEnabled)
                {
                    var ok = await SetModEnabledInternalAsync(instance, mod, shouldBeEnabled).ConfigureAwait(true);
                    if (ok)
                    {
                        mod.IsEnabled = shouldBeEnabled;
                        applied++;
                    }
                }
            }

            ShowStatus(string.Format(T("String_ModpackAppliedSummary", "Modpack \"{0}\" aplicado ({1} mods ajustados)"), pack.Name, applied), false);
            await LoadAsync().ConfigureAwait(true);
        }
        finally
        {
            _isApplyingModpack = false;
        }
    }

    [RelayCommand]
    public async Task ApplyModpackAsync(Modpack? pack)
    {
        if (pack == null) return;

        // Requirement 3: If already selected/active, clicking it again opens details modal
        if (pack.IsActive || (SelectedModpack != null && (SelectedModpack == pack || pack.Name.Equals(SelectedModpack.Name, StringComparison.OrdinalIgnoreCase))))
        {
            OpenModpackDetails(pack);
            return;
        }

        await ApplyModpackCoreAsync(pack).ConfigureAwait(true);
    }

    [RelayCommand]
    public void DeleteModpack(Modpack? pack) => RequestDeleteModpack(pack);

    public void DeleteModpackDirect(Modpack pack)
    {
        try
        {
            var dir = GetModpacksDirectory();
            if (!string.IsNullOrWhiteSpace(dir))
            {
                var safeName = string.Join("_", pack.Name.Split(Path.GetInvalidFileNameChars()));
                var filePath = Path.Combine(dir, $"{safeName}.json");
                if (File.Exists(filePath)) File.Delete(filePath);
                if (!string.IsNullOrWhiteSpace(pack.IconPath) && File.Exists(pack.IconPath) && Path.GetDirectoryName(pack.IconPath)?.Equals(dir, StringComparison.OrdinalIgnoreCase) == true)
                {
                    try { File.Delete(pack.IconPath); } catch { }
                }
            }

            var toRemove = Modpacks.FirstOrDefault(p => p == pack || p.Name.Equals(pack.Name, StringComparison.OrdinalIgnoreCase));
            if (toRemove != null)
            {
                Modpacks.Remove(toRemove);
            }
            else
            {
                Modpacks.Remove(pack);
            }

            var wasSelected = SelectedModpack != null && (SelectedModpack == pack || SelectedModpack.Name.Equals(pack.Name, StringComparison.OrdinalIgnoreCase));
            if (wasSelected)
            {
                _ = SelectDefaultModpackAsync();
            }

            IsModpackDetailsOpen = false;
            ActiveModpackForDetails = null;

            ShowStatus(string.Format(T("String_ModpackDeletedSuccess", "Modpack \"{0}\" eliminado"), pack.Name), false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete modpack {Name}", pack.Name);
            ShowStatus(string.Format(T("String_ModpackDeleteFailed", "Error al eliminar modpack: {0}"), ex.Message), true);
        }
    }

    [RelayCommand]
    public async Task ExportModpackAsync(Modpack? pack)
    {
        if (pack == null || Instance == null) return;

        string? destinationPath = null;
        if (PickExportModpackFile != null)
        {
            destinationPath = PickExportModpackFile(pack);
            if (string.IsNullOrWhiteSpace(destinationPath)) return;
        }
        else
        {
            var sfd = new SaveFileDialog
            {
                Title = T("String_ExportModpackDialogTitle", "Exportar modpack"),
                Filter = "BlueStar Modpack Package (*.zip)|*.zip|BlueStar Modpack Legacy (*.json)|*.json|Todos los archivos (*.*)|*.*",
                FileName = $"{pack.Name}.zip",
                DefaultExt = ".zip"
            };

            if (sfd.ShowDialog() != true) return;
            destinationPath = sfd.FileName;
        }

        var isZip = !string.Equals(Path.GetExtension(destinationPath), ".json", StringComparison.OrdinalIgnoreCase);

        try
        {
            if (!isZip)
            {
                var json = JsonSerializer.Serialize(pack, new JsonSerializerOptions { WriteIndented = true });
                await File.WriteAllTextAsync(destinationPath, json).ConfigureAwait(true);
                ShowStatus(string.Format(T("String_ModpackExportSuccess", "Modpack exportado a {0}"), Path.GetFileName(destinationPath)), false);
                return;
            }

            var instance = Instance;
            var tempZipPath = destinationPath + ".tmp";
            if (File.Exists(tempZipPath)) File.Delete(tempZipPath);

            var ledger = _pipeline != null
                ? await _pipeline.GetLedgerAsync(instance, CancellationToken.None).ConfigureAwait(true)
                : ModLedger.Empty;

            var managedRecordsInPack = new List<InstalledModRecord>();
            var filesToInclude = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            // Collect mod files for each mod in pack
            foreach (var entry in pack.Mods)
            {
                var record = ledger.Mods.FirstOrDefault(m =>
                    string.Equals(m.Id, entry.Id, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(m.Name, entry.Name, StringComparison.OrdinalIgnoreCase));

                if (record != null)
                {
                    managedRecordsInPack.Add(record);
                    foreach (var f in record.Files)
                    {
                        var normal = Path.Combine(instance.InstallPath, f.RelativePath);
                        var disabled = normal + ".disabled";
                        if (File.Exists(normal))
                        {
                            filesToInclude[$"files/{f.RelativePath.Replace('\\', '/')}"] = normal;
                        }
                        else if (File.Exists(disabled))
                        {
                            filesToInclude[$"files/{f.RelativePath.Replace('\\', '/')}"] = disabled;
                        }
                    }
                }
                else
                {
                    var unmanagedItem = Items.FirstOrDefault(i => IsModMatch(i, entry));
                    if (unmanagedItem != null && !string.IsNullOrWhiteSpace(unmanagedItem.LegacyPath))
                    {
                        var path = unmanagedItem.LegacyPath;
                        var cleanPath = path.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase) ? path[..^9] : path;
                        var targetPath = File.Exists(path) ? path : (File.Exists(cleanPath) ? cleanPath : (File.Exists(cleanPath + ".disabled") ? cleanPath + ".disabled" : null));

                        if (targetPath != null)
                        {
                            if (File.Exists(targetPath))
                            {
                                var rel = Path.GetRelativePath(instance.InstallPath, cleanPath).Replace('\\', '/');
                                if (!rel.StartsWith("../", StringComparison.Ordinal))
                                {
                                    filesToInclude[$"files/{rel}"] = targetPath;
                                }
                                else if (!string.IsNullOrWhiteSpace(WorkshopDirectoryPath) && targetPath.StartsWith(WorkshopDirectoryPath, StringComparison.OrdinalIgnoreCase))
                                {
                                    var wsRel = Path.GetRelativePath(WorkshopDirectoryPath, cleanPath).Replace('\\', '/');
                                    filesToInclude[$"files/__workshop/{wsRel}"] = targetPath;
                                }
                            }
                            else if (Directory.Exists(path) || Directory.Exists(cleanPath))
                            {
                                var dir = Directory.Exists(path) ? path : cleanPath;
                                var dirClean = dir.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase) ? dir[..^9] : dir;
                                var allFiles = Directory.GetFiles(dir, "*.*", SearchOption.AllDirectories);
                                foreach (var f in allFiles)
                                {
                                    var subRel = Path.GetRelativePath(dir, f);
                                    var rel = Path.Combine(Path.GetRelativePath(instance.InstallPath, dirClean), subRel).Replace('\\', '/');
                                    if (rel.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase))
                                    {
                                        rel = rel[..^9];
                                    }
                                    if (!rel.StartsWith("../", StringComparison.Ordinal))
                                    {
                                        filesToInclude[$"files/{rel}"] = f;
                                    }
                                }
                            }
                        }
                    }
                }
            }

            // Include BepInEx config folder if present
            var bepConfigDir = Path.Combine(instance.InstallPath, "BepInEx", "config");
            if (Directory.Exists(bepConfigDir))
            {
                var configFiles = Directory.GetFiles(bepConfigDir, "*.*", SearchOption.AllDirectories);
                foreach (var cfg in configFiles)
                {
                    var rel = Path.GetRelativePath(instance.InstallPath, cfg).Replace('\\', '/');
                    filesToInclude[$"files/{rel}"] = cfg;
                }
            }

            // Build ZIP archive
            using (var zipStream = new FileStream(tempZipPath, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Create, leaveOpen: false))
            {
                // 1. modpack.json
                var modpackEntry = archive.CreateEntry("modpack.json", CompressionLevel.Optimal);
                using (var entryStream = modpackEntry.Open())
                using (var writer = new Utf8JsonWriter(entryStream, new JsonWriterOptions { Indented = true }))
                {
                    JsonSerializer.Serialize(writer, pack, new JsonSerializerOptions { WriteIndented = true });
                }

                // 2. Icon (if available)
                if (!string.IsNullOrWhiteSpace(pack.IconPath) && File.Exists(pack.IconPath))
                {
                    try
                    {
                        var iconExt = Path.GetExtension(pack.IconPath);
                        if (string.IsNullOrWhiteSpace(iconExt)) iconExt = ".png";
                        var iconEntry = archive.CreateEntry($"icon{iconExt}", CompressionLevel.Optimal);
                        using var iconInStream = File.OpenRead(pack.IconPath);
                        using var iconOutStream = iconEntry.Open();
                        await iconInStream.CopyToAsync(iconOutStream).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to package modpack icon into zip");
                    }
                }

                // 3. records.json (managed mod records)
                if (managedRecordsInPack.Count > 0)
                {
                    var recordsEntry = archive.CreateEntry("records.json", CompressionLevel.Optimal);
                    using var recStream = recordsEntry.Open();
                    using var writer = new Utf8JsonWriter(recStream, new JsonWriterOptions { Indented = true });
                    {
                        JsonSerializer.Serialize(writer, managedRecordsInPack, new JsonSerializerOptions { WriteIndented = true });
                    }
                }

                // 4. Mod files and BepInEx config files
                foreach (var (entryKey, sourceFile) in filesToInclude)
                {
                    try
                    {
                        if (File.Exists(sourceFile))
                        {
                            var fileEntry = archive.CreateEntry(entryKey, CompressionLevel.Optimal);
                            using var fileInStream = File.OpenRead(sourceFile);
                            using var fileOutStream = fileEntry.Open();
                            await fileInStream.CopyToAsync(fileOutStream).ConfigureAwait(false);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to package file {SourceFile} as {EntryKey}", sourceFile, entryKey);
                    }
                }
            }

            if (File.Exists(destinationPath)) File.Delete(destinationPath);
            File.Move(tempZipPath, destinationPath);

            ShowStatus(string.Format(T("String_ModpackExportSuccess", "Modpack exportado a {0}"), Path.GetFileName(destinationPath)), false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to export modpack {Name}", pack.Name);
            ShowStatus(string.Format(T("String_ModpackExportFailed", "Error al exportar modpack: {0}"), ex.Message), true);
        }
    }

    [RelayCommand]
    public async Task ImportModpackAsync()
    {
        var instance = Instance;
        if (instance == null) return;

        string? importPath = null;
        if (PickImportModpackFile != null)
        {
            importPath = PickImportModpackFile();
            if (string.IsNullOrWhiteSpace(importPath)) return;
        }
        else
        {
            var ofd = new OpenFileDialog
            {
                Title = T("String_ImportModpackDialogTitle", "Seleccionar archivo de modpack"),
                Filter = "BlueStar Modpack (*.zip;*.json)|*.zip;*.json|BlueStar Modpack Package (*.zip)|*.zip|BlueStar Modpack Legacy (*.json)|*.json|Todos los archivos (*.*)|*.*"
            };
            if (ofd.ShowDialog() != true) return;
            importPath = ofd.FileName;
        }

        try
        {
            var ext = Path.GetExtension(importPath);
            var isZip = string.Equals(ext, ".zip", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(ext, ".bsmodpack", StringComparison.OrdinalIgnoreCase);

            Modpack? pack = null;
            string? packJson = null;

            if (isZip)
            {
                using var zipStream = File.OpenRead(importPath);
                using var archive = new ZipArchive(zipStream, ZipArchiveMode.Read);

                var modpackEntry = archive.GetEntry("modpack.json");
                if (modpackEntry == null)
                {
                    ShowStatus(T("String_ModpackImportFailedInvalidZip", "El archivo ZIP no contiene un modpack válido (falta modpack.json)."), true);
                    return;
                }

                using (var reader = new StreamReader(modpackEntry.Open()))
                {
                    packJson = await reader.ReadToEndAsync().ConfigureAwait(true);
                }
                pack = JsonSerializer.Deserialize<Modpack>(packJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (pack == null || string.IsNullOrWhiteSpace(pack.Name))
                {
                    ShowStatus(T("String_ModpackImportFailedInvalidJson", "El archivo modpack.json contiene datos inválidos."), true);
                    return;
                }

                var dir = GetModpacksDirectory();
                var safeName = string.Join("_", pack.Name.Split(Path.GetInvalidFileNameChars()));

                // Extract icon if present
                var iconEntry = archive.GetEntry("icon.png") ?? archive.Entries.FirstOrDefault(e => e.FullName.StartsWith("icon.", StringComparison.OrdinalIgnoreCase));
                string? extractedIconPath = null;
                if (iconEntry != null && !string.IsNullOrWhiteSpace(dir))
                {
                    try
                    {
                        Directory.CreateDirectory(dir);
                        var iconExt = Path.GetExtension(iconEntry.FullName);
                        if (string.IsNullOrWhiteSpace(iconExt)) iconExt = ".png";
                        extractedIconPath = Path.Combine(dir, $"{safeName}_icon{iconExt}");
                        iconEntry.ExtractToFile(extractedIconPath, overwrite: true);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to extract modpack icon from zip");
                    }
                }

                // Extract all files in files/ (including mods and BepInEx config)
                foreach (var entry in archive.Entries)
                {
                    if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\')) continue;
                    var normEntry = entry.FullName.Replace('\\', '/');
                    if (!normEntry.StartsWith("files/", StringComparison.OrdinalIgnoreCase)) continue;

                    var relativeInside = normEntry[6..];
                    string targetPath;
                    if (relativeInside.StartsWith("__workshop/", StringComparison.OrdinalIgnoreCase))
                    {
                        var wsSub = relativeInside[11..];
                        var wsRoot = !string.IsNullOrWhiteSpace(WorkshopDirectoryPath)
                            ? WorkshopDirectoryPath
                            : Path.Combine(instance.InstallPath, "Mods", "Workshop");
                        targetPath = Path.Combine(wsRoot, wsSub.Replace('/', Path.DirectorySeparatorChar));
                    }
                    else
                    {
                        targetPath = Path.Combine(instance.InstallPath, relativeInside.Replace('/', Path.DirectorySeparatorChar));
                    }

                    // ZipSlip safety check
                    var fullDest = Path.GetFullPath(targetPath);
                    var fullInstall = Path.GetFullPath(instance.InstallPath);
                    var fullWs = !string.IsNullOrWhiteSpace(WorkshopDirectoryPath) ? Path.GetFullPath(WorkshopDirectoryPath) : null;
                    if (!fullDest.StartsWith(fullInstall, StringComparison.OrdinalIgnoreCase) &&
                        (fullWs == null || !fullDest.StartsWith(fullWs, StringComparison.OrdinalIgnoreCase)))
                    {
                        _logger.LogWarning("Skipping unsafe zip entry {Entry} targeting {Path}", entry.FullName, fullDest);
                        continue;
                    }

                    Directory.CreateDirectory(Path.GetDirectoryName(fullDest)!);
                    entry.ExtractToFile(fullDest, overwrite: true);
                }

                // Merge ledger records if present
                var recordsEntry = archive.GetEntry("records.json");
                if (recordsEntry != null)
                {
                    try
                    {
                        using var recReader = new StreamReader(recordsEntry.Open());
                        var recJson = await recReader.ReadToEndAsync().ConfigureAwait(true);
                        var importedRecords = JsonSerializer.Deserialize<List<InstalledModRecord>>(recJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                        if (importedRecords != null && importedRecords.Count > 0)
                        {
                            var store = new BlueStar.Infrastructure.Mods.Installation.JsonModLedgerStore(
                                Microsoft.Extensions.Logging.Abstractions.NullLogger<BlueStar.Infrastructure.Mods.Installation.JsonModLedgerStore>.Instance,
                                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BlueStar", "instances"));

                            var currentLedger = await store.LoadAsync(instance.Id, CancellationToken.None).ConfigureAwait(true);
                            foreach (var rec in importedRecords)
                            {
                                currentLedger = currentLedger.WithMod(rec);
                            }
                            await store.SaveAsync(instance.Id, currentLedger, CancellationToken.None).ConfigureAwait(true);
                        }
                    }
                    catch (Exception exRec)
                    {
                        _logger.LogWarning(exRec, "Failed to merge modpack ledger records from zip");
                    }
                }

                if (!string.IsNullOrWhiteSpace(extractedIconPath))
                {
                    pack.IconPath = extractedIconPath;
                    packJson = JsonSerializer.Serialize(pack, new JsonSerializerOptions { WriteIndented = true });
                }

                if (!string.IsNullOrWhiteSpace(dir))
                {
                    Directory.CreateDirectory(dir);
                    var filePath = Path.Combine(dir, $"{safeName}.json");
                    await File.WriteAllTextAsync(filePath, packJson!).ConfigureAwait(true);
                }
            }
            else
            {
                packJson = await File.ReadAllTextAsync(importPath).ConfigureAwait(true);
                pack = JsonSerializer.Deserialize<Modpack>(packJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (pack != null && !string.IsNullOrWhiteSpace(pack.Name))
                {
                    var dir = GetModpacksDirectory();
                    if (!string.IsNullOrWhiteSpace(dir))
                    {
                        Directory.CreateDirectory(dir);
                        var safeName = string.Join("_", pack.Name.Split(Path.GetInvalidFileNameChars()));
                        var filePath = Path.Combine(dir, $"{safeName}.json");
                        await File.WriteAllTextAsync(filePath, packJson).ConfigureAwait(true);
                    }
                }
            }

            if (pack != null && !string.IsNullOrWhiteSpace(pack.Name))
            {
                var existing = Modpacks.FirstOrDefault(p => p.Name.Equals(pack.Name, StringComparison.OrdinalIgnoreCase));
                if (existing != null) Modpacks.Remove(existing);
                Modpacks.Add(pack);

                await LoadAsync().ConfigureAwait(true);
                await ApplyModpackCoreAsync(pack).ConfigureAwait(true);
                ShowStatus(string.Format(T("String_ModpackImportSuccess", "Modpack \"{0}\" importado correctamente"), pack.Name), false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to import modpack from {File}", importPath);
            ShowStatus(string.Format(T("String_ModpackImportFailed", "Error al importar modpack: {0}"), ex.Message), true);
        }
    }

    // ═══ MOD PAGE MODAL OVERLAY ═══
    [ObservableProperty]
    private bool _isModPageModalOpen;

    [ObservableProperty]
    private string? _modPageTitle;

    [ObservableProperty]
    private string? _modPageUrl;

    [ObservableProperty]
    private bool _isModPageLoading;

    public event Action<string>? OnNavigateModPage;

    public void OpenModPage(string? title, string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        ModPageTitle = title ?? "Mod";
        ModPageUrl = url;
        IsModPageLoading = true;
        IsModPageModalOpen = true;
        OnNavigateModPage?.Invoke(url);
    }

    [RelayCommand]
    public void CloseModPage()
    {
        IsModPageModalOpen = false;
        IsModPageLoading = false;
        ModPageUrl = null;
        ModPageTitle = null;
    }

    [RelayCommand]
    public void OpenModInExternalBrowser()
    {
        if (string.IsNullOrWhiteSpace(ModPageUrl)) return;
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = ModPageUrl,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to open mod URL: {Url}", ModPageUrl);
        }
    }

    [RelayCommand]
    public void OpenModDetail(ModListItem? item)
    {
        if (item == null) return;
        var url = item.PageUrl;
        if (string.IsNullOrWhiteSpace(url))
        {
            if (item.SourceLabel.Contains("Workshop", StringComparison.OrdinalIgnoreCase))
            {
                var match = System.Text.RegularExpressions.Regex.Match(item.Id, @"\d+");
                if (match.Success) url = $"https://steamcommunity.com/sharedfiles/filedetails/?id={match.Value}";
            }
            else if (item.SourceLabel.Contains("Thunderstore", StringComparison.OrdinalIgnoreCase))
            {
                var parts = item.Id.Split(':');
                if (parts.Length >= 3)
                {
                    url = $"https://thunderstore.io/c/{parts[0]}/p/{parts[1]}/{parts[2]}/";
                }
                else if (parts.Length == 2 && !string.IsNullOrWhiteSpace(ThunderstoreCommunity))
                {
                    url = $"https://thunderstore.io/c/{ThunderstoreCommunity}/p/{parts[0]}/{parts[1]}/";
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(url))
        {
            OpenModPage(item.Name, url);
        }
    }

    // ── Online Mod Providers Availability ──
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAnyProvider))]
    [NotifyPropertyChangedFor(nameof(HasModBrowserSupport))]
    [NotifyPropertyChangedFor(nameof(CanShowWorkshopFilter))]
    [NotifyPropertyChangedFor(nameof(WorkshopCardTitle))]
    [NotifyPropertyChangedFor(nameof(NativeModsBlockTitle))]
    [NotifyPropertyChangedFor(nameof(IsWorkshopCardVisible))]
    [NotifyPropertyChangedFor(nameof(HasAnyModLoaderCard))]
    [NotifyPropertyChangedFor(nameof(HasBothModLoaderCards))]
    private bool _hasWorkshopSupport;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAnyProvider))]
    [NotifyPropertyChangedFor(nameof(HasModBrowserSupport))]
    [NotifyPropertyChangedFor(nameof(CanShowThunderstoreFilter))]
    private bool _hasThunderstoreSupport;

    [ObservableProperty]
    private string? _thunderstoreCommunity;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAnyProvider))]
    [NotifyPropertyChangedFor(nameof(HasModBrowserSupport))]
    [NotifyPropertyChangedFor(nameof(CanShowCurseForgeFilter))]
    private bool _hasCurseForgeSupport;

    [ObservableProperty]
    private int? _curseForgeGameId;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAnyProvider))]
    [NotifyPropertyChangedFor(nameof(HasModBrowserSupport))]
    [NotifyPropertyChangedFor(nameof(CanShowNexusFilter))]
    private bool _hasNexusSupport;

    [ObservableProperty]
    private string? _nexusGameDomain;

    [ObservableProperty]
    private bool _isCheckingProviders;

    public bool HasAnyProvider => HasWorkshopSupport || HasThunderstoreSupport || HasCurseForgeSupport || HasNexusSupport;
    public bool HasModBrowserSupport => HasWorkshopSupport || HasThunderstoreSupport || HasCurseForgeSupport || HasNexusSupport;
    public bool CanShowWorkshopFilter => HasWorkshopSupport || WorkshopModsCount > 0;
    public bool CanShowThunderstoreFilter => HasThunderstoreSupport || ThunderstoreModsCount > 0;
    public bool CanShowCurseForgeFilter => HasCurseForgeSupport || CurseForgeModsCount > 0;
    public bool CanShowNexusFilter => HasNexusSupport || NexusModsCount > 0;
    public bool CanShowLocalFilter => LocalModsCount > 0;

    public async Task CheckProvidersAsync(GameInstance instance)
    {
        if (instance == null) return;
        IsCheckingProviders = true;
        try
        {
            var workshopTask = (_workshopService != null && instance.AppId > 0)
                ? _workshopService.HasWorkshopSupportAsync(instance.AppId)
                : Task.FromResult(false);

            var thunderstoreTask = _thunderstoreService != null
                ? _thunderstoreService.ResolveCommunityAsync(instance)
                : Task.FromResult<string?>(null);

            var curseForgeTask = _curseForgeService != null
                ? _curseForgeService.ResolveGameIdAsync(instance)
                : Task.FromResult<int?>(null);

            var nexusTask = _nexusModsService != null
                ? _nexusModsService.ResolveGameDomainAsync(instance)
                : Task.FromResult<string?>(null);

            await Task.WhenAll(
                SafeWrapTask(workshopTask, false),
                SafeWrapTask(thunderstoreTask, null),
                SafeWrapTask(curseForgeTask, null),
                SafeWrapTask(nexusTask, null)
            ).ConfigureAwait(true);

            HasWorkshopSupport = workshopTask.IsCompletedSuccessfully && workshopTask.Result;

            ThunderstoreCommunity = thunderstoreTask.IsCompletedSuccessfully ? thunderstoreTask.Result : null;
            HasThunderstoreSupport = !string.IsNullOrWhiteSpace(ThunderstoreCommunity);

            CurseForgeGameId = curseForgeTask.IsCompletedSuccessfully ? curseForgeTask.Result : null;
            HasCurseForgeSupport = CurseForgeGameId.HasValue && CurseForgeGameId.Value > 0;

            NexusGameDomain = nexusTask.IsCompletedSuccessfully ? nexusTask.Result : null;
            HasNexusSupport = !string.IsNullOrWhiteSpace(NexusGameDomain);

            ModBrowser.UpdateProviderInfo(
                HasWorkshopSupport,
                HasThunderstoreSupport, ThunderstoreCommunity,
                HasCurseForgeSupport, CurseForgeGameId,
                HasNexusSupport, NexusGameDomain);

            OnPropertyChanged(nameof(WorkshopCardTitle));
            OnPropertyChanged(nameof(NativeModsBlockTitle));
            OnPropertyChanged(nameof(IsWorkshopCardVisible));
            OnPropertyChanged(nameof(HasAnyModLoaderCard));
            OnPropertyChanged(nameof(HasBothModLoaderCards));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to check mod providers support for {Name}", instance.Name);
        }
        finally
        {
            IsCheckingProviders = false;
            OnPropertyChanged(nameof(HasAnyProvider));
            OnPropertyChanged(nameof(HasModBrowserSupport));
            OnPropertyChanged(nameof(CanShowWorkshopFilter));
            OnPropertyChanged(nameof(CanShowThunderstoreFilter));
            OnPropertyChanged(nameof(CanShowCurseForgeFilter));
            OnPropertyChanged(nameof(CanShowNexusFilter));
            OnPropertyChanged(nameof(CanShowLocalFilter));
        }
    }

    private static async Task<T> SafeWrapTask<T>(Task<T> task, T fallback)
    {
        try
        {
            return await task.ConfigureAwait(false);
        }
        catch
        {
            return fallback;
        }
    }

    // ── Mod Directories (Workshop & BepInEx Loader) ──
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WorkshopCardTitle))]
    [NotifyPropertyChangedFor(nameof(NativeModsBlockTitle))]
    [NotifyPropertyChangedFor(nameof(HasWorkshopDirectory))]
    [NotifyPropertyChangedFor(nameof(IsWorkshopCardVisible))]
    [NotifyPropertyChangedFor(nameof(HasAnyModLoaderCard))]
    [NotifyPropertyChangedFor(nameof(HasBothModLoaderCards))]
    private string? _workshopDirectoryPath;

    private string? _fallbackNativeModsDir;

    public string? NativeModsDirectoryPath
    {
        get => WorkshopDirectoryPath ?? _fallbackNativeModsDir;
        set => WorkshopDirectoryPath = value;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBepInExCardVisible))]
    [NotifyPropertyChangedFor(nameof(HasAnyModLoaderCard))]
    [NotifyPropertyChangedFor(nameof(HasBothModLoaderCards))]
    private string? _bepInExDirectoryPath;

    public string WorkshopCardTitle => T("String_NativeModsTitle", "Mods Nativos");
    public string NativeModsBlockTitle => T("String_NativeModsTitle", "Mods Nativos");

    public bool HasWorkshopDirectory => !string.IsNullOrWhiteSpace(WorkshopDirectoryPath);

        public int NativeModsCount => Items.Count(i => !i.IsBepInEx);

    public int BepInExModsCount => Items.Count(i => i.IsBepInEx);

    public string WorkshopModsStatsSummary => WorkshopModsCount == 1
        ? T("String_ModInstalledSingular", "1 mod instalado")
        : string.Format(T("String_ModsInstalledPlural", "{0} mods instalados"), WorkshopModsCount);

    public string NativeModsStatsSummary => NativeModsCount == 1
        ? T("String_ModInstalledSingular", "1 mod instalado")
        : string.Format(T("String_ModsInstalledPlural", "{0} mods instalados"), NativeModsCount);

    public string BepInExModsStatsSummary => BepInExModsCount == 1
        ? T("String_PluginLoadedSingular", "1 plugin cargado")
        : string.Format(T("String_PluginsLoadedPlural", "{0} plugins cargados"), BepInExModsCount);

    public bool IsNativeModsCardVisible =>
        HasWorkshopSupport ||
        (Instance?.Engine?.Type == EngineType.UnrealEngine && !string.IsNullOrWhiteSpace(WorkshopDirectoryPath)) ||
        (!string.IsNullOrWhiteSpace(WorkshopDirectoryPath) && (NativeModsCount > 0 || (Directory.Exists(WorkshopDirectoryPath) && Instance?.Engine?.Type != EngineType.Unity && NativeModsCount > 0))) ||
        (!string.IsNullOrWhiteSpace(_fallbackNativeModsDir) && Directory.Exists(_fallbackNativeModsDir) && Instance?.Engine?.Type != EngineType.Unity && NativeModsCount > 0) ||
        NativeModsCount > 0;

    public bool IsWorkshopCardVisible => IsNativeModsCardVisible;

    public bool IsBepInExCardVisible =>
        Instance?.Engine?.Type != EngineType.UnrealEngine &&
        (IsEngineSupported || IsEngineInstalled || !string.IsNullOrWhiteSpace(BepInExDirectoryPath));

    public bool HasAnyModLoaderCard => IsNativeModsCardVisible || IsBepInExCardVisible;

    public bool HasBothModLoaderCards => IsNativeModsCardVisible && IsBepInExCardVisible;

    // ── Mod Engine / Loader info (e.g. BepInEx, UE4SS, Native) ──
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ModEngineTitle))]
    private string? _engineName;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsVersionDifferent))]
    [NotifyPropertyChangedFor(nameof(EngineActionButtonText))]
    [NotifyPropertyChangedFor(nameof(FormattedEngineVersion))]
    private string? _engineVersion;

    public static string? CleanVersion(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var s = raw.Trim();
        while (s.StartsWith("v", StringComparison.OrdinalIgnoreCase))
        {
            s = s[1..].TrimStart();
        }
        var plusIdx = s.IndexOf('+');
        if (plusIdx >= 0) s = s[..plusIdx].Trim();
        var parenIdx = s.IndexOf('(');
        if (parenIdx >= 0) s = s[..parenIdx].Trim();
        return s;
    }

    public string? FormattedEngineVersion => CleanVersion(EngineVersion);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EngineActionButtonText))]
    [NotifyPropertyChangedFor(nameof(CanShowEngineActionButton))]
    [NotifyPropertyChangedFor(nameof(IsBepInExCardVisible))]
    [NotifyPropertyChangedFor(nameof(HasAnyModLoaderCard))]
    [NotifyPropertyChangedFor(nameof(HasBothModLoaderCards))]
    private bool _isEngineInstalled;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ModEngineTitle))]
    [NotifyPropertyChangedFor(nameof(IsBepInExCardVisible))]
    [NotifyPropertyChangedFor(nameof(HasAnyModLoaderCard))]
    [NotifyPropertyChangedFor(nameof(HasBothModLoaderCards))]
    private bool _isEngineSupported;

    public string ModEngineTitle => !string.IsNullOrWhiteSpace(EngineName)
        ? EngineName
        : (IsEngineSupported ? "BepInEx" : "Mods Nativos");

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(InstallOrUpdateEngineCommand))]
    [NotifyCanExecuteChangedFor(nameof(UninstallEngineCommand))]
    private bool _isLoadingEngine;

    public ObservableCollection<BepInExRelease> AvailableEngineReleases { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsVersionDifferent))]
    [NotifyPropertyChangedFor(nameof(EngineActionButtonText))]
    [NotifyPropertyChangedFor(nameof(CanShowEngineActionButton))]
    [NotifyCanExecuteChangedFor(nameof(InstallOrUpdateEngineCommand))]
    private BepInExRelease? _selectedEngineRelease;

    public bool IsVersionDifferent =>
        IsEngineInstalled &&
        SelectedEngineRelease != null &&
        !string.IsNullOrWhiteSpace(EngineVersion) &&
        !string.Equals(CleanVersion(SelectedEngineRelease.Version), CleanVersion(EngineVersion), StringComparison.OrdinalIgnoreCase);

    public bool CanShowEngineActionButton => !IsEngineInstalled || IsVersionDifferent;

    public string EngineActionButtonText
    {
        get
        {
            var targetVer = CleanVersion(SelectedEngineRelease?.Version);
            if (!IsEngineInstalled)
            {
                return targetVer != null
                    ? string.Format(T("String_EngineInstallBtn", "Install v{0}"), targetVer)
                    : T("String_InstallEngineBtn", "Install Loader");
            }
            if (IsVersionDifferent && targetVer != null)
            {
                return string.Format(T("String_EngineSwitchBtn", "Switch to v{0}"), targetVer);
            }
            return string.Empty;
        }
    }

    [RelayCommand]
    public async Task CheckAndLoadEngineAsync()
    {
        var instance = Instance;
        if (instance == null || string.IsNullOrWhiteSpace(instance.InstallPath)) return;

        var isUnity = (instance.Engine?.Supports(EngineCapabilities.BepInExSupported) == true
                       || instance.Engine?.Type == EngineType.Unity)
                      && instance.Engine?.Type != EngineType.UnrealEngine;
        IsEngineSupported = isUnity;
        EngineName = isUnity ? "BepInEx" : null;
        OnPropertyChanged(nameof(IsBepInExCardVisible));
        OnPropertyChanged(nameof(HasAnyModLoaderCard));
        OnPropertyChanged(nameof(HasBothModLoaderCards));

        if (!isUnity || _bepInExService == null)
        {
            IsEngineInstalled = false;
            EngineVersion = null;
            return;
        }

        try
        {
            IsLoadingEngine = true;
            IsEngineInstalled = _bepInExService.IsInstalled(instance.InstallPath);
            EngineVersion = await _bepInExService.GetInstalledVersionAsync(instance.InstallPath, CancellationToken.None).ConfigureAwait(true);

            if (AvailableEngineReleases.Count == 0)
            {
                var releases = await _bepInExService.GetAvailableReleasesAsync(false, CancellationToken.None).ConfigureAwait(true);
                AvailableEngineReleases.Clear();
                foreach (var r in releases)
                {
                    AvailableEngineReleases.Add(r);
                }
            }

            if (SelectedEngineRelease == null && AvailableEngineReleases.Count > 0)
            {
                var cleanCurrent = CleanVersion(EngineVersion);
                SelectedEngineRelease = !string.IsNullOrWhiteSpace(cleanCurrent)
                    ? AvailableEngineReleases.FirstOrDefault(r => string.Equals(CleanVersion(r.Version), cleanCurrent, StringComparison.OrdinalIgnoreCase)) ?? AvailableEngineReleases[0]
                    : AvailableEngineReleases[0];
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to inspect BepInEx status for {Instance}", instance.Name);
        }
        finally
        {
            IsLoadingEngine = false;
            OnPropertyChanged(nameof(IsVersionDifferent));
            OnPropertyChanged(nameof(CanShowEngineActionButton));
            OnPropertyChanged(nameof(EngineActionButtonText));
        }
    }

    [RelayCommand]
    public async Task InstallOrUpdateEngineAsync()
    {
        var instance = Instance;
        if (instance == null || string.IsNullOrWhiteSpace(instance.InstallPath) || SelectedEngineRelease == null || _bepInExService == null)
            return;

        IsLoadingEngine = true;
        ShowStatus(string.Format(T("String_EngineInstalling", "⏳ Installing BepInEx {0}..."), SelectedEngineRelease.Version), isError: false);
        try
        {
            var ok = await _bepInExService.InstallAsync(instance.InstallPath, SelectedEngineRelease, null, CancellationToken.None).ConfigureAwait(true);
            if (ok)
            {
                await MigrateActiveModsToLoaderFolderAsync(instance).ConfigureAwait(true);
                ShowStatus(string.Format(T("String_EngineInstalledSuccess", "✅ BepInEx {0} installed successfully!"), SelectedEngineRelease.Version), isError: false);
                await CheckAndLoadEngineAsync().ConfigureAwait(true);
                await LoadAsync().ConfigureAwait(true);
            }
            else
            {
                ShowStatus(T("String_EngineInstallFailed", "❌ Failed to install BepInEx."), isError: true);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to install BepInEx for {Instance}", instance.Name);
            ShowStatus(string.Format(T("String_EngineInstallFailed", "❌ Failed to install BepInEx: {0}"), ex.Message), isError: true);
        }
        finally
        {
            IsLoadingEngine = false;
        }
    }

    [ObservableProperty]
    private bool _isUninstallEngineConfirmOpen;

    [RelayCommand]
    public void RequestUninstallEngine()
    {
        IsUninstallEngineConfirmOpen = true;
    }

    [RelayCommand]
    public void CancelUninstallEngine()
    {
        IsUninstallEngineConfirmOpen = false;
    }

    [RelayCommand]
    public async Task ConfirmUninstallEngineAsync()
    {
        IsUninstallEngineConfirmOpen = false;
        await UninstallEngineAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    public async Task UninstallEngineAsync()
    {
        var instance = Instance;
        if (instance == null || string.IsNullOrWhiteSpace(instance.InstallPath) || _bepInExService == null)
            return;

        IsLoadingEngine = true;
        ShowStatus(T("String_EngineUninstalling", "⏳ Uninstalling BepInEx..."), isError: false);
        try
        {
            var ok = await _bepInExService.UninstallAsync(instance.InstallPath, keepPluginsFolder: true, CancellationToken.None).ConfigureAwait(true);
            if (ok)
            {
                ShowStatus(T("String_EngineUninstallSuccess", "✅ BepInEx uninstalled (plugins folder preserved)."), isError: false);
                await CheckAndLoadEngineAsync().ConfigureAwait(true);
                await LoadAsync().ConfigureAwait(true);
            }
            else
            {
                ShowStatus(T("String_EngineUninstallFailed", "❌ Failed to uninstall BepInEx."), isError: true);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to uninstall BepInEx for {Instance}", instance.Name);
            ShowStatus(string.Format(T("String_EngineUninstallFailed", "❌ Failed to uninstall BepInEx: {0}"), ex.Message), isError: true);
        }
        finally
        {
            IsLoadingEngine = false;
        }
    }

    public async Task MigrateActiveModsToLoaderFolderAsync(GameInstance instance)
    {
        if (instance == null || string.IsNullOrWhiteSpace(instance.InstallPath)) return;

        try
        {
            var loaderPluginsDir = Path.Combine(instance.InstallPath, "BepInEx", "plugins");
            Directory.CreateDirectory(loaderPluginsDir);

            var candidateDirs = new[]
            {
                Path.Combine(instance.InstallPath, "mods"),
                Path.Combine(instance.InstallPath, "Mods")
            };

            foreach (var legacyDir in candidateDirs)
            {
                if (!Directory.Exists(legacyDir) || string.Equals(legacyDir, loaderPluginsDir, StringComparison.OrdinalIgnoreCase))
                    continue;

                foreach (var file in Directory.EnumerateFiles(legacyDir, "*.*", SearchOption.TopDirectoryOnly))
                {
                    try
                    {
                        var destFile = Path.Combine(loaderPluginsDir, Path.GetFileName(file));
                        if (!File.Exists(destFile))
                        {
                            File.Move(file, destFile);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to move mod file {File} to BepInEx plugins", file);
                    }
                }

                foreach (var subDir in Directory.EnumerateDirectories(legacyDir, "*", SearchOption.TopDirectoryOnly))
                {
                    try
                    {
                        var dirName = Path.GetFileName(subDir);
                        if (string.Equals(dirName, "BepInEx", StringComparison.OrdinalIgnoreCase)) continue;

                        var destDir = Path.Combine(loaderPluginsDir, dirName);
                        if (!Directory.Exists(destDir))
                        {
                            Directory.Move(subDir, destDir);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to move mod directory {Dir} to BepInEx plugins", subDir);
                    }
                }

                try
                {
                    if (!Directory.EnumerateFileSystemEntries(legacyDir).Any())
                    {
                        Directory.Delete(legacyDir);
                    }
                }
                catch { }
            }

            if (_pipeline != null)
            {
                var ledger = await _pipeline.GetLedgerAsync(instance, CancellationToken.None).ConfigureAwait(true);
                var updatedMods = new List<InstalledModRecord>();
                var modified = false;

                foreach (var mod in ledger.Mods)
                {
                    var updatedFiles = new List<InstalledModFile>();
                    var modModified = false;

                    foreach (var f in mod.Files)
                    {
                        if (f.RelativePath.StartsWith("mods" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                            f.RelativePath.StartsWith("Mods" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                        {
                            var rest = f.RelativePath.Substring(5);
                            var newRel = Path.Combine("BepInEx", "plugins", rest);
                            updatedFiles.Add(new InstalledModFile(newRel, f.SizeBytes, f.Sha256) { ReplacedExisting = f.ReplacedExisting });
                            modModified = true;
                            modified = true;
                        }
                        else
                        {
                            updatedFiles.Add(f);
                        }
                    }

                    updatedMods.Add(modModified ? mod with { Files = updatedFiles } : mod);
                }

                if (modified)
                {
                    var store = new BlueStar.Infrastructure.Mods.Installation.JsonModLedgerStore(
                        Microsoft.Extensions.Logging.Abstractions.NullLogger<BlueStar.Infrastructure.Mods.Installation.JsonModLedgerStore>.Instance,
                        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BlueStar", "instances", instance.Id.ToString()));
                    await store.SaveAsync(instance.Id, ledger with { Mods = updatedMods }, CancellationToken.None).ConfigureAwait(true);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to migrate active mods to BepInEx plugins for {Instance}", instance.Name);
        }
    }

    [RelayCommand]
    public void OpenEngineFolder()
    {
        var instance = Instance;
        if (instance == null || string.IsNullOrWhiteSpace(instance.InstallPath)) return;
        var bepFolder = Path.Combine(instance.InstallPath, "BepInEx");
        var target = Directory.Exists(bepFolder) ? bepFolder : instance.InstallPath;
        try
        {
            Process.Start(new ProcessStartInfo { FileName = target, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to open BepInEx folder {Path}", target);
        }
    }

    /// <summary>Invokes engine installation from parent ViewModel if available.</summary>
    public Func<Task>? InstallEngineAction { get; set; }

    [RelayCommand]
    public async Task InstallEngineAsync()
    {
        if (InstallEngineAction != null)
        {
            await InstallEngineAction().ConfigureAwait(true);
        }
        else
        {
            await InstallOrUpdateEngineAsync().ConfigureAwait(true);
        }
    }

    public void UpdateEngineStatus(string? name, string? version, bool isInstalled, bool isSupported)
    {
        EngineName = name;
        EngineVersion = version;
        IsEngineInstalled = isInstalled;
        IsEngineSupported = isSupported;
        OnPropertyChanged(nameof(CanShowEngineActionButton));
        OnPropertyChanged(nameof(EngineActionButtonText));
    }

    public bool IsIdle => !IsBusy;

    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(IsIdle));

    partial void OnSearchTextChanged(string value) => RefreshFilteredItems();
    partial void OnSelectedFilterChanged(string value) => RefreshFilteredItems();

    [RelayCommand]
    public void SetFilter(string? filter)
    {
        SelectedFilter = string.IsNullOrWhiteSpace(filter) ? "All" : filter;
    }

    [RelayCommand]
    public void ClearStatus()
    {
        StatusMessage = null;
    }

    public async Task InstallDroppedFileAsync(string filePath)
    {
        if (Instance == null || string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath)) return;

        var ext = Path.GetExtension(filePath);
        if (!string.Equals(ext, ".zip", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(ext, ".dll", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(ext, ".pak", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(ext, ".ucas", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(ext, ".utoc", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(ext, ".json", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(ext, ".7z", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(ext, ".rar", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(ext, ".tar", StringComparison.OrdinalIgnoreCase))
        {
            ShowStatus(T("String_UnsupportedModFile", "❌ Unsupported file type. Please drop a .zip, .dll, or .7z mod file."), isError: true);
            return;
        }

        var name = Path.GetFileNameWithoutExtension(filePath);
        await InstallPackageAsync(new ModPackage
        {
            Path = filePath,
            Name = name,
            Source = ModSourceRef.Local(name)
        }).ConfigureAwait(true);
    }

    public async Task InstallDroppedFilesAsync(IEnumerable<string> filePaths)
    {
        foreach (var path in filePaths)
        {
            await InstallDroppedFileAsync(path).ConfigureAwait(true);
        }
    }

    public int TotalModsCount => Items.Count;
    public int ActiveModsCount => Items.Count(i => i.IsEnabled);
    public int DisabledModsCount => Items.Count(i => !i.IsEnabled);
    public int WorkshopModsCount => Items.Count(i => i.SourceLabel.Contains("Workshop", StringComparison.OrdinalIgnoreCase) || (!string.IsNullOrWhiteSpace(WorkshopDirectoryPath) && !string.IsNullOrWhiteSpace(i.LegacyPath) && i.LegacyPath.StartsWith(WorkshopDirectoryPath, StringComparison.OrdinalIgnoreCase)));
    public int ThunderstoreModsCount => Items.Count(i => i.SourceLabel.Contains("Thunderstore", StringComparison.OrdinalIgnoreCase));
    public int CurseForgeModsCount => Items.Count(i => i.SourceLabel.Contains("CurseForge", StringComparison.OrdinalIgnoreCase));
    public int NexusModsCount => Items.Count(i => i.SourceLabel.Contains("Nexus", StringComparison.OrdinalIgnoreCase));
    public int LocalModsCount => Items.Count(i =>
        !i.SourceLabel.Contains("Workshop", StringComparison.OrdinalIgnoreCase) &&
        !i.SourceLabel.Contains("Thunderstore", StringComparison.OrdinalIgnoreCase) &&
        !i.SourceLabel.Contains("CurseForge", StringComparison.OrdinalIgnoreCase) &&
        !i.SourceLabel.Contains("Nexus", StringComparison.OrdinalIgnoreCase));
    public bool HasNoSearchResults => FilteredItems.Count == 0 && Items.Count > 0;

    public string StatsSummary
    {
        get
        {
            var totalBytes = Items.Sum(i => i.SizeBytes);
            var sizeStr = totalBytes > 0 ? $" · {FormatSize(totalBytes)}" : string.Empty;
            return string.Format(T("String_ModStatsFormat", "{0} mods · {1} active{2}"), TotalModsCount, ActiveModsCount, sizeStr);
        }
    }

    public void RefreshFilteredItems()
    {
        var query = SearchText?.Trim() ?? string.Empty;
        var filter = SelectedFilter ?? "All";

        var filtered = Items.Where(item =>
        {
            var matchesCategory = filter switch
            {
                "Enabled" => item.IsEnabled,
                "Disabled" => !item.IsEnabled,
                "Workshop" => item.SourceLabel.Contains("Workshop", StringComparison.OrdinalIgnoreCase),
                "Thunderstore" => item.SourceLabel.Contains("Thunderstore", StringComparison.OrdinalIgnoreCase),
                "CurseForge" => item.SourceLabel.Contains("CurseForge", StringComparison.OrdinalIgnoreCase),
                "Nexus" => item.SourceLabel.Contains("Nexus", StringComparison.OrdinalIgnoreCase),
                "Local" => !item.SourceLabel.Contains("Workshop", StringComparison.OrdinalIgnoreCase) &&
                           !item.SourceLabel.Contains("Thunderstore", StringComparison.OrdinalIgnoreCase) &&
                           !item.SourceLabel.Contains("CurseForge", StringComparison.OrdinalIgnoreCase) &&
                           !item.SourceLabel.Contains("Nexus", StringComparison.OrdinalIgnoreCase),
                _ => true
            };
            if (!matchesCategory) return false;

            if (string.IsNullOrWhiteSpace(query)) return true;
            return item.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                   (item.Version != null && item.Version.Contains(query, StringComparison.OrdinalIgnoreCase)) ||
                   (item.Author != null && item.Author.Contains(query, StringComparison.OrdinalIgnoreCase)) ||
                   (item.Detail != null && item.Detail.Contains(query, StringComparison.OrdinalIgnoreCase)) ||
                   item.SourceLabel.Contains(query, StringComparison.OrdinalIgnoreCase);
        });

        IEnumerable<ModListItem> sorted = SelectedSort switch
        {
            "Status" => IsSortDescending ? filtered.OrderByDescending(i => i.IsEnabled).ThenBy(i => i.Name)
                                         : filtered.OrderBy(i => i.IsEnabled).ThenBy(i => i.Name),
            "Source" => IsSortDescending ? filtered.OrderByDescending(i => i.SourceLabel).ThenBy(i => i.Name)
                                         : filtered.OrderBy(i => i.SourceLabel).ThenBy(i => i.Name),
            "Size" => IsSortDescending ? filtered.OrderByDescending(i => i.SizeBytes).ThenBy(i => i.Name)
                                       : filtered.OrderBy(i => i.SizeBytes).ThenBy(i => i.Name),
            _ => IsSortDescending ? filtered.OrderByDescending(i => i.Name)
                                  : filtered.OrderBy(i => i.Name)
        };
        var sortedList = sorted.ToList();

        OnUiThread(() =>
        {
            FilteredItems.Clear();
            foreach (var item in sortedList) FilteredItems.Add(item);
            OnPropertyChanged(nameof(TotalModsCount));
            OnPropertyChanged(nameof(DefaultModpackActiveCountText));
            OnPropertyChanged(nameof(DefaultModpackInstalledCountText));
            OnPropertyChanged(nameof(ActiveModsCount));
            OnPropertyChanged(nameof(DisabledModsCount));
            OnPropertyChanged(nameof(WorkshopModsCount));
            OnPropertyChanged(nameof(WorkshopModsStatsSummary));
            OnPropertyChanged(nameof(NativeModsCount));
            OnPropertyChanged(nameof(NativeModsStatsSummary));
            OnPropertyChanged(nameof(BepInExModsCount));
            OnPropertyChanged(nameof(BepInExModsStatsSummary));
            OnPropertyChanged(nameof(ThunderstoreModsCount));
            OnPropertyChanged(nameof(CurseForgeModsCount));
            OnPropertyChanged(nameof(NexusModsCount));
            OnPropertyChanged(nameof(LocalModsCount));
            OnPropertyChanged(nameof(CanShowWorkshopFilter));
            OnPropertyChanged(nameof(CanShowThunderstoreFilter));
            OnPropertyChanged(nameof(CanShowCurseForgeFilter));
            OnPropertyChanged(nameof(CanShowNexusFilter));
            OnPropertyChanged(nameof(CanShowLocalFilter));
            OnPropertyChanged(nameof(HasNoSearchResults));
            OnPropertyChanged(nameof(StatsSummary));
            OnPropertyChanged(nameof(IsNativeModsCardVisible));
            OnPropertyChanged(nameof(IsWorkshopCardVisible));
            OnPropertyChanged(nameof(IsBepInExCardVisible));
            OnPropertyChanged(nameof(HasAnyModLoaderCard));
            OnPropertyChanged(nameof(HasBothModLoaderCards));
        });
    }

    private (bool IsInstalled, string? Version) CheckIfThunderstorePackageInstalled(ThunderstorePackage package)
    {
        if (package == null) return (false, null);
        var match = Items.FirstOrDefault(i =>
            i.Name.Equals(package.Name, StringComparison.OrdinalIgnoreCase) ||
            i.Id.EndsWith($":{package.Name}", StringComparison.OrdinalIgnoreCase));

        return (match != null, match?.Version);
    }

    private (bool IsInstalled, string? Version) CheckIfCurseForgeModInstalled(CurseForgeMod mod)
    {
        if (mod == null) return (false, null);
        var match = Items.FirstOrDefault(i =>
            i.Name.Equals(mod.Name, StringComparison.OrdinalIgnoreCase) ||
            i.Id.EndsWith($":{mod.Name}", StringComparison.OrdinalIgnoreCase) ||
            i.Id.Contains($":cf-{mod.Id}", StringComparison.OrdinalIgnoreCase) ||
            i.Id.Contains($":{mod.Id}", StringComparison.OrdinalIgnoreCase));

        return (match != null, match?.Version);
    }

    /// <summary>
    /// Installs a mod from an nxm:// URL using the Nexus Mods service and the mod install pipeline.
    /// Used by direct invocations, CLI, and IPC from external browser clicks.
    /// </summary>
    public async Task<bool> InstallNxmUrlAsync(string nxmUrl)
    {
        var instance = Instance;
        if (string.IsNullOrWhiteSpace(nxmUrl) || _nexusModsService == null || instance == null)
            return false;

        try
        {
            ShowStatus(T("String_NexusDownloading", "⏳ Descargando mod desde Nexus Mods..."), isError: false);
            var progress = new Progress<double>();
            var package = await _nexusModsService.DownloadFromNxmAsync(instance, nxmUrl, progress).ConfigureAwait(true);
            if (package == null)
            {
                ShowStatus(T("String_NexusDownloadFailed", "❌ Error al descargar el mod desde Nexus Mods."), isError: true);
                return false;
            }

            var result = await InstallPackageAsync(package).ConfigureAwait(true);
            if (result?.Status == ModInstallStatus.Installed)
            {
                ShowStatus(string.Format(T("String_ModInstalled", "✅ {0} instalado ({1} archivos)."), package.Name, result.Record?.Files.Count ?? 0), isError: false);
                await LoadAsync().ConfigureAwait(true);
                return true;
            }
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to install Nexus mod from NXM URI: {Uri}", nxmUrl);
            ShowStatus(string.Format(T("String_NexusDownloadFailedDetail", "❌ Error al descargar desde Nexus: {0}"), ex.Message), isError: true);
            return false;
        }
    }

    /// <summary>
    /// Tracks the instance shown by the page. A new copy of the same instance (it is an immutable record)
    /// only swaps the reference; switching to another instance clears what was shown.
    /// </summary>
    public void SetInstance(GameInstance? instance)
    {
        var sameInstance = instance != null && Instance != null && instance.Id == Instance.Id;
        Instance = instance;
        if (sameInstance) return;

        OnUiThread(() =>
        {
            Items.Clear();
            FilteredItems.Clear();
            SearchText = string.Empty;
            SelectedFilter = "All";
        });
        StatusMessage = null;
        IsStatusError = false;
        CancelPendingPlan();

        if (instance != null)
        {
            LoadModpacks();
            _checkProvidersTask = CheckProvidersAsync(instance);
            _ = CheckAndLoadEngineAsync();
            _ = LoadAsync();
        }
    }

    [RelayCommand]
    public async Task LoadAsync()
    {
        var instance = Instance;
        if (instance == null || string.IsNullOrWhiteSpace(instance.InstallPath)) return;

        try
        {
            var legacy = _legacyRegistry?.GetManagerForInstance(instance);
            var resolution = BlueStar.Infrastructure.Mods.GameModPathResolver.ResolveModPaths(instance);

            string? resolvedNativeDir = null;
            if (!string.IsNullOrWhiteSpace(resolution.WorkshopDirectory))
            {
                resolvedNativeDir = resolution.WorkshopDirectory;
            }
            else if (!string.IsNullOrWhiteSpace(resolution.PrimaryDirectory) && resolution.GameCategory is not ("Generic" or "Default" or "Unity" or "UnityBepInEx"))
            {
                resolvedNativeDir = resolution.PrimaryDirectory;
            }
            else if (HasWorkshopSupport)
            {
                resolvedNativeDir = legacy?.GetModsDirectory(instance);
            }
            else if (!string.IsNullOrWhiteSpace(resolution.PrimaryDirectory) && Directory.Exists(resolution.PrimaryDirectory) && instance.Engine?.Type != EngineType.Unity)
            {
                resolvedNativeDir = resolution.PrimaryDirectory;
            }

            WorkshopDirectoryPath = resolvedNativeDir;

            _fallbackNativeModsDir = legacy?.GetModsDirectory(instance);

            var isUnity = (instance.Engine?.Supports(EngineCapabilities.BepInExSupported) == true
                           || instance.Engine?.Type == EngineType.Unity)
                          && instance.Engine?.Type != EngineType.UnrealEngine;
            BepInExDirectoryPath = (isUnity || (instance.Engine?.Type != EngineType.UnrealEngine && Directory.Exists(Path.Combine(instance.InstallPath, "BepInEx"))))
                ? Path.Combine(instance.InstallPath, "BepInEx", "plugins")
                : null;

            ModsDirectoryPath = WorkshopDirectoryPath ?? BepInExDirectoryPath ?? legacy?.GetModsDirectory(instance) ?? instance.InstallPath;

            var ledger = _pipeline != null
                ? await _pipeline.GetLedgerAsync(instance, CancellationToken.None).ConfigureAwait(true)
                : ModLedger.Empty;

            var unmanaged = legacy != null
                ? await legacy.GetInstalledModsAsync(instance, CancellationToken.None).ConfigureAwait(true)
                : [];

            if (Instance == null || Instance.Id != instance.Id) return;

            var rows = ledger.Mods
                .OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
                .Select(FromRecord)
                .Concat(WithoutManagedFiles(unmanaged, ledger, instance.InstallPath).Select(FromLegacy))
                .ToList();

            // The page may ask for a reload from a background thread; the bound collection only accepts UI-thread changes.
            OnUiThread(() =>
            {
                var testItems = Items.Where(i => !i.IsManaged && string.IsNullOrWhiteSpace(i.LegacyPath)).ToList();
                Items.Clear();
                foreach (var row in rows) Items.Add(row);
                foreach (var testItem in testItems)
                {
                    if (!Items.Any(i => i.Id == testItem.Id)) Items.Add(testItem);
                }
                RefreshFilteredItems();
                OnPropertyChanged(nameof(NativeModsCount));
                OnPropertyChanged(nameof(NativeModsStatsSummary));
                OnPropertyChanged(nameof(WorkshopModsCount));
                OnPropertyChanged(nameof(WorkshopModsStatsSummary));
                OnPropertyChanged(nameof(BepInExModsCount));
                OnPropertyChanged(nameof(BepInExModsStatsSummary));
                OnPropertyChanged(nameof(IsNativeModsCardVisible));
                OnPropertyChanged(nameof(IsWorkshopCardVisible));
                OnPropertyChanged(nameof(IsBepInExCardVisible));
                OnPropertyChanged(nameof(HasAnyModLoaderCard));
                OnPropertyChanged(nameof(HasBothModLoaderCards));
            });

            ModsChanged?.Invoke(this, EventArgs.Empty);
            _ = CheckAndLoadEngineAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load mods for {Instance}", instance.Name);
            ShowStatus(string.Format(T("String_ModsLoadFailed", "❌ Could not read the installed mods: {0}"), ex.Message), isError: true);
        }
    }

    [RelayCommand(CanExecute = nameof(IsIdle))]
    public async Task InstallFromFileAsync()
    {
        if (Instance == null) return;

        var path = PickModFile();
        if (string.IsNullOrWhiteSpace(path)) return;

        var name = Path.GetFileNameWithoutExtension(path);
        await InstallPackageAsync(new ModPackage
        {
            Path = path,
            Name = name,
            Source = ModSourceRef.Local(name)
        }).ConfigureAwait(true);
    }

    /// <summary>
    /// Installs a package through the pipeline. A low-confidence plan opens the confirmation dialog
    /// instead of installing; providers (Workshop, Thunderstore...) call this too.
    /// </summary>
    public async Task<ModInstallResult?> InstallPackageAsync(ModPackage package, ModInstallOptions? options = null)
    {
        var instance = Instance;
        if (instance == null) return null;

        if (_pipeline == null)
        {
            ShowStatus(T("String_ModsPipelineUnavailable", "❌ The mod installer is not available."), isError: true);
            return null;
        }

        IsBusy = true;
        ShowStatus(string.Format(T("String_ModInstalling", "📦 Installing {0}..."), package.Name), isError: false);
        try
        {
            var result = await _pipeline.InstallAsync(instance, package, options, CancellationToken.None).ConfigureAwait(true);
            switch (result.Status)
            {
                case ModInstallStatus.Installed:
                    ShowStatus(string.Format(T("String_ModInstalled", "✅ {0} installed ({1} files)."),
                        package.Name, result.Record?.Files.Count ?? 0), isError: false);
                    await LoadAsync().ConfigureAwait(true);
                    break;

                case ModInstallStatus.NeedsConfirmation:
                    OpenConfirmation(package, result.Plan!);
                    ShowStatus(null, isError: false);
                    break;

                case ModInstallStatus.Conflict:
                    var first = result.Conflicts[0];
                    var owner = Items.FirstOrDefault(i => i.Id == first.OwnerModId)?.Name ?? first.OwnerModId;
                    ShowStatus(string.Format(T("String_ModConflict", "⚠ {0} would overwrite {1} files from {2} (e.g. {3}). Uninstall or disable {2} first."),
                        package.Name, result.Conflicts.Count, owner, first.RelativePath), isError: true);
                    break;

                default:
                    ShowStatus(string.Format(T("String_ModInstallFailed", "❌ Could not install {0}: {1}"),
                        package.Name, result.Error), isError: true);
                    break;
            }

            return result;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(IsIdle))]
    public async Task ConfirmPendingPlanAsync()
    {
        var package = _pendingPackage;
        CancelPendingPlan();
        if (package == null) return;

        await InstallPackageAsync(package, new ModInstallOptions { AllowLowConfidence = true }).ConfigureAwait(true);
    }

    [RelayCommand]
    public void CancelPendingPlan()
    {
        _pendingPackage = null;
        PendingDestinations.Clear();
        PendingModName = string.Empty;
        PendingPlanReason = null;
        IsConfirmPlanOpen = false;
    }

    private async Task<bool> SetModEnabledInternalAsync(GameInstance instance, ModListItem item, bool enable)
    {
        if (item.IsEnabled == enable) return true;
        bool ok = false;
        try
        {
            if (item.IsManaged)
            {
                ok = _pipeline != null &&
                     await _pipeline.SetEnabledAsync(instance, item.Id, enable, CancellationToken.None).ConfigureAwait(true);
            }
            else
            {
                var legacy = _legacyRegistry?.GetManagerForInstance(instance);
                if (legacy != null)
                {
                    ok = await legacy.ToggleModAsync(instance, item.Id, enable, CancellationToken.None).ConfigureAwait(true);
                }

                // Robust fallback for legacy mods: toggle the file or directory directly if needed
                if (!ok && !string.IsNullOrWhiteSpace(item.LegacyPath))
                {
                    try
                    {
                        var path = item.LegacyPath;
                        var cleanPath = path.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase) ? path[..^9] : path;
                        var disPath = cleanPath + ".disabled";
                        if (!enable)
                        {
                            if (File.Exists(cleanPath))
                            {
                                if (File.Exists(disPath)) File.Delete(disPath);
                                File.Move(cleanPath, disPath);
                                ok = true;
                            }
                            else if (Directory.Exists(cleanPath))
                            {
                                if (Directory.Exists(disPath)) Directory.Delete(disPath, true);
                                Directory.Move(cleanPath, disPath);
                                ok = true;
                            }
                        }
                        else
                        {
                            if (File.Exists(disPath))
                            {
                                if (File.Exists(cleanPath)) File.Delete(cleanPath);
                                File.Move(disPath, cleanPath);
                                ok = true;
                            }
                            else if (Directory.Exists(disPath))
                            {
                                if (Directory.Exists(cleanPath)) Directory.Delete(cleanPath, true);
                                Directory.Move(disPath, cleanPath);
                                ok = true;
                            }
                        }
                    }
                    catch (Exception exFallback)
                    {
                        _logger.LogWarning(exFallback, "Direct file toggle fallback failed for {Path}", item.LegacyPath);
                    }
                }
            }

            if (ok || (!item.IsManaged && string.IsNullOrWhiteSpace(item.LegacyPath)))
            {
                item.IsEnabled = enable;
                ok = true;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to change mod {ModId} enabled state", item.Id);
            ok = false;
        }

        return ok;
    }

    [RelayCommand]
    public async Task ToggleAsync(ModListItem? item)
    {
        var instance = Instance;
        if (instance == null || item == null) return;

        // Requirement 2: Toggling any mod when a modpack was active resets to default profile
        if (!_isApplyingModpack && SelectedModpack != null)
        {
            SelectedModpack = null;
            IsDefaultModpackActive = true;
            foreach (var p in Modpacks) p.IsActive = false;
            OnPropertyChanged(nameof(SelectedModpackDisplayName));
        }

        var enable = !item.IsEnabled;
        var ok = await SetModEnabledInternalAsync(instance, item, enable).ConfigureAwait(true);

        if (!ok)
        {
            ShowStatus(string.Format(T("String_ModToggleFailed", "❌ Could not change {0}. Another mod may now use the same files."), item.Name), isError: true);
        }

        await LoadAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    public async Task UninstallAsync(ModListItem? item)
    {
        var instance = Instance;
        if (instance == null || item == null) return;

        bool ok;
        try
        {
            ok = item.IsManaged
                ? _pipeline != null && await _pipeline.UninstallAsync(instance, item.Id, CancellationToken.None).ConfigureAwait(true)
                : await UninstallLegacyAsync(instance, item).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to uninstall mod {ModId}", item.Id);
            ok = false;
        }

        ShowStatus(ok
            ? string.Format(T("String_ModUninstalled", "🗑 {0} uninstalled."), item.Name)
            : string.Format(T("String_ModUninstallFailed", "❌ Could not uninstall {0}."), item.Name), isError: !ok);

        await LoadAsync().ConfigureAwait(true);
    }

    // ═══ SINGLE MOD DELETE CONFIRMATION (REQUIREMENT 9) ═══
    [ObservableProperty]
    private bool _isDeleteModConfirmOpen;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ModPendingDeleteVersionText))]
    private ModListItem? _modPendingDelete;

    public string? ModPendingDeleteVersionText =>
        !string.IsNullOrWhiteSpace(ModPendingDelete?.Version) ? $"  ({ModPendingDelete.Version.Trim()})" : null;

    [RelayCommand]
    public void RequestDeleteMod(ModListItem? item)
    {
        if (item == null) return;
        ModPendingDelete = item;
        IsDeleteModConfirmOpen = true;
    }

    [RelayCommand]
    public void CancelDeleteMod()
    {
        ModPendingDelete = null;
        IsDeleteModConfirmOpen = false;
    }

    [RelayCommand]
    public async Task ConfirmDeleteModAsync()
    {
        var item = ModPendingDelete;
        ModPendingDelete = null;
        IsDeleteModConfirmOpen = false;
        if (item != null)
        {
            await UninstallAsync(item).ConfigureAwait(true);
        }
    }

    [RelayCommand]
    public void OpenWorkshopFolder()
    {
        var target = !string.IsNullOrWhiteSpace(WorkshopDirectoryPath) ? WorkshopDirectoryPath : Instance?.InstallPath;
        if (string.IsNullOrWhiteSpace(target)) return;

        try
        {
            if (!Directory.Exists(target))
            {
                if (!IsNativeModsCardVisible) return;
                Directory.CreateDirectory(target);
            }
            Process.Start(new ProcessStartInfo { FileName = target, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to open workshop folder {Path}", target);
        }
    }

    [RelayCommand]
    public void OpenNativeModsFolder() => OpenWorkshopFolder();

    [RelayCommand]
    public void OpenBepInExFolder()
    {
        var target = !string.IsNullOrWhiteSpace(BepInExDirectoryPath)
            ? BepInExDirectoryPath
            : Path.Combine(Instance?.InstallPath ?? "", "BepInEx", "plugins");
        if (string.IsNullOrWhiteSpace(target)) return;

        try
        {
            Directory.CreateDirectory(target);
            Process.Start(new ProcessStartInfo { FileName = target, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to open BepInEx folder {Path}", target);
        }
    }

    [RelayCommand]
    public void OpenModsFolder()
    {
        var target = !string.IsNullOrWhiteSpace(ModsDirectoryPath) ? ModsDirectoryPath : Instance?.InstallPath;
        if (string.IsNullOrWhiteSpace(target)) return;

        try
        {
            if (Directory.Exists(target))
            {
                Process.Start(new ProcessStartInfo { FileName = target, UseShellExecute = true });
            }
            else if (Instance?.InstallPath != null && Directory.Exists(Instance.InstallPath))
            {
                Process.Start(new ProcessStartInfo { FileName = Instance.InstallPath, UseShellExecute = true });
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to open mods folder {Path}", target);
        }
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private void OpenConfirmation(ModPackage package, ModInstallPlan plan)
    {
        _pendingPackage = package;
        PendingModName = package.Name;
        // The resolver explains itself in English; the UI shows the translated reason for each rule.
        PendingPlanReason = T($"String_ModPlanReason_{plan.Strategy}", plan.Reason ?? string.Empty);
        PendingDestinations.Clear();
        foreach (var destination in plan.Operations.Select(o => o.RelativeDestination).Take(50))
        {
            PendingDestinations.Add(destination);
        }

        if (plan.Operations.Count > 50)
        {
            PendingDestinations.Add(string.Format(T("String_ModPlanMoreFiles", "… and {0} more"), plan.Operations.Count - 50));
        }

        IsConfirmPlanOpen = true;
    }

    private async Task<bool> UninstallLegacyAsync(GameInstance instance, ModListItem item)
    {
        var legacy = _legacyRegistry?.GetManagerForInstance(instance);
        if (legacy == null) return false;

        // Legacy managers only know mods by folder scan, so the file or folder itself is removed here.
        if (!string.IsNullOrWhiteSpace(item.LegacyPath))
        {
            if (File.Exists(item.LegacyPath)) File.Delete(item.LegacyPath);
            else if (Directory.Exists(item.LegacyPath)) Directory.Delete(item.LegacyPath, recursive: true);
        }

        await legacy.UninstallModAsync(instance, item.Id, CancellationToken.None).ConfigureAwait(true);
        return true;
    }

    /// <summary>Drops scanned mods whose files are already tracked in the ledger, so nothing is listed twice.</summary>
    public static IEnumerable<ModItem> WithoutManagedFiles(IEnumerable<ModItem> scanned, ModLedger ledger, string installPath)
    {
        static string StripDisabled(string p) =>
            p.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase) ? p[..^9] : p;

        var managedFiles = ledger.Mods
            .SelectMany(m => m.Files)
            .Select(f => StripDisabled(Path.GetFullPath(Path.Combine(installPath, f.RelativePath))))
            .ToList();
        if (managedFiles.Count == 0) return scanned;

        return scanned.Where(mod =>
        {
            if (string.IsNullOrWhiteSpace(mod.FilePath)) return true;
            var path = StripDisabled(Path.GetFullPath(mod.FilePath).TrimEnd(Path.DirectorySeparatorChar));
            return !managedFiles.Any(f =>
                f.Equals(path, StringComparison.OrdinalIgnoreCase) ||
                f.StartsWith(path + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith(f + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
        });
    }

    private ModListItem FromRecord(InstalledModRecord record)
    {
        var isEnabled = record.IsEnabled;
        if (Instance?.InstallPath != null && record.Files.Count > 0)
        {
            var first = record.Files[0];
            var normal = Path.Combine(Instance.InstallPath, first.RelativePath);
            var disabled = normal + ".disabled";
            if (File.Exists(disabled) && !File.Exists(normal))
            {
                isEnabled = false;
            }
            else if (File.Exists(normal) && !File.Exists(disabled))
            {
                isEnabled = true;
            }
        }

        var isBepInEx = record.Files.Any(f => f.RelativePath.StartsWith("BepInEx", StringComparison.OrdinalIgnoreCase));

        return new()
        {
            Id = record.Id,
            Name = record.Name,
            Version = record.Version,
            SourceLabel = ProviderLabel(record.Source.Provider),
            Detail = string.Format(T("String_ModManagedDetail", "{0} files · {1}"), record.Files.Count, FormatSize(record.TotalSizeBytes)),
            Author = record.Source.Provider.Equals("workshop", StringComparison.OrdinalIgnoreCase)
                ? $"Steam Workshop #{record.Source.LedgerId.Replace("workshop:", "")}"
                : record.Source.Provider.Equals("thunderstore", StringComparison.OrdinalIgnoreCase)
                    ? (record.Source.RemoteId.Split(':').ElementAtOrDefault(1) is { } owner && !string.IsNullOrWhiteSpace(owner)
                        ? $"by {owner} · Thunderstore"
                        : "Thunderstore")
                    : ProviderLabel(record.Source.Provider),
            SizeBytes = record.TotalSizeBytes,
            FormattedSize = FormatSize(record.TotalSizeBytes),
            IsEnabled = isEnabled,
            IsManaged = true,
            IsBepInEx = isBepInEx,
            PageUrl = ResolveModPageUrl(record),
            IconUrl = ResolveModIconUrl(record)
        };
    }

    private string? ResolveModPageUrl(InstalledModRecord record)
    {
        if (!string.IsNullOrWhiteSpace(record.Source.PageUrl)) return record.Source.PageUrl;
        if (record.Source.Provider.Equals("workshop", StringComparison.OrdinalIgnoreCase))
        {
            var id = record.Source.RemoteId.Replace("workshop:", "").Trim();
            return $"https://steamcommunity.com/sharedfiles/filedetails/?id={id}";
        }
        if (record.Source.Provider.Equals("thunderstore", StringComparison.OrdinalIgnoreCase))
        {
            var parts = record.Source.RemoteId.Split(':');
            if (parts.Length >= 3)
            {
                return $"https://thunderstore.io/c/{parts[0]}/p/{parts[1]}/{parts[2]}/";
            }
            if (parts.Length == 2 && !string.IsNullOrWhiteSpace(ThunderstoreCommunity))
            {
                return $"https://thunderstore.io/c/{ThunderstoreCommunity}/p/{parts[0]}/{parts[1]}/";
            }
        }
        return null;
    }

    private ModListItem FromLegacy(ModItem mod)
    {
        var isBepInEx = mod.Category?.Contains("BepInEx", StringComparison.OrdinalIgnoreCase) == true ||
                        (!string.IsNullOrWhiteSpace(mod.FilePath) && mod.FilePath.IndexOf("BepInEx", StringComparison.OrdinalIgnoreCase) >= 0);

        var isWorkshop = mod.Category?.Contains("Workshop", StringComparison.OrdinalIgnoreCase) == true ||
                         (!string.IsNullOrWhiteSpace(mod.FilePath) && (
                             mod.FilePath.IndexOf("workshop", StringComparison.OrdinalIgnoreCase) >= 0 ||
                             mod.FilePath.IndexOf("Tabletop Simulator", StringComparison.OrdinalIgnoreCase) >= 0
                         ));

        string? pageUrl = null;
        if (isWorkshop)
        {
            var idCandidate = Path.GetFileNameWithoutExtension(mod.FilePath ?? mod.Id);
            if (ulong.TryParse(idCandidate, out var pubId) && pubId > 0)
            {
                pageUrl = $"https://steamcommunity.com/sharedfiles/filedetails/?id={pubId}";
            }
        }

        var sourceLabel = isWorkshop ? "Steam Workshop" : (isBepInEx ? "BepInEx" : T("String_ModSourceDetected", "Found on disk"));

        return new()
        {
            Id = mod.Id,
            IsBepInEx = isBepInEx,
            Name = mod.Name,
            Version = mod.Version,
            SourceLabel = sourceLabel,
            Detail = mod.Category,
            Author = !string.IsNullOrWhiteSpace(mod.Author) ? mod.Author : (isWorkshop ? "Steam Workshop" : T("String_ModSourceDetected", "Found on disk")),
            SizeBytes = 0,
            FormattedSize = mod.Category,
            IsEnabled = mod.IsEnabled,
            IsManaged = false,
            LegacyPath = mod.FilePath,
            PageUrl = pageUrl,
            IconUrl = ResolveLegacyModIcon(mod)
        };
    }

    private string? ResolveModIconUrl(InstalledModRecord record)
    {
        if (!string.IsNullOrWhiteSpace(record.Source.IconUrl)) return record.Source.IconUrl;

        // 1. If Workshop, check cache folder for thumbnail
        if (record.Source.Provider.Equals("workshop", StringComparison.OrdinalIgnoreCase))
        {
            var idStr = record.Source.RemoteId.Replace("workshop:", "").Trim();
            if (ulong.TryParse(idStr, out var fileId) && Instance?.AppId is { } appId && appId > 0)
            {
                var cacheDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "BlueStar", "data", "workshop_cache", appId.ToString(), fileId.ToString(), "content");
                if (Directory.Exists(cacheDir))
                {
                    var preview = FindImageInFolder(cacheDir);
                    if (preview != null) return preview;
                }
            }
        }

        // 2. If Thunderstore, check installed files for icon
        if (record.Source.Provider.Equals("thunderstore", StringComparison.OrdinalIgnoreCase) && Instance?.InstallPath != null)
        {
            var iconFile = record.Files.FirstOrDefault(f =>
                f.RelativePath.EndsWith("icon.png", StringComparison.OrdinalIgnoreCase) ||
                f.RelativePath.EndsWith("icon.jpg", StringComparison.OrdinalIgnoreCase) ||
                f.RelativePath.EndsWith("icon.webp", StringComparison.OrdinalIgnoreCase));
            if (iconFile != null)
            {
                var full = Path.Combine(Instance.InstallPath, iconFile.RelativePath);
                if (File.Exists(full)) return full;
            }
        }

        // 3. Fallback: check all mod files in instance install path for icon / preview
        if (Instance?.InstallPath != null && record.Files.Count > 0)
        {
            var candidate = record.Files.FirstOrDefault(f =>
                f.RelativePath.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
                f.RelativePath.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
                f.RelativePath.EndsWith(".webp", StringComparison.OrdinalIgnoreCase));
            if (candidate != null)
            {
                var full = Path.Combine(Instance.InstallPath, candidate.RelativePath);
                if (File.Exists(full)) return full;
            }
        }

        return null;
    }

    private static string? ResolveLegacyModIcon(ModItem mod)
    {
        if (!string.IsNullOrWhiteSpace(mod.IconPath) && File.Exists(mod.IconPath))
        {
            return mod.IconPath;
        }

        if (string.IsNullOrWhiteSpace(mod.FilePath)) return null;

        var ext = Path.GetExtension(mod.FilePath);
        if (ext.Equals(".png", StringComparison.OrdinalIgnoreCase) ||
            ext.Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
            ext.Equals(".jpeg", StringComparison.OrdinalIgnoreCase))
        {
            if (File.Exists(mod.FilePath)) return mod.FilePath;
        }

        if (Directory.Exists(mod.FilePath))
        {
            return FindImageInFolder(mod.FilePath);
        }

        var dir = Path.GetDirectoryName(mod.FilePath);
        if (dir != null && Directory.Exists(dir))
        {
            var baseName = Path.GetFileNameWithoutExtension(mod.FilePath);
            var siblingPng = Path.Combine(dir, $"{baseName}.png");
            if (File.Exists(siblingPng)) return siblingPng;
            var siblingJpg = Path.Combine(dir, $"{baseName}.jpg");
            if (File.Exists(siblingJpg)) return siblingJpg;
            var siblingJpeg = Path.Combine(dir, $"{baseName}.jpeg");
            if (File.Exists(siblingJpeg)) return siblingJpeg;
            var icon = Path.Combine(dir, "icon.png");
            if (File.Exists(icon)) return icon;
        }

        return null;
    }

    private static string? FindImageInFolder(string folder)
    {
        try
        {
            string[] names = ["icon.png", "icon.jpg", "icon.webp", "preview.png", "preview.jpg", "thumb.png", "thumb.jpg"];
            foreach (var n in names)
            {
                var p = Path.Combine(folder, n);
                if (File.Exists(p)) return p;
            }
            return Directory.EnumerateFiles(folder, "*.*", SearchOption.TopDirectoryOnly)
                .FirstOrDefault(f => f.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
                                     f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
                                     f.EndsWith(".webp", StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return null;
        }
    }

    private string ProviderLabel(string provider) => provider.ToLowerInvariant() switch
    {
        ModSourceRef.LocalProvider => T("String_ModSourceLocal", "Local file"),
        "workshop" => "Steam Workshop",
        "thunderstore" => "Thunderstore",
        "curseforge" => "CurseForge",
        "nexus" => "Nexus Mods",
        _ => provider
    };

    private static string FormatSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return $"{value:0.#} {units[unit]}";
    }

    private CancellationTokenSource? _statusCts;

    private void ShowStatus(string? message, bool isError)
    {
        _statusCts?.Cancel();
        _statusCts?.Dispose();
        _statusCts = null;

        StatusMessage = message;
        IsStatusError = isError;

        if (!string.IsNullOrEmpty(message))
        {
            var cts = new CancellationTokenSource();
            _statusCts = cts;
            _ = Task.Delay(4000, cts.Token).ContinueWith(t =>
            {
                if (!t.IsCanceled)
                {
                    OnUiThread(() =>
                    {
                        if (_statusCts == cts)
                        {
                            StatusMessage = null;
                            IsStatusError = false;
                        }
                    });
                }
            }, TaskScheduler.Default);
        }
    }

    private string T(string key, string fallback) => _localization?.GetString(key, fallback) ?? fallback;

    /// <summary>Runs <paramref name="action"/> on the WPF dispatcher, or inline when there is none (tests) or already on it.</summary>
    private static void OnUiThread(Action action)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.CheckAccess())
        {
            action();
            return;
        }

        dispatcher.Invoke(action);
    }

    private static string? DefaultPickModFile()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select Mod File or Package",
            Filter = "Mod packages (*.zip, *.rar, *.7z, *.pak, *.dll, *.esp, *.vpk)|*.zip;*.rar;*.7z;*.pak;*.ucas;*.utoc;*.dll;*.esp;*.esm;*.esl;*.vpk|All Files (*.*)|*.*"
        };

        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }
}

public sealed partial class ModpackModSelectionItem : ObservableObject
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string? Version { get; init; }
    public required string Source { get; init; }
    public string? IconUrl { get; init; }
    public string IconGeometryKey { get; init; } = "IconMod";

    [ObservableProperty]
    private bool _isSelected = true;
}
