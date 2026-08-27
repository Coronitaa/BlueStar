using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using BlueStar.Core.Interfaces;
using BlueStar.Core.Models;
using BlueStar.Infrastructure.Services;
using BlueStar.Infrastructure.Storage;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace BlueStar.Infrastructure.Tests;

public class CommunityStatsServiceTests
{
    private readonly CommunityStatsService _service;
    private readonly Mock<ICacheService> _cacheMock;

    public CommunityStatsServiceTests()
    {
        var httpClient = new HttpClient();
        _cacheMock = new Mock<ICacheService>();
        var tempSettingsPath = Path.Combine(Path.GetTempPath(), $"bluestar_test_settings_{Guid.NewGuid():N}.json");
        var settingsService = new AppSettingsService(NullLogger<AppSettingsService>.Instance, tempSettingsPath);

        _service = new CommunityStatsService(
            httpClient,
            _cacheMock.Object,
            settingsService,
            NullLogger<CommunityStatsService>.Instance);
    }

    [Fact]
    public async Task GetTrendingBlueStarAsync_ExecutesGracefully()
    {
        // Act
        var results = await _service.GetTrendingBlueStarAsync(CancellationToken.None);

        // Assert
        results.Should().NotBeNull();
    }

    [Fact]
    public async Task GetMostPlayedBlueStarAsync_ExecutesGracefully()
    {
        // Act
        var results = await _service.GetMostPlayedBlueStarAsync(CancellationToken.None);

        // Assert
        results.Should().NotBeNull();
    }

    [Theory]
    [InlineData("most_played")]
    [InlineData("trending")]
    [InlineData("top_sellers")]
    [InlineData("top_rated")]
    public async Task GetSteamDbListAsync_ReturnsItemsOrEmptyGracefully(string listType)
    {
        // Act
        var results = await _service.GetSteamDbListAsync(listType, CancellationToken.None);

        // Assert
        results.Should().NotBeNull();
    }

    [Theory]
    [InlineData("added")]
    [InlineData("updated")]
    public async Task GetDepotBoxFeedAsync_ReturnsItemsOrEmptyGracefully(string feedType)
    {
        // Act
        var results = await _service.GetDepotBoxFeedAsync(feedType, CancellationToken.None);

        // Assert
        results.Should().NotBeNull();
    }

    [Fact]
    public async Task ReportInstanceAddedAsync_CompletesGracefully()
    {
        // Act
        Func<Task> act = async () => await _service.ReportInstanceAddedAsync(730, "Counter-Strike 2");

        // Assert
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task ReportGamePlayAsync_CompletesGracefully()
    {
        // Act
        Func<Task> act = async () => await _service.ReportGamePlayAsync(730, "Counter-Strike 2", TimeSpan.FromMinutes(45));

        // Assert
        await act.Should().NotThrowAsync();
    }
}
