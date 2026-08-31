using System;
using System.Linq;
using System.Threading;
using System.Windows.Controls;
using BlueStar.App.Views;
using BlueStar.Core.Interfaces;
using BlueStar.Core.Models;
using BlueStar.Infrastructure.Downloader;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;

namespace BlueStar.App.ViewModels;

/// <summary>
/// Main window ViewModel controlling AppShell navigation, active downloads, real-time Steam status, and toast notifications.
/// </summary>
public partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly DownloadQueueManager _downloadQueueManager;
    private readonly ISteamStatusService _steamStatusService;
    private readonly INotificationService _notificationService;
    private readonly IUpdateService _updateService;
    private readonly IInstanceManager _instanceManager;
    private readonly IGameLauncher? _gameLauncher;
    private readonly IDepotBoxApiClient? _apiClient;
    private readonly IBackgroundTaskService _backgroundTaskService;
    private readonly SynchronizationContext _uiContext;
    private readonly CancellationTokenSource _cts = new();
    private bool _isDisposed;

    private readonly System.Collections.Specialized.NotifyCollectionChangedEventHandler _queueCollectionChangedHandler;
    private readonly EventHandler _queueChangedHandler;
    private readonly EventHandler<SteamStatus> _steamStatusChangedHandler;
    private readonly EventHandler _tasksChangedHandler;
    private readonly EventHandler _instancesChangedHandler;
    private readonly EventHandler<(Guid InstanceId, bool IsRunning)>? _runningStateChangedHandler;

    public IBackgroundTaskService BackgroundTaskService => _backgroundTaskService;

    [ObservableProperty]
    private bool _hasActiveTasks;

    [ObservableProperty]
    private int _activeTasksCount;

    [ObservableProperty]
    private string _backgroundTaskStatusText = string.Empty;

    [ObservableProperty]
    private double _totalTasksProgressPercentage;

    [ObservableProperty]
    private bool _hasMultipleActiveTasks;

    [ObservableProperty]
    private System.Collections.ObjectModel.ObservableCollection<BackgroundTaskItem> _activeBackgroundTasks = new();

    [ObservableProperty]
    private bool _isTasksFlyoutOpen;

    public System.Collections.ObjectModel.ReadOnlyObservableCollection<NotificationItem> Notifications => _notificationService.Notifications;

    [ObservableProperty]
    private UserControl? _currentView;

    partial void OnCurrentViewChanged(UserControl? oldValue, UserControl? newValue)
    {
        if (oldValue?.DataContext is IDisposable oldDisposable && !ReferenceEquals(oldDisposable, newValue?.DataContext))
        {
            try
            {
                oldDisposable.Dispose();
            }
            catch { }
        }
    }

    [ObservableProperty]
    private string _selectedNavigation = "Home";

    [ObservableProperty]
    private string _statusText = "Ready";

    [ObservableProperty]
    private int _activeDownloadsCount;

    [ObservableProperty]
    private bool _hasActiveDownloads;

    [ObservableProperty]
    private int _pausedDownloadsCount;

    [ObservableProperty]
    private bool _hasPausedDownloads;

    [ObservableProperty]
    private int _totalPendingDownloadsCount;

    [ObservableProperty]
    private bool _hasPendingOrActiveDownloads;

    // ── App Startup Loading State ──
    [ObservableProperty]
    private bool _isLoading = true;

    [ObservableProperty]
    private string _startupStatusText = "Starting BlueStar...";

    // ── Sidebar State & Recent Shortcuts ──
    [ObservableProperty]
    private System.Collections.ObjectModel.ObservableCollection<GameInstance> _recentShortcuts = new();

    [ObservableProperty]
    private bool _isSidebarImportMenuOpen;

    // ── Live Downloads Tracker ──
    [ObservableProperty]
    private string _downloadsButtonText = "Downloads";

    [ObservableProperty]
    private bool _hasMultipleActiveDownloads;

    // ── Steam Live Status ──
    [ObservableProperty]
    private bool _isSteamRunning;

    [ObservableProperty]
    private string _steamPersonaName = string.Empty;

    [ObservableProperty]
    private string _steamStatusText = "Steam: Checking...";

    [ObservableProperty]
    private string _steamTooltipText = "Steam client status";

    private readonly IMetadataProvider? _metadataProvider;
    private readonly IPrerequisiteService? _prerequisiteService;
    private readonly BlueStar.Infrastructure.Storage.AppSettingsService? _appSettings;

    // ── System Requirements State ──
    [ObservableProperty]
    private bool _isSystemRequirementsModalOpen;

    [ObservableProperty]
    private System.Collections.ObjectModel.ObservableCollection<PrerequisiteItem> _systemPrerequisites = [];

    [ObservableProperty]
    private bool _isScanningSystemPrerequisites;

    [ObservableProperty]
    private bool _isInstallingSystemPrerequisites;

    [ObservableProperty]
    private string _systemPrerequisitesInstallStatusText = string.Empty;

    [ObservableProperty]
    private bool _checkRequirementsOnStartup = true;

    [ObservableProperty]
    private int _missingPrerequisitesCount;

    [ObservableProperty]
    private bool _hasMissingRequirements;

    partial void OnCheckRequirementsOnStartupChanged(bool value)
    {
        if (_appSettings != null)
        {
            _ = _appSettings.SetCheckSystemRequirementsOnStartupAsync(value);
        }
    }

    public MainViewModel(
        DownloadQueueManager downloadQueueManager,
        ISteamStatusService steamStatusService,
        INotificationService notificationService,
        IUpdateService updateService,
        IInstanceManager instanceManager,
        IBackgroundTaskService backgroundTaskService,
        IGameLauncher? gameLauncher = null,
        IDepotBoxApiClient? apiClient = null,
        IMetadataProvider? metadataProvider = null,
        IPrerequisiteService? prerequisiteService = null,
        BlueStar.Infrastructure.Storage.AppSettingsService? appSettings = null)
    {
        _downloadQueueManager = downloadQueueManager;
        _steamStatusService = steamStatusService;
        _notificationService = notificationService;
        _updateService = updateService;
        _instanceManager = instanceManager;
        _backgroundTaskService = backgroundTaskService;
        _gameLauncher = gameLauncher;
        _apiClient = apiClient;
        _metadataProvider = metadataProvider;
        _prerequisiteService = prerequisiteService;
        _appSettings = appSettings;
        _uiContext = SynchronizationContext.Current ?? new SynchronizationContext();

        if (_appSettings != null)
        {
            CheckRequirementsOnStartup = _appSettings.CheckSystemRequirementsOnStartup;
        }

        _queueCollectionChangedHandler = (_, _) => _uiContext.Post(_ => { UpdateDownloadStats(); UpdateBackgroundTaskStats(); }, null);
        _queueChangedHandler = (_, _) => _uiContext.Post(_ => { UpdateDownloadStats(); UpdateBackgroundTaskStats(); }, null);
        _steamStatusChangedHandler = OnSteamStatusChanged;
        _tasksChangedHandler = (_, _) => _uiContext.Post(_ => UpdateBackgroundTaskStats(), null);
        _instancesChangedHandler = (_, _) => _ = RefreshRecentShortcutsAsync();

        _downloadQueueManager.Queue.CollectionChanged += _queueCollectionChangedHandler;
        _downloadQueueManager.QueueChanged += _queueChangedHandler;
        _steamStatusService.StatusChanged += _steamStatusChangedHandler;
        _backgroundTaskService.TasksChanged += _tasksChangedHandler;
        _instanceManager.InstancesChanged += _instancesChangedHandler;

        if (_gameLauncher != null)
        {
            _runningStateChangedHandler = (_, _) => _ = RefreshRecentShortcutsAsync();
            _gameLauncher.RunningStateChanged += _runningStateChangedHandler;
        }

        // Initialize state
        UpdateDownloadStats();
        UpdateSteamProperties(_steamStatusService.CurrentStatus);

        // Default navigation landing page is Home Dashboard
        Navigate("Home");

        // Handle initial startup loading & update check
        _ = InitializeStartupAsync();
    }

    private async Task InitializeStartupAsync()
    {
        try
        {
            StartupStatusText = "Loading local instances and manifests...";
            await _instanceManager.GetAllAsync(CancellationToken.None).ConfigureAwait(true);

            StartupStatusText = "Analyzing system requirements...";
            await ScanSystemRequirementsAsync(autoPromptModal: true).ConfigureAwait(true);

            StartupStatusText = "Ready!";

            // Trigger smooth exit transition
            IsLoading = false;
            await RefreshRecentShortcutsAsync().ConfigureAwait(false);

            // Queue background game updates check in bottom-right task bar
            QueueGameUpdatesCheckBackgroundTask();
        }
        catch
        {
            IsLoading = false;
        }

        await CheckAppUpdatesOnStartupAsync().ConfigureAwait(true);
    }

    private async Task CheckAppUpdatesOnStartupAsync()
    {
        // 1. Check if a downloaded update from a previous session is pending
        var pendingPath = BlueStar.Infrastructure.Update.GitHubUpdateService.GetPendingUpdateFilePath();
        if (!string.IsNullOrWhiteSpace(pendingPath))
        {
            _notificationService.ShowInfo(
                "Update Ready",
                "A newer version of BlueStar was downloaded and is ready to install.",
                duration: TimeSpan.FromSeconds(30),
                actionText: "Update Now",
                action: () =>
                {
                    _ = _updateService.ApplyUpdateAsync(pendingPath, CancellationToken.None);
                });
            return;
        }

        // 2. Check for new versions from GitHub repository
        try
        {
            await Task.Delay(2000); // Allow initial UI render
            var update = await _updateService.CheckForUpdatesAsync(CancellationToken.None).ConfigureAwait(true);
            if (update != null)
            {
                _notificationService.ShowInfo(
                    "New BlueStar Version",
                    $"BlueStar v{update.Version} is available.",
                    duration: TimeSpan.FromSeconds(25),
                    actionText: "Download",
                    action: () =>
                    {
                        _ = DownloadAndPromptUpdateAsync(update);
                    });
            }
        }
        catch { }
    }

    private async Task DownloadAndPromptUpdateAsync(UpdateInfo update)
    {
        _notificationService.ShowInfo("Downloading Update", $"Downloading BlueStar v{update.Version} in the background...");
        try
        {
            var installerPath = await _updateService.DownloadUpdateAsync(update, null, CancellationToken.None).ConfigureAwait(true);
            _notificationService.ShowSuccess(
                "Update Ready",
                $"BlueStar v{update.Version} downloaded successfully. Click to restart and update.",
                duration: TimeSpan.FromSeconds(40),
                actionText: "Update",
                action: () =>
                {
                    _ = _updateService.ApplyUpdateAsync(installerPath, CancellationToken.None);
                });
        }
        catch (Exception ex)
        {
            _notificationService.ShowError("Failed to Download Update", ex.Message);
        }
    }

    private void QueueGameUpdatesCheckBackgroundTask()
    {
        if (_metadataProvider is not BlueStar.Infrastructure.Metadata.SteamStoreApiClient steamClient)
            return;

        _backgroundTaskService.QueueTask(
            "Game Updates Check",
            "Library Instances",
            async (progress, ct) =>
            {
                var instances = (await _instanceManager.GetAllAsync(ct).ConfigureAwait(false)).ToList();
                if (instances.Count == 0)
                {
                    progress.Report(new BlueStar.Core.Models.BackgroundTaskProgress(100, "No installed games to scan.", "Complete"));
                    return;
                }

                int checkedCount = 0;
                int updatesFound = 0;
                for (int i = 0; i < instances.Count; i++)
                {
                    if (ct.IsCancellationRequested) break;
                    var inst = instances[i];
                    var pct = (double)i / instances.Count * 100.0;
                    progress.Report(new BlueStar.Core.Models.BackgroundTaskProgress(pct, $"Checking {inst.Name} ({i + 1}/{instances.Count})...", "Analyzing"));

                    try
                    {
                        var (status, desc) = await BlueStar.Infrastructure.Services.GameUpdateDetectionHelper.CheckInstanceUpdateAsync(inst, steamClient, ct).ConfigureAwait(false);
                        if (status == UpdateCheckStatus.UpdateAvailable)
                        {
                            var updated = inst with
                            {
                                HasUpdateAvailable = true,
                                UpdateDescription = desc
                            };
                            await _instanceManager.UpdateAsync(updated, ct).ConfigureAwait(false);
                            updatesFound++;
                        }
                        else if (status == UpdateCheckStatus.UpToDate)
                        {
                            if (inst.HasUpdateAvailable || inst.UpdateDescription != null)
                            {
                                var updated = inst with
                                {
                                    HasUpdateAvailable = false,
                                    UpdateDescription = null
                                };
                                await _instanceManager.UpdateAsync(updated, ct).ConfigureAwait(false);
                            }
                        }
                        // When status is UpdateCheckStatus.Unknown, preserve existing known state
                    }
                    catch { }

                    checkedCount++;
                }

                var finalMessage = updatesFound > 0
                    ? $"Scan complete: {updatesFound} update(s) detected across {checkedCount} games."
                    : $"Scan complete: All {checkedCount} games are up to date.";
                progress.Report(new BlueStar.Core.Models.BackgroundTaskProgress(100, finalMessage, "Complete"));
            });
    }

    private void OnSteamStatusChanged(object? sender, SteamStatus status)
    {
        _uiContext.Post(_ => UpdateSteamProperties(status), null);
    }

    private void UpdateSteamProperties(SteamStatus status)
    {
        IsSteamRunning = status.IsRunning;
        SteamPersonaName = status.DisplayName;
        SteamStatusText = status.StatusText;

        if (status.IsRunning)
        {
            var idText = status.SteamId64.HasValue ? $" (ID: {status.SteamId64})" : "";
            SteamTooltipText = $"Steam is Running\nLogged in as: {status.DisplayName}{idText}";
        }
        else
        {
            SteamTooltipText = "Steam is Not Running\nLaunch Steam to sync shortcuts and playtime.";
        }
    }

    private readonly System.Collections.Generic.Dictionary<DownloadJobItem, System.ComponentModel.PropertyChangedEventHandler> _subscribedJobs = new();

    private void UpdateDownloadStats()
    {
        // 1. Remove jobs that are no longer in the queue
        var currentQueueJobs = _downloadQueueManager.Queue.ToHashSet();
        var jobsToRemove = _subscribedJobs.Keys.Where(j => !currentQueueJobs.Contains(j)).ToList();
        foreach (var job in jobsToRemove)
        {
            if (_subscribedJobs.Remove(job, out var handler))
            {
                job.PropertyChanged -= handler;
            }
        }

        // 2. Subscribe to any new jobs for live progress and speed
        foreach (var job in _downloadQueueManager.Queue)
        {
            if (!_subscribedJobs.ContainsKey(job))
            {
                System.ComponentModel.PropertyChangedEventHandler handler = (s, e) =>
                {
                    if (e.PropertyName is nameof(DownloadJobItem.Percentage) or nameof(DownloadJobItem.SpeedBytesPerSec) or nameof(DownloadJobItem.JobStatus))
                    {
                        _uiContext.Post(_ => UpdateDownloadStats(), null);
                    }
                };
                _subscribedJobs[job] = handler;
                job.PropertyChanged += handler;
            }
        }

        var activeDownloadingJobs = _downloadQueueManager.Queue.Where(j => j.JobStatus == DownloadJobStatus.Downloading).ToList();
        var pausedJobs = _downloadQueueManager.Queue.Where(j => j.JobStatus == DownloadJobStatus.Paused).ToList();
        var queuedJobs = _downloadQueueManager.Queue.Where(j => j.JobStatus == DownloadJobStatus.Queued).ToList();
        var pendingOrActiveJobs = _downloadQueueManager.Queue.Where(j => j.JobStatus is DownloadJobStatus.Downloading or DownloadJobStatus.Queued or DownloadJobStatus.Paused).ToList();

        var totalJobs = _downloadQueueManager.Queue.Count;
        var completedJobs = _downloadQueueManager.Queue.Count(j => j.IsCompleted);

        var activeCount = activeDownloadingJobs.Count;
        var pausedCount = pausedJobs.Count;
        var totalPendingCount = pendingOrActiveJobs.Count;

        ActiveDownloadsCount = activeCount;
        PausedDownloadsCount = pausedCount;
        TotalPendingDownloadsCount = totalPendingCount;

        HasActiveDownloads = activeCount > 0;
        HasPausedDownloads = pausedCount > 0 && activeCount == 0;
        HasPendingOrActiveDownloads = totalPendingCount > 0;
        HasMultipleActiveDownloads = activeCount > 1;

        if (totalPendingCount == 0)
        {
            DownloadsButtonText = "Downloads";
            StatusText = totalJobs > 0 && completedJobs == totalJobs ? "All downloads completed" : "Ready";
        }
        else if (activeCount == 1)
        {
            var singleJob = activeDownloadingJobs[0];
            var gameName = singleJob.Instance?.Name ?? "Game";
            var pct = singleJob.Percentage;
            var speed = singleJob.FormattedSpeed;
            DownloadsButtonText = $"Downloads: {gameName} ({pct:F0}%) • {speed}";
            StatusText = $"Downloading {gameName} ({pct:F0}%) • {speed}";
        }
        else if (activeCount > 1)
        {
            var totalSpeed = activeDownloadingJobs.Sum(j => j.SpeedBytesPerSec);
            var formattedSpeed = FormatSpeed(totalSpeed);
            DownloadsButtonText = $"Downloads ({completedJobs}/{totalJobs}) • {formattedSpeed}";
            StatusText = $"{activeCount} downloads active ({completedJobs}/{totalJobs} completed) • {formattedSpeed}";
        }
        else if (pausedCount > 0)
        {
            DownloadsButtonText = "Downloads: Paused";
            StatusText = pausedCount == 1 ? "1 download paused in queue" : $"{pausedCount} downloads paused in queue";
        }
        else
        {
            DownloadsButtonText = "Downloads: Queued";
            StatusText = queuedJobs.Count == 1 ? "1 download queued" : $"{queuedJobs.Count} downloads queued";
        }
    }

    private static string FormatSpeed(double bytesPerSec)
    {
        return bytesPerSec switch
        {
            > 1024 * 1024 * 1024 => $"{bytesPerSec / (1024.0 * 1024.0 * 1024.0):F1} GB/s",
            > 1024 * 1024 => $"{bytesPerSec / (1024.0 * 1024.0):F1} MB/s",
            > 1024 => $"{bytesPerSec / 1024.0:F1} KB/s",
            > 0 => $"{bytesPerSec:F0} B/s",
            _ => "0 KB/s"
        };
    }

    /// <summary>
    /// Dismisses a notification by its ID.
    /// </summary>
    [RelayCommand]
    public void DismissNotification(Guid id)
    {
        _notificationService.Dismiss(id);
    }

    #region Navigation & History (Mouse 4 & 5 Support)

    private readonly Stack<NavigationEntry> _backStack = new();
    private readonly Stack<NavigationEntry> _forwardStack = new();
    private bool _isNavigatingHistory;

    private sealed record NavigationEntry(string Page, object? Parameter, string? CategoryId);

    public bool CanGoBack => _backStack.Count > 0;
    public bool CanGoForward => _forwardStack.Count > 0;

    private void PushNavigation(string page, object? parameter = null, string? categoryId = null)
    {
        if (_isNavigatingHistory) return;

        var current = GetCurrentNavigationEntry();
        if (current != null)
        {
            _backStack.Push(current);
            _forwardStack.Clear();
            OnPropertyChanged(nameof(CanGoBack));
            OnPropertyChanged(nameof(CanGoForward));
        }
    }

    private NavigationEntry? GetCurrentNavigationEntry()
    {
        if (CurrentView is InstanceDetailView && CurrentView.DataContext is InstanceDetailViewModel idvm && idvm.Instance != null)
        {
            return new NavigationEntry("InstanceDetail", idvm.Instance, null);
        }

        if (!string.IsNullOrWhiteSpace(SelectedNavigation))
        {
            return new NavigationEntry(SelectedNavigation, null, null);
        }

        return null;
    }

    [RelayCommand]
    public void GoBack()
    {
        if (_backStack.Count == 0) return;

        var current = GetCurrentNavigationEntry();
        var prev = _backStack.Pop();
        if (current != null)
        {
            _forwardStack.Push(current);
        }

        _isNavigatingHistory = true;
        try
        {
            ApplyNavigationEntry(prev);
        }
        finally
        {
            _isNavigatingHistory = false;
            OnPropertyChanged(nameof(CanGoBack));
            OnPropertyChanged(nameof(CanGoForward));
        }
    }

    [RelayCommand]
    public void GoForward()
    {
        if (_forwardStack.Count == 0) return;

        var current = GetCurrentNavigationEntry();
        var next = _forwardStack.Pop();
        if (current != null)
        {
            _backStack.Push(current);
        }

        _isNavigatingHistory = true;
        try
        {
            ApplyNavigationEntry(next);
        }
        finally
        {
            _isNavigatingHistory = false;
            OnPropertyChanged(nameof(CanGoBack));
            OnPropertyChanged(nameof(CanGoForward));
        }
    }

    private void ApplyNavigationEntry(NavigationEntry entry)
    {
        if (entry.Page == "InstanceDetail" && entry.Parameter is GameInstance inst)
        {
            OpenInstanceDetail(inst, autoCheckUpdates: false);
        }
        else if (entry.Page == "ExploreCategory" && !string.IsNullOrWhiteSpace(entry.CategoryId))
        {
            NavigateToExploreCategory(entry.CategoryId);
        }
        else
        {
            Navigate(entry.Page);
        }
    }

    #endregion

    /// <summary>
    /// Navigates to the specified view.
    /// </summary>
    /// <param name="page">The target page identifier.</param>
    [RelayCommand]
    public void Navigate(string page)
    {
        PushNavigation(page);
        SelectedNavigation = page;

        if (page == "Home")
        {
            var view = new HomeView();
            var vm = App.Services.GetRequiredService<HomeViewModel>();
            vm.OnNavigateRequested = Navigate;
            vm.OnNavigateToCategoryRequested = NavigateToExploreCategory;
            vm.OnManageInstanceRequested = OpenInstanceDetail;
            vm.OnManageInstanceRequestedWithUpdate = (inst, autoCheck) => OpenInstanceDetail(inst, autoCheckUpdates: autoCheck);
            view.DataContext = vm;
            CurrentView = view;
            return;
        }

        if (page is "Library" or "Instances")
        {
            var view = new LibraryView();
            var vm = App.Services.GetRequiredService<LibraryViewModel>();
            vm.OnManageInstanceRequested = OpenInstanceDetail;
            vm.OnManageInstanceRequestedWithUpdate = (inst, autoCheck) => OpenInstanceDetail(inst, autoCheckUpdates: autoCheck);
            vm.OnNavigateRequested = Navigate;
            view.DataContext = vm;
            CurrentView = view;
            return;
        }

        if (page is "Browse" or "Explore")
        {
            var view = new BrowseView();
            var vm = App.Services.GetRequiredService<BrowseViewModel>();
            vm.OnManageInstanceRequested = OpenInstanceDetail;
            view.DataContext = vm;
            CurrentView = view;
            return;
        }

        if (page == "Downloads")
        {
            var view = new DownloadsView();
            var vm = App.Services.GetRequiredService<DownloadsViewModel>();
            vm.OnOpenInstanceRequested = OpenInstanceDetail;
            view.DataContext = vm;
            CurrentView = view;
            return;
        }

        CurrentView = page switch
        {
            "Settings" => CreateView<SettingsView, SettingsViewModel>(),
            "About" => CreateView<AboutView, AboutViewModel>(),
            _ => CurrentView
        };
    }

    /// <summary>
    /// Opens the instance dashboard for a specific game instance.
    /// </summary>
    public void OpenInstanceDetail(GameInstance instance) => OpenInstanceDetail(instance, autoCheckUpdates: false);

    /// <summary>
    /// Opens the instance dashboard for a specific game instance, optionally auto-checking DepotBox for updates.
    /// </summary>
    public void OpenInstanceDetail(GameInstance instance, bool autoCheckUpdates)
    {
        PushNavigation("InstanceDetail", instance);
        var view = new InstanceDetailView();
        var vm = App.Services.GetRequiredService<InstanceDetailViewModel>();
        vm.OnNavigateBack = () => Navigate("Library");
        view.DataContext = vm;
        _ = vm.LoadInstanceAsync(instance, autoCheckDepotUpdates: autoCheckUpdates);
        CurrentView = view;
    }

    /// <summary>
    /// Navigates to the Explore tab, auto-expanding the requested category vertical grid.
    /// </summary>
    public void NavigateToExploreCategory(string categoryId)
    {
        PushNavigation("ExploreCategory", null, categoryId);
        SelectedNavigation = "Explore";
        var view = new BrowseView();
        var vm = App.Services.GetRequiredService<BrowseViewModel>();
        vm.OnManageInstanceRequested = OpenInstanceDetail;
        view.DataContext = vm;
        CurrentView = view;
        vm.ExpandCategory(categoryId);
    }

    [RelayCommand]
    public void ToggleSidebarImportMenu() => IsSidebarImportMenuOpen = !IsSidebarImportMenuOpen;

    [RelayCommand]
    public void CloseSidebarImportMenu() => IsSidebarImportMenuOpen = false;

    [RelayCommand]
    public void OpenDownloads() => Navigate("Downloads");

    [RelayCommand]
    public void OpenRecentShortcut(GameInstance instance)
    {
        if (instance == null) return;
        OpenInstanceDetail(instance);
    }

    [RelayCommand]
    public async Task SidebarImportSteamAsync()
    {
        IsSidebarImportMenuOpen = false;
        Navigate("Home");
        if (CurrentView?.DataContext is HomeViewModel vm)
        {
            await vm.OpenSteamImportModalAsync();
        }
    }

    [RelayCommand]
    public void SidebarImportZip()
    {
        IsSidebarImportMenuOpen = false;
        Navigate("Home");
        if (CurrentView?.DataContext is HomeViewModel vm)
        {
            vm.OpenZipImportModal();
        }
    }

    [RelayCommand]
    public void SidebarImportFolder()
    {
        IsSidebarImportMenuOpen = false;
        Navigate("Home");
        if (CurrentView?.DataContext is HomeViewModel vm)
        {
            vm.OpenFolderImportModal();
        }
    }

    private void UpdateBackgroundTaskStats()
    {
        var activeBgTasks = _backgroundTaskService.Tasks.Where(t => t.IsActive).ToList();
        var activeDownloadJobs = _downloadQueueManager.Queue
            .Where(j => j.JobStatus is DownloadJobStatus.Downloading or DownloadJobStatus.Queued)
            .ToList();

        var downloadTaskItems = new List<BackgroundTaskItem>();
        foreach (var job in activeDownloadJobs)
        {
            downloadTaskItems.Add(new BackgroundTaskItem
            {
                Id = job.Instance.Id,
                Title = job.Instance.Name,
                InstanceName = job.Instance.Name,
                InstanceId = job.Instance.Id,
                Status = job.JobStatus == DownloadJobStatus.Downloading ? BackgroundTaskStatus.Running : BackgroundTaskStatus.Queued,
                ProgressPercentage = job.Percentage,
                CurrentStepMessage = job.JobStatus == DownloadJobStatus.Downloading
                    ? $"{job.FormattedSpeed} — {job.StatusMessage}"
                    : "Queued...",
                CanCancel = true
            });
        }

        var allActive = new List<BackgroundTaskItem>();
        allActive.AddRange(activeBgTasks);
        allActive.AddRange(downloadTaskItems);

        ActiveTasksCount = allActive.Count;
        HasActiveTasks = allActive.Count > 0;
        HasMultipleActiveTasks = allActive.Count > 1;

        if (allActive.Count == 0)
        {
            BackgroundTaskStatusText = "No active tasks";
            TotalTasksProgressPercentage = 0;
            ActiveBackgroundTasks.Clear();
            if (IsTasksFlyoutOpen)
            {
                IsTasksFlyoutOpen = false;
            }
        }
        else
        {
            var avg = allActive.Average(t => t.ProgressPercentage);
            TotalTasksProgressPercentage = Math.Clamp(avg, 0, 100);

            if (allActive.Count == 1)
            {
                var item = allActive[0];
                BackgroundTaskStatusText = $"{item.Title} ({item.ProgressPercentage:F0}%)";
            }
            else
            {
                BackgroundTaskStatusText = $"{allActive.Count} tasks ({TotalTasksProgressPercentage:F0}%)";
            }

            // Sync ActiveBackgroundTasks collection so UI updates cleanly
            var toRemove = ActiveBackgroundTasks.Where(t => !allActive.Any(a => a.Id == t.Id)).ToList();
            foreach (var rem in toRemove) ActiveBackgroundTasks.Remove(rem);

            foreach (var act in allActive)
            {
                var existing = ActiveBackgroundTasks.FirstOrDefault(t => t.Id == act.Id);
                if (existing != null)
                {
                    existing.ProgressPercentage = act.ProgressPercentage;
                    existing.CurrentStepMessage = act.CurrentStepMessage;
                    existing.Status = act.Status;
                }
                else
                {
                    ActiveBackgroundTasks.Add(act);
                }
            }
        }
    }

    [RelayCommand]
    public void ToggleTasksFlyout()
    {
        IsTasksFlyoutOpen = !IsTasksFlyoutOpen;
    }

    [RelayCommand]
    public void CloseTasksFlyout()
    {
        IsTasksFlyoutOpen = false;
    }

    [RelayCommand]
    public void CancelBackgroundTask(Guid taskId)
    {
        _backgroundTaskService.CancelTask(taskId);

        var downloadJob = _downloadQueueManager.Queue.FirstOrDefault(j => j.Instance.Id == taskId);
        if (downloadJob != null)
        {
            _ = _downloadQueueManager.CancelAsync(taskId);
        }
    }

    [RelayCommand]
    public void ClearCompletedTasks()
    {
        _backgroundTaskService.ClearCompleted();
    }

    public async Task RefreshRecentShortcutsAsync()
    {
        try
        {
            var instances = await _instanceManager.GetAllAsync(CancellationToken.None).ConfigureAwait(false);
            var top5 = instances
                .OrderByDescending(i => i.LastPlayedAt.HasValue)
                .ThenByDescending(i => i.LastPlayedAt ?? i.CreatedAt)
                .Take(5)
                .ToList();

            _uiContext.Post(_ =>
            {
                RecentShortcuts.Clear();
                foreach (var inst in top5)
                {
                    RecentShortcuts.Add(inst);
                }
            }, null);
        }
        catch { }
    }

    #region System Requirements Management

    /// <summary>
    /// Scans the host system to verify if all essential and recommended BlueStar requirements are installed.
    /// </summary>
    [RelayCommand]
    public async Task ScanSystemRequirementsAsync() => await ScanSystemRequirementsAsync(autoPromptModal: false).ConfigureAwait(true);

    /// <summary>
    /// Scans the host system for prerequisites, optionally auto-opening the modal if missing items are found.
    /// </summary>
    public async Task ScanSystemRequirementsAsync(bool autoPromptModal = false)
    {
        if (_prerequisiteService == null) return;

        IsScanningSystemPrerequisites = true;
        try
        {
            var items = await _prerequisiteService.DetectSystemPrerequisitesAsync(CancellationToken.None).ConfigureAwait(true);
            
            _uiContext.Post(_ =>
            {
                SystemPrerequisites.Clear();
                foreach (var item in items)
                {
                    SystemPrerequisites.Add(item);
                }

                var missing = items.Where(i => i.Status != PrerequisiteStatus.InstalledInSystem && i.Status != PrerequisiteStatus.InstalledSuccess).ToList();
                MissingPrerequisitesCount = missing.Count;
                HasMissingRequirements = missing.Count > 0;

                if (HasMissingRequirements && autoPromptModal && (_appSettings?.CheckSystemRequirementsOnStartup ?? true))
                {
                    IsSystemRequirementsModalOpen = true;
                }
            }, null);
        }
        catch { }
        finally
        {
            _uiContext.Post(_ => IsScanningSystemPrerequisites = false, null);
        }
    }

    /// <summary>
    /// Opens the System Requirements modal dialog.
    /// </summary>
    [RelayCommand]
    public void OpenSystemRequirementsModal()
    {
        _ = ScanSystemRequirementsAsync(autoPromptModal: false);
        IsSystemRequirementsModalOpen = true;
    }

    /// <summary>
    /// Closes the System Requirements modal dialog.
    /// </summary>
    [RelayCommand]
    public void CloseSystemRequirementsModal()
    {
        IsSystemRequirementsModalOpen = false;
    }

    /// <summary>
    /// Installs a specific system prerequisite item.
    /// </summary>
    [RelayCommand]
    public async Task InstallSystemPrerequisiteAsync(PrerequisiteItem? item)
    {
        if (item == null || _prerequisiteService == null || IsInstallingSystemPrerequisites) return;

        IsInstallingSystemPrerequisites = true;
        SystemPrerequisitesInstallStatusText = $"Installing {item.Name}...";
        try
        {
            var progress = new Progress<string>(msg => SystemPrerequisitesInstallStatusText = msg);
            var success = await _prerequisiteService.InstallPrerequisiteAsync(item, progress, CancellationToken.None).ConfigureAwait(true);
            if (success)
            {
                _notificationService.ShowSuccess("Prerequisite Installed", $"{item.Name} was installed successfully.");
            }
            else
            {
                _notificationService.ShowError("Installation Incomplete", $"Could not complete installation of {item.Name}.");
            }
            await ScanSystemRequirementsAsync(autoPromptModal: false).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _notificationService.ShowError("Installation Error", ex.Message);
        }
        finally
        {
            IsInstallingSystemPrerequisites = false;
            SystemPrerequisitesInstallStatusText = string.Empty;
        }
    }

    /// <summary>
    /// Installs all missing system prerequisites in 1-click.
    /// </summary>
    [RelayCommand]
    public async Task InstallAllSystemPrerequisitesAsync()
    {
        if (_prerequisiteService == null || IsInstallingSystemPrerequisites || SystemPrerequisites.Count == 0) return;

        var missing = SystemPrerequisites.Where(i => i.Status != PrerequisiteStatus.InstalledInSystem && i.Status != PrerequisiteStatus.InstalledSuccess).ToList();
        if (missing.Count == 0)
        {
            _notificationService.ShowInfo("System Up to Date", "All essential system requirements are already installed.");
            return;
        }

        IsInstallingSystemPrerequisites = true;
        try
        {
            var progress = new Progress<string>(msg => SystemPrerequisitesInstallStatusText = msg);
            int installed = await _prerequisiteService.InstallAllPrerequisitesAsync(missing, progress, CancellationToken.None).ConfigureAwait(true);
            _notificationService.ShowSuccess("System Requirements Updated", $"{installed} requirement(s) configured successfully.");
            await ScanSystemRequirementsAsync(autoPromptModal: false).ConfigureAwait(true);

            if (!HasMissingRequirements)
            {
                SystemPrerequisitesInstallStatusText = "All requirements verified successfully!";
            }
        }
        catch (Exception ex)
        {
            _notificationService.ShowError("Installation Error", ex.Message);
        }
        finally
        {
            IsInstallingSystemPrerequisites = false;
        }
    }

    #endregion

    private static TView CreateView<TView, TViewModel>()
        where TView : UserControl, new()
        where TViewModel : class
    {
        var view = new TView();
        var vm = App.Services.GetRequiredService<TViewModel>();
        view.DataContext = vm;
        return view;
    }

    /// <summary>
    /// Releases all event subscriptions and cancels running background operations.
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

        _downloadQueueManager.Queue.CollectionChanged -= _queueCollectionChangedHandler;
        _downloadQueueManager.QueueChanged -= _queueChangedHandler;
        _steamStatusService.StatusChanged -= _steamStatusChangedHandler;
        _backgroundTaskService.TasksChanged -= _tasksChangedHandler;
        _instanceManager.InstancesChanged -= _instancesChangedHandler;

        if (_gameLauncher != null && _runningStateChangedHandler != null)
        {
            _gameLauncher.RunningStateChanged -= _runningStateChangedHandler;
        }

        foreach (var kvp in _subscribedJobs)
        {
            kvp.Key.PropertyChanged -= kvp.Value;
        }
        _subscribedJobs.Clear();

        if (CurrentView?.DataContext is IDisposable currentDisposable)
        {
            try
            {
                currentDisposable.Dispose();
            }
            catch { }
        }
    }
}
