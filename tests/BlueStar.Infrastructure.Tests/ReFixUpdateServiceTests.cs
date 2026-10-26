using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using BlueStar.Core.Interfaces;
using BlueStar.Core.Models;
using BlueStar.Infrastructure.Emulators;
using BlueStar.Infrastructure.Update;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Moq.Protected;
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

    [Theory]
    [InlineData("1.3.3", "1.3.2", true)]
    [InlineData("1.3.2", "1.3.2", false)]
    [InlineData("1.3.0", "1.3.2", false)]
    [InlineData("1.4.0", "1.3.9", true)]
    public void IsNewerVersion_CorrectlyComparesVersions(string candidate, string baseline, bool expected)
    {
        var result = ReFixUpdateService.IsNewerVersion(candidate, baseline);
        result.Should().Be(expected);
    }

    [Theory]
    [InlineData("1.4.7", "1.4.6", true)]
    [InlineData("v1.4.7", "1.4.6", true)]
    [InlineData("1.4.6", "1.4.6", false)]
    [InlineData("1.4.5", "1.4.6", false)]
    [InlineData("1.3.3", "1.4.6", false)]
    [InlineData("1.4.8", "1.4.7", true)]
    public void GitHubUpdateService_IsNewerVersion_CorrectlyCompares(string candidate, string current, bool expected)
    {
        var curVer = Version.Parse(current);
        var result = GitHubUpdateService.IsNewerVersion(candidate, curVer);
        result.Should().Be(expected);
    }

    [Fact]
    public void GitHubUpdateService_IsPackaged_DoesNotThrow()
    {
        var isPackaged = BlueStar.Infrastructure.Update.GitHubUpdateService.IsRunningAsPackaged();
        // Running inside standard test runner, should be boolean without exceptions
        isPackaged.Should().Be(isPackaged);
    }

    [Fact]
    public async Task GitHubUpdateService_ForceRefresh_BypassesCache()
    {
        var mockCache = new Mock<ICacheService>();
        var handler = new Mock<HttpMessageHandler>();

        var json148 = """
        {
            "tag_name": "v1.4.8",
            "body": "Test release notes",
            "assets": [
                {
                    "name": "BlueStar-v1.4.8-Portable-win-x64.zip",
                    "browser_download_url": "https://example.com/BlueStar-v1.4.8-Portable-win-x64.zip",
                    "size": 50000000
                }
            ]
        }
        """;

        handler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new StringContent(json148, System.Text.Encoding.UTF8, "application/json")
            });

        var client = new HttpClient(handler.Object);
        var updateService = new GitHubUpdateService(
            client,
            NullLogger<GitHubUpdateService>.Instance,
            mockCache.Object);

        // When forceRefresh is true, GetAsync on cache should NEVER be called
        var update = await updateService.CheckForUpdatesAsync(CancellationToken.None, forceRefresh: true);

        update.Should().NotBeNull();
        update!.Version.Should().Be("1.4.8");
        mockCache.Verify(c => c.GetAsync<It.IsAnyType>(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
