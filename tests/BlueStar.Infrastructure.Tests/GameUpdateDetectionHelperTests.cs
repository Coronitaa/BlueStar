using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using BlueStar.Core.Interfaces;
using BlueStar.Core.Models;
using BlueStar.Infrastructure.Metadata;
using BlueStar.Infrastructure.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace BlueStar.Infrastructure.Tests;

public class GameUpdateDetectionHelperTests
{
    [Fact]
    public void GetInstalledManifestDate_WhenNoManifests_ReturnsNull()
    {
        var instance = new GameInstance
        {
            Name = "Empty Game",
            InstallPath = Path.GetTempPath(),
            Depots = []
        };

        var date = GameUpdateDetectionHelper.GetInstalledManifestDate(instance);
        date.Should().BeNull();
    }

    [Fact]
    public async Task CheckInstanceUpdateAsync_WhenAppIdZero_ReturnsUnknown()
    {
        using var http = new HttpClient();
        var steamClient = new SteamStoreApiClient(http, NullLogger<SteamStoreApiClient>.Instance);

        var instance = new GameInstance
        {
            AppId = 0,
            Name = "Custom App",
            InstallPath = Path.GetTempPath()
        };

        var (status, desc) = await GameUpdateDetectionHelper.CheckInstanceUpdateAsync(instance, steamClient, CancellationToken.None);
        status.Should().Be(UpdateCheckStatus.Unknown);
        desc.Should().BeNull();
    }

    [Fact]
    public async Task CheckInstanceUpdateAsync_WhenCancelled_ReturnsUnknown()
    {
        using var http = new HttpClient();
        var steamClient = new SteamStoreApiClient(http, NullLogger<SteamStoreApiClient>.Instance);

        var instance = new GameInstance
        {
            AppId = 1245620,
            Name = "Elden Ring",
            InstallPath = Path.GetTempPath()
        };

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var (status, desc) = await GameUpdateDetectionHelper.CheckInstanceUpdateAsync(instance, steamClient, cts.Token);
        status.Should().Be(UpdateCheckStatus.Unknown);
    }

    [Fact]
    public void GetInstalledManifestDate_WhenInstalledVersionDatePresent_ReturnsItDirectly()
    {
        var expectedDate = new DateTimeOffset(2024, 6, 20, 15, 30, 0, TimeSpan.Zero);
        var instance = new GameInstance
        {
            Name = "Elden Ring Shadow of the Erdtree",
            InstallPath = Path.GetTempPath(),
            InstalledVersionDate = expectedDate,
            Metadata = new GameMetadata
            {
                Name = "Elden Ring",
                ReleaseDate = "2022-02-25" // Base game release date
            }
        };

        var date = GameUpdateDetectionHelper.GetInstalledManifestDate(instance);
        date.Should().Be(expectedDate);
    }

    [Fact]
    public void GetInstalledManifestDate_WhenNoManifestsAndOnlyMetadataReleaseDate_DoesNotFallbackToReleaseDate()
    {
        var instance = new GameInstance
        {
            Name = "The Witcher 3: Wild Hunt",
            InstallPath = Path.Combine(Path.GetTempPath(), "NonExistentWitcherPath_" + Guid.NewGuid().ToString("N")),
            InstalledVersionDate = null,
            Metadata = new GameMetadata
            {
                Name = "The Witcher 3",
                ReleaseDate = "2015-05-18" // Old release date of base game
            }
        };

        var date = GameUpdateDetectionHelper.GetInstalledManifestDate(instance);
        // It must NOT return the 2015 base game release date!
        date.Should().BeNull();
    }

    [Fact]
    public async Task CheckInstanceUpdateDetailsAsync_WhenInstalledDepotManifestsDifferFromPublicManifests_ReturnsUpdateAvailable_EvenIfLatestBuildDateIsOlderOrEqual()
    {
        // Arrange
        const uint testAppId = 1245620;
        const uint testDepotId = 1245621;
        const ulong installedManifestId = 11111111111111111UL;
        const ulong publicManifestId = 99999999999999999UL;

        // Instance installation date is 2024-06-20
        var installedDate = new DateTimeOffset(2024, 6, 20, 12, 0, 0, TimeSpan.Zero);
        // Latest build date on Steam is equal or older than installation date
        var latestBuildDate = new DateTimeOffset(2024, 6, 20, 12, 0, 0, TimeSpan.Zero);

        var instance = new GameInstance
        {
            AppId = testAppId,
            Name = "Elden Ring",
            InstallPath = Path.GetTempPath(),
            Status = InstanceStatus.Ready,
            InstalledVersionDate = installedDate,
            Depots =
            [
                new DepotInfo
                {
                    DepotId = testDepotId,
                    ManifestId = installedManifestId,
                    IsDownloaded = true
                }
            ]
        };

        var publicManifests = new Dictionary<ulong, ulong>
        {
            [testDepotId] = publicManifestId
        };

        var depotInfo = new SteamStoreApiClient.SteamAppDepotInfo(
            latestBuildDate,
            "12345",
            publicManifests,
            "Game");

        var mockCache = new Mock<ICacheService>();
        mockCache.Setup(c => c.GetAsync<SteamStoreApiClient.SteamAppDepotInfo>(
            $"steamcmd_depotinfo_{testAppId}_v3",
            It.IsAny<CancellationToken>()))
            .ReturnsAsync(depotInfo);

        using var http = new HttpClient();
        var steamClient = new SteamStoreApiClient(
            http,
            NullLogger<SteamStoreApiClient>.Instance,
            cache: mockCache.Object);

        // Act
        var result = await GameUpdateDetectionHelper.CheckInstanceUpdateDetailsAsync(instance, steamClient, CancellationToken.None);

        // Assert: Differing depot manifest MUST report UpdateAvailable even if build date <= installed date
        result.Status.Should().Be(UpdateCheckStatus.UpdateAvailable);
        result.Description.Should().Contain("Newer depot build available on Steam/DepotBox");
    }

