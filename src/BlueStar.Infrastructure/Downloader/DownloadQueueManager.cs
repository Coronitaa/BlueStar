using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using BlueStar.Core.Interfaces;
using BlueStar.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;

namespace BlueStar.Infrastructure.Downloader;

// ── Enums ─────────────────────────────────────────────────────────────────────

public enum DownloadJobStatus
{
    Queued,
    Downloading,
    Paused,
    Completed,
    Failed,
    Canceled
}

// ── Log entry model ───────────────────────────────────────────────────────────

public sealed class DownloadLogEntry
{
    public string GameName { get; init; } = "";
    public DownloadJobStatus Status { get; init; }
    public string Message { get; init; } = "";
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.Now;
    public GameInstance? Instance { get; init; }

    public string FormattedTime => Timestamp.ToString("HH:mm:ss");

    public string StatusEmoji => Status switch
    {
        DownloadJobStatus.Completed => "✅",
        DownloadJobStatus.Failed    => "❌",
        DownloadJobStatus.Canceled  => "⚠",
        _                           => "ℹ"
    };
}

// ── Active job model ──────────────────────────────────────────────────────────

/// <summary>
/// Observable model for a single active or recently finished download job.
/// </summary>
public partial class DownloadJobItem : ObservableObject
{
    [ObservableProperty] private GameInstance _instance = null!;
    [ObservableProperty] private double _percentage;
    [ObservableProperty] private double _speedBytesPerSec;
    [ObservableProperty] private long _downloadedBytes;
    [ObservableProperty] private long _totalBytes;
    [ObservableProperty] private string _statusMessage = "Queued";
    [ObservableProperty] private DownloadJobStatus _jobStatus = DownloadJobStatus.Queued;
    [ObservableProperty] private string? _errorDetail;
    [ObservableProperty] private DateTimeOffset _startedAt = DateTimeOffset.Now;
    [ObservableProperty] private DateTimeOffset? _completedAt;
    [ObservableProperty] private double _writeBytesPerSec;
    [ObservableProperty] private int _activeConnections;
    [ObservableProperty] private TimeSpan? _estimatedTimeRemaining;
    [ObservableProperty] private string? _phase;
    [ObservableProperty] private int _totalChunks;
    [ObservableProperty] private int _completedChunks;
    [ObservableProperty] private uint _currentDepotId;
    [ObservableProperty] private int _currentDepotIndex;
    [ObservableProperty] private int _totalDepots;

    // ── Computed state flags ─────────────────────────────────────────────────
    public bool IsDownloading => JobStatus == DownloadJobStatus.Downloading;
    public bool IsPaused      => JobStatus == DownloadJobStatus.Paused;
    public bool IsCompleted   => JobStatus == DownloadJobStatus.Completed;
    public bool IsFailed      => JobStatus == DownloadJobStatus.Failed;
    public bool IsCanceled    => JobStatus == DownloadJobStatus.Canceled;
    public bool IsActive      => JobStatus is DownloadJobStatus.Downloading or DownloadJobStatus.Queued;
    public bool CanPause      => JobStatus == DownloadJobStatus.Downloading;
    public bool CanResume     => JobStatus == DownloadJobStatus.Paused;
    public bool CanRetry      => JobStatus is DownloadJobStatus.Failed or DownloadJobStatus.Canceled;
    public bool CanRemove     => JobStatus is DownloadJobStatus.Completed or DownloadJobStatus.Failed or DownloadJobStatus.Canceled;

    partial void OnJobStatusChanged(DownloadJobStatus value)
    {
        OnPropertyChanged(nameof(IsDownloading));
        OnPropertyChanged(nameof(IsPaused));
        OnPropertyChanged(nameof(IsCompleted));
        OnPropertyChanged(nameof(IsFailed));
        OnPropertyChanged(nameof(IsCanceled));
        OnPropertyChanged(nameof(IsActive));
        OnPropertyChanged(nameof(CanPause));
        OnPropertyChanged(nameof(CanResume));
        OnPropertyChanged(nameof(CanRetry));
        OnPropertyChanged(nameof(CanRemove));
    }

