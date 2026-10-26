using System;
using System.IO;
using BlueStar.Core.Models;
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
    [InlineData("webview2_runtime", "MicrosoftEdgeWebview2Setup.exe")]
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
    [Fact]
    public void Versioned_executable_is_found_inside_a_version_folder()
    {
        var root = Path.Combine(_appDir, "EdgeWebView", "Application");
        Directory.CreateDirectory(Path.Combine(root, "141.0.3537.71"));
        PrerequisiteService.HasVersionedExecutable(root, "msedgewebview2.exe").Should().BeFalse();

        File.WriteAllText(Path.Combine(root, "141.0.3537.71", "msedgewebview2.exe"), "stub");

        PrerequisiteService.HasVersionedExecutable(root, "msedgewebview2.exe").Should().BeTrue();
        PrerequisiteService.HasVersionedExecutable(Path.Combine(_appDir, "missing"), "msedgewebview2.exe").Should().BeFalse();
    }

    [Fact]
    public void Runtime_files_count_only_when_all_are_present()
    {
        File.WriteAllText(Path.Combine(_appDir, "vcruntime140.dll"), "stub");
        PrerequisiteService.AllFilesExist(_appDir, "vcruntime140.dll", "msvcp140.dll").Should().BeFalse();

        File.WriteAllText(Path.Combine(_appDir, "msvcp140.dll"), "stub");
        PrerequisiteService.AllFilesExist(_appDir, "vcruntime140.dll", "msvcp140.dll").Should().BeTrue();
    }

    [Theory]
    [InlineData(true, PrerequisiteStatus.NeedsDownload, true)]
    [InlineData(true, PrerequisiteStatus.InstalledInSystem, false)]
    [InlineData(true, PrerequisiteStatus.InstalledSuccess, false)]
    [InlineData(false, PrerequisiteStatus.NeedsDownload, false)]
    public void Only_missing_essential_components_trigger_the_startup_warning(bool essential, PrerequisiteStatus status, bool expected)
    {
        var item = new PrerequisiteItem { Id = "x", Name = "X", IsEssential = essential, Status = status };

        item.IsMissingEssential.Should().Be(expected);
    }

    [Fact]
    public async System.Threading.Tasks.Task Optional_runtimes_are_not_essential_for_BlueStar()
    {
        var service = new PrerequisiteService(new System.Net.Http.HttpClient(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<PrerequisiteService>.Instance);

        var items = await service.DetectSystemPrerequisitesAsync();

        items.Should().Contain(i => i.Id == "directx_enduser" && !i.IsEssential);
        items.Should().Contain(i => i.Id == "dotnet_desktop_8" && !i.IsEssential);
        items.Should().NotContain(i => i.Id == "dotnet_runtime_8");
        items.Should().Contain(i => i.Id == "webview2_runtime" && i.IsEssential);
    }
}
