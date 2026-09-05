using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using BlueStar.Core.Models;
using BlueStar.Infrastructure.Metadata;
using BlueStar.Infrastructure.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
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
}
