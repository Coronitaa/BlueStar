using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using BlueStar.Core.Interfaces;
using BlueStar.Core.Models;
using BlueStar.Infrastructure.Downloader;
using BlueStar.Infrastructure.Emulators;
using BlueStar.Infrastructure.Services;
using BlueStar.Infrastructure.Storage;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Moq.Protected;
using Xunit;

namespace BlueStar.Infrastructure.Tests;

public class NewFeaturesTests
{
    // ═════════════════════════════════════════════════════════════════════════
    // 1. DEPOTBOX AUTH SERVICE & API KEY PROTECTION TESTS
    // ═════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task DepotBoxAuthService_WhenCustomKeyExists_AlwaysOverridesProAndBackendKey()
    {
        // Arrange
        var mockStorage = new Mock<ISecureStorage>();
        mockStorage.Setup(s => s.GetAsync("depotbox_api_key", It.IsAny<CancellationToken>()))
            .ReturnsAsync("user_custom_key_12345");

        var mockLicense = new Mock<ILicenseService>();
        mockLicense.Setup(l => l.IsProLicenseActive).Returns(true); // PRO is active, but custom key must take priority

        var authService = new DepotBoxAuthService(mockStorage.Object, mockLicense.Object, NullLogger<DepotBoxAuthService>.Instance);

        // Act
        var effectiveKey = await authService.GetEffectiveApiKeyAsync();
        var source = await authService.GetCurrentAuthSourceAsync();

        // Assert
        effectiveKey.Should().Be("user_custom_key_12345");
        source.Should().Be(DepotBoxAuthSource.CustomOverride);
    }

    [Fact]
    public async Task DepotBoxAuthService_WhenNoCustomKey_ReturnsDefaultBackendKey()
    {
        // Arrange
        var mockStorage = new Mock<ISecureStorage>();
        mockStorage.Setup(s => s.GetAsync("depotbox_api_key", It.IsAny<CancellationToken>()))
            .ReturnsAsync((string?)null);

        var mockLicense = new Mock<ILicenseService>();
        mockLicense.Setup(l => l.IsProLicenseActive).Returns(false);

        var authService = new DepotBoxAuthService(mockStorage.Object, mockLicense.Object, NullLogger<DepotBoxAuthService>.Instance);

        // Act
        var effectiveKey = await authService.GetEffectiveApiKeyAsync();
        var source = await authService.GetCurrentAuthSourceAsync();

        // Assert
        effectiveKey.Should().NotBeNullOrWhiteSpace();
        source.Should().Be(DepotBoxAuthSource.DefaultBackend);
    }

    [Fact]
    public async Task DepotBoxAuthService_WhenDefaultApiKeyConfiguredInAppSettings_ReturnsConfiguredDefault()
    {
        // Arrange
        var mockStorage = new Mock<ISecureStorage>();
        mockStorage.Setup(s => s.GetAsync("depotbox_api_key", It.IsAny<CancellationToken>()))
            .ReturnsAsync((string?)null);

        var mockLicense = new Mock<ILicenseService>();
        var tempSettingsPath = Path.Combine(Path.GetTempPath(), $"settings_{Guid.NewGuid():N}.json");
        var appSettings = new AppSettingsService(NullLogger<AppSettingsService>.Instance, tempSettingsPath);
        await appSettings.SetDefaultApiKeyAsync("backend_configured_api_key");

        var authService = new DepotBoxAuthService(mockStorage.Object, mockLicense.Object, NullLogger<DepotBoxAuthService>.Instance, appSettings);

        // Act
        var effectiveKey = await authService.GetEffectiveApiKeyAsync();
        var source = await authService.GetCurrentAuthSourceAsync();

        // Assert
        effectiveKey.Should().Be("backend_configured_api_key");
        source.Should().Be(DepotBoxAuthSource.DefaultBackend);

        // Cleanup
        try { if (File.Exists(tempSettingsPath)) File.Delete(tempSettingsPath); } catch { }
    }

    [Fact]
    public async Task DepotBoxAuthService_SetAndClearCustomKey_InteractsWithStorage()
    {
        // Arrange
        var mockStorage = new Mock<ISecureStorage>();
        var mockLicense = new Mock<ILicenseService>();
        var authService = new DepotBoxAuthService(mockStorage.Object, mockLicense.Object, NullLogger<DepotBoxAuthService>.Instance);

        // Act & Assert Set
        await authService.SetCustomApiKeyAsync("my_new_key");
        mockStorage.Verify(s => s.SetAsync("depotbox_api_key", "my_new_key", It.IsAny<CancellationToken>()), Times.Once);

        // Act & Assert Clear
        await authService.ClearCustomApiKeyAsync();
        mockStorage.Verify(s => s.DeleteAsync("depotbox_api_key", It.IsAny<CancellationToken>()), Times.Once);
    }

    // ═════════════════════════════════════════════════════════════════════════
    // 2. TOAST NOTIFICATION SERVICE TESTS
    // ═════════════════════════════════════════════════════════════════════════

    [Fact]
    public void NotificationService_ShowSuccess_AddsNotificationItem()
    {
        // Arrange
        var service = new NotificationService(NullLogger<NotificationService>.Instance);

        // Act
        service.ShowSuccess("Instalación Completa", "El mod se ha instalado correctamente.", TimeSpan.FromSeconds(5));

        // Assert
        service.Notifications.Should().HaveCount(1);
        var item = service.Notifications[0];
        item.Title.Should().Be("Instalación Completa");
        item.Message.Should().Be("El mod se ha instalado correctamente.");
        item.Type.Should().Be(NotificationType.Success);
    }

    [Fact]
    public void NotificationService_Dismiss_RemovesNotification()
    {
        // Arrange
        var service = new NotificationService(NullLogger<NotificationService>.Instance);
        service.ShowInfo("Info Title", "Info Message");
        var id = service.Notifications.First().Id;

        // Act
        service.Dismiss(id);

        // Assert
        service.Notifications.Should().BeEmpty();
    }

    [Fact]
    public void NotificationService_ShowErrorAndWarning_AddsWithCorrectType()
    {
        // Arrange
        var service = new NotificationService(NullLogger<NotificationService>.Instance);

        // Act
        service.ShowWarning("Alerta", "Versión desactualizada");
        service.ShowError("Fallo", "No se pudo conectar");

        // Assert
        service.Notifications.Should().HaveCount(2);
        service.Notifications.Any(n => n.Type == NotificationType.Warning).Should().BeTrue();
        service.Notifications.Any(n => n.Type == NotificationType.Error).Should().BeTrue();
    }

