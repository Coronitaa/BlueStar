using System.Net;
using System.Security.Cryptography;
using System.Text;
using BlueStar.Core.Interfaces;
using BlueStar.Core.Models;
using BlueStar.Infrastructure.Admin;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace BlueStar.Infrastructure.Tests;

public sealed class AdminCatalogTests : IDisposable
{
    private static readonly string Sha = new('a', 64);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bs-admin-" + Guid.NewGuid().ToString("N"));

    public AdminCatalogTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private const string ValidCatalog = """
    {
      "schemaVersion": 1,
      "updatedAt": "2026-11-17T12:00:00Z",
      "games": [
        {
          "appId": 4465480,
          "name": "Counter-Strike: Global Offensive",
          "headerImageUrl": "https://shared.akamai.steamstatic.com/store_item_assets/steam/apps/4465480/a7ec/header.jpg",
          "heroImageUrl": "https://evil.example/hero.jpg",
          "tagIds": [19, 1663, 19, -4, "x"],
          "releaseDateUtc": 1345568400,
          "developer": "Valve",
          "presets": [
            {
              "id": "csgo-final",
              "name": "Last CS:GO build",
              "branch": "public",
              "buildId": "17592934",
              "manifests": { "4465481": "9007199254740993123" },
              "emulator": { "type": "refix_online_legacy" },
              "mods": [ { "name": "Pack", "url": "https://github.com/Coronitaa/packs/releases/download/v1/pack.zip" } ],
              "note": "Works offline.\nLAN tested."
            },
            {
              "id": "csgo-custom",
              "name": "Custom emulator",
              "emulator": {
                "type": "custom", "name": "MyEmu",
                "url": "https://example.com/emu.zip", "sha256": "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA",
                "settingsFile": "steam_settings/configs.user.ini",
                "settings": { "account_name": "Player" }
              }
            }
          ]
        },
        { "appId": 730, "hidden": true }
      ]
    }
    """;

    private static string WithPreset(string presetJson) =>
        $$"""{ "schemaVersion": 1, "games": [ { "appId": 10, "name": "G", "presets": [ {{presetJson}} ] } ] }""";

    [Fact]
    public void Parse_ValidCatalog_KeepsGamesPresetsAndBigManifestIds()
    {
        var result = AdminCatalogParser.Parse(ValidCatalog);

        result.Errors.Should().ContainSingle(e => e.Contains("heroImageUrl"), "a non-image host is dropped and reported");
        result.Catalog.Games.Should().HaveCount(2);
        var csgo = result.Catalog.Games[0];
        csgo.TagIds.Should().Equal(19, 1663);
        csgo.HeroImageUrl.Should().BeNull();
        csgo.HeaderImageUrl.Should().Contain("/a7ec/header.jpg");
        csgo.Developer.Should().Be("Valve");
        csgo.ToCatalogItem().TagIds.Should().Equal(19, 1663);
        csgo.ToCatalogItem().ReleaseDateUtc.Should().Be(1345568400);
        csgo.Presets.Should().HaveCount(2);
        csgo.Presets[0].Manifests[4465481].Should().Be(9007199254740993123UL);
        csgo.Presets[0].Note.Should().Contain("\n");
        csgo.Presets[1].Emulator.Type.Should().Be(AdminEmulatorType.Custom);
        csgo.Presets[1].Emulator.Download!.Sha256.Should().Be(Sha, "checksums are normalised to lowercase");
        result.Catalog.Games[1].Hidden.Should().BeTrue("a hidden entry needs no name");
    }

    [Theory]
    [InlineData("""{ "schemaVersion": 2, "games": [] }""")]
    [InlineData("""{ "games": [] }""")]
    [InlineData("""[1,2]""")]
    [InlineData("""not json""")]
    public void Parse_UnsupportedOrBrokenRoot_IsEmpty(string json)
    {
        AdminCatalogParser.Parse(json).Catalog.Should().BeSameAs(AdminCatalog.Empty);
    }

