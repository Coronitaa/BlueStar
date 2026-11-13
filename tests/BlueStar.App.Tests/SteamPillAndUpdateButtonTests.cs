using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using BlueStar.App.ViewModels;
using BlueStar.Core.Interfaces;
using BlueStar.Core.Models;
using BlueStar.Infrastructure.Downloader;
using BlueStar.Infrastructure.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace BlueStar.App.Tests;

public class SteamPillAndUpdateButtonTests
{
    private (MainViewModel MainVm, Mock<ISteamStatusService> MockSteam, Mock<IUpdateService> MockUpdate) CreateViewModel(bool steamRunning)
    {
        var mockSteam = new Mock<ISteamStatusService>();
        var status = new SteamStatus(steamRunning, steamRunning ? "TestUser" : null, steamRunning ? "TestUser" : null, steamRunning ? 76561198000000000UL : null);
        mockSteam.Setup(s => s.CurrentStatus).Returns(status);

        var mockUpdate = new Mock<IUpdateService>();
        var mockNotifications = new Mock<INotificationService>();
        var mockInstances = new Mock<IInstanceManager>();
        var mockTasks = new Mock<IBackgroundTaskService>();
        var mockDownloader = new Mock<IDownloadProvider>();

        var queueManager = new DownloadQueueManager(
            mockDownloader.Object,
            NullLogger<DownloadQueueManager>.Instance,
            mockInstances.Object,
            mockNotifications.Object);

        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        var homeVm = new HomeViewModel(
            mockInstances.Object,
            queueManager,
            new Mock<IGameLauncher>().Object,
            new Mock<IDepotBoxApiClient>().Object,
            new Mock<IDepotBoxArchiveParser>().Object,
            new Mock<IEngineDetector>().Object,
            NullLogger<HomeViewModel>.Instance,
            notificationService: mockNotifications.Object);
        services.AddSingleton(homeVm);
        var sp = services.BuildServiceProvider();
        App.SetTestServices(sp);

        var mainVm = new MainViewModel(
            queueManager,
            mockSteam.Object,
            mockNotifications.Object,
            mockUpdate.Object,
            mockInstances.Object,
            mockTasks.Object);

        return (mainVm, mockSteam, mockUpdate);
    }

    [Fact]
    public void ToggleSteamCommand_WhenSteamIsRunning_CallsBringSteamToFront()
    {
        StaTestHelper.Run(() =>
        {
            var (mainVm, mockSteam, _) = CreateViewModel(steamRunning: true);

            mainVm.ToggleSteamCommand.Execute(null);

            mockSteam.Verify(s => s.BringSteamToFront(), Times.Once);
            mockSteam.Verify(s => s.LaunchSteamHidden(), Times.Never);
        });
    }

    [Fact]
    public void ToggleSteamCommand_WhenSteamIsNotRunning_CallsLaunchSteamHidden()
    {
        StaTestHelper.Run(() =>
        {
            var (mainVm, mockSteam, _) = CreateViewModel(steamRunning: false);

            mainVm.ToggleSteamCommand.Execute(null);

            mockSteam.Verify(s => s.LaunchSteamHidden(), Times.Once);
            mockSteam.Verify(s => s.BringSteamToFront(), Times.Never);
        });
    }

    [Fact]
    public void AppUpdateButton_WhenUpdateAvailable_HasCorrectProperties()
    {
        StaTestHelper.Run(() =>
        {
            var (mainVm, _, _) = CreateViewModel(steamRunning: false);

            Assert.False(mainVm.IsAppUpdateAvailable);

            mainVm.AvailableAppUpdate = new UpdateInfo
            {
                Version = "1.5.0",
                DownloadUrl = "https://example.com/update.exe",
                PublishedAt = DateTimeOffset.UtcNow,
                FileSize = 1024
            };
            mainVm.AppUpdateVersion = "1.5.0";
            mainVm.AppUpdateButtonText = $"Actualizar a {mainVm.AppUpdateVersion}";
            mainVm.IsAppUpdateAvailable = true;

            Assert.True(mainVm.IsAppUpdateAvailable);
            Assert.Equal("Actualizar a 1.5.0", mainVm.AppUpdateButtonText);
        });
    }

    [Fact]
    public void UpdateAppCommand_WhenUpdateAvailableAndNotPackaged_DownloadsUpdate()
    {
        StaTestHelper.Run(() =>
        {
            var (mainVm, _, mockUpdate) = CreateViewModel(steamRunning: false);
            var updateInfo = new UpdateInfo
            {
                Version = "2.0.0",
                DownloadUrl = "https://example.com/update.exe",
                PublishedAt = DateTimeOffset.UtcNow,
                FileSize = 2048
            };

            mainVm.AvailableAppUpdate = updateInfo;
            mainVm.IsAppUpdateAvailable = true;

            mockUpdate.Setup(u => u.IsPackaged).Returns(false);
            mockUpdate.Setup(u => u.DownloadUpdateAsync(updateInfo, It.IsAny<IProgress<DownloadProgress>?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync("C:\\mock\\setup.exe");

            mainVm.UpdateAppCommand.ExecuteAsync(null).GetAwaiter().GetResult();

            mockUpdate.Verify(u => u.DownloadUpdateAsync(updateInfo, It.IsAny<IProgress<DownloadProgress>?>(), It.IsAny<CancellationToken>()), Times.Once);
        });
    }

    [Fact]
    public void UpdateAppCommand_WhenPackaged_OpensStore()
    {
        StaTestHelper.Run(() =>
        {
            var (mainVm, _, mockUpdate) = CreateViewModel(steamRunning: false);
            var updateInfo = new UpdateInfo
            {
                Version = "2.0.0",
                DownloadUrl = "https://example.com/update.exe",
                PublishedAt = DateTimeOffset.UtcNow,
                FileSize = 2048
            };

            mainVm.AvailableAppUpdate = updateInfo;
            mainVm.IsAppUpdateAvailable = true;

            mockUpdate.Setup(u => u.IsPackaged).Returns(true);

            mainVm.UpdateAppCommand.ExecuteAsync(null).GetAwaiter().GetResult();

            mockUpdate.Verify(u => u.OpenStoreForUpdates(), Times.Once);
        });
    }
}
