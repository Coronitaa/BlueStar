using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using BlueStar.App.ViewModels;
using BlueStar.Core.Interfaces;
using BlueStar.Core.Models;
using BlueStar.Infrastructure.Storage;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace BlueStar.App.Tests;

public class SettingsViewModelTests : IDisposable
{
    private readonly string _tempSettingsPath;
    private readonly AppSettingsService _appSettings;
    private readonly Mock<IDepotBoxAuthService> _mockAuth = new();
    private readonly Mock<IDepotBoxApiClient> _mockApi = new();
    private readonly Mock<ILicenseService> _mockLicense = new();
    private readonly Mock<INotificationService> _mockNotification = new();
    private readonly Mock<ILocalizationService> _mockLocalization = new();
    private readonly Mock<IPrerequisiteService> _mockPrerequisites = new();
    private readonly Mock<IBackgroundTaskService> _mockBackgroundTask = new();
    private readonly Mock<IInstanceManager> _mockInstanceManager = new();
    private readonly Mock<IManifestCacheService> _mockManifestCache = new();
    private readonly Mock<IManifestHubProvider> _mockManifestHub = new();
    private readonly Mock<IVoluntaryContributionService> _mockContributionService = new();
    private readonly Mock<IContributionTransportService> _mockContributionTransport = new();
    private readonly Mock<IStardustService> _mockStardust = new();
    private readonly Mock<ISteamLicenseDiscoveryService> _mockSteamLicense = new();
    private readonly Mock<IHistoricalRevisionDiscoveryService> _mockRevisionDiscovery = new();
    private readonly Mock<ISecureStorage> _mockSecureStorage = new();
    private readonly BlueStar.Infrastructure.Services.LocalCoverageComparator _coverageComparator = new();

