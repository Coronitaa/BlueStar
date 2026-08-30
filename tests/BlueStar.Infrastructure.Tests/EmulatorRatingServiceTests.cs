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
        var instance = new GameInstance
        {
            Id = Guid.NewGuid(),
            AppId = appId,
            Name = "Reset Game",
            InstallPath = @"C:\Games\ResetGame",
            InstalledEmulatorVersion = "1.0"
        };

        for (int i = 0; i < 12; i++)
        {
            await service.SubmitVoteAsync(appId, optionId, isPositive: true);
        }
        await service.RecordUserVoteFlagAsync(instance, optionId);

        service.HasUserVoted(instance, optionId).Should().BeTrue();

        // Reset ratings after update of refix
        await service.ResetRatingsForEmulatorAsync("refix");

        var options = await service.GetOptionsForInstanceAsync(instance);
        var valve = options.First(o => o.Id == optionId);

        valve.TotalVotes.Should().Be(0);
        valve.PositiveVotes.Should().Be(0);
        valve.HasEnoughVotesForScore.Should().BeFalse();
        valve.IsRecommended.Should().BeFalse();
        service.HasUserVoted(instance, optionId).Should().BeFalse();
    }

    [Fact]
    public async Task UserVoteFlag_TracksVotedState_PerGameAndEmulatorVersion()
    {
        var service = CreateService();
        var instanceV1 = new GameInstance
        {
            Id = Guid.NewGuid(),
            AppId = 112233,
            Name = "Versioned Game",
            InstallPath = @"C:\Games\VersionedGame",
            InstalledEmulatorVersion = "1.0",
            Depots = [new DepotInfo { DepotId = 1, ManifestId = 1001001 }]
        };
        string optionId = "refix_valve";

        service.HasUserVoted(instanceV1, optionId).Should().BeFalse();

        await service.RecordUserVoteFlagAsync(instanceV1, optionId);

        // Voted for v1
        service.HasUserVoted(instanceV1, optionId).Should().BeTrue();

        // If game is updated to new manifest 2002002, user can vote again!
        var instanceV2 = instanceV1 with
        {
            Depots = [new DepotInfo { DepotId = 1, ManifestId = 2002002 }]
        };
        service.HasUserVoted(instanceV2, optionId).Should().BeFalse();

        // If emulator is updated to 1.1 on original game version, user can vote again!
        var instanceWithNewEmu = instanceV1 with
        {
            InstalledEmulatorVersion = "1.1"
        };
        service.HasUserVoted(instanceWithNewEmu, optionId).Should().BeFalse();
    }

    [Fact]
    public async Task ResetRatingsForGame_ResetsAllOptionsForThatGameOnly()
    {
        var service = CreateService();
        uint gameAppId = 55555;
        uint otherAppId = 66666;

        await service.SubmitVoteAsync(gameAppId, "refix_valve", true);
        await service.SubmitVoteAsync(gameAppId, "refix_goldberg", true);
        await service.SubmitVoteAsync(gameAppId, "fix_123", true);
        await service.SubmitVoteAsync(otherAppId, "refix_valve", true);

        var (gameValvePos, _) = service.GetRatings(gameAppId, "refix_valve");
        var (otherValvePos, _) = service.GetRatings(otherAppId, "refix_valve");
        gameValvePos.Should().Be(1);
        otherValvePos.Should().Be(1);

        // Reset game on update
        await service.ResetRatingsForGameAsync(gameAppId);

        var (resetValvePos, _) = service.GetRatings(gameAppId, "refix_valve");
        var (resetGoldbergPos, _) = service.GetRatings(gameAppId, "refix_goldberg");
        var (resetFixPos, _) = service.GetRatings(gameAppId, "fix_123");
        var (intactOtherPos, _) = service.GetRatings(otherAppId, "refix_valve");

        resetValvePos.Should().Be(0);
        resetGoldbergPos.Should().Be(0);
        resetFixPos.Should().Be(0);
        intactOtherPos.Should().Be(1);
    }
}
