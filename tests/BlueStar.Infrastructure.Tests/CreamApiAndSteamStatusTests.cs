using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using BlueStar.Core.Helpers;
using BlueStar.Core.Models;
using BlueStar.Infrastructure.Dlc;
using BlueStar.Infrastructure.Steam;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BlueStar.Infrastructure.Tests;

public class CreamApiAndSteamStatusTests : IDisposable
{
    private readonly string _tempDir;

    public CreamApiAndSteamStatusTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "BlueStarTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, true);
        }
        catch { }
    }

    [Fact]
    public void WriteCreamApiIni_OnlySelectedDlcs_AreWrittenInDlcSection()
    {
        // Arrange
        var service = new CreamInstallerService(NullLogger<CreamInstallerService>.Instance);
        var instance = new GameInstance
        {
            AppId = 123456,
            Name = "Epic Adventure",
            InstallPath = _tempDir,
            Dlcs = new List<DlcInfo>
            {
                new() { AppId = 101, Name = "Expansion 1" },
                new() { AppId = 102, Name = "Soundtrack" },
                new() { AppId = 103, Name = "Season Pass" },
                new() { AppId = 104, Name = "Cosmetic Pack" },
            },
            UnlockedDlcIds = new List<uint> { 101, 103 } // Only 101 and 103 selected!
        };

        // Act
        int writtenCount = service.WriteCreamApiIni(_tempDir, instance);

        // Assert
        writtenCount.Should().Be(2);
        var iniFile = Path.Combine(_tempDir, "cream_api.ini");
        File.Exists(iniFile).Should().BeTrue();

        var content = File.ReadAllText(iniFile, Encoding.UTF8);
        content.Should().Contain("appid = 123456");
        content.Should().Contain("unlockall = false");
        content.Should().Contain("[steam_misc]");
        content.Should().Contain("disableuserinterface = false");
        content.Should().Contain("[dlc]");
        content.Should().Contain("101 = Expansion 1");
        content.Should().Contain("103 = Season Pass");
        content.Should().NotContain("102 = Soundtrack");
        content.Should().NotContain("104 = Cosmetic Pack");
    }

    [Fact]
    public void WriteCreamApiIni_NullUnlockedDlcIds_WritesAllDlcs()
    {
        // Arrange
        var service = new CreamInstallerService(NullLogger<CreamInstallerService>.Instance);
        var instance = new GameInstance
        {
            AppId = 123456,
            Name = "Epic Adventure",
            InstallPath = _tempDir,
            Dlcs = new List<DlcInfo>
            {
                new() { AppId = 201, Name = "DLC 1" },
                new() { AppId = 202, Name = "DLC 2" }
            },
            UnlockedDlcIds = null! // No filtering
        };

        // Act
        int writtenCount = service.WriteCreamApiIni(_tempDir, instance);

        // Assert
        writtenCount.Should().Be(2);
        var iniFile = Path.Combine(_tempDir, "cream_api.ini");
        var content = File.ReadAllText(iniFile, Encoding.UTF8);
        content.Should().Contain("201 = DLC 1");
        content.Should().Contain("202 = DLC 2");
    }

    [Fact]
    public void ParseLoginUsers_Utf8PersonaName_IsParsedCorrectly()
    {
        // Arrange
        var vdfContent = @"
""users""
{
	""76561198226419036""
	{
		""AccountName""		""valengamer96""
		""PersonaName""		""Corøna""
		""RememberPassword""		""1""
		""WantsOfflineMode""		""0""
		""SkipOfflineModeWarning""		""0""
		""AutoLogin""		""1""
		""Timestamp""		""1789008546""
	}
	""76561198668502573""
	{
		""AccountName""		""corofix""
		""PersonaName""		""ReFix""
		""RememberPassword""		""1""
		""WantsOfflineMode""		""0""
		""SkipOfflineModeWarning""		""0""
		""AutoLogin""		""0""
		""Timestamp""		""1788829757""
	}
}";
        var vdfFile = Path.Combine(_tempDir, "loginusers.vdf");
        File.WriteAllText(vdfFile, vdfContent, Encoding.UTF8);

        // Act 1: Match with explicit active SteamId64
        var (persona1, account1) = SteamStatusService.ParseLoginUsers(vdfFile, 76561198226419036UL);
        persona1.Should().Be("Corøna");
        account1.Should().Be("valengamer96");

        // Act 2: Fallback without SteamId64 (matches AutoLogin 1 or highest Timestamp)
        var (persona2, account2) = SteamStatusService.ParseLoginUsers(vdfFile, null);
        persona2.Should().Be("Corøna");
        account2.Should().Be("valengamer96");
    }

    [Fact]
    public void ShortcutHelper_GetSteamPath_ValidatesSteamExeExists()
    {
        if (!OperatingSystem.IsWindows()) return;

        var path = ShortcutHelper.GetSteamPath();
        if (path != null)
        {
            Directory.Exists(path).Should().BeTrue();
            (File.Exists(Path.Combine(path, "steam.exe")) || File.Exists(Path.Combine(path, "Steam.exe"))).Should().BeTrue();
        }
    }

    [Fact]
    public void SteamStatusService_LiveSystem_ResolvesPersonaName()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var svc = new SteamStatusService(NullLogger<SteamStatusService>.Instance);
        var status = svc.CurrentStatus;
        if (status.IsRunning)
        {
            status.PersonaName.Should().NotBeNullOrWhiteSpace();
            status.DisplayName.Should().Be(status.PersonaName);
            status.StatusText.Should().Contain(status.PersonaName);
        }
    }
}
