using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BlueStar.App.ViewModels;
using BlueStar.Core.Interfaces;
using BlueStar.Core.Models;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace BlueStar.App.Tests;

public sealed class ThunderstoreBrowserViewModelTests
{
    private readonly Mock<IThunderstoreService> _thunderstoreService = new();
    private ModPackage? _installedModPackage;

    private static ThunderstorePackage CreateSamplePackage(string name = "MoreCompany", string owner = "notnotnotswipez")
    {
        return new ThunderstorePackage
        {
            Name = name,
            FullName = $"{owner}-{name}",
            Owner = owner,
            PackageUrl = $"https://thunderstore.io/package/{owner}/{name}/",
            TotalDownloads = 1250000,
            RatingScore = 480,
            DateUpdated = DateTimeOffset.UtcNow,
            Categories = ["BepInEx", "Client", "Server"],
            Versions =
            [
                new ThunderstorePackageVersion
                {
                    Name = name,
                    FullName = $"{owner}-{name}-1.9.4",
                    VersionNumber = "1.9.4",
                    Description = "A mod that increases lobby player count.",
                    DownloadUrl = $"https://thunderstore.io/package/download/{owner}/{name}/1.9.4/",
                    Downloads = 500000
                },
                new ThunderstorePackageVersion
                {
                    Name = name,
                    FullName = $"{owner}-{name}-1.9.3",
                    VersionNumber = "1.9.3",
                    Description = "Older version.",
                    DownloadUrl = $"https://thunderstore.io/package/download/{owner}/{name}/1.9.3/",
                    Downloads = 200000
                }
            ]
        };
    }

    [Fact]
    public async Task Open_LoadsPackagesAndCategories_ForCommunity()
    {
        // Arrange
        var pkg = CreateSamplePackage();
        _thunderstoreService.Setup(s => s.SearchPackagesAsync("lethal-company", "", "All", "Downloads", false, It.IsAny<CancellationToken>()))
            .ReturnsAsync([pkg]);

        var vm = new ThunderstoreBrowserViewModel(
            _thunderstoreService.Object,
            _ => Task.FromResult<ModInstallResult?>(null),
            null,
            NullLogger.Instance);

        var instance = new GameInstance
        {
            Id = Guid.NewGuid(),
            Name = "Lethal Company",
            InstallPath = "C:\\Games\\Lethal Company"
        };

        // Act
        vm.Open(instance, "lethal-company");
        await vm.LoadPackagesCommand.ExecuteAsync(null);

        // Assert
        vm.IsOpen.Should().BeTrue();
        vm.Community.Should().Be("lethal-company");
        vm.InstanceName.Should().Be("Lethal Company");
        vm.Packages.Should().ContainSingle();
        vm.Packages[0].Name.Should().Be("MoreCompany");
        vm.Packages[0].FormattedDownloads.Should().Be("1.3M");
        vm.AvailableCategories.Should().Contain(["All", "BepInEx", "Client", "Server"]);
    }

    [Fact]
    public async Task ToggleSortDirection_TogglesDescendingStateAndReloads()
    {
        // Arrange
        var pkg = CreateSamplePackage();
        _thunderstoreService.Setup(s => s.SearchPackagesAsync("lethal-company", "", "All", "Downloads", true, It.IsAny<CancellationToken>()))
            .ReturnsAsync([pkg]);

        var vm = new ThunderstoreBrowserViewModel(
            _thunderstoreService.Object,
            _ => Task.FromResult<ModInstallResult?>(null),
            null,
            NullLogger.Instance);

        var instance = new GameInstance { Id = Guid.NewGuid(), Name = "Lethal Company", InstallPath = "C:\\Games" };
        vm.Open(instance, "lethal-company");

        // Act
        vm.IsSortDescending.Should().BeTrue();
        vm.ToggleSortDirection();

        // Assert
        vm.IsSortDescending.Should().BeFalse();
        await vm.LoadPackagesCommand.ExecuteAsync(null);
        vm.Packages.Should().ContainSingle();
    }

    [Fact]
    public async Task InstallItemAsync_DownloadsAndInstallsPackage_UpdatesInstalledState()
    {
        // Arrange
        var pkg = CreateSamplePackage();
        var downloadedModPackage = new ModPackage
        {
            Path = "C:\\Temp\\MoreCompany.zip",
            Name = "MoreCompany",
            Source = new ModSourceRef
            {
                Provider = "thunderstore",
                RemoteId = "lethal-company:notnotnotswipez:MoreCompany",
                Version = "1.9.4"
            },
            Layout = ModPackageLayout.BepInExPackage
        };

        _thunderstoreService.Setup(s => s.DownloadPackageAsync(
                "lethal-company",
                pkg,
                It.IsAny<ThunderstorePackageVersion>(),
                It.IsAny<IProgress<double>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(downloadedModPackage);

        var refreshed = false;
        var vm = new ThunderstoreBrowserViewModel(
            _thunderstoreService.Object,
            package =>
            {
                _installedModPackage = package;
                return Task.FromResult<ModInstallResult?>(new ModInstallResult
                {
                    Status = ModInstallStatus.Installed,
                    Record = new InstalledModRecord
                    {
                        Id = "morecompany",
                        Name = "MoreCompany",
                        Strategy = "BepInExPackage",
                        Source = package.Source!,
                        Files = []
                    }
                });
            },
            null,
            NullLogger.Instance)
        {
            OnModInstalledCallback = () => { refreshed = true; return Task.CompletedTask; }
        };

        var instance = new GameInstance
        {
            Id = Guid.NewGuid(),
            Name = "Lethal Company",
            InstallPath = "C:\\Games\\Lethal Company"
        };
        vm.Open(instance, "lethal-company");

        var item = new ThunderstorePackageItem(pkg, vm.InstallItemAsync);

        // Act
        await item.InstallCommand.ExecuteAsync(null);

        // Assert
        _installedModPackage.Should().NotBeNull();
        _installedModPackage!.Source?.Provider.Should().Be("thunderstore");
        item.IsInstalled.Should().BeTrue();
        item.InstalledVersion.Should().Be("1.9.4");
        item.ButtonLabel.Should().Be("Installed");
        refreshed.Should().BeTrue();
    }

    [Fact]
    public void SwitchToWorkshop_ClosesThunderstoreAndInvokesAction()
    {
        // Arrange
        var switched = false;
        var vm = new ThunderstoreBrowserViewModel(
            _thunderstoreService.Object,
            _ => Task.FromResult<ModInstallResult?>(null),
            null,
            NullLogger.Instance);

        var instance = new GameInstance { Id = Guid.NewGuid(), Name = "Lethal Company", InstallPath = "C:\\Games" };
        vm.Open(instance, "lethal-company", hasWorkshop: true, switchToWorkshop: () => switched = true);

        // Act
        vm.SwitchToWorkshopCommand.Execute(null);

        // Assert
        vm.IsOpen.Should().BeFalse();
        switched.Should().BeTrue();
    }
}
