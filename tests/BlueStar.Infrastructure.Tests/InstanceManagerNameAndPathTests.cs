using System;
using System.Collections.Generic;
using System.IO;
using BlueStar.Core.Models;
using BlueStar.Infrastructure.Instance;
using FluentAssertions;
using Xunit;

namespace BlueStar.Infrastructure.Tests;

public class InstanceManagerNameAndPathTests
{
    [Fact]
    public void ResolveUniqueName_WhenNoCollision_ReturnsOriginalName()
    {
        var existing = new List<GameInstance>
        {
            new() { Name = "Elden Ring", InstallPath = @"C:\Games\Elden Ring" }
        };

        var result = InstanceManager.ResolveUniqueName("Dark Souls", existing);
        result.Should().Be("Dark Souls");
    }

    [Fact]
    public void ResolveUniqueName_WhenCollisionExists_AppendsIncrementalNumber()
    {
        var existing = new List<GameInstance>
        {
            new() { Name = "Cyberpunk 2077", InstallPath = @"C:\Games\Cyberpunk 2077" },
            new() { Name = "Cyberpunk 2077 (2)", InstallPath = @"C:\Games\Cyberpunk 2077 (2)" }
        };

        var result = InstanceManager.ResolveUniqueName("Cyberpunk 2077", existing);
        result.Should().Be("Cyberpunk 2077 (3)");
    }

    [Fact]
    public void ResolveUniqueName_WhenAddingNumberedInstance_IncrementsCleanly()
    {
        var existing = new List<GameInstance>
        {
            new() { Name = "Half-Life 2", InstallPath = @"C:\Games\Half-Life 2" },
            new() { Name = "Half-Life 2 (2)", InstallPath = @"C:\Games\Half-Life 2 (2)" }
        };

        var result = InstanceManager.ResolveUniqueName("Half-Life 2 (2)", existing);
        result.Should().Be("Half-Life 2 (3)");
    }

    [Fact]
    public void ResolveNonCollidingInstallPath_WhenNoCollision_ReturnsOriginalPath()
    {
        var existing = new List<GameInstance>
        {
            new() { Name = "Game A", InstallPath = @"C:\Games\GameA" }
        };

        var result = InstanceManager.ResolveNonCollidingInstallPath("Game B", @"C:\Games\GameB", existing);
        result.Should().Be(@"C:\Games\GameB");
    }

    [Fact]
    public void ResolveNonCollidingInstallPath_WhenPathCollides_AutoDisambiguatesFolder()
    {
        var existing = new List<GameInstance>
        {
            new() { Name = "Game A", InstallPath = @"C:\Games\GameA" },
            new() { Name = "Game A (2)", InstallPath = @"C:\Games\GameA (2)" }
        };

        var result = InstanceManager.ResolveNonCollidingInstallPath("Game A (3)", @"C:\Games\GameA", existing);
        result.Should().Be(@"C:\Games\Game A (3)");
    }
}