    // ── Formatted display ────────────────────────────────────────────────────
    public string FormattedSpeed => SpeedBytesPerSec switch
    {
        > 1024 * 1024 * 1024 => $"{SpeedBytesPerSec / (1024.0 * 1024.0 * 1024.0):F1} GB/s",
        > 1024 * 1024        => $"{SpeedBytesPerSec / (1024.0 * 1024.0):F1} MB/s",
        > 1024               => $"{SpeedBytesPerSec / 1024.0:F1} KB/s",
        _                    => $"{SpeedBytesPerSec:F0} B/s"
    };

    public string FormattedWriteSpeed => WriteBytesPerSec switch
    {
        > 1024 * 1024 * 1024 => $"{WriteBytesPerSec / (1024.0 * 1024.0 * 1024.0):F1} GB/s",
        > 1024 * 1024        => $"{WriteBytesPerSec / (1024.0 * 1024.0):F1} MB/s",
        > 1024               => $"{WriteBytesPerSec / 1024.0:F1} KB/s",
        _                    => $"{WriteBytesPerSec:F0} B/s"
    };

    public string FormattedEta => EstimatedTimeRemaining switch
    {
        null => "—",
        { TotalHours: >= 1 } eta => $"{(int)eta.TotalHours}h {eta.Minutes}m",
        { TotalMinutes: >= 1 } eta => $"{(int)eta.TotalMinutes}m {eta.Seconds}s",
        { } eta => $"{eta.Seconds}s",
    };

    public string FormattedProgress =>
        TotalBytes > 0
            ? $"{FormatBytes(DownloadedBytes)} / {FormatBytes(TotalBytes)}  ({Percentage:F1}%)"
            : Percentage > 0 ? $"{Percentage:F1}%" : "Preparing...";

    public string FormattedTotalSize =>
        TotalBytes > 0 ? FormatBytes(TotalBytes) : "—";

    public string FormattedDepotProgress =>
        TotalDepots > 0 ? $"Depot {CurrentDepotIndex + 1}/{TotalDepots}" : "";

    public void NotifyMetricsChanged()
    {
        OnPropertyChanged(nameof(FormattedSpeed));
        OnPropertyChanged(nameof(FormattedWriteSpeed));
        OnPropertyChanged(nameof(FormattedEta));
        OnPropertyChanged(nameof(FormattedProgress));
        OnPropertyChanged(nameof(FormattedTotalSize));
        OnPropertyChanged(nameof(FormattedDepotProgress));
    }

    private static string FormatBytes(long bytes) => bytes switch
    {
        > 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024.0 * 1024.0):F2} GB",
        > 1024L * 1024        => $"{bytes / (1024.0 * 1024.0):F1} MB",
        _                     => $"{bytes / 1024.0:F0} KB"
    };
}

// ── Queue manager ─────────────────────────────────────────────────────────────

/// <summary>
/// Singleton download queue manager. Manages active downloads and a history log.
/// Supports pause (cancel + mark paused), resume (re-enqueue), retry and remove.
/// </summary>
public class DownloadQueueManager
{
    private readonly IDownloadProvider _downloadProvider;
    private readonly IInstanceManager? _instanceManager;
    private readonly INotificationService? _notificationService;
    private readonly ILogger<DownloadQueueManager> _logger;
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _ctsMap = new();
    private readonly SynchronizationContext? _uiContext;

    /// <summary>All jobs (active + finished) — the UI binds to this for the active card list.</summary>
    public ObservableCollection<DownloadJobItem> Queue { get; } = [];

    /// <summary>Completed, failed, and canceled jobs — shown in the log section.</summary>
    public ObservableCollection<DownloadLogEntry> HistoryLog { get; } = [];

    /// <summary>Raised whenever a job is added, removed, or its execution status changes.</summary>
    public event EventHandler? QueueChanged;

    public DownloadQueueManager(
        IDownloadProvider downloadProvider,
        ILogger<DownloadQueueManager> logger,
        IInstanceManager? instanceManager = null,
        INotificationService? notificationService = null)
    {
        _downloadProvider = downloadProvider;
        _logger = logger;
        _instanceManager = instanceManager;
        _notificationService = notificationService;
        _uiContext = SynchronizationContext.Current;
    }

