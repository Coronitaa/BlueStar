using System;
using System.IO;
using System.Threading.Tasks;
using BlueStar.Infrastructure.Workshop;
using FluentAssertions;
using Xunit;

namespace BlueStar.Infrastructure.Tests;

[Collection("DepotDownloaderHost")]
public class DepotDownloaderHostTests : IDisposable
{
    private readonly string _dir;
    private readonly string? _previousHost;

    public DepotDownloaderHostTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "BlueStar_DDHost_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _previousHost = DepotDownloaderHost.HostExecutablePath;
    }

    public void Dispose()
    {
        DepotDownloaderHost.HostExecutablePath = _previousHost;
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void Runs_through_BlueStar_when_the_host_is_set_even_without_the_standalone_tool()
    {
        var host = Path.Combine(_dir, "BlueStar.exe");
        File.WriteAllText(host, "stub");
        DepotDownloaderHost.HostExecutablePath = host;

        DepotDownloaderHost.IsAvailable(null).Should().BeTrue();
        var psi = DepotDownloaderHost.CreateStartInfo(null, "-app 4000 -pubfile 1 -dir \"D:/mods\"");

        psi.FileName.Should().Be(host);
        psi.Arguments.Should().Be($"{DepotDownloaderHost.HostSwitch} -app 4000 -pubfile 1 -dir \"D:/mods\"");
        psi.RedirectStandardOutput.Should().BeTrue();
        psi.EnvironmentVariables["DOTNET_ROOT"].Should().NotBe(AppContext.BaseDirectory);
    }

    [Fact]
    public void Falls_back_to_the_standalone_tool_without_a_host()
    {
        DepotDownloaderHost.HostExecutablePath = null;
        var tool = Path.Combine(_dir, "DepotDownloaderMod.exe");

        DepotDownloaderHost.IsAvailable(tool).Should().BeFalse();

        File.WriteAllText(tool, "stub");
        DepotDownloaderHost.IsAvailable(tool).Should().BeTrue();
        var psi = DepotDownloaderHost.CreateStartInfo(tool, "-app 1");
        psi.FileName.Should().Be(tool);
        psi.Arguments.Should().Be("-app 1");
    }

    [Fact]
    public void The_library_still_exposes_the_command_line_entry_point()
    {
        var main = DepotDownloaderHost.ResolveCommandLineEntryPoint();

        main.ReturnType.Should().Be(typeof(Task<int>));
    }
}
