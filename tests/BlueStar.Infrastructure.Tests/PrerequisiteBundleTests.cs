using System;
using System.IO;
using BlueStar.Infrastructure.Services;
using FluentAssertions;
using Xunit;

namespace BlueStar.Infrastructure.Tests;

public class PrerequisiteBundleTests : IDisposable
{
    private readonly string _appDir;

    public PrerequisiteBundleTests()
    {
        _appDir = Path.Combine(Path.GetTempPath(), "BlueStar_PrereqBundle_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_appDir, "prerequisites"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_appDir, recursive: true); } catch { }
    }

    [Theory]
    [InlineData("vcredist_2015_2022_x64", "vc_redist.x64.exe")]
    [InlineData("vcredist_2015_2022_x86", "vc_redist.x86.exe")]
    [InlineData("webview2_runtime", "MicrosoftEdgeWebView2RuntimeInstallerX64.exe")]
    public void Bundled_offline_installer_is_used_when_shipped_with_the_app(string id, string fileName)
    {
        var installer = Path.Combine(_appDir, "prerequisites", fileName);
        File.WriteAllText(installer, "stub");

        PrerequisiteService.BundledInstallerPath(id, _appDir).Should().Be(installer);
    }

    [Fact]
    public void Missing_or_unknown_installers_fall_back_to_download()
    {
        PrerequisiteService.BundledInstallerPath("vcredist_2015_2022_x64", _appDir).Should().BeNull();
        PrerequisiteService.BundledInstallerPath("directx_enduser", _appDir).Should().BeNull();
    }
}
