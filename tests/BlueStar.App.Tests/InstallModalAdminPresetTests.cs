using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using BlueStar.App.ViewModels;
using BlueStar.Core.Interfaces;
using BlueStar.Core.Models;
using BlueStar.Infrastructure.Downloader;
using BlueStar.Infrastructure.Storage;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace BlueStar.App.Tests;

/// <summary>The admin's presets show up in the install modal as the recommended version, with their note.</summary>
public class InstallModalAdminPresetTests
{
    private static readonly AdminInstallPreset ReFixPreset = new()
    {
        Id = "cs-go-con-refix",
        Name = "CS:GO con ReFix",
        Manifests = new Dictionary<uint, ulong> { [4465481] = 1234567890123456789 },
        Emulator = new AdminEmulator { Type = AdminEmulatorType.ReFixOnlineLegacy },
        Note = "Versión recomendada para jugar en LAN con ReFix."
    };

    private static readonly AdminInstallPreset CustomPreset = new()
    {
        Id = "custom",
        Name = "Con emulador propio",
        Emulator = new AdminEmulator
        {
            Type = AdminEmulatorType.Custom,
            Name = "MyEmu",
            Download = new AdminDownload { Name = "MyEmu", Url = new Uri("https://github.com/Coronitaa/x/e.zip") }
        },
        Mods = [new AdminDownload { Name = "Mapas", Url = new Uri("https://github.com/Coronitaa/x/m.zip") }]
    };

    private static InstanceDetailViewModel CreateViewModel(IReadOnlyList<AdminInstallPreset> presets)
    {
        var instances = new Mock<IInstanceManager>();
        instances.Setup(m => m.UpdateAsync(It.IsAny<GameInstance>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((GameInstance gi, CancellationToken _) => gi);
        var queue = new DownloadQueueManager(new Mock<IDownloadProvider>().Object, NullLogger<DownloadQueueManager>.Instance, instances.Object);
        var settings = new AppSettingsService(NullLogger<AppSettingsService>.Instance,
            Path.Combine(Path.GetTempPath(), $"bluestar_test_settings_{Guid.NewGuid():N}.json"));

        var catalog = new Mock<IAdminCatalogService>();
        catalog.Setup(c => c.GetPresetsAsync(4465480, It.IsAny<CancellationToken>())).ReturnsAsync(presets);

        var vm = new InstanceDetailViewModel(
            instances.Object,
            new Mock<IDlcInstaller>().Object,
            queue,
            settings,
            new Mock<IEngineDetector>().Object,
            new Mock<IModManagerRegistry>().Object,
            new Mock<IBepInExService>().Object,
            new Mock<IWorkshopService>().Object,
            new Mock<IEmulatorRegistry>().Object,
            new Mock<IEmulatorRatingService>().Object,
            new Mock<IGameLauncher>().Object,
            NullLogger<InstanceDetailViewModel>.Instance,
            adminCatalog: catalog.Object);

        vm.Instance = new GameInstance
        {
            Id = Guid.NewGuid(),
            AppId = 4465480,
            Name = "Counter-Strike: Global Offensive",
            InstallPath = Path.Combine(Path.GetTempPath(), "csgo-preset-test"),
            Depots = [new DepotInfo { DepotId = 4465481, SizeBytes = 1000, Platform = "Windows" }]
        };
        return vm;
    }

    [Fact]
    public async Task OpenInstallModal_WithAdminPreset_OffersItAsRecommendedWithItsNote()
    {
        var vm = CreateViewModel([ReFixPreset]);

        await vm.OpenInstallModalAsync();

        vm.InstallModalHasRecommendedVersion.Should().BeTrue();
        vm.IsInstallModalVersionRecommended.Should().BeTrue("a recommended version is pre-selected");
        vm.CuratedSummaryText.Should().Be("CS:GO con ReFix");
        vm.CuratedNotesText.Should().Be("Versión recomendada para jugar en LAN con ReFix.");
        vm.HasCuratedNotes.Should().BeTrue();
        vm.HasMultipleInstallModalPresets.Should().BeFalse("one preset needs no picker");
        vm.IsInstallModalEmulatorValve.Should().BeTrue("the preset asks for ReFix");
        vm.HasInstallModalPresetExtras.Should().BeFalse();
    }

    [Fact]
    public async Task PickingAnotherPreset_SwitchesRecommendationEmulatorAndExtras()
    {
        var vm = CreateViewModel([ReFixPreset, CustomPreset]);
        await vm.OpenInstallModalAsync();

        vm.HasMultipleInstallModalPresets.Should().BeTrue();
        vm.SelectedInstallModalPreset.Should().Be(ReFixPreset);

        vm.SelectedInstallModalPreset = CustomPreset;

        vm.CuratedRecommendation!.PresetId.Should().Be("custom");
        vm.CuratedRecommendation.HasPresetExtras.Should().BeTrue();
        vm.IsInstallModalEmulatorNone.Should().BeTrue("a custom emulator replaces BlueStar's own");
        vm.InstallModalPresetExtrasText.Should().Contain("MyEmu").And.Contain("Mapas");
    }

    [Theory]
    [InlineData(AdminEmulatorType.ReFixLan, "refix_goldberg_offline")]
    [InlineData(AdminEmulatorType.ReFixLanOnline, "refix_goldberg")]
    [InlineData(AdminEmulatorType.ReFixOnlineLegacy, "refix_valve")]
    [InlineData(AdminEmulatorType.None, "none")]
    public async Task EachReFixOption_SelectsTheMatchingModalEmulator(AdminEmulatorType type, string expected)
    {
        var vm = CreateViewModel([ReFixPreset with { Emulator = new AdminEmulator { Type = type } }]);

        await vm.OpenInstallModalAsync();

        vm.InstallModalSelectedEmulator.Should().Be(expected);
    }

    [Fact]
    public async Task OpenInstallModal_WithoutPresets_KeepsLatestAsDefault()
    {
        var vm = CreateViewModel([]);

        await vm.OpenInstallModalAsync();

        vm.InstallModalHasRecommendedVersion.Should().BeFalse();
        vm.IsInstallModalVersionLatest.Should().BeTrue();
    }
}