    [Theory]
    [InlineData("""{ "id": "p", "name": "P", "mods": [ { "name": "m", "url": "http://github.com/Coronitaa/x/a.zip" } ] }""")]
    [InlineData("""{ "id": "p", "name": "P", "mods": [ { "name": "m", "url": "https://evil.example/a.zip" } ] }""")]
    [InlineData("""{ "id": "p", "name": "P", "mods": [ { "name": "m", "url": "https://github.com/SomeoneElse/x/a.zip" } ] }""")]
    [InlineData("""{ "id": "p", "name": "P", "mods": [ { "name": "m", "url": "https://user:pw@github.com/Coronitaa/a.zip" } ] }""")]
    [InlineData("""{ "id": "p", "name": "P", "mods": [ { "name": "m", "url": "https://evil.example/a.zip", "sha256": "xyz" } ] }""")]
    [InlineData("""{ "id": "p", "name": "P", "emulator": { "type": "custom", "name": "E", "url": "https://evil.example/e.zip" } }""")]
    [InlineData("""{ "id": "p", "name": "P", "emulator": { "type": "launcher.exe" } }""")]
    [InlineData("""{ "id": "P!", "name": "P" }""")]
    [InlineData("""{ "id": "p", "name": "P", "manifests": { "1": 123 } }""")]
    [InlineData("""{ "id": "p", "name": "P", "branch": "a b" }""")]
    public void Parse_UnsafePreset_IsDropped(string preset)
    {
        var result = AdminCatalogParser.Parse(WithPreset(preset));

        result.Catalog.Games.Single().Presets.Should().BeEmpty();
        result.Errors.Should().NotBeEmpty();
    }

    [Fact]
    public void Parse_UntrustedHostWithChecksum_IsKept()
    {
        var result = AdminCatalogParser.Parse(WithPreset(
            $$"""{ "id": "p", "name": "P", "mods": [ { "name": "m", "url": "https://cdn.example/a.zip", "sha256": "{{Sha}}" } ] }"""));

        result.Catalog.Games.Single().Presets.Single().Mods.Single().Sha256.Should().Be(Sha);
    }

    [Theory]
    [InlineData("../evil.ini")]
    [InlineData("a/../../evil.ini")]
    [InlineData("C:/Windows/evil.ini")]
    [InlineData("/etc/evil.ini")]
    [InlineData("\\\\server\\share\\evil.ini")]
    [InlineData("cfg.ini:stream")]
    [InlineData("evil.exe")]
    public void Parse_SettingsFileOutsideGameFolder_IsRejected(string settingsFile)
    {
        var preset = $$"""
        { "id": "p", "name": "P", "emulator": { "type": "custom", "name": "E", "url": "https://github.com/Coronitaa/e/e.zip",
          "settingsFile": {{System.Text.Json.JsonSerializer.Serialize(settingsFile)}}, "settings": { "k": "v" } } }
        """;
        AdminCatalogParser.Parse(WithPreset(preset)).Catalog.Games.Single().Presets.Should().BeEmpty();
    }

    [Fact]
    public void Parse_SettingValueWithNewline_IsRejected()
    {
        var preset = """
        { "id": "p", "name": "P", "emulator": { "type": "custom", "name": "E", "url": "https://github.com/Coronitaa/e/e.zip",
          "settingsFile": "a.ini", "settings": { "k": "v\n[evil]" } } }
        """;
        AdminCatalogParser.Parse(WithPreset(preset)).Catalog.Games.Single().Presets.Should().BeEmpty();
    }

    [Fact]
    public void Parse_DuplicatesNamelessVisibleGamesAndBadImages_AreHandled()
    {
        var json = """
        { "schemaVersion": 1, "games": [
          { "appId": 1, "name": "A", "headerImageUrl": "https://tracker.example/pixel.png" },
          { "appId": 1, "name": "A again" },
          { "appId": 2 },
          { "appId": 0, "name": "zero" },
          { "appId": 3, "name": "C\u0000\u0007", "headerImageUrl": "https://shared.cloudflare.steamstatic.com/x/header.jpg" }
        ] }
        """;
        var games = AdminCatalogParser.Parse(json).Catalog.Games;

        games.Select(g => g.AppId).Should().Equal(1u, 3u);
        games[0].Name.Should().Be("A");
        games[0].HeaderImageUrl.Should().BeNull();
        games[1].Name.Should().Be("C");
        games[1].HeaderImageUrl.Should().StartWith("https://shared.cloudflare.steamstatic.com/");
    }

    [Fact]
    public void Parse_DepotKeys_KeepsValidHexAndDropsTheRest()
    {
        var key = new string('A', 64);
        var json = $$"""{ "schemaVersion": 1, "games": [ { "appId": 9, "name": "G", "depotKeys": { "731": "{{key}}", "732": "nothex", "x": "{{key}}" } } ] }""";

        var result = AdminCatalogParser.Parse(json);

        result.Catalog.Games.Single().DepotKeys.Should().ContainSingle().Which.Should().Be(new KeyValuePair<uint, string>(731, new string('a', 64)));
        result.Errors.Should().HaveCount(2);
    }

