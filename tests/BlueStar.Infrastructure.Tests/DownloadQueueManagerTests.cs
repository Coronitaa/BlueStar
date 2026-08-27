using BlueStar.Core.Interfaces;
using BlueStar.Core.Models;
using BlueStar.Infrastructure.Downloader;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace BlueStar.Infrastructure.Tests;

public class DownloadQueueManagerTests
{
    [Fact]
    public async Task DownloadCompletion_UpdatesDepotIsDownloadedAndDlcIsInstalled()
    {
        // Arrange
        var instanceId = Guid.NewGuid();
        var baseDepot1 = new DepotInfo { DepotId = 101, Name = "Base Depot 1", IsDownloaded = false };
        var baseDepot2 = new DepotInfo { DepotId = 102, Name = "Base Depot 2", IsDownloaded = false };
        var dlcDepot = new DepotInfo { DepotId = 103, Name = "DLC Depot", IsDownloaded = false };

        var dlc = new DlcInfo
        {
            AppId = 5001,
            Name = "DLC 1",
            Depots = [dlcDepot],
            IsInstalled = false
        };

        var fullInstance = new GameInstance
        {
            Id = instanceId,
            Name = "Test Game",
            AppId = 1000,
            InstallPath = @"C:\Games\Test",
            Status = InstanceStatus.NotInstalled,
            Depots = [baseDepot1, baseDepot2],
            Dlcs = [dlc]
        };

        GameInstance? updatedStoredInstance = null;

        var mockInstanceManager = new Mock<IInstanceManager>();
        mockInstanceManager
            .Setup(m => m.GetByIdAsync(instanceId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(fullInstance);

        mockInstanceManager
            .Setup(m => m.UpdateAsync(It.IsAny<GameInstance>(), It.IsAny<CancellationToken>()))
            .Callback<GameInstance, CancellationToken>((inst, _) => updatedStoredInstance = inst)
            .ReturnsAsync((GameInstance inst, CancellationToken _) => inst);

        var mockDownloadProvider = new Mock<IDownloadProvider>();
        mockDownloadProvider
            .Setup(p => p.DownloadAsync(It.IsAny<GameInstance>(), It.IsAny<IProgress<DownloadProgress>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var queueManager = new DownloadQueueManager(
            mockDownloadProvider.Object,
            NullLogger<DownloadQueueManager>.Instance,
            mockInstanceManager.Object);

        // Act - Download baseDepot1 and dlcDepot
        var downloadInstance = fullInstance with
        {
            Depots = [baseDepot1, dlcDepot]
        };

        await queueManager.StartDownloadAsync(downloadInstance);

        // Assert
        updatedStoredInstance.Should().NotBeNull();
        updatedStoredInstance!.Status.Should().Be(InstanceStatus.Ready);

        // baseDepot1 should be downloaded, baseDepot2 should not
        updatedStoredInstance.Depots.Should().HaveCount(2);
        updatedStoredInstance.Depots.First(d => d.DepotId == 101).IsDownloaded.Should().BeTrue();
        updatedStoredInstance.Depots.First(d => d.DepotId == 102).IsDownloaded.Should().BeFalse();

        // DLC depot was downloaded, so DLC IsInstalled should be true
        updatedStoredInstance.Dlcs.Should().HaveCount(1);
        var updatedDlc = updatedStoredInstance.Dlcs[0];
        updatedDlc.IsInstalled.Should().BeTrue();
        updatedDlc.Depots[0].IsDownloaded.Should().BeTrue();
    }

    [Fact]
    public async Task Reinstall_ResetsExistingCompletedJob_AndStartsDownloadFresh()
    {
        // Arrange
        var instanceId = Guid.NewGuid();
        var depot = new DepotInfo { DepotId = 101, Name = "Base Depot", IsDownloaded = true, SizeBytes = 1000 };

        var instance = new GameInstance
        {
            Id = instanceId,
            Name = "Test Game",
            AppId = 1000,
            InstallPath = @"C:\Games\Test",
            Status = InstanceStatus.Ready,
            Depots = [depot]
        };

        var mockInstanceManager = new Mock<IInstanceManager>();
        mockInstanceManager
            .Setup(m => m.GetByIdAsync(instanceId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(instance);

        mockInstanceManager
            .Setup(m => m.UpdateAsync(It.IsAny<GameInstance>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((GameInstance inst, CancellationToken _) => inst);

        int downloadCallCount = 0;

        var mockDownloadProvider = new Mock<IDownloadProvider>();
        mockDownloadProvider
            .Setup(p => p.DownloadAsync(It.IsAny<GameInstance>(), It.IsAny<IProgress<DownloadProgress>>(), It.IsAny<CancellationToken>()))
            .Returns<GameInstance, IProgress<DownloadProgress>, CancellationToken>((_, progress, _) =>
            {
                downloadCallCount++;
                progress?.Report(new DownloadProgress
                {
                    Percentage = 50,
                    DownloadedBytes = 500,
                    TotalBytes = 1000
                });
                return Task.CompletedTask;
            });

        var queueManager = new DownloadQueueManager(
            mockDownloadProvider.Object,
            NullLogger<DownloadQueueManager>.Instance,
            mockInstanceManager.Object);

        // First download run
        await queueManager.StartDownloadAsync(instance);
        downloadCallCount.Should().Be(1);

        var job = queueManager.Queue.First(q => q.Instance.Id == instanceId);
        job.JobStatus.Should().Be(DownloadJobStatus.Completed);
        job.Percentage.Should().Be(100);

        // Reinstallation run
        var reinstallTask = queueManager.StartDownloadAsync(instance);
        job.JobStatus.Should().BeOneOf(DownloadJobStatus.Queued, DownloadJobStatus.Downloading, DownloadJobStatus.Completed);

        await reinstallTask;

        // Assert
        downloadCallCount.Should().Be(2);
        job.JobStatus.Should().Be(DownloadJobStatus.Completed);
    }
}

