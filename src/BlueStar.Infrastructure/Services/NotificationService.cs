using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BlueStar.Core.Interfaces;
using BlueStar.Core.Models;
using Microsoft.Extensions.Logging;

namespace BlueStar.Infrastructure.Services;

/// <summary>
/// Thread-safe implementation of <see cref="INotificationService"/> managing live UI notifications.
/// </summary>
public sealed class NotificationService : INotificationService
{
    private readonly ObservableCollection<NotificationItem> _notifications = [];
    private readonly ReadOnlyObservableCollection<NotificationItem> _readOnlyNotifications;
    private readonly SynchronizationContext? _uiContext;
    private readonly ILogger<NotificationService> _logger;

    public static Action<Action>? UiDispatcher { get; set; }

    /// <summary>
    /// Optional coordinator invoked when an item is requested to be dismissed.
    /// The coordinator triggers the UI exit transition and then invokes the provided onDismissed callback.
    /// If null (e.g. in unit tests or headless runs), dismissal occurs immediately and synchronously.
    /// </summary>
    public static Action<NotificationItem, Action>? DismissCoordinator { get; set; }

    public ReadOnlyObservableCollection<NotificationItem> Notifications => _readOnlyNotifications;

    public NotificationService(ILogger<NotificationService> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _readOnlyNotifications = new ReadOnlyObservableCollection<NotificationItem>(_notifications);
        _uiContext = SynchronizationContext.Current;
    }

    public void Show(string title, string message, NotificationType type = NotificationType.Info, TimeSpan? duration = null, string? actionText = null, Action? action = null)
    {
        var item = new NotificationItem
        {
            Title = title,
            Message = message,
            Type = type,
            Duration = duration ?? TimeSpan.FromSeconds(20),
            ActionText = actionText,
            Action = action,
            DismissAction = Dismiss
        };

        _logger.LogInformation("[Toast] [{Type}] {Title}: {Message}", type, title, message);

        PostToUi(() =>
        {
            // Limit max simultaneous active toasts to 3:
            // Oldest active toast initiates dismissal transition rather than disappearing abruptly
            var activeToasts = _notifications.Where(n => !n.IsDismissing).ToList();
            while (activeToasts.Count >= 3)
            {
                var oldest = activeToasts[0];
                activeToasts.RemoveAt(0);
                Dismiss(oldest.Id);
            }

            // Safety ceiling to prevent unbounded buildup during rapid bursts of notifications
            while (_notifications.Count >= 6)
            {
                var oldestDismissing = _notifications.FirstOrDefault(n => n.IsDismissing);
                if (oldestDismissing != null)
                {
                    _notifications.Remove(oldestDismissing);
                }
                else
                {
                    _notifications.RemoveAt(0);
                }
            }

            _notifications.Add(item);
        });

        if (item.Duration > TimeSpan.Zero)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(item.Duration).ConfigureAwait(false);
                    Dismiss(item.Id);
                }
                catch { }
            });
        }
    }

    public void ShowSuccess(string title, string message, TimeSpan? duration = null, string? actionText = null, Action? action = null)
    {
        Show(title, message, NotificationType.Success, duration ?? TimeSpan.FromSeconds(20), actionText, action);
    }

    public void ShowInfo(string title, string message, TimeSpan? duration = null, string? actionText = null, Action? action = null)
    {
        Show(title, message, NotificationType.Info, duration ?? TimeSpan.FromSeconds(20), actionText, action);
    }

    public void ShowWarning(string title, string message, TimeSpan? duration = null, string? actionText = null, Action? action = null)
    {
        Show(title, message, NotificationType.Warning, duration ?? TimeSpan.FromSeconds(20), actionText, action);
    }

    public void ShowError(string title, string message, TimeSpan? duration = null, string? actionText = null, Action? action = null)
    {
        Show(title, message, NotificationType.Error, duration ?? TimeSpan.FromSeconds(20), actionText, action);
    }

    public void Dismiss(Guid id)
    {
        PostToUi(() =>
        {
            var item = _notifications.FirstOrDefault(n => n.Id == id);
            if (item == null || item.IsDismissing) return;

            if (DismissCoordinator != null)
            {
                item.IsDismissing = true;
                DismissCoordinator(item, () =>
                {
                    PostToUi(() =>
                    {
                        _notifications.Remove(item);
                    });
                });
            }
            else
            {
                _notifications.Remove(item);
            }
        });
    }

    public void ClearAll()
    {
        PostToUi(() =>
        {
            if (_notifications.Count == 0) return;

            if (DismissCoordinator != null)
            {
                var activeItems = _notifications.Where(n => !n.IsDismissing).ToList();
                if (activeItems.Count == 0) return;

                foreach (var item in activeItems)
                {
                    item.IsDismissing = true;
                    DismissCoordinator(item, () =>
                    {
                        PostToUi(() =>
                        {
                            _notifications.Remove(item);
                        });
                    });
                }
            }
            else
            {
                _notifications.Clear();
            }
        });
    }

    private void PostToUi(Action action)
    {
        if (UiDispatcher != null)
        {
            try
            {
                UiDispatcher(action);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Error executing UI action in NotificationService via UiDispatcher");
            }
            return;
        }

        var targetCtx = _uiContext ?? SynchronizationContext.Current;
        if (targetCtx != null && SynchronizationContext.Current != targetCtx)
        {
            targetCtx.Post(_ =>
            {
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Error executing UI action in NotificationService");
                }
            }, null);
        }
        else
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Error executing UI action in NotificationService");
            }
        }
    }
}
