using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using BlueStar.Core.Models;
using BlueStar.Infrastructure.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BlueStar.Infrastructure.Tests;

public class NotificationServiceLifecycleTests : IDisposable
{
    public NotificationServiceLifecycleTests()
    {
        // Reset static coordinators before each test
        NotificationService.DismissCoordinator = null;
        NotificationService.UiDispatcher = null;
    }

    public void Dispose()
    {
        NotificationService.DismissCoordinator = null;
        NotificationService.UiDispatcher = null;
    }

    [Fact]
    public void Show_WithoutCoordinator_LimitsActiveToastsToThreeSynchronously()
    {
        // Arrange
        var service = new NotificationService(NullLogger<NotificationService>.Instance);

        // Act: Show 4 toasts
        service.ShowInfo("Toast 1", "Msg 1");
        service.ShowInfo("Toast 2", "Msg 2");
        service.ShowInfo("Toast 3", "Msg 3");
        service.ShowInfo("Toast 4", "Msg 4");

        // Assert: Without coordinator, dismissal is synchronous; exactly 3 toasts remain (Toast 2, 3, 4)
        service.Notifications.Should().HaveCount(3);
        service.Notifications.Select(n => n.Title).Should().Equal("Toast 2", "Toast 3", "Toast 4");
    }

    [Fact]
    public void Show_WithDismissCoordinator_TransitionsOldestToDismissingOnFourthToast()
    {
        // Arrange
        var service = new NotificationService(NullLogger<NotificationService>.Instance);
        var pendingRemovals = new List<Action>();

        NotificationService.DismissCoordinator = (item, onDismissed) =>
        {
            // Capture dismissal callback without immediately executing it to inspect in-flight state
            pendingRemovals.Add(onDismissed);
        };

        // Act: Add 3 toasts
        service.ShowInfo("Toast 1", "Msg 1");
        service.ShowInfo("Toast 2", "Msg 2");
        service.ShowInfo("Toast 3", "Msg 3");

        service.Notifications.Should().HaveCount(3);
        service.Notifications.All(n => !n.IsDismissing).Should().BeTrue();

        // Act: Add 4th toast
        service.ShowInfo("Toast 4", "Msg 4");

        // Assert: Toast 1 is transitioning (IsDismissing = true), Toast 4 is added
        service.Notifications.Should().HaveCount(4);
        var toast1 = service.Notifications.First(n => n.Title == "Toast 1");
        toast1.IsDismissing.Should().BeTrue();

        // Other toasts remain active
        service.Notifications.Where(n => !n.IsDismissing).Select(n => n.Title)
            .Should().Equal("Toast 2", "Toast 3", "Toast 4");

        // Act: UI completes transition for Toast 1
        pendingRemovals.Should().HaveCount(1);
        pendingRemovals[0]();

        // Assert: Toast 1 is removed from collection, leaving exactly the 3 active toasts
        service.Notifications.Should().HaveCount(3);
        service.Notifications.Select(n => n.Title).Should().Equal("Toast 2", "Toast 3", "Toast 4");
    }

    [Fact]
    public void Dismiss_MultipleCalls_IsIdempotentAndSafe()
    {
        // Arrange
        var service = new NotificationService(NullLogger<NotificationService>.Instance);
        int coordinatorInvocations = 0;
        Action? removalAction = null;

        NotificationService.DismissCoordinator = (item, onDismissed) =>
        {
            coordinatorInvocations++;
            removalAction = onDismissed;
        };

        service.ShowInfo("Title", "Message");
        var id = service.Notifications.First().Id;

        // Act: Dismiss called repeatedly in rapid succession
        service.Dismiss(id);
        service.Dismiss(id);
        service.Dismiss(id);

        // Assert: Coordinator should be invoked exactly once
        coordinatorInvocations.Should().Be(1);

        // Execute removal
        removalAction?.Invoke();
        service.Notifications.Should().BeEmpty();

        // Dismiss after removal should not crash
        service.Dismiss(id);
        service.Notifications.Should().BeEmpty();
    }

    [Fact]
    public void ClearAll_WithCoordinator_MarksAllActiveAsDismissing()
    {
        // Arrange
        var service = new NotificationService(NullLogger<NotificationService>.Instance);
        var pendingRemovals = new List<Action>();

        NotificationService.DismissCoordinator = (item, onDismissed) =>
        {
            pendingRemovals.Add(onDismissed);
        };

        service.ShowInfo("T1", "M1");
        service.ShowInfo("T2", "M2");

        // Act
        service.ClearAll();

        // Assert
        service.Notifications.All(n => n.IsDismissing).Should().BeTrue();
        pendingRemovals.Should().HaveCount(2);

        // Complete dismissals
        foreach (var action in pendingRemovals)
        {
            action();
        }

        service.Notifications.Should().BeEmpty();
    }

    [Fact]
    public void RapidBursts_CapsTotalCollectionAtSafetyCeiling()
    {
        // Arrange
        var service = new NotificationService(NullLogger<NotificationService>.Instance);
        // Hold dismissals so they don't complete immediately
        NotificationService.DismissCoordinator = (item, onDismissed) => { };

        // Act: Rapidly blast 10 notifications
        for (int i = 1; i <= 10; i++)
        {
            service.ShowInfo($"Toast {i}", $"Msg {i}");
        }

        // Assert: Does not grow unbounded; strictly bounded by safety ceiling (<= 6)
        service.Notifications.Count.Should().BeLessThanOrEqualTo(6);

        // Active non-dismissing notifications are strictly <= 3
        service.Notifications.Count(n => !n.IsDismissing).Should().BeLessThanOrEqualTo(3);
    }
}
