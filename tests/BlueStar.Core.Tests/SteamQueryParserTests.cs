using BlueStar.Core.Helpers;
using Xunit;

namespace BlueStar.Core.Tests;

public class SteamQueryParserTests
{
    [Theory]
    [InlineData("570", 570u)]
    [InlineData("730", 730u)]
    [InlineData(" 400 ", 400u)]
    [InlineData("10", 10u)]
    public void TryParseAppId_PureNumeric_ReturnsAppId(string input, uint expected)
    {
        Assert.True(SteamQueryParser.TryParseAppId(input, out var appId));
        Assert.Equal(expected, appId);
    }

    [Theory]
    [InlineData("appid:570", 570u)]
    [InlineData("APPID:730", 730u)]
    [InlineData("app:570", 570u)]
    [InlineData("App: 1091500", 1091500u)]
    [InlineData("appid=400", 400u)]
    [InlineData("app=620", 620u)]
    [InlineData("appid 570", 570u)]
    public void TryParseAppId_Prefixed_ReturnsAppId(string input, uint expected)
    {
        Assert.True(SteamQueryParser.TryParseAppId(input, out var appId));
        Assert.Equal(expected, appId);
    }

    [Theory]
    [InlineData("https://store.steampowered.com/app/570/Dota_2/", 570u)]
    [InlineData("http://store.steampowered.com/app/730", 730u)]
    [InlineData("store.steampowered.com/app/400/Portal/?snr=1_4_4__118", 400u)]
    [InlineData("https://steamcommunity.com/app/1091500", 1091500u)]
    [InlineData("steamcommunity.com/app/620/screenshots/", 620u)]
    public void TryParseAppId_WebUrls_ReturnsAppId(string input, uint expected)
    {
        Assert.True(SteamQueryParser.TryParseAppId(input, out var appId));
        Assert.Equal(expected, appId);
    }

    [Theory]
    [InlineData("steam://rungameid/570", 570u)]
    [InlineData("steam://app/730", 730u)]
    [InlineData("steam://install/400", 400u)]
    [InlineData("steam://run/620/", 620u)]
    public void TryParseAppId_ProtocolUris_ReturnsAppId(string input, uint expected)
    {
        Assert.True(SteamQueryParser.TryParseAppId(input, out var appId));
        Assert.Equal(expected, appId);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("Portal 2")]
    [InlineData("Witcher 3")]
    [InlineData("cyberpunk 2077")]
    [InlineData("0")]
    [InlineData("appid:0")]
    [InlineData("https://store.steampowered.com/news/app/570")]
    public void TryParseAppId_NonAppIdQueries_ReturnsFalse(string? input)
    {
        Assert.False(SteamQueryParser.TryParseAppId(input, out var appId));
        Assert.Equal(0u, appId);
    }
}
