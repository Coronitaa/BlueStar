using System;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using BlueStar.Core.Interfaces;
using BlueStar.Core.Models;
using BlueStar.Infrastructure.Engine;
using BlueStar.Infrastructure.Mods;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace BlueStar.Infrastructure.Tests;

public class EngineDetectorAndModLoaderTests : IDisposable
{
    private readonly string _testDir;
    private readonly EngineDetector _detector;

    public EngineDetectorAndModLoaderTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "BlueStar_EngineModLoaderTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDir);
        _detector = new EngineDetector(NullLogger<EngineDetector>.Instance);
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
    public async Task DetectEngineAsync_IdentifiesUnityMono_AndRecommendsBepInEx5()
    {
        var gameDir = Path.Combine(_testDir, "UnityMonoGame");
        Directory.CreateDirectory(Path.Combine(gameDir, "MyGame_Data", "Managed"));
        File.WriteAllText(Path.Combine(gameDir, "UnityPlayer.dll"), "fake dll");
        File.WriteAllText(Path.Combine(gameDir, "MyGame_Data", "Managed", "Assembly-CSharp.dll"), "assembly");

        var engine = await _detector.DetectEngineAsync(gameDir);

        engine.Type.Should().Be(EngineType.Unity);
        engine.UnityFlavor.Should().Be(UnityFlavor.Mono);
        engine.RecommendedLoader.Should().BeOneOf(RecommendedModLoader.BepInEx5_x64, RecommendedModLoader.BepInEx5_x86);
        engine.Supports(EngineCapabilities.BepInExSupported).Should().BeTrue();
    }

    [Fact]
    public async Task DetectEngineAsync_IdentifiesUnityIL2CPP_AndRecommendsBepInEx6()
    {
        var gameDir = Path.Combine(_testDir, "UnityIL2CPPGame");
        Directory.CreateDirectory(Path.Combine(gameDir, "Game_Data", "il2cpp_data"));
        File.WriteAllText(Path.Combine(gameDir, "UnityPlayer.dll"), "fake dll");
        File.WriteAllText(Path.Combine(gameDir, "GameAssembly.dll"), "il2cpp assembly dll");

        var engine = await _detector.DetectEngineAsync(gameDir);

        engine.Type.Should().Be(EngineType.Unity);
        engine.UnityFlavor.Should().Be(UnityFlavor.IL2CPP);
        engine.RecommendedLoader.Should().Be(RecommendedModLoader.BepInEx6_IL2CPP_x64);
        engine.Supports(EngineCapabilities.BepInExSupported).Should().BeTrue();
    }

    [Fact]
    public async Task DetectEngineAsync_IdentifiesUnrealEngine_AndRecommendsUE4SS()
    {
        var gameDir = Path.Combine(_testDir, "UnrealGame");
        Directory.CreateDirectory(Path.Combine(gameDir, "Engine", "Binaries", "Win64"));
        Directory.CreateDirectory(Path.Combine(gameDir, "GameName", "Binaries", "Win64"));
        Directory.CreateDirectory(Path.Combine(gameDir, "GameName", "Content", "Paks"));

        File.WriteAllText(Path.Combine(gameDir, "GameName", "Binaries", "Win64", "GameName-Win64-Shipping.exe"), "exe");
        File.WriteAllText(Path.Combine(gameDir, "GameName", "Content", "Paks", "pakchunk0.pak"), "pak");

        var engine = await _detector.DetectEngineAsync(gameDir);

        engine.Type.Should().Be(EngineType.UnrealEngine);
        engine.RecommendedLoader.Should().Be(RecommendedModLoader.UE4SS_x64);
        engine.Supports(EngineCapabilities.UE4SSSupported).Should().BeTrue();
    }

    [Fact]
    public async Task InstallUE4SSAsync_ProvisionsSettingsAndModsDirectory()
    {
        var gameDir = Path.Combine(_testDir, "UE4SS_Instance");
        Directory.CreateDirectory(Path.Combine(gameDir, "Binaries", "Win64"));
        Directory.CreateDirectory(Path.Combine(gameDir, "Content", "Paks"));

        var mockBep = new Mock<IBepInExService>();
        var provisioner = new ModLoaderProvisioner(mockBep.Object, new HttpClient(), NullLogger<ModLoaderProvisioner>.Instance);

        var engine = new EngineInfo
        {
            Id = "unreal",
            Name = "Unreal Engine",
            Type = EngineType.UnrealEngine,
            RecommendedLoader = RecommendedModLoader.UE4SS_x64
        };

        bool success = await provisioner.InstallUE4SSAsync(gameDir, engine);
        success.Should().BeTrue();

        var binDir = Path.Combine(gameDir, "Binaries", "Win64");
        File.Exists(Path.Combine(binDir, "UE4SS-settings.ini")).Should().BeTrue();
        Directory.Exists(Path.Combine(binDir, "Mods")).Should().BeTrue();
        Directory.Exists(Path.Combine(gameDir, "Content", "Paks", "~mods")).Should().BeTrue();

        provisioner.IsModLoaderInstalled(gameDir).Should().BeTrue();
        provisioner.GetInstalledModLoader(gameDir).Should().Be("UE4SS");
    }

    [Fact]
    public async Task ToggleModLoaderAsync_EnablesAndDisablesProxyDlls()
    {
        var gameDir = Path.Combine(_testDir, "Toggle_Instance");
        Directory.CreateDirectory(Path.Combine(gameDir, "Win64"));
        var proxy = Path.Combine(gameDir, "Win64", "dwmapi.dll");
        File.WriteAllText(proxy, "proxy");

        var mockBep = new Mock<IBepInExService>();
        var provisioner = new ModLoaderProvisioner(mockBep.Object, new HttpClient(), NullLogger<ModLoaderProvisioner>.Instance);

        // Disable
        await provisioner.ToggleModLoaderAsync(gameDir, enabled: false);
        File.Exists(proxy).Should().BeFalse();
        File.Exists(proxy + ".disabled").Should().BeTrue();

        // Enable
        await provisioner.ToggleModLoaderAsync(gameDir, enabled: true);
        File.Exists(proxy).Should().BeTrue();
        File.Exists(proxy + ".disabled").Should().BeFalse();
    }
}