    [Theory]
    [InlineData("<!DOCTYPE html><html><head><title>DepotBox is Permanently Closed: Here Is the Truth</title><style>x</style>", "DepotBox is Permanently Closed: Here Is the Truth")]
    [InlineData("{\"error\":\"bad key\"}", "{\"error\":\"bad key\"}")]
    [InlineData("", "(empty response)")]
    public void DepotBoxErrors_AreSummarized(string body, string expected)
    {
        BlueStar.Infrastructure.DepotBox.DepotBoxApiClient.SummarizeBody(body).Should().Be(expected);
    }

    [Fact]
    public void ToRecommendation_MapsEmulatorAndExtras()
    {
        var presets = AdminCatalogParser.Parse(ValidCatalog).Catalog.Games[0].Presets;

        var refix = presets[0].ToRecommendation(4465480);
        refix.RecommendedEmulator.Should().Be("refix_valve");
        refix.RecommendedBuildId.Should().Be("17592934");
        refix.PinnedManifests.Should().ContainKey(4465481);
        refix.CompatibilityNotes.Should().Contain("LAN");
        refix.HasPresetExtras.Should().BeTrue("it has a mod");

        var custom = presets[1].ToRecommendation(4465480);
        custom.RecommendedEmulator.Should().Be("none", "BlueStar's own emulator must not go on top of a custom one");
        custom.HasPresetExtras.Should().BeTrue();
        custom.PresetId.Should().Be("csgo-custom");
    }

    [Fact]
    public void Parse_ReFixMods_KeepsIdsAndTellsAutoFromNone()
    {
        var picked = AdminCatalogParser.Parse(WithPreset("""{ "id": "p", "name": "P", "refixMods": ["refix.csgo.lan_overhaul", "refix.csgo.lan_overhaul"] }"""));
        var none = AdminCatalogParser.Parse(WithPreset("""{ "id": "p", "name": "P", "refixMods": [] }"""));
        var auto = AdminCatalogParser.Parse(WithPreset("""{ "id": "p", "name": "P" }"""));

        picked.Catalog.Games.Single().Presets.Single().ReFixMods.Should().Equal("refix.csgo.lan_overhaul");
        picked.Catalog.Games.Single().Presets.Single().ToRecommendation(10).ReFixModIds.Should().Equal("refix.csgo.lan_overhaul");
        none.Catalog.Games.Single().Presets.Single().ReFixMods.Should().BeEmpty("an empty list means no ReFix mods");
        auto.Catalog.Games.Single().Presets.Single().ReFixMods.Should().BeNull("no list leaves the choice to ReFix's catalog");
    }

    [Theory]
    [InlineData("""{ "id": "p", "name": "P", "refixMods": ["Bad Id"] }""")]
    [InlineData("""{ "id": "p", "name": "P", "refixMods": ["a,b"] }""")]
    [InlineData("""{ "id": "p", "name": "P", "refixMods": [1] }""")]
    [InlineData("""{ "id": "p", "name": "P", "refixMods": "refix.csgo.lan_overhaul" }""")]
    public void Parse_InvalidReFixMods_DropsThePreset(string preset)
    {
        var result = AdminCatalogParser.Parse(WithPreset(preset));

        result.Catalog.Games.Single().Presets.Should().BeEmpty();
        result.Errors.Should().NotBeEmpty();
    }

    [Fact]
    public void ReFixDeployEnvironment_PassesPortableRuntimeAndModChoice()
    {
        var auto = BlueStar.Infrastructure.Emulators.ReFixEmulator.BuildDeployEnvironment(new GameInstance { Name = "G", InstallPath = "G" });
        var none = BlueStar.Infrastructure.Emulators.ReFixEmulator.BuildDeployEnvironment(new GameInstance { Name = "G", InstallPath = "G", ReFixModIds = [] });
        var two = BlueStar.Infrastructure.Emulators.ReFixEmulator.BuildDeployEnvironment(new GameInstance { Name = "G", InstallPath = "G", ReFixModIds = ["a.b", "c"] });

        auto["REFIX_PORTABLE_RUNTIME"].Should().Be("Y");
        auto.Should().NotContainKey("REFIX_GAME_MODS");
        none["REFIX_GAME_MODS"].Should().Be("none");
        two["REFIX_GAME_MODS"].Should().Be("a.b,c");
    }

    [Fact]
    public void ResolveInside_RefusesEscapes()
    {
        AdminPresetDeployer.ResolveInside(_dir, "steam_settings/a.ini").Should().StartWith(_dir);
        var act = () => AdminPresetDeployer.ResolveInside(_dir, "../a.ini");
        act.Should().Throw<InvalidDataException>();
    }

