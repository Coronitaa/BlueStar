using System.Collections.ObjectModel;
using BlueStar.Infrastructure.Downloader;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BlueStar.App.ViewModels;

/// <summary>
/// ViewModel for the Downloads tab. Exposes the active queue and history log,
/// plus commands to pause, resume, retry, cancel, and remove jobs.
/// </summary>
public partial class DownloadsViewModel : ObservableObject
{
    private readonly DownloadQueueManager _queueManager;

    /// <summary>Access to the queue manager for controls and telemetry.</summary>
    public DownloadQueueManager QueueManager => _queueManager;

    /// <summary>Active + paused download jobs (shown as cards at top).</summary>
    public ObservableCollection<DownloadJobItem> DownloadQueue => _queueManager.Queue;

    /// <summary>Completed / failed / canceled history (shown in log section below).</summary>
    public ObservableCollection<DownloadLogEntry> HistoryLog => _queueManager.HistoryLog;

    /// <summary>Callback to navigate to an instance detail view.</summary>
    public Action<BlueStar.Core.Models.GameInstance>? OnOpenInstanceRequested { get; set; }

    public DownloadsViewModel(DownloadQueueManager queueManager)
    {
        _queueManager = queueManager;
    }

    // ── Commands ──────────────────────────────────────────────────────────────

    [RelayCommand]
    private void OpenInstance(BlueStar.Core.Models.GameInstance? instance)
    {
        if (instance != null)
        {
            OnOpenInstanceRequested?.Invoke(instance);
        }
    }

    [RelayCommand]
    private void PauseDownload(Guid instanceId) =>
        _ = _queueManager.PauseAsync(instanceId);

    [RelayCommand]
    private void ResumeDownload(Guid instanceId) =>
        _ = _queueManager.ResumeAsync(instanceId);

    [RelayCommand]
    private void RetryDownload(Guid instanceId) =>
        _ = _queueManager.RetryAsync(instanceId);

    [RelayCommand]
    private void CancelDownload(Guid instanceId) =>
        _ = _queueManager.CancelOrRemoveJobAsync(instanceId);

    [RelayCommand]
    private void RemoveJob(Guid instanceId) =>
        _queueManager.RemoveJob(instanceId);

    [RelayCommand]
    private void ClearCompleted() =>
        _queueManager.ClearCompleted();

    [RelayCommand]
    private void ClearFailed() =>
        _queueManager.ClearFailed();

    [RelayCommand]
    private void ClearAll() =>
        _queueManager.ClearAll();

    [RelayCommand]
    private void ClearHistory() =>
        _queueManager.ClearAll();
}
