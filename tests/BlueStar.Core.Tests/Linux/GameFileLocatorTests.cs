using BlueStar.Core.Helpers;
using FluentAssertions;
using Xunit;

namespace BlueStar.Core.Tests.Linux;

public sealed class GameFileLocatorTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("bs-locator-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Touch(string relative)
    {
        var full = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, "");
        return full;
    }

    [Fact]
    public void FindFile_MatchesEverySegmentIgnoringCase()
    {
        var real = Touch(Path.Combine("Game", "Binaries", "Win64", "Steam_API64.dll"));

        var found = GameFileLocator.FindFile(_root, "game/binaries/WIN64/steam_api64.DLL");

        found.Should().NotBeNull();
        Path.GetFullPath(found!).Should().BeEquivalentTo(Path.GetFullPath(real));
    }

    [Fact]
    public void FindFile_AcceptsBackslashSeparators()
    {
        Touch(Path.Combine("Data", "Managed", "Assembly-CSharp.dll"));

        GameFileLocator.FindFile(_root, @"data\managed\assembly-csharp.dll").Should().NotBeNull();
    }

    [Fact]
    public void FindFile_ReturnsNull_WhenMissingOrDirectory()
    {
        Directory.CreateDirectory(Path.Combine(_root, "Folder"));

        GameFileLocator.FindFile(_root, "nope.dll").Should().BeNull();
        GameFileLocator.FindFile(_root, "folder").Should().BeNull();
        GameFileLocator.FindFile(Path.Combine(_root, "missing-root"), "x").Should().BeNull();
    }

    [Fact]
    public void FindDirectory_FindsFolderButNotFile()
    {
        Touch(Path.Combine("BepInEx", "core", "BepInEx.dll"));

        GameFileLocator.FindDirectory(_root, "bepinex/CORE").Should().NotBeNull();
        GameFileLocator.FindDirectory(_root, "bepinex/core/bepinex.dll").Should().BeNull();
    }
}
