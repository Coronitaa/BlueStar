using System;
using System.Threading.Tasks;
using BlueStar.App.ViewModels;
using BlueStar.Core.Interfaces;
using BlueStar.Core.Models;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace BlueStar.App.Tests;

public sealed class ModBrowserViewModelTests
{
    private readonly Mock<IThunderstoreService> _thunderstoreService = new();

    private ModBrowserViewModel CreateBrowser(out WorkshopBrowserViewModel workshop, out ThunderstoreBrowserViewModel thunderstore)
    {
        workshop = new WorkshopBrowserViewModel((_, _) => Task.FromResult(true));
        thunderstore = new ThunderstoreBrowserViewModel(
            _thunderstoreService.Object,
            _ => Task.FromResult<ModInstallResult?>(null),
            null,
            NullLogger.Instance);

        return new ModBrowserViewModel(workshop, thunderstore, null, NullLogger.Instance);
    }

    [Fact]
    public void Open_WithOnlyThunderstore_SelectsThunderstoreTab()
    {
        // Arrange
        var vm = CreateBrowser(out _, out _);
        var instance = new GameInstance
        {
            Id = Guid.NewGuid(),
            Name = "R.E.P.O.",
            AppId = 3241660,
            InstallPath = "C:\\Games\\REPO"
        };

        // Act: R.E.P.O. has Thunderstore community "repo", but NO Steam Workshop
        vm.Open(instance, hasWorkshop: false, hasThunderstore: true, thunderstoreCommunity: "repo");

        // Assert
        vm.IsOpen.Should().BeTrue();
        vm.InstanceName.Should().Be("R.E.P.O.");
        vm.HasWorkshopSupport.Should().BeFalse();
        vm.HasThunderstoreSupport.Should().BeTrue();
        vm.SelectedProvider.Should().Be("Thunderstore");
        vm.Thunderstore.Should().NotBeNull();
        vm.Thunderstore!.IsOpen.Should().BeTrue();
        vm.Thunderstore.Community.Should().Be("repo");
    }

    [Fact]
    public void Open_WithOnlyWorkshop_SelectsWorkshopTab()
    {
        // Arrange
        var vm = CreateBrowser(out _, out _);
        var instance = new GameInstance
        {
            Id = Guid.NewGuid(),
            Name = "Left 4 Dead 2",
            AppId = 550,
            InstallPath = "C:\\Games\\L4D2"
        };

        // Act: Game with only Workshop
        vm.Open(instance, hasWorkshop: true, hasThunderstore: false);

        // Assert
        vm.IsOpen.Should().BeTrue();
        vm.HasWorkshopSupport.Should().BeTrue();
        vm.HasThunderstoreSupport.Should().BeFalse();
        vm.SelectedProvider.Should().Be("Workshop");
        vm.Workshop.IsOpen.Should().BeTrue();
    }

    [Fact]
    public void SelectProvider_SwitchesTabSmoothly_WhenSupported()
    {
        // Arrange
        var vm = CreateBrowser(out _, out _);
        var instance = new GameInstance
        {
            Id = Guid.NewGuid(),
            Name = "Lethal Company",
            AppId = 1966720,
            InstallPath = "C:\\Games\\Lethal Company"
        };

        vm.Open(instance, hasWorkshop: true, hasThunderstore: true, thunderstoreCommunity: "lethal-company");
        vm.SelectedProvider.Should().Be("Thunderstore");

        // Act: Switch to Steam Workshop
        vm.SelectProvider("Workshop");

        // Assert
        vm.SelectedProvider.Should().Be("Workshop");
        vm.Workshop.IsOpen.Should().BeTrue();

        // Act: Switch back to Thunderstore
        vm.SelectProvider("Thunderstore");
        vm.SelectedProvider.Should().Be("Thunderstore");
    }

    [Fact]
    public void SelectProvider_IgnoresUnsupportedProvider()
    {
        // Arrange
        var vm = CreateBrowser(out _, out _);
        var instance = new GameInstance
        {
            Id = Guid.NewGuid(),
            Name = "R.E.P.O.",
            AppId = 3241660,
            InstallPath = "C:\\Games\\REPO"
        };

        // Game with NO Workshop support
        vm.Open(instance, hasWorkshop: false, hasThunderstore: true, thunderstoreCommunity: "repo");
        vm.SelectedProvider.Should().Be("Thunderstore");

        // Act: Try switching to Workshop (should be ignored because HasWorkshopSupport is false)
        vm.SelectProvider("Workshop");

        // Assert
        vm.SelectedProvider.Should().Be("Thunderstore");
    }

    [Fact]
    public void Close_ClosesAllSubProviders()
    {
        // Arrange
        var vm = CreateBrowser(out var workshop, out var thunderstore);
        var instance = new GameInstance
        {
            Id = Guid.NewGuid(),
            Name = "Lethal Company",
            AppId = 1966720,
            InstallPath = "C:\\Games\\Lethal Company"
        };

        vm.Open(instance, hasWorkshop: true, hasThunderstore: true, thunderstoreCommunity: "lethal-company");

        // Act
        vm.Close();

        // Assert
        vm.IsOpen.Should().BeFalse();
        workshop.IsOpen.Should().BeFalse();
        thunderstore.IsOpen.Should().BeFalse();
    }
}
