using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BlueStar.App.ViewModels;
using BlueStar.Core.Interfaces;
using BlueStar.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace BlueStar.App.Tests;

public class LibrarySelectionViewModelTests
{
    private static (LibraryViewModel Vm, Mock<IInstanceManager> MockManager, GameInstance Inst1, GameInstance Inst2) CreateTestSetup()
    {
        var inst1 = new GameInstance
        {
            Id = Guid.NewGuid(),
            Name = "Game Alpha",
            AppId = 1001,
            InstallPath = @"C:\Games\GameAlpha",
            Status = InstanceStatus.Ready
        };

        var inst2 = new GameInstance
        {
            Id = Guid.NewGuid(),
            Name = "Game Beta",
            AppId = 1002,
            InstallPath = @"C:\Games\GameBeta",
            Status = InstanceStatus.Ready
        };

        var mockManager = new Mock<IInstanceManager>();
        mockManager.Setup(m => m.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<GameInstance> { inst1, inst2 });
        mockManager.Setup(m => m.DeleteAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var mockParser = new Mock<IDepotBoxArchiveParser>();
        var mockEngineDetector = new Mock<IEngineDetector>();
        var mockLauncher = new Mock<IGameLauncher>();
        var mockNotifications = new Mock<INotificationService>();

        var vm = new LibraryViewModel(
            mockManager.Object,
            mockParser.Object,
            mockEngineDetector.Object,
            mockLauncher.Object,
            NullLogger<LibraryViewModel>.Instance,
            notificationService: mockNotifications.Object);

        // Populate synchronously for test isolation
        vm.Instances = new ObservableCollection<GameInstance> { inst1, inst2 };
        vm.ApplyFilters();

        return (vm, mockManager, inst1, inst2);
    }

    [Fact]
    public void ToggleSelectionMode_TogglesStateAndClearsWhenDeactivated()
    {
        var (vm, _, _, _) = CreateTestSetup();
        Assert.False(vm.IsSelectionModeActive);

        // 1. Activate
        vm.ToggleSelectionModeCommand.Execute(null);
        Assert.True(vm.IsSelectionModeActive);

        // Select first item
        var firstCard = vm.FilteredInstances[0];
        vm.ToggleInstanceSelectionCommand.Execute(firstCard);
        Assert.True(firstCard.IsSelected);
        Assert.Equal(1, vm.SelectedInstancesCount);
        Assert.True(vm.HasSelectedInstances);

        // 2. Deactivate via toggle
        vm.ToggleSelectionModeCommand.Execute(null);
        Assert.False(vm.IsSelectionModeActive);
        Assert.Equal(0, vm.SelectedInstancesCount);
        Assert.False(vm.HasSelectedInstances);
        Assert.False(firstCard.IsSelected);
    }

    [Fact]
    public void ExitSelectionMode_DeactivatesAndClearsSelection()
    {
        var (vm, _, _, _) = CreateTestSetup();

        vm.ToggleSelectionModeCommand.Execute(null);
        Assert.True(vm.IsSelectionModeActive);

        vm.ToggleInstanceSelectionCommand.Execute(vm.FilteredInstances[0]);
        vm.ToggleInstanceSelectionCommand.Execute(vm.FilteredInstances[1]);
        Assert.Equal(2, vm.SelectedInstancesCount);

        // Exit via cross button command
        vm.ExitSelectionModeCommand.Execute(null);
        Assert.False(vm.IsSelectionModeActive);
        Assert.Equal(0, vm.SelectedInstancesCount);
        Assert.False(vm.HasSelectedInstances);
        Assert.All(vm.FilteredInstances, card => Assert.False(card.IsSelected));
    }

    [Fact]
    public void ClearSelection_ClearsSelectedInstancesWhileKeepingModeActive()
    {
        var (vm, _, _, _) = CreateTestSetup();

        vm.ToggleSelectionModeCommand.Execute(null);
        vm.ToggleInstanceSelectionCommand.Execute(vm.FilteredInstances[0]);
        vm.ToggleInstanceSelectionCommand.Execute(vm.FilteredInstances[1]);
        Assert.Equal(2, vm.SelectedInstancesCount);
        Assert.True(vm.HasSelectedInstances);

        // Clear selection
        vm.ClearSelectionCommand.Execute(null);
        Assert.True(vm.IsSelectionModeActive); // Still in selection mode
        Assert.Equal(0, vm.SelectedInstancesCount);
        Assert.False(vm.HasSelectedInstances);
        Assert.All(vm.FilteredInstances, card => Assert.False(card.IsSelected));
    }

    [Fact]
    public void ToggleInstanceSelection_IgnoredWhenSelectionModeInactive()
    {
        var (vm, _, _, _) = CreateTestSetup();
        Assert.False(vm.IsSelectionModeActive);

        var firstCard = vm.FilteredInstances[0];
        vm.ToggleInstanceSelectionCommand.Execute(firstCard);

        Assert.False(firstCard.IsSelected);
        Assert.Equal(0, vm.SelectedInstancesCount);
        Assert.False(vm.HasSelectedInstances);
    }

    [Fact]
    public void BulkDeleteModal_RequiresSelectionToOpen()
    {
        var (vm, _, _, _) = CreateTestSetup();
        vm.ToggleSelectionModeCommand.Execute(null);

        // No selection
        vm.OpenBulkDeleteModalCommand.Execute(null);
        Assert.False(vm.IsBulkDeleteModalOpen);

        // Select an item
        vm.ToggleInstanceSelectionCommand.Execute(vm.FilteredInstances[0]);
        Assert.True(vm.HasSelectedInstances);

        vm.OpenBulkDeleteModalCommand.Execute(null);
        Assert.True(vm.IsBulkDeleteModalOpen);

        vm.CloseBulkDeleteModalCommand.Execute(null);
        Assert.False(vm.IsBulkDeleteModalOpen);
    }

    [Fact]
    public async Task ConfirmBulkDeleteInstancesAsync_DeletesOnlySelectedAndExitsSelection()
    {
        var (vm, mockManager, inst1, inst2) = CreateTestSetup();
        vm.ToggleSelectionModeCommand.Execute(null);

        // Select only inst1
        var card1 = vm.FilteredInstances.First(c => c.Id == inst1.Id);
        vm.ToggleInstanceSelectionCommand.Execute(card1);
        Assert.Equal(1, vm.SelectedInstancesCount);

        vm.OpenBulkDeleteModalCommand.Execute(null);
        Assert.True(vm.IsBulkDeleteModalOpen);

        // Execute unlink bulk delete (deleteFiles = false)
        await vm.ConfirmBulkDeleteInstancesCommand.ExecuteAsync("false");

        // Verify inst1 was deleted
        mockManager.Verify(m => m.DeleteAsync(inst1.Id, It.IsAny<CancellationToken>()), Times.Once);
        // Verify inst2 was NOT deleted
        mockManager.Verify(m => m.DeleteAsync(inst2.Id, It.IsAny<CancellationToken>()), Times.Never);

        // Modal closed & selection mode exited
        Assert.False(vm.IsBulkDeleteModalOpen);
        Assert.False(vm.IsSelectionModeActive);
        Assert.Equal(0, vm.SelectedInstancesCount);

        // Instance collection updated
        Assert.Single(vm.Instances);
        Assert.Equal(inst2.Id, vm.Instances[0].Id);
    }

    [Fact]
    public void Selection_PreservedWhenFiltersModified()
    {
        var (vm, _, inst1, inst2) = CreateTestSetup();
        vm.ToggleSelectionModeCommand.Execute(null);

        // Select inst1
        var card1 = vm.FilteredInstances.First(c => c.Id == inst1.Id);
        vm.ToggleInstanceSelectionCommand.Execute(card1);
        Assert.True(card1.IsSelected);
        Assert.Equal(1, vm.SelectedInstancesCount);

        // Filter by Search: match only Game Beta (inst2)
        vm.SearchFilter = "Beta";
        Assert.Single(vm.FilteredInstances);
        Assert.Equal(inst2.Id, vm.FilteredInstances[0].Id);
        Assert.False(vm.FilteredInstances[0].IsSelected);
        // Overall selected count remains 1
        Assert.Equal(1, vm.SelectedInstancesCount);

        // Now clear search filter: inst1 reappears
        vm.SearchFilter = string.Empty;
        Assert.Equal(2, vm.FilteredInstances.Count);
        var reappearedCard1 = vm.FilteredInstances.First(c => c.Id == inst1.Id);
        var reappearedCard2 = vm.FilteredInstances.First(c => c.Id == inst2.Id);

        // Crucial test: inst1 MUST still be selected and visually flagged
        Assert.True(reappearedCard1.IsSelected);
        Assert.False(reappearedCard2.IsSelected);
        Assert.Equal(1, vm.SelectedInstancesCount);

        // Also test engine filter change
        vm.SelectEngineFilterCommand.Execute("All");
        reappearedCard1 = vm.FilteredInstances.First(c => c.Id == inst1.Id);
        Assert.True(reappearedCard1.IsSelected);
        Assert.Equal(1, vm.SelectedInstancesCount);

        // Also test status filter change
        vm.SelectStatusFilterCommand.Execute("Ready");
        reappearedCard1 = vm.FilteredInstances.First(c => c.Id == inst1.Id);
        Assert.True(reappearedCard1.IsSelected);
        Assert.Equal(1, vm.SelectedInstancesCount);
    }

    [Fact]
    public void ToggleInstanceSelection_IsIdempotentAndCannotDuplicate()
    {
        var (vm, _, inst1, _) = CreateTestSetup();
        vm.ToggleSelectionModeCommand.Execute(null);

        var card1 = vm.FilteredInstances.First(c => c.Id == inst1.Id);
        
        // Select once
        vm.ToggleInstanceSelectionCommand.Execute(card1);
        Assert.True(card1.IsSelected);
        Assert.Equal(1, vm.SelectedInstancesCount);

        // Simulate filter re-application
        vm.ApplyFilters();
        var refreshedCard1 = vm.FilteredInstances.First(c => c.Id == inst1.Id);
        Assert.True(refreshedCard1.IsSelected);
        Assert.Equal(1, vm.SelectedInstancesCount);

        // Toggling already selected item must cleanly DESELECT it, not double-select
        vm.ToggleInstanceSelectionCommand.Execute(refreshedCard1);
        Assert.False(refreshedCard1.IsSelected);
        Assert.Equal(0, vm.SelectedInstancesCount);
        Assert.False(vm.HasSelectedInstances);
    }
}
