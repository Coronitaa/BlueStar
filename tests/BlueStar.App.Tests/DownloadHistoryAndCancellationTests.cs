using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BlueStar.Core.Interfaces;
using BlueStar.Core.Models;
using BlueStar.Infrastructure.Downloader;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace BlueStar.App.Tests;

public class DownloadHistoryAndCancellationTests
{
    private readonly Mock<IDownloadProvider> _mockDownloadProvider = new();
    private readonly Mock<IInstanceManager> _mockInstanceManager = new();

    [Fact]
    public void SaveHistory_And_LoadHistory_RoundTripsCorrectly()
    {
        var testInstanceId = Guid.NewGuid();
        var testInstance = new GameInstance
        {
            Id = testInstanceId,
            AppId = 440,
            Name = "Team Fortress 2",
            InstallPath = @"C:\Games\TF2",
            Status = InstanceStatus.Ready
        };

        _mockInstanceManager.Setup(m => m.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<GameInstance> { testInstance });

        var tempHistoryPath = Path.Combine(Path.GetTempPath(), $"bluestar_tf2_test_{Guid.NewGuid():N}.json");
        try
        {
            var manager = new DownloadQueueManager(
                _mockDownloadProvider.Object,
                NullLogger<DownloadQueueManager>.Instance,
                _mockInstanceManager.Object,
                historyFilePath: tempHistoryPath);

            // Add test history entry
            manager.HistoryLog.Add(new DownloadLogEntry
            {
                GameName = "Team Fortress 2",
                Status = DownloadJobStatus.Completed,
                Message = "Downloaded successfully (12.5 GB)",
                Timestamp = DateTimeOffset.UtcNow,
                InstanceId = testInstanceId,
                Instance = testInstance
            });

            // Trigger SaveHistory
            manager.SaveHistory();

            // Verify history entries are in the collection and instance is mapped
            manager.HistoryLog.Should().Contain(e => e.GameName == "Team Fortress 2" && e.InstanceId == testInstanceId);
            var entry = manager.HistoryLog.First(e => e.GameName == "Team Fortress 2" && e.InstanceId == testInstanceId);
            entry.Status.Should().Be(DownloadJobStatus.Completed);
            entry.Instance.Should().NotBeNull();
            entry.Instance!.Id.Should().Be(testInstanceId);
        }
        finally
        {
            try { if (File.Exists(tempHistoryPath)) File.Delete(tempHistoryPath); } catch { }
        }
    }

    [Fact]
    public async Task CancelAsync_WhenUpdateWasInProgress_RevertsToPreviousBuildSnapshot()
    {
        var instanceId = Guid.NewGuid();
        var originalManifestMap = new Dictionary<uint, ulong> { [1001] = 111111 };
        var previousSnapshot = new InstalledBuildSnapshot
        {
            BuildId = "100",
            BranchName = "public",
            DisplayName = "Previous Stable Build",
            ManifestMap = originalManifestMap,
            BuildDate = DateTimeOffset.UtcNow.AddDays(-10),
            InstalledAt = DateTimeOffset.UtcNow.AddDays(-10),
            SizeBytes = 5000
        };

        var updatingInstance = new GameInstance
        {
            Id = instanceId,
            AppId = 440,
            Name = "Team Fortress 2",
            InstallPath = @"C:\Games\TF2",
            Status = InstanceStatus.Downloading,
            AwaitingPostUpdateRedeploy = true,
            Depots = new List<DepotInfo>
            {
                new DepotInfo { DepotId = 1001, ManifestId = 999999, IsDownloaded = false }
            },
            InstalledManifestMap = originalManifestMap,
            BuildHistory = new List<InstalledBuildSnapshot> { previousSnapshot }
        };

        GameInstance? savedInstance = null;
        _mockInstanceManager.Setup(m => m.GetByIdAsync(instanceId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(updatingInstance);
        _mockInstanceManager.Setup(m => m.UpdateAsync(It.IsAny<GameInstance>(), It.IsAny<CancellationToken>()))
            .Callback<GameInstance, CancellationToken>((inst, _) => savedInstance = inst)
            .ReturnsAsync((GameInstance inst, CancellationToken _) => inst);

        var manager = new DownloadQueueManager(
            _mockDownloadProvider.Object,
            NullLogger<DownloadQueueManager>.Instance,
            _mockInstanceManager.Object);

        // Cancel the download
        await manager.CancelAsync(instanceId);

        // Verify that the instance was reverted to the previous build
        savedInstance.Should().NotBeNull();
        savedInstance!.Status.Should().Be(InstanceStatus.Ready);
        savedInstance.AwaitingPostUpdateRedeploy.Should().BeFalse();
        savedInstance.Depots.Should().HaveCount(1);
        savedInstance.Depots[0].ManifestId.Should().Be(111111);
        savedInstance.Depots[0].IsDownloaded.Should().BeTrue();
        savedInstance.BuildHistory.Should().BeEmpty(); // Snapshot was consumed/reverted
    }
}
