using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using BlueStar.App.ViewModels;
using BlueStar.Core.Helpers;
using BlueStar.Core.Interfaces;
using BlueStar.Core.Models;
using BlueStar.Infrastructure.Downloader;
using BlueStar.Infrastructure.Storage;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace BlueStar.App.Tests;

public class InstallModalArchitecturePreferenceTests
{
    private static InstanceDetailViewModel CreateViewModel(GameInstance instance, Mock<IDlcInstaller>? mockDlcInstaller = null)
    {
        var mockInstanceManager = new Mock<IInstanceManager>();
        mockInstanceManager.Setup(m => m.UpdateAsync(It.IsAny<GameInstance>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((GameInstance gi, CancellationToken _) => gi);

        var mockDownloadProvider = new Mock<IDownloadProvider>();
        var mockNotifications = new Mock<INotificationService>();
        var mockGameLauncher = new Mock<IGameLauncher>();
        var mockEngineDetector = new Mock<IEngineDetector>();

        var downloadQueueManager = new DownloadQueueManager(
            mockDownloadProvider.Object,
            NullLogger<DownloadQueueManager>.Instance,
            mockInstanceManager.Object,
            mockNotifications.Object);

        var tempSettingsPath = Path.Combine(Path.GetTempPath(), $"bluestar_test_settings_{Guid.NewGuid():N}.json");
        var appSettings = new AppSettingsService(NullLogger<AppSettingsService>.Instance, tempSettingsPath);
        var dlcInstaller = mockDlcInstaller?.Object ?? new Mock<IDlcInstaller>().Object;

        var vm = new InstanceDetailViewModel(
            mockInstanceManager.Object,
            dlcInstaller,
            downloadQueueManager,
            appSettings,
            mockEngineDetector.Object,
            new Mock<IModManagerRegistry>().Object,
            new Mock<IBepInExService>().Object,
            new Mock<IWorkshopService>().Object,
            new Mock<IEmulatorRegistry>().Object,
            new Mock<IEmulatorRatingService>().Object,
            mockGameLauncher.Object,
            NullLogger<InstanceDetailViewModel>.Instance,
            notificationService: mockNotifications.Object);

        vm.Instance = instance;
        return vm;
    }

    [Fact]
    public void SelectableDepotItem_And_SelectableDlcItem_ExposeArchitectureTag()
    {
        var depot64 = new DepotInfo { DepotId = 100, Architecture = "64-bit", SizeBytes = 1000 };
        var depot32 = new DepotInfo { DepotId = 101, Architecture = "32-bit", SizeBytes = 1000 };
        var depotBoth = new DepotInfo { DepotId = 102, Name = "Shared Audio", SizeBytes = 1000 };

        var dlc = new DlcInfo
        {
            AppId = 200,
            Name = "DLC Pack",
            Depots = [depot64]
        };

        var depotItem64 = new SelectableDepotItem { Depot = depot64 };
        var depotItem32 = new SelectableDepotItem { Depot = depot32 };
        var depotItemBoth = new SelectableDepotItem { Depot = depotBoth };
        var dlcItem = new SelectableDlcItem { Dlc = dlc };

        depotItem64.ArchitectureTag.Should().Be("x64");
        depotItem32.ArchitectureTag.Should().Be("x32");
        depotItemBoth.ArchitectureTag.Should().Be("x32/x64");
        dlcItem.ArchitectureTag.Should().Be("x64");
    }

    [Fact]
    public async Task OpenInstallModal_FiltersIncompatibleDepotsAndDlcs_Automatically()
    {
        var instance = new GameInstance
        {
            Id = Guid.NewGuid(),
            AppId = 12345,
            Name = "Test Dual Arch Game",
            InstallPath = Path.Combine(Path.GetTempPath(), "TestDualArchGame"),
            Depots = [
                new DepotInfo { DepotId = 1, Architecture = "x64", SizeBytes = 2000, Platform = "Windows" },
                new DepotInfo { DepotId = 2, Architecture = "x32", SizeBytes = 1500, Platform = "Windows" },
                new DepotInfo { DepotId = 3, Name = "Common Assets", SizeBytes = 5000, Platform = "Windows" }
            ],
            Dlcs = [
                new DlcInfo
                {
                    AppId = 10,
                    Name = "64-bit DLC",
                    Depots = [new DepotInfo { DepotId = 101, Architecture = "x64", SizeBytes = 1000 }]
                },
                new DlcInfo
                {
                    AppId = 20,
                    Name = "32-bit DLC",
                    Depots = [new DepotInfo { DepotId = 201, Architecture = "x32", SizeBytes = 800 }]
                },
                new DlcInfo
                {
                    AppId = 30,
                    Name = "Universal DLC",
                    Depots = [new DepotInfo { DepotId = 301, Name = "Skins", SizeBytes = 500 }]
                }
            ]
        };

        var vm = CreateViewModel(instance);
        await vm.OpenInstallModalAsync();

        vm.IsInstallModalOpen.Should().BeTrue();

        if (Environment.Is64BitOperatingSystem)
        {
            // Automatic arch preference is x64 because game has x64 depots on 64-bit OS
            vm.InstallModalArchPreference.Should().Be("x64");
            vm.IsInstallModalArchX64.Should().BeTrue();

            // Incompatible 32-bit DLC is NOT shown in the installer modal at all!
            vm.InstallModalDlcs.Should().HaveCount(2);
            vm.InstallModalDlcs.Should().NotContain(d => d.Dlc.AppId == 20);
            vm.InstallModalDlcs.Should().Contain(d => d.Dlc.AppId == 10 && d.IsSelected && d.ArchitectureTag == "x64");
            vm.InstallModalDlcs.Should().Contain(d => d.Dlc.AppId == 30 && d.IsSelected && d.ArchitectureTag == "x32/x64");

            // Incompatible 32-bit depot is NOT shown in the installer modal at all!
            vm.InstallModalDepots.Should().HaveCount(2);
            vm.InstallModalDepots.Should().NotContain(d => d.Depot.DepotId == 2);
            vm.InstallModalDepots.Should().Contain(d => d.Depot.DepotId == 1 && d.ArchitectureTag == "x64");
            vm.InstallModalDepots.Should().Contain(d => d.Depot.DepotId == 3 && d.ArchitectureTag == "x32/x64");
        }

        // If switched to x32 preference:
        vm.IsInstallModalArchX32 = true;
        vm.InstallModalArchPreference.Should().Be("x32");

        // Now only 32-bit and Universal are visible; 64-bit DLC and depot are excluded!
        vm.InstallModalDlcs.Should().HaveCount(2);
        vm.InstallModalDlcs.Should().NotContain(d => d.Dlc.AppId == 10);
        vm.InstallModalDlcs.Should().Contain(d => d.Dlc.AppId == 20 && d.IsSelected && d.ArchitectureTag == "x32");
        vm.InstallModalDlcs.Should().Contain(d => d.Dlc.AppId == 30 && d.IsSelected && d.ArchitectureTag == "x32/x64");

        vm.InstallModalDepots.Should().HaveCount(2);
        vm.InstallModalDepots.Should().NotContain(d => d.Depot.DepotId == 1);
        vm.InstallModalDepots.Should().Contain(d => d.Depot.DepotId == 2 && d.ArchitectureTag == "x32");
        vm.InstallModalDepots.Should().Contain(d => d.Depot.DepotId == 3 && d.ArchitectureTag == "x32/x64");

        // If switched to x32/x64 preference:
        vm.IsInstallModalArchBoth = true;
        vm.InstallModalArchPreference.Should().Be("x32/x64");

        // On 64-bit OS with "x32/x64" preference, all depots and DLCs compatible with 64-bit OS are shown
        if (Environment.Is64BitOperatingSystem)
        {
            vm.InstallModalDlcs.Should().HaveCount(3);
            vm.InstallModalDepots.Should().HaveCount(3);
        }
    }

    [Fact]
    public async Task OpenInstallModal_AdaptsToPure32BitGame_On64BitOS()
    {
        var instance = new GameInstance
        {
            Id = Guid.NewGuid(),
            AppId = 54321,
            Name = "Retro 32-bit Game",
            InstallPath = Path.Combine(Path.GetTempPath(), "RetroGame"),
            Depots = [
                new DepotInfo { DepotId = 1, Architecture = "x32", SizeBytes = 3000, Platform = "Windows" },
                new DepotInfo { DepotId = 2, Name = "Data Content", SizeBytes = 4000, Platform = "Windows" }
            ],
            Dlcs = [
                new DlcInfo
                {
                    AppId = 99,
                    Name = "Classic Expansion",
                    Depots = [new DepotInfo { DepotId = 991, Architecture = "x32", SizeBytes = 500 }]
                }
            ]
        };

        var vm = CreateViewModel(instance);
        await vm.OpenInstallModalAsync();

        // Since no x64 depots exist, it automatically defaults to x32
        vm.InstallModalArchPreference.Should().Be("x32");
        vm.IsInstallModalArchX32.Should().BeTrue();
        vm.InstallModalDlcs.Should().HaveCount(1);
        vm.InstallModalDlcs[0].IsSelected.Should().BeTrue();
        vm.InstallModalDepots.Should().HaveCount(2);
    }

    [Fact]
    public async Task OpenInstallModal_AdaptsToUniversalOnlyGame()
    {
        var instance = new GameInstance
        {
            Id = Guid.NewGuid(),
            AppId = 67890,
            Name = "Universal Assets Only Game",
            InstallPath = Path.Combine(Path.GetTempPath(), "UniversalGame"),
            Depots = [
                new DepotInfo { DepotId = 1, Name = "Main Content", SizeBytes = 3000, Platform = "Windows" },
                new DepotInfo { DepotId = 2, Name = "Soundtrack", SizeBytes = 2000, Platform = "Windows" }
            ],
            Dlcs = [
                new DlcInfo
                {
                    AppId = 99,
                    Name = "Bonus Pack",
                    Depots = [new DepotInfo { DepotId = 991, Name = "Artbook", SizeBytes = 500 }]
                }
            ]
        };

        var vm = CreateViewModel(instance);
        await vm.OpenInstallModalAsync();

        // Since only universal depots exist, it defaults to x32/x64
        vm.InstallModalArchPreference.Should().Be("x32/x64");
        vm.IsInstallModalArchBoth.Should().BeTrue();
        vm.InstallModalDlcs.Should().HaveCount(1);
        vm.InstallModalDlcs[0].IsSelected.Should().BeTrue();
        vm.InstallModalDepots.Should().HaveCount(2);
    }
}