    private void RunOnUi(Action action)
    {
        if (_uiContext is null || SynchronizationContext.Current == _uiContext)
        {
            action();
        }
        else
        {
            _uiContext.Post(_ => action(), null);
        }
    }

    /// <summary>Notifies listeners on the UI thread that the queue or a job state has changed.</summary>
    public void NotifyQueueChanged() => RunOnUi(() => QueueChanged?.Invoke(this, EventArgs.Empty));

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>Enqueues a new download or re-starts an existing one.</summary>
    public async Task StartDownloadAsync(GameInstance instance)
    {
        // If already in queue and active, skip
        var existing = Queue.FirstOrDefault(q => q.Instance.Id == instance.Id);
        if (existing is not null && existing.IsActive) return;

        if (existing is null)
        {
            existing = new DownloadJobItem { Instance = instance, StatusMessage = "Queued..." };
            var toAdd = existing;
            RunOnUi(() =>
            {
                Queue.Add(toAdd);
                NotifyQueueChanged();
            });
        }
        else
        {
            // Reset existing job metrics and instance for a fresh / reinstall run
            existing.Instance = instance;
            existing.ErrorDetail = null;
            existing.Percentage = 0;
            existing.DownloadedBytes = 0;
            existing.SpeedBytesPerSec = 0;
            existing.CompletedAt = null;
            existing.NotifyMetricsChanged();
        }

        existing.JobStatus = DownloadJobStatus.Queued;
        existing.StatusMessage = "Starting download...";
        existing.StartedAt = DateTimeOffset.Now;
        NotifyQueueChanged();

        _notificationService?.ShowInfo("Download Started", $"{instance.Name} added to the download queue.");

        await RunDownloadAsync(existing, instance).ConfigureAwait(false);
    }

    /// <summary>Pauses an active download (cancels + marks as paused; resumes later with RetryAsync).</summary>
    public async Task PauseAsync(Guid instanceId)
    {
        var item = Queue.FirstOrDefault(q => q.Instance.Id == instanceId);
        if (item is null || !item.CanPause) return;

        if (_ctsMap.TryGetValue(instanceId, out var cts))
            cts.Cancel();

        await _downloadProvider.CancelAsync(instanceId).ConfigureAwait(false);

        item.JobStatus = DownloadJobStatus.Paused;
        item.StatusMessage = "Paused — click Resume to continue";
        NotifyQueueChanged();
        _logger.LogInformation("Paused download for {Game}", item.Instance.Name);
    }

    /// <summary>Resumes a paused download.</summary>
    public Task ResumeAsync(Guid instanceId)
    {
        var item = Queue.FirstOrDefault(q => q.Instance.Id == instanceId);
        if (item is null || !item.CanResume) return Task.CompletedTask;

        _logger.LogInformation("Resuming download for {Game}", item.Instance.Name);
        return StartDownloadAsync(item.Instance);
    }

    /// <summary>Retries a failed or canceled download.</summary>
    public Task RetryAsync(Guid instanceId)
    {
        var item = Queue.FirstOrDefault(q => q.Instance.Id == instanceId);
        if (item is null || !item.CanRetry) return Task.CompletedTask;

        _logger.LogInformation("Retrying download for {Game}", item.Instance.Name);
        return StartDownloadAsync(item.Instance);
    }

    /// <summary>Cancels an active download.</summary>
    public async Task CancelAsync(Guid instanceId)
    {
        if (_ctsMap.TryGetValue(instanceId, out var cts))
            cts.Cancel();

        await _downloadProvider.CancelAsync(instanceId).ConfigureAwait(false);
    }

    /// <summary>Cancels if active, then removes the job from the queue.</summary>
    public async Task CancelOrRemoveJobAsync(Guid instanceId)
    {
        await CancelAsync(instanceId).ConfigureAwait(false);

        var item = Queue.FirstOrDefault(q => q.Instance.Id == instanceId);
        if (item is not null)
        {
            RunOnUi(() =>
            {
                Queue.Remove(item);
                NotifyQueueChanged();
            });
        }
    }

