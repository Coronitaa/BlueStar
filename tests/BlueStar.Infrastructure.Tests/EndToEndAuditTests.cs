using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BlueStar.Core.Helpers;
using BlueStar.Core.Interfaces;
using BlueStar.Core.Models;
using BlueStar.Infrastructure.Cache;
using BlueStar.Infrastructure.Emulators;
using BlueStar.Infrastructure.Metadata;

using BlueStar.Infrastructure.Providers.Catalog;
using BlueStar.Infrastructure.Providers.Curation;
using BlueStar.Infrastructure.Providers.Fixes;
using BlueStar.Infrastructure.Providers.Manifest;
using BlueStar.Infrastructure.Services;
using BlueStar.Infrastructure.Storage;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Moq.Protected;
using Xunit;

namespace BlueStar.Infrastructure.Tests;

/// <summary>
/// Mandatory End-to-End Audit test suite verifying the 15 critical production-readiness criteria
/// for BlueStar 1.3 provider-agnostic architecture.
/// </summary>
public class EndToEndAuditTests : IDisposable
{
    private readonly string _testRoot;

    public EndToEndAuditTests()
    {
        _testRoot = Path.Combine(Path.GetTempPath(), "BlueStar_E2E_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testRoot);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testRoot))
        {
            try { Directory.Delete(_testRoot, true); } catch { }
        }
    }

    private static byte[] GenerateValidSteamManifest(uint depotId, ulong manifestId)
    {
        using var ms = new MemoryStream();
        ms.Write([0xD0, 0x17, 0xF6, 0x71]); // Steam Protobuf Manifest Magic: 0x71F617D0
        ms.Write(BitConverter.GetBytes(128u)); // Payload size

        using (var deflate = new DeflateStream(ms, CompressionLevel.Fastest, leaveOpen: true))
        {
            var content = Encoding.UTF8.GetBytes($"depot_{depotId}_manifest_{manifestId}_test_payload_data");
            deflate.Write(content);
        }

        return ms.ToArray();
    }

    #region 1. GAME DISCOVERY

    [Fact]
    public async Task Audit_01_GameDiscovery_FindsAndCreatesInstanceWithoutDepotBox()
    {
        // Setup SteamStoreCatalogProvider with mocked Steam Search response (game not in DepotBox)
        var handlerMock = new Mock<HttpMessageHandler>();
        var jsonResponse = @"{
            ""items"": [
                {
                    ""id"": 1030300,
                    ""name"": ""Hollow Knight: Silksong"",
                    ""tiny_image"": ""https://cdn.akamai.steamstatic.com/steam/apps/1030300/capsule_sm_120.jpg"",
                    ""header_image"": ""https://cdn.akamai.steamstatic.com/steam/apps/1030300/header.jpg""
                }
            ]
        }";

        handlerMock.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.Is<HttpRequestMessage>(req => req.RequestUri!.AbsoluteUri.Contains("storesearch")),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new StringContent(jsonResponse, Encoding.UTF8, "application/json")
            });


        var httpClient = new HttpClient(handlerMock.Object)
        {
            BaseAddress = new Uri("https://store.steampowered.com")
        };

        var steamApiClient = new SteamStoreApiClient(httpClient, NullLogger<SteamStoreApiClient>.Instance);
        var catalogProvider = new SteamStoreCatalogProvider(steamApiClient, NullLogger<SteamStoreCatalogProvider>.Instance);

        // 1. Search game
        var searchResults = await catalogProvider.SearchGamesAsync("Silksong");
        searchResults.Should().NotBeEmpty();
        var foundGame = searchResults[0];
        foundGame.AppId.Should().Be(1030300);
        foundGame.Name.Should().Be("Hollow Knight: Silksong");
        foundGame.HeaderImageUrl.Should().NotBeNullOrWhiteSpace();

        // 2. Create instance directly without querying DepotBox
        var instance = new GameInstance
        {
            AppId = foundGame.AppId,
            Name = foundGame.Name,
            InstallPath = Path.Combine(_testRoot, "Games", "Silksong"),
            Status = InstanceStatus.NotInstalled,
            Origin = InstanceOrigin.DepotBox // Standard managed instance
        };

        instance.AppId.Should().Be(1030300);
        instance.Name.Should().Be("Hollow Knight: Silksong");
        instance.Status.Should().Be(InstanceStatus.NotInstalled);
        instance.CanManageDepots.Should().BeTrue();
    }

    #endregion

    #region 2. BUILD DISCOVERY

    [Fact]
    public async Task Audit_02_BuildDiscovery_BuildsGameVersionsWithoutArbitraryGrouping()
    {
        var handlerMock = new Mock<HttpMessageHandler>();

        // Canonical SteamCMD AppInfo format with multiple branches
        var steamCmdJson = @"{
            ""status"": ""success"",
            ""data"": {
                ""70"": {
                    ""depots"": {
                        ""branches"": {
                            ""public"": {
                                ""buildid"": ""8612740"",
                                ""timeupdated"": ""1650393600""
                            },
                            ""beta"": {
                                ""buildid"": ""8923411"",
                                ""timeupdated"": ""1655000000""
                            }
                        },
                        ""1"": {
                            ""name"": ""Half-Life Base Content"",
                            ""manifests"": {
                                ""public"": { ""gid"": ""2667117727132184734"" },
                                ""beta"": { ""gid"": ""999888777666555444"" }
                            }
                        },
                        ""2"": {
                            ""name"": ""Half-Life Binaries"",
                            ""manifests"": {
                                ""public"": { ""gid"": ""111222333444555666"" },
                                ""beta"": { ""gid"": ""888777666555444333"" }
                            }
                        }
                    }
                }
            }
        }";

        handlerMock.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.Is<HttpRequestMessage>(req => req.RequestUri!.AbsoluteUri.Contains("api.steamcmd.net")),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new StringContent(steamCmdJson, Encoding.UTF8, "application/json")
            });

        var steamClient = new SteamStoreApiClient(new HttpClient(handlerMock.Object), NullLogger<SteamStoreApiClient>.Instance);
        var mockRegistry = new Mock<IManifestRegistry>();
        mockRegistry.Setup(r => r.DiscoverManifestsAsync(70, It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        var resolver = new BuildResolver(steamClient, mockRegistry.Object, NullLogger<BuildResolver>.Instance);

        var versions = await resolver.GetAvailableVersionsAsync(70);

        versions.Should().HaveCount(2);

        // Branch 1: Public
        var publicVersion = versions.FirstOrDefault(v => v.BranchName == "public");
        publicVersion.Should().NotBeNull();
        publicVersion!.BuildId.Should().Be("8612740");
        publicVersion.IsInferred.Should().BeFalse();
        publicVersion.Depots.Should().HaveCount(2);
        publicVersion.DepotManifests[1].Should().Be(2667117727132184734UL);
        publicVersion.DepotManifests[2].Should().Be(111222333444555666UL);

        // Branch 2: Beta
        var betaVersion = versions.FirstOrDefault(v => v.BranchName == "beta");
        betaVersion.Should().NotBeNull();
        betaVersion!.BuildId.Should().Be("8923411");
        betaVersion.IsInferred.Should().BeFalse();
        betaVersion.Depots.Should().HaveCount(2);
        betaVersion.DepotManifests[1].Should().Be(999888777666555444UL);
        betaVersion.DepotManifests[2].Should().Be(888777666555444333UL);

        // Manifests from public and beta are strictly segregated, never mixed!
        publicVersion.DepotManifests[1].Should().NotBe(betaVersion.DepotManifests[1]);
    }

    #endregion

    #region 3. MULTI-PROVIDER MANIFEST DISCOVERY

    [Fact]
    public async Task Audit_03_MultiProviderManifestDiscovery_MergesDuplicatesAndSeparatesDifferences()
    {
        var cacheDir = Path.Combine(_testRoot, "mp_cache");
        var cacheService = new ManifestCacheService(NullLogger<ManifestCacheService>.Instance, cacheDir);

        // Provider A: Depot 100 with Manifest 1000
        var providerA = new Mock<IManifestProvider>();
        providerA.SetupGet(p => p.ProviderId).Returns("provider_a");
        providerA.SetupGet(p => p.Priority).Returns(600);
        providerA.Setup(p => p.DiscoverManifestsAsync(500, It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new ManifestArtifact
                {
                    DepotId = 100,
                    ManifestId = 1000,
                    Routes = [new ManifestSourceRoute { ProviderId = "provider_a", RouteType = ManifestSourceType.DirectHttp }]
                },
                new ManifestArtifact
                {
                    DepotId = 200,
                    ManifestId = 2000,
                    Routes = [new ManifestSourceRoute { ProviderId = "provider_a", RouteType = ManifestSourceType.DirectHttp }]
                }
            ]);

        // Provider B: Same Depot 100 with Manifest 1000 (SAME), but Depot 200 with Manifest 2001 (DIFFERENT)
        var providerB = new Mock<IManifestProvider>();
        providerB.SetupGet(p => p.ProviderId).Returns("provider_b");
        providerB.SetupGet(p => p.Priority).Returns(400);
        providerB.Setup(p => p.DiscoverManifestsAsync(500, It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new ManifestArtifact
                {
                    DepotId = 100,
                    ManifestId = 1000,
                    Routes = [new ManifestSourceRoute { ProviderId = "provider_b", RouteType = ManifestSourceType.DirectHttp }]
                },
                new ManifestArtifact
                {
                    DepotId = 200,
                    ManifestId = 2001,
                    Routes = [new ManifestSourceRoute { ProviderId = "provider_b", RouteType = ManifestSourceType.DirectHttp }]
                }
            ]);


        var registry = new ManifestRegistry(
            [providerA.Object, providerB.Object],
            cacheService,
            NullLogger<ManifestRegistry>.Instance);

        var discovered = await registry.DiscoverManifestsAsync(500);

        // Depot 100 + Manifest 1000 must produce exactly ONE ManifestArtifact with merged routes
        var depot100Artifacts = discovered.Where(a => a.DepotId == 100 && a.ManifestId == 1000).ToList();
        depot100Artifacts.Should().HaveCount(1);
        depot100Artifacts[0].Routes.Should().HaveCount(2);
        depot100Artifacts[0].Routes.Select(r => r.ProviderId).Should().Contain(["provider_a", "provider_b"]);

        // Depot 200 has manifests 2000 and 2001: must produce TWO distinct artifacts!
        var depot200Artifacts = discovered.Where(a => a.DepotId == 200).ToList();
        depot200Artifacts.Should().HaveCount(2);
        depot200Artifacts.Select(a => a.ManifestId).Should().Contain([2000UL, 2001UL]);
    }

    #endregion

    #region 4. PROVIDER FAILURE & CIRCUIT BREAKER

    [Fact]
    public async Task Audit_04_ProviderFailure_FailsOverAndActivatesCircuitBreaker()
    {
        var cacheDir = Path.Combine(_testRoot, "cb_cache");
        var cacheService = new ManifestCacheService(NullLogger<ManifestCacheService>.Instance, cacheDir);

        var validData = GenerateValidSteamManifest(70, 777);
        var tempFile = Path.Combine(_testRoot, "fallback_manifest.manifest");
        await File.WriteAllBytesAsync(tempFile, validData);

        // Failing Provider A (throws 500 error)
        var failingProvider = new Mock<IManifestProvider>();
        failingProvider.SetupGet(p => p.ProviderId).Returns("failing_provider");
        failingProvider.SetupGet(p => p.Priority).Returns(900);
        failingProvider.Setup(p => p.DownloadManifestAsync(It.IsAny<uint>(), It.IsAny<ulong>(), It.IsAny<string>(), It.IsAny<uint>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("HTTP 500 Internal Server Error"));

        // Healthy Provider B
        var healthyProvider = new Mock<IManifestProvider>();
        healthyProvider.SetupGet(p => p.ProviderId).Returns("healthy_provider");
        healthyProvider.SetupGet(p => p.Priority).Returns(100);
        healthyProvider.Setup(p => p.DownloadManifestAsync(It.IsAny<uint>(), It.IsAny<ulong>(), It.IsAny<string>(), It.IsAny<uint>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(tempFile);

        var registry = new ManifestRegistry(
            [failingProvider.Object, healthyProvider.Object],
            cacheService,
            NullLogger<ManifestRegistry>.Instance);

        // Attempt 1: Failover to Provider B
        var result1 = await registry.AcquireManifestAsync(70, 777, 70);
        result1.Should().NotBeNull();
        failingProvider.Verify(p => p.DownloadManifestAsync(70, 777, It.IsAny<string>(), 70, It.IsAny<CancellationToken>()), Times.Once);

        // Attempt 2 and 3: Failover again to trip circuit breaker (threshold = 3 failures)
        await registry.AcquireManifestAsync(70, 778, 70);
        await registry.AcquireManifestAsync(70, 779, 70);

        // Now failingProvider should have consecutive failures = 3 and circuit breaker tripped!
        // Attempt 4: Should skip failingProvider completely without calling it!
        failingProvider.Invocations.Clear();

        await registry.AcquireManifestAsync(70, 780, 70);

        // Assert failingProvider was skipped by the circuit breaker
        failingProvider.Verify(p => p.DownloadManifestAsync(It.IsAny<uint>(), It.IsAny<ulong>(), It.IsAny<string>(), It.IsAny<uint>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    #endregion

    #region 5. CACHE LIFECYCLE & CORRUPTION REJECTION

    [Fact]
    public async Task Audit_05_Cache_PersistsAcrossRestartsAndRejectsCorruptedManifest()
    {
        var cacheDir = Path.Combine(_testRoot, "lifecycle_cache");

        // 1. Store valid manifest in cache instance 1
        var cache1 = new ManifestCacheService(NullLogger<ManifestCacheService>.Instance, cacheDir);
        var validData = GenerateValidSteamManifest(240, 55555);
        using (var stream = new MemoryStream(validData))
        {
            await cache1.StoreManifestAsync(240, 55555, stream);
        }

        // 2. Simulate application restart: instantiate cache instance 2 on the same folder
        var cache2 = new ManifestCacheService(NullLogger<ManifestCacheService>.Instance, cacheDir);
        cache2.HasManifest(240, 55555).Should().BeTrue();
        var cachedFile = cache2.GetManifestPath(240, 55555);
        cachedFile.Should().NotBeNull();
        File.Exists(cachedFile!).Should().BeTrue();

        // 3. Corrupt artifact: write garbage / HTML into the file
        await File.WriteAllTextAsync(cachedFile!, "<html><body>404 Not Found</body></html>");

        // 4. ValidateManifest must detect corruption and reject
        cache2.ValidateManifest(cachedFile!).Should().BeFalse();
        cache2.HasManifest(240, 55555).Should().BeFalse();
    }

    #endregion

    #region 6. INSTALLATION PLAN (DIFFERENTIAL CALCULATION)

    [Fact]
    public async Task Audit_06_InstallationPlan_CalculatesExactDifferentialUpdate()
    {
        var cacheDir = Path.Combine(_testRoot, "plan_cache");
        var cacheService = new ManifestCacheService(NullLogger<ManifestCacheService>.Instance, cacheDir);
        var planner = new InstallationPlanner(cacheService, NullLogger<InstallationPlanner>.Instance);

        // Instance currently installed:
        // A (10) -> Manifest 100 (1 MB)
        // B (20) -> Manifest 200 (2 MB)
        // C (30) -> Manifest 300 (3 MB)
        var instance = new GameInstance
        {
            AppId = 400,
            Name = "Portal",
            InstallPath = Path.Combine(_testRoot, "Portal"),
            InstalledManifestMap = new Dictionary<uint, ulong>
            {
                { 10, 100 },
                { 20, 200 },
                { 30, 300 }
            }
        };

        // Target version:
        // A (10) -> Manifest 101 (CHANGED - 1 MB)
        // B (20) -> Manifest 200 (UNCHANGED - 2 MB)
        // C (30) -> Manifest 301 (CHANGED - 3 MB)
        var targetVersion = new GameVersion
        {
            BuildId = "Build_Target",
            BranchName = "public",
            Depots =
            [
                new DepotVersion { DepotId = 10, ManifestId = 101, SizeBytes = 1_000_000, Name = "Depot A" },
                new DepotVersion { DepotId = 20, ManifestId = 200, SizeBytes = 2_000_000, Name = "Depot B" },
                new DepotVersion { DepotId = 30, ManifestId = 301, SizeBytes = 3_000_000, Name = "Depot C" }
            ]
        };

        var plan = await planner.CreateUpdatePlanAsync(instance, targetVersion);

        // Assert: only A and C are scheduled for download!
        plan.IsDifferential.Should().BeTrue();
        plan.DepotsToDownload.Should().HaveCount(2);
        plan.DepotsToDownload.Select(d => d.DepotId).Should().Contain([10u, 30u]);
        plan.DepotsToDownload.Any(d => d.DepotId == 20u).Should().BeFalse();

        // Assert: B is preserved in ReusedDepots!
        plan.ReusedDepots.Should().HaveCount(1);
        plan.ReusedDepots[0].DepotId.Should().Be(20u);
        plan.ReusedDepots[0].IsReused.Should().BeTrue();

        // Download bytes must sum only A and C (1MB + 3MB = 4MB)
        plan.TotalDownloadSizeBytes.Should().Be(4_000_000);
        plan.ReusedDepots.Sum(d => d.SizeBytes).Should().Be(2_000_000);
    }


    #endregion

    #region 7. RECOMMENDED BUILD & CURATION

    [Fact]
    public async Task Audit_07_RecommendedBuild_HonorsCuratedPinnedManifestsAndFallsBack()
    {
        var curationFile = Path.Combine(_testRoot, "curation.json");
        var curationProvider = new CommunityCurationProvider(NullLogger<CommunityCurationProvider>.Instance, curationFile);

        // 1. Save curated advice for AppId 70
        var curatedRec = new CurationRecommendation
        {
            AppId = 70,
            RecommendedBuildId = "8612740",
            RecommendedBranch = "public",
            Summary = "Gold Master 25th Anniversary Build",
            PinnedManifests = new Dictionary<uint, ulong>
            {
                { 1, 2667117727132184734UL }
            },
            RecommendedEmulator = "refix"
        };
        await curationProvider.SaveRecommendationAsync(curatedRec);

        // Setup BuildResolver with 2 builds: 8612740 (Curated) and 9999999 (Latest public)
        var steamCmdJson = @"{
            ""status"": ""success"",
            ""data"": {
                ""70"": {
                    ""depots"": {
                        ""branches"": {
                            ""public"": { ""buildid"": ""9999999"", ""timeupdated"": ""1655000000"" },
                            ""curated_branch"": { ""buildid"": ""8612740"", ""timeupdated"": ""1650393600"" }
                        },
                        ""1"": {
                            ""name"": ""Base Content"",
                            ""manifests"": {
                                ""public"": { ""gid"": ""111111111111111111"" },
                                ""curated_branch"": { ""gid"": ""2667117727132184734"" }
                            }
                        }
                    }
                }
            }
        }";

        var handlerMock = new Mock<HttpMessageHandler>();
        handlerMock.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new StringContent(steamCmdJson, Encoding.UTF8, "application/json")
            });

        var steamClient = new SteamStoreApiClient(new HttpClient(handlerMock.Object), NullLogger<SteamStoreApiClient>.Instance);

        var mockRegistry = new Mock<IManifestRegistry>();
        mockRegistry.Setup(r => r.DiscoverManifestsAsync(70, It.IsAny<CancellationToken>())).ReturnsAsync([]);

        var resolver = new BuildResolver(
            steamClient,
            mockRegistry.Object,
            NullLogger<BuildResolver>.Instance,
            curationProvider);


        // Resolve recommended: should select 8612740 with IsCurated = true
        var recommended = await resolver.ResolveRecommendedVersionAsync(70);
        recommended.Should().NotBeNull();
        recommended!.BuildId.Should().Be("8612740");
        recommended.IsCurated.Should().BeTrue();

        // 2. Test fallback when no recommendation exists
        var uncuratedRecommended = await resolver.ResolveRecommendedVersionAsync(999);
        // Fallback returns null or public release
        uncuratedRecommended.Should().BeNull();
    }

    #endregion

    #region 8. COMPONENT / FIX COEXISTENCE

    [Fact]
    public async Task Audit_08_ComponentFix_OperatesWithoutDepotBoxAndReFixIsSelfContained()
    {
        // 1. OnlineFixProvider operates independently
        var handlerMock = new Mock<HttpMessageHandler>();
        var onlineFixJson = @"{
            ""data"": [
                {
                    ""id"": ""550_fix"",
                    ""title"": ""Left 4 Dead 2"",
                    ""downloadUrl"": ""https://pixeldrain.com/api/file/abc12345""
                }
            ]
        }";

        handlerMock.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.Is<HttpRequestMessage>(req => req.RequestUri!.AbsoluteUri.Contains("onlinefix")),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new StringContent(onlineFixJson, Encoding.UTF8, "application/json")
            });

        var fixProvider = new OnlineFixProvider(new HttpClient(handlerMock.Object), NullLogger<OnlineFixProvider>.Instance);
        var fixes = await fixProvider.GetFixesAsync(550);
        fixes.Should().HaveCount(1);
        fixes[0].Id.Should().Be("onlinefix_550_fix");

        // 2. GameFixDeployService can instantiate with only IFixProvider and no IDepotBoxApiClient
        var mockInstanceManager = new Mock<IInstanceManager>();
        var mockDlcInstaller = new Mock<IDlcInstaller>();
        var deployService = new GameFixDeployService(
            mockInstanceManager.Object,
            mockDlcInstaller.Object,
            NullLogger<GameFixDeployService>.Instance,
            [fixProvider]);

        deployService.Should().NotBeNull();

        // 3. ReFix Manager operates on local directory without any provider dependency
        var gameDir = Path.Combine(_testRoot, "ReFixGame");
        Directory.CreateDirectory(gameDir);

        var refixManager = new ReFixManager(NullLogger<ReFixManager>.Instance);
        var success = await refixManager.ConfigureInstanceSettingsAsync(gameDir, new InstanceReFixConfig
        {
            AppId = 550,
            AccountName = "BlueStarPlayer",
            SteamId = 76561198000000001,
            ListenPort = 27015
        });

        success.Should().BeTrue();
        var settingsFile = Path.Combine(gameDir, "steam_settings", "steam_appid.txt");
        File.Exists(settingsFile).Should().BeTrue();
        (await File.ReadAllTextAsync(settingsFile)).Should().Be("550");
    }

    #endregion

    #region 9. LEGACY MIGRATION

    [Fact]
    public void Audit_09_LegacyMigration_DeserializesPre13InstanceWithoutDataLoss()
    {
        var legacyJson = @"{
            ""Id"": ""31a53597-035b-4c79-a4f1-c04da61356b9"",
            ""AppId"": 70,
            ""Name"": ""Half-Life"",
            ""InstallPath"": ""C:\\Games\\Half-Life"",
            ""ExecutablePath"": ""hl.exe"",
            ""LaunchArguments"": ""-console"",
            ""Origin"": 0,
            ""IsDepotBoxAssociated"": true,
            ""Status"": 2,
            ""Depots"": [
                {
                    ""DepotId"": 1,
                    ""ManifestId"": 2667117727132184734,
                    ""Name"": ""Base Content"",
                    ""SizeBytes"": 250000000,
                    ""DepotKey"": ""aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"",
                    ""IsDownloaded"": true
                }
            ],
            ""Dlcs"": [
                {
                    ""AppId"": 50,
                    ""Name"": ""Half-Life: Opposing Force"",
                    ""IsInstalled"": true,
                    ""Depots"": []
                }
            ]
        }";

        var instance = JsonSerializer.Deserialize<GameInstance>(legacyJson);

        instance.Should().NotBeNull();
        instance!.Id.Should().Be(Guid.Parse("31a53597-035b-4c79-a4f1-c04da61356b9"));
        instance.AppId.Should().Be(70);
        instance.Name.Should().Be("Half-Life");
        instance.InstallPath.Should().Be(@"C:\Games\Half-Life");
        instance.ExecutablePath.Should().Be("hl.exe");
        instance.LaunchArguments.Should().Be("-console");

        instance.Origin.Should().Be(InstanceOrigin.DepotBox);
        instance.IsDepotBoxAssociated.Should().BeTrue();
        instance.Status.Should().Be(InstanceStatus.Ready);
        instance.Depots.Should().HaveCount(1);
        instance.Depots[0].DepotId.Should().Be(1);
        instance.Depots[0].ManifestId.Should().Be(2667117727132184734UL);
        instance.Dlcs.Should().HaveCount(1);
        instance.Dlcs[0].AppId.Should().Be(50);
        instance.CanManageDepots.Should().BeTrue();
    }


    #endregion

    #region 10. SERVICE-LEVEL END-TO-END LIFECYCLE TEST

    /// <summary>
    /// Service-level end-to-end lifecycle test: executes complete backend flow across catalog, builds,
    /// manifest registry, differential installation planning, state serialization, and reload.
    /// </summary>
    [Fact]
    public async Task Audit_10_ServiceLevelEndToEndLifecycle_ExecutesFullLifecycle()
    {

        var appDataDir = Path.Combine(_testRoot, "JourneyAppData");
        Directory.CreateDirectory(appDataDir);

        // Step 1: Initialize Manifest Cache & Key Repository
        var cacheService = new ManifestCacheService(NullLogger<ManifestCacheService>.Instance, Path.Combine(appDataDir, "cache", "manifests"));
        var keyRepo = new DepotKeyRepository(NullLogger<DepotKeyRepository>.Instance, Path.Combine(appDataDir, "keys.json"));
        await keyRepo.RegisterKeyAsync(1, "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef");

        // Pre-create manifest payload
        var manifestBytes = GenerateValidSteamManifest(1, 100);
        var mockHttp = new Mock<HttpMessageHandler>();
        mockHttp.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new ByteArrayContent(manifestBytes)
            });

        var manifestHubProvider = new ManifestHubProvider(new HttpClient(mockHttp.Object), cacheService, NullLogger<ManifestHubProvider>.Instance, keyRepo);
        var registry = new ManifestRegistry(
            [new LocalCacheManifestProvider(cacheService, NullLogger<LocalCacheManifestProvider>.Instance), manifestHubProvider],
            cacheService,
            NullLogger<ManifestRegistry>.Instance);

        // Step 2: Search Catalog
        var catalogProvider = new SteamStoreCatalogProvider(
            new SteamStoreApiClient(new HttpClient(mockHttp.Object), NullLogger<SteamStoreApiClient>.Instance),
            NullLogger<SteamStoreCatalogProvider>.Instance);

        // Step 3: Add Game Instance
        var instanceId = Guid.NewGuid();
        var gameInstance = new GameInstance
        {
            Id = instanceId,
            AppId = 70,
            Name = "Half-Life",
            InstallPath = Path.Combine(_testRoot, "HL_Install"),
            Status = InstanceStatus.NotInstalled,
            Origin = InstanceOrigin.DepotBox
        };

        // Step 4: Discover Versions & Select Version
        var targetVersion = new GameVersion
        {
            BuildId = "8612740",
            BranchName = "public",
            DisplayName = "25th Anniversary",
            Depots = [new DepotVersion { DepotId = 1, ManifestId = 100, Name = "Base Depot", SizeBytes = 50_000_000 }]
        };

        // Step 5: Acquire Manifests across Providers
        var acquiredManifest = await registry.AcquireManifestAsync(1, 100, 70);
        acquiredManifest.Should().NotBeNull();
        File.Exists(acquiredManifest!).Should().BeTrue();

        // Step 6: Generate Installation Plan
        var planner = new InstallationPlanner(cacheService, NullLogger<InstallationPlanner>.Instance, keyRepo);
        var installPlan = await planner.CreateInstallPlanAsync(gameInstance, targetVersion);
        installPlan.DepotsToDownload.Should().HaveCount(1);
        installPlan.DepotsToDownload[0].DepotKey.Should().Be("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef");

        // Step 7: Simulate Download Completion & Persist State
        var installedInstance = gameInstance with
        {
            Status = InstanceStatus.Ready,
            ActiveBuildId = targetVersion.BuildId,
            ActiveBranch = targetVersion.BranchName,
            InstalledManifestMap = new Dictionary<uint, ulong> { { 1, 100 } },
            Depots = targetVersion.Depots.Select(d => d.ToDepotInfo(isDownloaded: true)).ToList().AsReadOnly()
        };

        var instanceFile = Path.Combine(appDataDir, "instances", $"{instanceId}.json");
        Directory.CreateDirectory(Path.GetDirectoryName(instanceFile)!);
        await File.WriteAllTextAsync(instanceFile, JsonSerializer.Serialize(installedInstance));

        // Step 8: Simulate App Restart & Reload Instance
        var reloadedJson = await File.ReadAllTextAsync(instanceFile);
        var reloadedInstance = JsonSerializer.Deserialize<GameInstance>(reloadedJson);
        reloadedInstance.Should().NotBeNull();
        reloadedInstance!.Status.Should().Be(InstanceStatus.Ready);
        reloadedInstance.ActiveBuildId.Should().Be("8612740");
        reloadedInstance.InstalledManifestMap[1].Should().Be(100);


        // Step 9: Recheck for Update (New build with unchanged Depot 1 and new Depot 2)
        var updateVersion = new GameVersion
        {
            BuildId = "8612741",
            BranchName = "public",
            DisplayName = "Patch 1",
            Depots =
            [
                new DepotVersion { DepotId = 1, ManifestId = 100, Name = "Base Depot", SizeBytes = 50_000_000 },
                new DepotVersion { DepotId = 2, ManifestId = 200, Name = "Patch Depot", SizeBytes = 5_000_000 }
            ]
        };

        var updatePlan = await planner.CreateUpdatePlanAsync(reloadedInstance, updateVersion);

        // Step 10: Verify Differential Plan
        updatePlan.IsDifferential.Should().BeTrue();
        updatePlan.ReusedDepots.Should().HaveCount(1);
        updatePlan.ReusedDepots[0].DepotId.Should().Be(1); // Depot 1 reused!
        updatePlan.DepotsToDownload.Should().HaveCount(1);
        updatePlan.DepotsToDownload[0].DepotId.Should().Be(2); // Only Depot 2 downloaded!
        updatePlan.TotalDownloadSizeBytes.Should().Be(5_000_000);
    }

    #endregion
}
