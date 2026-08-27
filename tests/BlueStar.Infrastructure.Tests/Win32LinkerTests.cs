using System;
using System.IO;
using BlueStar.Infrastructure.Storage;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BlueStar.Infrastructure.Tests;

public class Win32LinkerTests : IDisposable
{
    private readonly string _testDir;
    private readonly Win32Linker _linker;

    public Win32LinkerTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "BlueStar_Win32LinkerTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDir);
        _linker = new Win32Linker(NullLogger<Win32Linker>.Instance);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testDir))
            {
                Directory.Delete(_testDir, recursive: true);
            }
        }
        catch { }
    }

    [Fact]
    public void SupportsHardLinks_ReturnsTrue_OnNtfsDrive()
    {
        bool isNtfs = _linker.SupportsHardLinks(_testDir);
        var fs = _linker.GetVolumeFileSystem(_testDir);

        if (string.Equals(fs, "NTFS", StringComparison.OrdinalIgnoreCase))
        {
            isNtfs.Should().BeTrue();
        }
    }

    [Fact]
    public void CreateHardLink_IncrementsLinkCount_AndSharesContent()
    {
        if (!_linker.SupportsHardLinks(_testDir)) return;

        var sourceFile = Path.Combine(_testDir, "original.bin");
        var linkFile = Path.Combine(_testDir, "subfolder", "linked.bin");

        File.WriteAllText(sourceFile, "Original Hardlink Content 12345");

        int initialLinks = _linker.GetFileLinkCount(sourceFile);
        initialLinks.Should().Be(1);

        bool success = _linker.CreateHardLink(linkFile, sourceFile);
        success.Should().BeTrue();

        int afterLinksSource = _linker.GetFileLinkCount(sourceFile);
        int afterLinksTarget = _linker.GetFileLinkCount(linkFile);

        afterLinksSource.Should().Be(2);
        afterLinksTarget.Should().Be(2);

        File.ReadAllText(linkFile).Should().Be("Original Hardlink Content 12345");
    }

    [Fact]
    public void CreateJunction_And_DeleteJunction_SafelyRemovesReparsePoint_WithoutDeletingTarget()
    {
        if (!_linker.SupportsReparsePoints(_testDir)) return;

        var targetFolder = Path.Combine(_testDir, "TargetData");
        Directory.CreateDirectory(targetFolder);
        File.WriteAllText(Path.Combine(targetFolder, "important_depot_asset.pak"), "depot asset bytes");

        var junctionPath = Path.Combine(_testDir, "Instance", "DataJunction");

        bool created = _linker.CreateJunction(junctionPath, targetFolder);
        created.Should().BeTrue();

        _linker.IsJunction(junctionPath).Should().BeTrue();

        var targetFromJunction = _linker.GetJunctionTarget(junctionPath);
        targetFromJunction.Should().NotBeNullOrEmpty();
        targetFromJunction!.TrimEnd('\\').Should().BeEquivalentTo(targetFolder.TrimEnd('\\'));

        // Delete junction safely
        bool deleted = _linker.DeleteJunction(junctionPath);
        deleted.Should().BeTrue();

        Directory.Exists(junctionPath).Should().BeFalse();

        // Important Depot files MUST STILL EXIST untouched!
        Directory.Exists(targetFolder).Should().BeTrue();
        File.Exists(Path.Combine(targetFolder, "important_depot_asset.pak")).Should().BeTrue();
        File.ReadAllText(Path.Combine(targetFolder, "important_depot_asset.pak")).Should().Be("depot asset bytes");
    }
}