    /// <summary>Removes a finished/failed/canceled job from the active queue list.</summary>
    public void RemoveJob(Guid instanceId)
    {
        var item2 = Queue.FirstOrDefault(q => q.Instance.Id == instanceId);
        if (item2 is not null && item2.CanRemove)
        {
            RunOnUi(() =>
            {
                Queue.Remove(item2);
                NotifyQueueChanged();
            });
        }
    }

    /// <summary>Clears completed download jobs from history and finished queue cards.</summary>
    public void ClearCompleted()
    {
        RunOnUi(() =>
        {
            var toRemoveLogs = HistoryLog.Where(h => h.Status == DownloadJobStatus.Completed).ToList();
            foreach (var log in toRemoveLogs) HistoryLog.Remove(log);

            var toRemoveQueue = Queue.Where(q => q.JobStatus == DownloadJobStatus.Completed).ToList();
            foreach (var q in toRemoveQueue) Queue.Remove(q);

            NotifyQueueChanged();
        });
    }

    /// <summary>Clears failed and canceled download jobs from history and queue cards.</summary>
    public void ClearFailed()
    {
        RunOnUi(() =>
        {
            var toRemoveLogs = HistoryLog.Where(h => h.Status is DownloadJobStatus.Failed or DownloadJobStatus.Canceled).ToList();
            foreach (var log in toRemoveLogs) HistoryLog.Remove(log);

            var toRemoveQueue = Queue.Where(q => q.JobStatus is DownloadJobStatus.Failed or DownloadJobStatus.Canceled).ToList();
            foreach (var q in toRemoveQueue) Queue.Remove(q);

            NotifyQueueChanged();
        });
    }

    /// <summary>Clears all completed, failed, and canceled history entries and inactive queue cards.</summary>
    public void ClearAll()
    {
        RunOnUi(() =>
        {
            HistoryLog.Clear();
            var toRemoveQueue = Queue.Where(q => q.CanRemove).ToList();
            foreach (var q in toRemoveQueue) Queue.Remove(q);

            NotifyQueueChanged();
        });
    }

    /// <summary>Clears all history log entries.</summary>
    public void ClearHistory() => ClearAll();

    // ── Internal ──────────────────────────────────────────────────────────────

