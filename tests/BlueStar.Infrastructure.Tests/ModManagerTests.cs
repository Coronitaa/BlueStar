using System.IO;
using System.Linq;
using BlueStar.Core.Models;
using BlueStar.Infrastructure.Mods;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BlueStar.Infrastructure.Tests;

public class ModManagerTests : IDisposable
{
    private readonly string _tempTestDir;

    public ModManagerTests()
    {
        _tempTestDir = Path.Combine(Path.GetTempPath(), "BlueStar_ModTests_" + Guid.NewGuid().ToString("N"));
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
    public async Task UnrealModManager_InstallsAndTogglesMods()
    {
        // Arrange
        var gameDir = Path.Combine(_tempTestDir, "UnrealGame");
        var paksDir = Path.Combine(gameDir, "Game", "Content", "Paks");
        Directory.CreateDirectory(paksDir);

        var instance = new GameInstance
        {
            Name = "Unreal Test Game",
            AppId = 12345,
            InstallPath = gameDir,
            Engine = new EngineInfo
            {
                Id = "unreal",
                Name = "Unreal Engine",
                Type = EngineType.UnrealEngine,
                Capabilities = EngineCapabilities.Mods
            }
        };

        var manager = new UnrealModManager(NullLogger<UnrealModManager>.Instance);

        // Act 1: Check support & directory
        manager.IsSupported(instance).Should().BeTrue();
        var modsDir = manager.GetModsDirectory(instance);
        modsDir.Should().EndWith("~mods");

        // Act 2: Create a dummy pak file to install
        var sourceMod = Path.Combine(_tempTestDir, "CustomSkin.pak");
        File.WriteAllText(sourceMod, "dummy mod data");

        var installResult = await manager.InstallModAsync(instance, sourceMod);
        installResult.Should().BeTrue();

        // Act 3: List mods
        var installed = await manager.GetInstalledModsAsync(instance);
        installed.Should().HaveCount(1);
        installed[0].Name.Should().Be("CustomSkin");
        installed[0].IsEnabled.Should().BeTrue();

        // Act 4: Toggle mod disabled
        var toggleResult = await manager.ToggleModAsync(instance, installed[0].Id, false);
        toggleResult.Should().BeTrue();

        var afterDisable = await manager.GetInstalledModsAsync(instance);
        afterDisable.Should().HaveCount(1);
        afterDisable[0].IsEnabled.Should().BeFalse();

        // Act 5: Toggle mod back enabled
        var toggleEnableResult = await manager.ToggleModAsync(instance, afterDisable[0].Id, true);
        toggleEnableResult.Should().BeTrue();

        var afterEnable = await manager.GetInstalledModsAsync(instance);
        afterEnable[0].IsEnabled.Should().BeTrue();

        // Act 6: Uninstall mod
        var uninstallResult = await manager.UninstallModAsync(instance, afterEnable[0].Id);
        uninstallResult.Should().BeTrue();

        var emptyList = await manager.GetInstalledModsAsync(instance);
        emptyList.Should().BeEmpty();
    }

    [Fact]
    public void ModManagerRegistry_SelectsUnrealManager_ForUnrealInstance()
    {
        // Arrange
        var unrealManager = new UnrealModManager(NullLogger<UnrealModManager>.Instance);
        var genericManager = new GenericModManager(NullLogger<GenericModManager>.Instance);
        var registry = new ModManagerRegistry([unrealManager, genericManager]);

        var instance = new GameInstance
        {
            Name = "Unreal Game",
            AppId = 12345,
            InstallPath = @"C:\Games\UnrealGame",
            Engine = new EngineInfo
            {
                Id = "unreal",
                Name = "Unreal Engine",
                Type = EngineType.UnrealEngine,
                Capabilities = EngineCapabilities.Mods
            }
        };

        // Act
        var manager = registry.GetManagerForInstance(instance);

        // Assert
        manager.Should().NotBeNull();
        manager!.Id.Should().Be("unreal-paks");
    }
}
