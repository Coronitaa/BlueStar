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

    private CancellationTokenSource? _debounceCts;

    private CancellationTokenSource? _loadCts;



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

    private string? _selectedSort;



    public string SelectedSortDisplayName => SelectedSort switch

    {

        "Downloads" or "Descargas" => "Descargas",

        "Rating" or "Calificación" => "Calificación",

        "Updated" or "Actualizado" => "Actualizado",

        "Name" or "Nombre" => "Nombre",

        _ => "Ordenar"

    };



    public bool HasActiveSort => !string.IsNullOrWhiteSpace(SelectedSort);



    [ObservableProperty]

    private string _tagSearchText = string.Empty;



    partial void OnTagSearchTextChanged(string value) => OnPropertyChanged(nameof(FilteredTags));



    public ObservableCollection<ThunderstoreCategoryTag> AvailableTags { get; } = [];



    public IEnumerable<ThunderstoreCategoryTag> FilteredTags

    {

        get

        {

            if (string.IsNullOrWhiteSpace(TagSearchText))

                return AvailableTags;



            return AvailableTags.Where(t =>

                t.DisplayName.Contains(TagSearchText, StringComparison.OrdinalIgnoreCase) ||

                t.Name.Contains(TagSearchText, StringComparison.OrdinalIgnoreCase));

        }

    }



    public bool HasActiveCategoryFilter => AvailableTags.Any(t => t.State != TagFilterState.Neutral);



    [RelayCommand]

    public void ClearCategory()

    {

        foreach (var tag in AvailableTags)

        {

            tag.State = TagFilterState.Neutral;

        }

        OnPropertyChanged(nameof(HasActiveCategoryFilter));

        _ = LoadPackagesAsync();

    }





    [RelayCommand]

    public void ResetSort() => SelectedSort = null;



    [ObservableProperty]

    private bool _isSortDescending = true;



    [ObservableProperty]

    private bool _isLoading;



    [ObservableProperty]

    private string? _statusMessage;



    [ObservableProperty]

    private bool _isStatusError;



    [ObservableProperty]

    private bool _hasWorkshopSupport;



    public const int PageSize = 40;

    public const double PackageRowHeight = 112.0;

    public const int BufferRows = 3;



    private List<ThunderstorePackage> _allFilteredPackages = [];



    [ObservableProperty]

    private int _totalPackagesCount;



    [ObservableProperty]

    private bool _hasMorePackages;



    [ObservableProperty]

    private bool _isLoadingMore;



    [ObservableProperty]

    private double _topSpacerHeight;



    [ObservableProperty]

    private double _bottomSpacerHeight;



    private int _currentStartIndex = -1;

    private int _currentEndIndex = -1;

    private readonly Dictionary<int, ThunderstorePackageItem> _activeItems = new();

    private bool _isUpdatingWindow;



    public event Action? RequestScrollToTop;



    public ObservableCollection<ThunderstorePackageItem> Packages { get; } = [];

    public ObservableCollection<string> AvailableCategories { get; } = ["All"];

    public ObservableCollection<string> SortOptions { get; } = ["Descargas", "Calificación", "Actualizado", "Nombre"];



    public bool HasNoResults => !IsLoading && _allFilteredPackages.Count == 0;



    /// <summary>Allows switching to Steam Workshop from the browser header if the game supports both.</summary>

    public Action? SwitchToWorkshopAction { get; set; }



    /// <summary>Checks whether a Thunderstore package is already installed in the instance.</summary>

    public Func<ThunderstorePackage, (bool IsInstalled, string? Version)>? CheckInstalledStatus { get; set; }



    /// <summary>Called after a package successfully installs, e.g. to reload the Mods tab.</summary>

    public Func<Task>? OnModInstalledCallback { get; set; }



    /// <summary>Notification callback when the user or an action requests closing the modal.</summary>

    public Action? OnCloseRequested { get; set; }



    /// <summary>Notification callback when clicking on a package to view its web page.</summary>

    public Action<string, string>? OpenModPageAction { get; set; }



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

        SelectedSort = null;

        IsSortDescending = true;

        TagSearchText = string.Empty;

        AvailableTags.Clear();

        AvailableCategories.Clear();

        AvailableCategories.Add("All");

        StatusMessage = null;

        IsStatusError = false;

        TotalPackagesCount = 0;

        HasMorePackages = false;

        _currentStartIndex = -1;

        _currentEndIndex = -1;

        _activeItems.Clear();

        TopSpacerHeight = 0;

        BottomSpacerHeight = 0;

        Packages.Clear();

        IsOpen = true;



        _ = LoadPackagesAsync();

    }



    [RelayCommand]

    public void Close()

    {

        _debounceCts?.Cancel();

        _loadCts?.Cancel();

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

    public void ToggleSortDirection()

    {

        IsSortDescending = !IsSortDescending;

        _ = LoadPackagesAsync();

    }



    [RelayCommand]

    public void ExecuteSearch()

    {

        _debounceCts?.Cancel();

        _ = LoadPackagesAsync();

    }



    [RelayCommand]

    public void ToggleTag(ThunderstoreCategoryTag? tag)

    {

        if (tag == null) return;

        tag.State = tag.State switch

        {

            TagFilterState.Neutral => TagFilterState.Include,

            TagFilterState.Include => TagFilterState.Exclude,

            _ => TagFilterState.Neutral

        };

        OnPropertyChanged(nameof(HasActiveCategoryFilter));

        _ = LoadPackagesAsync();

    }



    [RelayCommand]

    public void ExcludeTag(ThunderstoreCategoryTag? tag)

    {

        if (tag == null) return;

        tag.State = tag.State == TagFilterState.Exclude

            ? TagFilterState.Neutral

            : TagFilterState.Exclude;

        OnPropertyChanged(nameof(HasActiveCategoryFilter));

        _ = LoadPackagesAsync();

    }



    [RelayCommand]

    public void SelectCategoryTag(string? category)

    {

        if (string.IsNullOrWhiteSpace(category)) return;

        var tag = AvailableTags.FirstOrDefault(t => t.Name.Equals(category, StringComparison.OrdinalIgnoreCase));

        if (tag != null)

        {

            ToggleTag(tag);

        }

    }



    [RelayCommand]

    public void OpenPackageDetail(ThunderstorePackageItem? item)

    {

        if (item == null) return;

        var url = item.PackageUrl;

        if (string.IsNullOrWhiteSpace(url))

        {

            url = $"https://thunderstore.io/c/{Community}/p/{item.Owner}/{item.Name}/";

        }

        OpenModPageAction?.Invoke(item.Name, url);

    }



    [RelayCommand]

    public void SwitchToWorkshop()

    {

        Close();

        SwitchToWorkshopAction?.Invoke();

    }



    partial void OnSearchTextChanged(string value) => DebouncedSearch();

    partial void OnSelectedCategoryChanged(string value) => _ = LoadPackagesAsync();

    partial void OnSelectedSortChanged(string? value)

    {

        OnPropertyChanged(nameof(SelectedSortDisplayName));

        OnPropertyChanged(nameof(HasActiveSort));

        _ = LoadPackagesAsync();

    }



    private void DebouncedSearch()

    {

        _debounceCts?.Cancel();

        _debounceCts = new CancellationTokenSource();

        var token = _debounceCts.Token;



        _ = Task.Run(async () =>

        {

            try

            {

                await Task.Delay(250, token).ConfigureAwait(false);

                if (token.IsCancellationRequested) return;



                if (System.Windows.Application.Current?.Dispatcher is { } dispatcher && !dispatcher.CheckAccess())

                {

                    await dispatcher.InvokeAsync(async () =>

                    {

                        if (!token.IsCancellationRequested)

                        {

                            await LoadPackagesAsync(token).ConfigureAwait(true);

                        }

                    });

                }

                else

                {

                    await LoadPackagesAsync(token).ConfigureAwait(true);

                }

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



        _loadCts?.Cancel();

        _loadCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        var token = _loadCts.Token;



        IsLoading = true;

        StatusMessage = null;

        OnPropertyChanged(nameof(HasNoResults));



        try

        {

            var sortByParam = SelectedSort switch

            {

                "Descargas" => "Downloads",

                "Calificación" => "Rating",

                "Actualizado" => "Updated",

                "Nombre" => "Name",

                _ => SelectedSort ?? "Relevance"

            };



            var included = AvailableTags.Where(t => t.State == TagFilterState.Include).Select(t => t.Name).ToList();

            var excluded = AvailableTags.Where(t => t.State == TagFilterState.Exclude).Select(t => t.Name).ToList();



            var results = await _thunderstoreService.SearchPackagesAsync(

                Community,

                SearchText,

                included.Count == 0 && excluded.Count == 0 ? SelectedCategory : null,

                sortByParam,

                !IsSortDescending,

                token,

                included,

                excluded).ConfigureAwait(true);



            if (token.IsCancellationRequested) return;



            _allFilteredPackages = results.ToList();

            TotalPackagesCount = _allFilteredPackages.Count;



            void UpdateCollections()

            {

                // Populate AvailableTags and AvailableCategories

                if (AvailableTags.Count <= 1 && _allFilteredPackages.Count > 0)

                {

                    AvailableTags.Clear();

                    AvailableCategories.Clear();

                    AvailableCategories.Add("All");



                    var tagCounts = _allFilteredPackages

                        .SelectMany(p => p.Categories)

                        .Where(c => !string.IsNullOrWhiteSpace(c))

                        .GroupBy(c => c, StringComparer.OrdinalIgnoreCase)

                        .Select(g => new { Name = g.Key, Count = g.Count() })

                        .OrderByDescending(g => g.Count)

                        .ThenBy(g => g.Name);



                    foreach (var tc in tagCounts)

                    {

                        AvailableCategories.Add(tc.Name);

                        AvailableTags.Add(new ThunderstoreCategoryTag

                        {

                            Name = tc.Name,

                            DisplayName = tc.Name,

                            Count = tc.Count,

                            State = TagFilterState.Neutral

                        });

                    }

                    OnPropertyChanged(nameof(FilteredTags));

                    OnPropertyChanged(nameof(HasActiveCategoryFilter));

                    OnPropertyChanged(nameof(HasActiveCategoryFilter));

                }



                _currentStartIndex = -1;

                _currentEndIndex = -1;

                _activeItems.Clear();

                UpdateScrollWindow(0, 650.0);

                HasMorePackages = false;

                RequestScrollToTop?.Invoke();

            }



            if (System.Windows.Application.Current?.Dispatcher is { } dispatcher && !dispatcher.CheckAccess())

            {

                dispatcher.Invoke(UpdateCollections);

            }

            else

            {

                UpdateCollections();

            }

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



    private static void RunOnUi(Action action)

    {

        if (System.Windows.Application.Current?.Dispatcher is { } d && !d.CheckAccess())

        {

            d.Invoke(action);

        }

        else

        {

            action();

        }

    }



    [RelayCommand]

    public void LoadMore()

    {

        // No-op: virtual scrolling window dynamically loads/unloads visible ranges

    }



    /// <summary>

    /// Updates the virtual sliding window to display up to 3 buffer rows above and 3 rows below the visible screen.

    /// Unloads out-of-range package items from memory while preserving the master search list so they can be reloaded instantly.

    /// </summary>

    public void UpdateScrollWindow(double verticalOffset, double viewportHeight)

    {

        if (_isUpdatingWindow) return;

        _isUpdatingWindow = true;

        try

        {

            int total = _allFilteredPackages.Count;

            if (total == 0)

            {

                TopSpacerHeight = 0;

                BottomSpacerHeight = 0;

                _currentStartIndex = -1;

                _currentEndIndex = -1;

                _activeItems.Clear();

                if (Packages.Count > 0)

                {

                    RunOnUi(() => Packages.Clear());

                }

                return;

            }



            if (viewportHeight <= 0)

            {

                viewportHeight = 650.0;

            }



            int visibleCount = (int)Math.Ceiling(viewportHeight / PackageRowHeight) + 1;

            int firstVisibleIndex = (int)(Math.Max(0, verticalOffset) / PackageRowHeight);

            firstVisibleIndex = Math.Clamp(firstVisibleIndex, 0, Math.Max(0, total - 1));



            // Max 3 buffer rows above, max 3 buffer rows below visible area

            int startIndex = Math.Max(0, firstVisibleIndex - BufferRows);

            int endIndex = Math.Min(total - 1, firstVisibleIndex + visibleCount + BufferRows);



            TopSpacerHeight = startIndex * PackageRowHeight;

            BottomSpacerHeight = Math.Max(0, (total - 1 - endIndex) * PackageRowHeight);



            if (startIndex == _currentStartIndex && endIndex == _currentEndIndex && Packages.Count == (endIndex - startIndex + 1))

            {

                return;

            }



            _currentStartIndex = startIndex;

            _currentEndIndex = endIndex;



            // Unload out-of-window items from memory

            var keysToRemove = _activeItems.Keys.Where(k => k < startIndex || k > endIndex).ToList();

            foreach (var k in keysToRemove)

            {

                _activeItems.Remove(k);

            }



            // Load newly visible items from _allFilteredPackages

            var neededItems = new List<ThunderstorePackageItem>(endIndex - startIndex + 1);

            for (int i = startIndex; i <= endIndex; i++)

            {

                if (!_activeItems.TryGetValue(i, out var item))

                {

                    item = CreatePackageItem(_allFilteredPackages[i]);

                    _activeItems[i] = item;

                }

                neededItems.Add(item);

            }



            RunOnUi(() =>

            {

                Packages.Clear();

                foreach (var item in neededItems)

                {

                    Packages.Add(item);

                }

            });

        }

        finally

        {

            _isUpdatingWindow = false;

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

        ReportStatus(string.Format(T("String_ModInstalling", "⏳ Installing {0}..."), item.Name), isError: false);



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

public enum TagFilterState
{
    Neutral,
    Include,
    Exclude
}

public sealed partial class ThunderstoreCategoryTag : ObservableObject
{
    public required string Name { get; init; }
    public required string DisplayName { get; init; }
    public int Count { get; init; }
    public string? CountText => Count > 0 ? Count.ToString() : null;

    [ObservableProperty]
    private TagFilterState _state = TagFilterState.Neutral;
}

