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
    }
}
