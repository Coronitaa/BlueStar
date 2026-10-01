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

    private SettingsViewModel CreateViewModel()
    {
        return new SettingsViewModel(
            _mockAuth.Object,
            _mockApi.Object,
            _mockLicense.Object,
            _mockNotification.Object,
            _appSettings,
            _mockLocalization.Object,
            NullLogger<SettingsViewModel>.Instance,
            prerequisiteService: _mockPrerequisites.Object);
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
}
