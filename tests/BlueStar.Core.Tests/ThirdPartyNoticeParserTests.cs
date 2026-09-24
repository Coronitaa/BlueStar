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

    [Theory]
    [InlineData("Easy Anti-Cheat (kernel-level)", "Easy Anti-Cheat")]
    [InlineData("BattlEye Anti-Cheat Protection", "BattlEye")]
    [InlineData("Denuvo Anti-Cheat system", "Denuvo Anti-Cheat")]
    [InlineData("Vanguard Anti-Cheat", "Vanguard")]
    [InlineData("Valve Anti-Cheat (VAC) enabled", "VAC")]
    [InlineData("Kernel Level Anti-Cheat", "Kernel Anti-Cheat")]
    [InlineData("<h2 class=\"bb_tag\" >Unreal</h2>Unreal is a registered trademark of Epic Games, Inc.", null)]
    [InlineData("© 2026 tinyBuild<br>", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void ExtractAntiCheatName_ParsesAntiCheatSystems(string? raw, string? expected)
    {
        var result = ThirdPartyNoticeParser.ExtractAntiCheatName(raw);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("Requires 3rd-Party Account: 2K (Supports Linking to Steam Account)", "2K Games")]
    [InlineData("Requires 3rd-Party Account: Rockstar Games Social Club (Supports Linking to Steam Account)", "Rockstar")]
    [InlineData("Requires 3rd-Party Account: EA Account (Supports Linking to Steam Account)", "EA Account")]
    [InlineData("Requires 3rd-Party Account: PlayStation Network (Supports Linking to Steam Account)", "PlayStation")]
    [InlineData("Requires 3rd-Party Account: Ubisoft Connect launcher", "Ubisoft")]
    [InlineData("Requires 3rd-Party Account: Epic Online Services (Supports Linking to Steam Account)", "Epic Online Services")]
    [InlineData("Requires 3rd-Party Account: Epic Games (Supports Linking to Steam Account)", "Epic Games")]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void ExtractAccountName_ParsesAccountNames(string? raw, string? expected)
    {
        var result = ThirdPartyNoticeParser.ExtractAccountName(raw);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("ARC Raiders™ EULA", "ARC Raiders EULA")]
    [InlineData("NBA 2K25 EULA", "NBA 2K25 EULA")]
    [InlineData("Tom Clancy's Rainbow Six® Siege EULA 1", "Rainbow Six Siege EULA")]
    [InlineData("https://store.steampowered.com/eula/1808500_eula_0", "ALUF")]
    [InlineData("RuneScape: Dragonwilds EULA", "RuneScape: Dragonwilds EULA")]
    [InlineData("<h2 class=\"bb_tag\" >Unreal</h2>Unreal is a registered trademark", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void ExtractEulaName_ParsesEulaNames(string? raw, string? expected)
    {
        var result = ThirdPartyNoticeParser.ExtractEulaName(raw);
        Assert.Equal(expected, result);
    }
}