    [Fact]
    public async Task CheckInstanceUpdateDetailsAsync_WhenAllInstalledDepotsMatchPublicManifests_ReturnsUpToDate()
    {
        // Arrange
        const uint testAppId = 1245620;
        const uint testDepotId1 = 1245621;
        const uint testDepotId2 = 1245622;
        const ulong matchingManifest1 = 55555555555555555UL;
        const ulong matchingManifest2 = 66666666666666666UL;

        var installedDate = new DateTimeOffset(2024, 6, 20, 12, 0, 0, TimeSpan.Zero);
        var latestBuildDate = new DateTimeOffset(2024, 6, 20, 12, 0, 0, TimeSpan.Zero);

        var instance = new GameInstance
        {
            AppId = testAppId,
            Name = "Elden Ring",
            InstallPath = Path.GetTempPath(),
            Status = InstanceStatus.Ready,
            InstalledVersionDate = installedDate,
            Depots =
            [
                new DepotInfo
                {
                    DepotId = testDepotId1,
                    ManifestId = matchingManifest1,
                    IsDownloaded = true
                },
                new DepotInfo
                {
                    DepotId = testDepotId2,
                    ManifestId = matchingManifest2,
                    IsDownloaded = true
                }
            ]
        };

        var publicManifests = new Dictionary<ulong, ulong>
        {
            [testDepotId1] = matchingManifest1,
            [testDepotId2] = matchingManifest2
        };

        var depotInfo = new SteamStoreApiClient.SteamAppDepotInfo(
            latestBuildDate,
            "12345",
            publicManifests,
            "Game");

        var mockCache = new Mock<ICacheService>();
        mockCache.Setup(c => c.GetAsync<SteamStoreApiClient.SteamAppDepotInfo>(
            $"steamcmd_depotinfo_{testAppId}_v3",
            It.IsAny<CancellationToken>()))
            .ReturnsAsync(depotInfo);

        using var http = new HttpClient();
        var steamClient = new SteamStoreApiClient(
            http,
            NullLogger<SteamStoreApiClient>.Instance,
            cache: mockCache.Object);

        // Act
        var result = await GameUpdateDetectionHelper.CheckInstanceUpdateDetailsAsync(instance, steamClient, CancellationToken.None);

        // Assert: When all depot manifests match public manifests, status is UpToDate
        result.Status.Should().Be(UpdateCheckStatus.UpToDate);
        result.Description.Should().BeNull();
    }

    [Fact]
    public void GetInstalledManifestDate_PrefersGenuineDiskManifest_AndNeverFallsBackToPoisonedMetadataReleaseDate()
    {
        // Arrange: Create a temporary directory containing an authentic depot manifest file
        var tempDir = Path.Combine(Path.GetTempPath(), "ManifestTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            const uint depotId = 1245621;
            const ulong manifestId = 88888888888888888UL;
            var manifestPath = Path.Combine(tempDir, $"{depotId}_{manifestId}.manifest");

            // Write 16 dummy bytes so file exists with length > 4
            File.WriteAllBytes(manifestPath, new byte[16]);

            // Set file write time to genuine patch date (2024-04-10)
            var expectedManifestDate = new DateTimeOffset(2024, 4, 10, 15, 30, 0, TimeSpan.Zero);
            File.SetLastWriteTimeUtc(manifestPath, expectedManifestDate.UtcDateTime);

            var instance = new GameInstance
            {
                Name = "Elden Ring",
                InstallPath = tempDir,
                InstalledVersionDate = null,
                Depots =
                [
                    new DepotInfo
                    {
                        DepotId = depotId,
                        ManifestId = manifestId,
                        IsDownloaded = true
                    }
                ],
                Metadata = new GameMetadata
                {
                    Name = "Elden Ring Base Game",
                    ReleaseDate = "2015-05-18" // Ancient poisoned fallback date
                }
            };

            // Act
            var resolvedDate = GameUpdateDetectionHelper.GetInstalledManifestDate(instance);

            // Assert: Must prefer genuine disk manifest and not fall back to 2015 release date
            resolvedDate.Should().NotBeNull();
            resolvedDate!.Value.Date.Should().Be(expectedManifestDate.Date);
            resolvedDate.Value.Year.Should().NotBe(2015);
        }
        finally
        {
            try
            {
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, true);
                }
            }
            catch { }
        }
    }
}
