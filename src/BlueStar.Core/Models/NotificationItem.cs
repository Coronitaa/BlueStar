using System;

namespace BlueStar.Core.Models;

/// <summary>
/// Defines the visual severity and icon style of a toast notification.
/// </summary>
public enum NotificationType
{
    Info,
    Success,
    Warning,
    Error,
    Progress
}

/// <summary>
/// Represents a non-intrusive popup/toast notification in the UI.
/// </summary>
public sealed class NotificationItem
{
    /// <summary>
    /// Unique identifier for this notification instance.
    /// </summary>
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>
    /// Title of the notification.
    /// </summary>
    public required string Title { get; init; }

    /// <summary>
    /// Descriptive message of the notification.
    /// </summary>
    public required string Message { get; init; }

    /// <summary>
    /// Severity level / visual classification.
    /// </summary>
    public NotificationType Type { get; init; } = NotificationType.Info;

    /// <summary>
    /// Timestamp when notification was triggered.
    /// </summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Duration before auto-dismissal. Null or Zero means persistent until closed. Default is 20 seconds.
    /// </summary>
    public TimeSpan Duration { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Optional label for a primary interactive action button (e.g. "Actualizar", "Ver").
    /// </summary>
    public string? ActionText { get; init; }

    /// <summary>
    /// Optional callback invoked when the user clicks the action button.
    /// </summary>
    public Action? Action { get; init; }

    /// <summary>
    /// Command wrapper for the Action to bind directly in WPF.
    /// </summary>
    public System.Windows.Input.ICommand? ActionCommand => Action != null ? new NotificationActionCommand(Action) : null;

    /// <summary>
    /// Gets whether this notification has an interactive action.
    /// </summary>
    public bool HasAction => !string.IsNullOrWhiteSpace(ActionText) && Action != null;

    private sealed class NotificationActionCommand(Action action) : System.Windows.Input.ICommand
    {
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => action();
    }
}
