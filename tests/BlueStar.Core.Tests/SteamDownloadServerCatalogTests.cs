using BlueStar.Core.Helpers;
using FluentAssertions;
using Xunit;

namespace BlueStar.Core.Tests;

public class SteamDownloadServerCatalogTests
{
    [Fact]
    public void Catalog_ContainsAuto_AsFirstEntry()
    {
        SteamDownloadServerCatalog.Entries.Should().NotBeEmpty();
        var first = SteamDownloadServerCatalog.Entries[0];
        first.CellId.Should().Be(0);
        first.NameEn.Should().Contain("Automatic");
        first.NameEs.Should().Contain("Automático");
    }

    [Fact]
    public void Catalog_ContainsKeyGlobalRegions()
    {
        var entries = SteamDownloadServerCatalog.Entries;
        entries.Should().Contain(e => e.CellId == 116 && e.NameEs.Contains("Argentina"));
        entries.Should().Contain(e => e.CellId == 79 && e.NameEs.Contains("Brasil"));
        entries.Should().Contain(e => e.CellId == 5 && e.NameEn.Contains("Germany"));
        entries.Should().Contain(e => e.CellId == 4 && e.NameEn.Contains("UK"));
        entries.Should().Contain(e => e.CellId == 1 && e.NameEn.Contains("Chicago"));
        entries.Should().Contain(e => e.CellId == 43 && e.NameEn.Contains("Japan"));
    }

    [Theory]
    [InlineData(0, "en", "Automatic")]
    [InlineData(0, "es", "Automático")]
    [InlineData(116, "en", "Argentina")]
    [InlineData(116, "es", "Argentina")]
    [InlineData(5, "en", "Germany")]
    [InlineData(5, "es", "Alemania")]
    [InlineData(999, "en", "Custom (Cell ID 999)")]
    [InlineData(999, "es", "Personalizado (Cell ID 999)")]
    public void GetDisplayName_ReturnsLocalizedStrings(int cellId, string lang, string expectedSubstring)
    {
        var name = SteamDownloadServerCatalog.GetDisplayName(cellId, lang);
        name.Should().Contain(expectedSubstring);
    }
}