    [Fact]
    public void ArchiveExtractor_DropsEntriesThatEscapeIntoASiblingFolder()
    {
        var zip = Path.Combine(_dir, "emu.zip");
        using (var archive = System.IO.Compression.ZipFile.Open(zip, System.IO.Compression.ZipArchiveMode.Create))
        {
            using (var w = new StreamWriter(archive.CreateEntry("ok.dll").Open())) w.Write("ok");
            using (var w = new StreamWriter(archive.CreateEntry("../Game-evil/pwn.dll").Open())) w.Write("x");
        }
        var game = Path.Combine(_dir, "Game");

        BlueStar.Infrastructure.Common.ArchiveExtractor.ExtractToDirectory(zip, game);

        File.Exists(Path.Combine(game, "ok.dll")).Should().BeTrue();
        File.Exists(Path.Combine(_dir, "Game-evil", "pwn.dll")).Should().BeFalse();
    }

    [Fact]
    public async Task WriteIniValues_ReplacesExistingKeysAndAppendsNewOnes()
    {
        var path = Path.Combine(_dir, "sub", "c.ini");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllLinesAsync(path, ["[user::general]", "account_name=Old", "language=english"]);

        await AdminPresetDeployer.WriteIniValuesAsync(path, new Dictionary<string, string> { ["account_name"] = "New", ["ip"] = "1" }, default);

        (await File.ReadAllLinesAsync(path)).Should().Equal("[user::general]", "account_name=New", "language=english", "ip=1");
    }

    [Fact]
    public async Task DownloadVerified_ChecksumMismatch_Throws_AndMatchSucceeds()
    {
        var payload = Encoding.UTF8.GetBytes("emulator bytes");
        var good = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
        var deployer = new AdminPresetDeployer(new HttpClient(new StubHandler(payload)), Mock.Of<IAdminCatalogService>(), NullLogger<AdminPresetDeployer>.Instance);

        var ok = await deployer.DownloadVerifiedAsync(
            new AdminDownload { Name = "e", Url = new Uri("https://cdn.example/e.zip"), Sha256 = good }, _dir, default);
        File.ReadAllBytes(ok).Should().Equal(payload);

        var bad = () => deployer.DownloadVerifiedAsync(
            new AdminDownload { Name = "e", Url = new Uri("https://cdn.example/e.zip"), Sha256 = Sha }, _dir, default);
        await bad.Should().ThrowAsync<InvalidDataException>();

        var unpinned = () => deployer.DownloadVerifiedAsync(
            new AdminDownload { Name = "e", Url = new Uri("https://cdn.example/e.zip") }, _dir, default);
        await unpinned.Should().ThrowAsync<InvalidDataException>("an untrusted host needs a checksum even past the parser");
    }

    [Fact]
    public async Task Service_FallsBackToCache_AndKeepsItWhenTheNetworkServesGarbage()
    {
        var cache = Path.Combine(_dir, "admin_catalog.json");
        await File.WriteAllTextAsync(cache, ValidCatalog);

        var offline = new AdminCatalogService(new HttpClient(new StubHandler(null)), NullLogger<AdminCatalogService>.Instance, cache);
        (await offline.GetAsync()).Games.Should().HaveCount(2);

        var garbage = new AdminCatalogService(new HttpClient(new StubHandler(Encoding.UTF8.GetBytes("{\"schemaVersion\":99}"))), NullLogger<AdminCatalogService>.Instance, cache);
        (await garbage.GetAsync()).Games.Should().HaveCount(2);
        (await File.ReadAllTextAsync(cache)).Should().Be(ValidCatalog, "an invalid download must not overwrite the good cache");

        var presets = await garbage.GetPresetsAsync(4465480);
        presets.Select(p => p.Id).Should().Equal("csgo-final", "csgo-custom");
    }

    [Fact]
    public async Task RecommendationProvider_PrefersAdminPreset_ElseFallback()
    {
        var catalog = new Mock<IAdminCatalogService>();
        catalog.Setup(c => c.GetPresetsAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync([new AdminInstallPreset { Id = "a", Name = "A" }]);
        catalog.Setup(c => c.GetPresetsAsync(2, It.IsAny<CancellationToken>())).ReturnsAsync([]);
        var fallback = new Mock<IRecommendationProvider>();
        fallback.Setup(f => f.GetRecommendationAsync(2, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CurationRecommendation { AppId = 2, Source = "CuratedDatabase" });

        var provider = new AdminRecommendationProvider(catalog.Object, fallback.Object);

        (await provider.GetRecommendationAsync(1))!.PresetId.Should().Be("a");
        (await provider.GetRecommendationAsync(2))!.Source.Should().Be("CuratedDatabase");
    }

    private sealed class StubHandler(byte[]? body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (body == null) throw new HttpRequestException("offline");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(body),
                RequestMessage = request
            });
        }
    }
}