    private async Task RunDownloadAsync(DownloadJobItem job, GameInstance instance)
    {
        var cts = new CancellationTokenSource();
        _ctsMap[instance.Id] = cts;

        RunOnUi(() =>
        {
            job.Percentage = 0;
            job.DownloadedBytes = 0;
            job.SpeedBytesPerSec = 0;
            job.CompletedAt = null;
            job.JobStatus = DownloadJobStatus.Downloading;
            job.StatusMessage = "Downloading...";
            job.NotifyMetricsChanged();
            NotifyQueueChanged();
        });

        var progress = new Progress<DownloadProgress>(p =>
        {
            RunOnUi(() =>
            {
                if (job.JobStatus != DownloadJobStatus.Downloading) return;
                job.DownloadedBytes = p.DownloadedBytes;
                job.TotalBytes = p.TotalBytes;
                job.SpeedBytesPerSec = p.Speed;
                job.Percentage = p.Percentage;
                job.WriteBytesPerSec = p.WriteBytesPerSec;
                job.ActiveConnections = p.ActiveConnections;
                job.EstimatedTimeRemaining = p.EstimatedTimeRemaining;
                job.Phase = p.Phase;
                job.TotalChunks = p.TotalChunks;
                job.CompletedChunks = p.CompletedChunks;
                job.CurrentDepotId = p.CurrentDepotId;
                job.CurrentDepotIndex = p.CurrentDepotIndex;
                job.TotalDepots = p.TotalDepots;
                if (!string.IsNullOrWhiteSpace(p.CurrentFile))
                    job.StatusMessage = p.CurrentFile;
                job.NotifyMetricsChanged();
            });
        });

        try
        {
            await _downloadProvider.DownloadAsync(instance, progress, cts.Token).ConfigureAwait(true);

            RunOnUi(() =>
            {
                job.JobStatus = DownloadJobStatus.Completed;
                job.Percentage = 100;
                job.StatusMessage = "Completed ✅";
                job.CompletedAt = DateTimeOffset.Now;
                job.NotifyMetricsChanged();
                NotifyQueueChanged();
            });

            // Update game instance status and depot installed flags in storage
            if (_instanceManager is not null)
            {
                try
                {
                    var existing = await _instanceManager.GetByIdAsync(instance.Id, CancellationToken.None).ConfigureAwait(false) ?? instance;
                    var downloadedDepotIds = instance.Depots.Select(d => d.DepotId).ToHashSet();

                    var updatedDepots = existing.Depots.Count > 0
                        ? existing.Depots.Select(d => (downloadedDepotIds.Contains(d.DepotId) || d.IsDownloaded) ? d with { IsDownloaded = true } : d).ToList()
                        : instance.Depots.Select(d => d with { IsDownloaded = true }).ToList();

                    var updatedDlcs = existing.Dlcs.Select(dlc =>
                    {
                        var dlcDepots = dlc.Depots
                            .Select(d => (downloadedDepotIds.Contains(d.DepotId) || d.IsDownloaded) ? d with { IsDownloaded = true } : d)
                            .ToList();
                        bool allDlcsDepotsDownloaded = dlcDepots.Count > 0 && dlcDepots.All(d => d.IsDownloaded);
                        return dlc with
                        {
                            Depots = dlcDepots.AsReadOnly(),
                            IsInstalled = dlc.IsInstalled || allDlcsDepotsDownloaded
                        };
                    }).ToList();

                    bool anyDepotNotDownloaded = updatedDepots.Any(d => !d.IsDownloaded);

                    var updatedInstance = existing with
                    {
                        Status = InstanceStatus.Ready,
                        Depots = updatedDepots.AsReadOnly(),
                        Dlcs = updatedDlcs.AsReadOnly(),
                        SourceArchivePath = (anyDepotNotDownloaded && !string.IsNullOrWhiteSpace(existing.SourceArchivePath))
                            ? existing.SourceArchivePath
                            : null
                    };

                    await _instanceManager.UpdateAsync(updatedInstance, CancellationToken.None).ConfigureAwait(false);
                    job.Instance = updatedInstance;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to update instance status to Ready for {Game}", instance.Name);
                }
            }

            AddToLog(new DownloadLogEntry
            {
                GameName = instance.Name,
                Status = DownloadJobStatus.Completed,
                Message = $"Downloaded successfully ({job.FormattedTotalSize})",
                Instance = instance
            });

            _logger.LogInformation("Download finished for {Game}", instance.Name);
            _notificationService?.ShowSuccess("Download Completed", $"{instance.Name} downloaded and installed successfully.");
        }
        catch (OperationCanceledException) when (job.IsPaused)
        {
            // Already marked Paused by PauseAsync — don't log as canceled
        }
        catch (OperationCanceledException)
        {
            job.JobStatus = DownloadJobStatus.Canceled;
            job.StatusMessage = "Canceled";
            job.CompletedAt = DateTimeOffset.Now;
            NotifyQueueChanged();

            AddToLog(new DownloadLogEntry
            {
                GameName = instance.Name,
                Status = DownloadJobStatus.Canceled,
                Message = "Download canceled by user",
                Instance = instance
            });

            _logger.LogInformation("Download canceled for {Game}", instance.Name);
        }
        catch (Exception ex)
        {
            job.JobStatus = DownloadJobStatus.Failed;
            job.StatusMessage = "Failed ❌";
            job.ErrorDetail = ex.Message;
            job.CompletedAt = DateTimeOffset.Now;
            NotifyQueueChanged();

            AddToLog(new DownloadLogEntry
            {
                GameName = instance.Name,
                Status = DownloadJobStatus.Failed,
                Message = $"Error: {ex.Message}",
                Instance = instance
            });

            _logger.LogError(ex, "Download failed for {Game}", instance.Name);
            _notificationService?.ShowError("Download Error", $"Failed to download {instance.Name}: {ex.Message}");
        }
        finally
        {
            _ctsMap.TryRemove(instance.Id, out _);
        }
    }

    private void AddToLog(DownloadLogEntry entry) =>
        RunOnUi(() => HistoryLog.Insert(0, entry)); // newest first

    private void ClearHistoryOnUi() =>
        RunOnUi(() => HistoryLog.Clear());
}
