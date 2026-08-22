using System;
using System.Collections.ObjectModel;
using BlueStar.Core.Models;

namespace BlueStar.Core.Interfaces;

/// <summary>
/// Service contract for managing application-wide toast notifications.
/// </summary>
public interface INotificationService
{
    /// <summary>
    /// Active live notifications collection displayed in the UI.
    /// </summary>
    ReadOnlyObservableCollection<NotificationItem> Notifications { get; }

    /// <summary>
    /// Shows a new toast notification.
    /// </summary>
    void Show(string title, string message, NotificationType type = NotificationType.Info, TimeSpan? duration = null, string? actionText = null, Action? action = null);

    /// <summary>
    /// Convenience helper for success notifications.
    /// </summary>
    void ShowSuccess(string title, string message, TimeSpan? duration = null, string? actionText = null, Action? action = null);

    /// <summary>
    /// Convenience helper for info notifications.
    /// </summary>
    void ShowInfo(string title, string message, TimeSpan? duration = null, string? actionText = null, Action? action = null);

    /// <summary>
    /// Convenience helper for warning notifications.
    /// </summary>
    void ShowWarning(string title, string message, TimeSpan? duration = null, string? actionText = null, Action? action = null);

    /// <summary>
    /// Convenience helper for error notifications.
    /// </summary>
    void ShowError(string title, string message, TimeSpan? duration = null, string? actionText = null, Action? action = null);

    /// <summary>
    /// Dismisses a notification by its unique identifier.
    /// </summary>
    void Dismiss(Guid id);

    /// <summary>
    /// Dismisses all currently active notifications.
    /// </summary>
    void ClearAll();
}
