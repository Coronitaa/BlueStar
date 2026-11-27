using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using BlueStar.Core.Models;
using BlueStar.Infrastructure.Platforms;
using FluentAssertions;
using Xunit;

namespace BlueStar.Infrastructure.Tests;

public class PlatformImportAndVerificationTests
{
    [Fact]
    public async Task EpicGamesLibraryScanner_ScanInstalledGamesAsync_CompletesWithoutException()
    {
        var games = await EpicGamesLibraryScanner.ScanInstalledGamesAsync(CancellationToken.None);
        games.Should().NotBeNull();
    }

    [Fact]
    public async Task MicrosoftStoreLibraryScanner_ScanInstalledGamesAsync_CompletesWithoutException()
    {
        var games = await MicrosoftStoreLibraryScanner.ScanInstalledGamesAsync(CancellationToken.None);
        games.Should().NotBeNull();
    }

    [Theory]
    [InlineData(InstanceOrigin.Steam)]
    [InlineData(InstanceOrigin.EpicGames)]
    [InlineData(InstanceOrigin.MicrosoftStore)]
    public async Task PlatformInstallVerificationService_ReturnsFalse_WhenInstallPathDoesNotExist(InstanceOrigin origin)
    {
        var missingDir = Path.Combine(Path.GetTempPath(), $"bluestar-missing-{Guid.NewGuid():N}");
        var instance = new GameInstance
        {
            Name = "Missing Platform Game",
            Origin = origin,
            AppId = origin == InstanceOrigin.Steam ? 999999999u : 0u,
            PlatformAppId = "missing-app-id",
            InstallPath = missingDir,
            Status = InstanceStatus.Ready
        };

        var isPresent = await PlatformInstallVerificationService.IsPlatformInstallPresentAsync(instance, CancellationToken.None);
        isPresent.Should().BeFalse();
    }

    [Fact]
    public async Task PlatformInstallVerificationService_ReturnsTrue_ForNonExternalOrigin()
    {
        var instance = new GameInstance
        {
            Name = "BlueStar DepotBox Game",
            Origin = InstanceOrigin.DepotBox,
            InstallPath = Path.Combine(Path.GetTempPath(), $"bluestar-nonext-{Guid.NewGuid():N}"),
            Status = InstanceStatus.NotInstalled
        };

        var isPresent = await PlatformInstallVerificationService.IsPlatformInstallPresentAsync(instance, CancellationToken.None);
        isPresent.Should().BeTrue();
    }
}
