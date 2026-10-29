using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using BlueStar.App.ViewModels;
using BlueStar.App.Views;
using BlueStar.Core.Interfaces;
using BlueStar.Core.Models;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace BlueStar.App.Tests;

public sealed class NexusBrowserViewModelTests
{
    [Theory]
    [InlineData("https://www.nexusmods.com/baldursgate3", "baldursgate3", true)]
    [InlineData("https://www.nexusmods.com/baldursgate3/", "baldursgate3", true)]
    [InlineData("https://www.nexusmods.com/baldursgate3/mods", "baldursgate3", true)]
    [InlineData("https://www.nexusmods.com/baldursgate3/mods/12345", "baldursgate3", true)]
    [InlineData("https://www.nexusmods.com/baldursgate3/mods/12345?tab=files", "baldursgate3", true)]
    [InlineData("https://www.nexusmods.com/skyrimspecialedition/mods/100", "baldursgate3", false)]
    [InlineData("https://www.nexusmods.com/news", "baldursgate3", false)]
    [InlineData("https://www.nexusmods.com/", "baldursgate3", false)]
    [InlineData("https://users.nexusmods.com/auth/sign_in", "baldursgate3", true)]
    [InlineData("https://www.nexusmods.com/login", "baldursgate3", true)]
    [InlineData("https://www.nexusmods.com/register", "baldursgate3", true)]
    [InlineData("https://accounts.google.com/o/oauth2/v2/auth?scope=email", "baldursgate3", true)]
    [InlineData("https://discord.com/api/oauth2/authorize?client_id=123", "baldursgate3", true)]
    [InlineData("https://steamcommunity.com/openid/login", "baldursgate3", true)]
    [InlineData("https://challenges.cloudflare.com/turnstile/v0/api.js", "baldursgate3", true)]
    [InlineData("https://files.nexusmods.com/123/mod.zip", "baldursgate3", true)]
    [InlineData("https://cf-files.nexusmods.com/456/mod.zip", "baldursgate3", true)]
    [InlineData("https://www.nexusmods.com/Core/Libs/Common/Managers/Downloads?id=789", "baldursgate3", true)]
    [InlineData("https://www.patreon.com/modder", "baldursgate3", false)]
    [InlineData("https://twitter.com/nexusmods", "baldursgate3", false)]
    [InlineData("https://youtube.com/watch?v=123", "baldursgate3", false)]
    [InlineData("nxm://baldursgate3/mods/123/files/456?key=abc&expires=123", "baldursgate3", true)]
    public void IsAllowedNexusUrl_ValidatesBoundaryCorrectly(string url, string gameDomain, bool expected)
    {
        var result = NexusBrowserPanel.IsAllowedNexusUrl(url, gameDomain);
        result.Should().Be(expected);
    }

    [Fact]
    public void GetGameModsHomeUrl_ReturnsCorrectModUrl()
    {
        NexusBrowserPanel.GetGameModsHomeUrl("baldursgate3")
            .Should().Be("https://www.nexusmods.com/baldursgate3/mods");

        NexusBrowserPanel.GetGameModsHomeUrl(null)
            .Should().Be("https://www.nexusmods.com/");
    }

    [Fact]
    public async Task HandleDownloadedFileAsync_InstallsPackageThroughPipeline()
    {
        // Arrange
        var tempFile = Path.Combine(Path.GetTempPath(), $"test_mod_{Guid.NewGuid():N}.zip");
        await File.WriteAllBytesAsync(tempFile, [1, 2, 3]);

        try
        {
            var nexusServiceMock = new Mock<INexusModsService>();
            ModPackage? installedPackage = null;

            var vm = new NexusBrowserViewModel(
                nexusServiceMock.Object,
                package =>
                {
                    installedPackage = package;
                    return Task.FromResult<ModInstallResult?>(new ModInstallResult
                    {
                        Status = ModInstallStatus.Installed,
                        Record = new InstalledModRecord { Id = "test", Name = package.Name, Source = package.Source, Strategy = "copy", Files = [] }
                    });
                },
                null,
                NullLogger.Instance);

            var instance = new GameInstance
            {
                Id = Guid.NewGuid(),
                Name = "Baldur's Gate 3",
                AppId = 1086940,
                InstallPath = @"C:\Games\BG3"
            };

            vm.Open(instance, "baldursgate3");
            var callbackTriggered = false;
            vm.OnModInstalledCallback = () =>
            {
                callbackTriggered = true;
                return Task.CompletedTask;
            };

            // Act
            var success = await vm.HandleDownloadedFileAsync(tempFile, "https://files.nexusmods.com/123/test_mod.zip");

            // Assert
            success.Should().BeTrue();
            installedPackage.Should().NotBeNull();
            installedPackage!.Source.Provider.Should().Be("nexus");
            installedPackage.Source.PageUrl.Should().Contain("baldursgate3");
            callbackTriggered.Should().BeTrue();
            vm.IsDownloading.Should().BeFalse();
            vm.StatusMessage.Should().Contain("instalado");
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }
}
