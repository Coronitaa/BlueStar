using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BlueStar.App.ViewModels;
using BlueStar.Core.Interfaces;
using BlueStar.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace BlueStar.App.Tests;

public class LibraryPosterGridViewModelTests
{
    private static LibraryViewModel CreateViewModel()
    {
        var mockManager = new Mock<IInstanceManager>();
        mockManager.Setup(m => m.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<GameInstance>());

        var mockParser = new Mock<IDepotBoxArchiveParser>();
        var mockEngineDetector = new Mock<IEngineDetector>();
        var mockLauncher = new Mock<IGameLauncher>();

        return new LibraryViewModel(
            mockManager.Object,
            mockParser.Object,
            mockEngineDetector.Object,
            mockLauncher.Object,
            NullLogger<LibraryViewModel>.Instance);
    }

    [Fact]
    public async Task CycleColumnCount_IncludesPosterGridModeAtEnd()
    {
        var vm = CreateViewModel();
        vm.ColumnCount = 3;

        // 3 -> 4
        await vm.CycleColumnCountCommand.ExecuteAsync(null);
        Assert.Equal(4, vm.ColumnCount);
        Assert.Equal(4, vm.EffectiveColumnCount);

        // 4 -> 5
        await vm.CycleColumnCountCommand.ExecuteAsync(null);
        Assert.Equal(5, vm.ColumnCount);
        Assert.Equal(5, vm.EffectiveColumnCount);

        // 5 -> 0 (list view)
        await vm.CycleColumnCountCommand.ExecuteAsync(null);
        Assert.Equal(0, vm.ColumnCount);

        // 0 -> -1 (poster capsule grid view at the end of the list)
        await vm.CycleColumnCountCommand.ExecuteAsync(null);
        Assert.Equal(-1, vm.ColumnCount);
        Assert.Equal(vm.ResponsiveColumnCount, vm.EffectiveColumnCount);

        // -1 -> 3 (wraps back to start)
        await vm.CycleColumnCountCommand.ExecuteAsync(null);
        Assert.Equal(3, vm.ColumnCount);
        Assert.Equal(3, vm.EffectiveColumnCount);
    }

    [Fact]
    public void UpdateResponsiveColumns_AdaptsToAvailableWidth_WhenInPosterMode()
    {
        var vm = CreateViewModel();
        vm.ColumnCount = -1; // Poster grid mode

        // Small width
        vm.UpdateResponsiveColumns(400);
        Assert.Equal(2, vm.ResponsiveColumnCount);
        Assert.Equal(2, vm.EffectiveColumnCount);

        // Medium width (~1100 px -> 5 columns)
        vm.UpdateResponsiveColumns(1100);
        Assert.Equal(5, vm.ResponsiveColumnCount);
        Assert.Equal(5, vm.EffectiveColumnCount);

        // Wide width (~1540 px -> 7 columns)
        vm.UpdateResponsiveColumns(1540);
        Assert.Equal(7, vm.ResponsiveColumnCount);
        Assert.Equal(7, vm.EffectiveColumnCount);

        // When switching back to fixed ColumnCount (e.g., 4)
        vm.ColumnCount = 4;
        Assert.Equal(4, vm.EffectiveColumnCount);
    }

    [Fact]
    public void CapsuleImageUrl_FallsBackCorrectly_AndExposedInInstanceCardItem()
    {
        var steamInst = new GameInstance
        {
            Id = Guid.NewGuid(),
            Name = "Steam Game",
            AppId = 730,
            InstallPath = @"C:\Games\CS2",
            Status = InstanceStatus.Ready
        };

        Assert.Equal("https://shared.cloudflare.steamstatic.com/store_item_assets/steam/apps/730/library_600x900.jpg", steamInst.CapsuleImageUrl);

        var cardItem = new InstanceCardItem(steamInst, []);
        Assert.Equal(steamInst.CapsuleImageUrl, cardItem.CapsuleImageUrl);

        var folderInst = new GameInstance
        {
            Id = Guid.NewGuid(),
            Name = "Custom Folder Game",
            AppId = 0,
            InstallPath = @"C:\Games\Custom",
            Status = InstanceStatus.Ready
        };

        // For non-Steam game without custom capsule, falls back to HeaderImageUrl
        Assert.Equal(folderInst.HeaderImageUrl, folderInst.CapsuleImageUrl);
    }
}
