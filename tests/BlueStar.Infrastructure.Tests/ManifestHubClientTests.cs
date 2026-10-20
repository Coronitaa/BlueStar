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

    [Fact]
    public async Task ManifestHubProvider_GetCoverageAsync_DiscoversAndAggregatesManifestsAcrossAppIds()
    {
        var cacheService = new ManifestCacheService(NullLogger<ManifestCacheService>.Instance, Path.Combine(_testDir, "cache_coverage"));

        var json730 = """
        [
          {
            "schemaVersion": "v1",
            "appId": 730,
            "buildId": 999111,
            "depotManifests": {
              "731": 10001
            }
          }
        ]
        """;

        var json440 = """
        [
          {
            "schemaVersion": "v1",
            "appId": 440,
            "buildId": 888222,
            "depotManifests": {
              "441": 20002
            }
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
                var uri = req.RequestUri?.ToString() ?? string.Empty;
                if (uri == $"{ManifestHubProvider.PrimaryBuildsBaseUrl}/730.json")
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(json730, Encoding.UTF8, "application/json")
                    };
                }
                if (uri == $"{ManifestHubProvider.PrimaryBuildsBaseUrl}/440.json")
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(json440, Encoding.UTF8, "application/json")
                    };
                }
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            });

        var httpClient = new HttpClient(handlerMock.Object);
        var provider = new ManifestHubProvider(
            httpClient,
            cacheService,
            NullLogger<ManifestHubProvider>.Instance);

        var coverage = await provider.GetCoverageAsync([730, 440]);

        coverage.Should().HaveCount(2);
        coverage.Should().Contain(m => m.DepotId == 731 && m.ManifestId == 10001);
        coverage.Should().Contain(m => m.DepotId == 441 && m.ManifestId == 20002);
    }

    [Fact]
    public async Task ManifestHubProvider_GetAppSnapshotsAsync_ParsesSnapshotsWithDepotsAndDates()
    {
        var cacheService = new ManifestCacheService(NullLogger<ManifestCacheService>.Instance, Path.Combine(_testDir, "cache_snapshots"));

        var snapshotsJson = """
        [
          {
            "schemaVersion": "v1",
            "appId": 730,
            "buildId": 123456,
            "timestamp": "2026-10-15T12:00:00Z",
            "creationTime": "2026-10-14T08:30:00Z",
            "branch": "public",
            "description": "Counter-Strike Release",
            "depotManifests": {
              "731": 555001,
              "732": 555002
            }
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
                var uri = req.RequestUri?.ToString() ?? string.Empty;
                if (uri == $"{ManifestHubProvider.PrimaryBuildsBaseUrl}/730.json")
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(snapshotsJson, Encoding.UTF8, "application/json")
                    };
                }
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            });

        var httpClient = new HttpClient(handlerMock.Object);
        var provider = new ManifestHubProvider(
            httpClient,
            cacheService,
            NullLogger<ManifestHubProvider>.Instance);

        var snapshots = await provider.GetAppSnapshotsAsync(730);

        snapshots.Should().HaveCount(1);
        var snap = snapshots[0];
        snap.AppId.Should().Be(730);
        snap.BuildId.Should().Be(123456);
        snap.CreationTime.Should().Be(DateTimeOffset.Parse("2026-10-14T08:30:00Z"));
        snap.DepotManifests.Should().ContainKey(731);
        snap.DepotManifests[731].Should().Be(555001);
        snap.DepotManifests.Should().ContainKey(732);
        snap.DepotManifests[732].Should().Be(555002);
    }

    [Fact]
    public async Task ManifestHubProvider_GetCoverageAsync_QueriesCoverageQueryEndpoint_WhenHubConfigured()
    {
        var appSettings = new AppSettingsService(NullLogger<AppSettingsService>.Instance, Path.Combine(_testDir, "settings_cov.json"));
        await appSettings.SetHubServerUrlAsync("http://127.0.0.1:5088");

        var cacheService = new ManifestCacheService(NullLogger<ManifestCacheService>.Instance, Path.Combine(_testDir, "cache_cov_query"));

        var coverageResponseJson = """
        {
          "isSuccess": true,
          "existingManifests": [
            { "depotId": 731, "manifestId": 500001 },
            { "depotId": 732, "manifestId": 500002 }
          ],
          "depotManifests": {
            "731": [ 500001 ],
            "732": [ 500002 ]
          },
          "totalFound": 2
        }
        """;

        var requestedUrls = new List<string>();
        string? requestBody = null;

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
                if (req.Content != null)
                {
                    requestBody = req.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                }

                if (uri == "http://127.0.0.1:5088/api/v1/coverage/query")
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(coverageResponseJson, Encoding.UTF8, "application/json")
                    };
                }

                return new HttpResponseMessage(HttpStatusCode.NotFound);
            });

        var httpClient = new HttpClient(handlerMock.Object);
        var provider = new ManifestHubProvider(
            httpClient,
            cacheService,
            NullLogger<ManifestHubProvider>.Instance,
            appSettings: appSettings);

        var coverage = await provider.GetCoverageAsync([730]);

        requestedUrls.Should().Contain("http://127.0.0.1:5088/api/v1/coverage/query");
        requestBody.Should().Contain("730");
        coverage.Should().HaveCount(2);
        coverage.Should().Contain(a => a.DepotId == 731 && a.ManifestId == 500001);
        coverage.Should().Contain(a => a.DepotId == 732 && a.ManifestId == 500002);
    }

    [Fact]
    public async Task ContributionTransportService_QueryExistingManifestsAsync_QueriesCoverageQueryEndpoint()
    {
        var appSettings = new AppSettingsService(NullLogger<AppSettingsService>.Instance, Path.Combine(_testDir, "settings_ts_cov.json"));
        await appSettings.SetHubServerUrlAsync("http://127.0.0.1:5088");

        string? requestedUrl = null;
        var coverageResponseJson = """
        {
          "isSuccess": true,
          "existingManifests": [
            { "depotId": 441, "manifestId": 123456789 }
          ]
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
                requestedUrl = req.RequestUri?.ToString();
                if (requestedUrl == "http://127.0.0.1:5088/api/v1/coverage/query")
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(coverageResponseJson, Encoding.UTF8, "application/json")
                    };
                }

                return new HttpResponseMessage(HttpStatusCode.NotFound);
            });

        var httpClient = new HttpClient(handlerMock.Object);
        var transport = new ContributionTransportService(
            httpClient,
            null,
            NullLogger<ContributionTransportService>.Instance,
            null,
            appSettings);

        var existing = await transport.QueryExistingManifestsAsync(appIds: [440], depotIds: [441]);

        requestedUrl.Should().Be("http://127.0.0.1:5088/api/v1/coverage/query");
        existing.Should().HaveCount(1);
        existing[0].DepotId.Should().Be(441);
        existing[0].ManifestId.Should().Be(123456789UL);

        // Pre-seeded: IsAlreadyCredited must now be true for this manifest!
        transport.IsAlreadyCredited(441, 123456789UL).Should().BeTrue();
    }

    [Fact]
    public async Task ContributionTransportService_TransmitContributionAsync_DuplicateReceipt_CreditsZeroStardustAndMarksAlreadyCredited()
    {
        var appSettings = new AppSettingsService(NullLogger<AppSettingsService>.Instance, Path.Combine(_testDir, "settings_dup.json"));
        await appSettings.SetHubServerUrlAsync("http://127.0.0.1:5088");

        var duplicateReceiptJson = """
        {
          "receiptId": "rcpt_dup_123",
          "status": "Duplicate",
          "isSuccess": true,
          "stardustCredited": 0,
          "isDuplicate": true,
          "appId": 730,
          "depotId": 731,
          "manifestId": 9999999
        }
        """;

        var handlerMock = new Mock<HttpMessageHandler>();
        handlerMock.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync((HttpRequestMessage _, CancellationToken _) =>
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(duplicateReceiptJson, Encoding.UTF8, "application/json")
                };
            });

        var httpClient = new HttpClient(handlerMock.Object);
        var transport = new ContributionTransportService(
            httpClient,
            null,
            NullLogger<ContributionTransportService>.Instance,
            null,
            appSettings);

        var manifestBytes = CreateValidSteamProtobufManifest();
        string sha256;
        using (var sha = System.Security.Cryptography.SHA256.Create())
        {
            sha256 = Convert.ToHexString(sha.ComputeHash(manifestBytes)).ToLowerInvariant();
        }

        var submission = new ContributionSubmission
        {
            AppId = 730,
            DepotId = 731,
            ManifestId = 9999999UL,
            AnonymousContributorId = Guid.NewGuid().ToString(),
            ManifestPayload = manifestBytes,
            ManifestSizeBytes = manifestBytes.Length,
            ManifestHashSha256 = sha256,
            UserConsentGranted = true,
            SanitizationAuditHash = "audit_dup"
        };

        var receipt = await transport.TransmitContributionAsync(submission);

        receipt.IsSuccess.Should().BeTrue();
        receipt.IsDuplicate.Should().BeTrue();
        receipt.StardustCredited.Should().Be(0);

        // 0 points credited to Stardust balance
        transport.GetTotalStardustCredited().Should().Be(0);

        // Idempotently recorded: IsAlreadyCredited is true
        transport.IsAlreadyCredited(731, 9999999UL).Should().BeTrue();
    }

    [Fact]
    public async Task EndToEnd_VersionHistory_SnapshotDiff_And_IncrementalPlan_Verification()
    {
        // 1. Setup Mock HTTP Handler responding to Manifest Hub endpoints
        var handlerMock = new Mock<HttpMessageHandler>(MockBehavior.Strict);

        var historyJson = """
        {
          "appId": 730,
          "branch": "public",
          "totalItems": 2,
          "totalPages": 1,
          "page": 1,
          "pageSize": 20,
          "items": [
            {
              "appId": 730,
              "buildId": 2000,
              "branch": "public",
              "creationTime": "2024-05-10T12:00:00Z",
              "dateCertainty": "Verified",
              "completeness": "Complete",
              "depotManifests": { "731": 22222, "732": 33333, "733": 44444 }
            },
            {
              "appId": 730,
              "buildId": 1000,
              "branch": "public",
              "creationTime": null,
              "dateCertainty": "Unknown",
              "completeness": "Complete",
              "depotManifests": { "731": 11111, "732": 33333 }
            }
          ]
        }
        """;

        var compareJson = """
        {
          "appId": 730,
          "baseBuildId": 1000,
          "targetBuildId": 2000,
          "unchanged": [ { "depotId": 732, "depotName": "Depot 732", "baseManifestId": 33333, "targetManifestId": 33333, "sizeBytes": 500000000, "status": "Unchanged", "isAvailableInHub": true } ],
          "modified": [ { "depotId": 731, "depotName": "Depot 731", "baseManifestId": 11111, "targetManifestId": 22222, "sizeBytes": 150000000, "status": "Modified", "isAvailableInHub": true } ],
          "added": [ { "depotId": 733, "depotName": "Depot 733", "baseManifestId": null, "targetManifestId": 44444, "sizeBytes": 20000000, "status": "Added", "isAvailableInHub": true } ],
          "removed": [],
          "totalDownloadSizeBytes": 170000000,
          "totalSavedSizeBytes": 500000000
        }
        """;

        var snapshot2000Json = """
        {
          "appId": 730,
          "buildId": 2000,
          "branch": "public",
          "creationTime": "2024-05-10T12:00:00Z",
          "dateCertainty": "Verified",
          "completeness": "Complete",
          "depotManifests": { "731": 22222, "732": 33333, "733": 44444 }
        }
        """;

        handlerMock.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.Is<HttpRequestMessage>(req => req.RequestUri != null && req.RequestUri.ToString().Contains("/api/v1/builds/730/history")),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new StringContent(historyJson, Encoding.UTF8, "application/json")
            });

        handlerMock.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.Is<HttpRequestMessage>(req => req.RequestUri != null && req.RequestUri.ToString().Contains("/api/v1/builds/730/compare")),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new StringContent(compareJson, Encoding.UTF8, "application/json")
            });

        handlerMock.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.Is<HttpRequestMessage>(req => req.RequestUri != null && req.RequestUri.ToString().Contains("/api/v1/builds/730/2000")),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new StringContent(snapshot2000Json, Encoding.UTF8, "application/json")
            });

        var httpClient = new HttpClient(handlerMock.Object);
        var manifestCache = new ManifestCacheService(NullLogger<ManifestCacheService>.Instance, Path.Combine(_testDir, "Cache"));
        var provider = new ManifestHubProvider(
            httpClient,
            manifestCache,
            NullLogger<ManifestHubProvider>.Instance,
            hubBaseUrl: "http://127.0.0.1:8080");

        // 2. Fetch history and verify chronological sorting & date authenticity
        var history = await provider.GetBuildHistoryPagedAsync(730, "public", 1, 20);
        history.Should().NotBeNull();
        history!.Items.Should().HaveCount(2);

        var snap2000 = history.Items[0];
        snap2000.BuildId.Should().Be(2000UL);
        snap2000.CreationTime.Should().NotBeNull();
        snap2000.DateQuality.Should().Be(DateCertainty.Verified);
        snap2000.Completeness.Should().Be(SnapshotCompleteness.Complete);

        var snap1000 = history.Items[1];
        snap1000.BuildId.Should().Be(1000UL);
        snap1000.CreationTime.Should().BeNull(); // Authentic date was null -> transparently reported as unknown
        snap1000.DateQuality.Should().Be(DateCertainty.Unknown);

        // Wrap into client view model HubSnapshotItem
        var hubItem2000 = new HubSnapshotItem { Snapshot = snap2000 };
        hubItem2000.HasAuthenticDate.Should().BeTrue();
        hubItem2000.AuthenticDateDisplay.Should().Contain("2024-05-10");
        hubItem2000.CoverageStatus.Should().Be("Completo (verificado)");

        var hubItem1000 = new HubSnapshotItem { Snapshot = snap1000 };
        hubItem1000.HasAuthenticDate.Should().BeFalse();
        hubItem1000.AuthenticDateDisplay.Should().BeEmpty();

        // 3. Compare snapshots via Hub API contract
        var diff = await provider.CompareSnapshotsAsync(730, 1000UL, 2000UL);
        diff.Should().NotBeNull();
        diff!.Unchanged.Should().ContainSingle(u => u.DepotId == 732 && u.TargetManifestId == 33333);
        diff.Modified.Should().ContainSingle(m => m.DepotId == 731 && m.BaseManifestId == 11111 && m.TargetManifestId == 22222);
        diff.Added.Should().ContainSingle(a => a.DepotId == 733 && a.TargetManifestId == 44444);
        diff.Removed.Should().BeEmpty();
        diff.TotalSavedSizeBytes.Should().Be(500000000);

        // 4. Test client-side Differential inspection against currently installed instance
        var installedMap = new Dictionary<uint, ulong>
        {
            [731] = 11111, // Modified in target
            [732] = 33333  // Identical in target
        };

        var snapshotWithDiff = hubItem2000.WithDiffAgainst(installedMap, manifestCache);
        snapshotWithDiff.UnchangedCount.Should().Be(1);
        snapshotWithDiff.ModifiedCount.Should().Be(1);
        snapshotWithDiff.AddedCount.Should().Be(1);
        snapshotWithDiff.RemovedCount.Should().Be(0);

        // 5. Test Incremental Plan Generation
        // Only Modified and Added depots need to be scheduled for download
        var updatedDepots = snapshotWithDiff.DepotManifests.Select(kv => new DepotInfo
        {
            DepotId = kv.Key,
            ManifestId = kv.Value,
            Name = $"Depot {kv.Key}"
        }).ToList();

        var incrementalDepotsToDownload = updatedDepots
            .Where(d => !installedMap.TryGetValue(d.DepotId, out var curM) || curM != d.ManifestId)
            .ToList();

        // Verification: Depot 732 (unchanged) is completely excluded from download request!
        incrementalDepotsToDownload.Should().HaveCount(2);
        incrementalDepotsToDownload.Select(d => d.DepotId).Should().BeEquivalentTo([731u, 733u]);
        incrementalDepotsToDownload.Any(d => d.DepotId == 732).Should().BeFalse();
    }
}
