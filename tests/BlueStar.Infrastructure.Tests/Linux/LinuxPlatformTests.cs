using BlueStar.Infrastructure.Emulators;
using BlueStar.Infrastructure.Platform;
using BlueStar.Infrastructure.Storage;
using BlueStar.Infrastructure.Workshop;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BlueStar.Infrastructure.Tests.Linux;

public sealed class LinuxPlatformTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("bs-platform-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void NxmDesktopEntry_QuotesTheLauncherAndClaimsEveryScheme()
    {
        var entry = LinuxDesktopIntegration.BuildNxmDesktopEntry("/home/me/Apps/Blue $tar \"x\".AppImage");

        entry.Should().Contain("Exec=\"/home/me/Apps/Blue \\$tar \\\"x\\\".AppImage\" %u");
        entry.Should().Contain("MimeType=x-scheme-handler/nxm;x-scheme-handler/vortex;x-scheme-handler/nxm-protocol;");
        entry.Should().StartWith("[Desktop Entry]");
    }

    [Fact]
    public void SteamCmdLinux_RejectsTamperedDownloads()
    {
        SteamCmdWorkshopClient.HasExpectedHash([1, 2, 3]).Should().BeFalse();
    }

    [LinuxFact]
    public async Task SecureStorage_WithoutKeyring_UsesUserOnlyFile()
    {
        var storage = new LinuxSecureStorage(NullLogger<LinuxSecureStorage>.Instance, Path.Combine(_root, "secure"), secretTool: null);

        await storage.SetAsync("nexus-token", "s3cret", CancellationToken.None);

        (await storage.GetAsync("nexus-token", CancellationToken.None)).Should().Be("s3cret");
        var file = Directory.GetFiles(Path.Combine(_root, "secure")).Single();
        File.GetUnixFileMode(file).Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.ReadAllText(file).Should().NotContain("nexus-token", "the key name is hashed");

        (await storage.DeleteAsync("nexus-token", CancellationToken.None)).Should().BeTrue();
        (await storage.GetAsync("nexus-token", CancellationToken.None)).Should().BeNull();
    }

    [LinuxFact]
    public void ReFixUpdate_RestoresExecBitOnScripts()
    {
        var script = Path.Combine(_root, "AutoDeploy.sh");
        File.WriteAllText(script, "#!/bin/sh\n");
        File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite);

        ReFixUpdateService.MakeScriptsExecutable(_root);

        File.GetUnixFileMode(script).Should().HaveFlag(UnixFileMode.UserExecute);
    }
}
