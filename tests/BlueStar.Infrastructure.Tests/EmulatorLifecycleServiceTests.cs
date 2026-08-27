using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BlueStar.Core.Interfaces;
using BlueStar.Core.Models;
using BlueStar.Infrastructure.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace BlueStar.Infrastructure.Tests;

public class EmulatorLifecycleServiceTests
{
    [Fact]
    public async Task DeployOrUpdateEmulatorWithDlcPreservation_PreservesAndRestoresDlcState()
    {
        var dlcInstallerMock = new Mock<IDlcInstaller>();
        var instanceManagerMock = new Mock<IInstanceManager>();

        dlcInstallerMock.Setup(d => d.UninstallDlcAsync(It.IsAny<GameInstance>(), It.IsAny<DlcInfo>(), It.IsAny<CancellationToken>(), It.IsAny<IProgress<string>>()))
            .ReturnsAsync(true);
        dlcInstallerMock.Setup(d => d.InstallDlcAsync(It.IsAny<GameInstance>(), It.IsAny<DlcInfo>(), It.IsAny<CancellationToken>(), It.IsAny<IProgress<string>>()))
            .ReturnsAsync(true);

        var service = new EmulatorLifecycleService(
            dlcInstallerMock.Object,
            NullLogger<EmulatorLifecycleService>.Instance,
            instanceManagerMock.Object,
            deployHandler: (inst, opt, prog, ct) => Task.FromResult(true));

        var instance = new GameInstance
        {
            Id = Guid.NewGuid(),
            Name = "Test Game",
            InstallPath = @"C:\Games\TestGame",
            DlcUnlockerInstalled = true,
            UnlockedDlcIds = new uint[] { 1001, 1002 },
            Dlcs = new List<DlcInfo>
            {
                new DlcInfo { AppId = 1001, Name = "DLC 1", Depots = new List<DepotInfo>(), IsInstalled = true },
                new DlcInfo { AppId = 1002, Name = "DLC 2", Depots = new List<DepotInfo>(), IsInstalled = true },
                new DlcInfo { AppId = 1003, Name = "DLC 3", Depots = new List<DepotInfo>(), IsInstalled = false }
            }
        };

        var progress = new Progress<DeployProgress>();
        var result = await service.DeployOrUpdateEmulatorWithDlcPreservationAsync(instance, "refix", progress, CancellationToken.None);

        result.Should().BeTrue();

        // Verify that dlc uninstaller was called to preserve
        dlcInstallerMock.Verify(d => d.UninstallDlcAsync(It.IsAny<GameInstance>(), It.IsAny<DlcInfo>(), It.IsAny<CancellationToken>(), It.IsAny<IProgress<string>>()), Times.Once);

        // Verify that dlc installer was called to restore
        dlcInstallerMock.Verify(d => d.InstallDlcAsync(It.IsAny<GameInstance>(), It.IsAny<DlcInfo>(), It.IsAny<CancellationToken>(), It.IsAny<IProgress<string>>()), Times.Once);

        // Verify that instance was updated
        instanceManagerMock.Verify(m => m.UpdateAsync(It.Is<GameInstance>(i => i.DlcUnlockerInstalled == true), It.IsAny<CancellationToken>()), Times.Once);
    }
}
