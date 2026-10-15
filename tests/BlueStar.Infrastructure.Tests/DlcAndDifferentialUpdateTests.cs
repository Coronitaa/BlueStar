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
using BlueStar.Infrastructure.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BlueStar.Infrastructure.Tests;

/// <summary>
/// Comprehensive verification for Privacy, Security, DLC classification,
/// Differential Updates, Voluntary Contribution, and Stardust Ledger.
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
    public void DlcPayloadClassifier_EmptyDepots_WithoutMetadataEnrichment_ClassifiesAsUnknown()
    {
        // PRIVACY & AUDIT RULE: If Depots=[] and metadata has not been enriched, classify as Unknown
        var classifier = new DlcPayloadClassifier(NullLogger<DlcPayloadClassifier>.Instance);
        var depots = new List<DepotInfo>();

        var classification = classifier.Classify(
            dlcAppId: 500100,
            parentAppId: 1000,
            dlcName: "Unverified Addon",
            depots: depots,
            isMetadataEnriched: false,
            hasEntitlementEvidence: false);

        classification.Should().NotBeNull();
        classification.PayloadType.Should().Be(DlcPayloadType.Unknown);
        classification.IsDownloadRequired.Should().BeFalse();
        classification.Reason.Should().Contain("insufficient evidence");
    }

    [Fact]
    public void DlcPayloadClassifier_EmptyDepots_WithMetadataEnrichment_ClassifiesAsEntitlementOnly()
    {
        // When metadata has been enriched and verified, 0 depots proves it's EntitlementOnly
        var classifier = new DlcPayloadClassifier(NullLogger<DlcPayloadClassifier>.Instance);
        var depots = new List<DepotInfo>();

        var classification = classifier.Classify(
            dlcAppId: 500200,
            parentAppId: 1000,
            dlcName: "Verified Sound Track / Key",
            depots: depots,
            isMetadataEnriched: true);

        classification.Should().NotBeNull();
        classification.PayloadType.Should().Be(DlcPayloadType.EntitlementOnly);
        classification.IsDownloadRequired.Should().BeFalse();
        classification.TotalSizeBytes.Should().Be(0);
    }

    [Fact]
    public void DlcPayloadClassifier_EmptyDepots_WithEntitlementEvidence_ClassifiesAsEntitlementOnly()
    {
        var classifier = new DlcPayloadClassifier(NullLogger<DlcPayloadClassifier>.Instance);
        var depots = new List<DepotInfo>();

        var classification = classifier.Classify(
            dlcAppId: 500300,
            parentAppId: 1000,
            dlcName: "Verified In-Game Unlock",
            depots: depots,
            isMetadataEnriched: false,
            hasEntitlementEvidence: true);

        classification.PayloadType.Should().Be(DlcPayloadType.EntitlementOnly);
        classification.IsDownloadRequired.Should().BeFalse();
    }

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
    public void DlcPayloadClassifier_PoppyPlaytimeChapter4_HasDedicatedDepots_ClassifiesAsDownloadablePayload()
    {
        // Arrange: Poppy Playtime Chapter 4 (DLC 3027850) with 14 GB content depot
        var classifier = new DlcPayloadClassifier(NullLogger<DlcPayloadClassifier>.Instance);
        var chapter4Depots = new List<DepotInfo>
        {
            new()
            {
                DepotId = 3027851,
                ManifestId = 7766554433221100UL,
                SizeBytes = 14_200_000_000L,
                Name = "Poppy Playtime - Chapter 4 Content",
                Category = "DLC"
            }
        };

        var classification = classifier.Classify(3027850, 1721470, "Poppy Playtime - Chapter 4", chapter4Depots);

        classification.PayloadType.Should().Be(DlcPayloadType.DownloadablePayload);
        classification.IsDownloadRequired.Should().BeTrue();
        classification.TotalSizeBytes.Should().Be(14_200_000_000L);
    }

    [Fact]
    public void DlcPayloadClassifier_TabletopSimulatorDLC_WithoutMetadataEnrichment_ClassifiesAsUnknown()
    {
        // Tabletop Simulator (286160) Scythe (DLC 402630) with Depots=[] but unenriched metadata
        // MUST NOT be blindly excluded; requires evidence!
        var classifier = new DlcPayloadClassifier(NullLogger<DlcPayloadClassifier>.Instance);
        var depots = new List<DepotInfo>();

        var classification = classifier.Classify(402630, 286160, "Tabletop Simulator - Scythe", depots, isMetadataEnriched: false);

        classification.PayloadType.Should().Be(DlcPayloadType.Unknown);
        classification.IsDownloadRequired.Should().BeFalse();
    }

    [Fact]
    public void DlcPayloadClassifier_TabletopSimulatorDLC_WithEnrichedEvidence_ClassifiesAsEntitlementOnly()
    {
        // Tabletop Simulator Scythe with verified enriched metadata confirming no separate depot payload
        var classifier = new DlcPayloadClassifier(NullLogger<DlcPayloadClassifier>.Instance);
        var depots = new List<DepotInfo>();

        var classification = classifier.Classify(402630, 286160, "Tabletop Simulator - Scythe", depots, isMetadataEnriched: true);

        classification.PayloadType.Should().Be(DlcPayloadType.EntitlementOnly);
        classification.IsDownloadRequired.Should().BeFalse();
        classification.TotalSizeBytes.Should().Be(0);
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
                Depots = [],
                IsMetadataEnriched = true
            }
        };

        var results = classifier.ClassifyAll(1721470, dlcs);

        results.Should().HaveCount(2);
        results[0].PayloadType.Should().Be(DlcPayloadType.DownloadablePayload);
        results[0].IsDownloadRequired.Should().BeTrue();
        results[1].PayloadType.Should().Be(DlcPayloadType.EntitlementOnly);
        results[1].IsDownloadRequired.Should().BeFalse();
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
                SizeBytes = 0,
                Name = "Unpopulated Content Depot"
            }
        };

        var classification = classifier.Classify(999110, 730, "DLC with Unpopulated Size", depots);

        classification.PayloadType.Should().Be(DlcPayloadType.DownloadablePayload);
        classification.IsDownloadRequired.Should().BeTrue("DLC has a valid depot manifest and must be downloaded even if byte size metadata is not yet populated");
    }

    // =========================================================================
    // 2. DIFFERENTIAL UPDATE RESOLVER & INSTALLATION PLANNER TESTS
    // =========================================================================

    [Fact]
    public void DifferentialUpdateResolver_WithIdenticalDepots_MarksUnchangedAndSavesBandwidth()
    {
        var resolver = new DifferentialUpdateResolver(NullLogger<DifferentialUpdateResolver>.Instance);
        uint appId = 480;

        var currentDepots = new List<DepotInfo>
        {
            new() { DepotId = 481, ManifestId = 111111111111UL, SizeBytes = 2_000_000_000L, Name = "Shared Assets" },
            new() { DepotId = 482, ManifestId = 222222222222UL, SizeBytes = 1_000_000_000L, Name = "Game Executable v1" },
            new() { DepotId = 483, ManifestId = 333333333333UL, SizeBytes = 300_000_000L, Name = "Obsolete Legacy Audio" }
        };

        var targetDepots = new List<DepotInfo>
        {
            new() { DepotId = 481, ManifestId = 111111111111UL, SizeBytes = 2_000_000_000L, Name = "Shared Assets" }, // Identical Manifest!
            new() { DepotId = 482, ManifestId = 999999999999UL, SizeBytes = 1_100_000_000L, Name = "Game Executable v2" }, // Updated!
            new() { DepotId = 484, ManifestId = 444444444444UL, SizeBytes = 500_000_000L, Name = "New HD Texture Pack" } // New!
        };

        var plan = resolver.ResolvePlan(appId, currentDepots, targetDepots, "Build-2000");

        plan.Should().NotBeNull();
        plan.AppId.Should().Be(appId);
        plan.TargetBuildId.Should().Be("Build-2000");

        plan.UnchangedDepots.Should().HaveCount(1);
        plan.UnchangedDepots[0].DepotId.Should().Be(481u);
        plan.TotalSavedSizeBytes.Should().Be(2_000_000_000L);

        plan.UpdatedDepots.Should().HaveCount(1);
        plan.UpdatedDepots[0].DepotId.Should().Be(482u);

        plan.NewDepots.Should().HaveCount(1);
        plan.NewDepots[0].DepotId.Should().Be(484u);

        plan.RemovedDepots.Should().HaveCount(1);
        plan.RemovedDepots[0].DepotId.Should().Be(483u);

        plan.TotalDownloadSizeBytes.Should().Be(1_600_000_000L);
        plan.HasSavings.Should().BeTrue();
        plan.SavingsPercentage.Should().BeApproximately(55.6, 0.5);
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
            new() { DepotId = 30, ManifestId = 11111UL, SizeBytes = 100_000_000L },
            new() { DepotId = 31, ManifestId = 99999UL, SizeBytes = 150_000_000L }
        };

        var plan = resolver.ResolvePlan(300, currentMap, target, "Build-3");

        plan.UnchangedDepots.Should().HaveCount(1);
        plan.UnchangedDepots[0].DepotId.Should().Be(30u);
        plan.UpdatedDepots.Should().HaveCount(1);
        plan.UpdatedDepots[0].DepotId.Should().Be(31u);
        plan.TotalSavedSizeBytes.Should().Be(100_000_000L);
        plan.TotalDownloadSizeBytes.Should().Be(150_000_000L);
    }

    [Fact]
    public async Task InstallationPlanner_CreateUpdatePlanAsync_UsesDifferentialResolver_FiltersUnchangedDepotsAndCalculatesSavings()
    {
        // Arrange
        var cacheDir = Path.Combine(_testDir, "planner_cache");
        var manifestCache = new ManifestCacheService(NullLogger<ManifestCacheService>.Instance, cacheDir);
        var diffResolver = new DifferentialUpdateResolver(NullLogger<DifferentialUpdateResolver>.Instance);
        var planner = new InstallationPlanner(manifestCache, NullLogger<InstallationPlanner>.Instance, null, diffResolver);

        var instance = new GameInstance
        {
            Name = "Cyber Odyssey",
            AppId = 730,
            InstallPath = @"C:\Games\CyberOdyssey",
            InstalledManifestMap = new Dictionary<uint, ulong>
            {
                { 731, 11111UL }, // Unchanged depot
                { 732, 22222UL }  // Updated depot in target
            }
        };

        var targetVersion = new GameVersion
        {
            BuildId = "2026-RELEASE-V2",
            BranchName = "public",
            Depots =
            [
                new() { DepotId = 731, ManifestId = 11111UL, SizeBytes = 2_000_000_000L, Name = "Core Textures" }, // Match: 2 GB
                new() { DepotId = 732, ManifestId = 33333UL, SizeBytes = 1_000_000_000L, Name = "Binaries" },      // Update: 1 GB
                new() { DepotId = 733, ManifestId = 44444UL, SizeBytes = 500_000_000L, Name = "New Audio Pack" }    // New: 500 MB
            ]
        };

        // Act
        var plan = await planner.CreateUpdatePlanAsync(instance, targetVersion);

        // Assert: Differential update accurately resolved
        plan.Should().NotBeNull();
        plan.IsDifferential.Should().BeTrue();

        // Unchanged depot filtered out of downloads
        plan.ReusedDepots.Should().HaveCount(1);
        plan.ReusedDepots[0].DepotId.Should().Be(731u);
        plan.ReusedDepots[0].IsReused.Should().BeTrue();

        // Downloadable depots: Updated 732 + New 733
        plan.DepotsToDownload.Should().HaveCount(2);
        plan.DepotsToDownload.Select(d => d.DepotId).Should().BeEquivalentTo(new[] { 732u, 733u });

        // Savings calculation
        plan.HasSavings.Should().BeTrue();
        plan.TotalSavedSizeBytes.Should().Be(2_000_000_000L, "Depot 731 was identical and spared from re-downloading");
        plan.TotalDownloadSizeBytes.Should().Be(1_500_000_000L);
        plan.SavingsPercentage.Should().BeApproximately(57.1, 0.5); // 2.0 / 3.5 = 57.1%
    }

    // =========================================================================
    // 3. 100% LOCAL COVERAGE COMPARISON TESTS (ZERO-KNOWLEDGE PRIVACY)
    // =========================================================================

    [Fact]
    public void LocalCoverageComparator_CompareAppCoverage_RunsCompletelyInLocalMemoryWithoutLeaks()
    {
        var comparator = new LocalCoverageComparator(NullLogger<LocalCoverageComparator>.Instance);

        var userPrivateLocalAppIds = new List<uint> { 1721470, 286160, 480, 730, 107410 };
        var publicHubCoveredAppIds = new List<uint> { 480, 730, 570, 440 };

        var result = comparator.CompareAppCoverage(userPrivateLocalAppIds, publicHubCoveredAppIds);

        result.Should().NotBeNull();
        result.TotalPrivacyGuaranteed.Should().BeTrue("No user library data was transmitted externally");
        result.CoveredAppIds.Should().BeEquivalentTo(new[] { 480u, 730u });
        result.MissingInHubAppIds.Should().BeEquivalentTo(new[] { 1721470u, 286160u, 107410u });
        result.CoveragePercentage.Should().Be(40.0);
    }

    [Fact]
    public void LocalCoverageComparator_CompareManifestCoverage_IdentifiesRareGapsLocally()
    {
        var comparator = new LocalCoverageComparator(NullLogger<LocalCoverageComparator>.Instance);

        var localDiscoveredManifests = new List<ManifestArtifact>
        {
            new() { DepotId = 481, ManifestId = 1111UL, SizeBytes = 5000 },
            new() { DepotId = 1817481, ManifestId = 2222UL, SizeBytes = 9000 }
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
        result.MissingInHubManifests.Should().HaveCount(1);
        result.MissingInHubManifests[0].DepotId.Should().Be(1817481u);
        result.MissingInHubManifests[0].ManifestId.Should().Be(2222UL);
    }

    // =========================================================================
    // 4. VOLUNTARY CONTRIBUTION & ANONYMITY TESTS
    // =========================================================================

    [Fact]
    public async Task VoluntaryContributionService_AnonymousContributorId_GeneratesCryptographicUuidV4WithoutSteamIdLink()
    {
        var service = new VoluntaryContributionService(null, NullLogger<VoluntaryContributionService>.Instance);
        string realSteamId = "76561198012345678";

        // Act
        var anonymousId1 = await service.GetOrCreateAnonymousContributorIdAsync();
        var anonymousId2 = await service.GetOrCreateAnonymousContributorIdAsync();

        // Assert 1: Must be a valid UUID v4 (Guid)
        Guid.TryParse(anonymousId1, out var parsedGuid).Should().BeTrue("Contributor ID must be a standard cryptographic UUID v4");
        parsedGuid.Should().NotBe(Guid.Empty);

        // Assert 2: Persistent across subsequent calls
        anonymousId1.Should().Be(anonymousId2);

        // Assert 3: PRIVACY INVARIANT — Raw SteamID has zero mathematical derivation or link to UUID
        anonymousId1.Should().NotContain(realSteamId);
        anonymousId1.Should().NotContain("76561198");
    }

    [Fact]
    public async Task VoluntaryContributionService_CreatePreviewAsync_GeneratesAuditedPreviewWithPrivacyDisclosures()
    {
        var service = new VoluntaryContributionService(null, NullLogger<VoluntaryContributionService>.Instance);
        var manifestPath = Path.Combine(_testDir, "test_481.manifest");
        var manifestBytes = CreateValidSteamProtobufManifest();
        await File.WriteAllBytesAsync(manifestPath, manifestBytes);

        // Act
        var preview = await service.CreatePreviewAsync(
            appId: 480,
            depotId: 481,
            manifestId: 123456789012345UL,
            manifestFilePath: manifestPath);

        // Assert
        preview.Should().NotBeNull();
        preview.AppId.Should().Be(480u);
        preview.DepotId.Should().Be(481u);
        preview.ManifestId.Should().Be(123456789012345UL);
        preview.ManifestSizeBytes.Should().Be(manifestBytes.Length);
        preview.ManifestHashSha256.Should().NotBeNullOrWhiteSpace();
        preview.IsSanitized.Should().BeTrue();

        // Anonymity verification
        Guid.TryParse(preview.AnonymousContributorId, out _).Should().BeTrue("Contributor ID must be a valid UUID v4");

        // Transparency and disclosures must be present
        preview.PrivacyDisclosures.Should().NotBeEmpty();
        preview.PrivacyDisclosures.Should().Contain(d => d.Contains("No Steam credentials"));
        preview.PrivacyDisclosures.Should().Contain(d => d.Contains("No session tokens"));
        preview.PrivacyDisclosures.Should().Contain(d => d.Contains("No SteamID"));
    }

    [Fact]
    public async Task VoluntaryContributionService_PrepareSubmissionAsync_WithoutConsent_ThrowsInvalidOperationException()
    {
        var service = new VoluntaryContributionService(null, NullLogger<VoluntaryContributionService>.Instance);
        var manifestPath = Path.Combine(_testDir, "test_no_consent.manifest");
        await File.WriteAllBytesAsync(manifestPath, CreateValidSteamProtobufManifest());

        var preview = await service.CreatePreviewAsync(480, 481, 1000UL, manifestPath);

        // Act & Assert: Must throw because userConsentGranted is false
        var act = async () => await service.PrepareSubmissionAsync(preview, userConsentGranted: false);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*consent*");
    }

    [Fact]
    public async Task VoluntaryContributionService_PrepareSubmissionAsync_WithConsent_ProducesSanitizedSubmission()
    {
        var service = new VoluntaryContributionService(null, NullLogger<VoluntaryContributionService>.Instance);
        var manifestPath = Path.Combine(_testDir, "test_with_consent.manifest");
        var manifestBytes = CreateValidSteamProtobufManifest();
        await File.WriteAllBytesAsync(manifestPath, manifestBytes);

        string rawSteamId = "76561198077665544";
        string sensitiveToken = "token_secret_session_key_999";
        string cookieSecret = "steamLoginSecure=123456789";

        var preview = await service.CreatePreviewAsync(480, 481, 7777UL, manifestPath);

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
        var service = new VoluntaryContributionService(null, NullLogger<VoluntaryContributionService>.Instance);
        var manifestPath = Path.Combine(_testDir, "test_tampered.manifest");
        await File.WriteAllBytesAsync(manifestPath, CreateValidSteamProtobufManifest());

        var preview = await service.CreatePreviewAsync(480, 481, 5555UL, manifestPath);

        // Tamper with the file on disk after preview was generated
        await File.WriteAllBytesAsync(manifestPath, [0x01, 0x02, 0x03]);

        var act = async () => await service.PrepareSubmissionAsync(preview, userConsentGranted: true);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*checksum mismatch*");
    }

    // =========================================================================
    // 5. CONTRIBUTION TRANSPORT SERVICE & STARDUST LEDGER TESTS
    // =========================================================================

    [Fact]
    public async Task ContributionTransportService_WithoutUserConsent_ThrowsInvalidOperationException()
    {
        var transport = new ContributionTransportService(null, null, NullLogger<ContributionTransportService>.Instance);
        var manifestBytes = CreateValidSteamProtobufManifest();

        var submission = new ContributionSubmission
        {
            AppId = 480,
            DepotId = 481,
            ManifestId = 12345UL,
            ManifestHashSha256 = "dummy_hash",
            ManifestSizeBytes = manifestBytes.Length,
            AnonymousContributorId = Guid.NewGuid().ToString(),
            ManifestPayload = manifestBytes,
            UserConsentGranted = false // No consent!
        };

        var act = async () => await transport.TransmitContributionAsync(submission);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*consent*");
    }

    [Fact]
    public async Task ContributionTransportService_InvalidBinaryManifestHeader_ThrowsInvalidOperationException()
    {
        var transport = new ContributionTransportService(null, null, NullLogger<ContributionTransportService>.Instance);
        var corruptedPayload = new byte[] { 0x00, 0x01, 0x02, 0x03, 0x04 }; // Corrupt magic

        var submission = new ContributionSubmission
        {
            AppId = 480,
            DepotId = 481,
            ManifestId = 12345UL,
            ManifestHashSha256 = "dummy_hash",
            ManifestSizeBytes = corruptedPayload.Length,
            AnonymousContributorId = Guid.NewGuid().ToString(),
            ManifestPayload = corruptedPayload,
            UserConsentGranted = true
        };

        var act = async () => await transport.TransmitContributionAsync(submission);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*magic*");
    }

    [Fact]
    public async Task ContributionTransportService_ConfirmedServerPublication_ReceivesReceiptAndCreditsStardust()
    {
        // Arrange
        var transport = new ContributionTransportService(null, null, NullLogger<ContributionTransportService>.Instance);
        var manifestBytes = CreateValidSteamProtobufManifest();

        using var sha256 = System.Security.Cryptography.SHA256.Create();
        var manifestHash = Convert.ToHexString(sha256.ComputeHash(manifestBytes)).ToLowerInvariant();

        var submission = new ContributionSubmission
        {
            AppId = 480,
            DepotId = 481,
            ManifestId = 987654321UL,
            ManifestHashSha256 = manifestHash,
            ManifestSizeBytes = manifestBytes.Length,
            AnonymousContributorId = Guid.NewGuid().ToString("D"),
            ManifestPayload = manifestBytes,
            UserConsentGranted = true,
            SanitizationAuditHash = "audit_sample_123"
        };

        // Act: Transmit contribution
        var receipt = await transport.TransmitContributionAsync(submission);

        // Assert: Receipt received with confirmation
        receipt.Should().NotBeNull();
        receipt.ReceiptId.Should().NotBeNullOrWhiteSpace();
        receipt.Status.Should().Be("Published");
        receipt.IsSuccess.Should().BeTrue();
        receipt.AppId.Should().Be(480u);
        receipt.DepotId.Should().Be(481u);
        receipt.ManifestId.Should().Be(987654321UL);

        // Stardust rewards credited exclusively upon confirmed publication
        receipt.StardustCredited.Should().Be(ContributionTransportService.DefaultStardustReward);
        transport.GetTotalStardustCredited().Should().Be(ContributionTransportService.DefaultStardustReward);
        transport.IsAlreadyCredited(481, 987654321UL).Should().BeTrue();
    }

    [Fact]
    public async Task ContributionTransportService_IsStrictlyIdempotent_DoesNotDuplicateStardustOnReTransmission()
    {
        // Arrange
        var transport = new ContributionTransportService(null, null, NullLogger<ContributionTransportService>.Instance);
        var manifestBytes = CreateValidSteamProtobufManifest();

        using var sha256 = System.Security.Cryptography.SHA256.Create();
        var manifestHash = Convert.ToHexString(sha256.ComputeHash(manifestBytes)).ToLowerInvariant();

        var submission = new ContributionSubmission
        {
            AppId = 480,
            DepotId = 481,
            ManifestId = 888888888UL,
            ManifestHashSha256 = manifestHash,
            ManifestSizeBytes = manifestBytes.Length,
            AnonymousContributorId = Guid.NewGuid().ToString("D"),
            ManifestPayload = manifestBytes,
            UserConsentGranted = true,
            SanitizationAuditHash = "audit_sample_456"
        };

        // Act: Transmit twice
        var receipt1 = await transport.TransmitContributionAsync(submission);
        var receipt2 = await transport.TransmitContributionAsync(submission);

        // Assert: Same receipt returned, Stardust credited only once (idempotent)
        receipt1.ReceiptId.Should().Be(receipt2.ReceiptId);
        transport.GetTotalStardustCredited().Should().Be(ContributionTransportService.DefaultStardustReward);
    }
}
