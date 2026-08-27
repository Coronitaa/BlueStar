using System;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BlueStar.Core.Interfaces;
using BlueStar.Core.Models;
using Microsoft.Extensions.Logging;

namespace BlueStar.Infrastructure.Services;

/// <summary>
/// Service managing application-wide background tasks, tracking live progress, execution state, and error handling.
/// </summary>
public sealed class BackgroundTaskService : IBackgroundTaskService
{
    private readonly ILogger<BackgroundTaskService> _logger;
    private readonly ObservableCollection<BackgroundTaskItem> _tasks = new();
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _cancellationSources = new();
    private readonly SynchronizationContext? _syncContext;

    public ReadOnlyObservableCollection<BackgroundTaskItem> Tasks { get; }
    public event EventHandler? TasksChanged;

    public bool HasActiveTasks => _tasks.Any(t => t.IsActive);
    public int ActiveTasksCount => _tasks.Count(t => t.IsActive);

    public BackgroundTaskService(ILogger<BackgroundTaskService> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _syncContext = SynchronizationContext.Current;
        Tasks = new ReadOnlyObservableCollection<BackgroundTaskItem>(_tasks);
    }

    /// <inheritdoc />
    public Guid QueueTask(
        string title,
        string? instanceName,
        Func<IProgress<BackgroundTaskProgress>, CancellationToken, Task> work,
        Guid? instanceId = null)
    {
        ArgumentNullException.ThrowIfNull(work);

        var taskItem = new BackgroundTaskItem
        {
            Title = title,
            InstanceName = instanceName,
            InstanceId = instanceId,
            Status = BackgroundTaskStatus.Queued,
            ProgressPercentage = 0,
            CurrentStepMessage = "Queued..."
        };

        var cts = new CancellationTokenSource();
        _cancellationSources[taskItem.Id] = cts;

        RunOnUI(() =>
        {
            _tasks.Insert(0, taskItem);
            TasksChanged?.Invoke(this, EventArgs.Empty);
        });

        // Launch worker in background
        _ = Task.Run(async () =>
        {
            try
            {
                RunOnUI(() =>
                {
                    taskItem.Status = BackgroundTaskStatus.Running;
                    taskItem.CurrentStepMessage = "Starting...";
                    TasksChanged?.Invoke(this, EventArgs.Empty);
                });

                var progress = new Progress<BackgroundTaskProgress>(p =>
                {
                    RunOnUI(() =>
                    {
                        taskItem.ProgressPercentage = Math.Clamp(p.Percentage, 0, 100);
                        taskItem.CurrentStepMessage = p.Message;
                        TasksChanged?.Invoke(this, EventArgs.Empty);
                    });
                });

                await work(progress, cts.Token).ConfigureAwait(false);

                RunOnUI(() =>
                {
                    taskItem.Status = BackgroundTaskStatus.Completed;
                    taskItem.ProgressPercentage = 100;
                    taskItem.EndTime = DateTimeOffset.UtcNow;
                    taskItem.CurrentStepMessage = "Completed successfully.";
                    TasksChanged?.Invoke(this, EventArgs.Empty);
                });
            }
            catch (OperationCanceledException)
            {
                RunOnUI(() =>
                {
                    taskItem.Status = BackgroundTaskStatus.Cancelled;
                    taskItem.EndTime = DateTimeOffset.UtcNow;
                    taskItem.CurrentStepMessage = "Operation cancelled by user.";
                    TasksChanged?.Invoke(this, EventArgs.Empty);
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Background task '{Title}' failed", taskItem.Title);
                RunOnUI(() =>
                {
                    taskItem.Status = BackgroundTaskStatus.Failed;
                    taskItem.ErrorMessage = ex.Message;
                    taskItem.EndTime = DateTimeOffset.UtcNow;
                    taskItem.CurrentStepMessage = $"Failed: {ex.Message}";
                    TasksChanged?.Invoke(this, EventArgs.Empty);
                });
            }
            finally
            {
                _cancellationSources.TryRemove(taskItem.Id, out _);
                cts.Dispose();
            }
        });

        return taskItem.Id;
    }

    /// <inheritdoc />
    public void CancelTask(Guid taskId)
    {
        if (_cancellationSources.TryGetValue(taskId, out var cts))
        {
            try
            {
                cts.Cancel();
            }
            catch (ObjectDisposedException) { }
        }
    }

    /// <inheritdoc />
    public void ClearCompleted()
    {
        RunOnUI(() =>
        {
            var toRemove = _tasks.Where(t => !t.IsActive).ToList();
            foreach (var item in toRemove)
            {
                _tasks.Remove(item);
            }
            TasksChanged?.Invoke(this, EventArgs.Empty);
        });
    }

    private void RunOnUI(Action action)
    {
        if (_syncContext != null && SynchronizationContext.Current != _syncContext)
        {
            _syncContext.Post(_ => action(), null);
        }
        else
        {
            action();
        }
    }
}
