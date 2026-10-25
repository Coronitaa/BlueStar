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
    private readonly BlueStar.Core.Interfaces.IInstanceManager? _instanceManager;

    /// <summary>Access to the queue manager for controls and telemetry.</summary>
    public DownloadQueueManager QueueManager => _queueManager;

    /// <summary>Active + paused download jobs (shown as cards at top).</summary>
    public ObservableCollection<DownloadJobItem> DownloadQueue => _queueManager.Queue;

    /// <summary>Completed / failed / canceled history (shown in log section below).</summary>
    public ObservableCollection<DownloadLogEntry> HistoryLog => _queueManager.HistoryLog;

    /// <summary>Callback to navigate to an instance detail view.</summary>
    public Action<BlueStar.Core.Models.GameInstance>? OnOpenInstanceRequested { get; set; }

    public DownloadsViewModel(DownloadQueueManager queueManager, BlueStar.Core.Interfaces.IInstanceManager? instanceManager = null)
    {
        _queueManager = queueManager;
        _instanceManager = instanceManager;
    }

    [ObservableProperty]
    private bool _isRemoveModalOpen;

    [ObservableProperty]
    private DownloadJobItem? _jobToRemove;

    [ObservableProperty]
    private string _removeModalGameName = string.Empty;

    [RelayCommand]
    private void RequestRemoveJob(DownloadJobItem? job)
    {
        if (job is null) return;
        JobToRemove = job;
        RemoveModalGameName = job.Instance?.Name ?? "Game";
        IsRemoveModalOpen = true;
    }

    [RelayCommand]
    private void CancelRemoveModal()
    {
        IsRemoveModalOpen = false;
        JobToRemove = null;
    }

    [RelayCommand]
    private async Task ConfirmRemoveJobAsync()
    {
        if (JobToRemove is null)
        {
            IsRemoveModalOpen = false;
            return;
        }

        var instanceId = JobToRemove.Instance.Id;
        IsRemoveModalOpen = false;
        JobToRemove = null;

        await _queueManager.CancelOrRemoveJobAsync(instanceId).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task OpenInstanceAsync(BlueStar.Core.Models.GameInstance? instance)
    {
        if (instance != null)
        {
            var target = _instanceManager != null
                ? (await _instanceManager.GetByIdAsync(instance.Id).ConfigureAwait(true) ?? instance)
                : instance;
            OnOpenInstanceRequested?.Invoke(target);
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
    private void CancelDownload(Guid instanceId)
    {
        var job = DownloadQueue.FirstOrDefault(q => q.Instance.Id == instanceId);
        if (job != null)
        {
            job.IsCanceling = true;
            try
            {
                if (System.Windows.Application.Current?.TryFindResource("String_Canceling") is string loc)
                    job.StatusMessage = loc;
            }
            catch { }
        }
        _ = _queueManager.CancelAsync(instanceId);
    }

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

    [RelayCommand]
    private void RemoveHistoryEntry(DownloadLogEntry? entry)
    {
        if (entry != null)
        {
            HistoryLog.Remove(entry);
        }
    }

    [RelayCommand]
    private void RetryHistoryEntry(DownloadLogEntry? entry)
    {
        if (entry?.Instance != null)
        {
            _ = _queueManager.StartDownloadAsync(entry.Instance);
        }
    }

    [RelayCommand]
    private async Task PlayHistoryEntryAsync(DownloadLogEntry? entry)
    {
        if (entry?.Instance != null)
        {
            var target = _instanceManager != null
                ? (await _instanceManager.GetByIdAsync(entry.Instance.Id).ConfigureAwait(true) ?? entry.Instance)
                : entry.Instance;
            OnOpenInstanceRequested?.Invoke(target);
        }
    }
}
