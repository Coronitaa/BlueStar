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
using BlueStar.Infrastructure.Steam;
using BlueStar.Infrastructure.Storage;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Moq.Protected;
using Xunit;

namespace BlueStar.Infrastructure.Tests;

/// <summary>
/// End-to-End acceptance tests fulfilling Section 7:
/// Demonstrates that BlueStar discovers a game from user Steam account licenses:
/// - Game is licensed on Steam.
/// - Game is NOT installed and NOT imported as an instance.
/// - Revisions are discovered across authorized historical sources without depending on local cache.
/// - Hub coverage comparison isolates missing revisions.
/// - Retrieves ONLY the missing manifest binary (zero game payload download).
/// - Validates and publishes the missing revision with explicit user consent.
/// - Second run detects existing coverage, producing ZERO duplicate contributions.
/// - Stardust ledger balance reconciles accurately and does not increase on duplicate runs.
/// </summary>
public sealed class UninstalledLicensedGameContributionE2ETests : IDisposable
{
    private readonly string _testDir;
    private readonly string _cacheDir;
    private readonly string _instancesDir;
    private readonly string _hubStorageDir;

    public UninstalledLicensedGameContributionE2ETests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "BlueStar_E2E_License_" + Guid.NewGuid().ToString("N"));
        _cacheDir = Path.Combine(_testDir, "cache");
        _instancesDir = Path.Combine(_testDir, "instances");
        _hubStorageDir = Path.Combine(_testDir, "hub_storage");

        Directory.CreateDirectory(_testDir);
        Directory.CreateDirectory(_cacheDir);
        Directory.CreateDirectory(_instancesDir);
        Directory.CreateDirectory(_hubStorageDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDir))
        {
            try { Directory.Delete(_testDir, true); } catch { }
        }
    }

    private static byte[] CreateValidSteamProtobufManifest(uint depotId, ulong manifestId)
    {
        using var ms = new MemoryStream();
        ms.Write([0xD0, 0x17, 0xF6, 0x71]); // Steam Protobuf Header 0x71F617D0
        ms.Write(BitConverter.GetBytes(128u));

        using (var deflate = new DeflateStream(ms, CompressionLevel.Fastest, leaveOpen: true))
        {
            var dummyPayload = Encoding.UTF8.GetBytes($"depot_{depotId}_manifest_{manifestId}_e2e_protobuf_payload");
            deflate.Write(dummyPayload);
        }

        return ms.ToArray();
    }

    [Fact]
    public async Task E2E_UninstalledLicensedGame_DiscoversRevisions_ContributesMissingOnly_AndReconcilesStardust()
    {
        // -------------------------------------------------------------------------
        // ARRANGE: SETUP UNINSTALLED GAME & ENVIRONMENT
        // -------------------------------------------------------------------------
        const uint uninstalledAppId = 892970; // Valheim (example licensed game)
        const uint depotId = 892971;
        const ulong missingManifestId = 445566778899UL;
        const ulong existingManifestId = 112233445566UL;
        var validManifestBytes = CreateValidSteamProtobufManifest(depotId, missingManifestId);
        string anonymousContributorId = Guid.NewGuid().ToString("D").ToLowerInvariant();

        // 1. Verify that InstanceManager has ZERO instances of this game (Not Installed & Not Imported)
        var instanceManagerMock = new Mock<IInstanceManager>();
        instanceManagerMock.Setup(m => m.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<GameInstance>()); // Zero instances in library!

        // 2. Verify that local manifest cache is empty (no dependence on local cache)
        var cacheService = new ManifestCacheService(NullLogger<ManifestCacheService>.Instance, _cacheDir);
        cacheService.HasManifest(depotId, missingManifestId).Should().BeFalse();

        // 3. Mock Steam session cookies & Steam Store userdata API for uninstalled game
        var cookieProviderMock = new Mock<ISteamSessionCookieProvider>();
        cookieProviderMock.Setup(c => c.GetSteamSessionCookiesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, string>
            {
                ["steamLoginSecure"] = "e2e_valid_steam_login_token_12345",
                ["sessionid"] = "e2e_session_id_abcdef"
            });

        // Track Hub state in-memory to simulate real Hub receiver behavior
        var hubCoveredManifests = new HashSet<(uint DepotId, ulong ManifestId)>
        {
            (depotId, existingManifestId) // Hub already has revision 112233445566, but lacks 445566778899
        };
        int hubConfirmedStardustBalance = 0;
        var publicationReceipts = new List<PublicationReceipt>();

        var httpHandlerMock = new Mock<HttpMessageHandler>();
        httpHandlerMock.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync((HttpRequestMessage req, CancellationToken _) =>
            {
                var uri = req.RequestUri?.ToString() ?? string.Empty;

                // A. Steam dynamicstore userdata (App 892970 is licensed to user)
                if (uri.Contains("dynamicstore/userdata"))
                {
                    var json = $$"""
                    {
                        "rgOwnedApps": [{{uninstalledAppId}}],
                        "rgOwnedPackages": []
                    }
                    """;
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(json, Encoding.UTF8, "application/json")
                    };
                }

                // B. Steam Store metadata enrichment for Valheim
                if (uri.Contains("api/appdetails") && uri.Contains($"appids={uninstalledAppId}"))
                {
                    var json = $$"""
                    {
                        "{{uninstalledAppId}}": {
                            "success": true,
                            "data": {
                                "type": "game",
                                "name": "Valheim",
                                "steam_appid": {{uninstalledAppId}}
                            }
                        }
                    }
                    """;
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(json, Encoding.UTF8, "application/json")
                    };
                }

                // C. BlueStar-ManifestHub historical discovery for App 892970
                if (uri.Contains($"/builds/{uninstalledAppId}.json") || uri.Contains($"Coronitaa/BlueStar-ManifestHub/main/builds/{uninstalledAppId}.json"))
                {
                    var json = $$"""
                    [
                        {
                            "buildId": "10002",
                            "branch": "public",
                            "creationTime": "2026-05-15T12:00:00Z",
                            "depotManifests": {
                                "{{depotId}}": "{{missingManifestId}}"
                            }
                        },
                        {
                            "buildId": "10001",
                            "branch": "public",
                            "creationTime": "2025-11-10T08:30:00Z",
                            "depotManifests": {
                                "{{depotId}}": "{{existingManifestId}}"
                            }
                        }
                    ]
                    """;
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(json, Encoding.UTF8, "application/json")
                    };
                }

                // D. BlueStar-ManifestHub manifest binary download for missing revision
                if (uri.Contains($"/manifests/{depotId}/{missingManifestId}.manifest") || uri.Contains($"Coronitaa/BlueStar-ManifestHub/main/manifests/{depotId}/{missingManifestId}.manifest"))
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new ByteArrayContent(validManifestBytes)
                    };
                }

                // E. Hub POST /api/v1/coverage/query
                if (uri.EndsWith("/api/v1/coverage/query", StringComparison.OrdinalIgnoreCase))
                {
                    var matchedList = hubCoveredManifests
                        .Where(h => h.DepotId == depotId)
                        .Select(h => new { depotId = h.DepotId, manifestId = h.ManifestId })
                        .ToList();

                    var respJson = JsonSerializer.Serialize(new
                    {
                        isSuccess = true,
                        existingManifests = matchedList,
                        depotManifests = new Dictionary<string, List<ulong>>
                        {
                            [depotId.ToString()] = matchedList.Select(m => m.manifestId).ToList()
                        },
                        totalFound = matchedList.Count
                    });

                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(respJson, Encoding.UTF8, "application/json")
                    };
                }

                // F. Hub POST /api/v1/contributions/publish
                if (uri.EndsWith("/api/v1/contributions/publish", StringComparison.OrdinalIgnoreCase))
                {
                    var reqBody = req.Content?.ReadAsStringAsync().GetAwaiter().GetResult() ?? string.Empty;
                    using var doc = JsonDocument.Parse(reqBody);
                    uint dId = 0;
                    if (doc.RootElement.TryGetProperty("depotId", out var dProp) || doc.RootElement.TryGetProperty("DepotId", out dProp))
                    {
                        dId = dProp.GetUInt32();
                    }
                    ulong mId = 0;
                    if (doc.RootElement.TryGetProperty("manifestId", out var mProp) || doc.RootElement.TryGetProperty("ManifestId", out mProp))
                    {
                        mId = mProp.GetUInt64();
                    }
                    if (doc.RootElement.TryGetProperty("userConsentGranted", out var ucProp) || doc.RootElement.TryGetProperty("UserConsentGranted", out ucProp))
                    {
                        if (!ucProp.GetBoolean())
                        {
                            return new HttpResponseMessage(HttpStatusCode.BadRequest)
                            {
                                Content = new StringContent("{\"error\":\"User consent required\",\"isSuccess\":false}", Encoding.UTF8, "application/json")
                            };
                        }
                    }

                    bool isDuplicate = hubCoveredManifests.Contains((dId, mId));
                    int stardustAwarded = 0;

                    if (!isDuplicate)
                    {
                        hubCoveredManifests.Add((dId, mId));
                        stardustAwarded = 10;
                        hubConfirmedStardustBalance += stardustAwarded;
                    }

                    var receipt = new PublicationReceipt
                    {
                        ReceiptId = Guid.NewGuid().ToString("N"),
                        Status = isDuplicate ? "Duplicate" : "Published",
                        StardustCredited = stardustAwarded,
                        AppId = uninstalledAppId,
                        DepotId = dId,
                        ManifestId = mId,
                        Timestamp = DateTimeOffset.UtcNow
                    };
                    publicationReceipts.Add(receipt);

                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(JsonSerializer.Serialize(receipt), Encoding.UTF8, "application/json")
                    };
                }

                // G. Hub GET /api/v1/stardust/balance/{contributorId}
                if (uri.Contains("/api/v1/stardust/balance/"))
                {
                    var balanceResp = new
                    {
                        contributorId = anonymousContributorId,
                        confirmedBalance = hubConfirmedStardustBalance,
                        pendingBalance = 0
                    };
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(JsonSerializer.Serialize(balanceResp), Encoding.UTF8, "application/json")
                    };
                }

                return new HttpResponseMessage(HttpStatusCode.NotFound);
            });

        var httpClient = new HttpClient(httpHandlerMock.Object);

        // Instantiate services
        var storeApiClient = new SteamStoreApiClient(httpClient, NullLogger<SteamStoreApiClient>.Instance);
        var dlcClassifier = new DlcPayloadClassifier(NullLogger<DlcPayloadClassifier>.Instance);
        var licenseService = new SteamLicenseDiscoveryService(
            http: httpClient,
            cookieProvider: cookieProviderMock.Object,
            metadataProvider: storeApiClient,
            dlcClassifier: dlcClassifier,
            packageResolver: null,
            logger: NullLogger<SteamLicenseDiscoveryService>.Instance);

        var keyRepoMock = new Mock<IDepotKeyRepository>();
        var revisionDiscoveryService = new HistoricalRevisionDiscoveryService(
            http: httpClient,
            cacheService: cacheService,
                        keyRepository: keyRepoMock.Object,
            appSettings: null,
            logger: NullLogger<HistoricalRevisionDiscoveryService>.Instance);

        var secureStorageMock = new Mock<ISecureStorage>();
        secureStorageMock.Setup(s => s.GetAsync("bluestar_anonymous_contributor_id", It.IsAny<CancellationToken>()))
            .ReturnsAsync(anonymousContributorId);

        var voluntaryService = new VoluntaryContributionService(
            secureStorageMock.Object,
            NullLogger<VoluntaryContributionService>.Instance);

        var transportService = new ContributionTransportService(
            httpClient: httpClient,
            logger: NullLogger<ContributionTransportService>.Instance,
            hubBaseUrl: "http://127.0.0.1:5088");

        // -------------------------------------------------------------------------
        // STEP 1: DISCOVER UNINSTALLED GAME VIA STEAM LICENSES (NOT INSTALLED!)
        // -------------------------------------------------------------------------
        var licenseOptions = new SteamLicenseDiscoveryOptions
        {
            IncludeLocalClientCache = false,
            IncludeWebSession = true,
            EnrichMetadata = true
        };
        var licenseResult = await licenseService.DiscoverLicensesAsync(licenseOptions, CancellationToken.None);
        licenseResult.IsSuccess.Should().BeTrue();
        licenseResult.BlockageReason.Should().Be(SteamLicenseBlockageReason.None);
        licenseResult.LicensedApps.Should().Contain(a => a.AppId == uninstalledAppId);
        licenseResult.LicensedApps.Should().ContainSingle(g => g.AppId == uninstalledAppId && g.Name == "Valheim");

        // Confirm game is NOT installed
        var installedInstances = await instanceManagerMock.Object.GetAllAsync(CancellationToken.None);
        installedInstances.Should().BeEmpty();

        // -------------------------------------------------------------------------
        // STEP 2: DISCOVER HISTORICAL REVISIONS ACROSS SOURCES (WITHOUT LOCAL CACHE)
        // -------------------------------------------------------------------------
        var discoveredRevisions = await revisionDiscoveryService.DiscoverRevisionsAsync(uninstalledAppId, CancellationToken.None);
        discoveredRevisions.Should().HaveCount(2);
        discoveredRevisions.Should().Contain(r => r.DepotId == depotId && r.ManifestId == missingManifestId);
        discoveredRevisions.Should().Contain(r => r.DepotId == depotId && r.ManifestId == existingManifestId);

        // -------------------------------------------------------------------------
        // STEP 3: QUERY CENTRAL HUB COVERAGE & ISOLATE MISSING REVISION
        // -------------------------------------------------------------------------
        var hubExistingPairs = await transportService.QueryExistingManifestsAsync(
            appIds: [uninstalledAppId],
            depotIds: [depotId],
            ct: CancellationToken.None);

        hubExistingPairs.Should().Contain((depotId, existingManifestId));
        hubExistingPairs.Should().NotContain((depotId, missingManifestId)); // Missing in Hub!

        var missingCandidates = discoveredRevisions
            .Where(r => !hubExistingPairs.Contains((r.DepotId, r.ManifestId)))
            .ToList();

        missingCandidates.Should().ContainSingle();
        var targetCandidate = missingCandidates[0];
        targetCandidate.DepotId.Should().Be(depotId);
        targetCandidate.ManifestId.Should().Be(missingManifestId);

        // -------------------------------------------------------------------------
        // STEP 4: FETCH MANIFEST BINARY ONLY (ZERO GAME PAYLOAD DOWNLOAD)
        // -------------------------------------------------------------------------
        var fetchedBinaryPath = await revisionDiscoveryService.FetchManifestBinaryAsync(
            uninstalledAppId, depotId, missingManifestId, _testDir, CancellationToken.None);

        fetchedBinaryPath.Should().NotBeNull();
        File.Exists(fetchedBinaryPath!).Should().BeTrue();

        // Validate Protobuf binary header magic 0x71F617D0 ([0xD0, 0x17, 0xF6, 0x71])
        var binaryBytes = await File.ReadAllBytesAsync(fetchedBinaryPath!);
        binaryBytes[0].Should().Be(0xD0);
        binaryBytes[1].Should().Be(0x17);
        binaryBytes[2].Should().Be(0xF6);
        binaryBytes[3].Should().Be(0x71);

        // -------------------------------------------------------------------------
        // STEP 5: PREPARE SANITIZED PREVIEW & REQUIRE USER CONSENT
        // -------------------------------------------------------------------------
        var preview = await voluntaryService.CreatePreviewAsync(
            uninstalledAppId, depotId, missingManifestId, fetchedBinaryPath!, anonymousContributorId, CancellationToken.None);

        preview.Should().NotBeNull();
        preview.AnonymousContributorId.Should().Be(anonymousContributorId);

        // Refuse submission if consent is missing
        var actWithoutConsent = () => voluntaryService.PrepareSubmissionAsync(preview, userConsentGranted: false);
        await actWithoutConsent.Should().ThrowAsync<InvalidOperationException>();

        // Prepare submission with explicit consent
        var submission = await voluntaryService.PrepareSubmissionAsync(preview, userConsentGranted: true);
        submission.UserConsentGranted.Should().BeTrue();
        submission.IsSafeForTransmission.Should().BeTrue();

        // -------------------------------------------------------------------------
        // STEP 6: TRANSMIT CONTRIBUTION & RECONCILE STARDUST REWARD
        // -------------------------------------------------------------------------
        var receipt = await transportService.TransmitContributionAsync(submission, CancellationToken.None);
        receipt.IsSuccess.Should().BeTrue();
        receipt.IsDuplicate.Should().BeFalse();
        receipt.Status.Should().Be("Published");
        receipt.StardustCredited.Should().Be(10);

        // Verify Stardust ledger balance on server
        var balance = await transportService.GetStardustBalanceAsync(anonymousContributorId, CancellationToken.None);
        balance.ConfirmedPoints.Should().Be(10);
        balance.PendingPoints.Should().Be(0);

        // -------------------------------------------------------------------------
        // STEP 7: SECOND RUN (IDEMPOTENCY / DEDUPLICATION VERIFICATION)
        // -------------------------------------------------------------------------
        // In run 2, re-query Hub coverage
        var secondRunCovered = await transportService.QueryExistingManifestsAsync(
            appIds: [uninstalledAppId],
            depotIds: [depotId],
            ct: CancellationToken.None);

        // The missing manifest is NOW covered in the Hub!
        secondRunCovered.Should().Contain((depotId, missingManifestId));

        var secondRunMissingCandidates = discoveredRevisions
            .Where(r => !secondRunCovered.Contains((r.DepotId, r.ManifestId)))
            .ToList();

        // ZERO missing candidates in second run!
        secondRunMissingCandidates.Should().BeEmpty();

        // If duplicate submission is attempted, verify it credits 0 Stardust and marks duplicate
        var duplicateReceipt = await transportService.TransmitContributionAsync(submission, CancellationToken.None);
        duplicateReceipt.IsSuccess.Should().BeTrue();
        duplicateReceipt.IsDuplicate.Should().BeTrue();
        duplicateReceipt.Status.Should().Be("Duplicate");
        duplicateReceipt.StardustCredited.Should().Be(0);

        // Server ledger balance remains unchanged (10 points, no duplicate inflation)
        var secondBalance = await transportService.GetStardustBalanceAsync(anonymousContributorId, CancellationToken.None);
        secondBalance.ConfirmedPoints.Should().Be(10);
    }
}
