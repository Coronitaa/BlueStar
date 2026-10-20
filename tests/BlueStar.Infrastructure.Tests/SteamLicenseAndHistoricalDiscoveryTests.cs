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
using BlueStar.Infrastructure.Services;
using BlueStar.Infrastructure.Steam;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Moq.Protected;
using Xunit;

namespace BlueStar.Infrastructure.Tests;

public sealed class SteamLicenseAndHistoricalDiscoveryTests : IDisposable
{
    private readonly string _testDir;

    public SteamLicenseAndHistoricalDiscoveryTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "BlueStarLicenseTests_" + Guid.NewGuid().ToString("N"));
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
        ms.Write([0xD0, 0x17, 0xF6, 0x71]); // Magic 0x71F617D0
        ms.Write(BitConverter.GetBytes(100u)); // payload length

        using (var deflate = new DeflateStream(ms, CompressionLevel.Fastest, leaveOpen: true))
        {
            var dummyPayload = Encoding.UTF8.GetBytes("steam_protobuf_manifest_data_for_unit_tests");
            deflate.Write(dummyPayload);
        }

        return ms.ToArray();
    }

    // =========================================================================
    // 1. STEAM LICENSE EXTRACTION & PACKAGE EXPANSION TESTS
    // =========================================================================

    [Fact]
    public async Task SteamLicenseDiscovery_ExtractsOwnedApps_AndExpandsPackages()
    {
        // Arrange
        var cookieProviderMock = new Mock<ISteamSessionCookieProvider>();
        cookieProviderMock.Setup(c => c.GetSteamSessionCookiesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, string>
            {
                ["steamLoginSecure"] = "test_secure_session_token_12345",
                ["sessionid"] = "session_id_abcdef"
            });

        var httpHandlerMock = new Mock<HttpMessageHandler>();
        httpHandlerMock.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync((HttpRequestMessage req, CancellationToken _) =>
            {
                var uri = req.RequestUri?.ToString() ?? string.Empty;

                // Steam Web API userdata
                if (uri.Contains("dynamicstore/userdata"))
                {
                    var json = """
                    {
                        "rgOwnedApps": [730, 440],
                        "rgOwnedPackages": [100]
                    }
                    """;
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(json, Encoding.UTF8, "application/json")
                    };
                }

                // Steam Store package details API
                if (uri.Contains("packagedetails") && uri.Contains("packageids=100"))
                {
                    var pkgJson = """
                    {
                        "100": {
                            "success": true,
                            "data": {
                                "name": "Valve Complete Pack",
                                "apps": [
                                    { "id": 570, "name": "Dota 2" },
                                    { "id": 730, "name": "Counter-Strike 2" }
                                ]
                            }
                        }
                    }
                    """;
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(pkgJson, Encoding.UTF8, "application/json")
                    };
                }

                return new HttpResponseMessage(HttpStatusCode.NotFound);
            });

        var httpClient = new HttpClient(httpHandlerMock.Object);
        var metadataProvider = new SteamStoreApiClient(httpClient, NullLogger<SteamStoreApiClient>.Instance);
        var dlcClassifier = new DlcPayloadClassifier(NullLogger<DlcPayloadClassifier>.Instance);
        var packageResolver = new SteamPackageResolver(httpClient, NullLogger<SteamPackageResolver>.Instance);

        var service = new SteamLicenseDiscoveryService(
            httpClient,
            cookieProviderMock.Object,
            metadataProvider,
            dlcClassifier,
            packageResolver,
            NullLogger<SteamLicenseDiscoveryService>.Instance);

        var options = new SteamLicenseDiscoveryOptions
        {
            IncludeLocalClientCache = false,
            IncludeWebSession = true,
            EnrichMetadata = false,
            ClassifyDlcs = false,
            ResolvePackages = true
        };

        // Act
        var result = await service.DiscoverLicensesAsync(options, CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        result.IsSuccess.Should().BeTrue();
        result.BlockageReason.Should().Be(SteamLicenseBlockageReason.None);

        // Discovered AppIDs should include 730, 440 (direct) and 570 (expanded from package 100)
        var appIds = result.LicensedApps.Select(a => a.AppId).ToList();
        appIds.Should().Contain([730u, 440u, 570u]);
        result.DiscoveredPackageIds.Should().Contain(100u);

        // App 570 should track Package 100 in its provenance
        var dotaItem = result.LicensedApps.FirstOrDefault(a => a.AppId == 570);
        dotaItem.Should().NotBeNull();
        dotaItem!.PackageIds.Should().Contain(100u);
    }

    // =========================================================================
    // 2. DLC CLASSIFICATION FILTERING TESTS
    // =========================================================================

    [Fact]
    public void DlcClassification_FiltersEntitlementOnly_FromCommunityContributions()
    {
        // Arrange
        var classifier = new DlcPayloadClassifier(NullLogger<DlcPayloadClassifier>.Instance);

        // DLC 1: Episodic downloadable chapter with non-zero payload depots
        var downloadableDepots = new List<DepotInfo>
        {
            new() { DepotId = 10011, ManifestId = 999911, SizeBytes = 500_000_000 }
        };
        var downloadableClassification = classifier.Classify(
            dlcAppId: 1001,
            parentAppId: 1000,
            dlcName: "Chapter 2 DLC",
            depots: downloadableDepots,
            isMetadataEnriched: true);

        // DLC 2: Entitlement unlock item with verified 0 depots
        var entitlementDepots = new List<DepotInfo>();
        var entitlementClassification = classifier.Classify(
            dlcAppId: 1002,
            parentAppId: 1000,
            dlcName: "Soundtrack & Skin Unlock",
            depots: entitlementDepots,
            isMetadataEnriched: true,
            hasEntitlementEvidence: true);

        // Act
        var dlcItem1 = new SteamLicenseItem
        {
            AppId = 1001,
            Name = "Chapter 2 DLC",
            IsDlc = true,
            ParentAppId = 1000,
            Classification = downloadableClassification,
            IsEligibleForContribution = downloadableClassification.PayloadType == DlcPayloadType.DownloadablePayload
        };

        var dlcItem2 = new SteamLicenseItem
        {
            AppId = 1002,
            Name = "Soundtrack & Skin Unlock",
            IsDlc = true,
            ParentAppId = 1000,
            Classification = entitlementClassification,
            IsEligibleForContribution = entitlementClassification.PayloadType == DlcPayloadType.DownloadablePayload
        };

        var baseGameItem = new SteamLicenseItem
        {
            AppId = 1000,
            Name = "Base Game",
            IsDlc = false,
            IsEligibleForContribution = true
        };

        var discoveryResult = SteamLicenseDiscoveryResult.Success([baseGameItem, dlcItem1, dlcItem2]);

        // Assert
        dlcItem1.IsEligibleForContribution.Should().BeTrue();
        dlcItem2.IsEligibleForContribution.Should().BeFalse();
        baseGameItem.IsEligibleForContribution.Should().BeTrue();

        discoveryResult.DownloadableDlcCount.Should().Be(1);
        discoveryResult.EntitlementOnlyDlcCount.Should().Be(1);
    }

    // =========================================================================
    // 3. MULTI-SOURCE CANDIDATE UNIFICATION & AUTHENTIC DATE PRESERVATION
    // =========================================================================

    [Fact]
    public void HistoricalRevisionDiscovery_UnifiesCandidates_AndPreservesAuthenticPublicationDates()
    {
        // Arrange: Multiple sources reporting revisions for App 730
        var authenticDate = new DateTimeOffset(2024, 5, 20, 14, 30, 0, TimeSpan.Zero);

        var candidates = new List<HubRevisionItem>
        {
            // Candidate 1: ManifestHub without authentic publication date (null)
            new()
            {
                AppId = 730,
                DepotId = 731,
                ManifestId = 100001,
                BranchName = "public",
                AuthenticPublicationDate = null,
                Provenance = "ManifestHub",
                SizeOriginal = 1000
            },
            // Candidate 2: ManifestHub2 with authentic publication date
            new()
            {
                AppId = 730,
                DepotId = 731,
                ManifestId = 100001,
                BranchName = "public",
                AuthenticPublicationDate = authenticDate,
                Provenance = "ManifestHub2",
                SizeOriginal = 1000
            },
            // Candidate 3: K25 reporting a different revision with authentic date
            new()
            {
                AppId = 730,
                DepotId = 732,
                ManifestId = 200002,
                BranchName = "public",
                AuthenticPublicationDate = authenticDate.AddDays(-10),
                Provenance = "K25",
                SizeOriginal = 2000
            },
            // Candidate 4: DepotBox reporting the same revision 200002 without authentic date
            new()
            {
                AppId = 730,
                DepotId = 732,
                ManifestId = 200002,
                BranchName = "public",
                AuthenticPublicationDate = null,
                Provenance = "DepotBox",
                SizeOriginal = 2000
            }
        };

        // Act
        var unified = HistoricalRevisionDiscoveryService.UnifyCandidateRevisions(candidates);

        // Assert
        unified.Should().HaveCount(2, "duplicates sharing exact (AppId, DepotId, ManifestGid) must be unified");

        var rev1 = unified.First(r => r.DepotId == 731 && r.ManifestId == 100001);
        rev1.HasAuthenticDate.Should().BeTrue();
        rev1.AuthenticPublicationDate.Should().Be(authenticDate, "authentic date from ManifestHub2 must be preserved");
        rev1.Provenance.Should().Contain("ManifestHub");
        rev1.Provenance.Should().Contain("ManifestHub2");

        var rev2 = unified.First(r => r.DepotId == 732 && r.ManifestId == 200002);
        rev2.HasAuthenticDate.Should().BeTrue();
        rev2.AuthenticPublicationDate.Should().Be(authenticDate.AddDays(-10), "authentic date from K25 must be preserved");
        rev2.Provenance.Should().Contain("K25");
        rev2.Provenance.Should().Contain("DepotBox");

        // Verify ordering: newest authentic date first
        unified[0].ManifestId.Should().Be(100001);
        unified[1].ManifestId.Should().Be(200002);
    }

    [Fact]
    public void HistoricalRevisionDiscovery_UnknownDate_NeverFabricatesTimestamp_AndRanksAfterAuthenticDates()
    {
        // Arrange
        var knownDate = new DateTimeOffset(2023, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var candidates = new List<HubRevisionItem>
        {
            // Rev with unknown date has higher numerical ManifestId
            new()
            {
                AppId = 440,
                DepotId = 441,
                ManifestId = 999999999,
                AuthenticPublicationDate = null,
                Provenance = "Community",
                IngestionDate = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)
            },
            // Rev with authentic date has lower numerical ManifestId
            new()
            {
                AppId = 440,
                DepotId = 441,
                ManifestId = 111111111,
                AuthenticPublicationDate = knownDate,
                Provenance = "ManifestHub"
            }
        };

        // Act
        var unified = HistoricalRevisionDiscoveryService.UnifyCandidateRevisions(candidates);

        // Assert: Authentic date revision ranks first regardless of numerical ManifestId
        unified.Should().HaveCount(2);
        unified[0].ManifestId.Should().Be(111111111);
        unified[0].HasAuthenticDate.Should().BeTrue();

        unified[1].ManifestId.Should().Be(999999999);
        unified[1].HasAuthenticDate.Should().BeFalse();
        unified[1].AuthenticPublicationDate.Should().BeNull();
    }

    // =========================================================================
    // 4. MULTI-SOURCE DISCOVERY & BINARY FETCH TESTS
    // =========================================================================

    [Fact]
    public async Task HistoricalRevisionDiscovery_FetchesManifestBinary_WithRouteFallback()
    {
        // Arrange
        var cacheDir = Path.Combine(_testDir, "cache");
        var targetDir = Path.Combine(_testDir, "target");
        Directory.CreateDirectory(targetDir);

        var cacheService = new ManifestCacheService(NullLogger<ManifestCacheService>.Instance, cacheDir);
        var manifestBytes = CreateValidSteamProtobufManifest();
        var attemptedUrls = new List<string>();

        var httpHandlerMock = new Mock<HttpMessageHandler>();
        httpHandlerMock.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync((HttpRequestMessage req, CancellationToken _) =>
            {
                var uri = req.RequestUri?.ToString() ?? string.Empty;
                attemptedUrls.Add(uri);

                // ManifestHub returns valid manifest binary
                if (uri.Contains("Coronitaa/BlueStar-ManifestHub"))
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new ByteArrayContent(manifestBytes)
                    };
                }

                return new HttpResponseMessage(HttpStatusCode.NotFound);
            });

        var httpClient = new HttpClient(httpHandlerMock.Object);
        var service = new HistoricalRevisionDiscoveryService(
            http: httpClient,
            cacheService: cacheService,
            keyRepository: null,
            appSettings: null,
            logger: NullLogger<HistoricalRevisionDiscoveryService>.Instance);

        // Act
        var savedFile = await service.FetchManifestBinaryAsync(
            appId: 730,
            depotId: 731,
            manifestId: 55555,
            targetDirectory: targetDir,
            CancellationToken.None);

        // Assert
        File.Exists(savedFile).Should().BeTrue();
        cacheService.HasManifest(731, 55555).Should().BeTrue();
        attemptedUrls.Should().Contain(u => u.Contains("Coronitaa/BlueStar-ManifestHub"));
    }

    // =========================================================================
    // 5. PRIVACY INVARIANT VERIFICATION
    // =========================================================================

    private sealed class MemoryTestLogger<T> : ILogger<T>
    {
        public List<string> LoggedMessages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            LoggedMessages.Add(formatter(state, exception));
        }
    }

    [Fact]
    public async Task PrivacyInvariant_NeverLogsOrTransmits_SessionTokensOrCredentials()
    {
        // Arrange
        const string secretSessionToken = "ultra_secret_steam_token_999888";
        const string secretSessionId = "secret_session_id_444555";

        var cookieProviderMock = new Mock<ISteamSessionCookieProvider>();
        cookieProviderMock.Setup(c => c.GetSteamSessionCookiesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, string>
            {
                ["steamLoginSecure"] = secretSessionToken,
                ["sessionid"] = secretSessionId
            });

        var sentRequests = new List<HttpRequestMessage>();
        var httpHandlerMock = new Mock<HttpMessageHandler>();
        httpHandlerMock.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync((HttpRequestMessage req, CancellationToken _) =>
            {
                sentRequests.Add(req);
                var json = "{\"rgOwnedApps\": [730], \"rgOwnedPackages\": []}";
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json")
                };
            });

        var httpClient = new HttpClient(httpHandlerMock.Object);
        var metadataProvider = new SteamStoreApiClient(httpClient, NullLogger<SteamStoreApiClient>.Instance);
        var dlcClassifier = new DlcPayloadClassifier(NullLogger<DlcPayloadClassifier>.Instance);
        var testLogger = new MemoryTestLogger<SteamLicenseDiscoveryService>();

        var service = new SteamLicenseDiscoveryService(
            httpClient,
            cookieProviderMock.Object,
            metadataProvider,
            dlcClassifier,
            null,
            testLogger);

        // Act
        var result = await service.DiscoverLicensesAsync(new SteamLicenseDiscoveryOptions
        {
            IncludeLocalClientCache = false,
            IncludeWebSession = true,
            EnrichMetadata = false,
            ClassifyDlcs = false
        });

        // Assert 1: Secrets are NOT in logs
        testLogger.LoggedMessages.Should().NotBeEmpty();
        foreach (var msg in testLogger.LoggedMessages)
        {
            msg.Should().NotContain(secretSessionToken);
            msg.Should().NotContain(secretSessionId);
        }

        // Assert 2: Only Valve's official steam store endpoints receive the cookie
        foreach (var req in sentRequests)
        {
            var host = req.RequestUri?.Host ?? string.Empty;
            host.Should().Contain("steampowered.com", "cookies must only ever be sent to Valve's official store host");
        }
    }

    // =========================================================================
    // 6. ERROR HANDLING & NO SILENT FALLBACK TO INSTALLED INSTANCES
    // =========================================================================

    [Fact]
    public async Task SteamLicenseDiscovery_WhenNoSessionCookies_ReportsNoSteamSession_AndDoesNotFallBack()
    {
        // Arrange
        var cookieProviderMock = new Mock<ISteamSessionCookieProvider>();
        cookieProviderMock.Setup(c => c.GetSteamSessionCookiesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyDictionary<string, string>?)null);

        var httpHandlerMock = new Mock<HttpMessageHandler>();
        var httpClient = new HttpClient(httpHandlerMock.Object);
        var metadataProvider = new SteamStoreApiClient(httpClient, NullLogger<SteamStoreApiClient>.Instance);
        var dlcClassifier = new DlcPayloadClassifier(NullLogger<DlcPayloadClassifier>.Instance);

        var service = new SteamLicenseDiscoveryService(
            httpClient,
            cookieProviderMock.Object,
            metadataProvider,
            dlcClassifier,
            null,
            NullLogger<SteamLicenseDiscoveryService>.Instance);

        // Act
        var result = await service.DiscoverLicensesAsync(new SteamLicenseDiscoveryOptions
        {
            IncludeLocalClientCache = false,
            IncludeWebSession = true
        });

        // Assert
        result.Should().NotBeNull();
        result.IsSuccess.Should().BeFalse();
        result.BlockageReason.Should().Be(SteamLicenseBlockageReason.NoSteamSession);
        result.BlockageDetails.Should().Contain("No active Steam session");
        result.LicensedApps.Should().BeEmpty("must NEVER silently fall back to installed instances");
    }

    [Fact]
    public async Task SteamLicenseDiscovery_WhenSessionExpired_ReportsUnauthenticated_AndDoesNotFallBack()
    {
        // Arrange
        var cookieProviderMock = new Mock<ISteamSessionCookieProvider>();
        cookieProviderMock.Setup(c => c.GetSteamSessionCookiesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, string>
            {
                ["steamLoginSecure"] = "expired_token_123"
            });

        var httpHandlerMock = new Mock<HttpMessageHandler>();
        httpHandlerMock.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync((HttpRequestMessage req, CancellationToken _) =>
            {
                var uri = req.RequestUri?.ToString() ?? string.Empty;
                // Steam returns empty owned apps when token is unauthenticated
                if (uri.Contains("dynamicstore/userdata"))
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("{\"rgOwnedApps\": [], \"rgOwnedPackages\": []}", Encoding.UTF8, "application/json")
                    };
                }
                // /account/licenses/ returns login form
                if (uri.Contains("account/licenses"))
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("<div id=\"login_btn_signin\">Please log in</div>", Encoding.UTF8, "text/html")
                    };
                }
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            });

        var httpClient = new HttpClient(httpHandlerMock.Object);
        var metadataProvider = new SteamStoreApiClient(httpClient, NullLogger<SteamStoreApiClient>.Instance);
        var dlcClassifier = new DlcPayloadClassifier(NullLogger<DlcPayloadClassifier>.Instance);

        var service = new SteamLicenseDiscoveryService(
            httpClient,
            cookieProviderMock.Object,
            metadataProvider,
            dlcClassifier,
            null,
            NullLogger<SteamLicenseDiscoveryService>.Instance);

        // Act
        var result = await service.DiscoverLicensesAsync(new SteamLicenseDiscoveryOptions
        {
            IncludeLocalClientCache = false,
            IncludeWebSession = true
        });

        // Assert
        result.Should().NotBeNull();
        result.IsSuccess.Should().BeFalse();
        result.BlockageReason.Should().Be(SteamLicenseBlockageReason.Unauthenticated);
        result.BlockageDetails.Should().Contain("expired or unauthenticated");
        result.LicensedApps.Should().BeEmpty("must NEVER silently fall back to installed instances");
    }

    [Fact]
    public void BuildSanitizedCookieHeader_FiltersNonWhitelisted_AndEnsuresStrictAscii()
    {
        // Arrange: cookies containing unicode username, arbitrary cookies, and valid steamLoginSecure
        var cookies = new Dictionary<string, string>
        {
            ["steamLoginSecure"] = "76561198000000000%7C%7Cvalid_session_token",
            ["sessionid"] = "abcdef123456",
            ["userName"] = "Corønita",
            ["randomJunk"] = "ñandu_123",
            ["steamCountry"] = "AR|Español"
        };

        // Act
        var header = SteamLicenseDiscoveryService.BuildSanitizedCookieHeader(cookies);

        // Assert
        header.Should().NotBeNullOrWhiteSpace();
        header.Should().Contain("steamLoginSecure=76561198000000000%7C%7Cvalid_session_token");
        header.Should().Contain("sessionid=abcdef123456");
        header.Should().NotContain("userName=");
        header.Should().NotContain("randomJunk=");

        // Verify 100% of characters are strictly printable ASCII (code <= 127)
        foreach (var c in header)
        {
            ((int)c).Should().BeInRange(32, 126, "every char in HTTP header must be strict ASCII");
        }
    }
}
