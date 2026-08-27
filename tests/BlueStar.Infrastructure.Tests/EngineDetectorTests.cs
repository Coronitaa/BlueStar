using System;
using System.IO;
using System.Threading.Tasks;
using BlueStar.Core.Models;
using BlueStar.Infrastructure.Engine;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BlueStar.Infrastructure.Tests;

public class EngineDetectorTests : IDisposable
{
    private readonly string _tempTestDir;

    public EngineDetectorTests()
    {
        _tempTestDir = Path.Combine(Path.GetTempPath(), "BlueStar_EngineTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempTestDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempTestDir))
            {
                Directory.Delete(_tempTestDir, recursive: true);
            }
        }
        catch { }
    }

    [Fact]
    public async Task DetectEngineAsync_DetectsUnrealEngine_WhenEngineFolderExists()
    {
        var gameDir = Path.Combine(_tempTestDir, "UnrealGame");
        Directory.CreateDirectory(Path.Combine(gameDir, "Engine", "Binaries", "Win64"));
        Directory.CreateDirectory(Path.Combine(gameDir, "GameFolder", "Binaries", "Win64"));

        var detector = new EngineDetector(NullLogger<EngineDetector>.Instance);
        var engine = await detector.DetectEngineAsync(gameDir);

        engine.Should().NotBeNull();
        engine.Type.Should().Be(EngineType.UnrealEngine);
        engine.Supports(EngineCapabilities.Mods).Should().BeTrue();
        engine.Supports(EngineCapabilities.Emulation).Should().BeTrue();
        engine.Supports(EngineCapabilities.SteamIntegration).Should().BeTrue();
    }

    [Fact]
    public async Task DetectEngineAsync_DetectsUnrealEngine_WhenShippingExeAndPaksExist()
    {
        var gameDir = Path.Combine(_tempTestDir, "UnrealShipped");
        Directory.CreateDirectory(Path.Combine(gameDir, "MyGame", "Binaries", "Win64"));
        Directory.CreateDirectory(Path.Combine(gameDir, "MyGame", "Content", "Paks"));
        File.WriteAllText(Path.Combine(gameDir, "MyGame", "Binaries", "Win64", "MyGame-Win64-Shipping.exe"), "bin");
        File.WriteAllText(Path.Combine(gameDir, "MyGame", "Content", "Paks", "pakchunk0-WindowsNoEditor.pak"), "pak");

        var detector = new EngineDetector(NullLogger<EngineDetector>.Instance);
        var engine = await detector.DetectEngineAsync(gameDir);

        engine.Should().NotBeNull();
        engine.Type.Should().Be(EngineType.UnrealEngine);
    }

    [Fact]
    public async Task DetectEngineAsync_DetectsUnity_WhenUnityPlayerDllExists()
    {
        var gameDir = Path.Combine(_tempTestDir, "UnityGame");
        Directory.CreateDirectory(gameDir);
        File.WriteAllText(Path.Combine(gameDir, "UnityPlayer.dll"), "fake dll");
        Directory.CreateDirectory(Path.Combine(gameDir, "UnityGame_Data"));

        var detector = new EngineDetector(NullLogger<EngineDetector>.Instance);
        var engine = await detector.DetectEngineAsync(gameDir);

        engine.Should().NotBeNull();
        engine.Type.Should().Be(EngineType.Unity);
        engine.Supports(EngineCapabilities.Mods).Should().BeTrue();
        engine.Supports(EngineCapabilities.BepInExSupported).Should().BeTrue();
    }

    [Fact]
    public async Task DetectEngineAsync_DetectsGodot_WhenPckFileExists()
    {
        var gameDir = Path.Combine(_tempTestDir, "GodotGame");
        Directory.CreateDirectory(gameDir);
        File.WriteAllText(Path.Combine(gameDir, "game.pck"), "godot package");

        var detector = new EngineDetector(NullLogger<EngineDetector>.Instance);
        var engine = await detector.DetectEngineAsync(gameDir);

        engine.Should().NotBeNull();
        engine.Type.Should().Be(EngineType.Godot);
    }

