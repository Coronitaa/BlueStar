using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using BlueStar.Core.Interfaces;
using BlueStar.Core.Models;
using BlueStar.Infrastructure.Emulators;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace BlueStar.Infrastructure.Tests;

public class ReFixUpdateServiceTests
{
    [Fact]
    public async Task CheckAndNotifyOutdatedInstances_SendsSingleAggregatedNotification_WhenMultipleOutdated()
    {
        var httpClient = new HttpClient();
        var instanceManagerMock = new Mock<IInstanceManager>();
        var notificationMock = new Mock<INotificationService>();
        var backgroundTaskMock = new Mock<IBackgroundTaskService>();

        var instances = new List<GameInstance>
        {
            new GameInstance
            {
                Id = Guid.NewGuid(),
                Name = "Game 1",
                InstallPath = @"C:\Games\Game1",
                EmulatorEnabled = true,
                InstalledEmulatorVersion = "0.9.0"
            },
            new GameInstance
            {
                Id = Guid.NewGuid(),
                Name = "Game 2",
                InstallPath = @"C:\Games\Game2",
                EmulatorEnabled = true,
                InstalledEmulatorVersion = "0.8.0"
            }
        };

        instanceManagerMock.Setup(m => m.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(instances);

        var service = new ReFixUpdateService(
            httpClient,
            NullLogger<ReFixUpdateService>.Instance,
            notificationMock.Object,
            instanceManagerMock.Object,
            backgroundTaskMock.Object,
            null);

        await service.CheckAndNotifyOutdatedInstancesAsync(CancellationToken.None);

        // Verify single aggregated notification was displayed for both instances
        notificationMock.Verify(n => n.ShowWarning(
            It.Is<string>(s => s.Contains("Emulator Updates Available")),
            It.Is<string>(s => s.Contains("2 instances have outdated emulators")),
            It.IsAny<TimeSpan?>(),
            It.IsAny<string?>(),
            It.IsAny<Action?>()), Times.Once);
    }
}
