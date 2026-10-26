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

/// <summary>
/// State and logic of the Thunderstore Content Browser modal (inspired by Modrinth).
/// </summary>
public sealed partial class ThunderstoreBrowserViewModel : ObservableObject
{
    private readonly IThunderstoreService _thunderstoreService;
    private readonly Func<ModPackage, Task<ModInstallResult?>> _installPackage;
    private readonly ILocalizationService? _localization;
    private readonly ILogger _logger;
    private CancellationTokenSource? _searchCts;

    public ThunderstoreBrowserViewModel(
        IThunderstoreService thunderstoreService,
        Func<ModPackage, Task<ModInstallResult?>> installPackage,
        ILocalizationService? localization,
        ILogger logger)
    {
        _thunderstoreService = thunderstoreService ?? throw new ArgumentNullException(nameof(thunderstoreService));
        _installPackage = installPackage ?? throw new ArgumentNullException(nameof(installPackage));
        _localization = localization;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    [ObservableProperty]
    private bool _isOpen;

    [ObservableProperty]
    private string? _community;

    [ObservableProperty]
    private string? _instanceName;

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private string _selectedCategory = "All";

    [ObservableProperty]
    private string _selectedSort = "Downloads";

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string? _statusMessage;

    [ObservableProperty]
    private bool _isStatusError;

    [ObservableProperty]
    private bool _hasWorkshopSupport;

    public const int PageSize = 40;
    private List<ThunderstorePackage> _allFilteredPackages = [];

    [ObservableProperty]
    private int _totalPackagesCount;

    [ObservableProperty]
    private bool _hasMorePackages;

    [ObservableProperty]
    private bool _isLoadingMore;

    public ObservableCollection<ThunderstorePackageItem> Packages { get; } = [];
    public ObservableCollection<string> AvailableCategories { get; } = ["All"];
    public ObservableCollection<string> SortOptions { get; } = ["Downloads", "Rating", "Updated", "Name"];

    public bool HasNoResults => !IsLoading && Packages.Count == 0;

    /// <summary>Allows switching to Steam Workshop from the browser header if the game supports both.</summary>
    public Action? SwitchToWorkshopAction { get; set; }

    /// <summary>Checks whether a Thunderstore package is already installed in the instance.</summary>
    public Func<ThunderstorePackage, (bool IsInstalled, string? Version)>? CheckInstalledStatus { get; set; }

    /// <summary>Called after a package successfully installs, e.g. to reload the Mods tab.</summary>
    public Func<Task>? OnModInstalledCallback { get; set; }

    /// <summary>Notification callback when the user or an action requests closing the modal.</summary>
    public Action? OnCloseRequested { get; set; }

    public void Open(
        GameInstance instance,
        string community,
        bool hasWorkshop = false,
        Action? switchToWorkshop = null)
    {
        ArgumentNullException.ThrowIfNull(instance);
        Community = community;
        InstanceName = instance.Name;
        HasWorkshopSupport = hasWorkshop;
        SwitchToWorkshopAction = switchToWorkshop;
        SearchText = string.Empty;
        SelectedCategory = "All";
        SelectedSort = "Downloads";
        StatusMessage = null;
        IsStatusError = false;
        TotalPackagesCount = 0;
        HasMorePackages = false;
        IsOpen = true;

        _ = LoadPackagesAsync();
    }

    [RelayCommand]
    public void Close()
    {
        _searchCts?.Cancel();
        if (!IsOpen) return;
        IsOpen = false;
        OnCloseRequested?.Invoke();
    }

    [RelayCommand]
    public void ClearSearch()
    {
        SearchText = string.Empty;
    }

    [RelayCommand]
    public void SwitchToWorkshop()
    {
        Close();
        SwitchToWorkshopAction?.Invoke();
    }

    partial void OnSearchTextChanged(string value) => DebouncedSearch();
    partial void OnSelectedCategoryChanged(string value) => _ = LoadPackagesAsync();
    partial void OnSelectedSortChanged(string value) => _ = LoadPackagesAsync();

    private void DebouncedSearch()
    {
        _searchCts?.Cancel();
        _searchCts = new CancellationTokenSource();
        var token = _searchCts.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(250, token).ConfigureAwait(false);
                if (token.IsCancellationRequested) return;
                await LoadPackagesAsync(token).ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                // Expected on fast typing
            }
        }, token);
    }

    [RelayCommand]
    public async Task LoadPackagesAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(Community)) return;

        _searchCts?.Cancel();
        _searchCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var token = _searchCts.Token;

        IsLoading = true;
        StatusMessage = null;
        OnPropertyChanged(nameof(HasNoResults));

        try
        {
            var results = await _thunderstoreService.SearchPackagesAsync(
                Community,
                SearchText,
                SelectedCategory,
                SelectedSort,
                token).ConfigureAwait(true);

            if (token.IsCancellationRequested) return;

            _allFilteredPackages = results.ToList();
            TotalPackagesCount = _allFilteredPackages.Count;

            // Populate categories once if needed
            if (AvailableCategories.Count <= 1 && _allFilteredPackages.Count > 0)
            {
                var cats = _allFilteredPackages
                    .SelectMany(p => p.Categories)
                    .Where(c => !string.IsNullOrWhiteSpace(c))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(c => c);

                foreach (var cat in cats)
                {
                    if (!AvailableCategories.Contains(cat))
                    {
                        AvailableCategories.Add(cat);
                    }
                }
            }

            Packages.Clear();
            var firstPage = _allFilteredPackages.Take(PageSize);
            foreach (var pkg in firstPage)
            {
                Packages.Add(CreatePackageItem(pkg));
            }

            HasMorePackages = Packages.Count < _allFilteredPackages.Count;
        }
        catch (OperationCanceledException)
        {
            // Cancelled
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to search Thunderstore packages for {Community}", Community);
            ReportStatus(string.Format(T("String_ThunderstoreSearchFailed", "❌ Could not load packages: {0}"), ex.Message), isError: true);
        }
        finally
        {
            IsLoading = false;
            OnPropertyChanged(nameof(HasNoResults));
        }
    }

    [RelayCommand]
    public void LoadMore()
    {
        if (!HasMorePackages || IsLoadingMore || IsLoading) return;

        IsLoadingMore = true;
        try
        {
            var nextBatch = _allFilteredPackages
                .Skip(Packages.Count)
                .Take(PageSize);

            foreach (var pkg in nextBatch)
            {
                Packages.Add(CreatePackageItem(pkg));
            }

            HasMorePackages = Packages.Count < _allFilteredPackages.Count;
        }
        finally
        {
            IsLoadingMore = false;
        }
    }

    private ThunderstorePackageItem CreatePackageItem(ThunderstorePackage pkg)
    {
        var item = new ThunderstorePackageItem(pkg, InstallItemAsync);
        if (CheckInstalledStatus != null)
        {
            var (isInst, ver) = CheckInstalledStatus(pkg);
            item.IsInstalled = isInst;
            item.InstalledVersion = ver;
        }
        return item;
    }

    public async Task InstallItemAsync(ThunderstorePackageItem item)
    {
        if (string.IsNullOrWhiteSpace(Community)) return;

        item.IsInstalling = true;
        item.DownloadProgress = 0;
        ReportStatus(string.Format(T("String_ModInstalling", "📦 Installing {0}..."), item.Name), isError: false);

        try
        {
            var progress = new Progress<double>(p => item.DownloadProgress = p);
            var modPackage = await _thunderstoreService.DownloadPackageAsync(
                Community,
                item.Package,
                item.SelectedVersion,
                progress,
                CancellationToken.None).ConfigureAwait(true);

            if (modPackage == null)
            {
                ReportStatus(string.Format(T("String_ThunderstoreDownloadFailed", "❌ Could not download package {0}."), item.Name), isError: true);
                return;
            }

            var result = await _installPackage(modPackage).ConfigureAwait(true);
            if (result?.Status == ModInstallStatus.Installed)
            {
                item.IsInstalled = true;
                item.InstalledVersion = item.SelectedVersion?.VersionNumber;
                ReportStatus(string.Format(T("String_ModInstalled", "✅ {0} installed ({1} files)."), item.Name, result.Record?.Files.Count ?? 0), isError: false);

                if (OnModInstalledCallback != null)
                {
                    await OnModInstalledCallback().ConfigureAwait(true);
                }
            }
            else if (result?.Status == ModInstallStatus.NeedsConfirmation)
            {
                // Confirmation required: close browser to reveal confirmation
                IsOpen = false;
            }
            else
            {
                ReportStatus(T("String_ModInstallFailedGeneric", "❌ Failed to install mod."), isError: true);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to install Thunderstore package {Package}", item.FullName);
            ReportStatus(string.Format(T("String_ModInstallFailed", "❌ Could not install {0}: {1}"), item.Name, ex.Message), isError: true);
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