    [Fact]
    public async Task DetectEngineAsync_DetectsGodot_WhenEmbeddedGdpcSignatureInExe()
    {
        var gameDir = Path.Combine(_tempTestDir, "GodotEmbedded");
        Directory.CreateDirectory(gameDir);

        var exePath = Path.Combine(gameDir, "Game.exe");
        var exeBytes = new byte[2048];
        // Write 'G' 'D' 'P' 'C' at byte 1800
        exeBytes[1800] = 0x47; // G
        exeBytes[1801] = 0x44; // D
        exeBytes[1802] = 0x50; // P
        exeBytes[1803] = 0x43; // C
        File.WriteAllBytes(exePath, exeBytes);

        var detector = new EngineDetector(NullLogger<EngineDetector>.Instance);
        var engine = await detector.DetectEngineAsync(gameDir);

        engine.Should().NotBeNull();
        engine.Type.Should().Be(EngineType.Godot);
    }

    [Fact]
    public async Task DetectEngineAsync_DetectsSource1_WhenTier0AndGameInfoExist()
    {
        var gameDir = Path.Combine(_tempTestDir, "SourceGame");
        Directory.CreateDirectory(Path.Combine(gameDir, "bin"));
        Directory.CreateDirectory(Path.Combine(gameDir, "cstrike"));
        File.WriteAllText(Path.Combine(gameDir, "bin", "tier0.dll"), "tier0");
        File.WriteAllText(Path.Combine(gameDir, "cstrike", "gameinfo.txt"), "gameinfo");

        var detector = new EngineDetector(NullLogger<EngineDetector>.Instance);
        var engine = await detector.DetectEngineAsync(gameDir);

        engine.Should().NotBeNull();
        engine.Type.Should().Be(EngineType.Source);
    }

    [Fact]
    public async Task DetectEngineAsync_DetectsSource2_WhenGameInfoGiExists()
    {
        var gameDir = Path.Combine(_tempTestDir, "Source2Game");
        Directory.CreateDirectory(Path.Combine(gameDir, "game", "csgo"));
        File.WriteAllText(Path.Combine(gameDir, "game", "csgo", "gameinfo.gi"), "source2 gi");

        var detector = new EngineDetector(NullLogger<EngineDetector>.Instance);
        var engine = await detector.DetectEngineAsync(gameDir);

        engine.Should().NotBeNull();
        engine.Type.Should().Be(EngineType.Source2);
    }

    [Fact]
    public async Task DetectEngineAsync_DetectsReEngine_WhenReChunkPakExists()
    {
        var gameDir = Path.Combine(_tempTestDir, "CapcomGame");
        Directory.CreateDirectory(gameDir);
        File.WriteAllText(Path.Combine(gameDir, "re_chunk_000.pak"), "pak");

        var detector = new EngineDetector(NullLogger<EngineDetector>.Instance);
        var engine = await detector.DetectEngineAsync(gameDir);

        engine.Should().NotBeNull();
        engine.Type.Should().Be(EngineType.ReEngine);
    }

    [Fact]
    public async Task DetectEngineAsync_DetectsRpgMaker_WhenRgssExists()
    {
        var gameDir = Path.Combine(_tempTestDir, "RpgGame");
        Directory.CreateDirectory(gameDir);
        File.WriteAllText(Path.Combine(gameDir, "Game.rgss3a"), "rgss");

        var detector = new EngineDetector(NullLogger<EngineDetector>.Instance);
        var engine = await detector.DetectEngineAsync(gameDir);

        engine.Should().NotBeNull();
        engine.Type.Should().Be(EngineType.RpgMaker);
    }

