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

public class EmulatorRatingServiceTests
{
    [Fact]
    public async Task GetOptionsForInstance_ForUnrealEngine_ReturnsOnlineAndGoldberg()
    {
        var service = new EmulatorRatingService(NullLogger<EmulatorRatingService>.Instance);
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
        options.Any(o => o.IsRecommended).Should().BeTrue();
    }

    [Fact]
    public async Task GetOptionsForInstance_ForGodotEngine_ReturnsOnlineAndGoldberg()
    {
        var service = new EmulatorRatingService(NullLogger<EmulatorRatingService>.Instance);
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
        options.Any(o => o.IsRecommended).Should().BeTrue();
    }

    [Fact]
    public async Task GetOptionsForInstance_ForUnityEngine_ReturnsOnlineAndGoldberg()
    {
        var service = new EmulatorRatingService(NullLogger<EmulatorRatingService>.Instance);
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
    public async Task SubmitVoteAsync_IncrementsVotes_AndRecalculatesPercentage()
    {
        var service = new EmulatorRatingService(NullLogger<EmulatorRatingService>.Instance);
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

        var initialOptions = await service.GetOptionsForInstanceAsync(instance);
        var initialValve = initialOptions.First(o => o.Id == optionId);
        int initialPos = initialValve.PositiveVotes;

        await service.SubmitVoteAsync(appId, optionId, isPositive: true);

        var updatedOptions = await service.GetOptionsForInstanceAsync(instance);
        var updatedValve = updatedOptions.First(o => o.Id == optionId);

        updatedValve.PositiveVotes.Should().Be(initialPos + 1);
        updatedValve.ScorePercentage.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task UserVoteFlag_TracksVotedState()
    {
        var service = new EmulatorRatingService(NullLogger<EmulatorRatingService>.Instance);
        var instanceId = Guid.NewGuid();
        string optionId = "refix_valve";

        service.HasUserVoted(instanceId, optionId).Should().BeFalse();

        await service.RecordUserVoteFlagAsync(instanceId, optionId);

        service.HasUserVoted(instanceId, optionId).Should().BeTrue();
    }
}
