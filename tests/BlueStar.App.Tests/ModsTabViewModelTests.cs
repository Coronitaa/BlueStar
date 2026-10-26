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
    public async Task InstallFromFileCommand_InvokedLikeAButton_AsksForAFile()
    {
        // Arrange: WPF buttons go through ICommand.CanExecute/Execute, not ExecuteAsync
        var asked = new TaskCompletionSource<bool>();
        var tab = CreateTab();
        tab.PickModFile = () => { asked.TrySetResult(true); return null; };
        System.Windows.Input.ICommand command = tab.InstallFromFileCommand;

        // Act
        var canExecute = command.CanExecute(null);
        command.Execute(null);

        // Assert
        canExecute.Should().BeTrue();
        (await Task.WhenAny(asked.Task, Task.Delay(2000))).Should().Be(asked.Task);
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

    [Fact]
    public void ModrinthSearchAndFilter_UpdatesFilteredItems()
    {
        // Arrange
        var tab = CreateTab();
        tab.Items.Add(new ModListItem { Id = "1", Name = "Fabric API", Author = "modmuss50", SourceLabel = "Steam Workshop", IsEnabled = true, SizeBytes = 2048 });
        tab.Items.Add(new ModListItem { Id = "2", Name = "Sodium", Author = "CaffeineMC", SourceLabel = "Steam Workshop", IsEnabled = false, SizeBytes = 4096 });
        tab.Items.Add(new ModListItem { Id = "3", Name = "Local Texture Pack", Author = "Custom", SourceLabel = "Local file", IsEnabled = true, SizeBytes = 8192 });

        tab.RefreshFilteredItems();

        // Assert: initial state
        tab.TotalModsCount.Should().Be(3);
        tab.ActiveModsCount.Should().Be(2);
        tab.DisabledModsCount.Should().Be(1);
        tab.WorkshopModsCount.Should().Be(2);
        tab.LocalModsCount.Should().Be(1);
        tab.FilteredItems.Should().HaveCount(3);

        // Act: search for "Sodium"
        tab.SearchText = "Sodium";
        tab.FilteredItems.Should().ContainSingle(i => i.Name == "Sodium");

        // Act: search by author "modmuss"
        tab.SearchText = "modmuss";
        tab.FilteredItems.Should().ContainSingle(i => i.Name == "Fabric API");

        // Act: filter by Enabled
        tab.SearchText = string.Empty;
        tab.SetFilter("Enabled");
        tab.FilteredItems.Should().HaveCount(2).And.OnlyContain(i => i.IsEnabled);

        // Act: filter by Disabled
        tab.SetFilter("Disabled");
        tab.FilteredItems.Should().ContainSingle(i => i.Name == "Sodium");

        // Act: filter by Local
        tab.SetFilter("Local");
        tab.FilteredItems.Should().ContainSingle(i => i.Name == "Local Texture Pack");

        // Act: add Thunderstore mod
        tab.Items.Add(new ModListItem
        {
            Id = "ts_morecompany",
            Name = "MoreCompany",
            SourceLabel = "Thunderstore",
            IsEnabled = true,
            IsManaged = true
        });
        tab.ThunderstoreModsCount.Should().Be(1);
        tab.CanShowThunderstoreFilter.Should().BeTrue();
        tab.Items.Last().IconGeometryKey.Should().Be("IconBolt");

        // Act: filter by Thunderstore
        tab.SetFilter("Thunderstore");
        tab.FilteredItems.Should().ContainSingle(i => i.Name == "MoreCompany");
    }

    [Fact]
    public async Task CheckProvidersAsync_ChecksAvailability_BeforeShowingProviders()
    {
        // Arrange
        var mockWorkshop = new Mock<IWorkshopService>();
        mockWorkshop.Setup(w => w.HasWorkshopSupportAsync(12345, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        mockWorkshop.Setup(w => w.HasWorkshopSupportAsync(99999, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var mockThunderstore = new Mock<IThunderstoreService>();
        var instSupported = new GameInstance { Id = Guid.NewGuid(), AppId = 12345, Name = "Lethal Company", InstallPath = _gameDir };
        var instUnsupported = new GameInstance { Id = Guid.NewGuid(), AppId = 99999, Name = "Unknown Game", InstallPath = _gameDir };

        mockThunderstore.Setup(t => t.ResolveCommunityAsync(instSupported, It.IsAny<CancellationToken>()))
            .ReturnsAsync("lethal-company");
        mockThunderstore.Setup(t => t.ResolveCommunityAsync(instUnsupported, It.IsAny<CancellationToken>()))
            .ReturnsAsync((string?)null);

        var tab = new ModsTabViewModel(
            _pipeline,
            _registry.Object,
            null,
            NullLogger.Instance,
            null,
            mockWorkshop.Object,
            null,
            mockThunderstore.Object);

        // Act 1: Game with both Workshop and Thunderstore
        await tab.CheckProvidersAsync(instSupported);
        tab.HasWorkshopSupport.Should().BeTrue();
        tab.HasThunderstoreSupport.Should().BeTrue();
        tab.ThunderstoreCommunity.Should().Be("lethal-company");
        tab.HasAnyProvider.Should().BeTrue();
        tab.CanShowWorkshopFilter.Should().BeTrue();
        tab.CanShowThunderstoreFilter.Should().BeTrue();

        // Act 2: Game with neither Workshop nor Thunderstore
        await tab.CheckProvidersAsync(instUnsupported);
        tab.HasWorkshopSupport.Should().BeFalse();
        tab.HasThunderstoreSupport.Should().BeFalse();
        tab.ThunderstoreCommunity.Should().BeNull();
        tab.HasAnyProvider.Should().BeFalse();
        tab.CanShowWorkshopFilter.Should().BeFalse();
        tab.CanShowThunderstoreFilter.Should().BeFalse();
    }

    [Fact]
    public async Task UnifiedBepInExManagement_InstallSwitchUninstall_UpdatesStateAndReloads()
    {
        // Arrange
        var mockBepInEx = new Mock<IBepInExService>();
        var releases = new List<BepInExRelease>
        {
            new("v5.4.23.2", "5.4.23.2", "url2", "x64", 1000, DateTimeOffset.UtcNow),
            new("v5.4.21", "5.4.21", "url1", "x64", 1000, DateTimeOffset.UtcNow)
        };
        mockBepInEx.Setup(b => b.GetAvailableReleasesAsync(false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(releases);
        mockBepInEx.Setup(b => b.IsInstalled(It.IsAny<string>()))
            .Returns(false);
        mockBepInEx.Setup(b => b.InstallAsync(It.IsAny<string>(), It.IsAny<BepInExRelease>(), null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        mockBepInEx.Setup(b => b.UninstallAsync(It.IsAny<string>(), true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var unityInstance = new GameInstance
        {
            Id = Guid.NewGuid(),
            Name = "Unity Game",
            InstallPath = _gameDir,
            Engine = new EngineInfo
            {
                Id = "unity",
                Name = "Unity",
                Type = EngineType.Unity,
                Capabilities = EngineCapabilities.BepInExSupported
            }
        };

        var tab = new ModsTabViewModel(
            _pipeline,
            _registry.Object,
            null,
            NullLogger.Instance,
            null,
            null,
            mockBepInEx.Object);

        tab.SetInstance(unityInstance);

        // Act 1: Initial state (Not installed)
        await tab.CheckAndLoadEngineAsync();
        tab.IsEngineSupported.Should().BeTrue();
        tab.IsEngineInstalled.Should().BeFalse();
        tab.AvailableEngineReleases.Should().HaveCount(2);
        tab.SelectedEngineRelease = releases[0];
        tab.EngineActionButtonText.Should().Contain("5.4.23.2");

        // Act 2: Install BepInEx
        mockBepInEx.Setup(b => b.IsInstalled(It.IsAny<string>())).Returns(true);
        mockBepInEx.Setup(b => b.GetInstalledVersionAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("5.4.23.2");

        await tab.InstallOrUpdateEngineAsync();
        tab.IsEngineInstalled.Should().BeTrue();
        tab.EngineVersion.Should().Be("5.4.23.2");
        tab.IsVersionDifferent.Should().BeFalse();
        tab.EngineActionButtonText.Should().Be("Reinstall");

        // Act 3: Select older version (5.4.21) -> switch button
        tab.SelectedEngineRelease = releases[1];
        tab.IsVersionDifferent.Should().BeTrue();
        tab.EngineActionButtonText.Should().Contain("Switch to v5.4.21");

        // Act 4: Uninstall BepInEx
        mockBepInEx.Setup(b => b.IsInstalled(It.IsAny<string>())).Returns(false);
        mockBepInEx.Setup(b => b.GetInstalledVersionAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string?)null);

        await tab.UninstallEngineAsync();
        tab.IsEngineInstalled.Should().BeFalse();
        tab.EngineVersion.Should().BeNull();
    }

    [Fact]
    public async Task EngineStatus_AndInstallAction_WorkSeamlessly()
    {
        // Arrange
        var tab = CreateTab();
        var installed = false;
        tab.InstallEngineAction = () => { installed = true; return Task.CompletedTask; };

        // Act
        tab.UpdateEngineStatus("BepInEx", "5.4.21", isInstalled: true, isSupported: true);

        // Assert
        tab.EngineName.Should().Be("BepInEx");
        tab.EngineVersion.Should().Be("5.4.21");
        tab.IsEngineInstalled.Should().BeTrue();
        tab.IsEngineSupported.Should().BeTrue();

        // Act: invoke install command
        await tab.InstallEngineCommand.ExecuteAsync(null);
        installed.Should().BeTrue();
    }
    [Theory]
    [InlineData("vv5.4.23.6+4865251c89f", "5.4.23.6")]
    [InlineData("v5.4.23.2", "5.4.23.2")]
    [InlineData("5.4.23.2 (x64)", "5.4.23.2")]
    [InlineData("v5.4.21 (x86)", "5.4.21")]
    [InlineData("5.4.22", "5.4.22")]
    [InlineData(null, null)]
    [InlineData("", null)]
    public void CleanVersion_NormalizesVersionStringsCorrectly(string? input, string? expected)
    {
        ModsTabViewModel.CleanVersion(input).Should().Be(expected);
    }

    [Fact]
    public void IsVersionDifferent_ReturnsFalse_WhenSameVersionDespiteHashOrArch()
    {
        var tab = CreateTab();
        tab.IsEngineInstalled = true;
        tab.EngineVersion = "vv5.4.23.6+4865251c89f";
        tab.SelectedEngineRelease = new BepInExRelease("v5.4.23.6", "5.4.23.6 (x64)", "url", "x64", 100, DateTimeOffset.UtcNow);

        tab.FormattedEngineVersion.Should().Be("5.4.23.6");
        tab.IsVersionDifferent.Should().BeFalse();
        tab.EngineActionButtonText.Should().Be("Reinstall");
    }

    [Fact]
    public void ClearStatusCommand_ResetsStatus()
    {
        var tab = CreateTab();
        tab.StatusMessage = "Some message";
        tab.IsStatusError = true;
        tab.StatusMessage.Should().Be("Some message");

        tab.ClearStatusCommand.Execute(null);
        tab.StatusMessage.Should().BeNull();
    }
}

