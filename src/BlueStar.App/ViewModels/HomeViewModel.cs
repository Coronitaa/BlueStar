using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BlueStar.Core.Interfaces;
using BlueStar.Core.Models;
using BlueStar.Infrastructure.Downloader;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace BlueStar.App.ViewModels;

/// <summary>
/// ViewModel for the Home Dashboard view (recent instances, active downloads, quick actions, stats).
/// </summary>
public partial class HomeViewModel : ObservableObject
{
    private readonly IInstanceManager _instanceManager;
    private readonly DownloadQueueManager _downloadQueueManager;
    private readonly IGameLauncher _gameLauncher;
    private readonly ILogger<HomeViewModel> _logger;

    [ObservableProperty]
    private string _greetingText = "Welcome to BlueStar";

    [ObservableProperty]
    private ObservableCollection<GameInstance> _recentInstances = [];

    [ObservableProperty]
    private DownloadJobItem? _currentActiveDownload;

    [ObservableProperty]
    private int _totalInstancesCount;

    [ObservableProperty]
    private int _readyInstancesCount;

    [ObservableProperty]
    private bool _isLoading;

    public Action<string>? OnNavigateRequested { get; set; }
    public Action<GameInstance>? OnManageInstanceRequested { get; set; }

    public HomeViewModel(
        IInstanceManager instanceManager,
        DownloadQueueManager downloadQueueManager,
        IGameLauncher gameLauncher,
        ILogger<HomeViewModel> logger)
    {
        _instanceManager = instanceManager;
        _downloadQueueManager = downloadQueueManager;
        _gameLauncher = gameLauncher;
        _logger = logger;

        SetTimeBasedGreeting();
        _downloadQueueManager.Queue.CollectionChanged += (_, _) => UpdateActiveDownload();

        _ = LoadDashboardDataAsync();
    }

    private void SetTimeBasedGreeting()
    {
        var hour = DateTime.Now.Hour;
        GreetingText = hour switch
        {
            >= 5 and < 12 => "Good morning",
            >= 12 and < 18 => "Good afternoon",
            _ => "Good evening"
        };
    }

    [RelayCommand]
    public async Task LoadDashboardDataAsync()
    {
        IsLoading = true;
        try
        {
            var instances = await _instanceManager.GetAllAsync(CancellationToken.None).ConfigureAwait(true);
            TotalInstancesCount = instances.Count;
            ReadyInstancesCount = instances.Count(i => i.Status is InstanceStatus.Ready or InstanceStatus.Running);

            // Order by LastPlayedAt descending or CreatedAt
            var sorted = instances
                .OrderByDescending(i => i.LastPlayedAt ?? DateTimeOffset.MinValue)
                .ThenByDescending(i => i.CreatedAt)
                .Take(6)
                .ToList();

            RecentInstances = new ObservableCollection<GameInstance>(sorted);
            UpdateActiveDownload();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load dashboard data");
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void UpdateActiveDownload()
    {
        CurrentActiveDownload = _downloadQueueManager.Queue.FirstOrDefault(j => j.IsActive);
    }

    [RelayCommand]
    private void NavigateTo(string targetPage)
    {
        OnNavigateRequested?.Invoke(targetPage);
    }

    [RelayCommand]
    private void ManageInstance(GameInstance instance)
    {
        OnManageInstanceRequested?.Invoke(instance);
    }

    [RelayCommand]
    private async Task PlayInstanceAsync(GameInstance instance)
    {
        if (instance == null) return;
        _logger.LogInformation("Launching instance {Name} from Dashboard", instance.Name);
        var result = await _gameLauncher.LaunchAsync(instance, null, CancellationToken.None).ConfigureAwait(true);
        if (!result.Success)
        {
            _logger.LogWarning("Launch failed: {Message}", result.Message);
        }
    }
}
