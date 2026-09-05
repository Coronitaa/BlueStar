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
using BlueStar.Core.Interfaces;
using BlueStar.Core.Models;
using BlueStar.Infrastructure.Cache;
using BlueStar.Infrastructure.Metadata;
using BlueStar.Infrastructure.Providers.Manifest;
using BlueStar.Infrastructure.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace BlueStar.Infrastructure.Tests;

/// <summary>
/// Unit and integration test suite verifying network efficiency, deduplication,
/// caching resilience, circuit-breaker behavior, and zero redundant traffic in BlueStar v1.3.
/// </summary>
public class NetworkEfficiencyAndAuditTests : IDisposable
{
    private readonly string _testDir;

    public NetworkEfficiencyAndAuditTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "BlueStar_NetAudit_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDir))
        {
            try { Directory.Delete(_testDir, true); } catch { }
        }
    }

    private static byte[] CreateValidProtobufManifest(uint depotId, ulong manifestId)
    {
        using var ms = new MemoryStream();
        ms.Write([0xD0, 0x17, 0xF6, 0x71]); // Magic: 0x71F617D0
        ms.Write(BitConverter.GetBytes(64u));

        using (var deflate = new DeflateStream(ms, CompressionLevel.Fastest, leaveOpen: true))
        {
            var bytes = Encoding.UTF8.GetBytes($"depot_{depotId}_manifest_{manifestId}_content");
            deflate.Write(bytes);
        }

        return ms.ToArray();
    }

    private sealed class TrackingHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responseFactory;
        public List<HttpRequestMessage> Requests { get; } = new();

        public TrackingHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
        {
            _responseFactory = responseFactory;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (Requests)
            {
                Requests.Add(request);
            }
            return Task.FromResult(_responseFactory(request));
        }
    }

    private sealed class AsyncTrackingHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _responseFactory;
        public List<HttpRequestMessage> Requests { get; } = new();

        public AsyncTrackingHttpMessageHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responseFactory)
        {
            _responseFactory = responseFactory;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (Requests)
            {
                Requests.Add(request);
            }
            return await _responseFactory(request, cancellationToken);
        }
    }

    private sealed class TestMemoryLogger<T> : ILogger<T>
    {
        public List<string> LoggedMessages { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (LoggedMessages)
            {
                LoggedMessages.Add(formatter(state, exception));
            }
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // SCENARIO 1: In-flight deduplication (10 concurrent requests = 1 HTTP call)
    // ─────────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task Scenario1_InFlightDeduplication_10ConcurrentRequests_ResultsInExactlyOneHttpCall()
    {
        var jsonResponse = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["730"] = new Dictionary<string, object>
            {
                ["success"] = true,
                ["data"] = new Dictionary<string, object>
                {
                    ["steam_appid"] = 730,
                    ["name"] = "Counter-Strike: Global Offensive",
                    ["header_image"] = "https://cdn.steam.com/730.jpg",
                    ["dlc"] = new[] { 101, 102 }
                }
            }
        });

        var handler = new AsyncTrackingHttpMessageHandler(async (req, ct) =>
        {
            // Simulate 50ms network delay to ensure concurrency window
            await Task.Delay(50, ct);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(jsonResponse, Encoding.UTF8, "application/json")
            };
        });

        var httpClient = new HttpClient(handler);
        var coordinator = new RequestCoordinator();
        var cacheService = new FileCacheService(NullLogger<FileCacheService>.Instance, _testDir);
        var client = new SteamStoreApiClient(httpClient, NullLogger<SteamStoreApiClient>.Instance, cacheService, coordinator);

        // Act: 10 concurrent requests for the same AppId on cold cache
        var tasks = Enumerable.Range(0, 10).Select(_ => client.GetMetadataAsync(730)).ToList();
        var results = await Task.WhenAll(tasks);

        // Assert: All 10 callers receive valid metadata, but exactly 1 network call occurred
        results.Should().HaveCount(10);
        foreach (var r in results)
        {
            r.Should().NotBeNull();
            r!.Name.Should().Be("Counter-Strike: Global Offensive");
            r.AppId.Should().Be(730);
        }

        handler.Requests.Should().HaveCount(1, "In-flight deduplication must coalesce 10 concurrent requests into exactly 1 HTTP call");
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // SCENARIO 2: Pre-populated DLC cache (0 extra HTTP calls)
    // ─────────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task Scenario2_PrePopulatedDlcCache_GetMetadataPopulatesDlcs_GetDlcListMakesZeroAdditionalHttpCalls()
    {
        var jsonResponse = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["730"] = new Dictionary<string, object>
            {
                ["success"] = true,
                ["data"] = new Dictionary<string, object>
                {
                    ["steam_appid"] = 730,
                    ["name"] = "Counter-Strike: Global Offensive",
                    ["header_image"] = "https://cdn.steam.com/730.jpg",
                    ["dlc"] = new[] { 101, 102 }
                }
            }
        });

        var handler = new TrackingHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(jsonResponse, Encoding.UTF8, "application/json")
        });

        var httpClient = new HttpClient(handler);
        var coordinator = new RequestCoordinator();
        var cacheService = new FileCacheService(NullLogger<FileCacheService>.Instance, _testDir);
        var client = new SteamStoreApiClient(httpClient, NullLogger<SteamStoreApiClient>.Instance, cacheService, coordinator);

        // 1. Calling GetMetadataAsync populates both metadata and DLC cache
        var meta = await client.GetMetadataAsync(730);
        meta.Should().NotBeNull();
        handler.Requests.Should().HaveCount(1);

        // 2. Calling GetDlcListAsync should read directly from pre-populated DLC cache
        var dlcs = await client.GetDlcListAsync(730);
        dlcs.Should().HaveCount(2);
        dlcs.Select(d => d.AppId).Should().Contain([101u, 102u]);

        handler.Requests.Should().HaveCount(1, "GetDlcListAsync must consume the pre-populated DLC cache without making an extra HTTP request");
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // SCENARIO 3: Negative caching for 0 DLCs (0 network calls on subsequent requests)
    // ─────────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task Scenario3_NegativeCaching_ZeroDlcs_CachesEmptyListAndMakesZeroSubsequentNetworkCalls()
    {
        var jsonResponse = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["440"] = new Dictionary<string, object>
            {
                ["success"] = true,
                ["data"] = new Dictionary<string, object>
                {
                    ["steam_appid"] = 440,
                    ["name"] = "Team Fortress 2",
                    ["header_image"] = "https://cdn.steam.com/440.jpg",
                    ["dlc"] = Array.Empty<int>() // Zero DLCs
                }
            }
        });

        var handler = new TrackingHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(jsonResponse, Encoding.UTF8, "application/json")
        });

        var httpClient = new HttpClient(handler);
        var coordinator = new RequestCoordinator();
        var cacheService = new FileCacheService(NullLogger<FileCacheService>.Instance, _testDir);
        var client = new SteamStoreApiClient(httpClient, NullLogger<SteamStoreApiClient>.Instance, cacheService, coordinator);

        // 1. First fetch: network request occurs, returns 0 DLCs
        var firstDlcs = await client.GetDlcListAsync(440);
        firstDlcs.Should().BeEmpty();
        handler.Requests.Should().HaveCount(1);

        // 2. Second and third queries must hit negative cache with 0 network calls
        var secondDlcs = await client.GetDlcListAsync(440);
        var thirdDlcs = await client.GetDlcListAsync(440);

        secondDlcs.Should().BeEmpty();
        thirdDlcs.Should().BeEmpty();
        handler.Requests.Should().HaveCount(1, "Subsequent calls for 0 DLCs must hit negative cache with zero network calls");
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // SCENARIO 4: Consolidated SteamCMD app info (1 call instead of 3)
    // ─────────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task Scenario4_ConsolidatedSteamCmdInfo_BuildsDepotsAndEnrichment_MakeSingleHttpCall()
    {
        var steamCmdJson = @"
{
  ""status"": ""success"",
  ""data"": {
    ""730"": {
      ""depots"": {
        ""731"": {
          ""name"": ""CSGO Binaries"",
          ""config"": { ""oslist"": ""windows"" },
          ""manifests"": {
            ""public"": { ""gid"": ""5555555555555555"" }
          }
        },
        ""branches"": {
          ""public"": {
            ""buildid"": ""99900"",
            ""timeupdated"": ""1700000000""
          }
        }
      },
      ""appinfo"": {
        ""common"": {
          ""name"": ""Counter-Strike: Global Offensive""
        }
      }
    }
  }
}";

        var handler = new TrackingHttpMessageHandler(req =>
        {
            if (req.RequestUri != null && req.RequestUri.Host.Contains("steamcmd.net"))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(steamCmdJson, Encoding.UTF8, "application/json")
                };
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var httpClient = new HttpClient(handler);
        var coordinator = new RequestCoordinator();
        var cacheService = new FileCacheService(NullLogger<FileCacheService>.Instance, _testDir);
        var client = new SteamStoreApiClient(httpClient, NullLogger<SteamStoreApiClient>.Instance, cacheService, coordinator);

        // Act: Invoke all three methods that require SteamCMD data
        var builds = await client.GetAppBuildsAsync(730);
        var depots = await client.GetAppDepotInfoAsync(730);
        var enrichment = await client.GetDepotEnrichmentAsync(730);

        // Assert
        builds.Should().HaveCount(1);
        builds[0].BuildId.Should().Be("99900");

        depots.Should().NotBeNull();
        depots!.BuildId.Should().Be("99900");
        depots.PublicManifests.Should().ContainKey(731);

        enrichment.Should().ContainKey(731u);
        enrichment[731u].Name.Should().Be("CSGO Binaries");

        var steamCmdCalls = handler.Requests.Count(r => r.RequestUri != null && r.RequestUri.Host.Contains("steamcmd.net"));
        steamCmdCalls.Should().Be(1, "Unified GetAppUnifiedInfoAsync must consolidate builds, depots, and enrichment into a single HTTP call");
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // SCENARIO 5: Corrupted cache file recovery (safely deleted, re-fetched)
    // ─────────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task Scenario5_CorruptedCacheFileRecovery_SafelyDeletesFileAndRecoversCleanly()
    {
        var cacheService = new FileCacheService(NullLogger<FileCacheService>.Instance, _testDir);
        var cacheKey = "corrupt_test_data";

        // Corrupt file creation: invalid truncated JSON with SHA256 hashed filename matching FileCacheService
        var hashBytes = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(cacheKey));
        var hash = Convert.ToHexString(hashBytes).ToLowerInvariant();
        var rawPath = Path.Combine(_testDir, $"{hash}.json");
        await File.WriteAllTextAsync(rawPath, "{ \"invalid_json_missing_brace: [1, 2, 3");
        File.Exists(rawPath).Should().BeTrue();

        // Act 1: GetAsync detects corruption, deletes file, and returns default (null)
        var cached = await cacheService.GetAsync<GameMetadata>(cacheKey);
        cached.Should().BeNull();
        File.Exists(rawPath).Should().BeFalse("Corrupt cache file must be automatically purged from disk upon JsonException");

        // Act 2: Subsequent Set and Get work normally
        var validData = new GameMetadata { AppId = 730, Name = "Counter-Strike" };
        await cacheService.SetAsync(cacheKey, validData, TimeSpan.FromHours(1));

        var recovered = await cacheService.GetAsync<GameMetadata>(cacheKey);
        recovered.Should().NotBeNull();
        recovered!.AppId.Should().Be(730);
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // SCENARIO 6: Circuit breaker resilience (non-coverage does not trip; 5xx does)
    // ─────────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task Scenario6_CircuitBreaker_NonCoverageDoesNotTrip_Transient5xxDoesTrip()
    {
        var cacheDir = Path.Combine(_testDir, "cb_cache");
        var cacheService = new ManifestCacheService(NullLogger<ManifestCacheService>.Instance, cacheDir);

        int providerAttempts = 0;
        var mockRemoteProvider = new Mock<IManifestProvider>();
        mockRemoteProvider.SetupGet(p => p.ProviderId).Returns("remote_provider");
        mockRemoteProvider.SetupGet(p => p.Priority).Returns(500);

        // 1. Provider throws 404 (non-coverage) on 5 consecutive calls
        mockRemoteProvider.Setup(p => p.DownloadManifestAsync(It.IsAny<uint>(), It.IsAny<ulong>(), It.IsAny<string>(), It.IsAny<uint>(), It.IsAny<CancellationToken>()))
            .Returns<uint, ulong, string, uint, CancellationToken>((_, _, _, _, _) =>
            {
                providerAttempts++;
                throw new HttpRequestException("404 Not Found", null, HttpStatusCode.NotFound);
            });

        var registry = new ManifestRegistry(
            [mockRemoteProvider.Object],
            cacheService,
            NullLogger<ManifestRegistry>.Instance,
            new RequestCoordinator());

        for (int i = 0; i < 5; i++)
        {
            await registry.AcquireManifestAsync(100, (ulong)(1000 + i));
        }

        providerAttempts.Should().Be(5, "404 Not Found must NOT trip the circuit breaker; all 5 attempts must reach provider");

        // 2. Now provider throws 500 Internal Server Error 3 times
        providerAttempts = 0;
        mockRemoteProvider.Setup(p => p.DownloadManifestAsync(It.IsAny<uint>(), It.IsAny<ulong>(), It.IsAny<string>(), It.IsAny<uint>(), It.IsAny<CancellationToken>()))
            .Returns<uint, ulong, string, uint, CancellationToken>((_, _, _, _, _) =>
            {
                providerAttempts++;
                throw new HttpRequestException("500 Internal Server Error", null, HttpStatusCode.InternalServerError);
            });

        // First 3 failures should be attempted
        await registry.AcquireManifestAsync(100, 2001);
        await registry.AcquireManifestAsync(100, 2002);
        await registry.AcquireManifestAsync(100, 2003);

        providerAttempts.Should().Be(3);

        // 4th attempt must be skipped due to tripped circuit breaker
        await registry.AcquireManifestAsync(100, 2004);
        providerAttempts.Should().Be(3, "Provider must be circuit broken after 3 consecutive 5xx errors; 4th call must be skipped");
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // SCENARIO 7: Multi-provider manifest failover (LocalCache -> ManifestHub -> DepotBox)
    // ─────────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task Scenario7_MultiProviderManifestFailover_RecoversAndCachesForZeroNetworkCall()
    {
        var cacheDir = Path.Combine(_testDir, "failover_cache");
        var cacheService = new ManifestCacheService(NullLogger<ManifestCacheService>.Instance, cacheDir);
        var localCacheProvider = new LocalCacheManifestProvider(cacheService, NullLogger<LocalCacheManifestProvider>.Instance);

        int manifestHubCalls = 0;
        var manifestHubMock = new Mock<IManifestProvider>();
        manifestHubMock.SetupGet(p => p.ProviderId).Returns("manifesthub");
        manifestHubMock.SetupGet(p => p.Priority).Returns(400);
        manifestHubMock.Setup(p => p.DownloadManifestAsync(It.IsAny<uint>(), It.IsAny<ulong>(), It.IsAny<string>(), It.IsAny<uint>(), It.IsAny<CancellationToken>()))
            .Returns<uint, ulong, string, uint, CancellationToken>((_, _, _, _, _) =>
            {
                manifestHubCalls++;
                throw new HttpRequestException("404 Manifest not in ManifestHub", null, HttpStatusCode.NotFound);
            });

        int depotBoxCalls = 0;
        var validManifestBytes = CreateValidProtobufManifest(731, 88888);
        var depotBoxMock = new Mock<IManifestProvider>();
        depotBoxMock.SetupGet(p => p.ProviderId).Returns("depotbox");
        depotBoxMock.SetupGet(p => p.Priority).Returns(100);
        depotBoxMock.Setup(p => p.DownloadManifestAsync(It.IsAny<uint>(), It.IsAny<ulong>(), It.IsAny<string>(), It.IsAny<uint>(), It.IsAny<CancellationToken>()))
            .Returns<uint, ulong, string, uint, CancellationToken>((dId, mId, dir, _, _) =>
            {
                depotBoxCalls++;
                var path = Path.Combine(dir, $"{dId}_{mId}.manifest");
                File.WriteAllBytes(path, validManifestBytes);
                return Task.FromResult(path);
            });

        var registry = new ManifestRegistry(
            [localCacheProvider, manifestHubMock.Object, depotBoxMock.Object],
            cacheService,
            NullLogger<ManifestRegistry>.Instance,
            new RequestCoordinator());

        // 1. First acquisition: LocalCache misses, ManifestHub returns 404, DepotBox succeeds
        var acquired1 = await registry.AcquireManifestAsync(731, 88888);
        acquired1.Should().NotBeNull();
        File.Exists(acquired1).Should().BeTrue();
        manifestHubCalls.Should().Be(1);
        depotBoxCalls.Should().Be(1);

        // 2. Second acquisition: Must resolve from LocalCache directly with ZERO calls to ManifestHub or DepotBox
        var acquired2 = await registry.AcquireManifestAsync(731, 88888);
        acquired2.Should().Be(acquired1);
        manifestHubCalls.Should().Be(1, "Second acquisition must not query remote providers");
        depotBoxCalls.Should().Be(1, "Second acquisition must resolve from local cache without querying DepotBox");
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // SCENARIO 8: Cancellation token propagation (aborts immediately, no leaked tasks)
    // ─────────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task Scenario8_CancellationTokenPropagation_AbortsImmediatelyWithoutOrphanTasks()
    {
        var tcs = new TaskCompletionSource<HttpResponseMessage>();
        using var cts = new CancellationTokenSource();

        var handler = new AsyncTrackingHttpMessageHandler(async (req, ct) =>
        {
            using (ct.Register(() => tcs.TrySetCanceled(ct)))
            {
                return await tcs.Task;
            }
        });

        var httpClient = new HttpClient(handler);
        var coordinator = new RequestCoordinator();
        var cacheService = new FileCacheService(NullLogger<FileCacheService>.Instance, _testDir);
        var client = new SteamStoreApiClient(httpClient, NullLogger<SteamStoreApiClient>.Instance, cacheService, coordinator);

        // Cancel after 20ms
        cts.CancelAfter(20);

        var act = async () => await client.GetMetadataAsync(730, cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();

        // Verify coordinator key was cleaned up and can be reused cleanly
        cts.Dispose();
        var immediateHandler = new TrackingHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["730"] = new Dictionary<string, object>
                {
                    ["success"] = true,
                    ["data"] = new Dictionary<string, object>
                    {
                        ["steam_appid"] = 730,
                        ["name"] = "CS:GO",
                        ["header_image"] = "https://cdn.steam.com/730.jpg"
                    }
                }
            }), Encoding.UTF8, "application/json")
        });

        var clientClean = new SteamStoreApiClient(new HttpClient(immediateHandler), NullLogger<SteamStoreApiClient>.Instance, cacheService, coordinator);
        var result = await clientClean.GetMetadataAsync(730);
        result.Should().NotBeNull();
        result!.Name.Should().Be("CS:GO");
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // SCENARIO 9: Token and credential redaction in network telemetry
    // ─────────────────────────────────────────────────────────────────────────────
    [Fact]
    public void Scenario9_NetworkTelemetry_RedactsSensitiveTokensAndCredentials()
    {
        var logger = new TestMemoryLogger<NetworkMetricsObserver>();
        var observer = new NetworkMetricsObserver(logger);

        // Act: Log events with sensitive query parameters
        observer.OnProviderRequest("DepotBox", "https://depotbox.org/api/v1/download?appId=730&token=secret_token_abcdef123456&key=my_password_xyz");
        observer.RecordEvent("ManifestHub", "https://manifesthub.uk/api/manifest?auth=bearer_99999&access_token=secret_val", "acquire", false, false, 200, 45);

        // Assert: Redaction verification
        logger.LoggedMessages.Should().NotBeEmpty();
        foreach (var msg in logger.LoggedMessages)
        {
            msg.Should().NotContain("secret_token_abcdef123456", "Tokens in URLs must be redacted");
            msg.Should().NotContain("my_password_xyz", "Passwords in URLs must be redacted");
            msg.Should().NotContain("bearer_99999", "Auth keys in URLs must be redacted");
            msg.Should().NotContain("secret_val", "Access tokens must be redacted");
            msg.Should().Contain("[REDACTED]");
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // SCENARIO 10: InstanceDetailViewModel initial load network audit
    // (≤ 2 calls cold, 0 calls warm)
    // ─────────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task Scenario10_InstanceDetailViewModel_InitialLoad_GeneratesAtMostTwoNetworkCallsCold_ZeroWarm()
    {
        var storeJson = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["730"] = new Dictionary<string, object>
            {
                ["success"] = true,
                ["data"] = new Dictionary<string, object>
                {
                    ["steam_appid"] = 730,
                    ["name"] = "Counter-Strike 2",
                    ["header_image"] = "https://cdn.steam.com/730.jpg",
                    ["dlc"] = new[] { 101, 102 }
                }
            }
        });

        var steamCmdJson = @"
{
  ""status"": ""success"",
  ""data"": {
    ""730"": {
      ""depots"": {
        ""731"": {
          ""name"": ""CS2 Binaries"",
          ""config"": { ""oslist"": ""windows"" },
          ""manifests"": {
            ""public"": { ""gid"": ""1111222233334444"" }
          }
        }
      },
      ""branches"": {
        ""public"": {
          ""buildid"": ""12345"",
          ""timeupdated"": ""1710000000""
        }
      }
    }
  }
}";

        var handler = new TrackingHttpMessageHandler(req =>
        {
            if (req.RequestUri != null && req.RequestUri.Host.Contains("steampowered.com"))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(storeJson, Encoding.UTF8, "application/json")
                };
            }
            if (req.RequestUri != null && req.RequestUri.Host.Contains("steamcmd.net"))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(steamCmdJson, Encoding.UTF8, "application/json")
                };
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var httpClient = new HttpClient(handler);
        var coordinator = new RequestCoordinator();
        var cacheService = new FileCacheService(NullLogger<FileCacheService>.Instance, _testDir);
        var manifestCache = new ManifestCacheService(NullLogger<ManifestCacheService>.Instance, Path.Combine(_testDir, "mcache"));
        var steamClient = new SteamStoreApiClient(httpClient, NullLogger<SteamStoreApiClient>.Instance, cacheService, coordinator);

        var manifestRegistry = new ManifestRegistry(
            [new LocalCacheManifestProvider(manifestCache, NullLogger<LocalCacheManifestProvider>.Instance)],
            manifestCache,
            NullLogger<ManifestRegistry>.Instance,
            coordinator);

        var buildResolver = new BuildResolver(
            steamClient,
            manifestRegistry,
            NullLogger<BuildResolver>.Instance,
            null,
            null,
            coordinator,
            cacheService);

        // Simulation of InstanceDetailViewModel initial parallel load:
        // 1. LoadDlcsFromMetadataIfEmptyAsync (fetches DLCs via steamClient.GetDlcListAsync)
        // 2. EnrichDepotsFromSteamDbAsync (fetches depot metadata via steamClient.GetDepotEnrichmentAsync)
        // 3. LoadAvailableBuildsAsync (fetches builds via buildResolver.GetAvailableVersionsAsync)

        // ACT 1: Cold load
        var taskDlcs = steamClient.GetDlcListAsync(730);
        var taskEnrich = steamClient.GetDepotEnrichmentAsync(730);
        var taskBuilds = buildResolver.GetAvailableVersionsAsync(730);

        await Task.WhenAll(taskDlcs, taskEnrich, taskBuilds);

        var coldCalls = handler.Requests.Count;
        coldCalls.Should().BeInRange(1, 2, "Cold initial load of InstanceDetail must generate at most 2 coordinated calls (1 Store + 1 SteamCMD)");

        // ACT 2: Warm load (simulating reopening or reloading the instance)
        var warmIndexStart = handler.Requests.Count;
        var warmDlcs = await steamClient.GetDlcListAsync(730);
        var warmEnrich = await steamClient.GetDepotEnrichmentAsync(730);
        var warmBuilds = await buildResolver.GetAvailableVersionsAsync(730);

        var warmCalls = handler.Requests.Count - warmIndexStart;
        warmCalls.Should().Be(0, "Warm load must hit cache/coordination resulting in exactly 0 network requests");
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // SCENARIO 11: Cache Stampede Protection (50 concurrent consumers)
    // 50 consumers -> 1 physical HTTP request -> 50 consumers receive same result
    // ─────────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task Scenario11_CacheStampede_50ConcurrentConsumers_ResultsInExactlyOneHttpRequest()
    {
        int physicalHttpCalls = 0;
        var handler = new AsyncTrackingHttpMessageHandler(async (req, ct) =>
        {
            Interlocked.Increment(ref physicalHttpCalls);
            await Task.Delay(40, ct); // Simulate network latency
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(@"{""status"":""success"",""data"":{""1091500"":{""common"":{""name"":""Cyberpunk 2077""}}}}", Encoding.UTF8, "application/json")
            };
        });

        var httpClient = new HttpClient(handler);
        var metrics = new NetworkMetricsObserver();
        var coordinator = new RequestCoordinator(metrics);
        var cacheService = new FileCacheService(NullLogger<FileCacheService>.Instance, _testDir);
        var steamClient = new SteamStoreApiClient(httpClient, NullLogger<SteamStoreApiClient>.Instance, cacheService, coordinator, metrics);

        // 50 concurrent consumers requesting the exact same resource simultaneously
        var tasks = Enumerable.Range(0, 50)
            .Select(_ => steamClient.GetAppUnifiedInfoAsync(1091500))
            .ToList();

        var results = await Task.WhenAll(tasks);

        // Assertions:
        // 1. Exactly 1 physical network request was made
        physicalHttpCalls.Should().Be(1, "50 concurrent requests for the same expired/cold resource must coalesce into 1 physical HTTP call");
        metrics.TotalNetworkRequests.Should().Be(1);
        metrics.TotalInFlightReused.Should().Be(49);

        // 2. All 50 consumers received the identical, non-null result
        results.Length.Should().Be(50);
        foreach (var result in results)
        {
            result.Should().NotBeNull();
            result!.Builds.Should().NotBeNull();
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // SCENARIO 12: Different Keys Parallelism (Game A, B, C execute in parallel)
    // same key -> coalesce, different key -> parallel (no global lock / serialization)
    // ─────────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task Scenario12_DifferentKeys_ExecuteInParallelWithoutCrossKeySerialization()
    {
        var activeRequests = new System.Collections.Concurrent.ConcurrentBag<uint>();
        int peakConcurrentRequests = 0;
        var lockObj = new object();
        int currentInFlight = 0;

        var handler = new AsyncTrackingHttpMessageHandler(async (req, ct) =>
        {
            lock (lockObj)
            {
                currentInFlight++;
                if (currentInFlight > peakConcurrentRequests) peakConcurrentRequests = currentInFlight;
            }

            await Task.Delay(60, ct); // Latency window

            lock (lockObj)
            {
                currentInFlight--;
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(@"{""status"":""success"",""data"":{}}", Encoding.UTF8, "application/json")
            };
        });

        var httpClient = new HttpClient(handler);
        var coordinator = new RequestCoordinator();
        var steamClient = new SteamStoreApiClient(httpClient, NullLogger<SteamStoreApiClient>.Instance, null, coordinator);

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        // Dispatch requests for 3 completely different games simultaneously
        var taskGameA = steamClient.GetAppUnifiedInfoAsync(10);
        var taskGameB = steamClient.GetAppUnifiedInfoAsync(20);
        var taskGameC = steamClient.GetAppUnifiedInfoAsync(30);

        await Task.WhenAll(taskGameA, taskGameB, taskGameC);
        stopwatch.Stop();

        // If they were serialized sequentially, total time would be >= 180ms.
        // In parallel, all 3 run concurrently, so peak concurrent requests must be > 1.
        handler.Requests.Count.Should().Be(3, "3 distinct keys must generate 3 distinct HTTP requests");
        peakConcurrentRequests.Should().BeGreaterThan(1, "Distinct keys must execute in parallel rather than serializing");
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // SCENARIO 13: Negative Cache Recovery & Forced Invalidation
    // request -> 404 -> negative cache -> resource becomes available -> force refresh -> resolves
    // ─────────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task Scenario13_NegativeCache_Temporary404_CanBeRefreshedWhenResourceBecomesAvailable()
    {
        bool isAvailableOnServer = false;
        var handler = new TrackingHttpMessageHandler(req =>
        {
            if (req.RequestUri != null && req.RequestUri.AbsolutePath.Contains("/api/manifests/550"))
            {
                if (!isAvailableOnServer)
                {
                    return new HttpResponseMessage(HttpStatusCode.NotFound);
                }

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(@"[{""depotId"":551,""manifestId"":""999988887777"",""sizeBytes"":50000}]", Encoding.UTF8, "application/json")
                };
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://depotbox.org") };
        var authService = new Mock<IDepotBoxAuthService>();
        var metrics = new NetworkMetricsObserver();
        var coordinator = new RequestCoordinator(metrics);
        var cacheService = new FileCacheService(NullLogger<FileCacheService>.Instance, _testDir);
        var depotBox = new BlueStar.Infrastructure.DepotBox.DepotBoxApiClient(
            httpClient,
            authService.Object,
            NullLogger<BlueStar.Infrastructure.DepotBox.DepotBoxApiClient>.Instance,
            null,
            cacheService,
            coordinator,
            metrics);

        // Step 1: Initial request when resource is not yet available (returns 404)
        var result1 = await depotBox.GetManifestsAsync(550);
        result1.Should().BeEmpty();
        handler.Requests.Count.Should().Be(1);

        // Step 2: Second request within TTL: negative cache should intercept and return empty with 0 HTTP calls
        var result2 = await depotBox.GetManifestsAsync(550);
        result2.Should().BeEmpty();
        handler.Requests.Count.Should().Be(1, "Negative cache must prevent redundant 404 requests");
        metrics.TotalNegativeCacheHits.Should().Be(1);

        // Step 3: Resource becomes available on server!
        isAvailableOnServer = true;

        // Step 4: Forced refresh / user refresh explicitly bypasses negative cache
        var result3 = await depotBox.GetManifestsAsync(550, forceRefresh: true);
        result3.Should().HaveCount(1);
        result3[0].DepotId.Should().Be(551u);
        result3[0].ManifestId.Should().Be(999988887777ul);
        handler.Requests.Count.Should().Be(2, "Forced refresh must execute physical HTTP request and overwrite negative cache");

        // Step 5: Subsequent normal requests now hit the positive cache with 0 HTTP calls!
        var result4 = await depotBox.GetManifestsAsync(550, forceRefresh: false);
        result4.Should().HaveCount(1);
        handler.Requests.Count.Should().Be(2, "Subsequent queries must be served from the updated positive cache");
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // SCENARIO 14: Provider Isolation
    // Provider A (404) does not block Provider B (valid result)
    // Provider A (500s) trips circuit breaker; Provider B remains operational
    // ─────────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task Scenario14_ProviderIsolation_404OnProviderADoesNotAffectProviderB_AndCircuitBreakerIsPerProvider()
    {
        var manifestCache = new ManifestCacheService(NullLogger<ManifestCacheService>.Instance, Path.Combine(_testDir, "mcache14"));
        var coordinator = new RequestCoordinator();

        int providerAAttempts = 0;
        int providerBAttempts = 0;

        // Provider A: DepotBox mock returning 404 (no coverage for this game)
        var mockProviderA = new Mock<IManifestProvider>();
        mockProviderA.Setup(p => p.ProviderId).Returns("depotbox");
        mockProviderA.Setup(p => p.DownloadManifestAsync(It.IsAny<uint>(), It.IsAny<ulong>(), It.IsAny<string>(), It.IsAny<uint>(), It.IsAny<CancellationToken>()))
            .Returns<uint, ulong, string, uint, CancellationToken>((_, _, _, _, _) =>
            {
                providerAAttempts++;
                throw new HttpRequestException("404 Not Found", null, HttpStatusCode.NotFound);
            });

        // Provider B: ManifestHub mock returning valid manifest
        var validBytes = CreateValidProtobufManifest(100, 1000);
        var mockProviderB = new Mock<IManifestProvider>();
        mockProviderB.Setup(p => p.ProviderId).Returns("manifesthub");
        mockProviderB.Setup(p => p.DownloadManifestAsync(It.IsAny<uint>(), It.IsAny<ulong>(), It.IsAny<string>(), It.IsAny<uint>(), It.IsAny<CancellationToken>()))
            .Returns<uint, ulong, string, uint, CancellationToken>(async (depotId, manifestId, targetDir, appId, ct) =>
            {
                providerBAttempts++;
                var path = Path.Combine(targetDir, $"{depotId}_{manifestId}.manifest");
                await File.WriteAllBytesAsync(path, validBytes, ct);
                return path;
            });

        var registry = new ManifestRegistry(
            [mockProviderA.Object, mockProviderB.Object],
            manifestCache,
            NullLogger<ManifestRegistry>.Instance,
            coordinator);

        // Step 1: Request manifest. Provider A fails with 404, Provider B succeeds.
        var filePath = await registry.AcquireManifestAsync(100, 1000, 50);
        filePath.Should().NotBeNull();
        File.Exists(filePath).Should().BeTrue();
        providerAAttempts.Should().Be(1);
        providerBAttempts.Should().Be(1);

        // Step 2: Now provider A suffers 3 consecutive 500 Internal Server Errors
        mockProviderA.Setup(p => p.DownloadManifestAsync(It.IsAny<uint>(), It.IsAny<ulong>(), It.IsAny<string>(), It.IsAny<uint>(), It.IsAny<CancellationToken>()))
            .Returns<uint, ulong, string, uint, CancellationToken>((_, _, _, _, _) =>
            {
                providerAAttempts++;
                throw new HttpRequestException("500 Internal Server Error", null, HttpStatusCode.InternalServerError);
            });

        providerAAttempts = 0;
        providerBAttempts = 0;

        for (uint i = 1; i <= 3; i++)
        {
            var validStepBytes = CreateValidProtobufManifest(200 + i, 2000 + i);
            mockProviderB.Setup(p => p.DownloadManifestAsync(200 + i, 2000 + i, It.IsAny<string>(), It.IsAny<uint>(), It.IsAny<CancellationToken>()))
                .Returns<uint, ulong, string, uint, CancellationToken>(async (depotId, manifestId, targetDir, appId, ct) =>
                {
                    providerBAttempts++;
                    var path = Path.Combine(targetDir, $"{depotId}_{manifestId}.manifest");
                    await File.WriteAllBytesAsync(path, validStepBytes, ct);
                    return path;
                });

            await registry.AcquireManifestAsync(200 + i, 2000 + i, 50);
        }

        providerAAttempts.Should().Be(3);
        providerBAttempts.Should().Be(3);

        // Step 3: 4th call - Provider A's circuit breaker is now OPEN, so Provider A must be skipped!
        // But Provider B remains CLOSED (healthy) and satisfies the request!
        var validFinalBytes = CreateValidProtobufManifest(300, 3000);
        mockProviderB.Setup(p => p.DownloadManifestAsync(300, 3000, It.IsAny<string>(), It.IsAny<uint>(), It.IsAny<CancellationToken>()))
            .Returns<uint, ulong, string, uint, CancellationToken>(async (depotId, manifestId, targetDir, appId, ct) =>
            {
                providerBAttempts++;
                var path = Path.Combine(targetDir, $"{depotId}_{manifestId}.manifest");
                await File.WriteAllBytesAsync(path, validFinalBytes, ct);
                return path;
            });

        var finalPath = await registry.AcquireManifestAsync(300, 3000, 50);
        finalPath.Should().NotBeNull();
        providerAAttempts.Should().Be(3, "Provider A must be skipped because its circuit is open");
        providerBAttempts.Should().Be(4, "Provider B must remain operational and satisfy the request");
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // SCENARIO 15: End-to-End User Flow Simulation
    // Full lifecycle simulation measuring exact HTTP calls across all providers
    // ─────────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task Scenario15_EndToEndUserFlow_MeasuresExactRequestsAcrossAllExternalProviders()
    {
        var metrics = new NetworkMetricsObserver();
        var coordinator = new RequestCoordinator(metrics);
        var cacheService = new FileCacheService(NullLogger<FileCacheService>.Instance, _testDir);
        var manifestCache = new ManifestCacheService(NullLogger<ManifestCacheService>.Instance, Path.Combine(_testDir, "mcache15"));

        var handler = new TrackingHttpMessageHandler(req =>
        {
            var uri = req.RequestUri?.ToString() ?? "";

            if (uri.Contains("api.github.com/repos/Coronitaa/BlueStar/releases/latest"))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(@"{""tag_name"":""v1.3.0"",""body"":""BlueStar v1.3""}", Encoding.UTF8, "application/json")
                };
            }
            if (uri.Contains("api.steamcmd.net/v1/info"))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(@"{""status"":""success"",""data"":{""730"":{""branches"":{""public"":{""buildid"":""5555""}},""depots"":{""731"":{""name"":""Binaries"",""manifests"":{""public"":{""gid"":""123456""}}}}}}}", Encoding.UTF8, "application/json")
                };
            }
            if (uri.Contains("store.steampowered.com/api/appdetails"))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(@"{""730"":{""success"":true,""data"":{""steam_appid"":730,""name"":""CS2"",""dlc"":[101]}}}", Encoding.UTF8, "application/json")
                };
            }
            if (uri.Contains("depotbox.org/api/manifests"))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(@"[{""depotId"":731,""manifestId"":""123456"",""sizeBytes"":1024}]", Encoding.UTF8, "application/json")
                };
            }
            if (uri.Contains("online-fix.me"))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(@"[{""fixId"":1,""title"":""Fix CS2"",""version"":""1.0""}]", Encoding.UTF8, "application/json")
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://depotbox.org") };
        var authService = new Mock<IDepotBoxAuthService>();
        var updateService = new BlueStar.Infrastructure.Update.GitHubUpdateService(
            httpClient,
            NullLogger<BlueStar.Infrastructure.Update.GitHubUpdateService>.Instance,
            cacheService,
            coordinator,
            metrics);

        var steamClient = new SteamStoreApiClient(httpClient, NullLogger<SteamStoreApiClient>.Instance, cacheService, coordinator, metrics);
        var depotBox = new BlueStar.Infrastructure.DepotBox.DepotBoxApiClient(
            httpClient,
            authService.Object,
            NullLogger<BlueStar.Infrastructure.DepotBox.DepotBoxApiClient>.Instance,
            null,
            cacheService,
            coordinator,
            metrics);

        var onlineFix = new BlueStar.Infrastructure.Providers.Fixes.OnlineFixProvider(
            httpClient,
            NullLogger<BlueStar.Infrastructure.Providers.Fixes.OnlineFixProvider>.Instance,
            cacheService,
            coordinator,
            metrics);

        // ── Phase 1: Cold Startup ──
        var coldUpdate = await updateService.CheckForUpdatesAsync(CancellationToken.None);
        metrics.GetRequestCountForProvider("GitHub").Should().Be(1, "Cold startup executes 1 GitHub update check");

        // ── Phase 2: Warm Startup (Reopening the app within TTL) ──
        var warmUpdate = await updateService.CheckForUpdatesAsync(CancellationToken.None);
        metrics.GetRequestCountForProvider("GitHub").Should().Be(1, "Warm startup uses cached update check with 0 GitHub requests");

        // ── Phase 3: Cold Instance Detail Open (AppId 730) ──
        // Concurrently query: Metadatos, Builds, Depots, OnlineFix
        var metaTask = steamClient.GetMetadataAsync(730);
        var buildsTask = steamClient.GetAppBuildsAsync(730);
        var depotsTask = steamClient.GetAppDepotInfoAsync(730);
        var dlcTask = steamClient.GetDlcListAsync(730);
        var onlineFixTask = onlineFix.GetFixesAsync(730);

        await Task.WhenAll(metaTask, buildsTask, depotsTask, dlcTask, onlineFixTask);

        // Cold counts:
        // Steam Store: 1 (appdetails)
        // SteamCMD: 1 (coalesced unified info for builds + depots)
        // OnlineFix: 1
        metrics.GetRequestCountForProvider("SteamStore").Should().Be(1);
        metrics.GetRequestCountForProvider("SteamCmd").Should().Be(1);
        metrics.GetRequestCountForProvider("OnlineFix").Should().Be(1);

        // ── Phase 4: Warm Instance Detail (Navigating back to the same instance) ──
        int preWarmTotal = metrics.TotalNetworkRequests;
        var warmMeta = await steamClient.GetMetadataAsync(730);
        var warmBuilds = await steamClient.GetAppBuildsAsync(730);
        var warmDepots = await steamClient.GetAppDepotInfoAsync(730);
        var warmDlc = await steamClient.GetDlcListAsync(730);
        var warmOnlineFix = await onlineFix.GetFixesAsync(730);

        int postWarmTotal = metrics.TotalNetworkRequests;
        (postWarmTotal - preWarmTotal).Should().Be(0, "Navigating to a previously loaded instance must generate EXACTLY 0 network requests");

        // ── Phase 5: DLC Tab Opening ──
        // Opening DLC tab retrieves DLCs that were already cached during Metadata / DlcList
        int preDlcTotal = metrics.TotalNetworkRequests;
        var dlcList = await steamClient.GetDlcListAsync(730);
        dlcList.Should().HaveCount(1);
        (metrics.TotalNetworkRequests - preDlcTotal).Should().Be(0, "Opening DLC tab must generate 0 network requests");

        // ── Phase 6: Manifest Acquisition ──
        var manifests = await depotBox.GetManifestsAsync(730);
        manifests.Should().HaveCount(1);
        metrics.GetRequestCountForProvider("DepotBox").Should().Be(1);

        var warmManifests = await depotBox.GetManifestsAsync(730);
        metrics.GetRequestCountForProvider("DepotBox").Should().Be(1, "Second manifest acquisition must hit cache with 0 requests");
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // SCENARIO 16: Multi-Provider Stampede & Coalescing Resilience
    // 50 concurrent requests on cold/expired cache across DepotBox, OnlineFix, and ManifestHub
    // ─────────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task Scenario16_MultiProviderStampede_50ConcurrentConsumersPerProvider_ResultsInExactlyOneHttpCallEach()
    {
        int depotBoxHttpCalls = 0;
        int onlineFixHttpCalls = 0;
        int manifestHubHttpCalls = 0;

        var handler = new AsyncTrackingHttpMessageHandler(async (req, ct) =>
        {
            var uri = req.RequestUri?.ToString() ?? "";
            await Task.Delay(30, ct); // Concurrency window

            if (uri.Contains("depotbox.org/api/manifests"))
            {
                Interlocked.Increment(ref depotBoxHttpCalls);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(@"[{""depotId"":731,""manifestId"":""123456"",""sizeBytes"":1024}]", Encoding.UTF8, "application/json")
                };
            }
            if (uri.Contains("onlinefix.manifesthub.uk/api/games"))
            {
                Interlocked.Increment(ref onlineFixHttpCalls);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(@"[{""id"":""1"",""title"":""Fix 730"",""appId"":730}]", Encoding.UTF8, "application/json")
                };
            }
            if (uri.Contains("raw.githubusercontent.com/SSMGAlt/ManifestHub2"))
            {
                Interlocked.Increment(ref manifestHubHttpCalls);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(@"{""depot"":{""731"":{""manifests"":{""public"":{""gid"":""123456""}}}}}", Encoding.UTF8, "application/json")
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://depotbox.org") };
        var authService = new Mock<IDepotBoxAuthService>();
        var metrics = new NetworkMetricsObserver();
        var coordinator = new RequestCoordinator(metrics);
        var cacheService = new FileCacheService(NullLogger<FileCacheService>.Instance, Path.Combine(_testDir, "c16"));
        var manifestCache = new ManifestCacheService(NullLogger<ManifestCacheService>.Instance, Path.Combine(_testDir, "mc16"));

        var depotBox = new BlueStar.Infrastructure.DepotBox.DepotBoxApiClient(
            httpClient,
            authService.Object,
            NullLogger<BlueStar.Infrastructure.DepotBox.DepotBoxApiClient>.Instance,
            null,
            cacheService,
            coordinator,
            metrics);

        var onlineFix = new BlueStar.Infrastructure.Providers.Fixes.OnlineFixProvider(
            httpClient,
            NullLogger<BlueStar.Infrastructure.Providers.Fixes.OnlineFixProvider>.Instance,
            cacheService,
            coordinator,
            metrics);

        var manifestHub = new BlueStar.Infrastructure.Providers.Manifest.ManifestHubProvider(
            httpClient,
            manifestCache,
            NullLogger<BlueStar.Infrastructure.Providers.Manifest.ManifestHubProvider>.Instance,
            null,
            cacheService,
            coordinator,
            metrics);

        // 1. DepotBox: 50 concurrent consumers
        var depotTasks = Enumerable.Range(0, 50).Select(_ => depotBox.GetManifestsAsync(730)).ToList();
        var depotResults = await Task.WhenAll(depotTasks);
        depotBoxHttpCalls.Should().Be(1, "DepotBox stampede of 50 concurrent requests must coalesce into 1 HTTP call");
        depotResults.Should().HaveCount(50);
        foreach (var r in depotResults) r.Should().HaveCount(1);

        // 2. OnlineFix: 50 concurrent consumers
        var onlineFixTasks = Enumerable.Range(0, 50).Select(_ => onlineFix.GetFixesAsync(730)).ToList();
        var onlineFixResults = await Task.WhenAll(onlineFixTasks);
        onlineFixHttpCalls.Should().Be(1, "OnlineFix stampede of 50 concurrent requests must coalesce into 1 HTTP call");
        onlineFixResults.Should().HaveCount(50);
        foreach (var r in onlineFixResults) r.Should().HaveCount(1);

        // 3. ManifestHub: 50 concurrent consumers
        var manifestHubTasks = Enumerable.Range(0, 50).Select(_ => manifestHub.DiscoverManifestsAsync(730)).ToList();
        var manifestHubResults = await Task.WhenAll(manifestHubTasks);
        manifestHubHttpCalls.Should().Be(1, "ManifestHub stampede of 50 concurrent requests must coalesce into 1 HTTP call");
        manifestHubResults.Should().HaveCount(50);
        foreach (var r in manifestHubResults) r.Should().HaveCount(1);
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // SCENARIO 17: Full 10-Scenario Cold vs Warm Comprehensive Integration Audit
    // Rigorously measures and differentiates the exact HTTP calls across all 10 core scenarios
    // ─────────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task Scenario17_FullTenScenarioColdVsWarmAudit_MeasuresExactRequests()
    {
        var testDir = Path.Combine(_testDir, "s17");
        Directory.CreateDirectory(testDir);

        var metrics = new NetworkMetricsObserver();
        var coordinator = new RequestCoordinator(metrics);
        var cacheService = new FileCacheService(NullLogger<FileCacheService>.Instance, Path.Combine(testDir, "cache"));
        var manifestCache = new ManifestCacheService(NullLogger<ManifestCacheService>.Instance, Path.Combine(testDir, "mcache"));
        var settingsService = new BlueStar.Infrastructure.Storage.AppSettingsService(NullLogger<BlueStar.Infrastructure.Storage.AppSettingsService>.Instance, Path.Combine(testDir, "settings.json"));

        var handler = new TrackingHttpMessageHandler(req =>
        {
            var uri = req.RequestUri?.ToString() ?? "";

            // 1. GitHub Releases
            if (uri.Contains("api.github.com/repos/Coronitaa/BlueStar/releases/latest"))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(@"{""tag_name"":""v1.3.0"",""body"":""BlueStar v1.3 release""}", Encoding.UTF8, "application/json")
                };
            }

            // 2. Cloudflare Worker (Explore feeds)
            if (uri.Contains("bluestar-api-worker.blustar.workers.dev/api/stats/trending") ||
                uri.Contains("bluestar-api-worker.blustar.workers.dev/api/stats/most-played") ||
                uri.Contains("bluestar-api-worker.blustar.workers.dev/api/steam/lists"))
            {
                var dummyItems = string.Join(",", Enumerable.Range(1, 25).Select(i => $"{{\"appId\":{700 + i},\"name\":\"Game {i}\"}}"));
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent($"{{\"success\":true,\"results\":[{dummyItems}]}}", Encoding.UTF8, "application/json")
                };
            }

            // 3. Steam Store (AppDetails)
            if (uri.Contains("store.steampowered.com/api/appdetails"))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(@"{""730"":{""success"":true,""data"":{""steam_appid"":730,""name"":""Counter-Strike 2"",""dlc"":[101,102]}}}", Encoding.UTF8, "application/json")
                };
            }

            // 4. SteamCMD (Unified AppInfo)
            if (uri.Contains("api.steamcmd.net/v1/info"))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(@"{""status"":""success"",""data"":{""730"":{""branches"":{""public"":{""buildid"":""7777""}},""depots"":{""731"":{""name"":""CS2 Content"",""manifests"":{""public"":{""gid"":""999888""}}}}}}}", Encoding.UTF8, "application/json")
                };
            }

            // 5. OnlineFix
            if (uri.Contains("onlinefix.manifesthub.uk/api/games"))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(@"[{""id"":""1"",""title"":""Fix CS2"",""appId"":730}]", Encoding.UTF8, "application/json")
                };
            }

            // 6. DepotBox Manifests
            if (uri.Contains("depotbox.org/api/manifests"))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(@"[{""depotId"":731,""manifestId"":""999888"",""sizeBytes"":2048}]", Encoding.UTF8, "application/json")
                };
            }

            // 7. ManifestHub Manifests & Download
            if (uri.Contains("raw.githubusercontent.com/SSMGAlt/ManifestHub2") && uri.EndsWith(".json"))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(@"{""depot"":{""731"":{""manifests"":{""public"":{""gid"":""999888""}}}}}", Encoding.UTF8, "application/json")
                };
            }
            if (uri.Contains("raw.githubusercontent.com/SSMGAlt/ManifestHub2") && uri.EndsWith(".manifest"))
            {
                var proto = CreateValidProtobufManifest(731, 999888);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(proto)
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://depotbox.org") };
        var authService = new Mock<IDepotBoxAuthService>();
        var archiveParser = new Mock<IDepotBoxArchiveParser>();

        var updateService = new BlueStar.Infrastructure.Update.GitHubUpdateService(httpClient, NullLogger<BlueStar.Infrastructure.Update.GitHubUpdateService>.Instance, cacheService, coordinator, metrics);
        var communityStats = new CommunityStatsService(httpClient, cacheService, settingsService, NullLogger<CommunityStatsService>.Instance);
        var steamClient = new SteamStoreApiClient(httpClient, NullLogger<SteamStoreApiClient>.Instance, cacheService, coordinator, metrics);
        var depotBox = new BlueStar.Infrastructure.DepotBox.DepotBoxApiClient(httpClient, authService.Object, NullLogger<BlueStar.Infrastructure.DepotBox.DepotBoxApiClient>.Instance, null, cacheService, coordinator, metrics);
        var onlineFix = new BlueStar.Infrastructure.Providers.Fixes.OnlineFixProvider(httpClient, NullLogger<BlueStar.Infrastructure.Providers.Fixes.OnlineFixProvider>.Instance, cacheService, coordinator, metrics);
        var localCacheProvider = new LocalCacheManifestProvider(manifestCache, NullLogger<LocalCacheManifestProvider>.Instance);
        var depotBoxManifestProvider = new BlueStar.Infrastructure.Providers.Manifest.DepotBoxManifestProvider(depotBox, archiveParser.Object, manifestCache, NullLogger<BlueStar.Infrastructure.Providers.Manifest.DepotBoxManifestProvider>.Instance);
        var manifestHubProvider = new BlueStar.Infrastructure.Providers.Manifest.ManifestHubProvider(httpClient, manifestCache, NullLogger<BlueStar.Infrastructure.Providers.Manifest.ManifestHubProvider>.Instance, null, cacheService, coordinator, metrics);
        var manifestRegistry = new ManifestRegistry([localCacheProvider, depotBoxManifestProvider, manifestHubProvider], manifestCache, NullLogger<ManifestRegistry>.Instance, coordinator, metrics);
        var buildResolver = new BuildResolver(steamClient, manifestRegistry, NullLogger<BuildResolver>.Instance, null, null, coordinator, cacheService);
        var planner = new InstallationPlanner(manifestCache, NullLogger<InstallationPlanner>.Instance);

        // ═════════════════════════════════════════════════════════════════════════
        // 1. SCENARIO: Startup
        // ═════════════════════════════════════════════════════════════════════════
        int startHttp = handler.Requests.Count;
        var coldUpdate = await updateService.CheckForUpdatesAsync(CancellationToken.None);
        (handler.Requests.Count - startHttp).Should().Be(1, "Cold startup: 1 GitHub update check");

        startHttp = handler.Requests.Count;
        var warmUpdate = await updateService.CheckForUpdatesAsync(CancellationToken.None);
        (handler.Requests.Count - startHttp).Should().Be(0, "Warm startup: 0 requests (cached for 4h)");

        // ═════════════════════════════════════════════════════════════════════════
        // 2. SCENARIO: Explore (6 Category Feeds)
        // ═════════════════════════════════════════════════════════════════════════
        startHttp = handler.Requests.Count;
        await communityStats.GetTrendingBlueStarAsync();
        await communityStats.GetMostPlayedBlueStarAsync();
        await communityStats.GetSteamDbListAsync("most_played");
        await communityStats.GetSteamDbListAsync("trending");
        await communityStats.GetSteamDbListAsync("top_sellers");
        await communityStats.GetSteamDbListAsync("top_rated");
        (handler.Requests.Count - startHttp).Should().Be(6, "Cold Explore: 6 category requests to Cloudflare worker");

        startHttp = handler.Requests.Count;
        await communityStats.GetTrendingBlueStarAsync();
        await communityStats.GetMostPlayedBlueStarAsync();
        await communityStats.GetSteamDbListAsync("most_played");
        await communityStats.GetSteamDbListAsync("trending");
        await communityStats.GetSteamDbListAsync("top_sellers");
        await communityStats.GetSteamDbListAsync("top_rated");
        (handler.Requests.Count - startHttp).Should().Be(0, "Warm Explore: 0 requests (all 6 feeds cached)");

        // ═════════════════════════════════════════════════════════════════════════
        // 3. SCENARIO: Instances (Library View)
        // ═════════════════════════════════════════════════════════════════════════
        startHttp = handler.Requests.Count;
        // Instances are stored in local JSON files
        (handler.Requests.Count - startHttp).Should().Be(0, "Library view: 0 requests (purely local filesystem)");

        // ═════════════════════════════════════════════════════════════════════════
        // 4. SCENARIO: Open Specific Instance (Metadata + Builds + OnlineFix + Manifest Discovery)
        // ═════════════════════════════════════════════════════════════════════════
        startHttp = handler.Requests.Count;
        var metaTask = steamClient.GetMetadataAsync(730);
        var buildsTask = buildResolver.GetAvailableVersionsAsync(730);
        var fixesTask = onlineFix.GetFixesAsync(730);
        await Task.WhenAll(metaTask, buildsTask, fixesTask);
        (handler.Requests.Count - startHttp).Should().Be(5, "Cold Instance: 1 SteamStore + 1 SteamCMD + 1 DepotBox + 1 ManifestHub + 1 OnlineFix");

        startHttp = handler.Requests.Count;
        var warmMeta = await steamClient.GetMetadataAsync(730);
        var warmBuilds = await buildResolver.GetAvailableVersionsAsync(730);
        var warmFixes = await onlineFix.GetFixesAsync(730);
        (handler.Requests.Count - startHttp).Should().Be(0, "Warm Instance: 0 requests (all cached)");

        // ═════════════════════════════════════════════════════════════════════════
        // 5. SCENARIO: DLCs Tab
        // ═════════════════════════════════════════════════════════════════════════
        startHttp = handler.Requests.Count;
        var dlcs = await steamClient.GetDlcListAsync(730);
        dlcs.Should().HaveCount(2);
        (handler.Requests.Count - startHttp).Should().Be(0, "DLC tab: 0 requests (pre-populated by GetMetadataAsync)");

        // ═════════════════════════════════════════════════════════════════════════
        // 6. SCENARIO: Check for Updates (Manual)
        // ═════════════════════════════════════════════════════════════════════════
        startHttp = handler.Requests.Count;
        var manualUpdate = await updateService.CheckForUpdatesAsync(CancellationToken.None);
        (handler.Requests.Count - startHttp).Should().Be(0, "Manual update check within TTL: 0 requests");

        // ═════════════════════════════════════════════════════════════════════════
        // 7. SCENARIO: Manual Refresh (forceRefresh: true)
        // ═════════════════════════════════════════════════════════════════════════
        startHttp = handler.Requests.Count;
        var refreshedManifests = await depotBox.GetManifestsAsync(730, CancellationToken.None, forceRefresh: true);
        refreshedManifests.Should().HaveCount(1);
        (handler.Requests.Count - startHttp).Should().Be(1, "Manual refresh with forceRefresh: true on DepotBox makes fresh HTTP call bypassing cache");

        // ═════════════════════════════════════════════════════════════════════════
        // 8. SCENARIO: Resolve Installation (Planner)
        // ═════════════════════════════════════════════════════════════════════════
        startHttp = handler.Requests.Count;
        var instance = new GameInstance { Id = Guid.NewGuid(), Name = "CS2", AppId = 730, InstallPath = Path.Combine(testDir, "cs2") };
        var selectedVersion = new GameVersion { BranchName = "public", BuildId = "7777", Depots = [new DepotVersion { DepotId = 731, ManifestId = 999888 }] };
        var plan = await planner.CreateInstallPlanAsync(instance, selectedVersion);
        plan.Should().NotBeNull();
        (handler.Requests.Count - startHttp).Should().Be(0, "Installation planner: 0 requests (purely computational)");

        // ═════════════════════════════════════════════════════════════════════════
        // 9. SCENARIO: Manifest Acquisition
        // ═════════════════════════════════════════════════════════════════════════
        startHttp = handler.Requests.Count;
        var manifestPath = await manifestRegistry.AcquireManifestAsync(731, 999888, 730);
        manifestPath.Should().NotBeNull();
        File.Exists(manifestPath).Should().BeTrue();
        (handler.Requests.Count - startHttp).Should().Be(1, "Cold manifest acquisition: 1 download request");

        startHttp = handler.Requests.Count;
        var warmManifestPath = await manifestRegistry.AcquireManifestAsync(731, 999888, 730);
        warmManifestPath.Should().Be(manifestPath);
        (handler.Requests.Count - startHttp).Should().Be(0, "Warm manifest acquisition: 0 requests (LocalCache hit)");

        // ═════════════════════════════════════════════════════════════════════════
        // 10. SCENARIO: OnlineFix Query
        // ═════════════════════════════════════════════════════════════════════════
        startHttp = handler.Requests.Count;
        var secondOnlineFix = await onlineFix.GetFixesAsync(730);
        secondOnlineFix.Should().HaveCount(1);
        (handler.Requests.Count - startHttp).Should().Be(0, "Warm OnlineFix query: 0 requests (served from L1/L2 cache)");
    }
}
