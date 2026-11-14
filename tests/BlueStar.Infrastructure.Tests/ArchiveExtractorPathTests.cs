using BlueStar.Infrastructure.Common;
using FluentAssertions;
using Xunit;

namespace BlueStar.Infrastructure.Tests;

public sealed class ArchiveExtractorPathTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "mods", "Foo");

    [Fact]
    public void IsInside_RejectsSiblingFolderThatOnlySharesThePrefix() =>
        ArchiveExtractor.IsInside(Root, Path.GetFullPath(Path.Combine(Root, "..", "FooBar", "x.dll"))).Should().BeFalse();

    [Fact]
    public void IsInside_AcceptsFilesInsideAndRejectsParent()
    {
        ArchiveExtractor.IsInside(Root, Path.Combine(Root, "plugins", "x.dll")).Should().BeTrue();
        ArchiveExtractor.IsInside(Root, Path.GetFullPath(Path.Combine(Root, "..", "x.dll"))).Should().BeFalse();
    }
}
