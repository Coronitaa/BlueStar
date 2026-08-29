using System.ComponentModel;
using System.Runtime.CompilerServices;

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
public sealed class BackgroundTaskItem : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    public Guid Id { get; init; } = Guid.NewGuid();
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string? InstanceName { get; set; }
    public Guid? InstanceId { get; set; }

    private BackgroundTaskStatus _status = BackgroundTaskStatus.Queued;
    public BackgroundTaskStatus Status
    {
        get => _status;
        set
        {
            if (_status != value)
            {
                _status = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsActive));
                OnPropertyChanged(nameof(IsCompleted));
                OnPropertyChanged(nameof(IsFailed));
            }
        }
    }

    private double _progressPercentage;
    public double ProgressPercentage
    {
        get => _progressPercentage;
        set
        {
            if (Math.Abs(_progressPercentage - value) > 0.001)
            {
                _progressPercentage = value;
                OnPropertyChanged();
            }
        }
    }

    private string _currentStepMessage = string.Empty;
    public string CurrentStepMessage
    {
        get => _currentStepMessage;
        set
        {
            if (_currentStepMessage != value)
            {
                _currentStepMessage = value;
                OnPropertyChanged();
            }
        }
    }

    private string? _errorMessage;
    public string? ErrorMessage
    {
        get => _errorMessage;
        set
        {
            if (_errorMessage != value)
            {
                _errorMessage = value;
                OnPropertyChanged();
            }
        }
    }

    public DateTimeOffset StartTime { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? EndTime { get; set; }
    public bool CanCancel { get; set; } = true;
    public bool IsActive => Status is BackgroundTaskStatus.Queued or BackgroundTaskStatus.Running;
    public bool IsCompleted => Status is BackgroundTaskStatus.Completed;
    public bool IsFailed => Status is BackgroundTaskStatus.Failed;
}