    public SettingsViewModelTests()
    {
        _tempSettingsPath = Path.Combine(Path.GetTempPath(), $"settings_test_{Guid.NewGuid():N}.json");
        _appSettings = new AppSettingsService(NullLogger<AppSettingsService>.Instance, _tempSettingsPath);
        _mockLocalization.SetupGet(l => l.CurrentLanguage).Returns("en");
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

    private SettingsViewModel CreateViewModel(
        IBackgroundTaskService? backgroundTaskService = null,
        IInstanceManager? instanceManager = null,
        IManifestCacheService? manifestCacheService = null,
        IManifestHubProvider? manifestHubProvider = null,
        IVoluntaryContributionService? voluntaryContributionService = null,
        IContributionTransportService? contributionTransportService = null,
        IStardustService? stardustService = null,
        ILocalCoverageComparator? coverageComparator = null,
        ISteamLicenseDiscoveryService? steamLicenseDiscoveryService = null,
        IHistoricalRevisionDiscoveryService? historicalRevisionDiscoveryService = null,
        ISecureStorage? secureStorage = null)
    {
        return new SettingsViewModel(
            _mockAuth.Object,
            _mockApi.Object,
            _mockLicense.Object,
            _mockNotification.Object,
            _appSettings,
            _mockLocalization.Object,
            NullLogger<SettingsViewModel>.Instance,
            prerequisiteService: _mockPrerequisites.Object,
            backgroundTaskService: backgroundTaskService ?? _mockBackgroundTask.Object,
            instanceManager: instanceManager ?? _mockInstanceManager.Object,
            manifestCacheService: manifestCacheService ?? _mockManifestCache.Object,
            manifestHubProvider: manifestHubProvider ?? _mockManifestHub.Object,
            voluntaryContributionService: voluntaryContributionService ?? _mockContributionService.Object,
            contributionTransportService: contributionTransportService ?? _mockContributionTransport.Object,
            stardustService: stardustService ?? _mockStardust.Object,
            coverageComparator: coverageComparator ?? _coverageComparator,
            steamLicenseDiscoveryService: steamLicenseDiscoveryService ?? _mockSteamLicense.Object,
            historicalRevisionDiscoveryService: historicalRevisionDiscoveryService ?? _mockRevisionDiscovery.Object,
            secureStorage: secureStorage ?? _mockSecureStorage.Object);
    }

    [Fact]
    public void CanInstallAllRequirements_InitialState_IsDisabled()
    {
        // Arrange
        var vm = CreateViewModel();

        // Assert
        vm.HasMissingRequirements.Should().BeFalse();
        vm.CanInstallAllRequirements.Should().BeFalse();
        vm.InstallAllRequirementsCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public async Task CanInstallAllRequirements_AfterScanDetectsMissing_EnablesCommand()
    {
        // Arrange
        var missingItem = new PrerequisiteItem
        {
            Id = "vcredist_x64",
            Name = "Visual C++ Redistributable",
            Status = PrerequisiteStatus.NeedsDownload
        };

        _mockPrerequisites
            .Setup(p => p.DetectSystemPrerequisitesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([missingItem]);

        var vm = CreateViewModel();

        bool canExecuteChangedFired = false;
        vm.InstallAllRequirementsCommand.CanExecuteChanged += (s, e) => canExecuteChangedFired = true;

        // Act
        await vm.ScanRequirementsAsync();

        // Assert
        vm.HasMissingRequirements.Should().BeTrue();
        vm.MissingRequirementsCount.Should().Be(1);
        vm.CanInstallAllRequirements.Should().BeTrue();
        vm.InstallAllRequirementsCommand.CanExecute(null).Should().BeTrue();
        canExecuteChangedFired.Should().BeTrue();
    }

    [Fact]
    public async Task CanInstallAllRequirements_WhenAllInstalled_RemainsDisabled()
    {
        // Arrange
        var installedItem = new PrerequisiteItem
        {
            Id = "directx",
            Name = "DirectX Runtime",
            Status = PrerequisiteStatus.InstalledInSystem
        };

        _mockPrerequisites
            .Setup(p => p.DetectSystemPrerequisitesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([installedItem]);

        var vm = CreateViewModel();

        // Act
        await vm.ScanRequirementsAsync();

        // Assert
        vm.HasMissingRequirements.Should().BeFalse();
        vm.MissingRequirementsCount.Should().Be(0);
        vm.CanInstallAllRequirements.Should().BeFalse();
        vm.InstallAllRequirementsCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public void CanInstallAllRequirements_WhileScanningOrInstalling_IsDisabled()
    {
        // Arrange
        var vm = CreateViewModel();
        vm.HasMissingRequirements = true;

        vm.CanInstallAllRequirements.Should().BeTrue();
        vm.InstallAllRequirementsCommand.CanExecute(null).Should().BeTrue();

        // Act & Assert 1: Scanning active
        vm.IsScanningRequirements = true;
        vm.CanInstallAllRequirements.Should().BeFalse();
        vm.InstallAllRequirementsCommand.CanExecute(null).Should().BeFalse();

        // Reset
        vm.IsScanningRequirements = false;
        vm.CanInstallAllRequirements.Should().BeTrue();

        // Act & Assert 2: Installing active
        vm.IsInstallingRequirements = true;
        vm.CanInstallAllRequirements.Should().BeFalse();
        vm.InstallAllRequirementsCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public void RunManifestAnalysis_WhenDisabled_ShowsWarningAndDoesNotRun()
    {
        // Arrange
        var vm = CreateViewModel();
        vm.EnableManifestAnalysis = false;

        // Act
        vm.RunManifestAnalysis();

        // Assert
        _mockNotification.Verify(n => n.ShowWarning("Manifest Analysis", It.Is<string>(s => s.Contains("opt-in")), It.IsAny<TimeSpan?>(), It.IsAny<string?>(), It.IsAny<Action?>()), Times.Once);
        _mockBackgroundTask.Verify(b => b.QueueTask(
            It.IsAny<string>(),
            It.IsAny<string?>(),
            It.IsAny<Func<IProgress<BackgroundTaskProgress>, CancellationToken, Task>>(),
            It.IsAny<Guid?>()),
            Times.Never);
    }

    [Fact]
    public void LicenseDiscovery_DiscoversUninstalledGames_ComputesMissingAndPopulatesBatchPreview()
    {
        // Arrange: Sync task runner
        _mockBackgroundTask
            .Setup(b => b.QueueTask(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<Func<IProgress<BackgroundTaskProgress>, CancellationToken, Task>>(), It.IsAny<Guid?>()))
            .Callback<string, string?, Func<IProgress<BackgroundTaskProgress>, CancellationToken, Task>, Guid?>((name, desc, work, guid) =>
            {
                var mockProgress = new Mock<IProgress<BackgroundTaskProgress>>();
                work(mockProgress.Object, CancellationToken.None).GetAwaiter().GetResult();
            });

        // Stage 1: Discovers owned games (e.g. uninstalled App 570 "Dota 2" and App 440 "Team Fortress 2")
        var licensedApps = new List<SteamLicenseItem>
        {
            new SteamLicenseItem { AppId = 440, Name = "Team Fortress 2", IsEligibleForContribution = true },
            new SteamLicenseItem { AppId = 570, Name = "Dota 2", IsEligibleForContribution = true }
        };
        _mockSteamLicense
            .Setup(s => s.DiscoverLicensesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(SteamLicenseDiscoveryResult.Success(licensedApps));

        // Stage 2: Historical revision discovery returns candidate revisions across community sources
        var revisionsMap = new Dictionary<uint, IReadOnlyList<HubRevisionItem>>
        {
            [440] = new List<HubRevisionItem>
            {
                new HubRevisionItem { AppId = 440, DepotId = 441, ManifestId = 10001, Provenance = "ManifestHub2", SizeOriginal = 5000000 },
                new HubRevisionItem { AppId = 440, DepotId = 442, ManifestId = 10002, Provenance = "K25", SizeOriginal = 3000000 }
            },
            [570] = new List<HubRevisionItem>
            {
                new HubRevisionItem { AppId = 570, DepotId = 571, ManifestId = 20001, Provenance = "DepotBox", SizeOriginal = 8000000 }
            }
        };
        _mockRevisionDiscovery
            .Setup(r => r.DiscoverRevisionsForAppsAsync(It.IsAny<IEnumerable<uint>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(revisionsMap);

        // Stage 3: Central Hub coverage contains only (441, 10001)
        _mockContributionTransport
            .Setup(t => t.QueryExistingManifestsAsync(It.IsAny<IEnumerable<uint>>(), It.IsAny<IEnumerable<uint>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<(uint DepotId, ulong ManifestId)>
            {
                (441, 10001)
            });

        var vm = CreateViewModel();
        vm.EnableManifestAnalysis = true;
        vm.EnableVoluntaryContributions = true;
        vm.SelectedManifestAnalysisScope = "Licensed Steam Library (All)";

        // Act
        vm.RunManifestAnalysis();

        // Assert
        vm.HasAnalysisResults.Should().BeTrue();
        vm.CoverageAuditedCount.Should().Be(3);
        vm.CoverageMatchedCount.Should().Be(1);
        vm.CoverageMissingCount.Should().Be(2); // (442, 10002) and (571, 20001) missing
        vm.CoveragePercentage.Should().Be(33.3);
        vm.ManifestAnalysisStatus.Should().Be("Completed");

        // Verify Batch Contribution Preview modal is opened
        vm.IsBatchPreviewModalOpen.Should().BeTrue();
        vm.ContributionBatchPreview.Should().HaveCount(2);
        vm.ContributionBatchPreview.Should().Contain(b => b.AppId == 440 && b.DepotId == 442 && b.ManifestId == 10002 && b.Provenance == "K25");
        vm.ContributionBatchPreview.Should().Contain(b => b.AppId == 570 && b.DepotId == 571 && b.ManifestId == 20001 && b.AppName == "Dota 2");
    }

    [Fact]
    public void LicenseDiscovery_WhenUnauthenticated_ReportsRealBlockageAndNeverSubstitutesInstalledInstances()
    {
        // Arrange
        _mockBackgroundTask
            .Setup(b => b.QueueTask(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<Func<IProgress<BackgroundTaskProgress>, CancellationToken, Task>>(), It.IsAny<Guid?>()))
            .Callback<string, string?, Func<IProgress<BackgroundTaskProgress>, CancellationToken, Task>, Guid?>((name, desc, work, guid) =>
            {
                var mockProgress = new Mock<IProgress<BackgroundTaskProgress>>();
                work(mockProgress.Object, CancellationToken.None).GetAwaiter().GetResult();
            });

        _mockSteamLicense
            .Setup(s => s.DiscoverLicensesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(SteamLicenseDiscoveryResult.Blocked(
                SteamLicenseBlockageReason.Unauthenticated,
                "Steam session is unauthenticated or expired."));

        var vm = CreateViewModel();
        vm.EnableManifestAnalysis = true;
        vm.SelectedManifestAnalysisScope = "Licensed Steam Library (All)";

        // Act
        vm.RunManifestAnalysis();

        // Assert: Real blockage reason reported in UI status
        vm.ManifestAnalysisStatus.Should().Contain("Steam account not authenticated");
        vm.HasAnalysisResults.Should().BeFalse();

        // INVARIANT VERIFICATION: NEVER silently fall back to installed instances!
        _mockInstanceManager.Verify(m => m.GetAllAsync(It.IsAny<CancellationToken>()), Times.Never);
        _mockRevisionDiscovery.Verify(r => r.DiscoverRevisionsForAppsAsync(It.IsAny<IEnumerable<uint>>(), It.IsAny<CancellationToken>()), Times.Never);
        _mockContributionTransport.Verify(t => t.QueryExistingManifestsAsync(It.IsAny<IEnumerable<uint>>(), It.IsAny<IEnumerable<uint>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public void LicenseDiscovery_WhenNoSteamSession_ReportsRealBlockageInStatus()
    {
        // Arrange
        _mockBackgroundTask
            .Setup(b => b.QueueTask(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<Func<IProgress<BackgroundTaskProgress>, CancellationToken, Task>>(), It.IsAny<Guid?>()))
            .Callback<string, string?, Func<IProgress<BackgroundTaskProgress>, CancellationToken, Task>, Guid?>((name, desc, work, guid) =>
            {
                var mockProgress = new Mock<IProgress<BackgroundTaskProgress>>();
                work(mockProgress.Object, CancellationToken.None).GetAwaiter().GetResult();
            });

        _mockSteamLicense
            .Setup(s => s.DiscoverLicensesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(SteamLicenseDiscoveryResult.Blocked(
                SteamLicenseBlockageReason.NoSteamSession,
                "No active Steam session found in Chromium or CEF cache."));

        var vm = CreateViewModel();
        vm.EnableManifestAnalysis = true;
        vm.SelectedManifestAnalysisScope = "Licensed Steam Library (All)";

        // Act
        vm.RunManifestAnalysis();

        // Assert
        vm.ManifestAnalysisStatus.Should().Be("Steam account not authenticated: No active Steam session found");
        vm.HasAnalysisResults.Should().BeFalse();
        _mockInstanceManager.Verify(m => m.GetAllAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public void LicenseDiscovery_WhenHubConnectionFails_SetsStatusToConnectionErrorAndNeverReportsCompleted()
    {
        // Arrange
        _mockBackgroundTask
            .Setup(b => b.QueueTask(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<Func<IProgress<BackgroundTaskProgress>, CancellationToken, Task>>(), It.IsAny<Guid?>()))
            .Callback<string, string?, Func<IProgress<BackgroundTaskProgress>, CancellationToken, Task>, Guid?>((name, desc, work, guid) =>
            {
                var mockProgress = new Mock<IProgress<BackgroundTaskProgress>>();
                work(mockProgress.Object, CancellationToken.None).GetAwaiter().GetResult();
            });

        _mockSteamLicense
            .Setup(s => s.DiscoverLicensesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(SteamLicenseDiscoveryResult.Success([
                new SteamLicenseItem { AppId = 440, Name = "Team Fortress 2" }
            ]));

        _mockRevisionDiscovery
            .Setup(r => r.DiscoverRevisionsForAppsAsync(It.IsAny<IEnumerable<uint>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<uint, IReadOnlyList<HubRevisionItem>>
            {
                [440] = [new HubRevisionItem { AppId = 440, DepotId = 441, ManifestId = 10001 }]
            });

        // Hub coverage throws HttpRequestException (network error / offline)
        _mockContributionTransport
            .Setup(t => t.QueryExistingManifestsAsync(It.IsAny<IEnumerable<uint>>(), It.IsAny<IEnumerable<uint>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Central Manifest Hub connection refused"));

        var vm = CreateViewModel();
        vm.EnableManifestAnalysis = true;

        // Act
        vm.RunManifestAnalysis();

        // Assert: Accurate state is "Connection Error"; NEVER report "Completed"!
        vm.ManifestAnalysisStatus.Should().Be("Connection Error");
        vm.ManifestAnalysisStatus.Should().NotBe("Completed");
        vm.HasAnalysisResults.Should().BeFalse();
    }

    [Fact]
    public void LicenseDiscovery_WhenAllCoveredInHub_Reports100PercentCompleteCoverage()
    {
        // Arrange
        _mockBackgroundTask
            .Setup(b => b.QueueTask(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<Func<IProgress<BackgroundTaskProgress>, CancellationToken, Task>>(), It.IsAny<Guid?>()))
            .Callback<string, string?, Func<IProgress<BackgroundTaskProgress>, CancellationToken, Task>, Guid?>((name, desc, work, guid) =>
            {
                var mockProgress = new Mock<IProgress<BackgroundTaskProgress>>();
                work(mockProgress.Object, CancellationToken.None).GetAwaiter().GetResult();
            });

        _mockSteamLicense
            .Setup(s => s.DiscoverLicensesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(SteamLicenseDiscoveryResult.Success([
                new SteamLicenseItem { AppId = 440, Name = "Team Fortress 2" }
            ]));

        _mockRevisionDiscovery
            .Setup(r => r.DiscoverRevisionsForAppsAsync(It.IsAny<IEnumerable<uint>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<uint, IReadOnlyList<HubRevisionItem>>
            {
                [440] = [new HubRevisionItem { AppId = 440, DepotId = 441, ManifestId = 10001 }]
            });

        // Hub coverage matches all candidate revisions
        _mockContributionTransport
            .Setup(t => t.QueryExistingManifestsAsync(It.IsAny<IEnumerable<uint>>(), It.IsAny<IEnumerable<uint>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<(uint DepotId, ulong ManifestId)> { (441, 10001) });

        var vm = CreateViewModel();
        vm.EnableManifestAnalysis = true;

        // Act
        vm.RunManifestAnalysis();

        // Assert: Accurately reports 100% Complete Coverage
        vm.ManifestAnalysisStatus.Should().Be("100% Complete Coverage");
        vm.CoveragePercentage.Should().Be(100);
        vm.CoverageMissingCount.Should().Be(0);
        vm.IsBatchPreviewModalOpen.Should().BeFalse();
    }

    [Fact]
    public void LicenseDiscovery_WhenZeroCandidates_Reports0Candidates()
    {
        // Arrange
        _mockBackgroundTask
            .Setup(b => b.QueueTask(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<Func<IProgress<BackgroundTaskProgress>, CancellationToken, Task>>(), It.IsAny<Guid?>()))
            .Callback<string, string?, Func<IProgress<BackgroundTaskProgress>, CancellationToken, Task>, Guid?>((name, desc, work, guid) =>
            {
                var mockProgress = new Mock<IProgress<BackgroundTaskProgress>>();
                work(mockProgress.Object, CancellationToken.None).GetAwaiter().GetResult();
            });

        _mockSteamLicense
            .Setup(s => s.DiscoverLicensesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(SteamLicenseDiscoveryResult.Success([])); // 0 licenses

        var vm = CreateViewModel();
        vm.EnableManifestAnalysis = true;

        // Act
        vm.RunManifestAnalysis();

        // Assert
        vm.ManifestAnalysisStatus.Should().Be("0 Candidates");
        vm.CoverageAuditedCount.Should().Be(0);
    }

    [Fact]
    public async Task BatchContribution_WhenAuthorized_DownloadsValidatesSubmitsAndReconcilesStardust()
    {
        // Arrange
        await _appSettings.SetEnableVoluntaryContributionsAsync(true);

        _mockBackgroundTask
            .Setup(b => b.QueueTask(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<Func<IProgress<BackgroundTaskProgress>, CancellationToken, Task>>(), It.IsAny<Guid?>()))
            .Callback<string, string?, Func<IProgress<BackgroundTaskProgress>, CancellationToken, Task>, Guid?>((name, desc, work, guid) =>
            {
                var mockProgress = new Mock<IProgress<BackgroundTaskProgress>>();
                work(mockProgress.Object, CancellationToken.None).GetAwaiter().GetResult();
            });

        // Create temporary manifest file with valid Steam Protobuf magic header [0xD0, 0x17, 0xF6, 0x71]
        var tempManifestDir = Path.Combine(Path.GetTempPath(), "SettingsTestManifests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempManifestDir);
        var manifestFile = Path.Combine(tempManifestDir, "441_10001.manifest");
        byte[] validProtobufPayload = [0xD0, 0x17, 0xF6, 0x71, 0xAA, 0xBB, 0xCC, 0xDD];
        await File.WriteAllBytesAsync(manifestFile, validProtobufPayload);

        _mockRevisionDiscovery
            .Setup(r => r.FetchManifestBinaryAsync(440, 441, 10001, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(manifestFile);

        _mockContributionTransport
            .Setup(t => t.TransmitContributionAsync(It.IsAny<ContributionSubmission>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PublicationReceipt
            {
                ReceiptId = "RCPT-BATCH-440",
                Status = "Published",
                StardustCredited = 150,
                AppId = 440,
                DepotId = 441,
                ManifestId = 10001
            });

        var vm = CreateViewModel();
        vm.EnableVoluntaryContributions = true;
        vm.IsBatchPreviewModalOpen = true;
        vm.ContributionBatchPreview.Add(new ContributionBatchItem
        {
            AppName = "Team Fortress 2",
            AppId = 440,
            DepotId = 441,
            ManifestId = 10001,
            Provenance = "ManifestHub2",
            SizeBytes = validProtobufPayload.Length
        });

        // Act: User authorizes batch submission via single click without per-file prompts
        await vm.AuthorizeAndSubmitBatchAsync();

        // Assert
        vm.IsBatchPreviewModalOpen.Should().BeFalse();
        _mockRevisionDiscovery.Verify(r => r.FetchManifestBinaryAsync(440, 441, 10001, It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
        _mockContributionTransport.Verify(t => t.TransmitContributionAsync(
            It.Is<ContributionSubmission>(s => s.AppId == 440 && s.DepotId == 441 && s.ManifestId == 10001 && s.UserConsentGranted),
            It.IsAny<CancellationToken>()), Times.Once);

        // Verify Stardust balance reconciliation
        _mockStardust.Verify(s => s.NotifyRewardConfirmed(150), Times.Once);
        vm.BatchConfirmedCount.Should().Be(1);
        vm.LastContributionReceiptId.Should().Be("RCPT-BATCH-440");
        vm.ContributionBatchPreview.Should().BeEmpty();

        try { Directory.Delete(tempManifestDir, true); } catch { }
    }

    [Fact]
    public async Task BatchContribution_WhenVoluntaryContributionsDisabled_AbortsWithWarning()
    {
        // Arrange
        await _appSettings.SetEnableVoluntaryContributionsAsync(false);
        var vm = CreateViewModel();
        vm.EnableVoluntaryContributions = false;
        vm.ContributionBatchPreview.Add(new ContributionBatchItem
        {
            AppId = 440,
            DepotId = 441,
            ManifestId = 10001
        });

        // Act
        await vm.AuthorizeAndSubmitBatchAsync();

        // Assert
        _mockNotification.Verify(n => n.ShowWarning(
            "Contributions Disabled",
            It.Is<string>(s => s.Contains("Voluntary contributions")),
            It.IsAny<TimeSpan?>(),
            It.IsAny<string?>(),
            It.IsAny<Action?>()),
            Times.Once);

        _mockContributionTransport.Verify(t => t.TransmitContributionAsync(
            It.IsAny<ContributionSubmission>(),
            It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public void DiagnosticScope_InstalledInstances_AuditsLocalGameInstances()
    {
        // Arrange
        _mockBackgroundTask
            .Setup(b => b.QueueTask(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<Func<IProgress<BackgroundTaskProgress>, CancellationToken, Task>>(), It.IsAny<Guid?>()))
            .Callback<string, string?, Func<IProgress<BackgroundTaskProgress>, CancellationToken, Task>, Guid?>((name, desc, work, guid) =>
            {
                var mockProgress = new Mock<IProgress<BackgroundTaskProgress>>();
                work(mockProgress.Object, CancellationToken.None).GetAwaiter().GetResult();
            });

        var instance = new GameInstance
        {
            Id = Guid.NewGuid(),
            Name = "Local Game",
            AppId = 440,
            InstallPath = @"C:\Games\LocalGame",
            Depots = new List<DepotInfo>
            {
                new DepotInfo { DepotId = 441, ManifestId = 10001, IsDownloaded = true },
                new DepotInfo { DepotId = 442, ManifestId = 10002, IsDownloaded = true }
            }
        };

        _mockInstanceManager
            .Setup(m => m.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<GameInstance> { instance });

        _mockContributionTransport
            .Setup(t => t.QueryExistingManifestsAsync(It.IsAny<IEnumerable<uint>>(), It.IsAny<IEnumerable<uint>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<(uint DepotId, ulong ManifestId)> { (441, 10001) });

        var vm = CreateViewModel();
        vm.EnableManifestAnalysis = true;
        vm.SelectedManifestAnalysisScope = "Installed Instances";

        // Act
        vm.RunManifestAnalysis();

        // Assert
        _mockInstanceManager.Verify(m => m.GetAllAsync(It.IsAny<CancellationToken>()), Times.Once);
        vm.HasAnalysisResults.Should().BeTrue();
        vm.CoverageAuditedCount.Should().Be(2);
        vm.CoverageMatchedCount.Should().Be(1);
        vm.CoverageMissingCount.Should().Be(1);
    }

    [Fact]
    public async Task TransmitActiveContribution_WhenVoluntaryContributionsDisabled_AbortsWithWarning()
    {
        // Arrange
        await _appSettings.SetEnableVoluntaryContributionsAsync(false);
        var vm = CreateViewModel();
        vm.ActiveContributionPreview = new ContributionPreview
        {
            DepotId = 441,
            ManifestId = 10001
        };
        vm.ContributionConsentGranted = true;

        // Act
        vm.TransmitActiveContribution();

        // Assert
        _mockNotification.Verify(n => n.ShowWarning(
            "Contributions Disabled",
            It.Is<string>(s => s.Contains("Voluntary contributions")),
            It.IsAny<TimeSpan?>(),
            It.IsAny<string?>(),
            It.IsAny<Action?>()),
            Times.Once);

        _mockContributionTransport.Verify(t => t.TransmitContributionAsync(
            It.IsAny<ContributionSubmission>(),
            It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task TransmitActiveContribution_WhenEnabled_SubmitsSanitizedPayloadAndUpdatesStardust()
    {
        // Arrange
        await _appSettings.SetEnableVoluntaryContributionsAsync(true);

        _mockBackgroundTask
            .Setup(b => b.QueueTask(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<Func<IProgress<BackgroundTaskProgress>, CancellationToken, Task>>(), It.IsAny<Guid?>()))
            .Callback<string, string?, Func<IProgress<BackgroundTaskProgress>, CancellationToken, Task>, Guid?>((name, desc, work, guid) =>
            {
                var mockProgress = new Mock<IProgress<BackgroundTaskProgress>>();
                work(mockProgress.Object, CancellationToken.None).GetAwaiter().GetResult();
            });

        var preview = new ContributionPreview
        {
            DepotId = 441,
            ManifestId = 10001
        };

        var submission = new ContributionSubmission
        {
            DepotId = 441,
            ManifestId = 10001,
            ManifestHashSha256 = "sig-verified-sha256"
        };

        _mockContributionService
            .Setup(s => s.PrepareSubmissionAsync(preview, true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(submission);

        _mockContributionTransport
            .Setup(t => t.TransmitContributionAsync(submission, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PublicationReceipt
            {
                ReceiptId = "REC-441-TEST",
                Status = "Accepted",
                StardustCredited = 50
            });

        var vm = CreateViewModel();
        vm.ActiveContributionPreview = preview;
        vm.ContributionConsentGranted = true;

        // Act
        vm.TransmitActiveContribution();

        // Assert
        _mockContributionService.Verify(s => s.PrepareSubmissionAsync(preview, true, It.IsAny<CancellationToken>()), Times.Once);
        _mockContributionTransport.Verify(t => t.TransmitContributionAsync(submission, It.IsAny<CancellationToken>()), Times.Once);
        _mockStardust.Verify(s => s.NotifyRewardConfirmed(50), Times.Once);
        vm.LastContributionReceiptId.Should().Be("REC-441-TEST");
    }

    [Fact]
    public void OpenSteamLoginModalCommand_OpensModalAndSetsLoading()
    {
        // Arrange
        var vm = CreateViewModel();

        // Act
        vm.OpenSteamLoginModalCommand.Execute(null);

        // Assert
        vm.IsSteamLoginModalOpen.Should().BeTrue();
        vm.IsSteamLoginLoading.Should().BeTrue();
    }

    [Fact]
    public void CloseSteamLoginModalCommand_ClosesModalAndResetsLoading()
    {
        // Arrange
        var vm = CreateViewModel();
        vm.OpenSteamLoginModalCommand.Execute(null);

        // Act
        vm.CloseSteamLoginModalCommand.Execute(null);

        // Assert
        vm.IsSteamLoginModalOpen.Should().BeFalse();
        vm.IsSteamLoginLoading.Should().BeFalse();
    }

    [Fact]
    public void OnSteamLoginSuccessful_UpdatesSessionStateAndClosesModal()
    {
        // Arrange
        var vm = CreateViewModel();
        vm.OpenSteamLoginModalCommand.Execute(null);

        // Act
        vm.OnSteamLoginSuccessful("76561198000000001", "CoronitaTester");

        // Assert
        vm.IsSteamLoginModalOpen.Should().BeFalse();
        vm.IsSteamLoginLoading.Should().BeFalse();
        vm.IsSteamAuthenticated.Should().BeTrue();
        vm.SteamAuthenticatedSteamId.Should().Be("76561198000000001");
        vm.SteamAuthenticatedUser.Should().Be("CoronitaTester");
    }

    [Fact]
    public async Task LogoutSteamSessionAsync_ClearsSessionState()
    {
        // Arrange
        var vm = CreateViewModel();
        vm.OnSteamLoginSuccessful("76561198000000001", "CoronitaTester");

        // Act
        await vm.LogoutSteamSessionCommand.ExecuteAsync(null);

        // Assert
        vm.IsSteamAuthenticated.Should().BeFalse();
        vm.SteamAuthenticatedSteamId.Should().BeNull();
        vm.SteamAuthenticatedUser.Should().BeNull();
    }

    [Fact]
    public void AnalyzeManifestCoverage_WhenSteamUnauthenticated_AutoOpensSteamLoginModal()
    {
        // Arrange
        _mockBackgroundTask
            .Setup(b => b.QueueTask(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<Func<IProgress<BackgroundTaskProgress>, CancellationToken, Task>>(), It.IsAny<Guid?>()))
            .Callback<string, string?, Func<IProgress<BackgroundTaskProgress>, CancellationToken, Task>, Guid?>((name, desc, work, guid) =>
            {
                var mockProgress = new Mock<IProgress<BackgroundTaskProgress>>();
                work(mockProgress.Object, CancellationToken.None).GetAwaiter().GetResult();
            });

        _mockSteamLicense
            .Setup(s => s.DiscoverLicensesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(SteamLicenseDiscoveryResult.Blocked(SteamLicenseBlockageReason.NoSteamSession, "No active session"));

        var vm = CreateViewModel();
        vm.EnableManifestAnalysis = true;
        vm.SelectedManifestAnalysisScope = "Licensed Steam Library (All)";

        // Act
        vm.RunManifestAnalysis();

        // Assert
        vm.IsSteamLoginModalOpen.Should().BeTrue();
        vm.IsAnalyzingManifests.Should().BeFalse();
    }
}
