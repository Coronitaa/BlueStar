using BlueStar.Core.Models;
using FluentAssertions;
using Xunit;

namespace BlueStar.Core.Tests;

public class GameInstanceTests
{
    [Fact]
    public void GameInstance_DefaultValues_AreInitializedCorrectly()
    {
        // Act
        var instance = new GameInstance
        {
            Name = "Test Game",
            InstallPath = @"C:\Games\Test"
        };

        // Assert
        instance.Id.Should().NotBeEmpty();
        instance.Status.Should().Be(InstanceStatus.NotInstalled);
        instance.Depots.Should().BeEmpty();
        instance.Dlcs.Should().BeEmpty();
        instance.EnableAdvancedBuildOptions.Should().BeFalse();
    }

    [Fact]
    public void GameInstance_EnableAdvancedBuildOptions_PreservedInJson()
    {
        var instance = new GameInstance
        {
            Name = "Test Game",
            InstallPath = @"C:\Games\Test",
            EnableAdvancedBuildOptions = true
        };

        var json = System.Text.Json.JsonSerializer.Serialize(instance);
        var deserialized = System.Text.Json.JsonSerializer.Deserialize<GameInstance>(json);

        deserialized.Should().NotBeNull();
        deserialized!.EnableAdvancedBuildOptions.Should().BeTrue();
    }
}
