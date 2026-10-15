using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using BlueStar.App.ViewModels;
using BlueStar.Core.Interfaces;
using BlueStar.Core.Models;
using BlueStar.Infrastructure.Downloader;
using BlueStar.Infrastructure.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

using System.IO;
using FluentAssertions;

namespace BlueStar.App.Tests;

public class InstanceDetailCustomizationTests
{
    private static InstanceDetailViewModel CreateViewModel(
        GameInstance instance,
        Mock<IDlcInstaller>? mockDlcInstaller = null,
        AppSettingsService? appSettings = null,
        Mock<INotificationService>? mockNotifications = null,
        Mock<IInstanceManager>? mockInstanceManager = null,
        Mock<IManifestHubProvider>? mockManifestHub = null,
        Mock<IVoluntaryContributionService>? mockContributionService = null,
        Mock<IContributionTransportService>? mockContributionTransport = null,
        Mock<IStardustService>? mockStardust = null,
        Mock<IBackgroundTaskService>? mockBackgroundTask = null,
        Mock<IManifestCacheService>? mockManifestCache = null)
    {
        var instanceManager = mockInstanceManager ?? new Mock<IInstanceManager>();
        instanceManager.Setup(m => m.UpdateAsync(It.IsAny<GameInstance>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((GameInstance gi, CancellationToken _) => gi);

        var mockDownloadProvider = new Mock<IDownloadProvider>();
        var notifications = mockNotifications ?? new Mock<INotificationService>();
        var mockGameLauncher = new Mock<IGameLauncher>();
        var mockEngineDetector = new Mock<IEngineDetector>();

        var downloadQueueManager = new DownloadQueueManager(
            mockDownloadProvider.Object,
            NullLogger<DownloadQueueManager>.Instance,
            instanceManager.Object,
            notifications.Object);

        var tempSettingsPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"bluestar_test_settings_{Guid.NewGuid():N}.json");
        var settings = appSettings ?? new AppSettingsService(NullLogger<AppSettingsService>.Instance, tempSettingsPath);
        var dlcInstaller = mockDlcInstaller?.Object ?? new Mock<IDlcInstaller>().Object;

        var vm = new InstanceDetailViewModel(
            instanceManager.Object,
            dlcInstaller,
            downloadQueueManager,
            settings,
            mockEngineDetector.Object,
            new Mock<IModManagerRegistry>().Object,
            new Mock<IBepInExService>().Object,
            new Mock<IWorkshopService>().Object,
            new Mock<IEmulatorRegistry>().Object,
            new Mock<IEmulatorRatingService>().Object,
            mockGameLauncher.Object,
            NullLogger<InstanceDetailViewModel>.Instance,
            notificationService: notifications.Object,
            backgroundTaskService: mockBackgroundTask?.Object,
            manifestCacheService: mockManifestCache?.Object,
            manifestHubProvider: mockManifestHub?.Object,
            voluntaryContributionService: mockContributionService?.Object,
            contributionTransportService: mockContributionTransport?.Object,
            stardustService: mockStardust?.Object);

        vm.Instance = instance;
        return vm;
    }

    [Fact]
    public void ImportedFolderWithoutSteamAssociation_HidesVersionsAndPrerequisitesTabs()
    {
        var unassociatedInstance = new GameInstance
        {
            Id = Guid.NewGuid(),
            Name = "Standalone Folder Game",
            AppId = 0,
            InstallPath = @"C:\Games\StandaloneGame",
            Origin = InstanceOrigin.ImportedFolder,
            Status = InstanceStatus.Ready
        };

        var vm = CreateViewModel(unassociatedInstance);

        Assert.True(vm.IsFolderWithoutSteamAssociation);
        Assert.False(vm.IsVersionsTabVisible);
        Assert.False(vm.IsDepotBoxTabsVisible);
        Assert.False(vm.IsPrerequisitesTabVisible);

        // Cannot switch to hidden tabs
        vm.SwitchTab("Files");
        Assert.NotEqual("Files", vm.SelectedTab);

        vm.SwitchTab("Prerequisites");
        Assert.NotEqual("Prerequisites", vm.SelectedTab);
    }

    [Fact]
    public void ImportedFolderWithSpacewarAppId_HidesVersionsAndPrerequisitesTabs()
    {
        var spacewarInstance = new GameInstance
        {
            Id = Guid.NewGuid(),
            Name = "Folder Game With Spacewar",
            AppId = 480,
            InstallPath = @"C:\Games\SpacewarGame",
            Origin = InstanceOrigin.ImportedFolder,
            Status = InstanceStatus.Ready
        };

        var vm = CreateViewModel(spacewarInstance);

        Assert.True(vm.IsFolderWithoutSteamAssociation);
        Assert.False(vm.IsVersionsTabVisible);
        Assert.False(vm.IsPrerequisitesTabVisible);
    }

    [Fact]
    public void ImportedFolderWithSteamAssociation_ShowsVersionsAndPrerequisitesTabs()
    {
        var steamAssociatedFolderInstance = new GameInstance
        {
            Id = Guid.NewGuid(),
            Name = "Cyberpunk 2077",
            AppId = 1091500,
            InstallPath = @"C:\Games\Cyberpunk2077",
            Origin = InstanceOrigin.ImportedFolder,
            Status = InstanceStatus.Ready
        };

        var vm = CreateViewModel(steamAssociatedFolderInstance);

        Assert.False(vm.IsFolderWithoutSteamAssociation);
        Assert.True(vm.IsVersionsTabVisible);
        Assert.True(vm.IsPrerequisitesTabVisible);
    }

    [Fact]
    public void DepotBoxInstance_ShowsVersionsAndPrerequisitesTabs()
    {
        var depotInstance = new GameInstance
        {
            Id = Guid.NewGuid(),
            Name = "Hollow Knight",
            AppId = 367520,
            InstallPath = @"C:\Games\HollowKnight",
            Origin = InstanceOrigin.DepotBox,
            Status = InstanceStatus.Ready
        };

        var vm = CreateViewModel(depotInstance);

        Assert.False(vm.IsFolderWithoutSteamAssociation);
        Assert.True(vm.IsVersionsTabVisible);
        Assert.True(vm.IsPrerequisitesTabVisible);
    }

    [Fact]
    public async Task ForceInstallAllDlcs_UnlocksInstallDlcsButton_AndForcesInstallationToAllDlcs()
    {
        var dlc1 = new DlcInfo { AppId = 1001, Name = "DLC 1", Depots = [], IsInstalled = false };
        var dlc2 = new DlcInfo { AppId = 1002, Name = "DLC 2", Depots = [], IsInstalled = false };

        var instance = new GameInstance
        {
            Id = Guid.NewGuid(),
            Name = "Test Game",
            AppId = 1000,
            InstallPath = @"C:\Games\TestGame",
            Origin = InstanceOrigin.DepotBox,
            Dlcs = [dlc1, dlc2],
            Status = InstanceStatus.Ready,
            ForceInstallAllDlcs = false
        };

        var mockDlcInstaller = new Mock<IDlcInstaller>();
        mockDlcInstaller.Setup(d => d.InstallDlcAsync(
                It.IsAny<GameInstance>(),
                It.IsAny<DlcInfo>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<IProgress<string>>()))
            .ReturnsAsync(true);

        var vm = CreateViewModel(instance, mockDlcInstaller);
        vm.Dlcs = new ObservableCollection<SelectableDlcItem>
        {
            new SelectableDlcItem { Dlc = dlc1, IsSelected = false },
            new SelectableDlcItem { Dlc = dlc2, IsSelected = false }
        };

        // Initially no DLC selected and ForceInstallAllDlcs is false: button is locked (disabled)
        Assert.False(vm.CanDownloadAndInstallDlcs);
        Assert.False(vm.DownloadAndInstallDlcsCommand.CanExecute(null));

        // When user confirms ForceInstallAllDlcs (or ticks it):
        vm.DoNotShowForceAllDlcsWarning = true;
        await vm.ConfirmForceAllDlcsWarningCommand.ExecuteAsync(null);

        // Tick is active and button is unlocked!
        Assert.True(vm.ForceInstallAllDlcs);
        Assert.True(vm.CanDownloadAndInstallDlcs);
        Assert.True(vm.DownloadAndInstallDlcsCommand.CanExecute(null));

        // When command is executed, it forces installation for all DLCs
        await vm.DownloadAndInstallDlcsCommand.ExecuteAsync(null);

        // Both DLCs in the collection are now marked as unlocked and selected
        Assert.All(vm.Dlcs, item => Assert.True(item.IsSelected));
        Assert.All(vm.Dlcs, item => Assert.True(item.IsUnlocked));
    }

    [Fact]
    public void ImportedFolderWithoutSteamAssociation_HidesDetectedVersion()
    {
        var folderUnassociated = new GameInstance
        {
            Id = Guid.NewGuid(),
            Name = "Folder Game",
            AppId = 0,
            InstallPath = @"C:\Games\FolderGame",
            Origin = InstanceOrigin.ImportedFolder,
            Status = InstanceStatus.Ready
        };

        var vm = CreateViewModel(folderUnassociated);
        Assert.True(vm.IsFolderWithoutSteamAssociation);
        Assert.False(vm.IsDetectedVersionVisible);

        var spacewarInstance = folderUnassociated with { AppId = 480 };
        var vmSpacewar = CreateViewModel(spacewarInstance);
        Assert.True(vmSpacewar.IsFolderWithoutSteamAssociation);
        Assert.False(vmSpacewar.IsDetectedVersionVisible);
    }

    [Fact]
    public void SteamOrAssociatedInstances_ShowDetectedVersion()
    {
        var steamInstance = new GameInstance
        {
            Id = Guid.NewGuid(),
            Name = "Steam Game",
            AppId = 730,
            InstallPath = @"C:\Games\CS2",
            Origin = InstanceOrigin.Steam,
            Status = InstanceStatus.Ready
        };

        var vmSteam = CreateViewModel(steamInstance);
        Assert.False(vmSteam.IsFolderWithoutSteamAssociation);
        Assert.True(vmSteam.IsDetectedVersionVisible);

        var associatedFolderInstance = new GameInstance
        {
            Id = Guid.NewGuid(),
            Name = "Associated Folder Game",
            AppId = 1245620,
            InstallPath = @"C:\Games\EldenRing",
            Origin = InstanceOrigin.ImportedFolder,
            Status = InstanceStatus.Ready
        };

        var vmAssociated = CreateViewModel(associatedFolderInstance);
        Assert.False(vmAssociated.IsFolderWithoutSteamAssociation);
        Assert.True(vmAssociated.IsDetectedVersionVisible);
    }

    [Fact]
    public async Task ForceInstallAllDlcs_OpensWarningModal_AndOnlyAppliesWhenConfirmed()
    {
        var instance = new GameInstance
        {
            Id = Guid.NewGuid(),
            Name = "Test Game",
            AppId = 1000,
            InstallPath = @"C:\Games\TestGame",
            Origin = InstanceOrigin.DepotBox,
            Dlcs = [],
            Status = InstanceStatus.Ready,
            ForceInstallAllDlcs = false
        };

        var vm = CreateViewModel(instance);

        Assert.False(vm.ForceInstallAllDlcs);
        Assert.False(vm.IsForceAllDlcsWarningOpen);

        // When user toggles ForceInstallAllDlcs to true:
        vm.ForceInstallAllDlcs = true;

        // Modal opens and ForceInstallAllDlcs is NOT yet applied
        Assert.True(vm.IsForceAllDlcsWarningOpen);
        Assert.False(vm.ForceInstallAllDlcs);

        // If user cancels:
        vm.CancelForceAllDlcsWarningCommand.Execute(null);
        Assert.False(vm.IsForceAllDlcsWarningOpen);
        Assert.False(vm.ForceInstallAllDlcs);

        // When user toggles again and confirms:
        vm.ForceInstallAllDlcs = true;
        Assert.True(vm.IsForceAllDlcsWarningOpen);

        await vm.ConfirmForceAllDlcsWarningCommand.ExecuteAsync(null);
        Assert.False(vm.IsForceAllDlcsWarningOpen);
        Assert.True(vm.ForceInstallAllDlcs);
    }

    [Fact]
    public async Task RequestContributeManifestAsync_WhenVoluntaryContributionsDisabled_ShowsWarningAndDoesNotOpenModal()
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"settings_{Guid.NewGuid():N}.json");
        var appSettings = new AppSettingsService(NullLogger<AppSettingsService>.Instance, tempPath);
        await appSettings.SetEnableVoluntaryContributionsAsync(false);

