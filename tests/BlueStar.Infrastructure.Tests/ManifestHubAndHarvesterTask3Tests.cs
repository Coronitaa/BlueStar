using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BlueStar.Core.Interfaces;
using BlueStar.Core.Models;
using BlueStar.Infrastructure.Providers.Manifest;
using BlueStar.Infrastructure.Services;
using BlueStar.Infrastructure.Storage;
using BlueStar.Infrastructure.Metadata;
using BlueStar.Infrastructure.Cache;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Moq.Protected;
using Xunit;

namespace BlueStar.Infrastructure.Tests;

public sealed class ManifestHubAndHarvesterTask3Tests : IDisposable
{
    private readonly string _testDir;
    private static readonly byte[] ValidProtobufHeader = [0xD0, 0x17, 0xF6, 0x71];

    public ManifestHubAndHarvesterTask3Tests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "BlueStar_Task3_Tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testDir))
            {
                Directory.Delete(_testDir, true);
            }
        }
        catch { }
    }

    #region 1. BuildSnapshot & ManifestHub Demand Tests

    [Fact]
    public void BuildSnapshot_Serialization_IncludesPatchDateAndChangedDepots()
    {
        var snapshot = new BuildSnapshot
        {
            AppId = 730,
            BuildId = 12345678,
            Branch = "public",
            Description = "Major Update",
            PatchDate = "2024-05-10",
            ChangedDepots = [731u, 732u],
            DepotManifests = new Dictionary<uint, ulong>
            {
                { 731, 111111UL },
                { 732, 222222UL },
                { 733, 333333UL }
            }
        };

        var json = JsonSerializer.Serialize(snapshot);
        json.Should().Contain("\"patchDate\":\"2024-05-10\"");
        json.Should().Contain("\"changedDepots\":[731,732]");

        var deserialized = JsonSerializer.Deserialize<BuildSnapshot>(json);
        deserialized.Should().NotBeNull();
        deserialized!.PatchDate.Should().Be("2024-05-10");
        deserialized.ChangedDepots.Should().Equal([731u, 732u]);
        deserialized.DepotManifests.Should().HaveCount(3);
    }

    [Fact]
    public async Task ManifestHubProvider_GetDemandAsync_ParsesDemandIndexSuccessfully()
    {
        var demandJson = """
        {
            "schemaVersion": "v1",
            "generatedAt": "2026-10-16T12:00:00Z",
            "records": [
                {
                    "appId": 730,
                    "depotId": 731,
                    "manifestId": 111111,
                    "appName": "Counter-Strike 2",
                    "priority": 10
                },
                {
                    "appId": 892970,
                    "depotId": 892971,
                    "manifestId": 222222,
                    "appName": "Valheim",
                    "priority": 5
                }
            ]
        }
        """;

        var handlerMock = new Mock<HttpMessageHandler>();
        handlerMock.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new StringContent(demandJson, Encoding.UTF8, "application/json")
            });

        var httpClient = new HttpClient(handlerMock.Object);
        var cacheMock = new Mock<IManifestCacheService>();
        var hubProvider = new ManifestHubProvider(
            httpClient,
            cacheMock.Object,
            NullLogger<ManifestHubProvider>.Instance,
            hubBaseUrl: "https://manifesthub.local");

        var demandIndex = await hubProvider.GetDemandAsync();

        demandIndex.Should().NotBeNull();
        demandIndex!.Records.Should().HaveCount(2);
        demandIndex.Records[0].AppId.Should().Be(730u);
        demandIndex.Records[0].DepotId.Should().Be(731u);
        demandIndex.Records[0].ManifestId.Should().Be(111111UL);
        demandIndex.Records[1].AppId.Should().Be(892970u);
    }

    #endregion

    #region 2. Differential Update by PatchDate & ChangedDepots

    [Fact]
    public async Task InstallationPlanner_CreateUpdatePlanAsync_WithSnapshot_DownloadsOnlyChangedDepots()
    {
        var cacheDir = Path.Combine(_testDir, "planner_cache");
        var manifestCache = new ManifestCacheService(NullLogger<ManifestCacheService>.Instance, cacheDir);
        var diffResolver = new DifferentialUpdateResolver(NullLogger<DifferentialUpdateResolver>.Instance);
        var planner = new InstallationPlanner(manifestCache, NullLogger<InstallationPlanner>.Instance, null, diffResolver);

        var instance = new GameInstance
        {
            Name = "Space Station Odyssey",
            AppId = 1000,
            InstallPath = Path.Combine(_testDir, "GameInstall"),
            InstalledManifestMap = new Dictionary<uint, ulong>
            {
                { 1001, 55555UL }, // Base content (unchanged)
                { 1002, 66666UL }, // Binaries (changed)
                { 1003, 77777UL }  // Audio (unchanged)
            }
        };

        var snapshot = new BuildSnapshot
        {
            AppId = 1000,
            BuildId = 998877,
            Branch = "public",
            Description = "Patch 1.2",
            PatchDate = "2024-06-15",
            ChangedDepots = [1002u], // ONLY Depot 1002 changed!
            DepotManifests = new Dictionary<uint, ulong>
            {
                { 1001, 55555UL },
                { 1002, 88888UL }, // Updated revision
                { 1003, 77777UL }
            }
        };

        var plan = await planner.CreateUpdatePlanAsync(instance, snapshot);

        plan.Should().NotBeNull();
        plan.IsDifferential.Should().BeTrue();

        // Depots 1001 and 1003 must be reused!
        plan.ReusedDepots.Should().HaveCount(2);
        plan.ReusedDepots.Select(d => d.DepotId).Should().BeEquivalentTo([1001u, 1003u]);

        // ONLY Depot 1002 must be scheduled for download!
        plan.DepotsToDownload.Should().HaveCount(1);
        plan.DepotsToDownload[0].DepotId.Should().Be(1002u);
        plan.DepotsToDownload[0].ManifestId.Should().Be(88888UL);
    }

    [Fact]
    public async Task BuildResolver_ResolveVersionByPatchDateAsync_FindsMatchingVersion()
    {
        var handlerMock = new Mock<HttpMessageHandler>();
        handlerMock.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new StringContent("{\"data\":{}}", Encoding.UTF8, "application/json")
            });

        var httpClient = new HttpClient(handlerMock.Object);
        var steamClient = new SteamStoreApiClient(httpClient, NullLogger<SteamStoreApiClient>.Instance);
        var manifestRegistryMock = new Mock<IManifestRegistry>();

        var hubMock = new Mock<IManifestHubProvider>();
        hubMock.Setup(h => h.GetAppSnapshotsAsync(730u, It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new BuildSnapshot
                {
                    AppId = 730,
                    BuildId = 111,
                    PatchDate = "2024-01-10",
                    Description = "Winter Patch",
                    DepotManifests = new Dictionary<uint, ulong> { { 731, 1000 } }
                },
                new BuildSnapshot
                {
                    AppId = 730,
                    BuildId = 222,
                    PatchDate = "2024-05-20",
                    Description = "Summer Patch",
                    ChangedDepots = [731u],
                    DepotManifests = new Dictionary<uint, ulong> { { 731, 2000 } }
                }
            ]);

        var resolver = new BuildResolver(
            steamClient,
            manifestRegistryMock.Object,
            NullLogger<BuildResolver>.Instance,
            hubProvider: hubMock.Object);

        var resolved = await resolver.ResolveVersionByPatchDateAsync(730, "2024-05-20");

        resolved.Should().NotBeNull();
        resolved!.BuildId.Should().Be("222");
        resolved.PatchDate.Should().Be("2024-05-20");
        resolved.Description.Should().Be("Summer Patch");
        resolved.ChangedDepots.Should().Equal([731u]);
    }

    #endregion

    #region 3. Steam License Discovery (appcache packageinfo.vdf / appinfo.vdf)

    [Fact]
    public void SteamLicenseDiscovery_InspectLocalSteamClientLicenses_ParsesAppCacheFiles()
    {
        var steamMockPath = Path.Combine(_testDir, "MockSteam");
        var appcacheDir = Path.Combine(steamMockPath, "appcache");
        var userDir = Path.Combine(steamMockPath, "userdata", "123456", "config");
        Directory.CreateDirectory(appcacheDir);
        Directory.CreateDirectory(userDir);

        // 1. Write userdata localconfig.vdf
        File.WriteAllText(Path.Combine(userDir, "localconfig.vdf"), """
        "UserLocalConfigStore"
        {
            "apps"
            {
                "440" { }
            }
        }
        """);

        // 2. Write appcache/packageinfo.vdf with test app
        File.WriteAllText(Path.Combine(appcacheDir, "packageinfo.vdf"), """
        "packageinfo"
        {
            "1"
            {
                "apps"
                {
                    "730" { }
                }
            }
        }
        """);

        // 3. Write binary appcache/appinfo.vdf with AppId 892970
        var binaryAppInfo = new byte[32];
        BitConverter.GetBytes(0x07564427u).CopyTo(binaryAppInfo, 0); // Magic
        BitConverter.GetBytes(1u).CopyTo(binaryAppInfo, 4);          // Universe
        BitConverter.GetBytes(892970u).CopyTo(binaryAppInfo, 8);     // AppId 892970
        BitConverter.GetBytes(16u).CopyTo(binaryAppInfo, 12);        // EntrySize
        File.WriteAllBytes(Path.Combine(appcacheDir, "appinfo.vdf"), binaryAppInfo);

        // Act: inspect local licenses
        var discoveredAppIds = SteamLicenseDiscoveryService.InspectLocalSteamClientLicenses(steamMockPath);

        // Assert: all three apps detected across localconfig, packageinfo.vdf, and appinfo.vdf!
        discoveredAppIds.Should().Contain(440u);
        discoveredAppIds.Should().Contain(730u);
        discoveredAppIds.Should().Contain(892970u);
    }

    #endregion

    #region 4. Demand Crossing with User Licenses

    [Fact]
    public void VoluntaryContributionService_MatchDemandWithLicenses_ReturnsLegitimateCandidates()
    {
        var service = new VoluntaryContributionService();

        var ownedAppIds = new uint[] { 730, 440 }; // User owns 730 and 440 (does NOT own 1000)

        var demandIndex = new DemandIndex
        {
            Records =
            [
                new() { AppId = 730, DepotId = 731, ManifestId = 111111, AppName = "CS2" },
                new() { AppId = 730, DepotId = 732, ManifestId = 222222, AppName = "CS2 DLC" },
                new() { AppId = 1000, DepotId = 1001, ManifestId = 333333, AppName = "Unowned Game" }
            ]
        };

        var matched = service.MatchDemandWithLicenses(ownedAppIds, demandIndex);

        matched.Should().HaveCount(2);
        matched.Should().OnlyContain(r => r.AppId == 730);
        matched.Should().NotContain(r => r.AppId == 1000);
    }

    #endregion

    #region 5. Multi-Mechanism Manifest Harvesters

    [Fact]
    public async Task SteamClientManifestHarvesterProvider_HarvestsAndValidatesProtobufHeader()
    {
        var steamDir = Path.Combine(_testDir, "SteamClientTest");
        var depotCacheDir = Path.Combine(steamDir, "depotcache");
        Directory.CreateDirectory(depotCacheDir);

        uint testAppId = 730;
        uint testDepotId = 731;
        ulong testManifestId = 9999999999UL;
        var manifestPath = Path.Combine(depotCacheDir, $"{testDepotId}_{testManifestId}.manifest");

        // Simulate Steam client command execution writing the .manifest file
        var harvester = new SteamClientManifestHarvesterProvider(
            steamPath: steamDir,
            commandLauncher: (exe, args) =>
            {
                // Write valid binary manifest protobuf
                var payload = new byte[32];
                ValidProtobufHeader.CopyTo(payload, 0);
                File.WriteAllBytes(manifestPath, payload);
                return true;
            },
            timeout: TimeSpan.FromSeconds(5),
            logger: NullLogger<SteamClientManifestHarvesterProvider>.Instance);

        var result = await harvester.HarvestManifestAsync(testAppId, testDepotId, testManifestId);

        result.IsSuccess.Should().BeTrue();
        result.ManifestFilePath.Should().Be(manifestPath);
        result.ProviderName.Should().Be("SteamClient");
    }

    [Fact]
    public async Task CompositeManifestHarvester_TriesInPriorityOrder_FallsBackGracefully()
    {
        uint testAppId = 730;
        uint testDepotId = 731;
        ulong testManifestId = 1234567UL;

        var validFile = Path.Combine(_testDir, $"{testDepotId}_{testManifestId}.manifest");
        var payload = new byte[16];
        ValidProtobufHeader.CopyTo(payload, 0);
        File.WriteAllBytes(validFile, payload);

        // Provider 1 (Priority 300) fails
        var provider1Mock = new Mock<IManifestHarvesterProvider>();
        provider1Mock.Setup(p => p.Name).Returns("FailingHarvester");
        provider1Mock.Setup(p => p.Priority).Returns(300);
        provider1Mock.Setup(p => p.IsAvailableAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true);
        provider1Mock.Setup(p => p.HarvestManifestAsync(testAppId, testDepotId, testManifestId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(HarvestResult.Failed("Network timeout", "FailingHarvester"));

        // Provider 2 (Priority 200) succeeds
        var provider2Mock = new Mock<IManifestHarvesterProvider>();
        provider2Mock.Setup(p => p.Name).Returns("BackupHarvester");
        provider2Mock.Setup(p => p.Priority).Returns(200);
        provider2Mock.Setup(p => p.IsAvailableAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true);
        provider2Mock.Setup(p => p.HarvestManifestAsync(testAppId, testDepotId, testManifestId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(HarvestResult.Success(validFile, "BackupHarvester"));

        var composite = new CompositeManifestHarvester([provider1Mock.Object, provider2Mock.Object]);

        var result = await composite.HarvestManifestAsync(testAppId, testDepotId, testManifestId);

        result.IsSuccess.Should().BeTrue();
        result.ProviderName.Should().Be("BackupHarvester");
        result.ManifestFilePath.Should().Be(validFile);
    }

    #endregion

    #region 6. End-to-End Harvest, Upload & Stardust Crediting

    [Fact]
    public async Task VoluntaryManifestContributionCoordinator_HarvestAndContribute_CreditsStardust()
    {
        uint testAppId = 730;
        uint testDepotId = 731;
        ulong testManifestId = 987654321UL;

        var manifestFile = Path.Combine(_testDir, $"{testDepotId}_{testManifestId}.manifest");
        var manifestPayload = new byte[64];
        ValidProtobufHeader.CopyTo(manifestPayload, 0);
        for (int i = 4; i < manifestPayload.Length; i++) manifestPayload[i] = (byte)(i % 256);
        File.WriteAllBytes(manifestFile, manifestPayload);

        // 1. Mocks
        var licenseMock = new Mock<ISteamLicenseDiscoveryService>();
        var hubProviderMock = new Mock<IManifestHubProvider>();

        var contributionService = new VoluntaryContributionService();

        var harvesterMock = new Mock<IManifestHarvestCoordinator>();
        harvesterMock.Setup(h => h.HarvestManifestAsync(testAppId, testDepotId, testManifestId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(HarvestResult.Success(manifestFile, "TestHarvester"));

        var transportMock = new Mock<IContributionTransportService>();
        transportMock.Setup(t => t.TransmitContributionAsync(It.IsAny<ContributionSubmission>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PublicationReceipt
            {
                DepotId = testDepotId,
                ManifestId = testManifestId,
                StardustCredited = 100,
                Status = "Published",
                ReceiptId = "rcpt_12345"
            });

        var stardustMock = new Mock<IStardustService>();

        var coordinator = new VoluntaryManifestContributionCoordinator(
            licenseMock.Object,
            hubProviderMock.Object,
            contributionService,
            harvesterMock.Object,
            transportMock.Object,
            stardustMock.Object);

        var demand = new DemandRecord
        {
            AppId = testAppId,
            DepotId = testDepotId,
            ManifestId = testManifestId,
            AppName = "Counter-Strike 2"
        };

        // Act: Execute full harvest and contribution
        var receipt = await coordinator.HarvestAndContributeAsync(demand, userConsentGranted: true);

        // Assert
        receipt.Should().NotBeNull();
        receipt.IsSuccess.Should().BeTrue();
        receipt.StardustAwarded.Should().Be(100);
        receipt.Status.Should().Be("Published");

        // Stardust reward must be accredited!
        stardustMock.Verify(s => s.NotifyRewardConfirmed(100), Times.Once);
        stardustMock.Verify(s => s.RefreshBalanceAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task VoluntaryManifestContributionCoordinator_WhenUserConsentNotGranted_BlocksHarvestAndUpload()
    {
        var licenseMock = new Mock<ISteamLicenseDiscoveryService>();
        var hubProviderMock = new Mock<IManifestHubProvider>();
        var contributionService = new VoluntaryContributionService();
        var harvesterMock = new Mock<IManifestHarvestCoordinator>();
        var transportMock = new Mock<IContributionTransportService>();
        var stardustMock = new Mock<IStardustService>();

        var coordinator = new VoluntaryManifestContributionCoordinator(
            licenseMock.Object,
            hubProviderMock.Object,
            contributionService,
            harvesterMock.Object,
            transportMock.Object,
            stardustMock.Object);

        var demand = new DemandRecord { AppId = 730, DepotId = 731, ManifestId = 12345 };

        var receipt = await coordinator.HarvestAndContributeAsync(demand, userConsentGranted: false);

        receipt.IsSuccess.Should().BeFalse();
        receipt.ErrorMessage.Should().Contain("consent");

        // Zero harvesting or transport should occur
        harvesterMock.Verify(h => h.HarvestManifestAsync(It.IsAny<uint>(), It.IsAny<uint>(), It.IsAny<ulong>(), It.IsAny<CancellationToken>()), Times.Never);
        transportMock.Verify(t => t.TransmitContributionAsync(It.IsAny<ContributionSubmission>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    #endregion
}
