using BlueStar.Core.Models;
using BlueStar.Infrastructure.Emulators;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BlueStar.Infrastructure.Tests.Linux;

public sealed class ReFixLinuxDeployTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("bs-refix-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Write(string relative, string content = "x")
    {
        var full = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        return full;
    }

    [Theory]
    [InlineData("refix_goldberg", "lan")]
    [InlineData(ReFixEmulator.LanOfflineOptionId, "offline")]
    [InlineData("refix_valve", "online")]
    public void ShellMode_MapsOptionsToAutoDeployModes(string option, string mode)
    {
        ReFixEmulator.ShellMode(option).Should().Be(mode);
    }

    [Fact]
    public void MoveScriptConfig_MovesIniAppIdAndFusionFolder()
    {
        Write("ReFix.ini", "[Game]");
        Write("steam_appid.txt", "480");
        Write(Path.Combine("ReFix", "FusionShared", "ReFixFusionShared.exe"));
        var exeDir = Path.Combine(_root, "Game", "Binaries", "Win64");
        Directory.CreateDirectory(exeDir);
        Write(Path.Combine("Game", "Binaries", "Win64", "ReFix", "stale.txt"));

        ReFixEmulator.MoveScriptConfig(_root, exeDir);

        File.ReadAllText(Path.Combine(exeDir, "ReFix.ini")).Should().Be("[Game]");
        File.Exists(Path.Combine(exeDir, "steam_appid.txt")).Should().BeTrue();
        File.Exists(Path.Combine(exeDir, "ReFix", "FusionShared", "ReFixFusionShared.exe")).Should().BeTrue();
        File.Exists(Path.Combine(exeDir, "ReFix", "stale.txt")).Should().BeFalse();
        File.Exists(Path.Combine(_root, "ReFix.ini")).Should().BeFalse();
        Directory.Exists(Path.Combine(_root, "ReFix")).Should().BeFalse();
    }

    [Fact]
    public void MoveScriptConfig_DoesNothing_WhenExeIsInTheGameRoot()
    {
        Write("ReFix.ini", "[Game]");

        ReFixEmulator.MoveScriptConfig(_root, _root + Path.DirectorySeparatorChar);

        File.Exists(Path.Combine(_root, "ReFix.ini")).Should().BeTrue();
    }

    [LinuxFact]
    public async Task DeployOptionAsync_OnUnrealTree_PutsConfigNextToTheExe()
    {
        var exe = Write(Path.Combine("Game", "Binaries", "Win64", "Game-Win64-Shipping.exe"));
        var steamDir = Path.Combine("Engine", "Binaries", "ThirdParty", "Steamworks", "Steamv157", "Win64");
        Write(Path.Combine(steamDir, "steam_api64.dll"), "valve original");
        var instance = new GameInstance { Name = "Unreal Test", InstallPath = _root, ExecutablePath = exe, AppId = 480 };
        var emulator = new ReFixEmulator(NullLogger<ReFixEmulator>.Instance);

        var ok = await emulator.DeployOptionAsync(instance, "refix_goldberg", null, CancellationToken.None);

        ok.Should().BeTrue();
        var exeDir = Path.GetDirectoryName(exe)!;
        File.Exists(Path.Combine(exeDir, "ReFix.ini")).Should().BeTrue();
        File.ReadAllText(Path.Combine(exeDir, "steam_appid.txt")).Trim().Should().Be("480");
        File.Exists(Path.Combine(_root, "ReFix.ini")).Should().BeFalse();
        File.ReadAllText(Path.Combine(_root, steamDir, "steam_api64_original.dll")).Should().Be("valve original");

        (await emulator.UninstallAsync(instance, CancellationToken.None)).Should().BeTrue();
        File.ReadAllText(Path.Combine(_root, steamDir, "steam_api64.dll")).Should().Be("valve original");
        File.Exists(Path.Combine(exeDir, "ReFix.ini")).Should().BeFalse();
    }

    [LinuxFact]
    public async Task DeployOptionAsync_Fails_WhenGameHasNoSteamApi()
    {
        Write("Game.exe");
        var instance = new GameInstance { Name = "No Steam", InstallPath = _root, AppId = 480 };
        var emulator = new ReFixEmulator(NullLogger<ReFixEmulator>.Instance);

        (await emulator.DeployOptionAsync(instance, "refix_goldberg", null, CancellationToken.None)).Should().BeFalse();
    }
}
