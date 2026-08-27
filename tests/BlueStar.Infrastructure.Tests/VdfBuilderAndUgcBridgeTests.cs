using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using BlueStar.Core.Interfaces;
using BlueStar.Core.Models;
using BlueStar.Infrastructure.Storage;
using BlueStar.Infrastructure.Workshop;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BlueStar.Infrastructure.Tests;

public class VdfBuilderAndUgcBridgeTests : IDisposable
{
    private readonly string _testDir;
    private readonly Win32Linker _linker;
    private readonly UgcBridge _ugcBridge;
    private readonly HeuristicModDispatcher _dispatcher;

    public VdfBuilderAndUgcBridgeTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "BlueStar_UgcBridgeTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDir);
        _linker = new Win32Linker(NullLogger<Win32Linker>.Instance);
        _ugcBridge = new UgcBridge(_linker, NullLogger<UgcBridge>.Instance);
        _dispatcher = new HeuristicModDispatcher(_linker, NullLogger<HeuristicModDispatcher>.Instance);
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
    public void VdfBuilder_GeneratesCompliantSteamWorkshopVdf()
    {
        var items = new List<WorkshopItemInfo>
        {
            new(
                PublishedFileId: 123456789UL,
                AppId: 730,
                Title: "Custom Weapon Skin",
                Description: "Awesome Skin",
                PreviewUrl: null,
                FileSizeBytes: 1048576,
                Author: "Modder1",
                UpdatedAt: DateTimeOffset.FromUnixTimeSeconds(1700000000)
            ),
            new(
                PublishedFileId: 987654321UL,
                AppId: 730,
                Title: "Competitive Map",
                Description: "Awesome Map",
                PreviewUrl: null,
                FileSizeBytes: 5242880,
                Author: "Mapper2",
                UpdatedAt: DateTimeOffset.FromUnixTimeSeconds(1700005000)
            )
        };

        var vdf = VdfBuilder.GenerateAppWorkshopVdf(730, items);

        vdf.Should().Contain("\"AppWorkshop\"");
        vdf.Should().Contain("\"appid\"\t\t\"730\"");
        vdf.Should().Contain("\"SizeOnDisk\"\t\t\"6291456\"");
        vdf.Should().Contain("\"NeedsUpdate\"\t\t\"0\"");
        vdf.Should().Contain("\"123456789\"");
        vdf.Should().Contain("\"size\"\t\t\"1048576\"");
        vdf.Should().Contain("\"timeupdated\"\t\t\"1700000000\"");
        vdf.Should().Contain("\"987654321\"");
    }

    [Fact]
    public async Task DeployWorkshopItemToInstanceAsync_PopulatesContentAndEmulatorSettings()
    {
        var instancePath = Path.Combine(_testDir, "GameInstance");
        var cachePath = Path.Combine(_testDir, "Cache", "12345");
        Directory.CreateDirectory(cachePath);
        File.WriteAllText(Path.Combine(cachePath, "mod.pak"), "pak mod content");

        // Deploy to instance
        bool deployed = await _ugcBridge.DeployWorkshopItemToInstanceAsync(730, 12345, cachePath, instancePath);
        deployed.Should().BeTrue();

        var contentDir = _ugcBridge.GetInstanceWorkshopContentPath(instancePath, 730, 12345);
        Directory.Exists(contentDir).Should().BeTrue();
        File.Exists(Path.Combine(contentDir, "mod.pak")).Should().BeTrue();

        // Check emulator mapping in steam_settings/mods/
        var emulatorModDir = Path.Combine(instancePath, "steam_settings", "mods", "12345");
        Directory.Exists(emulatorModDir).Should().BeTrue();
    }

    [Fact]
    public async Task HeuristicModDispatcher_RoutesVpk_ToSourceAddons()
    {
        var modSource = Path.Combine(_testDir, "SourceModSource");
        var instancePath = Path.Combine(_testDir, "L4D2_Instance");

        Directory.CreateDirectory(modSource);
        File.WriteAllText(Path.Combine(modSource, "survivor_skin.vpk"), "vpk content");

        Directory.CreateDirectory(Path.Combine(instancePath, "left4dead2", "addons"));

        var engine = new EngineInfo { Id = "source", Name = "Source Engine", Type = EngineType.Source };
        var result = await _dispatcher.DispatchModPayloadAsync(modSource, instancePath, engine, "SurvivorSkin");

        result.Success.Should().BeTrue();
        result.TargetCategory.Should().Be("SourceEngineAddons");
        File.Exists(Path.Combine(instancePath, "left4dead2", "addons", "workshop", "survivor_skin.vpk")).Should().BeTrue();
    }

    [Fact]
    public async Task HeuristicModDispatcher_RoutesPak_ToUnrealModsFolder()
    {
        var modSource = Path.Combine(_testDir, "UnrealModSource");
        var instancePath = Path.Combine(_testDir, "Unreal_Instance");

        Directory.CreateDirectory(modSource);
        File.WriteAllText(Path.Combine(modSource, "mod_P.pak"), "unreal pak");
        File.WriteAllText(Path.Combine(modSource, "mod_P.ucas"), "unreal ucas");

        Directory.CreateDirectory(Path.Combine(instancePath, "Game", "Content", "Paks"));

        var engine = new EngineInfo { Id = "unreal", Name = "Unreal Engine", Type = EngineType.UnrealEngine };
        var result = await _dispatcher.DispatchModPayloadAsync(modSource, instancePath, engine, "CoolSkin");

        result.Success.Should().BeTrue();
        result.TargetCategory.Should().Be("UnrealPaks");
        File.Exists(Path.Combine(instancePath, "Game", "Content", "Paks", "~mods", "mod_P.pak")).Should().BeTrue();
    }

    [Fact]
    public async Task HeuristicModDispatcher_RoutesDll_ToUnityBepInExPlugins()
    {
        var modSource = Path.Combine(_testDir, "UnityModSource");
        var instancePath = Path.Combine(_testDir, "Unity_Instance");

        Directory.CreateDirectory(modSource);
        File.WriteAllText(Path.Combine(modSource, "CheatsMod.dll"), "csharp assembly dll");

        Directory.CreateDirectory(Path.Combine(instancePath, "BepInEx", "plugins"));

        var engine = new EngineInfo { Id = "unity", Name = "Unity", Type = EngineType.Unity };
        var result = await _dispatcher.DispatchModPayloadAsync(modSource, instancePath, engine, "CheatsMod");

        result.Success.Should().BeTrue();
        result.TargetCategory.Should().Be("UnityBepInExPlugins");
        File.Exists(Path.Combine(instancePath, "BepInEx", "plugins", "CheatsMod", "CheatsMod.dll")).Should().BeTrue();
    }
}