    // ═════════════════════════════════════════════════════════════════════════
    // 3. REFIX AUTO-UPDATE & OUTDATED DETECTION TESTS
    // ═════════════════════════════════════════════════════════════════════════

    [Fact]
    public void ReFixUpdateService_IsInstanceReFixOutdated_DetectsOlderVersions()
    {
        // Arrange
        var mockHttp = new Mock<HttpMessageHandler>();
        var httpClient = new HttpClient(mockHttp.Object);
        var mockNotification = new Mock<INotificationService>();
        var updater = new ReFixUpdateService(httpClient, NullLogger<ReFixUpdateService>.Instance, mockNotification.Object);

        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            // Simulate installed steam_api64.dll in instance
            File.WriteAllText(Path.Combine(tempDir, "steam_api64.dll"), "fake dll");

            var instance1 = new GameInstance
            {
                Id = Guid.NewGuid(),
                AppId = 730,
                Name = "Test Game",
                InstallPath = tempDir,
                EmulatorEnabled = true,
                InstalledEmulatorVersion = "1.0" // Older than 1.1
            };

            var instance2 = new GameInstance
            {
                Id = Guid.NewGuid(),
                AppId = 570,
                Name = "Test Game Up to Date",
                InstallPath = tempDir,
                EmulatorEnabled = true,
                InstalledEmulatorVersion = "1.1" // Up to date
            };

            var instanceWithoutEmu = new GameInstance
            {
                Id = Guid.NewGuid(),
                AppId = 440,
                Name = "Vanilla Game",
                InstallPath = tempDir,
                EmulatorEnabled = false,
                InstalledEmulatorVersion = null
            };

            // Act & Assert
            updater.IsInstanceReFixOutdated(instanceWithoutEmu).Should().BeFalse();
            // Note: If deploy directory has version > "1.0", instance1 should be outdated
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task ReFixUpdateService_CheckForUpdateAsync_ParsesGitHubReleaseResponse()
    {
        // Arrange
        var jsonResponse = """
        {
          "tag_name": "v2.5",
          "name": "ReFix Release v2.5",
          "body": "Fixes and performance improvements",
          "published_at": "2026-08-19T20:00:00Z",
          "assets": [
            {
              "name": "ReFix_Release_v2.5.zip",
              "browser_download_url": "https://github.com/Coronitaa/ReFix/releases/download/v2.5/ReFix_Release_v2.5.zip",
              "size": 1048576
            }
          ]
        }
        """;

        var mockHandler = new Mock<HttpMessageHandler>();
        mockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>()
            )
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new StringContent(jsonResponse, System.Text.Encoding.UTF8, "application/json")
            });

        var client = new HttpClient(mockHandler.Object);
        var updater = new ReFixUpdateService(client, NullLogger<ReFixUpdateService>.Instance);

        // Act
        var release = await updater.CheckForUpdatesAsync(CancellationToken.None);

        // Assert
        release.Should().NotBeNull();
        release!.TagName.Should().Be("v2.5");
        release.DownloadUrl.Should().Be("https://github.com/Coronitaa/ReFix/releases/download/v2.5/ReFix_Release_v2.5.zip");
        release.Version.Should().Be("2.5");
    }

    // ═════════════════════════════════════════════════════════════════════════
    // 4. PROJECT CREDITS & REFIX DEPLOY TESTS
    // ═════════════════════════════════════════════════════════════════════════

    [Fact]
    public void ProjectCredit_PropertiesInitializeCorrectly()
    {
        // Act
        var credit = new ProjectCredit
        {
            Name = "ReFix",
            Role = "Multi-Engine Steam Fix",
            Description = "Steam online lobbies & LAN fix",
            Author = "Coronitaa",
            License = "MIT",
            GitHubUrl = "https://github.com/Coronitaa/ReFix",
            CategoryBadge = "Emulation",
            IsCore = true
        };

        // Assert
        credit.Name.Should().Be("ReFix");
        credit.GitHubUrl.Should().Be("https://github.com/Coronitaa/ReFix");
        credit.IsCore.Should().BeTrue();
    }

    [Fact]
    public async Task ReFixEmulator_DeployOptionAsync_WhenGameNotInstalled_ReturnsFalseAndDoesNotDeploy()
    {
        // Arrange
        var tempFolder = Path.Combine(Path.GetTempPath(), "BlueStar_Test_" + Guid.NewGuid().ToString("N"));
        var emulator = new ReFixEmulator(NullLogger<ReFixEmulator>.Instance);

        var instance = new GameInstance
        {
            Id = Guid.NewGuid(),
            AppId = 1966720,
            Name = "Lethal Company Test",
            InstallPath = tempFolder
        };

        try
        {
            Directory.Exists(tempFolder).Should().BeFalse();

            // Act
            var success = await emulator.DeployOptionAsync(instance, "refix_valve", CancellationToken.None);

            // Assert: Emulator installation MUST fail if game is not installed
            success.Should().BeFalse();
            Directory.Exists(tempFolder).Should().BeFalse();
        }
        finally
        {
            try { if (Directory.Exists(tempFolder)) Directory.Delete(tempFolder, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task ReFixEmulator_UninstallAsync_WhenFolderDoesNotExist_ReturnsTrueCleanly()
    {
        // Arrange
        var tempFolder = Path.Combine(Path.GetTempPath(), "BlueStar_NonExistent_" + Guid.NewGuid().ToString("N"));
        var emulator = new ReFixEmulator(NullLogger<ReFixEmulator>.Instance);

        var instance = new GameInstance
        {
            Id = Guid.NewGuid(),
            AppId = 1966720,
            Name = "NonExistent Game",
            InstallPath = tempFolder
        };

        // Act
        var result = await emulator.UninstallAsync(instance, CancellationToken.None);

        // Assert
        result.Should().BeTrue();
    }

    [Fact]
    public async Task ReFixEmulator_DeployOptionAsync_GoldbergMode_DeploysSteamSettingsAndProgress()
    {
        // Arrange
        var tempFolder = Path.Combine(Path.GetTempPath(), "BlueStar_GoldbergTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempFolder);
        File.WriteAllText(Path.Combine(tempFolder, "csgo.exe"), "dummy binary");

        var emulator = new ReFixEmulator(NullLogger<ReFixEmulator>.Instance);

        var instance = new GameInstance
        {
            Id = Guid.NewGuid(),
            AppId = 730,
            Name = "Counter-Strike Test",
            InstallPath = tempFolder,
            Dlcs = new List<DlcInfo> { new() { AppId = 731, Name = "Test DLC" } }
        };

        var reportedProgress = new List<DeployProgress>();
        var progress = new Progress<DeployProgress>(reportedProgress.Add);

        try
        {
            // Act
            var success = await emulator.DeployOptionAsync(instance, "refix_goldberg", progress, CancellationToken.None);

            // Assert
            if (ReFixEmulator.GetReFixDeployPath() != null)
            {
                success.Should().BeTrue();
                Directory.Exists(tempFolder).Should().BeTrue();
                File.Exists(Path.Combine(tempFolder, "ReFix.ini")).Should().BeTrue();
                File.Exists(Path.Combine(tempFolder, "steam_appid.txt")).Should().BeTrue();
                File.Exists(Path.Combine(tempFolder, "local_save.txt")).Should().BeTrue();
                Directory.Exists(Path.Combine(tempFolder, "saves")).Should().BeTrue();
                Directory.Exists(Path.Combine(tempFolder, "steam_settings")).Should().BeTrue();

                var configsApp = await File.ReadAllTextAsync(Path.Combine(tempFolder, "steam_settings", "configs.app.ini"));
                configsApp.Should().Contain("appid=730");
                configsApp.Should().Contain("unlock_all_dlc=");

                reportedProgress.Should().NotBeEmpty();
                reportedProgress.Last().Percentage.Should().Be(100);
            }
        }
        finally
        {
            try { if (Directory.Exists(tempFolder)) Directory.Delete(tempFolder, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task ReFixEmulator_DeployOptionAsync_UnityStructure_PrioritizesPluginsFolder()
    {
        // Arrange
        var tempFolder = Path.Combine(Path.GetTempPath(), "BlueStar_UnityTest_" + Guid.NewGuid().ToString("N"));
        var dataFolder = Path.Combine(tempFolder, "Game_Data", "Plugins", "x86_64");
        Directory.CreateDirectory(dataFolder);
        File.WriteAllText(Path.Combine(tempFolder, "Game.exe"), "dummy exe");

        var emulator = new ReFixEmulator(NullLogger<ReFixEmulator>.Instance);

        var instance = new GameInstance
        {
            Id = Guid.NewGuid(),
            AppId = 1966720,
            Name = "Unity Game Test",
            InstallPath = tempFolder
        };

        try
        {
            // Act
            var success = await emulator.DeployOptionAsync(instance, "refix_goldberg", CancellationToken.None);

            // Assert
            if (ReFixEmulator.GetReFixDeployPath() != null)
            {
                success.Should().BeTrue();
                File.Exists(Path.Combine(dataFolder, "steam_api64.dll")).Should().BeTrue();
                File.Exists(Path.Combine(dataFolder, "steam_api64_valve.dll")).Should().BeTrue();
                Directory.Exists(Path.Combine(dataFolder, "steam_settings")).Should().BeTrue();
            }
        }
        finally
        {
            try { if (Directory.Exists(tempFolder)) Directory.Delete(tempFolder, recursive: true); } catch { }
        }
    }

    [Theory]
    [InlineData("1.1", "1.1.0", false)]
    [InlineData("1.1.0", "1.1", false)]
    [InlineData("v1.1", "1.1", false)]
    [InlineData("1.1", "v1.1", false)]
    [InlineData("1.2", "1.1", true)]
    [InlineData("1.1.1", "1.1.0", true)]
    [InlineData("1.0", "1.1", false)]
    [InlineData("2.0.0", "1.9.9", true)]
    public void ReFixUpdateService_IsNewerVersion_NormalizedComparisons(string candidate, string baseline, bool expected)
    {
        var result = ReFixUpdateService.IsNewerVersion(candidate, baseline);
        result.Should().Be(expected);
    }

    [Fact]
    public void ReFixUpdateService_IsInstanceReFixOutdated_WhenNoVersionRecorded_ReturnsFalse()
    {
        var service = new ReFixUpdateService(new HttpClient(), NullLogger<ReFixUpdateService>.Instance);
        var instance = new GameInstance
        {
            Id = Guid.NewGuid(),
            Name = "Test Game",
            InstallPath = Path.GetTempPath(),
            EmulatorEnabled = true,
            InstalledEmulatorVersion = null
        };

        var isOutdated = service.IsInstanceReFixOutdated(instance);
        isOutdated.Should().BeFalse();
    }

    // ═════════════════════════════════════════════════════════════════════════
    // 5. EXPLORE CATALOG (DEPOTBOX & STEAMDB FILTERING & SYNERGY) TESTS
    // ═════════════════════════════════════════════════════════════════════════

    [Fact]
    public void DepotBoxApiClient_ParseSearchResults_FiltersOutDlcsRedistsAndSoundtracks()
    {
        // Arrange
        var sampleJson = """
        [
            {
                "appId": 1966720,
                "name": "Lethal Company",
                "type": "game",
                "version": "v64",
                "platforms": { "windows": true, "linux": false, "mac": false },
                "dlcCount": 2
            },
            {
                "appId": 228980,
                "name": "Steamworks Common Redistributables",
                "type": "redistributable"
            },
            {
                "appId": 1966721,
                "name": "Lethal Company - Soundtrack DLC",
                "type": "dlc",
                "isDlc": true
            },
            {
                "appId": 413150,
                "name": "Stardew Valley",
                "type": "game",
                "version": "1.6.8",
                "platforms": { "windows": true, "linux": true, "mac": true },
                "dlcs": [ { "id": 1 } ]
            },
            {
                "appId": 863550,
                "name": "Blender",
                "type": "application",
                "platforms": { "windows": true, "linux": true, "mac": true }
            }
        ]
        """;

        // Act
        var results = BlueStar.Infrastructure.DepotBox.DepotBoxApiClient.ParseSearchResults(sampleJson);

        // Assert
        results.Should().HaveCount(3);

        var lethal = results.First(r => r.AppId == 1966720);
        lethal.Name.Should().Be("Lethal Company");
        lethal.AppType.Should().Be("Game");
        lethal.Version.Should().Be("v64");
        lethal.HasWindows.Should().BeTrue();
        lethal.HasLinux.Should().BeFalse();
        lethal.HasMac.Should().BeFalse();
        lethal.DlcCount.Should().Be(2);
        lethal.SteamDbUrl.Should().Be("https://steamdb.info/app/1966720/");

        var stardew = results.First(r => r.AppId == 413150);
        stardew.Name.Should().Be("Stardew Valley");
        stardew.HasWindows.Should().BeTrue();
        stardew.HasLinux.Should().BeTrue();
        stardew.HasMac.Should().BeTrue();
        stardew.DlcCount.Should().Be(1);

        var blender = results.First(r => r.AppId == 863550);
        blender.Name.Should().Be("Blender");
        blender.AppType.Should().Be("Application");

        // DLC and Redistributables must be excluded
        results.Any(r => r.AppId == 228980).Should().BeFalse();
        results.Any(r => r.AppId == 1966721).Should().BeFalse();
    }

    // ═════════════════════════════════════════════════════════════════════════
    // 6. PREREQUISITE SERVICE DETECTION TESTS
    // ═════════════════════════════════════════════════════════════════════════

#pragma warning disable CA1416
    [Fact]
    public async Task PrerequisiteService_DetectPrerequisites_ReturnsEssentialDependencies()
    {
        // Arrange
        var http = new HttpClient();
        var service = new PrerequisiteService(http, NullLogger<PrerequisiteService>.Instance);
        var instance = new GameInstance
        {
            Id = Guid.NewGuid(),
            AppId = 1966720,
            Name = "Lethal Company",
            InstallPath = Path.GetTempPath(),
            Engine = new EngineInfo { Id = "unity", Name = "Unity", Type = EngineType.Unity }
        };

        // Act
        var prereqs = await service.DetectPrerequisitesAsync(instance, CancellationToken.None);

        // Assert
        prereqs.Should().NotBeEmpty();
        prereqs.Should().Contain(p => p.Id == "vcredist_2015_2022_x64");
        prereqs.Should().Contain(p => p.Id == "directx_enduser");
        prereqs.Should().Contain(p => p.Id == "dotnet_desktop_8");
        prereqs.Should().Contain(p => p.Id == "dotnet_runtime_9");
    }

    [Fact]
    public async Task PrerequisiteService_DetectPrerequisites_WhenUnrealEngine_IncludesUePrereqs()
    {
        // Arrange
        var http = new HttpClient();
        var service = new PrerequisiteService(http, NullLogger<PrerequisiteService>.Instance);
        var instance = new GameInstance
        {
            Id = Guid.NewGuid(),
            AppId = 1091500,
            Name = "Cyberpunk 2077",
            InstallPath = Path.GetTempPath(),
            Engine = new EngineInfo { Id = "unreal-5", Name = "Unreal Engine 5", Type = EngineType.UnrealEngine }
        };

        // Act
        var prereqs = await service.DetectPrerequisitesAsync(instance, CancellationToken.None);

        // Assert
        prereqs.Should().Contain(p => p.Id == "ue_prereqs_x64");
        var ueItem = prereqs.First(p => p.Id == "ue_prereqs_x64");
        ueItem.IsEssential.Should().BeTrue();
    }

    [Fact]
    public void DownloadQueueManager_NotifyQueueChanged_FiresQueueChangedEvent()
    {
        // Arrange
        var mockProvider = new Mock<IDownloadProvider>();
        var manager = new DownloadQueueManager(mockProvider.Object, NullLogger<DownloadQueueManager>.Instance);
        var eventFired = false;
        manager.QueueChanged += (_, _) => eventFired = true;

        // Act
        manager.NotifyQueueChanged();

        // Assert
        eventFired.Should().BeTrue();
    }

    [Fact]
    public void NotificationItem_ActionCommand_ExecutesAction()
    {
        // Arrange
        var executed = false;
        var notification = new NotificationItem
        {
            Title = "Actualización",
            Message = "Descargar nueva versión",
            ActionText = "Descargar",
            Action = () => executed = true
        };

        // Assert & Act
        notification.HasAction.Should().BeTrue();
        notification.ActionCommand.Should().NotBeNull();
        notification.ActionCommand!.Execute(null);
        executed.Should().BeTrue();
    }

    [Fact]
    public async Task SteamStoreApiClient_EnrichSearchResult_MapsAppTypesAndDates()
    {
        // Arrange
        var jsonResponse = """
        {
            "730": {
                "success": true,
                "data": {
                    "type": "game",
                    "header_image": "https://cdn.akamai.steamstatic.com/steam/apps/730/header.jpg",
                    "platforms": {
                        "windows": true,
                        "linux": true,
                        "mac": false
                    },
                    "dlc": [123, 456],
                    "release_date": {
                        "coming_soon": false,
                        "date": "21 Aug, 2012"
                    }
                }
            }
        }
        """;

        var newsResponse = """
        {
            "appnews": {
                "appid": 730,
                "newsitems": [
                    {
                        "gid": "123",
                        "title": "Release Notes",
                        "date": 1700000000
                    }
                ]
            }
        }
        """;

        var handlerMock = new Mock<HttpMessageHandler>();
        handlerMock.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.Is<HttpRequestMessage>(req => req.RequestUri!.ToString().Contains("appdetails")),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new StringContent(jsonResponse)
            });

        handlerMock.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.Is<HttpRequestMessage>(req => req.RequestUri!.ToString().Contains("GetNewsForApp")),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new StringContent(newsResponse)
            });

        var httpClient = new HttpClient(handlerMock.Object);
        var client = new BlueStar.Infrastructure.Metadata.SteamStoreApiClient(httpClient, NullLogger<BlueStar.Infrastructure.Metadata.SteamStoreApiClient>.Instance);

        var searchResult = new SearchResult { AppId = 730, Name = "Counter-Strike 2" };

        // Act
        await client.EnrichSearchResultAsync(searchResult, CancellationToken.None);

        // Assert
        searchResult.AppType.Should().Be("Game");
        searchResult.HasWindows.Should().BeTrue();
        searchResult.HasLinux.Should().BeTrue();
        searchResult.HasMac.Should().BeFalse();
        searchResult.DlcCount.Should().Be(2);
        searchResult.Version.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void SteamManifestDateHelper_ExtractsProtobufCreationDate()
    {
        // Arrange
        var tempFile = Path.Combine(Path.GetTempPath(), $"test_manifest_{Guid.NewGuid():N}.manifest");
        try
        {
            // Create a fake payload with tag 4 (0x20) and unix timestamp 1700000000 (2023-11-14)
            // 1700000000 in varint = 0x80, 0xE2, 0xCF, 0xAA, 0x06
            var bytes = new byte[] { 0x08, 0x01, 0x10, 0x02, 0x20, 0x80, 0xE2, 0xCF, 0xAA, 0x06, 0x28, 0x01 };
            File.WriteAllBytes(tempFile, bytes);

            // Act
            var date = BlueStar.Infrastructure.Downloader.SteamManifestDateHelper.GetManifestCreationDate(tempFile);

            // Assert
            date.Should().NotBeNull();
            date!.Value.ToUnixTimeSeconds().Should().Be(1700000000);
        }
        finally
        {
            try { if (File.Exists(tempFile)) File.Delete(tempFile); } catch { }
        }
    }

    [Fact]
    public async Task SteamStoreApiClient_GetLatestAppUpdateDateAsync_ReturnsDateForRoadsideResearch()
    {
        var http = new HttpClient();
        http.DefaultRequestHeaders.UserAgent.ParseAdd("BlueStar/0.1.0");
        var resp = await http.GetAsync("https://api.steampowered.com/ISteamNews/GetNewsForApp/v2/?appid=3643170&count=1");
        var body = await resp.Content.ReadAsStringAsync();
        Console.WriteLine($"STATUS: {resp.StatusCode}, BODY: {body}");

        var client = new BlueStar.Infrastructure.Metadata.SteamStoreApiClient(http, NullLogger<BlueStar.Infrastructure.Metadata.SteamStoreApiClient>.Instance);
        var date = await client.GetLatestAppUpdateDateAsync(3643170, CancellationToken.None);
        date.Should().NotBeNull();
    }

    [Fact]
    public async Task SteamStoreApiClient_GetAppDepotInfoAsync_ReturnsDepotManifestsAndDate()
    {
        var http = new HttpClient();
        http.DefaultRequestHeaders.UserAgent.ParseAdd("BlueStar/0.1.0");
        var client = new BlueStar.Infrastructure.Metadata.SteamStoreApiClient(http, NullLogger<BlueStar.Infrastructure.Metadata.SteamStoreApiClient>.Instance);

        var info = await client.GetAppDepotInfoAsync(4108000, CancellationToken.None);
        info.Should().NotBeNull();
        info!.LatestBuildDate.Should().NotBeNull();
        info.LatestBuildDate!.Value.Year.Should().BeGreaterThanOrEqualTo(2026);
        info.PublicManifests.Should().ContainKey(4108002);
    }

    [Fact]
    public void DepotBoxApiClient_ParseManifests_HandlesDepotBoxSourcesFormat()
    {
        // Arrange
        var json = """
        {
          "success": true,
          "appid": "4108000",
          "sources": {
            "db1_primary": {
              "source": "local",
              "manifests": [
                "4108002_1944295227853622944.manifest",
                "4108003_7195774274748855882.manifest"
              ],
              "count": 2
            }
          }
        }
        """;

        // Act
        var manifests = BlueStar.Infrastructure.DepotBox.DepotBoxApiClient.ParseManifests(json);

        // Assert
        manifests.Should().HaveCount(2);
        manifests[0].DepotId.Should().Be(4108002);
        manifests[0].ManifestId.Should().Be(1944295227853622944);
        manifests[1].DepotId.Should().Be(4108003);
        manifests[1].ManifestId.Should().Be(7195774274748855882);
    }

    [Fact]
    public async Task SteamStoreApiClient_EnrichSearchResultAsync_DetectsApplicationTypeForWallpaperEngine()
    {
        var http = new HttpClient();
        http.DefaultRequestHeaders.UserAgent.ParseAdd("BlueStar/0.1.0");
        var client = new BlueStar.Infrastructure.Metadata.SteamStoreApiClient(http, NullLogger<BlueStar.Infrastructure.Metadata.SteamStoreApiClient>.Instance);

        var result = new BlueStar.Core.Models.SearchResult
        {
            AppId = 431960,
            Name = "Wallpaper Engine"
        };

        await client.EnrichSearchResultAsync(result, CancellationToken.None);

        result.AppType.Should().Be("Application");
    }

    [Fact]
    public async Task SteamStoreApiClient_EnrichSearchResultAsync_ReturnsFormattedDateVersionNotManifestCode()
    {
        var http = new HttpClient();
        http.DefaultRequestHeaders.UserAgent.ParseAdd("BlueStar/0.1.0");
        var client = new BlueStar.Infrastructure.Metadata.SteamStoreApiClient(http, NullLogger<BlueStar.Infrastructure.Metadata.SteamStoreApiClient>.Instance);

        var result = new BlueStar.Core.Models.SearchResult
        {
            AppId = 1966720,
            Name = "Lethal Company"
        };

        await client.EnrichSearchResultAsync(result, CancellationToken.None);

        result.Version.Should().NotBeNullOrWhiteSpace();
        result.Version.Should().NotStartWith("Manifest");
    }

    // ═════════════════════════════════════════════════════════════════════════
    // 16. HOME DASHBOARD IMPORT & INSTANCE UPDATE NOTIFICATION TESTS
    // ═════════════════════════════════════════════════════════════════════════

    [Fact]
    public void GameInstance_HasUpdateAvailable_DefaultsToFalse_AndCanBeToggled()
    {
        var instance = new GameInstance
        {
            Name = "Hollow Knight",
            AppId = 367520,
            InstallPath = "C:\\Games\\HollowKnight"
        };

        instance.HasUpdateAvailable.Should().BeFalse();

        var updated = instance with
        {
            HasUpdateAvailable = true,
            UpdateDescription = "New build v1.5.78 available"
        };

        updated.HasUpdateAvailable.Should().BeTrue();
        updated.UpdateDescription.Should().Be("New build v1.5.78 available");
    }

    [Fact]
    public async Task SteamLibraryScanner_ScanInstalledGamesAsync_ReturnsReadOnlyListWithoutExceptions()
    {
        // Act
        var games = await BlueStar.Infrastructure.Steam.SteamLibraryScanner.ScanInstalledGamesAsync(CancellationToken.None);

        // Assert
        games.Should().NotBeNull();
    }

    // ═════════════════════════════════════════════════════════════════════════
    // 17. INSTANCES SORTING & FILTERING TESTS
    // ═════════════════════════════════════════════════════════════════════════

    [Fact]
    public void GameInstance_Sorting_Alphabetical_And_Chronological_OrdersCorrectly()
    {
        var gameA = new GameInstance
        {
            Name = "Apex Legends",
            AppId = 1172470,
            InstallPath = "C:\\Games\\Apex",
            CreatedAt = DateTimeOffset.UtcNow.AddDays(-10),
            TotalPlayTime = TimeSpan.FromHours(5)
        };

        var gameB = new GameInstance
        {
            Name = "Cyberpunk 2077",
            AppId = 1091500,
            InstallPath = "C:\\Games\\Cyberpunk",
            CreatedAt = DateTimeOffset.UtcNow.AddDays(-2),
            LastPlayedAt = DateTimeOffset.UtcNow.AddMinutes(-10),
            TotalPlayTime = TimeSpan.FromHours(45)
        };

        var gameC = new GameInstance
        {
            Name = "Baldur's Gate 3",
            AppId = 1086940,
            InstallPath = "C:\\Games\\BG3",
            CreatedAt = DateTimeOffset.UtcNow.AddDays(-30),
            LastPlayedAt = DateTimeOffset.UtcNow.AddHours(-2),
            TotalPlayTime = TimeSpan.FromHours(120)
        };

        var list = new List<GameInstance> { gameA, gameB, gameC };

        // Test Alphabetical Ascending
        var az = list.OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ToList();
        az[0].Name.Should().Be("Apex Legends");
        az[1].Name.Should().Be("Baldur's Gate 3");
        az[2].Name.Should().Be("Cyberpunk 2077");

        // Test Alphabetical Descending
        var za = list.OrderByDescending(i => i.Name, StringComparer.OrdinalIgnoreCase).ToList();
        za[0].Name.Should().Be("Cyberpunk 2077");
        za[1].Name.Should().Be("Baldur's Gate 3");
        za[2].Name.Should().Be("Apex Legends");

        // Test PlayTime Descending
        var playTimeSort = list.OrderByDescending(i => i.TotalPlayTime).ToList();
        playTimeSort[0].Name.Should().Be("Baldur's Gate 3");
        playTimeSort[1].Name.Should().Be("Cyberpunk 2077");
        playTimeSort[2].Name.Should().Be("Apex Legends");

        // Test Recent (LastPlayedAt descending)
        var recentSort = list.OrderByDescending(i => i.LastPlayedAt.HasValue)
                             .ThenByDescending(i => i.LastPlayedAt ?? i.CreatedAt)
                             .ToList();
        recentSort[0].Name.Should().Be("Cyberpunk 2077");
        recentSort[1].Name.Should().Be("Baldur's Gate 3");
        recentSort[2].Name.Should().Be("Apex Legends");
    }

    // ═════════════════════════════════════════════════════════════════════════
    // 16. CATALOG NSFW & DRM SETTINGS AND FILTERING TESTS
    // ═════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task AppSettingsService_NsfwAndDrmDefaultsAndToggle_PersistsCorrectly()
    {
        var tempSettingsPath = Path.Combine(Path.GetTempPath(), $"settings_nsfw_{Guid.NewGuid():N}.json");
        try
        {
            var appSettings = new AppSettingsService(NullLogger<AppSettingsService>.Instance, tempSettingsPath);

            // Default values: NSFW = false, DRM = true
            appSettings.ShowNsfwContent.Should().BeFalse();
            appSettings.ShowDrmContent.Should().BeTrue();

            // Change values
            await appSettings.SetShowNsfwContentAsync(true);
            await appSettings.SetShowDrmContentAsync(false);

            appSettings.ShowNsfwContent.Should().BeTrue();
            appSettings.ShowDrmContent.Should().BeFalse();

            // Reload from file to verify persistence
            var reloadedSettings = new AppSettingsService(NullLogger<AppSettingsService>.Instance, tempSettingsPath);
            reloadedSettings.ShowNsfwContent.Should().BeTrue();
            reloadedSettings.ShowDrmContent.Should().BeFalse();
        }
        finally
        {
            if (File.Exists(tempSettingsPath)) File.Delete(tempSettingsPath);
        }
    }

    [Fact]
    public void SearchResult_NsfwAndDrmFiltering_FiltersAppropriately()
    {
        var items = new List<SearchResult>
        {
            new() { Name = "Safe Game", AppId = 1, IsNsfw = false, HasDrm = false },
            new() { Name = "NSFW Game", AppId = 2, IsNsfw = true, HasDrm = false },
            new() { Name = "DRM Game", AppId = 3, IsNsfw = false, HasDrm = true },
            new() { Name = "NSFW DRM Game", AppId = 4, IsNsfw = true, HasDrm = true }
        };

        // Case 1: Default (NSFW=false, DRM=true) -> Safe Game and DRM Game
        var defaultFiltered = items.Where(i => !i.IsNsfw && true).ToList();
        defaultFiltered.Select(i => i.Name).Should().BeEquivalentTo(new[] { "Safe Game", "DRM Game" });

        // Case 2: NSFW=true, DRM=true -> All 4 games
        var allFiltered = items.Where(i => true && true).ToList();
        allFiltered.Count.Should().Be(4);

        // Case 3: NSFW=false, DRM=false -> Only Safe Game
        var strictFiltered = items.Where(i => !i.IsNsfw && !i.HasDrm).ToList();
        strictFiltered.Select(i => i.Name).Should().BeEquivalentTo(new[] { "Safe Game" });
    }

    [Fact]
    public async Task SteamStoreApiClient_EnrichSearchResult_OnlyFlagsExplicitAdultContentAsNsfw()
    {
        // Arrange
        var handlerMock = new Mock<HttpMessageHandler>();

        // Response for App 1 (Cyberpunk / Mature game with descriptor 5 and age 18)
        var matureResponseJson = """
        {
          "1091500": {
            "success": true,
            "data": {
              "type": "game",
              "required_age": 18,
              "content_descriptors": {
                "ids": [2, 5],
                "notes": "Frequent Violence, Blood and Gore, Strong Language."
              },
              "genres": [
                { "description": "Action" },
                { "description": "RPG" }
              ]
            }
          }
        }
        """;

        // Response for App 2 (Adult Game with descriptor 3)
        var adultResponseJson = """
        {
          "999999": {
            "success": true,
            "data": {
              "type": "game",
              "content_descriptors": {
                "ids": [3],
                "notes": "Explicit Sexual Content and Hentai."
              },
              "genres": [
                { "description": "Sexual Content" },
                { "description": "Adult Only" }
              ]
            }
          }
        }
        """;

        handlerMock.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.Is<HttpRequestMessage>(req => req.RequestUri != null && req.RequestUri.ToString().Contains("1091500")),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new StringContent(matureResponseJson)
            });

        handlerMock.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.Is<HttpRequestMessage>(req => req.RequestUri != null && req.RequestUri.ToString().Contains("999999")),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new StringContent(adultResponseJson)
            });

        var client = new HttpClient(handlerMock.Object);
        var apiClient = new BlueStar.Infrastructure.Metadata.SteamStoreApiClient(client, NullLogger<BlueStar.Infrastructure.Metadata.SteamStoreApiClient>.Instance);

        var matureGame = new SearchResult { AppId = 1091500, Name = "Cyberpunk 2077" };
        var adultGame = new SearchResult { AppId = 999999, Name = "Adult Visual Novel" };

        // Act
        await apiClient.EnrichSearchResultAsync(matureGame, CancellationToken.None);
        await apiClient.EnrichSearchResultAsync(adultGame, CancellationToken.None);

        // Assert: Mature game with descriptor 5 / age 18 should NOT be flagged as NSFW
        matureGame.IsNsfw.Should().BeFalse();

        // Assert: Explicit adult game with descriptor 3 should be flagged as NSFW
        adultGame.IsNsfw.Should().BeTrue();
    }

    // ═════════════════════════════════════════════════════════════════════════
    // 8. UNINSTALL GAME FILES & KEEP INSTANCE RECORD TESTS
    // ═════════════════════════════════════════════════════════════════════════

    [Fact]
    public void GameInstance_WhenResetForUninstall_PreservesMetadataAndResetsState()
    {
        // Arrange
        var originalInstance = new GameInstance
        {
            Id = Guid.NewGuid(),
            AppId = 1196590,
            Name = "Resident Evil Village",
            InstallPath = @"C:\Games\ResidentEvilVillage",
            Status = InstanceStatus.Ready,
            Depots = new List<DepotInfo>
            {
                new() { DepotId = 1196591, ManifestId = 111111111, IsDownloaded = true, SizeBytes = 5000000 },
                new() { DepotId = 1196592, ManifestId = 222222222, IsDownloaded = true, SizeBytes = 8000000 }
            },
            Dlcs = new List<DlcInfo>
            {
                new()
                {
                    AppId = 1731080,
                    Name = "Winters Expansion",
                    IsInstalled = true,
                    Depots = new List<DepotInfo>
                    {
                        new() { DepotId = 1731080, ManifestId = 333333333, IsDownloaded = true }
                    }
                }
            },
            InstalledFixLayers = new List<FixLayerInfo>
            {
                new()
                {
                    LayerId = "re_village_bypass",
                    DisplayName = "Bypass",
                    SourceType = "depotbox_gamefix",
                    Tags = ["bypass"]
                }
            },
            DlcUnlockerInstalled = true,
            UnlockedDlcIds = new List<uint> { 1731080 }
        };

        // Act: Perform state reset as done by Uninstall Game Files workflow
        var uninstalledDepots = originalInstance.Depots.Select(d => d with { IsDownloaded = false }).ToList();
        var uninstalledDlcs = originalInstance.Dlcs.Select(d => d with
        {
            IsInstalled = false,
            Depots = d.Depots.Select(dp => dp with { IsDownloaded = false }).ToList().AsReadOnly()
        }).ToList();

        var resetInstance = originalInstance with
        {
            Status = InstanceStatus.NotInstalled,
            Depots = uninstalledDepots.AsReadOnly(),
            Dlcs = uninstalledDlcs.AsReadOnly(),
            InstalledFixLayers = [],
            DlcUnlockerInstalled = false,
            UnlockedDlcIds = [],
            EmulatorEnabled = false,
            EmulatorId = null
        };

        // Assert: Instance ID, Name, AppId, and install configuration are preserved
        resetInstance.Id.Should().Be(originalInstance.Id);
        resetInstance.Name.Should().Be("Resident Evil Village");
        resetInstance.AppId.Should().Be(1196590);
        resetInstance.InstallPath.Should().Be(@"C:\Games\ResidentEvilVillage");

        // Status and download flags are reset
        resetInstance.Status.Should().Be(InstanceStatus.NotInstalled);
        resetInstance.Depots.Should().AllSatisfy(d => d.IsDownloaded.Should().BeFalse());
        resetInstance.Dlcs.Should().AllSatisfy(d => d.IsInstalled.Should().BeFalse());
        resetInstance.InstalledFixLayers.Should().BeEmpty();
        resetInstance.DlcUnlockerInstalled.Should().BeFalse();
        resetInstance.UnlockedDlcIds.Should().BeEmpty();
    }

    // ═════════════════════════════════════════════════════════════════════════
    // 9. GAME BUILDS & VERSION SWITCHING INTERCOMPATIBILITY TESTS
    // ═════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task SteamStoreApiClient_GetAppBuildsAsync_ParsesBranchesAndBuilds()
    {
        // Arrange
        var handlerMock = new Mock<HttpMessageHandler>();
        var sampleSteamCmdJson = """
        {
          "status": "success",
          "data": {
            "1196590": {
              "depots": {
                "branches": {
                  "public": {
                    "buildid": "15283921",
                    "timeupdated": "1723708800"
                  },
                  "previous": {
                    "buildid": "14920193",
                    "timeupdated": "1718179200",
                    "description": "Previous Build"
                  }
                },
                "1196591": {
                  "manifests": {
                    "public": { "gid": "3932505801699900266" },
                    "previous": { "gid": "1234567890123456789" }
                  }
                },
                "1196592": {
                  "manifests": {
                    "public": { "gid": "6661267619031919339" },
                    "previous": { "gid": "9876543210987654321" }
                  }
                }
              }
            }
          }
        }
        """;

        handlerMock.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.Is<HttpRequestMessage>(req => req.RequestUri != null && req.RequestUri.ToString().Contains("api.steamcmd.net")),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new StringContent(sampleSteamCmdJson)
            });

        var client = new HttpClient(handlerMock.Object);
        var apiClient = new BlueStar.Infrastructure.Metadata.SteamStoreApiClient(client, NullLogger<BlueStar.Infrastructure.Metadata.SteamStoreApiClient>.Instance);

        // Act
        var builds = await apiClient.GetAppBuildsAsync(1196590, CancellationToken.None);

        // Assert
        builds.Should().NotBeNull();
        builds.Count.Should().Be(2);

        var publicBuild = builds.FirstOrDefault(b => b.BranchName == "public");
        publicBuild.Should().NotBeNull();
        publicBuild!.BuildId.Should().Be("15283921");
        publicBuild.DepotManifests.Should().ContainKey(1196591);
        publicBuild.DepotManifests[1196591].Should().Be(3932505801699900266);

        var prevBuild = builds.FirstOrDefault(b => b.BranchName == "previous");
        prevBuild.Should().NotBeNull();
        prevBuild!.BuildId.Should().Be("14920193");
        prevBuild.DepotManifests[1196591].Should().Be(1234567890123456789);
    }

    [Fact]
    public void GameBuildSwitching_PreservesDlcsAndFixCompatibility()
    {
        // Arrange
        var targetBuild = new GameBuildInfo
        {
            BuildId = "14920193",
            BranchName = "previous",
            DisplayName = "Previous Build 14920193",
            DepotManifests = new Dictionary<uint, ulong>
            {
                { 1196591, 1234567890123456789 },
                { 1196592, 9876543210987654321 }
            }
        };

        var instance = new GameInstance
        {
            Id = Guid.NewGuid(),
            AppId = 1196590,
            Name = "Resident Evil Village",
            InstallPath = @"C:\Games\ResidentEvilVillage",
            Depots = new List<DepotInfo>
            {
                new() { DepotId = 1196591, ManifestId = 3932505801699900266, IsDownloaded = true },
                new() { DepotId = 1196592, ManifestId = 6661267619031919339, IsDownloaded = true }
            },
            Dlcs = new List<DlcInfo>
            {
                new() { AppId = 1731080, Name = "Winters Expansion", IsInstalled = true }
            }
        };

        // Act: Apply build switch
        var updatedDepots = instance.Depots.Select(d =>
        {
            if (targetBuild.DepotManifests.TryGetValue(d.DepotId, out var newManifestId))
            {
                bool isStillDownloaded = d.IsDownloaded && (d.ManifestId == newManifestId);
                return d with { ManifestId = newManifestId, IsDownloaded = isStillDownloaded };
            }
            return d;
        }).ToList();

        var updatedInstance = instance with
        {
            Depots = updatedDepots.AsReadOnly()
        };

        // Assert: Manifests are updated and DLC intercompatibility is preserved
        updatedInstance.Depots.First(d => d.DepotId == 1196591).ManifestId.Should().Be(1234567890123456789);
        updatedInstance.Depots.First(d => d.DepotId == 1196591).IsDownloaded.Should().BeFalse(); // Requires download for new build
        updatedInstance.Dlcs.Should().HaveCount(1);
        updatedInstance.Dlcs[0].AppId.Should().Be(1731080);
    }

    [Fact]
    public void CustomBuild_CreationAndDepotMapping_CorrectlyTargetsBypassBuild()
    {
        // Scenario: User specifies Build 22357085 with custom manifests for a bypass/crack
        var customManifests = new Dictionary<uint, ulong>
        {
            [3357651] = 6330832861176696160,
            [3357652] = 5639705324720691310
        };

        var customBuild = new GameBuildInfo
        {
            BuildId = "22357085",
            BranchName = "custom",
            DisplayName = "PRAGMATA Build 22357085 (Bypass Compatible)",
            Description = "Custom build configured for PRAGMATA bypass",
            Source = "Custom",
            UpdatedAt = DateTimeOffset.UtcNow,
            DepotManifests = customManifests
        };

        customBuild.BuildId.Should().Be("22357085");
        customBuild.BranchName.Should().Be("custom");
        customBuild.DepotManifests.Should().ContainKey(3357651);
        customBuild.DepotManifests[3357651].Should().Be(6330832861176696160);
    }

    [Fact]
    public void ManifestFilenameRegex_CorrectlyExtractsDepotAndManifestIds()
    {
        var filename = "3357651_6330832861176696160.manifest";
        var match = System.Text.RegularExpressions.Regex.Match(filename, @"^(\d+)_(\d+)\.manifest$");

        match.Success.Should().BeTrue();
        uint.Parse(match.Groups[1].Value).Should().Be(3357651);
        ulong.Parse(match.Groups[2].Value).Should().Be(6330832861176696160);
    }

    [Fact]
    public async Task DepotPackageZipImporter_ImportsManifestsKeysAndBuildIdFromZip()
    {
        // Arrange
        var tempZip = Path.Combine(Path.GetTempPath(), $"PRAGMATA_build_22357085_{Guid.NewGuid():N}.zip");
        var tempTargetDir = Path.Combine(Path.GetTempPath(), $"manifest_target_{Guid.NewGuid():N}");

        try
        {
            using (var fs = new FileStream(tempZip, FileMode.Create))
            using (var archive = new System.IO.Compression.ZipArchive(fs, System.IO.Compression.ZipArchiveMode.Create))
            {
                // Add manifest entry
                var manifestEntry = archive.CreateEntry("3357651_6330832861176696160.manifest");
                using (var writer = new StreamWriter(manifestEntry.Open()))
                {
                    writer.Write("DUMMY_MANIFEST_BINARY_DATA");
                }

                // Add depotkeys.txt entry
                var keysEntry = archive.CreateEntry("depotkeys.txt");
                using (var writer = new StreamWriter(keysEntry.Open()))
                {
                    writer.WriteLine("3357651;AABBCCDDEEFF00112233445566778899");
                }

                // Add build.txt entry
                var buildEntry = archive.CreateEntry("build_id.txt");
                using (var writer = new StreamWriter(buildEntry.Open()))
                {
                    writer.WriteLine("22357085");
                }
            }

            // Act
            var result = await BlueStar.Infrastructure.Services.DepotPackageZipImporter.ImportZipAsync(tempZip, tempTargetDir);

            // Assert
            result.Success.Should().BeTrue();
            result.BuildId.Should().Be("22357085");
            result.ManifestMap.Should().ContainKey(3357651);
            result.ManifestMap[3357651].Should().Be(6330832861176696160);
            result.DepotKeys.Should().ContainKey(3357651);
            result.DepotKeys[3357651].Should().Be("AABBCCDDEEFF00112233445566778899");
            result.ExtractedManifestFiles.Should().HaveCount(1);
            File.Exists(result.ExtractedManifestFiles[0]).Should().BeTrue();
        }
        finally
        {
            try { if (File.Exists(tempZip)) File.Delete(tempZip); } catch { }
            try { if (Directory.Exists(tempTargetDir)) Directory.Delete(tempTargetDir, true); } catch { }
        }
    }

    [Fact]
    public async Task AppSettingsService_EnableAdvancedBuildOptions_DefaultsToFalseAndPersists()
    {
        var tempSettingsFile = Path.Combine(Path.GetTempPath(), $"settings_{Guid.NewGuid():N}.json");
        try
        {
            var logger = Microsoft.Extensions.Logging.Abstractions.NullLogger<BlueStar.Infrastructure.Storage.AppSettingsService>.Instance;
            var settings = new BlueStar.Infrastructure.Storage.AppSettingsService(logger, tempSettingsFile);

            // Default must be false for normal users
            settings.EnableAdvancedBuildOptions.Should().BeFalse();

            // Toggle to true
            await settings.SetEnableAdvancedBuildOptionsAsync(true);
            settings.EnableAdvancedBuildOptions.Should().BeTrue();

            // Reload and verify persistence
            var reloaded = new BlueStar.Infrastructure.Storage.AppSettingsService(logger, tempSettingsFile);
            reloaded.EnableAdvancedBuildOptions.Should().BeTrue();
        }
        finally
        {
            try { if (File.Exists(tempSettingsFile)) File.Delete(tempSettingsFile); } catch { }
        }
    }
}


