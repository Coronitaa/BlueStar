using System;
using System.IO;
using BlueStar.Core.Helpers;
using FluentAssertions;
using Xunit;

namespace BlueStar.Core.Tests;

public class EmulatorIniHelperTests : IDisposable
{
    private readonly string _tempDir;

    public EmulatorIniHelperTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "BlueStar_IniTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, true);
            }
        }
        catch { }
    }

    [Fact]
    public void GenerateRandomSteamId64_ReturnsValid17DigitStringStartingWithPrefix()
    {
        var steamId = EmulatorIniHelper.GenerateRandomSteamId64();

        steamId.Should().NotBeNullOrWhiteSpace();
        steamId.Length.Should().Be(17);
        steamId.Should().StartWith("76561198");
        ulong.TryParse(steamId, out _).Should().BeTrue();
    }

    [Fact]
    public void UpdateReFixIni_OverridesExistingUserSection()
    {
        var refixPath = Path.Combine(_tempDir, "ReFix.ini");
        File.WriteAllText(refixPath, @"
[Game]
GameName = TestGame

[Online]
Mode = valve

[User]
Name = OldPlayer
UserName = OldPlayer
AutoGenerateSteamId = true
SteamId = 123456789
");

        EmulatorIniHelper.UpdateReFixIni(refixPath, "NewGamer", "76561198123456789");

        var text = File.ReadAllText(refixPath);
        text.Should().Contain("Name = NewGamer");
        text.Should().Contain("UserName = NewGamer");
        text.Should().Contain("SteamId = 76561198123456789");
        text.Should().Contain("AutoGenerateSteamId = false");
    }

    [Fact]
    public void UpdateReFixIni_CreatesUserSectionIfMissing()
    {
        var refixPath = Path.Combine(_tempDir, "ReFix.ini");
        File.WriteAllText(refixPath, @"
[Game]
GameName = TestGame
");

        EmulatorIniHelper.UpdateReFixIni(refixPath, "FreshUser", "76561198987654321");

        var text = File.ReadAllText(refixPath);
        text.Should().Contain("[User]");
        text.Should().Contain("Name = FreshUser");
        text.Should().Contain("UserName = FreshUser");
        text.Should().Contain("SteamId = 76561198987654321");
        text.Should().Contain("AutoGenerateSteamId = false");
    }

    [Fact]
    public void UpdateOnlineFixIni_OverridesExistingSettings()
    {
        var onlineFixPath = Path.Combine(_tempDir, "OnlineFix.ini");
        File.WriteAllText(onlineFixPath, @"
[Main]
RealAppId = 480

[Settings]
Language = english
UserName = DefaultFixUser
SteamId = 0
AccountId = 0
");

        EmulatorIniHelper.UpdateOnlineFixIni(onlineFixPath, "CustomFixUser", "76561198055555555");

        var text = File.ReadAllText(onlineFixPath);
        text.Should().Contain("UserName = CustomFixUser");
        text.Should().Contain("SteamId = 76561198055555555");
        ulong.TryParse("76561198055555555", out var id64).Should().BeTrue();
        text.Should().Contain($"AccountId = {(uint)(id64 & 0xFFFFFFFF)}");
    }

    [Fact]
    public void UpdateSteamSettingsDir_WritesAccountAndSteamIdFiles()
    {
        var steamSettingsDir = Path.Combine(_tempDir, "steam_settings");
        Directory.CreateDirectory(steamSettingsDir);

        EmulatorIniHelper.UpdateSteamSettingsDir(steamSettingsDir, "GoldbergPlayer", "76561198011223344");

        File.ReadAllText(Path.Combine(steamSettingsDir, "force_account_name.txt")).Should().Be("GoldbergPlayer");
        File.ReadAllText(Path.Combine(steamSettingsDir, "account_name.txt")).Should().Be("GoldbergPlayer");
        File.ReadAllText(Path.Combine(steamSettingsDir, "force_steamid.txt")).Should().Be("76561198011223344");
        File.ReadAllText(Path.Combine(steamSettingsDir, "user_steam_id.txt")).Should().Be("76561198011223344");
    }

    [Fact]
    public void ApplyOverrides_RecursivelyFindsAndUpdatesAllConfigs()
    {
        var subDir = Path.Combine(_tempDir, "Binaries", "Win64");
        Directory.CreateDirectory(subDir);

        var refix = Path.Combine(subDir, "ReFix.ini");
        File.WriteAllText(refix, "[User]\nName = Default");

        var onlineFix = Path.Combine(subDir, "OnlineFix64.ini");
        File.WriteAllText(onlineFix, "[Settings]\nUserName = Default");

        var settings = Path.Combine(subDir, "steam_settings");
        Directory.CreateDirectory(settings);

        EmulatorIniHelper.ApplyOverrides(_tempDir, "MasterChief", "76561198012345678");

        File.ReadAllText(refix).Should().Contain("Name = MasterChief");
        File.ReadAllText(refix).Should().Contain("SteamId = 76561198012345678");

        File.ReadAllText(onlineFix).Should().Contain("UserName = MasterChief");
        File.ReadAllText(onlineFix).Should().Contain("SteamId = 76561198012345678");

        File.ReadAllText(Path.Combine(settings, "force_account_name.txt")).Should().Be("MasterChief");
        File.ReadAllText(Path.Combine(settings, "force_steamid.txt")).Should().Be("76561198012345678");
    }
}
