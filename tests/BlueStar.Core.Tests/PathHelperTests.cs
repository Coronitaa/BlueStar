using BlueStar.Core.Helpers;
using FluentAssertions;
using Xunit;

namespace BlueStar.Core.Tests;

public class PathHelperTests
{
    [Theory]
    [InlineData(@"D:\Games", "Big Walk", @"D:\Games\Big Walk")]
    [InlineData(@"D:\Games\Big Walk", "Big Walk", @"D:\Games\Big Walk")]
    [InlineData(@"D:\Games\big walk", "Big Walk", @"D:\Games\big walk")]
    [InlineData(@"C:\Program Files\Steam", "Cyberpunk: 2077", @"C:\Program Files\Steam\Cyberpunk_ 2077")]
    public void EnsureGameSubfolder_AppendsSubfolderWhenNeeded(string baseDir, string gameName, string expected)
    {
        var result = PathHelper.EnsureGameSubfolder(baseDir, gameName);
        result.Should().Be(expected);
    }
}
