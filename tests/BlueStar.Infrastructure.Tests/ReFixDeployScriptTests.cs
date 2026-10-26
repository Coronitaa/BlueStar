using System;
using System.IO;
using BlueStar.Core.Models;
using BlueStar.Infrastructure.Emulators;
using FluentAssertions;
using Xunit;

namespace BlueStar.Infrastructure.Tests;

public class ReFixDeployScriptTests : IDisposable
{
    private readonly string _deployDir;
    private readonly string _binDir;

    public ReFixDeployScriptTests()
    {
        _deployDir = Path.Combine(Path.GetTempPath(), "BlueStar_ReFixDeployScript_" + Guid.NewGuid().ToString("N"));
        _binDir = Path.Combine(_deployDir, "bin");
        Directory.CreateDirectory(_binDir);
        File.WriteAllText(Path.Combine(_binDir, "deploy_helper.ps1"), "# suite script");
    }

    public void Dispose()
    {
        try { Directory.Delete(_deployDir, recursive: true); } catch { }
    }

    private void WriteSuiteVersion(string version) =>
        File.WriteAllText(Path.Combine(_deployDir, "refix_version.json"), $"{{\"version\":\"{version}\"}}");

    private static GameInstance Instance(bool useVersionScript = false) =>
        new() { Name = "R.E.P.O.", InstallPath = "C:\\Games\\REPO", UseVersionDeployScript = useVersionScript };

    [Fact]
    public void Suite_newer_than_standard_scripts_runs_its_own_deploy_helper()
    {
        // v1.4.0 writes [Photon] Backend=ReFixCloud; the older standard script writes OfficialPhoton,
        // which makes R.E.P.O. fail with "Custom Authentication Failed".
        WriteSuiteVersion("1.4.0");

        var script = ReFixEmulator.ResolveDeployScript(Instance(), _deployDir, _binDir, "deploy_helper.ps1");

        script.Should().Be(Path.Combine(_binDir, "deploy_helper.ps1"));
    }

    [Fact]
    public void Firewall_rules_never_use_the_suite_script_that_prompts_for_UAC()
    {
        WriteSuiteVersion("1.4.0");
        File.WriteAllText(Path.Combine(_binDir, "apply_firewall.ps1"), "Start-Process cmd.exe -Verb RunAs");

        var script = ReFixEmulator.ResolveFirewallScript();

        script.Should().NotBe(Path.Combine(_binDir, "apply_firewall.ps1"));
        if (script != null) File.ReadAllText(script).Should().NotContain("-Verb RunAs");
    }

    [Fact]
    public void Explicit_version_script_setting_still_wins_for_old_suites()
    {
        WriteSuiteVersion("1.21");

        var script = ReFixEmulator.ResolveDeployScript(Instance(useVersionScript: true), _deployDir, _binDir, "deploy_helper.ps1");

        script.Should().Be(Path.Combine(_binDir, "deploy_helper.ps1"));
    }

    [Theory]
    [InlineData("1.21", "1.2.1")]
    [InlineData("v1.23", "1.2.3")]
    [InlineData("1.3.3", "1.3.3")]
    [InlineData("1.4.0", "1.4.0")]
    public void Suite_versions_follow_ReFix_tag_order(string tag, string expected)
    {
        ReFixEmulator.ParseSuiteVersion(tag).Should().Be(Version.Parse(expected));
    }

    [Fact]
    public void Old_suite_without_the_setting_does_not_run_its_own_script()
    {
        WriteSuiteVersion("1.21");

        var script = ReFixEmulator.ResolveDeployScript(Instance(), _deployDir, _binDir, "deploy_helper.ps1");

        // Uses the bundled standard script; only without one does it fall back to the suite's own copy.
        var standard = Path.Combine(AppContext.BaseDirectory, "tools", "ReFix_StandardDeploy", "bin", "deploy_helper.ps1");
        script.Should().Be(File.Exists(standard) ? standard : Path.Combine(_binDir, "deploy_helper.ps1"));
    }
}
