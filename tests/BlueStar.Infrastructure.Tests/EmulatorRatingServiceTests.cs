using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using BlueStar.Core.Models;
using BlueStar.Infrastructure.Emulators;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BlueStar.Infrastructure.Tests;

public class EmulatorRatingServiceTests : IDisposable
{
    private readonly string _tempDir;

    public EmulatorRatingServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "BlueStar_RatingTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true);
        }
        catch { }
    }

    private EmulatorRatingService CreateService() =>
        new(NullLogger<EmulatorRatingService>.Instance, dataDirectory: _tempDir);

    [Fact]
    public async Task GetOptionsForInstance_ForUnrealEngine_ReturnsOnlineAndGoldberg()
    {
        var service = CreateService();
        var instance = new GameInstance
        {
            Id = Guid.NewGuid(),
            AppId = 12345,
            Name = "Unreal Game",
            InstallPath = @"C:\Games\TestGame",
            Engine = new EngineInfo
            {
                Id = "unreal",
                Name = "Unreal Engine",
                Type = EngineType.UnrealEngine
            }
        };

        var options = await service.GetOptionsForInstanceAsync(instance);

        options.Should().NotBeNull();
        options.Should().HaveCount(2);
        options.Select(o => o.Id).Should().Contain(new[] { "refix_valve", "refix_goldberg" });
        // With 0 votes, neither option should be recommended or have score enabled
        options.Any(o => o.IsRecommended).Should().BeFalse();
        options.All(o => o.HasEnoughVotesForScore).Should().BeFalse();
    }

    [Fact]
    public async Task GetOptionsForInstance_ForGodotEngine_ReturnsOnlineAndGoldberg()
    {
        var service = CreateService();
        var instance = new GameInstance
        {
            Id = Guid.NewGuid(),
            AppId = 54321,
            Name = "Godot Game",
            InstallPath = @"C:\Games\TestGodot",
            Engine = new EngineInfo
            {
                Id = "godot",
                Name = "Godot Engine",
                Type = EngineType.Godot
            }
        };

        var options = await service.GetOptionsForInstanceAsync(instance);

        options.Should().NotBeNull();
        options.Should().HaveCount(2);
        options.Select(o => o.Id).Should().Contain(new[] { "refix_valve", "refix_goldberg" });
        options.Any(o => o.IsRecommended).Should().BeFalse();
    }

    [Fact]
    public async Task GetOptionsForInstance_ForUnityEngine_ReturnsOnlineAndGoldberg()
    {
        var service = CreateService();
        var instance = new GameInstance
        {
            Id = Guid.NewGuid(),
            AppId = 99999,
            Name = "Unity Game",
            InstallPath = @"C:\Games\TestUnity",
            Engine = new EngineInfo
            {
                Id = "unity",
                Name = "Unity",
                Type = EngineType.Unity
            }
        };

        var options = await service.GetOptionsForInstanceAsync(instance);

        options.Should().NotBeNull();
        options.Should().HaveCount(2);
        options.Select(o => o.Id).Should().Contain(new[] { "refix_valve", "refix_goldberg" });
    }

    [Fact]
    public async Task SubmitVoteAsync_WithTenVotes_EnablesRecommendationAndScore()
    {
        var service = CreateService();
        uint appId = 888888;
        string optionId = "refix_valve";

        var instance = new GameInstance
        {
            Id = Guid.NewGuid(),
            AppId = appId,
            Name = "Voted Game",
            InstallPath = @"C:\Games\TestVoted",
            Engine = new EngineInfo { Id = "unity", Name = "Unity", Type = EngineType.Unity }
        };

        // Submit 10 positive votes
        for (int i = 0; i < 10; i++)
        {
            await service.SubmitVoteAsync(appId, optionId, isPositive: true);
        }

        var updatedOptions = await service.GetOptionsForInstanceAsync(instance);
        var updatedValve = updatedOptions.First(o => o.Id == optionId);

        updatedValve.PositiveVotes.Should().BeGreaterOrEqualTo(10);
        updatedValve.TotalVotes.Should().BeGreaterOrEqualTo(10);
        updatedValve.HasEnoughVotesForScore.Should().BeTrue();
        updatedValve.ScorePercentage.Should().Be(100.0);
        updatedValve.IsRecommended.Should().BeTrue();
    }

    [Fact]
    public async Task ResetRatingsForEmulator_ResetsVotesToZero_AndClearsUserVotes()
    {
        var service = CreateService();
        uint appId = 777777;
        string optionId = "refix_valve";
        var instanceId = Guid.NewGuid();

        for (int i = 0; i < 12; i++)
        {
            await service.SubmitVoteAsync(appId, optionId, isPositive: true);
        }
        await service.RecordUserVoteFlagAsync(instanceId, optionId);

        service.HasUserVoted(instanceId, optionId).Should().BeTrue();

        // Reset ratings after update
        await service.ResetRatingsForEmulatorAsync("refix");

        var instance = new GameInstance
        {
            Id = instanceId,
            AppId = appId,
            Name = "Reset Game",
            InstallPath = @"C:\Games\ResetGame"
        };

        var options = await service.GetOptionsForInstanceAsync(instance);
        var valve = options.First(o => o.Id == optionId);

        valve.TotalVotes.Should().Be(0);
        valve.PositiveVotes.Should().Be(0);
        valve.HasEnoughVotesForScore.Should().BeFalse();
        valve.IsRecommended.Should().BeFalse();
        service.HasUserVoted(instanceId, optionId).Should().BeFalse();
    }

    [Fact]
    public async Task UserVoteFlag_TracksVotedState()
    {
        var service = CreateService();
        var instanceId = Guid.NewGuid();
        string optionId = "refix_valve";

        service.HasUserVoted(instanceId, optionId).Should().BeFalse();

        await service.RecordUserVoteFlagAsync(instanceId, optionId);

        service.HasUserVoted(instanceId, optionId).Should().BeTrue();
    }
}
