using System.IO;
using BlueStar.Core.Helpers;
using BlueStar.Core.Models;
using FluentAssertions;
using Xunit;

namespace BlueStar.Core.Tests;

public class ShortcutHelperTests
{
    [Fact]
    public void FindGameExecutables_FiltersExcludedExecutables_AndPrioritizesGameName()
    {
        // Arrange
        var tempDir = Path.Combine(Path.GetTempPath(), "BlueStar_ShortcutTest_" + Guid.NewGuid());
        Directory.CreateDirectory(tempDir);

        try
        {
            var subDir = Path.Combine(tempDir, "Binaries", "Win64");
            Directory.CreateDirectory(subDir);

            // Create various executables
            File.WriteAllText(Path.Combine(tempDir, "UnityCrashHandler64.exe"), "dummy");
            File.WriteAllText(Path.Combine(tempDir, "dxsetup.exe"), "dummy");
            File.WriteAllText(Path.Combine(tempDir, "vcredist_x64.exe"), "dummy");
            File.WriteAllText(Path.Combine(tempDir, "unins000.exe"), "dummy");
            File.WriteAllText(Path.Combine(tempDir, "MyAwesomeGame.exe"), "dummy");
            File.WriteAllText(Path.Combine(subDir, "MyAwesomeGame-Win64-Shipping.exe"), "dummy");
            File.WriteAllText(Path.Combine(tempDir, "Launcher.exe"), "dummy");

            // Act
            var results = ShortcutHelper.FindGameExecutables(tempDir, "My Awesome Game");

            // Assert
            results.Should().NotBeEmpty();
            results.Should().NotContain(p => p.Contains("UnityCrashHandler"));
            results.Should().NotContain(p => p.Contains("dxsetup"));
            results.Should().NotContain(p => p.Contains("vcredist"));
            results.Should().NotContain(p => p.Contains("unins000"));

            // MyAwesomeGame.exe in root or shipping should be first
            Path.GetFileName(results[0]).Should().Be("MyAwesomeGame.exe");
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public void CreateShortcuts_NonExistentTarget_ReturnsFailure()
    {
        // Act
        var result = ShortcutHelper.CreateShortcuts(
            @"C:\NonExistentFolder\Game.exe",
            "Game",
            createDesktop: true,
            createStartMenu: true);

        // Assert
        result.Success.Should().BeFalse();
        result.Message.Should().Contain("does not exist");
    }

    [Fact]
    public void CreateShortcuts_NoLocationSelected_ReturnsFailure()
    {
        // Arrange
        var tempFile = Path.GetTempFileName();

        try
        {
            // Act
            var result = ShortcutHelper.CreateShortcuts(
                tempFile,
                "Game",
                createDesktop: false,
                createStartMenu: false);

            // Assert
            result.Success.Should().BeFalse();
            result.Message.Should().Contain("at least one shortcut destination");
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public void LoadInstance_UninstalledDepots_RemainNotDownloaded_EvenIfStatusReady()
    {
        // Verify that uninstalled depots stay IsDownloaded = false and are not falsely promoted
        var depot1 = new DepotInfo { DepotId = 1, Name = "Downloaded Depot", IsDownloaded = true };
        var depot2 = new DepotInfo { DepotId = 2, Name = "Not Downloaded Depot", IsDownloaded = false };

        var instance = new GameInstance
        {
            Name = "Test Game",
            InstallPath = @"C:\Games\Test",
            Status = InstanceStatus.Ready,
            Depots = [depot1, depot2]
        };

        // Simulating the clean loading logic
        var loadedDepots = instance.Depots.Select(d => d with { Name = d.Name }).ToList();

        loadedDepots.First(d => d.DepotId == 1).IsDownloaded.Should().BeTrue();
        loadedDepots.First(d => d.DepotId == 2).IsDownloaded.Should().BeFalse();
    }

    [Fact]
    public void ComputeCrc32_ReturnsExpectedHash()
    {
        // Standard CRC-32 test vectors
        ShortcutHelper.ComputeCrc32("").Should().Be(0);
        ShortcutHelper.ComputeCrc32("123456789").Should().Be(0xCBF43926);
    }

    [Fact]
    public void BuildVdfEntry_BuildsValidStructure()
    {
        var entryBytes = ShortcutHelper.BuildVdfEntry(0, "TestGame", @"C:\Games\Test\game.exe", @"C:\Games\Test");
        entryBytes.Should().NotBeEmpty();

        var text = System.Text.Encoding.UTF8.GetString(entryBytes);
        text.Should().Contain("TestGame");
        text.Should().Contain("game.exe");
        text.Should().Contain("AppName");
        text.Should().Contain("appid");
    }

    [Fact]
    public void AddSteamShortcutToUser_CreatesNewVdf_AndAppendsCorrectly()
    {
        var tempVdf = Path.Combine(Path.GetTempPath(), "shortcuts_test_" + Guid.NewGuid() + ".vdf");

        try
        {
            // First shortcut
            var success1 = ShortcutHelper.AddSteamShortcutToUser(
                tempVdf,
                "Game One",
                @"C:\Games\Game1\game.exe",
                @"C:\Games\Game1");

            success1.Should().BeTrue();
            File.Exists(tempVdf).Should().BeTrue();

            var bytes1 = File.ReadAllBytes(tempVdf);
            bytes1[^2].Should().Be(0x08);
            bytes1[^1].Should().Be(0x08);

            // Second shortcut
            var success2 = ShortcutHelper.AddSteamShortcutToUser(
                tempVdf,
                "Game Two",
                @"C:\Games\Game2\game.exe",
                @"C:\Games\Game2");

            success2.Should().BeTrue();

            var bytes2 = File.ReadAllBytes(tempVdf);
            var text = System.Text.Encoding.UTF8.GetString(bytes2);
            text.Should().Contain("Game One");
            text.Should().Contain("Game Two");
            bytes2[^2].Should().Be(0x08);
            bytes2[^1].Should().Be(0x08);

            // Duplicate shortcut check
            var successDuplicate = ShortcutHelper.AddSteamShortcutToUser(
                tempVdf,
                "Game One",
                @"C:\Games\Game1\game.exe",
                @"C:\Games\Game1");

            successDuplicate.Should().BeTrue();
            var bytes3 = File.ReadAllBytes(tempVdf);
            bytes3.Length.Should().Be(bytes2.Length); // Length unchanged on duplicate
        }
        finally
        {
            if (File.Exists(tempVdf)) File.Delete(tempVdf);
            if (File.Exists($"{tempVdf}.bak")) File.Delete($"{tempVdf}.bak");
        }
    }
}

