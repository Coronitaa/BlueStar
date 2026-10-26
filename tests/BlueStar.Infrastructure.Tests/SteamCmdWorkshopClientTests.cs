using BlueStar.Infrastructure.Workshop;
using FluentAssertions;
using Xunit;

namespace BlueStar.Infrastructure.Tests;

public sealed class SteamCmdWorkshopClientTests
{
    [Fact]
    public void ParseDownloadResult_ReadsContentPathOnSuccess()
    {
        // Arrange
        const string output = """
            Logging in user 'anonymous' to Steam Public...OK
            Downloading item 3821122707 ...
            Success. Downloaded item 3821122707 to "C:\Users\me\AppData\Roaming\BlueStar\tools\steamcmd\steamapps\workshop\content\4704690\3821122707" (83050949 bytes) Unloading Steam API...OK
            """;

        // Act
        var (ok, path, error, needsAccount) = SteamCmdWorkshopClient.ParseDownloadResult(output, 3821122707);

        // Assert
        ok.Should().BeTrue();
        path.Should().EndWith(@"content\4704690\3821122707");
        error.Should().BeNull();
        needsAccount.Should().BeFalse();
    }

    [Theory]
    [InlineData("ERROR! Download item 3821122707 failed (Access Denied).", "owns the game", true)]
    [InlineData("ERROR! Download item 3821122707 failed (Failure).", "owns the game", true)]
    [InlineData("ERROR! Download item 3821122707 failed (Timeout).", "Timeout", false)]
    public void ParseDownloadResult_ExplainsFailures(string line, string expectedFragment, bool expectedNeedsAccount)
    {
        // Act
        var (ok, path, error, needsAccount) = SteamCmdWorkshopClient.ParseDownloadResult($"Logging in...\n{line}\n", 3821122707);

        // Assert
        ok.Should().BeFalse();
        path.Should().BeNull();
        error.Should().Contain(expectedFragment);
        needsAccount.Should().Be(expectedNeedsAccount);
    }

    [Fact]
    public void WinTrust_AcceptsSignedBinaries_AndRejectsUnsignedFiles()
    {
        // Arrange: the .NET core library is Authenticode-signed by Microsoft
        var signed = typeof(object).Assembly.Location;
        var unsigned = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"bluestar_unsigned_{System.Guid.NewGuid():N}.exe");
        System.IO.File.WriteAllBytes(unsigned, new byte[] { 0x4D, 0x5A, 0, 0 });

        try
        {
            // Act + Assert
            WinTrust.IsSignatureTrusted(signed).Should().BeTrue();
            WinTrust.IsSignatureTrusted(unsigned).Should().BeFalse();
        }
        finally
        {
            System.IO.File.Delete(unsigned);
        }
    }

    [Fact]
    public void ParseDownloadResult_IgnoresOtherItems_AndReportsMissingResult()
    {
        // Arrange: a success line for a different item must not count
        const string output = "Success. Downloaded item 111 to \"C:\\x\\111\" (5 bytes)";

        // Act
        var (ok, _, error, needsAccount) = SteamCmdWorkshopClient.ParseDownloadResult(output, 3821122707);

        // Assert
        ok.Should().BeFalse();
        error.Should().Contain("did not report");
        needsAccount.Should().BeFalse();
    }
}
