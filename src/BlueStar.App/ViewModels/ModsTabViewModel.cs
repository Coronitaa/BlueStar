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

namespace BlueStar.App.ViewModels;

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
    private ModPackage? _pendingPackage;

    public ModsTabViewModel(
        IModInstallPipeline? pipeline,
        IModManagerRegistry? legacyRegistry,
        ILocalizationService? localization,
        ILogger logger,
        IWorkshopModFetcher? workshopFetcher = null,
        IWorkshopService? workshopService = null)
    {
        _pipeline = pipeline;
        _legacyRegistry = legacyRegistry;
        _localization = localization;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _workshopFetcher = workshopFetcher;
        _workshopService = workshopService;
        Workshop = new WorkshopBrowserViewModel(InstallWorkshopItemAsync);
    }

    private readonly IWorkshopModFetcher? _workshopFetcher;
    private readonly IWorkshopService? _workshopService;

    /// <summary>Steam Workshop browser modal of this tab.</summary>
    public WorkshopBrowserViewModel Workshop { get; }

    [RelayCommand]
    public void OpenWorkshop()
    {
        if (Instance == null) return;
        Workshop.Open(Instance.AppId);
    }

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

    [ObservableProperty]
    private string _selectedFilter = "All";

    // ── Mod Engine / Loader info (e.g. BepInEx, UE4SS, Native) ──
    [ObservableProperty]
    private string? _engineName;

    [ObservableProperty]
    private string? _engineVersion;

    [ObservableProperty]
    private bool _isEngineInstalled;

    [ObservableProperty]
    private bool _isEngineSupported;

    /// <summary>Invokes engine installation from parent ViewModel if available.</summary>
    public Func<Task>? InstallEngineAction { get; set; }

    [RelayCommand]
    public async Task InstallEngineAsync()
    {
        if (InstallEngineAction != null)
        {
            await InstallEngineAction().ConfigureAwait(true);
        }
    }

    public void UpdateEngineStatus(string? name, string? version, bool isInstalled, bool isSupported)
    {
        EngineName = name;
        EngineVersion = version;
        IsEngineInstalled = isInstalled;
        IsEngineSupported = isSupported;
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

    public int TotalModsCount => Items.Count;
    public int ActiveModsCount => Items.Count(i => i.IsEnabled);
    public int DisabledModsCount => Items.Count(i => !i.IsEnabled);
    public int WorkshopModsCount => Items.Count(i => i.SourceLabel.Contains("Workshop", StringComparison.OrdinalIgnoreCase));
    public int LocalModsCount => Items.Count(i => !i.SourceLabel.Contains("Workshop", StringComparison.OrdinalIgnoreCase));
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
                "Local" => !item.SourceLabel.Contains("Workshop", StringComparison.OrdinalIgnoreCase),
                _ => true
            };
            if (!matchesCategory) return false;

            if (string.IsNullOrWhiteSpace(query)) return true;
            return item.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                   (item.Version != null && item.Version.Contains(query, StringComparison.OrdinalIgnoreCase)) ||
                   (item.Author != null && item.Author.Contains(query, StringComparison.OrdinalIgnoreCase)) ||
                   (item.Detail != null && item.Detail.Contains(query, StringComparison.OrdinalIgnoreCase)) ||
                   item.SourceLabel.Contains(query, StringComparison.OrdinalIgnoreCase);
        }).ToList();

        OnUiThread(() =>
        {
            FilteredItems.Clear();
            foreach (var item in filtered) FilteredItems.Add(item);
            OnPropertyChanged(nameof(TotalModsCount));
            OnPropertyChanged(nameof(ActiveModsCount));
            OnPropertyChanged(nameof(DisabledModsCount));
            OnPropertyChanged(nameof(WorkshopModsCount));
            OnPropertyChanged(nameof(LocalModsCount));
            OnPropertyChanged(nameof(HasNoSearchResults));
            OnPropertyChanged(nameof(StatsSummary));
        });
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
    }

    [RelayCommand]
    public async Task LoadAsync()
    {
        var instance = Instance;
        if (instance == null || string.IsNullOrWhiteSpace(instance.InstallPath)) return;

        try
        {
            var legacy = _legacyRegistry?.GetManagerForInstance(instance);
            ModsDirectoryPath = legacy?.GetModsDirectory(instance) ?? instance.InstallPath;

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
                Items.Clear();
                foreach (var row in rows) Items.Add(row);
                RefreshFilteredItems();
            });

            ModsChanged?.Invoke(this, EventArgs.Empty);
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

    [RelayCommand]
    public async Task ToggleAsync(ModListItem? item)
    {
        var instance = Instance;
        if (instance == null || item == null) return;

        var enable = !item.IsEnabled;
        bool ok;
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
                ok = legacy != null &&
                     await legacy.ToggleModAsync(instance, item.Id, enable, CancellationToken.None).ConfigureAwait(true);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to toggle mod {ModId}", item.Id);
            ok = false;
        }

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

    [RelayCommand]
    public void OpenModsFolder()
    {
        var target = !string.IsNullOrWhiteSpace(ModsDirectoryPath) ? ModsDirectoryPath : Instance?.InstallPath;
        if (string.IsNullOrWhiteSpace(target)) return;

        try
        {
            Directory.CreateDirectory(target);
            Process.Start(new ProcessStartInfo { FileName = target, UseShellExecute = true });
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
        var managedFiles = ledger.Mods
            .SelectMany(m => m.Files)
            .Select(f => Path.GetFullPath(Path.Combine(installPath, f.RelativePath)))
            .ToList();
        if (managedFiles.Count == 0) return scanned;

        return scanned.Where(mod =>
        {
            if (string.IsNullOrWhiteSpace(mod.FilePath)) return true;
            var path = Path.GetFullPath(mod.FilePath).TrimEnd(Path.DirectorySeparatorChar);
            return !managedFiles.Any(f =>
                f.Equals(path, StringComparison.OrdinalIgnoreCase) ||
                f.StartsWith(path + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
        });
    }

    private ModListItem FromRecord(InstalledModRecord record) => new()
    {
        Id = record.Id,
        Name = record.Name,
        Version = record.Version,
        SourceLabel = ProviderLabel(record.Source.Provider),
        Detail = string.Format(T("String_ModManagedDetail", "{0} files · {1}"), record.Files.Count, FormatSize(record.TotalSizeBytes)),
        Author = record.Source.Provider.Equals("workshop", StringComparison.OrdinalIgnoreCase)
            ? $"Steam Workshop #{record.Source.LedgerId.Replace("workshop:", "")}"
            : ProviderLabel(record.Source.Provider),
        SizeBytes = record.TotalSizeBytes,
        FormattedSize = FormatSize(record.TotalSizeBytes),
        IsEnabled = record.IsEnabled,
        IsManaged = true
    };

    private ModListItem FromLegacy(ModItem mod) => new()
    {
        Id = mod.Id,
        Name = mod.Name,
        Version = mod.Version,
        SourceLabel = T("String_ModSourceDetected", "Found on disk"),
        Detail = mod.Category,
        Author = !string.IsNullOrWhiteSpace(mod.Author) ? mod.Author : T("String_ModSourceDetected", "Found on disk"),
        SizeBytes = 0,
        FormattedSize = mod.Category,
        IsEnabled = mod.IsEnabled,
        IsManaged = false,
        LegacyPath = mod.FilePath
    };

    private string ProviderLabel(string provider) => provider.ToLowerInvariant() switch
    {
        ModSourceRef.LocalProvider => T("String_ModSourceLocal", "Local file"),
        "workshop" => "Steam Workshop",
        "thunderstore" => "Thunderstore",
        "curseforge" => "CurseForge",
        "nexus" => "Nexus Mods",
        "modio" => "mod.io",
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

    private void ShowStatus(string? message, bool isError)
    {
        StatusMessage = message;
        IsStatusError = isError;
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
