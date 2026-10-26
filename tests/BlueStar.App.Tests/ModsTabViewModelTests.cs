using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BlueStar.App.ViewModels;
using BlueStar.Core.Interfaces;
using BlueStar.Core.Models;
using BlueStar.Infrastructure.Mods.Installation;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace BlueStar.App.Tests;

public sealed class ModsTabViewModelTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bluestar_modstab_" + Guid.NewGuid().ToString("N"));
    private readonly string _gameDir;
    private readonly ModInstallPipeline _pipeline;
    private readonly Mock<IModManager> _legacy = new();
    private readonly Mock<IModManagerRegistry> _registry = new();
    private IReadOnlyList<ModItem> _legacyMods = [];

    public ModsTabViewModelTests()
    {
        _gameDir = Directory.CreateDirectory(Path.Combine(_root, "game")).FullName;
        var store = new JsonModLedgerStore(NullLogger<JsonModLedgerStore>.Instance, Path.Combine(_root, "data"));
        _pipeline = new ModInstallPipeline(store, new ModPlacementResolver(), NullLogger<ModInstallPipeline>.Instance, Path.Combine(_root, "staging"));

        _legacy.Setup(m => m.GetModsDirectory(It.IsAny<GameInstance>())).Returns(Path.Combine(_gameDir, "mods"));
        _legacy.Setup(m => m.GetInstalledModsAsync(It.IsAny<GameInstance>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => _legacyMods);
        _registry.Setup(r => r.GetManagerForInstance(It.IsAny<GameInstance>())).Returns(_legacy.Object);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private GameInstance Instance() => new()
    {
        Id = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"),
        Name = "Test Game",
        InstallPath = _gameDir
    };

    private ModsTabViewModel CreateTab(string? pickedFile = null)
    {
        var tab = new ModsTabViewModel(_pipeline, _registry.Object, null, NullLogger.Instance)
        {
            PickModFile = () => pickedFile
        };
        tab.SetInstance(Instance());
        return tab;
    }

    private string ModFile(string name, string content = "data")
    {
        var path = Path.Combine(_root, "downloads", name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public async Task InstallFromFile_LowConfidencePlan_OpensConfirmation_ThenInstallsWhenConfirmed()
    {
        // Arrange
        var tab = CreateTab(ModFile("mystery.bin"));

        // Act
        await tab.InstallFromFileCommand.ExecuteAsync(null);

        // Assert: nothing written yet, the plan is shown
        tab.IsConfirmPlanOpen.Should().BeTrue();
        tab.PendingModName.Should().Be("mystery");
        tab.PendingDestinations.Should().ContainSingle().Which.Should().EndWith("mystery.bin");
        Directory.EnumerateFileSystemEntries(_gameDir).Should().BeEmpty();

        // Act
        await tab.ConfirmPendingPlanCommand.ExecuteAsync(null);

        // Assert
        tab.IsConfirmPlanOpen.Should().BeFalse();
        tab.IsStatusError.Should().BeFalse();
        tab.Items.Should().ContainSingle(i => i.IsManaged && i.Name == "mystery");
        File.Exists(Path.Combine(_gameDir, "mods", "mystery", "mystery.bin")).Should().BeTrue();
    }

    [Fact]
    public async Task CancelPendingPlan_LeavesGameUntouched()
    {
        // Arrange
        var tab = CreateTab(ModFile("mystery.bin"));
        await tab.InstallFromFileCommand.ExecuteAsync(null);

        // Act
        tab.CancelPendingPlanCommand.Execute(null);

        // Assert
        tab.IsConfirmPlanOpen.Should().BeFalse();
        tab.PendingDestinations.Should().BeEmpty();
        Directory.EnumerateFileSystemEntries(_gameDir).Should().BeEmpty();
    }

    [Fact]
    public async Task Toggle_And_Uninstall_ManagedMod_GoThroughPipeline()
    {
        // Arrange
        Directory.CreateDirectory(Path.Combine(_gameDir, "Data"));
        File.WriteAllText(Path.Combine(_gameDir, "Data", "Base.esm"), "master");
        var tab = CreateTab(ModFile("Cool.esp", "plugin"));
        await tab.InstallFromFileCommand.ExecuteAsync(null);
        var item = tab.Items.Single();
        var installed = Path.Combine(_gameDir, "Data", "Cool.esp");
        File.Exists(installed).Should().BeTrue();

        // Act: disable
        await tab.ToggleCommand.ExecuteAsync(item);

        // Assert
        File.Exists(installed).Should().BeFalse();
        tab.Items.Single().IsEnabled.Should().BeFalse();

        // Act: uninstall
        await tab.UninstallCommand.ExecuteAsync(tab.Items.Single());

        // Assert
        tab.Items.Should().BeEmpty();
        (await _pipeline.GetLedgerAsync(Instance())).Mods.Should().BeEmpty();
    }

    [Fact]
    public async Task Load_ListsUnmanagedMods_ButHidesFilesTheLedgerAlreadyTracks()
    {
        // Arrange
        Directory.CreateDirectory(Path.Combine(_gameDir, "Data"));
        File.WriteAllText(Path.Combine(_gameDir, "Data", "Base.esm"), "master");
        var tab = CreateTab(ModFile("Cool.esp"));
        await tab.InstallFromFileCommand.ExecuteAsync(null);

        _legacyMods =
        [
            new ModItem { Id = "Cool.esp", Name = "Cool", FilePath = Path.Combine(_gameDir, "Data", "Cool.esp") },
            new ModItem { Id = "Old.esp", Name = "Old", FilePath = Path.Combine(_gameDir, "Data", "Old.esp") }
        ];

        // Act
        await tab.LoadCommand.ExecuteAsync(null);

        // Assert
        tab.Items.Select(i => (i.Name, i.IsManaged)).Should().BeEquivalentTo(new[] { ("Cool", true), ("Old", false) });
    }

    [Fact]
    public async Task Install_ConflictingWithAnotherMod_ShowsErrorStatus()
    {
        // Arrange
        Directory.CreateDirectory(Path.Combine(_gameDir, "Data"));
        File.WriteAllText(Path.Combine(_gameDir, "Data", "Base.esm"), "master");
        var first = Path.Combine(_root, "a", "Shared.esp");
        var second = Path.Combine(_root, "b", "Shared.esp");
        Directory.CreateDirectory(Path.GetDirectoryName(first)!);
        Directory.CreateDirectory(Path.GetDirectoryName(second)!);
        File.WriteAllText(first, "a");
        File.WriteAllText(second, "b");
        var tab = CreateTab();

        // Act
        await tab.InstallPackageAsync(new ModPackage { Path = first, Name = "A", Source = ModSourceRef.Local("a") });
        var result = await tab.InstallPackageAsync(new ModPackage { Path = second, Name = "B", Source = ModSourceRef.Local("b") });

        // Assert
        result!.Status.Should().Be(ModInstallStatus.Conflict);
        tab.IsStatusError.Should().BeTrue();
        tab.StatusMessage.Should().Contain("A");
    }

    [Fact]
    public void SetInstance_SameInstanceNewCopy_KeepsList_OtherInstance_ClearsIt()
    {
        // Arrange
        var tab = CreateTab();
        tab.Items.Add(new ModListItem { Id = "x", Name = "x", SourceLabel = "Local file" });

        // Act
        tab.SetInstance(Instance() with { Name = "Renamed" });

        // Assert
        tab.Items.Should().HaveCount(1);

        // Act
        tab.SetInstance(Instance() with { Id = Guid.NewGuid() });

        // Assert
        tab.Items.Should().BeEmpty();
    }
}
