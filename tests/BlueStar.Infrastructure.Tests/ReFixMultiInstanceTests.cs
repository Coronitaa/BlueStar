using System;
using System.IO;
using System.Threading.Tasks;
using BlueStar.Core.Interfaces;
using BlueStar.Core.Models;
using BlueStar.Infrastructure.Emulators;
using BlueStar.Infrastructure.Engine;
using BlueStar.Infrastructure.Instance;
using BlueStar.Infrastructure.Storage;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BlueStar.Infrastructure.Tests;

public class ReFixMultiInstanceTests : IDisposable
{
    private readonly string _testDir;
    private readonly ReFixManager _refixManager;
    private readonly Win32Linker _linker;
    private readonly InstanceStorageManager _storageManager;
    private readonly InstanceManager _instanceManager;

    public ReFixMultiInstanceTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "BlueStar_ReFixMultiTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDir);

        _refixManager = new ReFixManager(NullLogger<ReFixManager>.Instance);
        _linker = new Win32Linker(NullLogger<Win32Linker>.Instance);
        _storageManager = new InstanceStorageManager(_linker, NullLogger<InstanceStorageManager>.Instance);
        var engineDetector = new EngineDetector(NullLogger<EngineDetector>.Instance);

        var instancesDir = Path.Combine(_testDir, "instances");
        var depotsDir = Path.Combine(_testDir, "depots");

        _instanceManager = new InstanceManager(
            NullLogger<InstanceManager>.Instance,
            statsService: null,
            storageManager: _storageManager,
            engineDetector: engineDetector,
            refixManager: _refixManager,
            rootPath: instancesDir,
            depotsRootPath: depotsDir);
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
    public async Task ConfigureInstanceSettingsAsync_WritesExpectedFiles()
    {
        var gameDir = Path.Combine(_testDir, "GameWithReFix");
        Directory.CreateDirectory(gameDir);

        var config = new InstanceReFixConfig
        {
            AppId = 730,
            SteamId = 76561198000000005UL,
            AccountName = "Player_Leader",
            ListenPort = 47585,
            DisableOverlay = true,
            LocalSave = true
        };

        bool ok = await _refixManager.ConfigureInstanceSettingsAsync(gameDir, config);
        ok.Should().BeTrue();

        var settingsDir = Path.Combine(gameDir, "steam_settings");
        File.ReadAllText(Path.Combine(settingsDir, "steam_appid.txt")).Trim().Should().Be("730");
        File.ReadAllText(Path.Combine(settingsDir, "force_steamid.txt")).Trim().Should().Be("76561198000000005");
        File.ReadAllText(Path.Combine(settingsDir, "force_account_name.txt")).Trim().Should().Be("Player_Leader");
        File.ReadAllText(Path.Combine(settingsDir, "listen_port.txt")).Trim().Should().Be("47585");
        File.ReadAllText(Path.Combine(settingsDir, "disable_overlay.txt")).Trim().Should().Be("1");
        File.ReadAllText(Path.Combine(settingsDir, "local_save.txt")).Trim().Should().Be("1");

        var readBack = await _refixManager.ReadInstanceSettingsAsync(gameDir);
        readBack.Should().NotBeNull();
        readBack!.AppId.Should().Be(730);
        readBack.SteamId.Should().Be(76561198000000005UL);
        readBack.AccountName.Should().Be("Player_Leader");
        readBack.ListenPort.Should().Be(47585);
    }

    [Fact]
    public void GenerateUniqueSteamId_ProducesUniqueAndDeterministicIds()
    {
        var guidA = Guid.NewGuid();
        var guidB = Guid.NewGuid();

        var steamIdA1 = _refixManager.GenerateUniqueSteamId(guidA, slotIndex: 0);
        var steamIdA2 = _refixManager.GenerateUniqueSteamId(guidA, slotIndex: 0);
        var steamIdB = _refixManager.GenerateUniqueSteamId(guidB, slotIndex: 1);

        // Deterministic for same Guid
        steamIdA1.Should().Be(steamIdA2);

        // Unique across distinct instances
        steamIdA1.Should().NotBe(steamIdB);

        // Valid 64-bit SteamID format (starts with 7656...)
        steamIdA1.ToString().Should().StartWith("7656");
        steamIdB.ToString().Should().StartWith("7656");
    }

    [Fact]
    public async Task CreateInstanceFromDepotAsync_And_CloneInstanceAsync_WorkEndToEnd()
    {
        // 1. Create dummy base depot
        var depotPath = _instanceManager.GetBaseDepotPath(4000);
        Directory.CreateDirectory(depotPath);
        File.WriteAllText(Path.Combine(depotPath, "hl2.exe"), "Source Executable");
        File.WriteAllText(Path.Combine(depotPath, "gameinfo.txt"), "gameinfo");

        // 2. Create Instance 1 (Player 1)
        var instance1 = await _instanceManager.CreateInstanceFromDepotAsync(4000, "GarrysMod_Player1", depotPath);

        instance1.Should().NotBeNull();
        instance1.Name.Should().Be("GarrysMod_Player1");
        instance1.AppId.Should().Be(4000);
        instance1.Engine?.Type.Should().Be(EngineType.Source);

        var settings1 = await _refixManager.ReadInstanceSettingsAsync(instance1.InstallPath);
        settings1.Should().NotBeNull();
        settings1!.AccountName.Should().Be("GarrysMod_Player1");

        // 3. Clone Instance 2 (Player 2)
        var instance2 = await _instanceManager.CloneInstanceAsync(instance1.Id, "GarrysMod_Player2");

        instance2.Should().NotBeNull();
        instance2.Id.Should().NotBe(instance1.Id);
        instance2.Name.Should().Be("GarrysMod_Player2");

        var settings2 = await _refixManager.ReadInstanceSettingsAsync(instance2.InstallPath);
        settings2.Should().NotBeNull();
        settings2!.AccountName.Should().Be("GarrysMod_Player2");

        // Verify distinct SteamID & Listen Ports between player 1 and player 2
        settings1.SteamId.Should().NotBe(settings2.SteamId);
        settings1.ListenPort.Should().NotBe(settings2.ListenPort);
    }
}
