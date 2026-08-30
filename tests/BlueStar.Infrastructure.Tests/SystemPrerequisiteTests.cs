using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using BlueStar.Core.Models;
using BlueStar.Infrastructure.Services;
using BlueStar.Infrastructure.Storage;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BlueStar.Infrastructure.Tests;

#pragma warning disable CA1416
public class SystemPrerequisiteTests
{
    [Fact]
    public async Task DetectSystemPrerequisitesAsync_ReturnsStandardFiveRuntimes()
    {
        // Arrange
        using var http = new HttpClient();
        var service = new PrerequisiteService(http, NullLogger<PrerequisiteService>.Instance);

        // Act
        var prereqs = await service.DetectSystemPrerequisitesAsync(CancellationToken.None);

        // Assert
        prereqs.Should().NotBeNull();
        prereqs.Should().HaveCount(5);

        var ids = prereqs.Select(p => p.Id).ToList();
        ids.Should().Contain("vcredist_2015_2022_x64");
        ids.Should().Contain("vcredist_2015_2022_x86");
        ids.Should().Contain("directx_enduser");
        ids.Should().Contain("dotnet_desktop_8");
        ids.Should().Contain("dotnet_runtime_9");

        // Essential runtimes
        prereqs.First(p => p.Id == "vcredist_2015_2022_x64").IsEssential.Should().BeTrue();
        prereqs.First(p => p.Id == "vcredist_2015_2022_x86").IsEssential.Should().BeTrue();
        prereqs.First(p => p.Id == "directx_enduser").IsEssential.Should().BeTrue();
        prereqs.First(p => p.Id == "dotnet_runtime_9").IsEssential.Should().BeTrue();
    }

    [Fact]
    public async Task DetectPrerequisitesAsync_WithUnrealEngine_IncludesUePrereqs()
    {
        // Arrange
        using var http = new HttpClient();
        var service = new PrerequisiteService(http, NullLogger<PrerequisiteService>.Instance);

        var ueInstance = new GameInstance
        {
            AppId = 12345,
            Name = "Unreal Game",
            InstallPath = @"C:\Fake\Path",
            Engine = new EngineInfo { Id = "unreal-5", Name = "Unreal Engine 5", Type = EngineType.UnrealEngine }
        };

        // Act
        var prereqs = await service.DetectPrerequisitesAsync(ueInstance, CancellationToken.None);

        // Assert
        prereqs.Should().Contain(p => p.Id == "ue_prereqs_x64");
    }

    [Fact]
    public async Task AppSettingsService_CheckSystemRequirementsOnStartup_Persists()
    {
        // Arrange
        var tempFile = Path.Combine(Path.GetTempPath(), $"bluestar_settings_{Guid.NewGuid():N}.json");
        try
        {
            var settings = new AppSettingsService(NullLogger<AppSettingsService>.Instance, tempFile);

            // Assert default
            settings.CheckSystemRequirementsOnStartup.Should().BeTrue();

            // Act - Set to false
            await settings.SetCheckSystemRequirementsOnStartupAsync(false);
            settings.CheckSystemRequirementsOnStartup.Should().BeFalse();

            // Re-load from disk
            var reloaded = new AppSettingsService(NullLogger<AppSettingsService>.Instance, tempFile);
            reloaded.CheckSystemRequirementsOnStartup.Should().BeFalse();

            // Set back to true
            await reloaded.SetCheckSystemRequirementsOnStartupAsync(true);
            reloaded.CheckSystemRequirementsOnStartup.Should().BeTrue();
        }
        finally
        {
            if (File.Exists(tempFile))
            {
                try { File.Delete(tempFile); } catch { }
            }
        }
    }

    [Fact]
    public async Task InstallAllPrerequisitesAsync_WhenAllInstalled_ReturnsZero()
    {
        // Arrange
        using var http = new HttpClient();
        var service = new PrerequisiteService(http, NullLogger<PrerequisiteService>.Instance);

        var allInstalledList = new[]
        {
            new PrerequisiteItem
            {
                Id = "fake_1",
                Name = "Fake Prereq 1",
                Status = PrerequisiteStatus.InstalledInSystem
            },
            new PrerequisiteItem
            {
                Id = "fake_2",
                Name = "Fake Prereq 2",
                Status = PrerequisiteStatus.InstalledSuccess
            }
        };

        // Act
        var installedCount = await service.InstallAllPrerequisitesAsync(allInstalledList, null, CancellationToken.None);

        // Assert
        installedCount.Should().Be(0);
    }
}
