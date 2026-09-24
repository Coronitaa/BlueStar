using BlueStar.Core.Helpers;
using Xunit;

namespace BlueStar.Core.Tests;

public class ThirdPartyNoticeParserTests
{
    [Theory]
    [InlineData("Incorporates 3rd-party DRM: Denuvo Anti-tamper", "DENUVO")]
    [InlineData("Denuvo Anti-tamper 5 different PC limit", "DENUVO")]
    [InlineData("VMProtect", "VMProtect")]
    [InlineData("SecuROM 5 machine activation limit", "SecuROM")]
    [InlineData("Easy Anti-Cheat", "Easy Anti-Cheat")]
    [InlineData("BattlEye Anti-Cheat", "BattlEye")]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void ExtractDrmName_ParsesKnownDrmSystems(string? raw, string? expected)
    {
        var result = ThirdPartyNoticeParser.ExtractDrmName(raw);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("Requires 3rd-Party Account: Rockstar Games Social Club (Supports Linking to Steam Account)", "Rockstar")]
    [InlineData("Requires 3rd-Party Account: EA Account (Supports Linking to Steam Account)", "EA App")]
    [InlineData("Requires 3rd-Party Account: Ubisoft Connect (Supports Linking to Steam Account)", "Ubisoft")]
    [InlineData("Requires 3rd-Party Account: Battle.net (Supports Linking to Steam Account)", "Battle.net")]
    [InlineData("Requires 3rd-Party Account: PlayStation Network (Supports Linking to Steam Account)", "PlayStation")]
    [InlineData("Requires 3rd-Party Account: Xbox Live (Supports Linking to Steam Account)", "Xbox Live")]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void ExtractLauncherName_ParsesKnownLaunchers(string? raw, string? expected)
    {
        var result = ThirdPartyNoticeParser.ExtractLauncherName(raw);
        Assert.Equal(expected, result);
    }
}
