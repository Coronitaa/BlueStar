using System;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using BlueStar.Core.Models;

namespace BlueStar.Core.Interfaces;

/// <summary>
/// Service contract for managing, monitoring, and executing asynchronous background tasks in BlueStar.
/// </summary>
public interface IBackgroundTaskService
{
    /// <summary>
    /// Live collection of all queued, active, and recently completed background tasks.
    /// </summary>
    ReadOnlyObservableCollection<BackgroundTaskItem> Tasks { get; }

    /// <summary>
    /// Gets whether any background task is currently active or queued.
    /// </summary>
    bool HasActiveTasks { get; }

    /// <summary>
    /// Gets the count of active or queued background tasks.
    /// </summary>
    int ActiveTasksCount { get; }

    /// <summary>
    /// Event raised when the collection of tasks or their active states change.
    /// </summary>
    event EventHandler? TasksChanged;

    /// <summary>
    /// Enqueues and executes a new background work task.
    /// </summary>
    /// <param name="title">Human-readable title for the task (e.g. 'Bulk Emulator Update').</param>
    /// <param name="instanceName">Optional name of the target game instance.</param>
    /// <param name="work">Asynchronous work delegate receiving progress and cancellation token.</param>
    /// <param name="instanceId">Optional ID of the target game instance.</param>
    /// <returns>Unique task identifier.</returns>
    Guid QueueTask(
        string title,
        string? instanceName,
        Func<IProgress<BackgroundTaskProgress>, CancellationToken, Task> work,
        Guid? instanceId = null);

    /// <summary>
    /// Cancels a running or queued background task.
    /// </summary>
    void CancelTask(Guid taskId);

    /// <summary>
    /// Clears completed, failed, or cancelled tasks from the list.
    /// </summary>
    void ClearCompleted();
}
