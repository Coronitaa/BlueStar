using System.Collections.Generic;
using System.Linq;
using BlueStar.Core.Models;
using FluentAssertions;
using Xunit;

namespace BlueStar.Core.Tests;

public class PresetDepotSelectionTests
{
    private static readonly DepotInfo[] Depots =
    [
        new() { DepotId = 731, ManifestId = 1 },
        new() { DepotId = 732, ManifestId = 2 },
        new() { DepotId = 735, ManifestId = 3 },
        new() { DepotId = 9001, ManifestId = 4 } // a DLC depot
    ];

    private static readonly HashSet<uint> BaseDepots = [731, 732, 735];

    private static readonly Dictionary<uint, ulong> Pins = new() { [731] = 2666951201135270376, [732] = 5695413866370602519 };

    [Fact]
    public void AdminPreset_InstallsOnlyItsBaseDepots_PinnedToItsManifests()
    {
        var rec = new CurationRecommendation { PresetId = "pre-panorama", PinnedManifests = Pins };

        var selected = rec.SelectDepots(Depots, BaseDepots);

        selected.Select(d => d.DepotId).Should().Equal(731u, 732u, 9001u);
        selected[0].ManifestId.Should().Be(2666951201135270376);
        selected[1].ManifestId.Should().Be(5695413866370602519);
        selected[2].ManifestId.Should().Be(4, "DLC depots are not part of the preset's build");
    }

    [Fact]
    public void CommunityCuration_KeepsUnpinnedDepots()
    {
        var rec = new CurationRecommendation { PinnedManifests = Pins };

        rec.SelectDepots(Depots, BaseDepots).Select(d => d.DepotId).Should().Equal(731u, 732u, 735u, 9001u);
    }

    [Fact]
    public void PresetWithoutPins_KeepsEveryDepot()
    {
        var rec = new CurationRecommendation { PresetId = "latest" };

        rec.SelectDepots(Depots, BaseDepots).Should().HaveCount(4);
    }
}
