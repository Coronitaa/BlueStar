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
        Directory.CreateDirectory(Path.Combine(instancePath, "steam_settings"));
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

    [Fact]
    public void AdaptAndDeployWorkshopMod_CorrectlyFlattensAndDeploysKleiMod_ForDontStarve()
    {
        var stagingFolder = Path.Combine(_testDir, "StagingKlei");
        var instancePath = Path.Combine(_testDir, "DontStarve_Instance");

        // Simulate a downloaded mod with nested subfolder
        Directory.CreateDirectory(Path.Combine(stagingFolder, "nested_folder", "scripts"));
        File.WriteAllText(Path.Combine(stagingFolder, "nested_folder", "modinfo.lua"), "name = 'Combined Status'\nversion = '1.0'");
        File.WriteAllText(Path.Combine(stagingFolder, "nested_folder", "modmain.lua"), "GLOBAL.print('Mod Loaded')");
        File.WriteAllText(Path.Combine(stagingFolder, "nested_folder", "scripts", "status.lua"), "-- script");

        // Game instance has mods/, modsettings.lua, and optional steam_settings
        Directory.CreateDirectory(Path.Combine(instancePath, "mods"));
        Directory.CreateDirectory(Path.Combine(instancePath, "steam_settings"));
        File.WriteAllText(Path.Combine(instancePath, "mods", "modsettings.lua"), "-- Default settings\n");

        var instance = new GameInstance
        {
            Id = Guid.NewGuid(),
            Name = "Don't Starve",
            AppId = 214950,
            InstallPath = instancePath
        };

        var targetFolder = Mods.GameModPathResolver.GetItemTargetFolder(instance, 378160970UL, "Combined Status");
        var details = new WorkshopItemInfo(378160970UL, 214950, "Combined Status", "Status mod", null, 1024, "Author", DateTimeOffset.UtcNow);

        Mods.GameModPathResolver.AdaptAndDeployWorkshopMod(instance, 378160970UL, stagingFolder, targetFolder, details);

        // Verification 1: target folder is named workshop-378160970
        targetFolder.Should().EndWith("workshop-378160970");

        // Verification 2: modinfo.lua and modmain.lua are directly in root of workshop-378160970
        File.Exists(Path.Combine(targetFolder, "modinfo.lua")).Should().BeTrue();
        File.Exists(Path.Combine(targetFolder, "modmain.lua")).Should().BeTrue();
        File.Exists(Path.Combine(targetFolder, "scripts", "status.lua")).Should().BeTrue();

        // Verification 3: ForceEnableMod was registered in modsettings.lua
        var modsettingsContent = File.ReadAllText(Path.Combine(instancePath, "mods", "modsettings.lua"));
        modsettingsContent.Should().Contain("ForceEnableMod(\"workshop-378160970\")");

        // Verification 4: Subscribed items registered for Goldberg/ReFix emulator
        var subFile = Path.Combine(instancePath, "steam_settings", "subscribed_items.txt");
        File.Exists(subFile).Should().BeTrue();
        File.ReadAllText(subFile).Should().Contain("378160970");
    }

    [Fact]
    public void IsEmulatorInstalled_ReturnsFalse_WhenNoEmulatorBinariesExist()
    {
        var cleanGameDir = Path.Combine(_testDir, "CleanGameNoEmulator");
        Directory.CreateDirectory(cleanGameDir);
        Directory.CreateDirectory(Path.Combine(cleanGameDir, "mods"));

        // Should return false even if steam_settings folder exists without binaries
        Directory.CreateDirectory(Path.Combine(cleanGameDir, "steam_settings"));

        Emulators.ReFixEmulator.IsEmulatorInstalled(cleanGameDir).Should().BeFalse();
        Emulators.ReFixEmulator.GetInstalledMode(cleanGameDir).Should().BeNull();
    }

    [Fact]
    public void IsEmulatorInstalled_ReturnsFalse_WhenOnlySmokeApiDlcUnlockerIsInstalled()
    {
        var dlcOnlyDir = Path.Combine(_testDir, "DlcOnlyNoEmulator");
        Directory.CreateDirectory(dlcOnlyDir);
        File.WriteAllText(Path.Combine(dlcOnlyDir, "steam_api64.dll"), "fake smokeapi dll");
        File.WriteAllText(Path.Combine(dlcOnlyDir, "steam_api64_o.dll"), "fake backup dll");
        File.WriteAllText(Path.Combine(dlcOnlyDir, "cream_api.ini"), "[steam]\nappid=1234");
        File.WriteAllText(Path.Combine(dlcOnlyDir, "SmokeAPI.config.json"), "{}");

        Emulators.ReFixEmulator.IsEmulatorInstalled(dlcOnlyDir).Should().BeFalse();
        Emulators.ReFixEmulator.GetInstalledMode(dlcOnlyDir).Should().BeNull();
    }

    [Fact]
    public void GetInstalledMode_ReturnsReGoldbergLan_WhenReFixIniSpecifiesGoldberg()
    {
        var goldbergDir = Path.Combine(_testDir, "GoldbergReFixGame");
        Directory.CreateDirectory(goldbergDir);
        File.WriteAllText(Path.Combine(goldbergDir, "steam_api64.dll"), "proxy");
        File.WriteAllText(Path.Combine(goldbergDir, "steam_api64_valve.dll"), "goldberg backend");
        File.WriteAllText(Path.Combine(goldbergDir, "local_save.txt"), "saves");
        File.WriteAllText(Path.Combine(goldbergDir, "ReFix.ini"), "[Online]\nMode=goldberg\n");

        Emulators.ReFixEmulator.IsEmulatorInstalled(goldbergDir).Should().BeTrue();
        Emulators.ReFixEmulator.GetInstalledMode(goldbergDir).Should().Be("Re:Goldberg LAN");
    }

    [Fact]
    public void GetInstalledMode_ReturnsReFixOnline_WhenReFixIniSpecifiesValve()
    {
        var valveDir = Path.Combine(_testDir, "ValveReFixGame");
        Directory.CreateDirectory(valveDir);
        File.WriteAllText(Path.Combine(valveDir, "steam_api64.dll"), "proxy");
        File.WriteAllText(Path.Combine(valveDir, "steam_api64_valve.dll"), "original valve");
        File.WriteAllText(Path.Combine(valveDir, "ReFix.ini"), "[Online]\nMode=valve\n");

        Emulators.ReFixEmulator.IsEmulatorInstalled(valveDir).Should().BeTrue();
        Emulators.ReFixEmulator.GetInstalledMode(valveDir).Should().Be("ReFix Online (Steam)");
    }

    [Fact]
    public void FindGameRoot_CorrectlyClimbsUp_FromBinSubdirectory()
    {
        var rootDir = Path.Combine(_testDir, "TrueGameRoot");
        var binDir = Path.Combine(rootDir, "bin");
        var exePath = Path.Combine(binDir, "dontstarve_steam.exe");

        Directory.CreateDirectory(binDir);
        Directory.CreateDirectory(Path.Combine(rootDir, "data"));
        Directory.CreateDirectory(Path.Combine(rootDir, "mods"));
        File.WriteAllText(exePath, "fake exe");

        var detectedFromBin = Mods.GameModPathResolver.FindGameRoot(binDir, exePath);
        var detectedFromExe = Mods.GameModPathResolver.FindGameRoot(exePath, exePath);

        detectedFromBin.Should().BeEquivalentTo(rootDir);
        detectedFromExe.Should().BeEquivalentTo(rootDir);
    }

    [Fact]
    public void TabletopSimulator_DeploysSaveAndCleansBrokenFiles()
    {
        var ttsDir = Path.Combine(_testDir, "TTS_Test_Workshop");
        Directory.CreateDirectory(ttsDir);

        // Simulate broken files left in Workshop folder
        File.WriteAllText(Path.Combine(ttsDir, "999_info.json"), "{\"bad\": true}");
        File.WriteAllText(Path.Combine(ttsDir, "workshop_info.json"), "{\"bad\": true}");

        var staging = Path.Combine(_testDir, "TTS_Staging");
        Directory.CreateDirectory(staging);
        File.WriteAllText(Path.Combine(staging, "WorkshopUpload"), "{\"SaveName\":\"Secret Hitler Edition\",\"ObjectStates\":[]}");

        var instance = new GameInstance
        {
            Id = Guid.NewGuid(),
            Name = "Tabletop Simulator",
            AppId = 286160,
            InstallPath = Path.Combine(_testDir, "TTS_Install")
        };

        var details = new WorkshopItemInfo(123456UL, 286160, "Secret Hitler Edition", "Popular game", null, 2048, "Author", DateTimeOffset.UtcNow);

        Mods.GameModPathResolver.AdaptAndDeployWorkshopMod(instance, 123456UL, staging, ttsDir, details);

        // Verification 1: 123456.json exists and contains SaveName
        var saveJson = Path.Combine(ttsDir, "123456.json");
        File.Exists(saveJson).Should().BeTrue();
        File.ReadAllText(saveJson).Should().Contain("Secret Hitler Edition");

        // Verification 2: Bad _info.json and workshop_info.json were cleaned up
        File.Exists(Path.Combine(ttsDir, "999_info.json")).Should().BeFalse();
        File.Exists(Path.Combine(ttsDir, "workshop_info.json")).Should().BeFalse();
        File.Exists(Path.Combine(ttsDir, "123456_info.json")).Should().BeFalse();

        // Verification 3: WorkshopFileInfos.json index exists and registers the item
        var indexFile = Path.Combine(ttsDir, "WorkshopFileInfos.json");
        File.Exists(indexFile).Should().BeTrue();
        File.ReadAllText(indexFile).Should().Contain("123456");
        File.ReadAllText(indexFile).Should().Contain("Secret Hitler Edition");
    }
}
