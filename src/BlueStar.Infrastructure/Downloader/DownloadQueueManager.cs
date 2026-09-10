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
    public bool CanCancel     => JobStatus is DownloadJobStatus.Downloading or DownloadJobStatus.Queued or DownloadJobStatus.Paused;
    public bool CanRemove     => true;

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
        OnPropertyChanged(nameof(CanCancel));
        OnPropertyChanged(nameof(CanRemove));
        OnPropertyChanged(nameof(FriendlyPhase));
        OnPropertyChanged(nameof(PhaseGlyph));
        OnPropertyChanged(nameof(StageDetail));
        OnPropertyChanged(nameof(IsIndeterminate));
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

    /// <summary>
    /// Gets the stage the job is in, in words. An update is a sequence — strip the layers, fetch
    /// the manifests, download, write, put the layers back — and a bare percentage told the user
    /// none of that, which made a long "0%" look like a hang.
    /// </summary>
    public string FriendlyPhase => JobStatus switch
    {
        DownloadJobStatus.Queued    => "Waiting in queue",
        DownloadJobStatus.Paused    => "Paused",
        DownloadJobStatus.Completed => "Finished",
        DownloadJobStatus.Failed    => "Failed",
        DownloadJobStatus.Canceled  => "Canceled",
        _ => (Phase ?? string.Empty).ToLowerInvariant() switch
        {
            "initializing" => "Connecting to Steam",
            "preparing"    => "Preparing files",
            "manifests"    => "Fetching manifests",
            "downloading"  => "Downloading",
            "verifying" or "validating" => "Verifying files",
            "installing"   => "Writing to disk",
            "completed"    => "Finishing up",
            "paused"       => "Paused",
            "failed"       => "Failed",
            _ => "Working"
        }
    };

    /// <summary>Gets the icon that goes with <see cref="FriendlyPhase"/>.</summary>
    public string PhaseGlyph => JobStatus switch
    {
        DownloadJobStatus.Queued    => "\U0001F551",
        DownloadJobStatus.Paused    => "\u23F8",
        DownloadJobStatus.Completed => "\u2705",
        DownloadJobStatus.Failed    => "\u274C",
        DownloadJobStatus.Canceled  => "\u26A0",
        _ => "\U0001F4E5"
    };

    /// <summary>
    /// Gets the secondary line under the phase: which depot, how many chunks, how fast the disk is
    /// keeping up. Falls back to the raw status message when there is nothing numeric to say.
    /// </summary>
    public string StageDetail
    {
        get
        {
            if (JobStatus is DownloadJobStatus.Completed or DownloadJobStatus.Failed or DownloadJobStatus.Canceled)
                return StatusMessage;

            var parts = new List<string>();

            if (TotalDepots > 0)
                parts.Add($"Depot {Math.Min(CurrentDepotIndex + 1, TotalDepots)} of {TotalDepots}");

            if (TotalChunks > 0)
                parts.Add($"{CompletedChunks:N0}/{TotalChunks:N0} chunks");

            if (ActiveConnections > 0)
                parts.Add($"{ActiveConnections} connection{(ActiveConnections == 1 ? "" : "s")}");

            if (WriteBytesPerSec > 0)
                parts.Add($"disk {FormattedWriteSpeed}");

            return parts.Count > 0 ? string.Join("  \u00B7  ", parts) : StatusMessage;
        }
    }

    /// <summary>Gets whether the progress bar should be indeterminate (no measurable total yet).</summary>
    public bool IsIndeterminate =>
        JobStatus == DownloadJobStatus.Downloading && TotalBytes <= 0 && Percentage <= 0;

    public void NotifyMetricsChanged()
    {
        OnPropertyChanged(nameof(FormattedSpeed));
        OnPropertyChanged(nameof(FormattedWriteSpeed));
        OnPropertyChanged(nameof(FormattedEta));
        OnPropertyChanged(nameof(FormattedProgress));
        OnPropertyChanged(nameof(FormattedTotalSize));
        OnPropertyChanged(nameof(FormattedDepotProgress));
        OnPropertyChanged(nameof(FriendlyPhase));
        OnPropertyChanged(nameof(PhaseGlyph));
        OnPropertyChanged(nameof(StageDetail));
        OnPropertyChanged(nameof(IsIndeterminate));
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
    private readonly DownloadStateManager? _stateManager;
    private readonly IEmulatorLifecycleService? _emulatorLifecycleService;
    private readonly ILogger<DownloadQueueManager> _logger;

    /// <summary>
    /// Serializes actual downloading to one job at a time.
    /// <para>
    /// This is not a preference, it is a correctness requirement: DepotDownloader's
    /// <c>ContentDownloader</c> keeps its configuration in STATIC fields
    /// (<c>Config.InstallDirectory</c>, <c>CancellationToken</c>, <c>cdnPool</c>, <c>steam3</c>),
    /// all of which are reassigned on every call. Two concurrent downloads therefore overwrite
    /// each other's install directory — the second game's files land in the first game's folder —
    /// and the first one to finish resets the shared cancellation token, leaving the other
    /// download impossible to cancel.
    /// </para>
    /// <para>
    /// Until ContentDownloader is instance-scoped, jobs wait here. This also makes the
    /// "Queued" state the UI already shows actually mean something.
    /// </para>
    /// </summary>
    private readonly SemaphoreSlim _downloadGate = new(1, 1);
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _ctsMap = new();
    private readonly ConcurrentDictionary<Guid, Task> _runningTasks = new();
    private readonly ConcurrentDictionary<Guid, bool> _pausedInstances = new();
    private readonly SynchronizationContext? _uiContext;

    /// <summary>All jobs (active + finished) — the UI binds to this for the active card list.</summary>
    public ObservableCollection<DownloadJobItem> Queue { get; } = [];

    /// <summary>Completed, failed, and canceled jobs — shown in the log section.</summary>
    public ObservableCollection<DownloadLogEntry> HistoryLog { get; } = [];

    /// <summary>Raised whenever a job is added, removed, or its execution status changes.</summary>
    public event EventHandler? QueueChanged;

    /// <summary>Fired on every speed sample update for real-time chart rendering.</summary>
    public event Action<double, double>? SpeedSampleReceived;

    public double TotalDownloadSpeed { get; private set; }
    public double TotalWriteSpeed { get; private set; }
    public double PeakDownloadSpeed { get; private set; }
    public double PeakWriteSpeed { get; private set; }

    public DownloadQueueManager(
        IDownloadProvider downloadProvider,
        ILogger<DownloadQueueManager> logger,
        IInstanceManager? instanceManager = null,
        INotificationService? notificationService = null,
        DownloadStateManager? stateManager = null,
        IEmulatorLifecycleService? emulatorLifecycleService = null)
    {
        _downloadProvider = downloadProvider;
        _logger = logger;
        _instanceManager = instanceManager;
        _notificationService = notificationService;
        _stateManager = stateManager;
        _emulatorLifecycleService = emulatorLifecycleService;
        _uiContext = SynchronizationContext.Current;
    }

    private void UpdateTelemetry(double netSpeed, double writeSpeed)
    {
        TotalDownloadSpeed = Math.Max(0, netSpeed);
        TotalWriteSpeed = Math.Max(0, writeSpeed);
        if (netSpeed > PeakDownloadSpeed) PeakDownloadSpeed = netSpeed;
        if (writeSpeed > PeakWriteSpeed) PeakWriteSpeed = writeSpeed;

        SpeedSampleReceived?.Invoke(TotalDownloadSpeed, TotalWriteSpeed);
    }

    /// <summary>
    /// Scans for interrupted downloads from previous sessions and restores them to the queue in a Paused state.
    /// </summary>
    public async Task RestorePendingDownloadsAsync(CancellationToken ct = default)
    {
        if (_stateManager == null || _instanceManager == null) return;

        try
        {
            var pending = _stateManager.GetAllPendingDownloads();
            foreach (var (instanceId, state) in pending)
            {
                if (ct.IsCancellationRequested) break;

                // Check if already in queue
                if (Queue.Any(q => q.Instance.Id == instanceId)) continue;

                var instance = await _instanceManager.GetByIdAsync(instanceId, ct).ConfigureAwait(false);
                if (instance == null) continue;

                if (instance.Status == InstanceStatus.Ready && instance.Depots.All(d => d.IsDownloaded))
                {
                    _stateManager.ClearState(instanceId);
                    continue;
                }

                var pct = state.TotalBytes > 0
                    ? Math.Min(99.0, (double)state.DownloadedBytes / state.TotalBytes * 100.0)
                    : 0.0;

                var item = new DownloadJobItem
                {
                    Instance = instance,
                    JobStatus = DownloadJobStatus.Paused,
                    StatusMessage = "Paused (interrupted) — click Resume to continue",
                    TotalBytes = state.TotalBytes,
                    DownloadedBytes = state.DownloadedBytes,
                    Percentage = pct,
                    StartedAt = state.StartedAt
                };

                item.NotifyMetricsChanged();

                RunOnUi(() =>
                {
                    Queue.Add(item);
                    NotifyQueueChanged();
                });

                _logger.LogInformation("Restored pending download for {GameName} ({Pct:F1}%)", instance.Name, pct);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to restore pending downloads");
        }
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
    public Task StartDownloadAsync(GameInstance instance)
    {
        // If already in queue and active, skip
        var existing = Queue.FirstOrDefault(q => q.Instance.Id == instance.Id);
        if (existing is not null && existing.IsActive) return Task.CompletedTask;

        bool isResume = false;
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
            isResume = existing.JobStatus == DownloadJobStatus.Paused;
            existing.Instance = instance;
            existing.ErrorDetail = null;
            existing.SpeedBytesPerSec = 0;
            existing.WriteBytesPerSec = 0;
            existing.CompletedAt = null;
            if (!isResume && existing.JobStatus != DownloadJobStatus.Downloading)
            {
                existing.Percentage = 0;
                existing.DownloadedBytes = 0;
            }
            existing.NotifyMetricsChanged();
        }

        existing.JobStatus = DownloadJobStatus.Queued;
        existing.StatusMessage = isResume ? "Resuming download..." : "Starting download...";
        existing.StartedAt = DateTimeOffset.Now;
        NotifyQueueChanged();

        if (!isResume)
        {
            _notificationService?.ShowInfo("Download Started", $"{instance.Name} added to the download queue.");
        }
        else
        {
            _notificationService?.ShowInfo("Download Resumed", $"Resuming download for {instance.Name}.");
        }

        _ = RunDownloadAsync(existing, instance);
        return Task.CompletedTask;
    }


    /// <summary>Pauses an active download instantly (cancels + marks as paused; resumes later with ResumeAsync).</summary>
    public Task PauseAsync(Guid instanceId)
    {
        var item = Queue.FirstOrDefault(q => q.Instance.Id == instanceId);
        if (item is null || !item.CanPause) return Task.CompletedTask;

        _pausedInstances[instanceId] = true;

        if (_ctsMap.TryGetValue(instanceId, out var cts))
        {
            try { cts.Cancel(); } catch { }
        }

        RunOnUi(() =>
        {
            item.JobStatus = DownloadJobStatus.Paused;
            item.StatusMessage = "Paused — click Resume to continue";
            item.SpeedBytesPerSec = 0;
            item.WriteBytesPerSec = 0;
            item.NotifyMetricsChanged();
            UpdateTelemetry(0, 0);
            NotifyQueueChanged();
        });

        _logger.LogInformation("Paused download for {Game}", item.Instance.Name);

        _ = Task.Run(async () =>
        {
            try
            {
                await _downloadProvider.CancelAsync(instanceId).ConfigureAwait(false);
            }
            catch { }
        });

        return Task.CompletedTask;
    }

    /// <summary>Resumes a paused download.</summary>
    public Task ResumeAsync(Guid instanceId)
    {
        var item = Queue.FirstOrDefault(q => q.Instance.Id == instanceId);
        if (item is null || !item.CanResume) return Task.CompletedTask;

        _pausedInstances.TryRemove(instanceId, out _);

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

    /// <summary>Cancels an active or paused download.</summary>
    public Task CancelAsync(Guid instanceId)
    {
        _pausedInstances.TryRemove(instanceId, out _);
        _stateManager?.ClearState(instanceId);

        var item = Queue.FirstOrDefault(q => q.Instance.Id == instanceId);
        if (item is not null && (item.IsPaused || item.JobStatus == DownloadJobStatus.Queued))
        {
            RunOnUi(() =>
            {
                item.JobStatus = DownloadJobStatus.Canceled;
                item.StatusMessage = "Canceled";
                item.SpeedBytesPerSec = 0;
                item.WriteBytesPerSec = 0;
                item.CompletedAt = DateTimeOffset.Now;
                item.NotifyMetricsChanged();
                NotifyQueueChanged();
            });

            AddToLog(new DownloadLogEntry
            {
                GameName = item.Instance.Name,
                Status = DownloadJobStatus.Canceled,
                Message = "Download canceled by user",
                Instance = item.Instance
            });
        }

        if (_ctsMap.TryGetValue(instanceId, out var cts))
        {
            try { cts.Cancel(); } catch { }
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await _downloadProvider.CancelAsync(instanceId).ConfigureAwait(false);
            }
            catch { }
        });

        return Task.CompletedTask;
    }

    /// <summary>Cancels if active, then removes the job from the queue.</summary>
    public async Task CancelOrRemoveJobAsync(Guid instanceId)
    {
        _pausedInstances.TryRemove(instanceId, out _);
        _stateManager?.ClearState(instanceId);

        try
        {
            var workDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "BlueStar", "DepotWork", instanceId.ToString());
            if (Directory.Exists(workDir))
            {
                Directory.Delete(workDir, true);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to clean DepotWork dir for {InstanceId}", instanceId);
        }

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

    /// <summary>Removes a job from the active queue list.</summary>
    public void RemoveJob(Guid instanceId)
    {
        _pausedInstances.TryRemove(instanceId, out _);
        _stateManager?.ClearState(instanceId);

        if (_ctsMap.TryGetValue(instanceId, out var cts))
        {
            try { cts.Cancel(); } catch { }
        }

        try
        {
            var workDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "BlueStar", "DepotWork", instanceId.ToString());
            if (Directory.Exists(workDir))
            {
                Directory.Delete(workDir, true);
            }
        }
        catch { }

        var item2 = Queue.FirstOrDefault(q => q.Instance.Id == instanceId);
        if (item2 is not null)
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
            foreach (var q in toRemoveQueue)
            {
                _stateManager?.ClearState(q.Instance.Id);
                Queue.Remove(q);
            }

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
            foreach (var q in toRemoveQueue)
            {
                _stateManager?.ClearState(q.Instance.Id);
                Queue.Remove(q);
            }

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
            foreach (var q in toRemoveQueue)
            {
                _stateManager?.ClearState(q.Instance.Id);
                Queue.Remove(q);
            }

            NotifyQueueChanged();
        });
    }

    /// <summary>Clears all history log entries.</summary>
    public void ClearHistory() => ClearAll();

    // ── Internal ──────────────────────────────────────────────────────────────

    private async Task RunDownloadAsync(DownloadJobItem job, GameInstance instance)
    {
        // If an earlier task for this instance is still cancelling or closing handles, wait for it cleanly
        if (_runningTasks.TryGetValue(instance.Id, out var prevTask) && !prevTask.IsCompleted)
        {
            try
            {
                await prevTask.ConfigureAwait(false);
            }
            catch { }
        }

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _runningTasks[instance.Id] = tcs.Task;

        var cts = new CancellationTokenSource();
        _ctsMap[instance.Id] = cts;

        RunOnUi(() =>
        {
            job.SpeedBytesPerSec = 0;
            job.WriteBytesPerSec = 0;
            job.CompletedAt = null;
            job.JobStatus = DownloadJobStatus.Downloading;
            job.StatusMessage = "Downloading...";
            job.NotifyMetricsChanged();
            NotifyQueueChanged();
        });

        // Wait for our turn in the queue. The job stays visible as "Queued" meanwhile.
        bool gateTaken = false;
        try
        {
            if (!await _downloadGate.WaitAsync(0).ConfigureAwait(false))
            {
                RunOnUi(() =>
                {
                    if (job.JobStatus == DownloadJobStatus.Downloading)
                    {
                        job.JobStatus = DownloadJobStatus.Queued;
                        job.StatusMessage = "Waiting for the active download to finish...";
                        job.NotifyMetricsChanged();
                        NotifyQueueChanged();
                    }
                });

                await _downloadGate.WaitAsync(cts.Token).ConfigureAwait(false);
            }
            gateTaken = true;
        }
        catch (OperationCanceledException)
        {
            // Cancelled or paused while still queued: never started, so just settle the job.
            bool wasPausedWhileQueued = _pausedInstances.TryRemove(instance.Id, out _) || job.IsPaused;
            RunOnUi(() =>
            {
                job.JobStatus = wasPausedWhileQueued ? DownloadJobStatus.Paused : DownloadJobStatus.Canceled;
                job.StatusMessage = wasPausedWhileQueued ? "Paused \u2014 click Resume to continue" : "Canceled";
                job.SpeedBytesPerSec = 0;
                job.NotifyMetricsChanged();
                NotifyQueueChanged();
            });
            _ctsMap.TryRemove(instance.Id, out _);
            _runningTasks.TryRemove(instance.Id, out _);
            tcs.TrySetResult();
            return;
        }

        RunOnUi(() =>
        {
            if (job.JobStatus == DownloadJobStatus.Queued)
            {
                job.JobStatus = DownloadJobStatus.Downloading;
                job.Phase = "Initializing";
                job.StatusMessage = "Connecting to Steam and resolving depots...";
                job.NotifyMetricsChanged();
                NotifyQueueChanged();
            }
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
                UpdateTelemetry(p.Speed, p.WriteBytesPerSec);
            });
        });

        try
        {
            // Task.Run + ConfigureAwait(false): DepotDownloaderProvider.DownloadAsync has a
            // synchronous path (no SourceArchivePath -> EnsureValidManifestsAsync never really
            // awaits) that would otherwise run ContentDownloader.InitializeSteam3 on the UI
            // thread. That call blocks on RunWaitCallbacks and, on a bad connection, on
            // Thread.Sleep with a backoff of up to 10 retries — freezing the window for ~55s.
            // Progress<T> and RunOnUi already marshal back to the UI thread, so nothing is lost.
            await Task.Run(() => _downloadProvider.DownloadAsync(instance, progress, cts.Token), cts.Token)
                .ConfigureAwait(false);

            RunOnUi(() =>
            {
                job.JobStatus = DownloadJobStatus.Completed;
                job.Percentage = 100;
                job.Phase = "Completed";
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

                    var installedManifestDate = BlueStar.Infrastructure.Services.GameUpdateDetectionHelper.GetInstalledManifestDate(existing);
                    var versionDate = installedManifestDate ?? existing.InstalledVersionDate ?? DateTimeOffset.UtcNow;

                    var updatedInstance = existing with
                    {
                        Status = InstanceStatus.Ready,
                        Depots = updatedDepots.AsReadOnly(),
                        Dlcs = updatedDlcs.AsReadOnly(),
                        UpdatedAt = DateTimeOffset.UtcNow,
                        InstalledVersionDate = versionDate,
                        SourceArchivePath = (anyDepotNotDownloaded && !string.IsNullOrWhiteSpace(existing.SourceArchivePath))
                            ? existing.SourceArchivePath
                            : null
                    };

                    await _instanceManager.UpdateAsync(updatedInstance, CancellationToken.None).ConfigureAwait(false);

                    // The depot files that just landed overwrote the pristine Steam binaries, so any
                    // emulator / DLC unlocker that the update flow stripped beforehand has to be put
                    // back on top of the NEW files. Doing it here (instead of in the update flow)
                    // guarantees it also runs when the app is restarted mid-download and the job is
                    // resumed later.
                    if (updatedInstance.AwaitingPostUpdateRedeploy && _emulatorLifecycleService != null)
                    {
                        RunOnUi(() =>
                        {
                            job.Phase = "Installing";
                            job.StatusMessage = "Reinstalling emulator and DLC unlocker...";
                            job.NotifyMetricsChanged();
                            NotifyQueueChanged();
                        });

                        try
                        {
                            var redeployProgress = new Progress<DeployProgress>(dp =>
                                RunOnUi(() =>
                                {
                                    if (!string.IsNullOrWhiteSpace(dp.Message)) job.StatusMessage = dp.Message;
                                    job.NotifyMetricsChanged();
                                }));

                            updatedInstance = await _emulatorLifecycleService
                                .RestoreAfterGameUpdateAsync(updatedInstance, redeployProgress, CancellationToken.None)
                                .ConfigureAwait(false);

                            _logger.LogInformation("Post-update redeploy completed for {Game}", instance.Name);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Post-update redeploy failed for {Game}", instance.Name);
                            _notificationService?.ShowError(
                                "Reinstall Required",
                                $"{instance.Name} was updated, but the emulator/DLC unlocker could not be reinstalled automatically. Reinstall them from the Emulator tab.");
                        }
                        finally
                        {
                            RunOnUi(() =>
                            {
                                job.StatusMessage = "Completed \u2705";
                                job.NotifyMetricsChanged();
                            });
                        }
                    }

                    job.Instance = updatedInstance;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to update instance status to Ready for {Game}", instance.Name);
                }
            }

            // Clear persistent download state since download has completed successfully
            _stateManager?.ClearState(instance.Id);

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
        catch (OperationCanceledException oce)
        {
            bool wasPaused = _pausedInstances.TryRemove(instance.Id, out _) || job.IsPaused;

            // A cancellation that did NOT come from our own token is not a user action.
            // DepotDownloader signals unrecoverable failures (manifest unavailable, CDN 403/404,
            // "failed to find any server with chunk X") by cancelling its *internal* linked token
            // and rethrowing OperationCanceledException. Reporting those as "canceled by user" is
            // what makes downloads look like they stopped at 0% for no reason.
            bool userRequested = wasPaused || cts.IsCancellationRequested;

            if (!userRequested)
            {
                var detail = string.IsNullOrWhiteSpace(oce.Message) || oce is TaskCanceledException
                    ? "The download was aborted by the depot downloader. This usually means a depot manifest or chunk could not be retrieved from the Steam CDN (missing/expired depot key, region block, or a network drop). Check the log for details and retry."
                    : oce.Message;

                RunOnUi(() =>
                {
                    job.JobStatus = DownloadJobStatus.Failed;
                    job.StatusMessage = "Failed \u274C";
                    job.ErrorDetail = detail;
                    job.SpeedBytesPerSec = 0;
                    job.WriteBytesPerSec = 0;
                    job.CompletedAt = DateTimeOffset.Now;
                    job.NotifyMetricsChanged();
                    NotifyQueueChanged();
                });

                AddToLog(new DownloadLogEntry
                {
                    GameName = instance.Name,
                    Status = DownloadJobStatus.Failed,
                    Message = $"Download aborted: {detail}",
                    Instance = instance
                });

                _logger.LogError(oce, "Download aborted (not user-initiated) for {Game}", instance.Name);
                _notificationService?.ShowError("Download Failed", $"{instance.Name} could not be downloaded. {detail}");
            }
            else if (wasPaused)
            {
                // Download was paused by user — do NOT log as canceled in Recent Activity!
                RunOnUi(() =>
                {
                    if (job.JobStatus != DownloadJobStatus.Downloading && job.JobStatus != DownloadJobStatus.Queued)
                    {
                        job.JobStatus = DownloadJobStatus.Paused;
                        job.StatusMessage = "Paused — click Resume to continue";
                        job.SpeedBytesPerSec = 0;
                        job.WriteBytesPerSec = 0;
                        job.NotifyMetricsChanged();
                        NotifyQueueChanged();
                    }
                });
                _logger.LogInformation("Download paused for {Game}", instance.Name);
            }
            else
            {
                // Explicitly canceled by user
                _stateManager?.ClearState(instance.Id);

                RunOnUi(() =>
                {
                    job.JobStatus = DownloadJobStatus.Canceled;
                    job.StatusMessage = "Canceled";
                    job.SpeedBytesPerSec = 0;
                    job.WriteBytesPerSec = 0;
                    job.CompletedAt = DateTimeOffset.Now;
                    job.NotifyMetricsChanged();
                    NotifyQueueChanged();
                });

                AddToLog(new DownloadLogEntry
                {
                    GameName = instance.Name,
                    Status = DownloadJobStatus.Canceled,
                    Message = "Download canceled by user",
                    Instance = instance
                });

                _logger.LogInformation("Download canceled for {Game}", instance.Name);
            }
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
            if (gateTaken)
            {
                try { _downloadGate.Release(); } catch (SemaphoreFullException) { }
            }

            _ctsMap.TryRemove(instance.Id, out _);
            _runningTasks.TryRemove(instance.Id, out _);
            tcs.TrySetResult();

            if (!Queue.Any(q => q.IsDownloading))
            {
                RunOnUi(() => UpdateTelemetry(0, 0));
            }
        }
    }

    private void AddToLog(DownloadLogEntry entry) =>
        RunOnUi(() => HistoryLog.Insert(0, entry)); // newest first

    private void ClearHistoryOnUi() =>
        RunOnUi(() => HistoryLog.Clear());
}
