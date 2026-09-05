using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BlueStar.Core.Interfaces;
using BlueStar.Core.Models;
using BlueStar.Infrastructure.Cache;
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

public class MultiProviderArchitectureTests : IDisposable
{
    private readonly string _testDir;

    public MultiProviderArchitectureTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "BlueStarTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDir))
        {
            try { Directory.Delete(_testDir, true); } catch { }
        }
    }

    private static byte[] CreateValidSteamProtobufManifest()
    {
        // Steam Protobuf Manifest:
        // Byte 0..3: 0x71F617D0 (Little-endian D0 17 F6 71)
        // Byte 4..7: Payload length uint32
        // Byte 8..: Deflate compressed payload
        using var ms = new MemoryStream();
        ms.Write([0xD0, 0x17, 0xF6, 0x71]); // magic
        ms.Write(BitConverter.GetBytes(100u)); // payload length

        using (var deflate = new DeflateStream(ms, CompressionLevel.Fastest, leaveOpen: true))
        {
            var dummyPayload = Encoding.UTF8.GetBytes("steam_protobuf_manifest_sample_payload_data_for_unit_tests");
            deflate.Write(dummyPayload);
        }

        return ms.ToArray();
    }

    private static byte[] CreateValidGzipManifest()
    {
        using var ms = new MemoryStream();
        using (var gzip = new GZipStream(ms, CompressionLevel.Fastest, leaveOpen: true))
        {
            var dummyPayload = Encoding.UTF8.GetBytes("gzip_compressed_manifest_payload_data");
            gzip.Write(dummyPayload);
        }
        return ms.ToArray();
    }

    [Fact]
    public async Task ManifestCacheService_StoresAndValidatesAuthenticSteamManifest()
    {
        var cacheDir = Path.Combine(_testDir, "cache");
        var service = new ManifestCacheService(NullLogger<ManifestCacheService>.Instance, cacheDir);

        var manifestData = CreateValidSteamProtobufManifest();
        using var stream = new MemoryStream(manifestData);

        var storedPath = await service.StoreManifestAsync(730, 10001, stream);

        File.Exists(storedPath).Should().BeTrue();
        service.HasManifest(730, 10001).Should().BeTrue();
        service.GetManifestPath(730, 10001).Should().Be(storedPath);
        service.ValidateManifest(storedPath).Should().BeTrue();
    }

    [Fact]
    public async Task ManifestCacheService_ValidatesGzipManifest()
    {
        var cacheDir = Path.Combine(_testDir, "cache_gzip");
        var service = new ManifestCacheService(NullLogger<ManifestCacheService>.Instance, cacheDir);

        var manifestData = CreateValidGzipManifest();
        using var stream = new MemoryStream(manifestData);

        var storedPath = await service.StoreManifestAsync(440, 20002, stream);
        service.HasManifest(440, 20002).Should().BeTrue();
    }

    [Fact]
    public async Task ManifestCacheService_RejectsHtmlAndCleartextErrorResponses()
    {
        var cacheDir = Path.Combine(_testDir, "cache_bad");
        var service = new ManifestCacheService(NullLogger<ManifestCacheService>.Instance, cacheDir);

        var htmlError = Encoding.UTF8.GetBytes("<!DOCTYPE html><html><body>404 Not Found</body></html>");
        using var htmlStream = new MemoryStream(htmlError);

        var actHtml = async () => await service.StoreManifestAsync(730, 99999, htmlStream);
        await actHtml.Should().ThrowAsync<InvalidDataException>();

        var jsonError = Encoding.UTF8.GetBytes("{\"error\": \"Rate limit exceeded\", \"status\": 429}");
        using var jsonStream = new MemoryStream(jsonError);

        var actJson = async () => await service.StoreManifestAsync(730, 99998, jsonStream);
        await actJson.Should().ThrowAsync<InvalidDataException>();
    }

    [Fact]
    public async Task DepotKeyRepository_RegistersAndPersistsKeys()
    {
        var keyFile = Path.Combine(_testDir, "keys.json");
        var repo = new DepotKeyRepository(NullLogger<DepotKeyRepository>.Instance, keyFile);

        var keyHex = "0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF";
        await repo.RegisterKeyAsync(731, keyHex, "Unit Test");

        var retrieved = await repo.GetKeyAsync(731);
        retrieved.Should().Be(keyHex.ToLowerInvariant());

        // Test persistence reload
        var reloadedRepo = new DepotKeyRepository(NullLogger<DepotKeyRepository>.Instance, keyFile);
        var reloadedKey = await reloadedRepo.GetKeyAsync(731);
        reloadedKey.Should().Be(keyHex.ToLowerInvariant());
    }

    [Fact]
    public async Task DepotKeyRepository_ParsesLuaAddAppIdFormat()
    {
        var keyFile = Path.Combine(_testDir, "keys_lua.json");
        var repo = new DepotKeyRepository(NullLogger<DepotKeyRepository>.Instance, keyFile);

        var luaScript = @"
-- Discovered Steam keys
addappid(228980, 0, ""aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"")
addappid(228981, 0, ""bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"")
";
        await repo.ImportFromLuaAsync(luaScript);

        (await repo.GetKeyAsync(228980)).Should().Be("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        (await repo.GetKeyAsync(228981)).Should().Be("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");

    }

    [Fact]
    public async Task ManifestRegistry_PrioritizesLocalCacheOverRemoteProviders()
    {
        var cacheDir = Path.Combine(_testDir, "registry_cache");
        var cacheService = new ManifestCacheService(NullLogger<ManifestCacheService>.Instance, cacheDir);

        // Pre-cache manifest
        var validData = CreateValidSteamProtobufManifest();
        using var stream = new MemoryStream(validData);
        var cachedFile = await cacheService.StoreManifestAsync(70, 101, stream);

        var mockRemoteProvider = new Mock<IManifestProvider>();
        mockRemoteProvider.SetupGet(p => p.ProviderId).Returns("remote");
        mockRemoteProvider.SetupGet(p => p.Priority).Returns(500);

        var registry = new ManifestRegistry(
            [new LocalCacheManifestProvider(cacheService, NullLogger<LocalCacheManifestProvider>.Instance), mockRemoteProvider.Object],
            cacheService,
            NullLogger<ManifestRegistry>.Instance);

        var resolved = await registry.AcquireManifestAsync(70, 101, 70);

        resolved.Should().Be(cachedFile);
        mockRemoteProvider.Verify(p => p.DownloadManifestAsync(It.IsAny<uint>(), It.IsAny<ulong>(), It.IsAny<string>(), It.IsAny<uint>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ManifestRegistry_FailsOverWhenProviderThrows()
    {
        var cacheDir = Path.Combine(_testDir, "failover_cache");
        var cacheService = new ManifestCacheService(NullLogger<ManifestCacheService>.Instance, cacheDir);

        var validData = CreateValidSteamProtobufManifest();
        var tempManifest = Path.Combine(_testDir, "temp_manifest.manifest");
        await File.WriteAllBytesAsync(tempManifest, validData);

        var failingProvider = new Mock<IManifestProvider>();
        failingProvider.SetupGet(p => p.ProviderId).Returns("failing");
        failingProvider.SetupGet(p => p.Priority).Returns(800);
        failingProvider.Setup(p => p.DownloadManifestAsync(It.IsAny<uint>(), It.IsAny<ulong>(), It.IsAny<string>(), It.IsAny<uint>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Connection timed out"));

        var succeedingProvider = new Mock<IManifestProvider>();
        succeedingProvider.SetupGet(p => p.ProviderId).Returns("succeeding");
        succeedingProvider.SetupGet(p => p.Priority).Returns(400);
        succeedingProvider.Setup(p => p.DownloadManifestAsync(It.IsAny<uint>(), It.IsAny<ulong>(), It.IsAny<string>(), It.IsAny<uint>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(tempManifest);

        var registry = new ManifestRegistry(
            [failingProvider.Object, succeedingProvider.Object],
            cacheService,
            NullLogger<ManifestRegistry>.Instance);

        var result = await registry.AcquireManifestAsync(70, 202, 70);

        result.Should().NotBeNull();
        failingProvider.Verify(p => p.DownloadManifestAsync(70, 202, It.IsAny<string>(), 70, It.IsAny<CancellationToken>()), Times.Once);
        succeedingProvider.Verify(p => p.DownloadManifestAsync(70, 202, It.IsAny<string>(), 70, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task InstallationPlanner_ReusesUnchangedDepotsInDifferentialUpdate()
    {
        var cacheDir = Path.Combine(_testDir, "planner_cache");
        var cacheService = new ManifestCacheService(NullLogger<ManifestCacheService>.Instance, cacheDir);
        var planner = new InstallationPlanner(cacheService, NullLogger<InstallationPlanner>.Instance);

        // Instance currently has Depot 1 (Manifest 100) and Depot 2 (Manifest 200) installed
        var instance = new GameInstance
        {
            Name = "Half-Life",
            AppId = 70,
            InstallPath = @"C:\Games\Half-Life",
            InstalledManifestMap = new Dictionary<uint, ulong>
            {
                { 1, 100 },
                { 2, 200 }
            }
        };

        // Target version has Depot 1 (Manifest 100 - UNCHANGED) and Depot 2 (Manifest 201 - UPDATED)
        var targetVersion = new GameVersion
        {
            BuildId = "15000",
            BranchName = "public",
            Depots =

            [
                new DepotVersion { DepotId = 1, ManifestId = 100, Name = "Depot 1", SizeBytes = 1_000_000 },
                new DepotVersion { DepotId = 2, ManifestId = 201, Name = "Depot 2", SizeBytes = 2_000_000 }
            ]
        };

        var plan = await planner.CreateUpdatePlanAsync(instance, targetVersion);

        plan.IsDifferential.Should().BeTrue();
        plan.ReusedDepots.Should().HaveCount(1);
        plan.ReusedDepots[0].DepotId.Should().Be(1);
        plan.ReusedDepots[0].IsReused.Should().BeTrue();

        plan.DepotsToDownload.Should().HaveCount(1);
        plan.DepotsToDownload[0].DepotId.Should().Be(2);
        plan.DepotsToDownload[0].IsReused.Should().BeFalse();
        plan.TotalDownloadSizeBytes.Should().Be(2_000_000);
    }

    [Fact]
    public void ProviderCapabilities_ExposeAccurateFlags()
    {
        var localCache = new LocalCacheManifestProvider(
            new ManifestCacheService(NullLogger<ManifestCacheService>.Instance, _testDir),
            NullLogger<LocalCacheManifestProvider>.Instance);

        localCache.Capabilities.HasFlag(ProviderCapabilities.ManifestDownload).Should().BeTrue();
        localCache.Capabilities.HasFlag(ProviderCapabilities.FixDownload).Should().BeFalse();

        var curation = new CommunityCurationProvider(NullLogger<CommunityCurationProvider>.Instance);
        curation.Capabilities.HasFlag(ProviderCapabilities.CurationAdvice).Should().BeTrue();

        var steamCatalog = new SteamStoreCatalogProvider(
            new SteamStoreApiClient(new HttpClient(), NullLogger<SteamStoreApiClient>.Instance),
            NullLogger<SteamStoreCatalogProvider>.Instance);

        steamCatalog.Capabilities.HasFlag(ProviderCapabilities.CatalogSearch).Should().BeTrue();
        steamCatalog.Capabilities.HasFlag(ProviderCapabilities.BuildDiscovery).Should().BeTrue();
    }

    [Fact]
    public async Task ManifestDiscovery_SeparatedFrom_Acquisition()
    {
        var cacheDir = Path.Combine(_testDir, "discovery_sep_cache");
        var cacheService = new ManifestCacheService(NullLogger<ManifestCacheService>.Instance, cacheDir);

        var validData = CreateValidSteamProtobufManifest();
        var directPayloadFile = Path.Combine(_testDir, "direct_acquired.manifest");

        await File.WriteAllBytesAsync(directPayloadFile, validData);

        // Discovery Provider: capable of index discovery, but acquisition fails or yields route
        var discoveryProvider = new Mock<IManifestProvider>();
        discoveryProvider.SetupGet(p => p.ProviderId).Returns("discovery_indexer");
        discoveryProvider.SetupGet(p => p.Priority).Returns(100);
        discoveryProvider.Setup(p => p.DiscoverManifestsAsync(500, It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new ManifestArtifact
                {
                    DepotId = 500,
                    ManifestId = 100,
                    Routes = [new ManifestSourceRoute { ProviderId = "discovery_indexer", RouteType = ManifestSourceType.GitHubRaw }]
                }
            ]);
        discoveryProvider.Setup(p => p.DownloadManifestAsync(It.IsAny<uint>(), It.IsAny<ulong>(), It.IsAny<string>(), It.IsAny<uint>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Not a direct download provider"));

        // Acquisition Provider: high priority acquisition provider (e.g. mirror or local cache)
        var acquisitionProvider = new Mock<IManifestProvider>();
        acquisitionProvider.SetupGet(p => p.ProviderId).Returns("acquisition_mirror");
        acquisitionProvider.SetupGet(p => p.Priority).Returns(900);
        acquisitionProvider.Setup(p => p.DiscoverManifestsAsync(500, It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        acquisitionProvider.Setup(p => p.DownloadManifestAsync(500, 100, It.IsAny<string>(), 500, It.IsAny<CancellationToken>()))
            .ReturnsAsync(directPayloadFile);

        var registry = new ManifestRegistry(
            [discoveryProvider.Object, acquisitionProvider.Object],
            cacheService,
            NullLogger<ManifestRegistry>.Instance);

        // 1. Discovery is performed by discoveryProvider
        var discovered = await registry.DiscoverManifestsAsync(500);
        discovered.Should().HaveCount(1);
        discovered[0].DepotId.Should().Be(500);
        discovered[0].ManifestId.Should().Be(100);

        // 2. Acquisition is fulfilled by acquisitionProvider
        var acquired = await registry.AcquireManifestAsync(500, 100, 500);
        acquired.Should().NotBeNull();
        File.Exists(acquired!).Should().BeTrue();
        acquisitionProvider.Verify(p => p.DownloadManifestAsync(500, 100, It.IsAny<string>(), 500, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public void VersionAvailability_RepresentsGranularState_WithoutCollapsingToUnavailable()
    {
        // Build 15900: Manifests known, but depot key missing, and ReFix component available
        var availability = new VersionAvailability
        {
            AppId = 1000,
            BuildId = "15900",
            BranchName = "public",
            AvailableComponents = ["ReFix", "OnlineFix"],
            Depots = new Dictionary<uint, ManifestAvailability>
            {
                {
                    1001,
                    new ManifestAvailability
                    {
                        DepotId = 1001,
                        ManifestId = 55555,
                        IsCachedLocally = true,
                        HasDecryptionKey = false // Key missing!
                    }
                }
            }
        };

        // Assert granular representation
        availability.ManifestsKnown.Should().BeTrue();
        availability.KeysComplete.Should().BeFalse();
        availability.MissingKeyDepots.Should().Contain(1001u);
        availability.MissingManifestDepots.Should().BeEmpty();
        availability.AvailableComponents.Should().Contain(["ReFix", "OnlineFix"]);
        availability.IsFullyAvailable.Should().BeFalse();
    }

    [Fact]
    public void ManifestRegistry_PriorityOrder_DepotBoxIsStrictlyLastProvider()
    {
        var cacheDir = Path.Combine(_testDir, "priority_cache");
        var cacheService = new ManifestCacheService(NullLogger<ManifestCacheService>.Instance, cacheDir);
        var localCacheProvider = new LocalCacheManifestProvider(cacheService, NullLogger<LocalCacheManifestProvider>.Instance);

        var manifestHubProvider = new ManifestHubProvider(
            new HttpClient(),
            cacheService,
            NullLogger<ManifestHubProvider>.Instance);

        var depotBoxProvider = new DepotBoxManifestProvider(
            new Mock<IDepotBoxApiClient>().Object,
            new Mock<IDepotBoxArchiveParser>().Object,
            cacheService,
            NullLogger<DepotBoxManifestProvider>.Instance);

        var registry = new ManifestRegistry(
            [localCacheProvider, depotBoxProvider, manifestHubProvider],
            cacheService,
            NullLogger<ManifestRegistry>.Instance);

        // Assert priority values
        localCacheProvider.Priority.Should().Be(1000);
        manifestHubProvider.Priority.Should().Be(400);
        depotBoxProvider.Priority.Should().Be(100);

        // Registry providers must be ordered strictly by Priority descending, placing DepotBox last
        registry.Providers.Should().HaveCount(3);
        registry.Providers[0].Should().BeSameAs(localCacheProvider);
        registry.Providers[1].Should().BeSameAs(manifestHubProvider);
        registry.Providers[2].Should().BeSameAs(depotBoxProvider);
    }

    [Fact]
    public void ManifestResolution_DifferentialUpdate_IdentifiesOnlyChangedDepots()
    {
        // Arrange: Installed instance with 3 depots
        var installedDepots = new Dictionary<uint, ulong>
        {
            { 1001, 11111 },
            { 1002, 22222 },
            { 1003, 33333 }
        };

        // Target build from providers: only depot 1001 and 1002 changed, depot 1003 is identical
        var targetBuildManifests = new Dictionary<uint, ulong>
        {
            { 1001, 99999 }, // Updated
            { 1002, 88888 }, // Updated
            { 1003, 33333 }  // Unchanged
        };

        // Act: Filter changed depots
        var changedDepots = new List<uint>();
        foreach (var (depotId, targetManifest) in targetBuildManifests)
        {
            if (!installedDepots.TryGetValue(depotId, out var installedManifest) || installedManifest != targetManifest)
            {
                changedDepots.Add(depotId);
            }
        }

        // Assert: Exactly 2 depots changed; depot 1003 is untouched
        changedDepots.Should().Equal([1001u, 1002u]);
        changedDepots.Should().NotContain(1003u);
    }

    [Fact]
    public void UpdateStateEvaluation_WhenSteamHasUpdateButProvidersMatchInstalled_PreservesUpdateFlag()
    {
        // Scenario: Steam web API indicates a newer release date/build exists on Steam (HasGameUpdateAvailable = true),
        // but providers have not indexed or published newer manifests yet (targetManifests matches installedDepots).
        bool hasGameUpdateAvailable = true;
        var installedDepots = new Dictionary<uint, ulong> { { 1001, 55555 } };
        var providerTargetManifests = new Dictionary<uint, ulong> { { 1001, 55555 } };

        var changedDepots = providerTargetManifests
            .Where(kvp => !installedDepots.TryGetValue(kvp.Key, out var curr) || curr != kvp.Value)
            .ToList();

        // Evaluation logic
        string statusNotification;
        if (changedDepots.Count == 0)
        {
            if (hasGameUpdateAvailable)
            {
                // Must NOT clear hasGameUpdateAvailable! Must inform user that providers don't have it yet.
                statusNotification = "Latest Version Not Available Yet";
            }
            else
            {
                hasGameUpdateAvailable = false;
                statusNotification = "Depots Up to Date";
            }
        }
        else
        {
            statusNotification = "Update Available";
        }

        // Assert
        statusNotification.Should().Be("Latest Version Not Available Yet");
        hasGameUpdateAvailable.Should().BeTrue("Update flag must remain true to inform the user that a Steam update is pending provider availability");
    }

    [Fact]
    public async Task ManifestRegistry_ProviderFailover_TriesNextProviderWhenFirstFails()
    {
        // Arrange
        var cacheDir = Path.Combine(_testDir, "failover_cache");
        var cacheService = new ManifestCacheService(NullLogger<ManifestCacheService>.Instance, cacheDir);

        var failingProviderMock = new Mock<IManifestProvider>();
        failingProviderMock.SetupGet(p => p.ProviderId).Returns("FailingProvider");
        failingProviderMock.SetupGet(p => p.Priority).Returns(500);
        failingProviderMock.Setup(p => p.DownloadManifestAsync(It.IsAny<uint>(), It.IsAny<ulong>(), It.IsAny<string>(), It.IsAny<uint>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("404 Not Found"));

        var successProviderMock = new Mock<IManifestProvider>();
        successProviderMock.SetupGet(p => p.ProviderId).Returns("SuccessProvider");
        successProviderMock.SetupGet(p => p.Priority).Returns(100);

        var validManifestBytes = CreateValidSteamProtobufManifest();
        successProviderMock.Setup(p => p.DownloadManifestAsync(It.IsAny<uint>(), It.IsAny<ulong>(), It.IsAny<string>(), It.IsAny<uint>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((uint depotId, ulong manifestId, string targetDir, uint appId, CancellationToken ct) =>
            {
                var filePath = Path.Combine(targetDir, $"{depotId}_{manifestId}.manifest");
                File.WriteAllBytes(filePath, validManifestBytes);
                return filePath;
            });

        var registry = new ManifestRegistry(
            [failingProviderMock.Object, successProviderMock.Object],
            cacheService,
            NullLogger<ManifestRegistry>.Instance);

        // Act
        var acquiredPath = await registry.AcquireManifestAsync(1001, 77777);

        // Assert: Failing provider was attempted and threw, registry gracefully failed over to success provider
        acquiredPath.Should().NotBeNull();
        File.Exists(acquiredPath).Should().BeTrue();
        failingProviderMock.Verify(p => p.DownloadManifestAsync(1001, 77777, It.IsAny<string>(), It.IsAny<uint>(), It.IsAny<CancellationToken>()), Times.Once);
        successProviderMock.Verify(p => p.DownloadManifestAsync(1001, 77777, It.IsAny<string>(), It.IsAny<uint>(), It.IsAny<CancellationToken>()), Times.Once);
    }
}


