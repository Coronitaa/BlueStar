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
public partial class MainViewModel : ObservableObject
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

    public MainViewModel(
        DownloadQueueManager downloadQueueManager,
        ISteamStatusService steamStatusService,
        INotificationService notificationService,
        IUpdateService updateService,
        IInstanceManager instanceManager,
        IBackgroundTaskService backgroundTaskService,
        IGameLauncher? gameLauncher = null,
        IDepotBoxApiClient? apiClient = null,
        IMetadataProvider? metadataProvider = null)
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
        _uiContext = SynchronizationContext.Current ?? new SynchronizationContext();

        _downloadQueueManager.Queue.CollectionChanged += (_, _) => _uiContext.Post(_ => UpdateDownloadStats(), null);
        _downloadQueueManager.QueueChanged += (_, _) => _uiContext.Post(_ => UpdateDownloadStats(), null);
        _steamStatusService.StatusChanged += OnSteamStatusChanged;
        _backgroundTaskService.TasksChanged += (_, _) => _uiContext.Post(_ => UpdateBackgroundTaskStats(), null);

        // Auto-refresh sidebar shortcuts on any instance create, update, delete or game launch
        _instanceManager.InstancesChanged += (_, _) => _ = RefreshRecentShortcutsAsync();
        if (_gameLauncher != null)
        {
            _gameLauncher.RunningStateChanged += (_, _) => _ = RefreshRecentShortcutsAsync();
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
            StartupStatusText = "Starting ecosystem services...";
            await Task.Delay(150).ConfigureAwait(true);

            StartupStatusText = "Loading local instances and manifests...";
            await _instanceManager.GetAllAsync(CancellationToken.None).ConfigureAwait(true);

            StartupStatusText = "Syncing Steam status...";
            await Task.Delay(150).ConfigureAwait(true);

            StartupStatusText = "Ready!";
            await Task.Delay(100).ConfigureAwait(true);

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
                        var (hasUpdate, desc) = await BlueStar.Infrastructure.Services.GameUpdateDetectionHelper.CheckInstanceUpdateAsync(inst, steamClient, ct).ConfigureAwait(false);
                        if (hasUpdate != inst.HasUpdateAvailable || desc != inst.UpdateDescription)
                        {
                            var updated = inst with
                            {
                                HasUpdateAvailable = hasUpdate,
                                UpdateDescription = desc
                            };
                            await _instanceManager.UpdateAsync(updated, ct).ConfigureAwait(false);
                        }
                        if (hasUpdate) updatesFound++;
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

    private readonly HashSet<DownloadJobItem> _subscribedJobs = new();

    private void UpdateDownloadStats()
    {
        // Subscribe to any new jobs for live progress and speed
        foreach (var job in _downloadQueueManager.Queue)
        {
            if (_subscribedJobs.Add(job))
            {
                job.PropertyChanged += (s, e) =>
                {
                    if (e.PropertyName is nameof(DownloadJobItem.Percentage) or nameof(DownloadJobItem.SpeedBytesPerSec) or nameof(DownloadJobItem.JobStatus))
                    {
                        _uiContext.Post(_ => UpdateDownloadStats(), null);
                    }
                };
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
            var singleJob = pausedJobs[0];
            var gameName = singleJob.Instance?.Name ?? "Game";
            DownloadsButtonText = pausedCount == 1 ? $"Downloads: {gameName} (Paused)" : $"Downloads: {pausedCount} Paused";
            StatusText = $"{pausedCount} downloads paused in queue";
        }
        else
        {
            var singleJob = queuedJobs.FirstOrDefault();
            var gameName = singleJob?.Instance?.Name ?? "Game";
            DownloadsButtonText = queuedJobs.Count == 1 ? $"Downloads: {gameName} (Queued)" : $"Downloads: {queuedJobs.Count} Queued";
            StatusText = $"{queuedJobs.Count} downloads queued";
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

    /// <summary>
    /// Navigates to the specified view.
    /// </summary>
    /// <param name="page">The target page identifier.</param>
    [RelayCommand]
    public void Navigate(string page)
    {
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
        var active = _backgroundTaskService.Tasks.Where(t => t.IsActive).ToList();
        ActiveTasksCount = active.Count;
        HasActiveTasks = active.Count > 0;
        HasMultipleActiveTasks = active.Count > 1;

        if (active.Count == 0)
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
            var avg = active.Average(t => t.ProgressPercentage);
            TotalTasksProgressPercentage = Math.Clamp(avg, 0, 100);
            BackgroundTaskStatusText = active.Count == 1
                ? $"{active[0].Title} ({active[0].ProgressPercentage:F0}%)"
                : $"{active.Count} tasks ({TotalTasksProgressPercentage:F0}%)";

            // Sync ActiveBackgroundTasks collection so UI updates cleanly
            var toRemove = ActiveBackgroundTasks.Where(t => !active.Contains(t)).ToList();
            foreach (var rem in toRemove) ActiveBackgroundTasks.Remove(rem);

            foreach (var act in active)
            {
                if (!ActiveBackgroundTasks.Contains(act))
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

    private static TView CreateView<TView, TViewModel>()
        where TView : UserControl, new()
        where TViewModel : class
    {
        var view = new TView();
        var vm = App.Services.GetRequiredService<TViewModel>();
        view.DataContext = vm;
        return view;
    }
}