        var mockNotifications = new Mock<INotificationService>();
        var instance = new GameInstance
        {
            Id = Guid.NewGuid(),
            Name = "Contribute Test Game",
            AppId = 1000,
            InstallPath = @"C:\Games\ContributeTestGame",
            Origin = InstanceOrigin.DepotBox,
            Status = InstanceStatus.Ready,
            Depots = [new DepotInfo { DepotId = 101, ManifestId = 9991, IsDownloaded = true }]
        };

        var vm = CreateViewModel(instance, appSettings: appSettings, mockNotifications: mockNotifications);
        var depotItem = new SelectableDepotItem
        {
            Depot = instance.Depots[0]
        };
        vm.Depots = [depotItem];

        await vm.RequestContributeManifestCommand.ExecuteAsync(depotItem);

        vm.IsContributionConsentModalOpen.Should().BeFalse();
        mockNotifications.Verify(n => n.ShowWarning(
            "Voluntary Contributions Disabled",
            It.Is<string>(s => s.Contains("Voluntary contributions are disabled")),
            It.IsAny<TimeSpan?>(),
            It.IsAny<string?>(),
            It.IsAny<Action?>()),
            Times.Once);
    }

    [Fact]
    public async Task RequestContributeManifestAsync_WhenEnabledAndManifestPresent_OpensModalWithPreview()
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"settings_{Guid.NewGuid():N}.json");
        var appSettings = new AppSettingsService(NullLogger<AppSettingsService>.Instance, tempPath);
        await appSettings.SetEnableVoluntaryContributionsAsync(true);

        var tempManifest = Path.GetTempFileName();
        try
        {
            var mockManifestCache = new Mock<IManifestCacheService>();
            mockManifestCache.Setup(c => c.GetManifestPath(101, 9991)).Returns(tempManifest);

            var preview = new ContributionPreview
            {
                AppId = 1000,
                DepotId = 101,
                ManifestId = 9991,
                ManifestSizeBytes = 1024,
                ManifestHashSha256 = "hash123"
            };

            var mockContributionService = new Mock<IVoluntaryContributionService>();
            mockContributionService
                .Setup(s => s.CreatePreviewAsync(1000, 101, 9991, tempManifest, null, It.IsAny<CancellationToken>()))
                .ReturnsAsync(preview);

            var instance = new GameInstance
            {
                Id = Guid.NewGuid(),
                Name = "Contribute Test Game",
                AppId = 1000,
                InstallPath = @"C:\Games\ContributeTestGame",
                Origin = InstanceOrigin.DepotBox,
                Status = InstanceStatus.Ready,
                Depots = [new DepotInfo { DepotId = 101, ManifestId = 9991, IsDownloaded = true }]
            };

            var vm = CreateViewModel(
                instance,
                appSettings: appSettings,
                mockManifestCache: mockManifestCache,
                mockContributionService: mockContributionService);

            var depotItem = new SelectableDepotItem
            {
                Depot = instance.Depots[0]
            };
            vm.Depots = [depotItem];

            await vm.RequestContributeManifestCommand.ExecuteAsync(depotItem);

            vm.IsContributionConsentModalOpen.Should().BeTrue();
            vm.ActiveContributionPreview.Should().Be(preview);
            vm.ContributionConsentGranted.Should().BeFalse();
        }
        finally
        {
            if (File.Exists(tempManifest)) File.Delete(tempManifest);
        }
    }

    [Fact]
    public async Task TransmitActiveContribution_WhenVoluntaryContributionsDisabled_AbortsWithWarning()
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"settings_{Guid.NewGuid():N}.json");
        var appSettings = new AppSettingsService(NullLogger<AppSettingsService>.Instance, tempPath);
        await appSettings.SetEnableVoluntaryContributionsAsync(false);

        var mockNotifications = new Mock<INotificationService>();
        var instance = new GameInstance
        {
            Id = Guid.NewGuid(),
            Name = "Contribute Test Game",
            AppId = 1000,
            InstallPath = @"C:\Games\ContributeTestGame",
            Origin = InstanceOrigin.DepotBox,
            Status = InstanceStatus.Ready,
            Depots = [new DepotInfo { DepotId = 101, ManifestId = 9991, IsDownloaded = true }]
        };

        var vm = CreateViewModel(instance, appSettings: appSettings, mockNotifications: mockNotifications);
        vm.ActiveContributionPreview = new ContributionPreview
        {
            DepotId = 101,
            ManifestId = 9991
        };
        vm.ContributionConsentGranted = true;

        vm.TransmitActiveContributionCommand.Execute(null);

        mockNotifications.Verify(n => n.ShowWarning(
            "Voluntary Contributions Disabled",
            It.Is<string>(s => s.Contains("Voluntary contributions are disabled")),
            It.IsAny<TimeSpan?>(),
            It.IsAny<string?>(),
            It.IsAny<Action?>()),
            Times.Once);
        vm.IsTransmittingContribution.Should().BeFalse();
    }

    [Fact]
    public async Task ApplyHubSnapshotAsync_UpdatesAllDepotRevisionsFromSnapshot()
    {
        var instance = new GameInstance
        {
            Id = Guid.NewGuid(),
            Name = "Snapshot Target Game",
            AppId = 1000,
            InstallPath = @"C:\Games\SnapshotTargetGame",
            Origin = InstanceOrigin.DepotBox,
            Status = InstanceStatus.Ready,
            Depots = [
                new DepotInfo { DepotId = 101, ManifestId = 1111, IsDownloaded = true },
                new DepotInfo { DepotId = 102, ManifestId = 2222, IsDownloaded = true }
            ]
        };

        var vm = CreateViewModel(instance);

        var snapshotItem = new HubSnapshotItem
        {
            Snapshot = new BuildSnapshot
            {
                AppId = 1000,
                BuildId = 5555,
                DepotManifests = new Dictionary<uint, ulong>
                {
                    [101] = 9991,
                    [102] = 9992
                }
            }
        };

        await vm.ApplyHubSnapshotCommand.ExecuteAsync(snapshotItem);

        vm.Instance.Depots.First(d => d.DepotId == 101).ManifestId.Should().Be(9991);
        vm.Instance.Depots.First(d => d.DepotId == 102).ManifestId.Should().Be(9992);
        vm.Instance.Depots.First(d => d.DepotId == 101).IsDownloaded.Should().BeFalse();
        vm.Instance.Depots.First(d => d.DepotId == 102).IsDownloaded.Should().BeFalse();
    }

    [Fact]
    public void HubRevisionItem_DateAuthenticity_RejectsUnixEpochAndMinValue()
    {
        var itemNull = new HubRevisionItem { AuthenticPublicationDate = null };
        itemNull.HasAuthenticDate.Should().BeFalse();

        var itemEpoch = new HubRevisionItem { AuthenticPublicationDate = DateTimeOffset.UnixEpoch };
        itemEpoch.HasAuthenticDate.Should().BeFalse();

        var itemMin = new HubRevisionItem { AuthenticPublicationDate = DateTimeOffset.MinValue };
        itemMin.HasAuthenticDate.Should().BeFalse();

        var itemValid = new HubRevisionItem { AuthenticPublicationDate = new DateTimeOffset(2026, 10, 15, 12, 0, 0, TimeSpan.Zero) };
        itemValid.HasAuthenticDate.Should().BeTrue();
    }

    [Fact]
    public void HubSnapshotItem_DateAuthenticity_RejectsUnixEpochAndDisplaysUnknownDate()
    {
        var snapshotEpoch = new HubSnapshotItem
        {
            Snapshot = new BuildSnapshot
            {
                CreationTime = DateTimeOffset.UnixEpoch
            }
        };
        snapshotEpoch.HasAuthenticDate.Should().BeFalse();
        snapshotEpoch.AuthenticDateDisplay.Should().BeEmpty();

        var snapshotValid = new HubSnapshotItem
        {
            Snapshot = new BuildSnapshot
            {
                CreationTime = new DateTimeOffset(2026, 5, 10, 0, 0, 0, TimeSpan.Zero)
            }
        };
        snapshotValid.HasAuthenticDate.Should().BeTrue();
        snapshotValid.AuthenticDateDisplay.Should().NotBeEmpty();
    }

    [Fact]
    public void GameBuildInfo_And_InstalledBuildSnapshot_RejectUnixEpoch()
    {
        var buildEpoch = new GameBuildInfo
        {
            BuildId = "1",
            UpdatedAt = DateTimeOffset.UnixEpoch
        };
        buildEpoch.HasAuthenticDate.Should().BeFalse();
        buildEpoch.FormattedDate.Should().Be("Unknown date");

        var installedEpoch = new InstalledBuildSnapshot
        {
            BuildId = "1",
            BuildDate = DateTimeOffset.UnixEpoch,
            InstalledAt = DateTimeOffset.UnixEpoch
        };
        installedEpoch.HasAuthenticDate.Should().BeFalse();
        installedEpoch.FormattedDate.Should().Be("Unknown date");
    }
}

