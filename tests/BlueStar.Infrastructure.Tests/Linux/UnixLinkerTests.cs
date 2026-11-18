using BlueStar.Infrastructure.Storage;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BlueStar.Infrastructure.Tests.Linux;

public sealed class UnixLinkerTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("bs-linker-").FullName;
    private readonly UnixLinker _linker = new(NullLogger<UnixLinker>.Instance);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [LinuxFact]
    public void CreateHardLink_SharesContentAndCountsLinks()
    {
        var target = Path.Combine(_root, "depot", "game.pak");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.WriteAllText(target, "data");
        var link = Path.Combine(_root, "instance", "game.pak");

        _linker.CreateHardLink(link, target).Should().BeTrue();

        File.ReadAllText(link).Should().Be("data");
        _linker.GetFileLinkCount(target).Should().Be(2);
        _linker.IsSameVolume(link, target).Should().BeTrue();
        _linker.SupportsHardLinks(_root).Should().BeTrue();
    }

    [LinuxFact]
    public void Junction_IsADirectorySymlink_AndDeletingItKeepsTheTarget()
    {
        var target = Path.Combine(_root, "saves");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "slot1.sav"), "save");
        var junction = Path.Combine(_root, "game", "saves");
        Directory.CreateDirectory(junction); // empty folder may be replaced, as with NTFS junctions

        _linker.CreateJunction(junction, target).Should().BeTrue();

        _linker.IsJunction(junction).Should().BeTrue();
        _linker.GetJunctionTarget(junction).Should().Be(target);
        File.ReadAllText(Path.Combine(junction, "slot1.sav")).Should().Be("save");

        _linker.DeleteJunction(junction).Should().BeTrue();
        Directory.Exists(junction).Should().BeFalse();
        File.Exists(Path.Combine(target, "slot1.sav")).Should().BeTrue();
    }

    [LinuxFact]
    public void CreateJunction_RefusesNonEmptyFolder()
    {
        var target = Path.Combine(_root, "t");
        Directory.CreateDirectory(target);
        var junction = Path.Combine(_root, "j");
        Directory.CreateDirectory(junction);
        File.WriteAllText(Path.Combine(junction, "keep.txt"), "user data");

        _linker.CreateJunction(junction, target).Should().BeFalse();
        File.Exists(Path.Combine(junction, "keep.txt")).Should().BeTrue();
    }
}
