using BlueStar.Core.Helpers;
using FluentAssertions;
using Xunit;

namespace BlueStar.Core.Tests;

public class PathHelperTests
{
    [Theory]
    [Trait("OS", "Windows")]
    [InlineData(@"D:\Games", "Big Walk", @"D:\Games\Big Walk")]
    [InlineData(@"D:\Games\Big Walk", "Big Walk", @"D:\Games\Big Walk")]
    [InlineData(@"D:\Games\big walk", "Big Walk", @"D:\Games\big walk")]
    [InlineData(@"C:\Program Files\Steam", "Cyberpunk: 2077", @"C:\Program Files\Steam\Cyberpunk_ 2077")]
    public void EnsureGameSubfolder_AppendsSubfolderWhenNeeded(string baseDir, string gameName, string expected)
    {
        var result = PathHelper.EnsureGameSubfolder(baseDir, gameName);
        result.Should().Be(expected);
    }

    [Fact]
    public void GenerateUniqueInstanceName_WhenNameDoesNotExist_ReturnsBaseName()
    {
        var existing = new[] { "Portal", "Half-Life 2" };
        var unique = PathHelper.GenerateUniqueInstanceName(existing, "Cyberpunk 2077");
        unique.Should().Be("Cyberpunk 2077");
    }

    [Fact]
    public void GenerateUniqueInstanceName_WhenNameExists_ReturnsNumberedSuffix()
    {
        var existing = new[] { "Cyberpunk 2077" };
        var unique = PathHelper.GenerateUniqueInstanceName(existing, "Cyberpunk 2077");
        unique.Should().Be("Cyberpunk 2077 (2)");
    }

    [Fact]
    public void GenerateUniqueInstanceName_WhenMultipleRepeatsExist_IncrementsSuffix()
    {
        var existing = new[] { "Cyberpunk 2077", "Cyberpunk 2077 (2)", "Cyberpunk 2077 (3)" };
        var unique = PathHelper.GenerateUniqueInstanceName(existing, "Cyberpunk 2077");
        unique.Should().Be("Cyberpunk 2077 (4)");
    }

    [Fact]
    [Trait("OS", "Windows")]
    public void GenerateUniqueInstallPath_WhenPathCollides_ReturnsUniqueNumberedPath()
    {
        var baseDir = @"D:\Games";
        var existingPaths = new[] { @"D:\Games\Cyberpunk 2077" };

        var uniquePath = PathHelper.GenerateUniqueInstallPath(baseDir, "Cyberpunk 2077", existingPaths);
        uniquePath.Should().Be(@"D:\Games\Cyberpunk 2077 (2)");
    }

    [Theory]
    [InlineData("Cyberpunk: 2077", "Cyberpunk_ 2077")]
    [InlineData("What? *Now*", "What_ _Now_")]
    [InlineData("A/B\\C", "A_B_C")]
    public void SanitizeFolderName_UsesWindowsRulesOnEveryOs(string name, string expected)
    {
        PathHelper.SanitizeFolderName(name).Should().Be(expected);
    }
}
