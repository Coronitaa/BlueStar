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
    private readonly SynchronizationContext _uiContext;

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

    // ── Steam Live Status ──
    [ObservableProperty]
    private bool _isSteamRunning;

    [ObservableProperty]
    private string _steamPersonaName = string.Empty;

    [ObservableProperty]
    private string _steamStatusText = "Steam: Checking...";

    [ObservableProperty]
    private string _steamTooltipText = "Steam client status";

    public MainViewModel(
        DownloadQueueManager downloadQueueManager,
        ISteamStatusService steamStatusService,
        INotificationService notificationService,
        IUpdateService updateService)
    {
        _downloadQueueManager = downloadQueueManager;
        _steamStatusService = steamStatusService;
        _notificationService = notificationService;
        _updateService = updateService;
        _uiContext = SynchronizationContext.Current ?? new SynchronizationContext();

        _downloadQueueManager.Queue.CollectionChanged += (_, _) => _uiContext.Post(_ => UpdateDownloadStats(), null);
        _downloadQueueManager.QueueChanged += (_, _) => _uiContext.Post(_ => UpdateDownloadStats(), null);
        _steamStatusService.StatusChanged += OnSteamStatusChanged;

        // Initialize state
        UpdateDownloadStats();
        UpdateSteamProperties(_steamStatusService.CurrentStatus);

        // Default navigation landing page is Home Dashboard
        Navigate("Home");

        // Check for application updates on startup
        _ = CheckAppUpdatesOnStartupAsync();
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

    private void UpdateDownloadStats()
    {
        var count = _downloadQueueManager.Queue.Count(j => j.IsActive);
        ActiveDownloadsCount = count;
        HasActiveDownloads = count > 0;
        StatusText = HasActiveDownloads ? $"{count} download(s) active" : "Ready";
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
            vm.OnManageInstanceRequested = OpenInstanceDetail;
            view.DataContext = vm;
            CurrentView = view;
            return;
        }

        if (page is "Library" or "Instances")
        {
            var view = new LibraryView();
            var vm = App.Services.GetRequiredService<LibraryViewModel>();
            vm.OnManageInstanceRequested = OpenInstanceDetail;
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

        CurrentView = page switch
        {
            "Downloads" => CreateView<DownloadsView, DownloadsViewModel>(),
            "Settings" => CreateView<SettingsView, SettingsViewModel>(),
            "About" => CreateView<AboutView, AboutViewModel>(),
            _ => CurrentView
        };
    }

    /// <summary>
    /// Opens the instance dashboard for a specific game instance.
    /// </summary>
    public void OpenInstanceDetail(GameInstance instance)
    {
        var view = new InstanceDetailView();
        var vm = App.Services.GetRequiredService<InstanceDetailViewModel>();
        vm.OnNavigateBack = () => Navigate("Library");
        view.DataContext = vm;
        _ = vm.LoadInstanceAsync(instance);
        CurrentView = view;
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
