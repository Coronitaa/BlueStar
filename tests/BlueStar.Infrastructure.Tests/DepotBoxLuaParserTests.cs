using BlueStar.Infrastructure.DepotBox;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BlueStar.Infrastructure.Tests;

public class DepotBoxLuaParserTests
{
    [Fact]
    public void Parse_ValidLuaContent_ExtractsGamesAndDepots()
    {
        // Arrange
        var parser = new DepotBoxLuaParser(NullLogger<DepotBoxLuaParser>.Instance);
        var sampleLua = @"
addappid(730)
addappid(731, 1, ""731_Key"")
setManifestid(730, 731, 1234567890123456789)
";

        // Act
        var archive = parser.Parse(sampleLua);

        // Assert
        archive.Should().NotBeNull();
        archive.Games.Should().HaveCount(2);

        var game730 = archive.Games.FirstOrDefault(g => g.AppId == 730);
        game730.Should().NotBeNull();
        game730!.IsDlc.Should().BeFalse();

        var game731 = archive.Games.FirstOrDefault(g => g.AppId == 731);
        game731.Should().NotBeNull();
        game731!.IsDlc.Should().BeTrue();
        game731.DepotKey.Should().Be("731_Key");
        game731.Depots.Should().HaveCount(1);
        game731.Depots[0].DepotId.Should().Be(730);
        game731.Depots[0].ManifestId.Should().Be(731UL);
        game731.Depots[0].SizeBytes.Should().Be(1234567890123456789L);
    }

    [Fact]
    public void ParseSearchResults_TopLevelArray_ParsesCorrectly()
    {
        var json = @"[
            { ""appId"": 1966720, ""name"": ""Lethal Company"", ""isAvailable"": true, ""dlcCount"": 0 },
            { ""appId"": 730, ""name"": ""Counter-Strike 2"", ""isAvailable"": true, ""dlcCount"": 2 }
        ]";

        var results = DepotBoxApiClient.ParseSearchResults(json);

        results.Should().HaveCount(2);
        results[0].AppId.Should().Be(1966720u);
        results[0].Name.Should().Be("Lethal Company");
        results[0].IsAvailable.Should().BeTrue();
        results[1].AppId.Should().Be(730u);
    }

    [Fact]
    public void ParseSearchResults_WrappedInGamesObject_ParsesCorrectly()
    {
        var json = @"{
            ""success"": true,
            ""games"": [
                { ""appId"": 1966720, ""name"": ""Lethal Company"", ""available"": true }
            ]
        }";

        var results = DepotBoxApiClient.ParseSearchResults(json);

        results.Should().HaveCount(1);
        results[0].AppId.Should().Be(1966720u);
        results[0].Name.Should().Be("Lethal Company");
    }

    [Fact]
    public void ParseSearchResults_WrappedInDataObject_ParsesCorrectly()
    {
        var json = @"{
            ""success"": true,
            ""data"": [
                { ""id"": ""286160"", ""title"": ""Tabletop Simulator"", ""is_available"": 1 }
            ]
        }";

        var results = DepotBoxApiClient.ParseSearchResults(json);

        results.Should().HaveCount(1);
        results[0].AppId.Should().Be(286160u);
        results[0].Name.Should().Be("Tabletop Simulator");
        results[0].IsAvailable.Should().BeTrue();
    }

    [Fact]
    public void ParseSearchResults_DictionaryShape_ParsesCorrectly()
    {
        var json = @"{
            ""1966720"": { ""name"": ""Lethal Company"", ""isAvailable"": true },
            ""286160"": { ""name"": ""Tabletop Simulator"", ""isAvailable"": true }
        }";

        var results = DepotBoxApiClient.ParseSearchResults(json);

        results.Should().HaveCount(2);
        results.Select(r => r.AppId).Should().Contain([1966720u, 286160u]);
    }

    [Fact]
    public void ParseManifests_WrappedObject_ParsesCorrectly()
    {
        var json = @"{
            ""success"": true,
            ""manifests"": [
                { ""depotId"": 1966721, ""manifestId"": ""1234567890123456789"", ""sizeBytes"": 500000000 },
                { ""depotId"": 1966722, ""manifestId"": ""9876543210987654321"", ""sizeBytes"": 100000000 }
            ]
        }";

        var manifests = DepotBoxApiClient.ParseManifests(json);

        manifests.Should().HaveCount(2);
        manifests[0].DepotId.Should().Be(1966721u);
        manifests[0].ManifestId.Should().Be(1234567890123456789UL);
        manifests[0].SizeBytes.Should().Be(500000000L);
    }
}
