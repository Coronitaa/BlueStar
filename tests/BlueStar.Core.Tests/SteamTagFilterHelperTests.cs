using BlueStar.Core.Helpers;
using Xunit;

namespace BlueStar.Core.Tests;

public class SteamTagFilterHelperTests
{
    [Theory]
    [InlineData("Online co-op", true)]
    [InlineData("Co-op Campaign", true)]
    [InlineData("Co-op", true)]
    [InlineData("Multi-player", true)]
    [InlineData("Single-player", true)]
    [InlineData("PvP", true)]
    [InlineData("PvE", true)]
    [InlineData("LAN co-op", true)]
    [InlineData("Local Multiplayer", true)]
    [InlineData("Full controller support", true)]
    [InlineData("Steam Achievements", true)]
    [InlineData("Steam Cloud", true)]
    [InlineData("Remote Play Together", true)]
    [InlineData("VR Supported", true)]
    [InlineData("Massively Multiplayer", true)]
    [InlineData("Action", false)]
    [InlineData("Party-Based RPG", false)]
    [InlineData("Cute", false)]
    [InlineData("Cartoony", false)]
    [InlineData("3D", false)]
    [InlineData("Top-Down", false)]
    [InlineData("Indie", false)]
    [InlineData("Adventure", false)]
    [InlineData("Utilities", false)]
    [InlineData("Audio Production", false)]
    public void IsConnectivityOrTechnicalTag_FiltersCorrectly(string tag, bool expectedFilter)
    {
        var result = SteamTagFilterHelper.IsConnectivityOrTechnicalTag(tag);
        Assert.Equal(expectedFilter, result);
    }
}
