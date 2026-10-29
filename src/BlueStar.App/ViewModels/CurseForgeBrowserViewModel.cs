using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BlueStar.Core.Interfaces;
using BlueStar.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace BlueStar.App.ViewModels;

public sealed partial class CurseForgeBrowserViewModel : ObservableObject
{
    private readonly ICurseForgeService _curseForgeService;
    private readonly Func<ModPackage, Task<ModInstallResult?>> _installPackage;
    private readonly ILocalizationService? _localization;
    private readonly ILogger _logger;

    private GameInstance? _instance;
    private CancellationTokenSource? _loadCts;
    private CancellationTokenSource? _searchCts;
    private bool _isInitializing;

    public CurseForgeBrowserViewModel(
        ICurseForgeService curseForgeService,
        Func<ModPackage, Task<ModInstallResult?>> installPackage,
        ILocalizationService? localization,
        ILogger logger)
    {
        _curseForgeService = curseForgeService ?? throw new ArgumentNullException(nameof(curseForgeService));
        _installPackage = installPackage ?? throw new ArgumentNullException(nameof(installPackage));
        _localization = localization;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    [ObservableProperty]
    private int _gameId;

    [ObservableProperty]
    private string? _instanceName;

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private string _tagSearchText = string.Empty;

    [ObservableProperty]
    private CurseForgeCategory? _selectedCategory;

    [ObservableProperty]
    private string _selectedSort = "Popularidad";

    public IReadOnlyList<string> SortOptions { get; } = ["Popularidad", "Descargas", "Actualizado", "Nombre"];

    public ObservableCollection<CurseForgeModItem> Mods { get; } = [];
    public ObservableCollection<CurseForgeCategory> Categories { get; } = [];
    public ObservableCollection<CurseForgeCategory> FilteredCategories { get; } = [];

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private bool _isLoadingMore;

    [ObservableProperty]
    private bool _hasNoResults;

    [ObservableProperty]
    private int _totalCount;

    public bool CanLoadMore => !IsLoading && !IsLoadingMore && Mods.Count > 0 && (TotalCount <= 0 || Mods.Count < TotalCount);

    [ObservableProperty]
    private string? _statusMessage;

    [ObservableProperty]
    private bool _isStatusError;

    public Func<CurseForgeMod, (bool IsInstalled, string? Version)>? CheckInstalledStatus { get; set; }
    public Func<Task>? OnModInstalledCallback { get; set; }

    public bool HasActiveCategoryFilter => SelectedCategory != null;

    public void Open(GameInstance instance, int gameId)
    {
        _isInitializing = true;
        _instance = instance;
        GameId = gameId;
        InstanceName = instance.Name;
        SearchText = string.Empty;
        TagSearchText = string.Empty;
        SelectedCategory = null;
        TotalCount = 0;
        StatusMessage = null;
        IsStatusError = false;
        OnPropertyChanged(nameof(CanLoadMore));

        _ = LoadInitialAsync();
    }

    public void Close()
    {
        _loadCts?.Cancel();
        _loadCts?.Dispose();
        _loadCts = null;

        _searchCts?.Cancel();
        _searchCts?.Dispose();
        _searchCts = null;

        Mods.Clear();
        TotalCount = 0;
        OnPropertyChanged(nameof(CanLoadMore));
    }

    private async Task LoadInitialAsync()
    {
        _searchCts?.Cancel();
        _loadCts?.Cancel();
        _loadCts = new CancellationTokenSource();
        var ct = _loadCts.Token;

        IsLoading = true;
        HasNoResults = false;
        Mods.Clear();
        Categories.Clear();
        FilteredCategories.Clear();

        try
        {
            var categories = await _curseForgeService.GetCategoriesAsync(GameId, ct).ConfigureAwait(true);
            foreach (var cat in categories)
            {
                Categories.Add(cat);
                FilteredCategories.Add(cat);
            }

            var result = await _curseForgeService.SearchModsWithCountAsync(GameId, sortBy: SelectedSort, ct: ct).ConfigureAwait(true);
            TotalCount = result.TotalCount;
            foreach (var m in result.Mods)
            {
                Mods.Add(CreateModItem(m));
            }

            HasNoResults = Mods.Count == 0;
            OnPropertyChanged(nameof(CanLoadMore));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load CurseForge mods for game {GameId}", GameId);
            ReportStatus("Error al cargar mods de CurseForge.", isError: true);
        }
        finally
        {
            _isInitializing = false;
            IsLoading = false;
        }
    }

    partial void OnTagSearchTextChanged(string value)
    {
        FilteredCategories.Clear();
        var filter = value?.Trim() ?? string.Empty;
        foreach (var cat in Categories)
        {
            if (string.IsNullOrWhiteSpace(filter) || cat.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
            {
                FilteredCategories.Add(cat);
            }
        }
    }

    partial void OnSelectedCategoryChanged(CurseForgeCategory? value)
    {
        OnPropertyChanged(nameof(HasActiveCategoryFilter));
        if (!_isInitializing)
        {
            _ = ExecuteSearchAsync();
        }
    }

    partial void OnSelectedSortChanged(string value)
    {
        if (!_isInitializing)
        {
            _ = ExecuteSearchAsync();
        }
    }

    [RelayCommand]
    public void SelectCategory(CurseForgeCategory? cat)
    {
        SelectedCategory = SelectedCategory == cat ? null : cat;
    }

    [RelayCommand]
    public void ClearCategory()
    {
        SelectedCategory = null;
    }

    [RelayCommand]
    public void ClearSearch()
    {
        SearchText = string.Empty;
        _ = ExecuteSearchAsync();
    }

    [RelayCommand]
    public async Task ExecuteSearchAsync()
    {
        _loadCts?.Cancel();
        _searchCts?.Cancel();
        _searchCts = new CancellationTokenSource();
        var ct = _searchCts.Token;

        IsLoading = true;
        HasNoResults = false;
        Mods.Clear();

        try
        {
            var result = await _curseForgeService.SearchModsWithCountAsync(
                GameId,
                query: SearchText,
                categoryId: SelectedCategory?.Id,
                sortBy: SelectedSort,
                ct: ct).ConfigureAwait(true);

            TotalCount = result.TotalCount;
            foreach (var m in result.Mods)
            {
                Mods.Add(CreateModItem(m));
            }

            HasNoResults = Mods.Count == 0;
            OnPropertyChanged(nameof(CanLoadMore));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Search failed for CurseForge game {GameId}", GameId);
            ReportStatus("Error en la búsqueda de CurseForge.", isError: true);
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    public async Task LoadMoreAsync()
    {
        if (IsLoading || IsLoadingMore || Mods.Count == 0) return;

        IsLoadingMore = true;
        try
        {
            var result = await _curseForgeService.SearchModsWithCountAsync(
                GameId,
                query: SearchText,
                categoryId: SelectedCategory?.Id,
                sortBy: SelectedSort,
                index: Mods.Count,
                pageSize: 50).ConfigureAwait(true);

            TotalCount = result.TotalCount;
            foreach (var m in result.Mods)
            {
                if (!Mods.Any(existing => existing.Id == m.Id))
                {
                    Mods.Add(CreateModItem(m));
                }
            }
            OnPropertyChanged(nameof(CanLoadMore));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load more mods for CurseForge game {GameId}", GameId);
        }
        finally
        {
            IsLoadingMore = false;
        }
    }

    private CurseForgeModItem CreateModItem(CurseForgeMod mod)
    {
        var item = new CurseForgeModItem(mod, InstallItemAsync);
        if (CheckInstalledStatus != null)
        {
            var (isInst, ver) = CheckInstalledStatus(mod);
            item.IsInstalled = isInst;
            item.InstalledVersion = ver;
        }
        return item;
    }

    public async Task InstallItemAsync(CurseForgeModItem item)
    {
        item.IsInstalling = true;
        item.DownloadProgress = 0;
        ReportStatus(string.Format(T("String_ModInstalling", "⏳ Instalando {0}..."), item.Name), isError: false);

        try
        {
            var progress = new Progress<double>(p => item.DownloadProgress = p);
            var modPackage = await _curseForgeService.DownloadModAsync(
                GameId,
                item.Mod,
                item.SelectedFile,
                progress).ConfigureAwait(true);

            if (modPackage == null)
            {
                ReportStatus(string.Format(T("String_CurseForgeDownloadFailed", "❌ No se pudo descargar {0} de CurseForge."), item.Name), isError: true);
                return;
            }

            var result = await _installPackage(modPackage).ConfigureAwait(true);
            if (result?.Status == ModInstallStatus.Installed)
            {
                item.IsInstalled = true;
                item.InstalledVersion = item.SelectedFile?.DisplayName;
                ReportStatus(string.Format(T("String_ModInstalled", "✅ {0} instalado ({1} archivos)."), item.Name, result.Record?.Files.Count ?? 0), isError: false);

                if (OnModInstalledCallback != null)
                {
                    await OnModInstalledCallback().ConfigureAwait(true);
                }
            }
            else if (result?.Status == ModInstallStatus.NeedsConfirmation)
            {
                // Confirmation dialog will be handled by tab
            }
            else
            {
                ReportStatus(result?.Error ?? T("String_ModInstallFailedGeneric", "❌ Error al instalar mod."), isError: true);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to install CurseForge mod {Mod}", item.Name);
            ReportStatus(string.Format(T("String_ModInstallFailed", "❌ No se pudo instalar {0}: {1}"), item.Name, ex.Message), isError: true);
        }
        finally
        {
            item.IsInstalling = false;
        }
    }

    public void ReportStatus(string? message, bool isError)
    {
        StatusMessage = message;
        IsStatusError = isError;
    }

    private string T(string key, string fallback) => _localization?.GetString(key, fallback) ?? fallback;
}
