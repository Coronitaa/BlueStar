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
using BlueStar.Infrastructure.Providers.Manifest;
using BlueStar.Infrastructure.Services;
using BlueStar.Infrastructure.Storage;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Moq.Protected;
using Xunit;

namespace BlueStar.Infrastructure.Tests;

public sealed class ManifestHubClientTests : IDisposable
{
    private readonly string _testDir;

    public ManifestHubClientTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "BlueStarHubTests_" + Guid.NewGuid().ToString("N"));
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
        using var ms = new MemoryStream();
        ms.Write([0xD0, 0x17, 0xF6, 0x71]); // magic 0x71F617D0
        ms.Write(BitConverter.GetBytes(100u)); // payload length

        using (var deflate = new DeflateStream(ms, CompressionLevel.Fastest, leaveOpen: true))
        {
            var dummyPayload = Encoding.UTF8.GetBytes("steam_protobuf_manifest_sample_payload_data_for_unit_tests");
            deflate.Write(dummyPayload);
        }

        return ms.ToArray();
    }

    [Fact]
    public async Task ManifestHubProvider_PrioritizesCoronitaaRoutes_ForDiscoveryAndDownload()
    {
        var cacheService = new ManifestCacheService(NullLogger<ManifestCacheService>.Instance, Path.Combine(_testDir, "cache"));
        var requestedUrls = new List<string>();

        var primaryJson = """
        [
          {
            "schemaVersion": "v1",
            "appId": 730,
            "buildId": 999111,
            "timestamp": "2026-10-15T12:00:00Z",
            "branch": "public",
            "description": "Primary Branch",
            "depotManifests": {
              "731": 10001,
              "732": 10002
            }
          }
        ]
        """;

        var manifestBytes = CreateValidSteamProtobufManifest();

        var handlerMock = new Mock<HttpMessageHandler>();
        handlerMock.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync((HttpRequestMessage req, CancellationToken _) =>
            {
                var uri = req.RequestUri?.ToString() ?? string.Empty;
                requestedUrls.Add(uri);

                if (uri == $"{ManifestHubProvider.PrimaryBuildsBaseUrl}/730.json")
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(primaryJson, Encoding.UTF8, "application/json")
                    };
                }

                if (uri == $"{ManifestHubProvider.PrimaryManifestsBaseUrl}/731/10001.manifest")
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new ByteArrayContent(manifestBytes)
                    };
                }

                return new HttpResponseMessage(HttpStatusCode.NotFound);
            });

        var httpClient = new HttpClient(handlerMock.Object);
        var provider = new ManifestHubProvider(
            httpClient,
            cacheService,
            NullLogger<ManifestHubProvider>.Instance);

        // 1. Discovery must query primary Coronitaa endpoint first
        var artifacts = await provider.DiscoverManifestsAsync(730);

        artifacts.Should().NotBeNull();
        artifacts.Should().HaveCount(2);

        var a1 = artifacts.FirstOrDefault(a => a.DepotId == 731 && a.ManifestId == 10001);
        var a2 = artifacts.FirstOrDefault(a => a.DepotId == 732 && a.ManifestId == 10002);
        a1.Should().NotBeNull();
        a2.Should().NotBeNull();

        a1!.Routes.Should().NotBeEmpty();
        a1.Routes[0].Url.Should().Be($"{ManifestHubProvider.PrimaryManifestsBaseUrl}/731/10001.manifest");

        requestedUrls.Should().Contain($"{ManifestHubProvider.PrimaryBuildsBaseUrl}/730.json");
        requestedUrls.Should().NotContain(u => u.Contains("SSMGAlt/ManifestHub2"));

        // 2. Download must query primary Coronitaa route first
        var downloadDir = Path.Combine(_testDir, "downloads");
        var downloadedPath = await provider.DownloadManifestAsync(731, 10001, downloadDir, appId: 730);

        downloadedPath.Should().NotBeNull();
        File.Exists(downloadedPath).Should().BeTrue();
        cacheService.HasManifest(731, 10001).Should().BeTrue();

        requestedUrls.Should().Contain($"{ManifestHubProvider.PrimaryManifestsBaseUrl}/731/10001.manifest");
    }

    [Fact]
    public void ManifestHubProvider_DeserializesBuildSnapshots_List_Single_And_HubV1Formats()
    {
        var cacheService = new ManifestCacheService(NullLogger<ManifestCacheService>.Instance, Path.Combine(_testDir, "cache_parse"));
        var provider = new ManifestHubProvider(
            new HttpClient(),
            cacheService,
            NullLogger<ManifestHubProvider>.Instance);

        // Format 1: List<BuildSnapshot>
        var listJson = """
        [
          {
            "schemaVersion": "v1",
            "appId": 730,
            "buildId": 12345,
            "timestamp": "2026-10-15T00:00:00Z",
            "branch": "public",
            "description": "Build 12345",
            "depotManifests": {
              "731": 55555,
              "732": 66666
            }
          }
        ]
        """;

        var listArtifacts = provider.ParseHubBuildSnapshots(listJson, 730);
        listArtifacts.Should().HaveCount(2);
        listArtifacts.Should().Contain(a => a.DepotId == 731 && a.ManifestId == 55555);
        listArtifacts.Should().Contain(a => a.DepotId == 732 && a.ManifestId == 66666);

        // Format 2: Single BuildSnapshot
        var singleJson = """
        {
          "schemaVersion": "v1",
          "appId": 730,
          "buildId": 54321,
          "timestamp": "2026-10-15T01:00:00Z",
          "branch": "beta",
          "description": "Beta Snapshot",
          "depotManifests": {
            "733": 77777
          }
        }
        """;

        var singleArtifacts = provider.ParseHubBuildSnapshots(singleJson, 730);
        singleArtifacts.Should().HaveCount(1);
        singleArtifacts[0].DepotId.Should().Be(733);
        singleArtifacts[0].ManifestId.Should().Be(77777);

        // Format 3: Hub v1 with depotManifests
        var hubV1Json = """
        {
          "depotManifests": {
            "734": 88888,
            "735": 99999
          }
        }
        """;

        var hubV1Artifacts = provider.ParseHubBuildSnapshots(hubV1Json, 730);
        hubV1Artifacts.Should().HaveCount(2);
        hubV1Artifacts.Should().Contain(a => a.DepotId == 734 && a.ManifestId == 88888);
        hubV1Artifacts.Should().Contain(a => a.DepotId == 735 && a.ManifestId == 99999);
    }

    [Fact]
    public async Task ManifestHubProvider_FallsBackToManifestHub2_WhenPrimaryHubReturns404()
    {
        var cacheService = new ManifestCacheService(NullLogger<ManifestCacheService>.Instance, Path.Combine(_testDir, "cache_fallback"));
        var requestedUrls = new List<string>();

        var secondaryJson = """
        {
          "depot": {
            "501": {
              "decryptionkey": "abcdefabcdefabcdefabcdefabcdefabcdefabcdefabcdefabcdefabcdefabcd",
              "manifests": {
                "public": {
                  "gid": "123456789012345",
                  "size": 987654
                }
              }
            }
          }
        }
        """;

        var manifestBytes = CreateValidSteamProtobufManifest();

        var handlerMock = new Mock<HttpMessageHandler>();
        handlerMock.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync((HttpRequestMessage req, CancellationToken _) =>
            {
                var uri = req.RequestUri?.ToString() ?? string.Empty;
                requestedUrls.Add(uri);

                // Primary discovery returns 404
                if (uri == $"{ManifestHubProvider.PrimaryBuildsBaseUrl}/500.json")
                {
                    return new HttpResponseMessage(HttpStatusCode.NotFound);
                }

                // Secondary discovery returns 200 OK
                if (uri == $"{ManifestHubProvider.SecondaryManifestHub2BaseUrl}/500/500.json")
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(secondaryJson, Encoding.UTF8, "application/json")
                    };
                }

                // Primary manifest download returns 404
                if (uri == $"{ManifestHubProvider.PrimaryManifestsBaseUrl}/501/123456789012345.manifest")
                {
                    return new HttpResponseMessage(HttpStatusCode.NotFound);
                }

                // Secondary manifest download returns 200 OK
                if (uri == $"{ManifestHubProvider.SecondaryManifestHub2BaseUrl}/500/501_123456789012345.manifest")
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new ByteArrayContent(manifestBytes)
                    };
                }

                return new HttpResponseMessage(HttpStatusCode.NotFound);
            });

        var keyRepo = new DepotKeyRepository(NullLogger<DepotKeyRepository>.Instance, Path.Combine(_testDir, "keys.json"));
        var httpClient = new HttpClient(handlerMock.Object);
        var provider = new ManifestHubProvider(
            httpClient,
            cacheService,
            NullLogger<ManifestHubProvider>.Instance,
            keyRepo);

        // Act: Discover manifests
        var artifacts = await provider.DiscoverManifestsAsync(500);

        // Assert: Discovery fell back cleanly to SSMGAlt/ManifestHub2
        artifacts.Should().NotBeNull();
        artifacts.Should().HaveCount(1);
        artifacts[0].DepotId.Should().Be(501);
        artifacts[0].ManifestId.Should().Be(123456789012345UL);
        artifacts[0].SizeBytes.Should().Be(987654);

        // Key was registered from SSMGAlt JSON
        var key = await keyRepo.GetKeyAsync(501);
        key.Should().Be("abcdefabcdefabcdefabcdefabcdefabcdefabcdefabcdefabcdefabcdefabcd");

        // Act: Download manifest (Route 1 404 -> Route 2 success)
        var downloadDir = Path.Combine(_testDir, "fallback_dl");
        var downloaded = await provider.DownloadManifestAsync(501, 123456789012345UL, downloadDir, appId: 500);

        downloaded.Should().NotBeNull();
        File.Exists(downloaded).Should().BeTrue();
        cacheService.HasManifest(501, 123456789012345UL).Should().BeTrue();

        requestedUrls.Should().Contain($"{ManifestHubProvider.PrimaryBuildsBaseUrl}/500.json");
        requestedUrls.Should().Contain($"{ManifestHubProvider.SecondaryManifestHub2BaseUrl}/500/500.json");
        requestedUrls.Should().Contain($"{ManifestHubProvider.PrimaryManifestsBaseUrl}/501/123456789012345.manifest");
        requestedUrls.Should().Contain($"{ManifestHubProvider.SecondaryManifestHub2BaseUrl}/500/501_123456789012345.manifest");
    }

    [Fact]
    public async Task ManifestHubProvider_IsAvailableAsync_PrioritizesPrimaryAndFallsBack()
    {
        var cacheService = new ManifestCacheService(NullLogger<ManifestCacheService>.Instance, Path.Combine(_testDir, "cache_avail"));
        var checkedUrls = new List<string>();

        var handlerMock = new Mock<HttpMessageHandler>();
        handlerMock.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync((HttpRequestMessage req, CancellationToken _) =>
            {
                var uri = req.RequestUri?.ToString() ?? string.Empty;
                checkedUrls.Add(uri);

                if (uri == $"{ManifestHubProvider.PrimaryBuildsBaseUrl}/100.json")
                    return new HttpResponseMessage(HttpStatusCode.OK);

                if (uri == $"{ManifestHubProvider.PrimaryBuildsBaseUrl}/200.json")
                    return new HttpResponseMessage(HttpStatusCode.NotFound);

                if (uri == $"{ManifestHubProvider.SecondaryManifestHub2BaseUrl}/200/200.json")
                    return new HttpResponseMessage(HttpStatusCode.OK);

                return new HttpResponseMessage(HttpStatusCode.NotFound);
            });

        var httpClient = new HttpClient(handlerMock.Object);
        var provider = new ManifestHubProvider(httpClient, cacheService, NullLogger<ManifestHubProvider>.Instance);

        // App 100 is in primary Hub
        var avail100 = await provider.IsAvailableAsync(100);
        avail100.Should().BeTrue();
        checkedUrls.Should().Contain($"{ManifestHubProvider.PrimaryBuildsBaseUrl}/100.json");
        checkedUrls.Should().NotContain($"{ManifestHubProvider.SecondaryManifestHub2BaseUrl}/100/100.json");

        // App 200 is not in primary Hub, but in secondary Hub
        var avail200 = await provider.IsAvailableAsync(200);
        avail200.Should().BeTrue();
        checkedUrls.Should().Contain($"{ManifestHubProvider.PrimaryBuildsBaseUrl}/200.json");
        checkedUrls.Should().Contain($"{ManifestHubProvider.SecondaryManifestHub2BaseUrl}/200/200.json");

        // App 300 is in neither
        var avail300 = await provider.IsAvailableAsync(300);
        avail300.Should().BeFalse();
    }

    [Fact]
    public async Task ManifestHubProvider_HandlesAllRoutesFailing_GracefullyWithoutCrashing()
    {
        var cacheService = new ManifestCacheService(NullLogger<ManifestCacheService>.Instance, Path.Combine(_testDir, "cache_fail"));

        var handlerMock = new Mock<HttpMessageHandler>();
        handlerMock.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.NotFound));

        var httpClient = new HttpClient(handlerMock.Object);
        var provider = new ManifestHubProvider(httpClient, cacheService, NullLogger<ManifestHubProvider>.Instance);

        // Discovery returning empty list instead of throwing
        var artifacts = await provider.DiscoverManifestsAsync(999999);
        artifacts.Should().BeEmpty();

        // Download failing with HttpRequestException after exhausting all routes
        var downloadDir = Path.Combine(_testDir, "downloads_fail");
        var act = async () => await provider.DownloadManifestAsync(111, 222, downloadDir, appId: 999999);
        await act.Should().ThrowAsync<HttpRequestException>();
    }

    [Fact]
    public async Task ManifestHubProvider_DownloadManifest_FallsBackToK25CanonicalRoute()
    {
        var cacheDir = Path.Combine(_testDir, "cache_k25");
        var cacheService = new ManifestCacheService(NullLogger<ManifestCacheService>.Instance, cacheDir);
        var requestedUrls = new List<string>();

        var validSteamManifestBytes = CreateValidSteamProtobufManifest();

        var handlerMock = new Mock<HttpMessageHandler>();
        handlerMock.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync((HttpRequestMessage req, CancellationToken _) =>
            {
                var uri = req.RequestUri?.ToString() ?? string.Empty;
                requestedUrls.Add(uri);

                // Coronitaa 404
                if (uri.Contains("Coronitaa/BlueStar-ManifestHub"))
                    return new HttpResponseMessage(HttpStatusCode.NotFound);

                // SSMGAlt 404
                if (uri.Contains("SSMGAlt/ManifestHub2"))
                    return new HttpResponseMessage(HttpStatusCode.NotFound);

                // K25 canonical route succeeds!
                if (uri == $"{ManifestHubProvider.TertiaryK25BaseUrl}/manifests/601/999888777.manifest")
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new ByteArrayContent(validSteamManifestBytes)
                    };
                }

                return new HttpResponseMessage(HttpStatusCode.NotFound);
            });

        var httpClient = new HttpClient(handlerMock.Object);
        var provider = new ManifestHubProvider(httpClient, cacheService, NullLogger<ManifestHubProvider>.Instance);

        var targetDir = Path.Combine(_testDir, "k25_dl");
        var downloadedPath = await provider.DownloadManifestAsync(601, 999888777UL, targetDir, appId: 600);

        downloadedPath.Should().NotBeNull();
        File.Exists(downloadedPath).Should().BeTrue();
        requestedUrls.Should().Contain($"{ManifestHubProvider.TertiaryK25BaseUrl}/manifests/601/999888777.manifest");
    }

    [Fact]
    public async Task ManifestHubProvider_DownloadManifest_WhenTargetDirectoryIsCacheDir_DoesNotThrowFileAccessException()
    {
        var cacheDir = Path.Combine(_testDir, "cache_same_path");
        var cacheService = new ManifestCacheService(NullLogger<ManifestCacheService>.Instance, cacheDir);

        var validSteamManifestBytes = CreateValidSteamProtobufManifest();

        var handlerMock = new Mock<HttpMessageHandler>();
        handlerMock.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync((HttpRequestMessage req, CancellationToken _) =>
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(validSteamManifestBytes)
                };
            });

        var httpClient = new HttpClient(handlerMock.Object);
        var provider = new ManifestHubProvider(httpClient, cacheService, NullLogger<ManifestHubProvider>.Instance);

        // When targetDirectory is the depot cache directory itself:
        var targetDepotDir = Path.Combine(cacheDir, "701");
        var downloadedPath = await provider.DownloadManifestAsync(701, 1122334455UL, targetDepotDir, appId: 700);

        downloadedPath.Should().NotBeNull();
        File.Exists(downloadedPath).Should().BeTrue();
    }

    [Fact]
    public async Task ManifestHubProvider_GetRevisionsForAppAsync_PreservesAuthenticDates_AndNeverDefaultsToUtcNow()
    {
        var cacheDir = Path.Combine(_testDir, "cache_revisions");
        var cacheService = new ManifestCacheService(NullLogger<ManifestCacheService>.Instance, cacheDir);

        var json = """
        [
          {
            "depotId": 555001,
            "manifestId": 987654321098765432,
            "depotName": "Game Content",
            "date": "2023-11-15T18:30:00Z",
            "size": 52428800,
            "source": "ManifestHub"
          },
          {
            "depotId": 555002,
            "manifestId": 123456789012345678,
            "depotName": "Game Binaries",
            "size": 1048576,
            "source": "ManifestHub"
          }
        ]
        """;

        var handlerMock = new Mock<HttpMessageHandler>();
        handlerMock.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync((HttpRequestMessage req, CancellationToken _) =>
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json")
                };
            });

        var httpClient = new HttpClient(handlerMock.Object);
        var provider = new ManifestHubProvider(httpClient, cacheService, NullLogger<ManifestHubProvider>.Instance);

        var revisions = await provider.GetRevisionsForAppAsync(555000);

        revisions.Should().HaveCount(2);

        // Revision 1: authentic date is verified and preserved
        var rev1 = revisions.First(r => r.DepotId == 555001);
        rev1.HasAuthenticDate.Should().BeTrue();
        rev1.AuthenticPublicationDate.Should().Be(DateTimeOffset.Parse("2023-11-15T18:30:00Z"));
        rev1.Provenance.Should().Be("ManifestHub");

        // Revision 2: date was absent; MUST NOT be fabricated or defaulted to UtcNow
        var rev2 = revisions.First(r => r.DepotId == 555002);
        rev2.HasAuthenticDate.Should().BeFalse();
        rev2.AuthenticPublicationDate.Should().BeNull();
        rev2.Provenance.Should().Be("ManifestHub");
    }

    [Fact]
    public async Task ContributionTransportService_GetStardustBalanceAsync_QueriesServerLedger_DistinguishesConfirmedAndPending()
    {
        var appSettings = new AppSettingsService(NullLogger<AppSettingsService>.Instance, Path.Combine(_testDir, "settings.json"));
        await appSettings.SetHubServerUrlAsync("http://127.0.0.1:5088");

        HttpRequestMessage? capturedRequest = null;
        var ledgerJson = """
        {
          "confirmedPoints": 250,
          "pendingPoints": 75,
          "totalPoints": 325
        }
        """;

        var handlerMock = new Mock<HttpMessageHandler>();
        handlerMock.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync((HttpRequestMessage req, CancellationToken _) =>
            {
                capturedRequest = req;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(ledgerJson, Encoding.UTF8, "application/json")
                };
            });

        var httpClient = new HttpClient(handlerMock.Object);
        var transport = new ContributionTransportService(httpClient, null, NullLogger<ContributionTransportService>.Instance, null, appSettings);

        var contributorId = Guid.NewGuid().ToString();
        var balance = await transport.GetStardustBalanceAsync(contributorId);

        capturedRequest.Should().NotBeNull();
        capturedRequest!.RequestUri!.ToString().Should().Be($"http://127.0.0.1:5088/api/v1/stardust/balance/{contributorId}");
        balance.ConfirmedPoints.Should().Be(250);
        balance.PendingPoints.Should().Be(75);
        balance.TotalPoints.Should().Be(325);
    }

    [Fact]
    public async Task ContributionTransportService_GetStardustBalanceAsync_WhenEndpointOffline_FallsBackToVerifiedCreditedTotal()
    {
        var appSettings = new AppSettingsService(NullLogger<AppSettingsService>.Instance, Path.Combine(_testDir, "settings_offline.json"));
        await appSettings.SetHubServerUrlAsync("http://127.0.0.1:5088");

        var handlerMock = new Mock<HttpMessageHandler>();
        handlerMock.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync((HttpRequestMessage _, CancellationToken _) =>
            {
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            });

        var httpClient = new HttpClient(handlerMock.Object);
        var transport = new ContributionTransportService(httpClient, null, NullLogger<ContributionTransportService>.Instance, null, appSettings);

        var balance = await transport.GetStardustBalanceAsync("anon-user-uuid");

        balance.ConfirmedPoints.Should().Be(transport.GetTotalStardustCredited());
        balance.PendingPoints.Should().Be(0);
    }
}
