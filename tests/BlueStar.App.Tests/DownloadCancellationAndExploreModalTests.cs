using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
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

public class DownloadCancellationAndExploreModalTests : IDisposable
{
    private readonly string _tempSettingsPath;
    private readonly AppSettingsService _appSettings;
    private readonly Mock<IInstanceManager> _mockInstanceManager = new();
    private readonly Mock<IDownloadProvider> _mockDownloadProvider = new();
    private readonly Mock<INotificationService> _mockNotification = new();
    private readonly Mock<IDepotBoxApiClient> _mockApiClient = new();
    private readonly Mock<IDepotBoxArchiveParser> _mockArchiveParser = new();
    private readonly Mock<IEngineDetector> _mockEngineDetector = new();
    private readonly Mock<IGameLauncher> _mockGameLauncher = new();
    private readonly Mock<ILocalCatalogRepository> _mockLocalRepo = new();
    private readonly Mock<ILocalizationService> _mockLocalization = new();
    private readonly Mock<IDlcInstaller> _mockDlcInstaller = new();
    private readonly Mock<IManifestRegistry> _mockManifestRegistry = new();
    private readonly Mock<IBuildResolver> _mockBuildResolver = new();
    private readonly DownloadQueueManager _downloadQueueManager;

    public DownloadCancellationAndExploreModalTests()
    {
        _tempSettingsPath = Path.Combine(Path.GetTempPath(), $"cancel_test_{Guid.NewGuid():N}.json");
        _appSettings = new AppSettingsService(NullLogger<AppSettingsService>.Instance, _tempSettingsPath);
        _mockLocalization.Setup(l => l.GetString(It.IsAny<string>(), It.IsAny<string>()))
            .Returns((string _, string fallback) => fallback);

        _downloadQueueManager = new DownloadQueueManager(
            _mockDownloadProvider.Object,
            NullLogger<DownloadQueueManager>.Instance,
            _mockInstanceManager.Object,
            _mockNotification.Object);
    }

    public void Dispose()
    {
        try
        {
            if (File.Exists(_tempSettingsPath))
                File.Delete(_tempSettingsPath);
        }
        catch { }
    }

    private InstanceDetailViewModel CreateInstanceDetailViewModel(GameInstance instance)
    {
        var vm = new InstanceDetailViewModel(
            _mockInstanceManager.Object,
            _mockDlcInstaller.Object,
            _downloadQueueManager,
            _appSettings,
            _mockEngineDetector.Object,
            new Mock<IModManagerRegistry>().Object,
            new Mock<IBepInExService>().Object,
            new Mock<IWorkshopService>().Object,
            new Mock<IEmulatorRegistry>().Object,
            new Mock<IEmulatorRatingService>().Object,
            _mockGameLauncher.Object,
            NullLogger<InstanceDetailViewModel>.Instance,
            notificationService: _mockNotification.Object,
            localizationService: _mockLocalization.Object,
            manifestRegistry: _mockManifestRegistry.Object,
            buildResolver: _mockBuildResolver.Object);

        vm.Instance = instance;
        return vm;
    }

    [Fact]
    public void HasActiveJob_ReturnsTrue_OnlyWhenActiveOrPaused()
    {
        var instance = new GameInstance
        {
            Id = Guid.NewGuid(),
            Name = "Test Game",
            AppId = 100,
            InstallPath = @"C:\Games\Test1"
        };
        using var vm = CreateInstanceDetailViewModel(instance);

        // When ActiveJob is null
        vm.ActiveJob = null;
        vm.HasActiveJob.Should().BeFalse();

        // When ActiveJob is Downloading
        var job = new DownloadJobItem { Instance = instance, JobStatus = DownloadJobStatus.Downloading };
        vm.ActiveJob = job;
        vm.HasActiveJob.Should().BeTrue();

        // When ActiveJob is Queued
        job.JobStatus = DownloadJobStatus.Queued;
        vm.HasActiveJob.Should().BeTrue();

        // When ActiveJob is Paused
        job.JobStatus = DownloadJobStatus.Paused;
        vm.HasActiveJob.Should().BeTrue();

        // When ActiveJob is Canceled -> immediately false!
        job.JobStatus = DownloadJobStatus.Canceled;
        vm.HasActiveJob.Should().BeFalse();

        // When ActiveJob is Completed -> immediately false!
        job.JobStatus = DownloadJobStatus.Completed;
        vm.HasActiveJob.Should().BeFalse();

        // When ActiveJob is Failed -> immediately false!
        job.JobStatus = DownloadJobStatus.Failed;
        vm.HasActiveJob.Should().BeFalse();
    }

