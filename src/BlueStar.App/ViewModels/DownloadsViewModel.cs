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

    /// <summary>Active + paused download jobs (shown as cards at top).</summary>
    public ObservableCollection<DownloadJobItem> DownloadQueue => _queueManager.Queue;

    /// <summary>Completed / failed / canceled history (shown in log section below).</summary>
    public ObservableCollection<DownloadLogEntry> HistoryLog => _queueManager.HistoryLog;

    public DownloadsViewModel(DownloadQueueManager queueManager)
    {
        _queueManager = queueManager;
    }

    // ── Commands ──────────────────────────────────────────────────────────────

    [RelayCommand]
    private async Task PauseDownloadAsync(Guid instanceId) =>
        await _queueManager.PauseAsync(instanceId).ConfigureAwait(true);

    [RelayCommand]
    private async Task ResumeDownloadAsync(Guid instanceId) =>
        await _queueManager.ResumeAsync(instanceId).ConfigureAwait(true);

    [RelayCommand]
    private async Task RetryDownloadAsync(Guid instanceId) =>
        await _queueManager.RetryAsync(instanceId).ConfigureAwait(true);

    [RelayCommand]
    private async Task CancelDownloadAsync(Guid instanceId) =>
        await _queueManager.CancelOrRemoveJobAsync(instanceId).ConfigureAwait(true);

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
