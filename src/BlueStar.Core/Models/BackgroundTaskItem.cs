using System;

namespace BlueStar.Core.Models;

/// <summary>
/// Status state of a background operation.
/// </summary>
public enum BackgroundTaskStatus
{
    Queued,
    Running,
    Completed,
    Failed,
    Cancelled
}

/// <summary>
/// Progress reporting payload for a background task.
/// </summary>
public sealed record BackgroundTaskProgress(
    double Percentage,
    string Message,
    string? CurrentStep = null
);

/// <summary>
/// Model representing an active or recent background task in BlueStar.
/// </summary>
public sealed class BackgroundTaskItem
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string? InstanceName { get; set; }
    public Guid? InstanceId { get; set; }
    public BackgroundTaskStatus Status { get; set; } = BackgroundTaskStatus.Queued;
    public double ProgressPercentage { get; set; }
    public string CurrentStepMessage { get; set; } = string.Empty;
    public string? ErrorMessage { get; set; }
    public DateTimeOffset StartTime { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? EndTime { get; set; }
    public bool CanCancel { get; set; } = true;
    public bool IsActive => Status is BackgroundTaskStatus.Queued or BackgroundTaskStatus.Running;
    public bool IsCompleted => Status is BackgroundTaskStatus.Completed;
    public bool IsFailed => Status is BackgroundTaskStatus.Failed;
}