    [Fact]
    public async Task CancelDownloadAsync_CancelsJob_RefreshesInstance_AndUpdatesActiveJob()
    {
        var instanceId = Guid.NewGuid();
        var instance = new GameInstance
        {
            Id = instanceId,
            Name = "Active Game",
            AppId = 200,
            InstallPath = @"C:\Games\ActiveGame",
            Status = InstanceStatus.Downloading
        };

        var refreshedInstance = instance with { Status = InstanceStatus.Ready };
        _mockInstanceManager.Setup(m => m.GetByIdAsync(instanceId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(refreshedInstance);

        using var vm = CreateInstanceDetailViewModel(instance);

        var job = new DownloadJobItem
        {
            Instance = instance,
            JobStatus = DownloadJobStatus.Downloading
        };
        _downloadQueueManager.Queue.Add(job);
        vm.ActiveJob = job;
        vm.HasActiveJob.Should().BeTrue();

        // Act - Cancel download
        await vm.CancelDownloadCommand.ExecuteAsync(null);

        // Assert
        vm.Instance.Should().Be(refreshedInstance);
        vm.HasActiveJob.Should().BeFalse();
    }

    [Fact]
    public async Task CheckDepotUpdates_DiscardsFakeZeroKbDepots_AndShowsNoDownloadsAvailableNotification()
    {
        var instanceId = Guid.NewGuid();
        var instance = new GameInstance
        {
            Id = instanceId,
            Name = "Game with Zero Kb Depot",
            AppId = 500,
            InstallPath = @"C:\Games\ZeroKbGame",
            Origin = InstanceOrigin.Steam,
            InstalledVersionDate = DateTimeOffset.UtcNow.AddDays(-10),
            Depots =
            [
                new DepotInfo
                {
                    DepotId = 501,
                    Name = "Fake 0kb depot",
                    SizeBytes = 0,
                    ManifestId = 11111,
                    IsDownloaded = false
                }
            ]
        };

        var gameVersion = new GameVersion
        {
            BuildId = "build_2",
            BranchName = "public",
            Depots = [new DepotVersion { DepotId = 501, ManifestId = 22222 }]
        };

        _mockBuildResolver.Setup(b => b.GetAvailableVersionsAsync(500, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<GameVersion> { gameVersion });

        // Discovered artifacts returns 0-byte artifact with NO routes
        var emptyArtifact = new ManifestArtifact
        {
            DepotId = 501,
            ManifestId = 22222,
            SizeBytes = 0,
            Routes = []
        };
        _mockManifestRegistry.Setup(m => m.DiscoverManifestsAsync(500, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ManifestArtifact> { emptyArtifact });

        using var vm = CreateInstanceDetailViewModel(instance);

        // Act - Recheck version triggers CheckDepotUpdatesInternalAsync
        await vm.RecheckVersionCommand.ExecuteAsync(null);
        await Task.Delay(150);

        // Assert
        vm.IsUpdateModalOpen.Should().BeFalse();
        vm.HasPendingDepotUpdate.Should().BeFalse();
        _mockNotification.Verify(n => n.ShowInfo(
            It.Is<string>(s => s.Contains("No Downloads Available from Providers") || s.Contains("Latest Version Not Available Yet")),
            It.Is<string>(s => s.Contains("not yet available across configured providers")),
            It.IsAny<TimeSpan?>(),
            It.IsAny<string?>(),
            It.IsAny<Action?>()), Times.Once);
    }

    [Fact]
    public async Task CheckDepotUpdates_ExcludesZeroKbDepots_AndPreselectsOnlyInstalledCompatibleDepots()
    {
        var instanceId = Guid.NewGuid();
        var instance = new GameInstance
        {
            Id = instanceId,
            Name = "Multi Depot Game",
            AppId = 600,
            InstallPath = @"C:\Games\MultiDepotGame",
            Origin = InstanceOrigin.Steam,
            InstalledVersionDate = DateTimeOffset.UtcNow.AddDays(-10),
            Depots =
            [
                new DepotInfo
                {
                    DepotId = 601,
                    Name = "Base Content Installed",
                    SizeBytes = 5000000,
                    ManifestId = 10001,
                    IsDownloaded = true,
                    Platform = "Universal"
                },
                new DepotInfo
                {
                    DepotId = 602,
                    Name = "Shared 0KB Depot",
                    SizeBytes = 0,
                    ManifestId = 0,
                    IsDownloaded = false,
                    Platform = "Universal"
                },
                new DepotInfo
                {
                    DepotId = 603,
                    Name = "Optional Language Pack",
                    SizeBytes = 2000000,
                    ManifestId = 0,
                    IsDownloaded = false,
                    Platform = "Universal"
                },
                new DepotInfo
                {
                    DepotId = 604,
                    Name = "Linux Binaries",
                    SizeBytes = 3000000,
                    ManifestId = 40001,
                    IsDownloaded = true,
                    Platform = "Linux"
                }
            ]
        };

        var gameVersion = new GameVersion
        {
            BuildId = "build_latest",
            BranchName = "public",
            Depots =
            [
                new DepotVersion { DepotId = 601, ManifestId = 10002, SizeBytes = 5000000 },
                new DepotVersion { DepotId = 602, ManifestId = 20002, SizeBytes = 0 },
                new DepotVersion { DepotId = 603, ManifestId = 30002, SizeBytes = 2000000 },
                new DepotVersion { DepotId = 604, ManifestId = 40002, SizeBytes = 3000000 }
            ]
        };

        _mockBuildResolver.Setup(b => b.GetAvailableVersionsAsync(600, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<GameVersion> { gameVersion });

        var discoveredArtifacts = new List<ManifestArtifact>
        {
            new ManifestArtifact { DepotId = 601, ManifestId = 10002, SizeBytes = 5000000, Routes = [] },
            new ManifestArtifact { DepotId = 602, ManifestId = 20002, SizeBytes = 0, Routes = [] },
            new ManifestArtifact { DepotId = 603, ManifestId = 30002, SizeBytes = 2000000, Routes = [] },
            new ManifestArtifact { DepotId = 604, ManifestId = 40002, SizeBytes = 3000000, Routes = [] }
        };
        _mockManifestRegistry.Setup(m => m.DiscoverManifestsAsync(600, It.IsAny<CancellationToken>()))
            .ReturnsAsync(discoveredArtifacts);

        using var vm = CreateInstanceDetailViewModel(instance);

        // Act
        await vm.RecheckVersionCommand.ExecuteAsync(null);
        await Task.Delay(150);

        // Assert
        vm.IsUpdateModalOpen.Should().BeTrue();
        vm.UpdateAvailableDepots.Should().NotBeEmpty();

        // 1. Depot 602 (0 KB) must NOT be present
        vm.UpdateAvailableDepots.Should().NotContain(d => d.DepotId == 602);

        // 2. Depot 601 (Installed, Windows compatible, > 0 bytes) -> MUST be preselected
        var item601 = vm.UpdateAvailableDepots.FirstOrDefault(d => d.DepotId == 601);
        item601.Should().NotBeNull();
        item601!.IsSelected.Should().BeTrue();

        // 3. Depot 603 (Not downloaded / not installed) -> MUST NOT be preselected
        var item603 = vm.UpdateAvailableDepots.FirstOrDefault(d => d.DepotId == 603);
        item603.Should().NotBeNull();
        item603!.IsSelected.Should().BeFalse();

        // 4. Depot 604 (Linux / incompatible OS) -> MUST NOT be preselected
        var item604 = vm.UpdateAvailableDepots.FirstOrDefault(d => d.DepotId == 604);
        item604.Should().NotBeNull();
        item604!.IsSelected.Should().BeFalse();
    }

    [Fact]
    public async Task BrowseViewModel_UpdateDetailTargetFromWebAsync_ResolvesFromCatalog()
    {
        // Arrange
        var browseVm = new BrowseViewModel(
            _mockApiClient.Object,
            _mockInstanceManager.Object,
            _mockArchiveParser.Object,
            _mockEngineDetector.Object,
            NullLogger<BrowseViewModel>.Instance,
            settingsService: _appSettings,
            localRepo: _mockLocalRepo.Object,
            autoSearch: false);

        var catalogItem = new CatalogAppItem
        {
            AppId = 730,
            Name = "Counter-Strike 2",
            ReviewPercent = 88,
            PriceCents = 0,
            HeaderImageUrl = "https://cdn.cloudflare.steamstatic.com/steam/apps/730/header.jpg"
        };

        _mockLocalRepo.Setup(r => r.GetByAppIdAsync(730, It.IsAny<CancellationToken>()))
            .ReturnsAsync(catalogItem);

        // Act
        await browseVm.UpdateDetailTargetFromWebAsync(730, "CS2", "https://store.steampowered.com/app/730/CounterStrike_2/");

        // Assert
        browseVm.DetailTarget.Should().NotBeNull();
        browseVm.DetailTarget!.AppId.Should().Be(730);
        browseVm.DetailTarget.Name.Should().Be("Counter-Strike 2");
        browseVm.DetailUrl.Should().Be("https://store.steampowered.com/app/730/CounterStrike_2/");
    }

    [Fact]
    public async Task BrowseViewModel_UpdateDetailTargetFromWebAsync_FallbackWhenNotFound()
    {
        // Arrange
        var browseVm = new BrowseViewModel(
            _mockApiClient.Object,
            _mockInstanceManager.Object,
            _mockArchiveParser.Object,
            _mockEngineDetector.Object,
            NullLogger<BrowseViewModel>.Instance,
            settingsService: _appSettings,
            localRepo: _mockLocalRepo.Object,
            autoSearch: false);

        _mockLocalRepo.Setup(r => r.GetByAppIdAsync(9999, It.IsAny<CancellationToken>()))
            .ReturnsAsync((CatalogAppItem?)null);

        // Act
        await browseVm.UpdateDetailTargetFromWebAsync(9999, "Indie Hit", "https://store.steampowered.com/app/9999/Indie_Hit/");

        // Assert
        browseVm.DetailTarget.Should().NotBeNull();
        browseVm.DetailTarget!.AppId.Should().Be(9999);
        browseVm.DetailTarget.Name.Should().Be("Indie Hit");
        browseVm.DetailTarget.HeaderImageUrl.Should().Contain("9999");
        browseVm.DetailUrl.Should().Be("https://store.steampowered.com/app/9999/Indie_Hit/");
    }

    [Fact]
    public async Task HomeViewModel_UpdateDetailTargetFromWebAsync_UpdatesDetailTargetAndDetailUrl()
    {
        // Arrange
        var homeVm = new HomeViewModel(
            _mockInstanceManager.Object,
            _downloadQueueManager,
            _mockGameLauncher.Object,
            _mockApiClient.Object,
            _mockArchiveParser.Object,
            _mockEngineDetector.Object,
            NullLogger<HomeViewModel>.Instance,
            localRepo: _mockLocalRepo.Object);

        var catalogItem = new CatalogAppItem
        {
            AppId = 570,
            Name = "Dota 2",
            ReviewPercent = 82
        };

        _mockLocalRepo.Setup(r => r.GetByAppIdAsync(570, It.IsAny<CancellationToken>()))
            .ReturnsAsync(catalogItem);

        // Act
        await homeVm.UpdateDetailTargetFromWebAsync(570, "Dota 2", "https://store.steampowered.com/app/570/Dota_2/");

        // Assert
        homeVm.DetailTarget.Should().NotBeNull();
        homeVm.DetailTarget!.AppId.Should().Be(570);
        homeVm.DetailTarget.Name.Should().Be("Dota 2");
        homeVm.DetailUrl.Should().Be("https://store.steampowered.com/app/570/Dota_2/");
    }
}
