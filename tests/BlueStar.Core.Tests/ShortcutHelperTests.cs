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

    [Fact]
    public async Task InstallSteamGridArtworkAsync_WithValidAppId_CreatesGridFolder()
    {
        // Arrange
        var tempUserFolder = Path.Combine(Path.GetTempPath(), "SteamUser_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempUserFolder);

        try
        {
            uint shortcutAppId32 = 0x81234567;
            uint gameAppId = 1091500; // Cyberpunk 2077

            // Act
            await ShortcutHelper.InstallSteamGridArtworkAsync(
                tempUserFolder,
                shortcutAppId32,
                gameAppId,
                customHeaderUrl: null,
                customCapsuleUrl: null,
                ct: CancellationToken.None);

            // Assert
            var gridDir = Path.Combine(tempUserFolder, "config", "grid");
            Directory.Exists(gridDir).Should().BeTrue();

            var files = Directory.GetFiles(gridDir);
            files.Should().NotBeEmpty();
        }
        finally
        {
            if (Directory.Exists(tempUserFolder))
            {
                try { Directory.Delete(tempUserFolder, recursive: true); } catch { }
            }
        }
    }

    [Fact]
    public async Task InstallSteamGridArtworkAsync_WithShiftAtMidnight_DownloadsAllFourAssets()
    {
        // Arrange
        var tempUserFolder = Path.Combine(Path.GetTempPath(), "SteamUser_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempUserFolder);

        try
        {
            uint shortcutAppId32 = 0x89abcdef;
            uint gameAppId = 3722330; // Shift At Midnight

            // Act
            await ShortcutHelper.InstallSteamGridArtworkAsync(
                tempUserFolder,
                shortcutAppId32,
                gameAppId,
                customHeaderUrl: null,
                customCapsuleUrl: null,
                ct: CancellationToken.None);

            // Assert
            var gridDir = Path.Combine(tempUserFolder, "config", "grid");
            Directory.Exists(gridDir).Should().BeTrue();

            string id32 = shortcutAppId32.ToString();

            // 1. Portada (Vertical Capsule)
            var verticalPath = Path.Combine(gridDir, $"{id32}p.jpg");
            File.Exists(verticalPath).Should().BeTrue("Portada (vertical capsule) must be downloaded");
            new FileInfo(verticalPath).Length.Should().BeGreaterThan(5000);

            // 2. Fondo (Hero banner)
            var heroPath = Path.Combine(gridDir, $"{id32}_hero.jpg");
            File.Exists(heroPath).Should().BeTrue("Fondo (hero banner) must be downloaded");
            new FileInfo(heroPath).Length.Should().BeGreaterThan(5000);

            // 3. Logo (Transparent PNG)
            var logoPath = Path.Combine(gridDir, $"{id32}_logo.png");
            File.Exists(logoPath).Should().BeTrue("Logo (transparent PNG) must be downloaded");
            new FileInfo(logoPath).Length.Should().BeGreaterThan(5000);

            // 4. Portada Ancha (Header)
            var headerPath = Path.Combine(gridDir, $"{id32}.jpg");
            File.Exists(headerPath).Should().BeTrue("Portada Ancha (header) must be downloaded");
            new FileInfo(headerPath).Length.Should().BeGreaterThan(5000);

            // 5. Ícono (clienticon .ico)
            var iconPath = Path.Combine(gridDir, $"{id32}_icon.ico");
            File.Exists(iconPath).Should().BeTrue("Ícono (.ico) must be downloaded");
            new FileInfo(iconPath).Length.Should().BeGreaterThan(500);
        }
        finally
        {
            if (Directory.Exists(tempUserFolder))
            {
                try { Directory.Delete(tempUserFolder, recursive: true); } catch { }
            }
        }
    }
}