    [Fact]
    public async Task DetectEngineAsync_DetectsRenPy_WhenRenpyDirExists()
    {
        var gameDir = Path.Combine(_tempTestDir, "VisualNovel");
        Directory.CreateDirectory(Path.Combine(gameDir, "renpy"));
        File.WriteAllText(Path.Combine(gameDir, "game.rpa"), "rpa");

        var detector = new EngineDetector(NullLogger<EngineDetector>.Instance);
        var engine = await detector.DetectEngineAsync(gameDir);

        engine.Should().NotBeNull();
        engine.Type.Should().Be(EngineType.RenPy);
    }

    [Fact]
    public async Task DetectEngineAsync_DetectsGameMaker_WhenDataWinExists()
    {
        var gameDir = Path.Combine(_tempTestDir, "IndieGame");
        Directory.CreateDirectory(gameDir);
        File.WriteAllText(Path.Combine(gameDir, "data.win"), "gamemaker");

        var detector = new EngineDetector(NullLogger<EngineDetector>.Instance);
        var engine = await detector.DetectEngineAsync(gameDir);

        engine.Should().NotBeNull();
        engine.Type.Should().Be(EngineType.GameMaker);
    }

    [Fact]
    public async Task DetectEngineAsync_DetectsIsaacEngine_WhenIsaacFilesPresent()
    {
        var gameDir = Path.Combine(_tempTestDir, "BindingOfIsaac");
        Directory.CreateDirectory(Path.Combine(gameDir, "resources", "packed"));
        Directory.CreateDirectory(Path.Combine(gameDir, "resources", "scripts"));
        File.WriteAllText(Path.Combine(gameDir, "isaac-ng.exe"), "isaac");
        File.WriteAllText(Path.Combine(gameDir, "resources", "packed", "afterbirth.a"), "archive");
        File.WriteAllText(Path.Combine(gameDir, "lua5.3.dll"), "lua dll");
        File.WriteAllText(Path.Combine(gameDir, "resources", "scripts", "main.lua"), "lua script");

        var detector = new EngineDetector(NullLogger<EngineDetector>.Instance);
        var engine = await detector.DetectEngineAsync(gameDir);

        engine.Should().NotBeNull();
        engine.Type.Should().Be(EngineType.Custom);
        engine.Name.Should().Contain("Isaac");
        engine.Type.Should().NotBe(EngineType.Supergiant);
    }

    [Fact]
    public async Task DetectEngineAsync_DetectsSupergiant_WhenHadesFilesPresent()
    {
        var gameDir = Path.Combine(_tempTestDir, "HadesGame");
        Directory.CreateDirectory(Path.Combine(gameDir, "Content", "Packages"));
        Directory.CreateDirectory(Path.Combine(gameDir, "Content", "Scripts"));
        File.WriteAllText(Path.Combine(gameDir, "Hades.exe"), "hades");
        File.WriteAllText(Path.Combine(gameDir, "Content", "Packages", "Package1.pkg"), "pkg");
        File.WriteAllText(Path.Combine(gameDir, "Content", "Scripts", "RoomManager.lua"), "lua");

        var detector = new EngineDetector(NullLogger<EngineDetector>.Instance);
        var engine = await detector.DetectEngineAsync(gameDir);

        engine.Should().NotBeNull();
        engine.Type.Should().Be(EngineType.Supergiant);
    }

    [Fact]
    public async Task DetectEngineAsync_FallbackToGeneric_WhenNoEngineSignaturesFound()
    {
        var gameDir = Path.Combine(_tempTestDir, "SimpleGame");
        Directory.CreateDirectory(gameDir);
        File.WriteAllText(Path.Combine(gameDir, "game.exe"), "executable");

        var detector = new EngineDetector(NullLogger<EngineDetector>.Instance);
        var engine = await detector.DetectEngineAsync(gameDir);

        engine.Should().NotBeNull();
        engine.Type.Should().Be(EngineType.Generic);
    }
}
