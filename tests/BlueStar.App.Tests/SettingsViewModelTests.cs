using System;
using System.Collections.Generic;
using System.IO;
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
        ILocalCoverageComparator? coverageComparator = null)
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
            coverageComparator: coverageComparator ?? _coverageComparator);
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
    public void RunManifestAnalysis_WhenEnabled_QueriesRemoteCoverage_AndPopulatesCoverageStats()
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
            Name = "Audited Game",
            AppId = 440,
            InstallPath = @"C:\Games\AuditedGame",
            Depots = new List<DepotInfo>
            {
                new DepotInfo { DepotId = 441, ManifestId = 10001, IsDownloaded = true },
                new DepotInfo { DepotId = 442, ManifestId = 10002, IsDownloaded = true }
            }
        };

        _mockInstanceManager
            .Setup(m => m.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<GameInstance> { instance });

        _mockManifestHub
            .Setup(h => h.GetCoverageAsync(It.IsAny<IEnumerable<uint>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ManifestArtifact>
            {
                new ManifestArtifact { DepotId = 441, ManifestId = 10001 },
                new ManifestArtifact { DepotId = 443, ManifestId = 10003 }
            });

        var vm = CreateViewModel();
        vm.EnableManifestAnalysis = true;

        // Act
        vm.RunManifestAnalysis();

        // Assert
        vm.HasAnalysisResults.Should().BeTrue();
        vm.CoverageAuditedCount.Should().Be(2);
        vm.CoverageMatchedCount.Should().Be(1);
        vm.CoverageLocalOnlyCount.Should().Be(1);
        vm.CoverageMissingCount.Should().Be(1);
        vm.CoveragePercentage.Should().Be(50);
        vm.CoverageSummaryText.Should().Contain("Audited 2 local manifests: 1 matched in Hub (50%), 1 local-only (candidates for contribution), 1 available in Hub to download.");
        vm.MissingManifestsList.Should().ContainSingle(m => m.DepotId == 443 && m.ManifestId == 10003);
        vm.LocalOnlyManifestsList.Should().ContainSingle(m => m.DepotId == 442 && m.ManifestId == 10002);
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
}
