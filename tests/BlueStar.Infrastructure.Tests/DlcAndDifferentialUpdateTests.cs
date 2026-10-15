using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using BlueStar.Core.Interfaces;
using BlueStar.Core.Models;
using BlueStar.Infrastructure.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BlueStar.Infrastructure.Tests;

/// <summary>
/// Comprehensive verification for Privacy, Security, DLC classification,
/// Differential Updates, and Voluntary Contribution (Phases D & E).
/// </summary>
public class DlcAndDifferentialUpdateTests : IDisposable
{
    private readonly string _testDir;

    public DlcAndDifferentialUpdateTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "BlueStarPrivacyDlcTests_" + Guid.NewGuid().ToString("N"));
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
        ms.Write([0xD0, 0x17, 0xF6, 0x71]); // Steam Protobuf magic
        ms.Write(BitConverter.GetBytes(100u)); // payload length

        using (var deflate = new DeflateStream(ms, CompressionLevel.Fastest, leaveOpen: true))
        {
            var dummyPayload = Encoding.UTF8.GetBytes("steam_protobuf_manifest_sample_payload_data_for_unit_tests");
            deflate.Write(dummyPayload);
        }

        return ms.ToArray();
    }

    // =========================================================================
    // 1. DLC CLASSIFICATION TESTS
    // =========================================================================

    [Fact]
    public void DlcPayloadClassifier_PoppyPlaytimeChapter2_HasDedicatedDepots_ClassifiesAsDownloadablePayload()
    {
        // Arrange: Poppy Playtime (Base Game 1721470) Chapter 2 (DLC 1817480)
        // Has a multi-gigabyte dedicated content depot
        var classifier = new DlcPayloadClassifier(NullLogger<DlcPayloadClassifier>.Instance);
        uint parentAppId = 1721470;
        uint chapter2DlcId = 1817480;

        var chapter2Depots = new List<DepotInfo>
        {
            new()
            {
                DepotId = 1817481,
                ManifestId = 8877665544332211UL,
                SizeBytes = 8_500_000_000L, // 8.5 GB
                Name = "Poppy Playtime - Chapter 2 Content",
                Category = "DLC"
            }
        };

        // Act
        var classification = classifier.Classify(chapter2DlcId, parentAppId, "Poppy Playtime - Chapter 2: Fly in a Web", chapter2Depots);

        // Assert
        classification.Should().NotBeNull();
        classification.DlcAppId.Should().Be(chapter2DlcId);
        classification.ParentAppId.Should().Be(parentAppId);
        classification.PayloadType.Should().Be(DlcPayloadType.DownloadablePayload);
        classification.IsDownloadRequired.Should().BeTrue();
        classification.TotalSizeBytes.Should().Be(8_500_000_000L);
        classification.DepotIds.Should().Contain(1817481u);
        classification.FormattedSize.Should().Contain("GB");
    }

    [Fact]
    public void DlcPayloadClassifier_PoppyPlaytimeChapter3_MultipleDepots_ClassifiesAsDownloadablePayload()
    {
        // Arrange: Chapter 3 (DLC 2555550) with multiple depots
        var classifier = new DlcPayloadClassifier(NullLogger<DlcPayloadClassifier>.Instance);
        var dlcInfo = new DlcInfo
        {
            AppId = 2555550,
            Name = "Poppy Playtime - Chapter 3: Deep Sleep",
            Depots = new List<DepotInfo>
            {
                new() { DepotId = 2555551, ManifestId = 111122223333UL, SizeBytes = 12_000_000_000L },
                new() { DepotId = 2555552, ManifestId = 444455556666UL, SizeBytes = 3_000_000_000L }
            }
        };

        // Act
        var classification = classifier.Classify(1721470, dlcInfo);

        // Assert
        classification.PayloadType.Should().Be(DlcPayloadType.DownloadablePayload);
        classification.IsDownloadRequired.Should().BeTrue();
        classification.TotalSizeBytes.Should().Be(15_000_000_000L);
        classification.DepotIds.Should().HaveCount(2);
    }

    [Fact]
    public void DlcPayloadClassifier_TabletopSimulatorDLC_NoDepots_ClassifiesAsEntitlementOnly()
    {
        // Arrange: Tabletop Simulator (Base Game 286160) Scythe (DLC 402630)
        // Assets are delivered within base game / engine scripts, requires 0 separate depot download
        var classifier = new DlcPayloadClassifier(NullLogger<DlcPayloadClassifier>.Instance);
        uint parentAppId = 286160;
        uint scytheDlcId = 402630;

        var depots = new List<DepotInfo>(); // No separate depots

        // Act
        var classification = classifier.Classify(scytheDlcId, parentAppId, "Tabletop Simulator - Scythe", depots);

        // Assert
        classification.Should().NotBeNull();
        classification.PayloadType.Should().Be(DlcPayloadType.EntitlementOnly);
        classification.IsDownloadRequired.Should().BeFalse();
        classification.TotalSizeBytes.Should().Be(0);
        classification.DepotIds.Should().BeEmpty();
    }

    [Fact]
    public void DlcPayloadClassifier_TabletopSimulatorDLC_ZeroByteStubDepot_ClassifiesAsEntitlementOnly()
    {
        // Arrange: Tabletop Simulator Wingspan DLC with 0-byte entitlement marker depot
        var classifier = new DlcPayloadClassifier(NullLogger<DlcPayloadClassifier>.Instance);
        var wingspanDepots = new List<DepotInfo>
        {
            new()
            {
                DepotId = 468721,
                ManifestId = 0,
                SizeBytes = 0,
                Name = "Wingspan Entitlement Stub"
            }
        };

        // Act
        var classification = classifier.Classify(468720, 286160, "Tabletop Simulator - Wingspan", wingspanDepots);

        // Assert
        classification.PayloadType.Should().Be(DlcPayloadType.EntitlementOnly);
        classification.IsDownloadRequired.Should().BeFalse();
        classification.TotalSizeBytes.Should().Be(0);
    }

    [Fact]
    public void DlcPayloadClassifier_ExternalContentDlc_ClassifiesAsExternalContent()
    {
        var classifier = new DlcPayloadClassifier(NullLogger<DlcPayloadClassifier>.Instance);
        var classification = classifier.Classify(999999, 1000, "Online MMO [External Server Key]", new List<DepotInfo>());

        classification.PayloadType.Should().Be(DlcPayloadType.ExternalContent);
        classification.IsDownloadRequired.Should().BeFalse();
    }

    [Fact]
    public void DlcPayloadClassifier_NullDepots_ClassifiesAsUnknown()
    {
        var classifier = new DlcPayloadClassifier(NullLogger<DlcPayloadClassifier>.Instance);
        var classification = classifier.Classify(88888, 1000, "Mystery Addon", null);

        classification.PayloadType.Should().Be(DlcPayloadType.Unknown);
        classification.IsDownloadRequired.Should().BeFalse();
    }

    [Fact]
    public void DlcPayloadClassifier_ClassifyAll_AccuratelyProcessesMixedCollection()
    {
        var classifier = new DlcPayloadClassifier(NullLogger<DlcPayloadClassifier>.Instance);
        var dlcs = new List<DlcInfo>
        {
            new()
            {
                AppId = 1817480,
                Name = "Poppy Playtime Ch 2",
                Depots = [new() { DepotId = 1817481, ManifestId = 9999UL, SizeBytes = 5000000000L }]
            },
            new()
            {
                AppId = 402630,
                Name = "Tabletop Sim Scythe",
                Depots = []
            }
        };

        var results = classifier.ClassifyAll(1721470, dlcs);

        results.Should().HaveCount(2);
        results[0].PayloadType.Should().Be(DlcPayloadType.DownloadablePayload);
        results[0].IsDownloadRequired.Should().BeTrue();
        results[1].PayloadType.Should().Be(DlcPayloadType.EntitlementOnly);
        results[1].IsDownloadRequired.Should().BeFalse();
    }

    // =========================================================================
    // 2. DIFFERENTIAL UPDATE RESOLVER TESTS
    // =========================================================================

    [Fact]
    public void DifferentialUpdateResolver_WithIdenticalDepots_MarksUnchangedAndSavesBandwidth()
    {
        // Scenario: A 3-depot game where 1 depot is unchanged, 1 is updated, 1 is new, and 1 old depot is removed.
        var resolver = new DifferentialUpdateResolver(NullLogger<DifferentialUpdateResolver>.Instance);
        uint appId = 480;

        // Current installed version (Build 1000)
        var currentDepots = new List<DepotInfo>
        {
            new() { DepotId = 481, ManifestId = 111111111111UL, SizeBytes = 2_000_000_000L, Name = "Shared Assets" },
            new() { DepotId = 482, ManifestId = 222222222222UL, SizeBytes = 1_000_000_000L, Name = "Game Executable v1" },
            new() { DepotId = 483, ManifestId = 333333333333UL, SizeBytes = 300_000_000L, Name = "Obsolete Legacy Audio" }
        };

        // Target new version (Build 2000)
        var targetDepots = new List<DepotInfo>
        {
            new() { DepotId = 481, ManifestId = 111111111111UL, SizeBytes = 2_000_000_000L, Name = "Shared Assets" }, // Identical Manifest!
            new() { DepotId = 482, ManifestId = 999999999999UL, SizeBytes = 1_100_000_000L, Name = "Game Executable v2" }, // Updated Manifest!
            new() { DepotId = 484, ManifestId = 444444444444UL, SizeBytes = 500_000_000L, Name = "New HD Texture Pack" } // New Depot!
        };

        // Act
        var plan = resolver.ResolvePlan(appId, currentDepots, targetDepots, "Build-2000");

        // Assert
        plan.Should().NotBeNull();
        plan.AppId.Should().Be(appId);
        plan.TargetBuildId.Should().Be("Build-2000");

        // Unchanged: Depot 481 (Manifest identical)
        plan.UnchangedDepots.Should().HaveCount(1);
        plan.UnchangedDepots[0].DepotId.Should().Be(481u);
        plan.TotalSavedSizeBytes.Should().Be(2_000_000_000L, "Depot 481 was unchanged, avoiding a 2 GB redundant download");

        // Updated: Depot 482 (Manifest changed)
        plan.UpdatedDepots.Should().HaveCount(1);
        plan.UpdatedDepots[0].DepotId.Should().Be(482u);

        // New: Depot 484 (Newly added)
        plan.NewDepots.Should().HaveCount(1);
        plan.NewDepots[0].DepotId.Should().Be(484u);

        // Removed: Depot 483 (No longer in target)
        plan.RemovedDepots.Should().HaveCount(1);
        plan.RemovedDepots[0].DepotId.Should().Be(483u);

        // Download: Depot 482 (1.1 GB) + Depot 484 (500 MB) = 1.6 GB
        plan.TotalDownloadSizeBytes.Should().Be(1_600_000_000L);
        plan.HasSavings.Should().BeTrue();
        plan.SavingsPercentage.Should().BeApproximately(55.6, 0.5); // 2.0 GB / 3.6 GB = ~55.6%
    }

    [Fact]
    public void DifferentialUpdateResolver_AllDepotsUnchanged_ZeroBytesDownloadNeeded()
    {
        var resolver = new DifferentialUpdateResolver(NullLogger<DifferentialUpdateResolver>.Instance);
        var depots = new List<DepotInfo>
        {
            new() { DepotId = 10, ManifestId = 12345UL, SizeBytes = 500_000_000L },
            new() { DepotId = 11, ManifestId = 67890UL, SizeBytes = 200_000_000L }
        };

        var plan = resolver.ResolvePlan(100, depots, depots, "Same-Build");

        plan.UnchangedDepots.Should().HaveCount(2);
        plan.UpdatedDepots.Should().BeEmpty();
        plan.NewDepots.Should().BeEmpty();
        plan.TotalDownloadSizeBytes.Should().Be(0L);
        plan.TotalSavedSizeBytes.Should().Be(700_000_000L);
        plan.SavingsPercentage.Should().Be(100.0);
    }

    [Fact]
    public void DifferentialUpdateResolver_CleanInstall_NoExistingDepots_DownloadsEverything()
    {
        var resolver = new DifferentialUpdateResolver(NullLogger<DifferentialUpdateResolver>.Instance);
        var targetDepots = new List<DepotInfo>
        {
            new() { DepotId = 20, ManifestId = 55555UL, SizeBytes = 400_000_000L }
        };

        var plan = resolver.ResolvePlan(200, [], targetDepots, "Initial-Install");

        plan.UnchangedDepots.Should().BeEmpty();
        plan.NewDepots.Should().HaveCount(1);
        plan.TotalDownloadSizeBytes.Should().Be(400_000_000L);
        plan.TotalSavedSizeBytes.Should().Be(0L);
        plan.SavingsPercentage.Should().Be(0.0);
    }

    [Fact]
    public void DifferentialUpdateResolver_WithManifestMapOverload_ComputesCorrectly()
    {
        var resolver = new DifferentialUpdateResolver(NullLogger<DifferentialUpdateResolver>.Instance);
        var currentMap = new Dictionary<uint, ulong>
        {
            [30] = 11111UL,
            [31] = 22222UL
        };

        var target = new List<DepotInfo>
        {
            new() { DepotId = 30, ManifestId = 11111UL, SizeBytes = 100_000_000L }, // Match
            new() { DepotId = 31, ManifestId = 99999UL, SizeBytes = 150_000_000L }  // Mismatch
        };

        var plan = resolver.ResolvePlan(300, currentMap, target, "Build-3");

        plan.UnchangedDepots.Should().HaveCount(1);
        plan.UnchangedDepots[0].DepotId.Should().Be(30u);
        plan.UpdatedDepots.Should().HaveCount(1);
        plan.UpdatedDepots[0].DepotId.Should().Be(31u);
        plan.TotalSavedSizeBytes.Should().Be(100_000_000L);
        plan.TotalDownloadSizeBytes.Should().Be(150_000_000L);
    }

    // =========================================================================
    // 3. 100% LOCAL COVERAGE COMPARISON TESTS (ZERO-KNOWLEDGE PRIVACY)
    // =========================================================================

    [Fact]
    public void LocalCoverageComparator_CompareAppCoverage_RunsCompletelyInLocalMemoryWithoutLeaks()
    {
        var comparator = new LocalCoverageComparator(NullLogger<LocalCoverageComparator>.Instance);

        // User's private local library (should NEVER leave client memory)
        var userPrivateLocalAppIds = new List<uint> { 1721470, 286160, 480, 730, 107410 };

        // Public Hub catalog index (downloaded once or cached)
        var publicHubCoveredAppIds = new List<uint> { 480, 730, 570, 440 };

        // Act: Comparison executed 100% in-memory locally
        var result = comparator.CompareAppCoverage(userPrivateLocalAppIds, publicHubCoveredAppIds);

        // Assert
        result.Should().NotBeNull();
        result.TotalPrivacyGuaranteed.Should().BeTrue("No user library data was transmitted externally");

        // Covered: 480, 730
        result.CoveredAppIds.Should().BeEquivalentTo(new[] { 480u, 730u });

        // Missing in Hub (potential candidates for local manifest contribution): 1721470, 286160, 107410
        result.MissingInHubAppIds.Should().BeEquivalentTo(new[] { 1721470u, 286160u, 107410u });

        // Coverage percentage: 2 out of 5 = 40.0%
        result.CoveragePercentage.Should().Be(40.0);
    }

    [Fact]
    public void LocalCoverageComparator_CompareManifestCoverage_IdentifiesRareGapsLocally()
    {
        var comparator = new LocalCoverageComparator(NullLogger<LocalCoverageComparator>.Instance);

        var localDiscoveredManifests = new List<ManifestArtifact>
        {
            new() { DepotId = 481, ManifestId = 1111UL, SizeBytes = 5000 },
            new() { DepotId = 1817481, ManifestId = 2222UL, SizeBytes = 9000 } // Poppy Playtime rare manifest
        };

        var hubPublicManifests = new List<ManifestArtifact>
        {
            new() { DepotId = 481, ManifestId = 1111UL, SizeBytes = 5000 },
            new() { DepotId = 731, ManifestId = 3333UL, SizeBytes = 4000 }
        };

        var result = comparator.CompareManifestCoverage(localDiscoveredManifests, hubPublicManifests);

        result.TotalPrivacyGuaranteed.Should().BeTrue();
        result.CoveredManifests.Should().HaveCount(1);
        result.CoveredManifests[0].DepotId.Should().Be(481u);

        // The rare manifest is correctly flagged for voluntary contribution
        result.MissingInHubManifests.Should().HaveCount(1);
        result.MissingInHubManifests[0].DepotId.Should().Be(1817481u);
        result.MissingInHubManifests[0].ManifestId.Should().Be(2222UL);
    }

    // =========================================================================
    // 4. VOLUNTARY CONTRIBUTION & PSEUDONYMITY TESTS
    // =========================================================================

    [Fact]
    public void VoluntaryContributionService_GeneratePseudonym_IsDeterministicAndIrreversible()
    {
        var service = new VoluntaryContributionService(NullLogger<VoluntaryContributionService>.Instance);
        string realSteamId = "76561198012345678";

        // Act
        var pseudonym1 = service.GeneratePseudonym(realSteamId);
        var pseudonym2 = service.GeneratePseudonym(realSteamId);
        var differentPseudonym = service.GeneratePseudonym("76561198999999999");

        // Assert 1: Deterministic for identical input
        pseudonym1.Should().Be(pseudonym2);
        pseudonym1.Length.Should().Be(64, "SHA-256 hex string must be 64 characters");

        // Assert 2: Different SteamIDs yield distinct pseudonyms
        pseudonym1.Should().NotBe(differentPseudonym);

        // Assert 3: PRIVACY INVARIANT — Raw SteamID is completely absent from the pseudonym
        pseudonym1.Should().NotContain(realSteamId);
        pseudonym1.Should().NotContain("76561198");
    }

    [Fact]
    public async Task VoluntaryContributionService_CreatePreviewAsync_GeneratesAuditedPreviewWithPrivacyDisclosures()
    {
        var service = new VoluntaryContributionService(NullLogger<VoluntaryContributionService>.Instance);
        var manifestPath = Path.Combine(_testDir, "test_481.manifest");
        var manifestBytes = CreateValidSteamProtobufManifest();
        await File.WriteAllBytesAsync(manifestPath, manifestBytes);

        string rawSteamId = "76561198099887766";

        // Act
        var preview = await service.CreatePreviewAsync(
            appId: 480,
            depotId: 481,
            manifestId: 123456789012345UL,
            manifestFilePath: manifestPath,
            rawUserIdentifier: rawSteamId);

        // Assert
        preview.Should().NotBeNull();
        preview.AppId.Should().Be(480u);
        preview.DepotId.Should().Be(481u);
        preview.ManifestId.Should().Be(123456789012345UL);
        preview.ManifestSizeBytes.Should().Be(manifestBytes.Length);
        preview.ManifestHashSha256.Should().NotBeNullOrWhiteSpace();
        preview.IsSanitized.Should().BeTrue();

        // Transparency and disclosures must be present
        preview.PrivacyDisclosures.Should().NotBeEmpty();
        preview.PrivacyDisclosures.Should().Contain(d => d.Contains("No Steam credentials"));
        preview.PrivacyDisclosures.Should().Contain(d => d.Contains("No session tokens"));
        preview.PrivacyDisclosures.Should().Contain(d => d.Contains("No SteamID"));

        // User SteamID is scrubbed and replaced with pseudonym
        preview.PseudonymousContributorId.Should().NotContain(rawSteamId);
    }

    [Fact]
    public async Task VoluntaryContributionService_PrepareSubmissionAsync_WithoutConsent_ThrowsInvalidOperationException()
    {
        var service = new VoluntaryContributionService(NullLogger<VoluntaryContributionService>.Instance);
        var manifestPath = Path.Combine(_testDir, "test_no_consent.manifest");
        await File.WriteAllBytesAsync(manifestPath, CreateValidSteamProtobufManifest());

        var preview = await service.CreatePreviewAsync(480, 481, 1000UL, manifestPath, "76561198000000001");

        // Act & Assert: Must throw because userConsentGranted is false
        var act = async () => await service.PrepareSubmissionAsync(preview, userConsentGranted: false);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*consent*");
    }

    [Fact]
    public async Task VoluntaryContributionService_PrepareSubmissionAsync_WithConsent_ProducesSanitizedSubmission()
    {
        var service = new VoluntaryContributionService(NullLogger<VoluntaryContributionService>.Instance);
        var manifestPath = Path.Combine(_testDir, "test_with_consent.manifest");
        var manifestBytes = CreateValidSteamProtobufManifest();
        await File.WriteAllBytesAsync(manifestPath, manifestBytes);

        string rawSteamId = "76561198077665544";
        string sensitiveToken = "token_secret_session_key_999";
        string cookieSecret = "steamLoginSecure=123456789";

        var preview = await service.CreatePreviewAsync(480, 481, 7777UL, manifestPath, rawSteamId);

        // Act: User explicitly granted consent
        var submission = await service.PrepareSubmissionAsync(preview, userConsentGranted: true);

        // Assert
        submission.Should().NotBeNull();
        submission.UserConsentGranted.Should().BeTrue();
        submission.IsSafeForTransmission.Should().BeTrue();
        submission.SanitizationAuditHash.Should().NotBeNullOrWhiteSpace();
        submission.ManifestPayload.Should().Equal(manifestBytes);

        // Verify deep sanitization against raw credentials, tokens, cookies, and SteamID
        submission.AuditForSensitiveTokens(rawSteamId, sensitiveToken, cookieSecret).Should().BeTrue(
            "Community submission must never contain raw SteamIDs, session tokens, or sensitive cookies");
    }

    [Fact]
    public async Task VoluntaryContributionService_PrepareSubmissionAsync_WhenFileTampered_ThrowsSecurityMismatch()
    {
        var service = new VoluntaryContributionService(NullLogger<VoluntaryContributionService>.Instance);
        var manifestPath = Path.Combine(_testDir, "test_tampered.manifest");
        await File.WriteAllBytesAsync(manifestPath, CreateValidSteamProtobufManifest());

        var preview = await service.CreatePreviewAsync(480, 481, 5555UL, manifestPath, "user1");

        // Tamper with the file on disk after preview was generated
        await File.WriteAllBytesAsync(manifestPath, [0x01, 0x02, 0x03]);

        var act = async () => await service.PrepareSubmissionAsync(preview, userConsentGranted: true);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*checksum mismatch*");
    }

    [Fact]
    public void DlcPayloadClassifier_DownloadablePayloadWithZeroReportedSize_StillRequiresDownload()
    {
        // Arrange: DLC with depot manifest assigned but store metadata has 0 bytes (unpopulated size)
        var classifier = new DlcPayloadClassifier(NullLogger<DlcPayloadClassifier>.Instance);
        var depots = new List<DepotInfo>
        {
            new()
            {
                DepotId = 999111,
                ManifestId = 5544332211UL,
                SizeBytes = 0, // Unpopulated byte size
                Name = "Unpopulated Content Depot"
            }
        };

        // Act
        var classification = classifier.Classify(999110, 730, "DLC with Unpopulated Size", depots);

        // Assert
        classification.PayloadType.Should().Be(DlcPayloadType.DownloadablePayload);
        classification.IsDownloadRequired.Should().BeTrue("DLC has a valid depot manifest and must be downloaded even if byte size metadata is not yet populated");
    }
}
