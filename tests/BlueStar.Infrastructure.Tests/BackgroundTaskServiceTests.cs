using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BlueStar.Core.Models;
using BlueStar.Infrastructure.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BlueStar.Infrastructure.Tests;

public class BackgroundTaskServiceTests
{
    [Fact]
    public async Task QueueTask_ExecutesWorkAndCompletes()
    {
        var service = new BackgroundTaskService(NullLogger<BackgroundTaskService>.Instance);
        var executed = false;

        var taskId = service.QueueTask("Test Task", "Testing background runner", async (reporter, ct) =>
        {
            reporter.Report(new BackgroundTaskProgress(50, "Halfway"));
            await Task.Delay(20, ct);
            executed = true;
            reporter.Report(new BackgroundTaskProgress(100, "Finished"));
        });

        taskId.Should().NotBeEmpty();

        // Wait for task completion
        for (int i = 0; i < 100 && (!executed || service.Tasks.FirstOrDefault(t => t.Id == taskId)?.Status != BackgroundTaskStatus.Completed || service.Tasks.FirstOrDefault(t => t.Id == taskId)?.ProgressPercentage < 100); i++)
        {
            await Task.Delay(20);
        }

        executed.Should().BeTrue();
        var task = service.Tasks.FirstOrDefault(t => t.Id == taskId);
        task.Should().NotBeNull();
        task!.Status.Should().Be(BackgroundTaskStatus.Completed);
        task.ProgressPercentage.Should().Be(100);
    }

    [Fact]
    public async Task QueueTask_HandlesCancellation()
    {
        var service = new BackgroundTaskService(NullLogger<BackgroundTaskService>.Instance);

        var startedTcs = new TaskCompletionSource<bool>();
        var taskId = service.QueueTask("Cancellable Task", "Will be cancelled", async (reporter, ct) =>
        {
            startedTcs.TrySetResult(true);
            await Task.Delay(5000, ct);
        });

        await startedTcs.Task;
        service.CancelTask(taskId);

        // Wait for background cancellation to take effect
        for (int i = 0; i < 30 && service.Tasks.FirstOrDefault(t => t.Id == taskId)?.Status != BackgroundTaskStatus.Cancelled; i++)
        {
            await Task.Delay(50);
        }

        var task = service.Tasks.FirstOrDefault(t => t.Id == taskId);
        task.Should().NotBeNull();
        task!.Status.Should().Be(BackgroundTaskStatus.Cancelled);
    }

    [Fact]
    public async Task ClearCompleted_RemovesOnlyFinishedTasks()
    {
        var service = new BackgroundTaskService(NullLogger<BackgroundTaskService>.Instance);

        var taskId = service.QueueTask("Fast Task", "Done quickly", (reporter, ct) =>
        {
            reporter.Report(new BackgroundTaskProgress(100, "Done"));
            return Task.CompletedTask;
        });

        for (int i = 0; i < 30 && service.Tasks.FirstOrDefault(t => t.Id == taskId)?.Status != BackgroundTaskStatus.Completed; i++)
        {
            await Task.Delay(50);
        }
        service.Tasks.Should().Contain(t => t.Id == taskId);

        service.ClearCompleted();
        service.Tasks.Should().NotContain(t => t.Id == taskId);
    }

    [Fact]
    public async Task QueueTask_WithDepotDownloadProgress_ReportsProgressAndCompletes()
    {
        var service = new BackgroundTaskService(NullLogger<BackgroundTaskService>.Instance);
        var reports = new System.Collections.Generic.List<double>();

        var taskId = service.QueueTask(
            "Downloading Depots: Test Game",
            "Test Game",
            async (reporter, ct) =>
            {
                reporter.Report(new BackgroundTaskProgress(0, "Fetching metadata...", "Preparing"));
                await Task.Delay(10, ct);

                // Simulate downloading archive
                for (int p = 10; p <= 90; p += 20)
                {
                    reporter.Report(new BackgroundTaskProgress(p, $"Downloading depot archive ({p}%)...", "Downloading"));
                    await Task.Delay(10, ct);
                }

                reporter.Report(new BackgroundTaskProgress(95, "Configuring instance...", "Configuring"));
                await Task.Delay(10, ct);
                reporter.Report(new BackgroundTaskProgress(100, "Instance ready", "Complete"));
            });

        service.TasksChanged += (_, _) =>
        {
            var item = service.Tasks.FirstOrDefault(t => t.Id == taskId);
            if (item != null)
            {
                reports.Add(item.ProgressPercentage);
            }
        };

        for (int i = 0; i < 50 && service.Tasks.FirstOrDefault(t => t.Id == taskId)?.Status != BackgroundTaskStatus.Completed; i++)
        {
            await Task.Delay(20);
        }

        var task = service.Tasks.FirstOrDefault(t => t.Id == taskId);
        task.Should().NotBeNull();
        task!.Title.Should().Be("Downloading Depots: Test Game");
        task.InstanceName.Should().Be("Test Game");
        task.Status.Should().Be(BackgroundTaskStatus.Completed);
        task.ProgressPercentage.Should().Be(100);
    }

    [Fact]
    public async Task QueueTask_WithDepotUpdateSearch_ReportsProgressAndCompletes()
    {
        var service = new BackgroundTaskService(NullLogger<BackgroundTaskService>.Instance);

        var taskId = service.QueueTask(
            "Searching Depot Updates: Cyber Game",
            "Cyber Game",
            async (reporter, ct) =>
            {
                reporter.Report(new BackgroundTaskProgress(15, "Connecting to DepotBox API...", "Searching"));
                await Task.Delay(10, ct);
                reporter.Report(new BackgroundTaskProgress(65, "Analyzing depot manifests...", "Analyzing"));
                await Task.Delay(10, ct);
                reporter.Report(new BackgroundTaskProgress(100, "Found 2 updated depot(s).", "Complete"));
            });

        for (int i = 0; i < 50 && service.Tasks.FirstOrDefault(t => t.Id == taskId)?.Status != BackgroundTaskStatus.Completed; i++)
        {
            await Task.Delay(20);
        }

        var task = service.Tasks.FirstOrDefault(t => t.Id == taskId);
        task.Should().NotBeNull();
        task!.Title.Should().Be("Searching Depot Updates: Cyber Game");
        task.InstanceName.Should().Be("Cyber Game");
        task.Status.Should().Be(BackgroundTaskStatus.Completed);
        task.CurrentStepMessage.Should().NotBeNullOrWhiteSpace();
    }
}
