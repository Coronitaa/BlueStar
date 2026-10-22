using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using BlueStar.Core.Helpers;
using FluentAssertions;
using Xunit;

namespace BlueStar.Core.Tests;

public class InstanceArtworkServiceTests
{
    [Fact]
    public void GetRecommendedDimensions_ReturnsAccurateSpecs()
    {
        var capsule = InstanceArtworkService.GetRecommendedDimensions(SteamArtworkType.Capsule);
        capsule.Width.Should().Be(600);
        capsule.Height.Should().Be(900);

        var hero = InstanceArtworkService.GetRecommendedDimensions(SteamArtworkType.Hero);
        hero.Width.Should().Be(1920);
        hero.Height.Should().Be(620);

        var header = InstanceArtworkService.GetRecommendedDimensions(SteamArtworkType.Header);
        header.Width.Should().Be(460);
        header.Height.Should().Be(215);

        var icon = InstanceArtworkService.GetRecommendedDimensions(SteamArtworkType.Icon);
        icon.Width.Should().Be(256);
        icon.Height.Should().Be(256);

        var banner = InstanceArtworkService.GetRecommendedDimensions(SteamArtworkType.InstanceBanner);
        banner.Width.Should().Be(460);
        banner.Height.Should().Be(215);

        var navLogo = InstanceArtworkService.GetRecommendedDimensions(SteamArtworkType.InstanceLogo);
        navLogo.Width.Should().Be(256);
        navLogo.Height.Should().Be(256);
    }

    [Theory]
    [InlineData(600, 900, true, true)]
    [InlineData(1200, 1800, true, false)] // 2:3 ratio, valid
    [InlineData(1920, 1080, false, false)] // Landscape for vertical capsule -> invalid
    [InlineData(200, 300, true, false)] // 2:3 ratio but small
    public void ValidateDimensions_Capsule_ValidatesCorrectly(int width, int height, bool expectedValid, bool expectedRecommended)
    {
        var result = InstanceArtworkService.ValidateDimensions(SteamArtworkType.Capsule, width, height, ".jpg");
        result.IsValid.Should().Be(expectedValid);
        result.IsRecommended.Should().Be(expectedRecommended);
    }

    [Theory]
    [InlineData(1920, 620, true, true)]
    [InlineData(3840, 1240, true, false)] // ~3.1:1 ratio, valid
    [InlineData(600, 900, false, false)] // Vertical image for hero -> invalid
    [InlineData(1000, 1000, false, false)] // Square for hero -> invalid
    public void ValidateDimensions_Hero_ValidatesCorrectly(int width, int height, bool expectedValid, bool expectedRecommended)
    {
        var result = InstanceArtworkService.ValidateDimensions(SteamArtworkType.Hero, width, height, ".jpg");
        result.IsValid.Should().Be(expectedValid);
        result.IsRecommended.Should().Be(expectedRecommended);
    }

    [Theory]
    [InlineData(256, 256, true, true)]
    [InlineData(512, 512, true, false)]
    [InlineData(1920, 1080, false, false)] // Non-square for icon -> invalid
    public void ValidateDimensions_Icon_ValidatesCorrectly(int width, int height, bool expectedValid, bool expectedRecommended)
    {
        var result = InstanceArtworkService.ValidateDimensions(SteamArtworkType.Icon, width, height, ".ico");
        result.IsValid.Should().Be(expectedValid);
        result.IsRecommended.Should().Be(expectedRecommended);
    }

    [Fact]
    public void ValidateDimensions_Logo_WarnsOnNonPng()
    {
        var resultJpg = InstanceArtworkService.ValidateDimensions(SteamArtworkType.Logo, 800, 400, ".jpg");
        resultJpg.WarningMessage.Should().Contain("PNG");

        var resultPng = InstanceArtworkService.ValidateDimensions(SteamArtworkType.Logo, 800, 400, ".png");
        resultPng.WarningMessage.Should().BeNull();
    }

    [Fact]
    public void GetImageDimensions_DecodesPngDimensions()
    {
        // Construct minimum PNG header
        // Signature: 89 50 4E 47 0D 0A 1A 0A
        // IHDR chunk length: 00 00 00 0D
        // IHDR chunk type: 'I','H','D','R' (49 48 44 52)
        // Width: 600 (0x00000258)
        // Height: 900 (0x00000384)
        var pngBytes = new byte[32];
        pngBytes[0] = 0x89; pngBytes[1] = 0x50; pngBytes[2] = 0x4E; pngBytes[3] = 0x47;
        pngBytes[4] = 0x0D; pngBytes[5] = 0x0A; pngBytes[6] = 0x1A; pngBytes[7] = 0x0A;

        // IHDR length + type (offset 8-15)
        pngBytes[12] = (byte)'I'; pngBytes[13] = (byte)'H'; pngBytes[14] = (byte)'D'; pngBytes[15] = (byte)'R';

        // Width 600 = 0x0258
        pngBytes[16] = 0x00; pngBytes[17] = 0x00; pngBytes[18] = 0x02; pngBytes[19] = 0x58;

        // Height 900 = 0x0384
        pngBytes[20] = 0x00; pngBytes[21] = 0x00; pngBytes[22] = 0x03; pngBytes[23] = 0x84;

        var (w, h) = InstanceArtworkService.GetImageDimensions(pngBytes);
        w.Should().Be(600);
        h.Should().Be(900);
    }

    [Fact]
    public void GetImageDimensions_DecodesBmpDimensions()
    {
        // 'BM' header
        var bmpBytes = new byte[30];
        bmpBytes[0] = (byte)'B'; bmpBytes[1] = (byte)'M';

        // Width at 18: 460 = 0x01CC
        bmpBytes[18] = 0xCC; bmpBytes[19] = 0x01; bmpBytes[20] = 0x00; bmpBytes[21] = 0x00;

        // Height at 22: 215 = 0x00D7
        bmpBytes[22] = 0xD7; bmpBytes[23] = 0x00; bmpBytes[24] = 0x00; bmpBytes[25] = 0x00;

        var (w, h) = InstanceArtworkService.GetImageDimensions(bmpBytes);
        w.Should().Be(460);
        h.Should().Be(215);
    }

    [Fact]
    public void GetImageDimensions_DecodesIcoDimensions()
    {
        // ICO header: 00 00 01 00, count = 1
        var icoBytes = new byte[16];
        icoBytes[0] = 0x00; icoBytes[1] = 0x00; icoBytes[2] = 0x01; icoBytes[3] = 0x00;
        icoBytes[4] = 0x01; icoBytes[5] = 0x00;

        // Directory entry 1: Width at 6, Height at 7
        // 0 means 256
        icoBytes[6] = 0x00;
        icoBytes[7] = 0x00;

        var (w, h) = InstanceArtworkService.GetImageDimensions(icoBytes);
        w.Should().Be(256);
        h.Should().Be(256);
    }

    [Fact]
    public async Task SaveCustomArtwork_StoresFileInInstanceDirectory()
    {
        var testInstanceId = Guid.NewGuid();
        var fakeData = Encoding.UTF8.GetBytes("fake capsule image data for test");

        try
        {
            await InstanceArtworkService.SaveCustomArtworkAsync(
                testInstanceId,
                SteamArtworkType.Capsule,
                fakeData,
                ".jpg");

            var path = InstanceArtworkService.GetArtworkFilePath(testInstanceId, SteamArtworkType.Capsule);
            path.Should().NotBeNull();
            File.Exists(path!).Should().BeTrue();
            Path.GetFileName(path!).Should().Be("capsule.jpg");

            var read = await File.ReadAllBytesAsync(path!);
            read.Should().Equal(fakeData);
        }
        finally
        {
            var dir = Path.Combine(InstanceArtworkService.GetInstancesRootPath(), testInstanceId.ToString());
            if (Directory.Exists(dir))
            {
                try { Directory.Delete(dir, recursive: true); } catch { }
            }
        }
    }

    [Fact]
    public void DeleteArtworkFiles_RemovesAllMatchingFilesAndFiresEvent()
    {
        var testInstanceId = Guid.NewGuid();
        var dir = InstanceArtworkService.GetArtworkDirectory(testInstanceId);
        bool eventFired = false;
        EventHandler<(Guid InstanceId, SteamArtworkType Type)> handler = (s, e) =>
        {
            if (e.InstanceId == testInstanceId && e.Type == SteamArtworkType.InstanceLogo)
            {
                eventFired = true;
            }
        };

        InstanceArtworkService.ArtworkChanged += handler;

        try
        {
            var p1 = Path.Combine(dir, "instance_logo.png");
            var p2 = Path.Combine(dir, "instance_logo.ico");
            var p3 = Path.Combine(dir, "instance_banner.png");
            File.WriteAllText(p1, "png data");
            File.WriteAllText(p2, "ico data");
            File.WriteAllText(p3, "banner data");

            var success = InstanceArtworkService.DeleteArtworkFiles(testInstanceId, SteamArtworkType.InstanceLogo);
            success.Should().BeTrue();
            File.Exists(p1).Should().BeFalse();
            File.Exists(p2).Should().BeFalse();
            File.Exists(p3).Should().BeTrue(); // other slot untouched
            eventFired.Should().BeTrue();
        }
        finally
        {
            InstanceArtworkService.ArtworkChanged -= handler;
            var rootDir = Path.Combine(InstanceArtworkService.GetInstancesRootPath(), testInstanceId.ToString());
            if (Directory.Exists(rootDir))
            {
                try { Directory.Delete(rootDir, recursive: true); } catch { }
            }
        }
    }

    [Fact]
    public void EnsureIcoPath_ResolvesSiblingOrConverts()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "bluestar_ico_test_" + Guid.NewGuid());
        Directory.CreateDirectory(tempDir);

        try
        {
            var pngFile = Path.Combine(tempDir, "game.png");
            var icoFile = Path.Combine(tempDir, "game.ico");
            File.WriteAllText(pngFile, "png content");

            // 1. If sibling ico exists, return sibling
            File.WriteAllText(icoFile, "ico content");
            var resolved = ShortcutHelper.EnsureIcoPath(pngFile);
            resolved.Should().Be(icoFile);

            // 2. If no sibling, but converter provided, converts and creates sibling
            File.Delete(icoFile);
            ShortcutHelper.ImageToIcoConverter = (b) => Encoding.UTF8.GetBytes("converted_ico");
            var resolved2 = ShortcutHelper.EnsureIcoPath(pngFile);
            resolved2.Should().Be(icoFile);
            File.Exists(icoFile).Should().BeTrue();
            File.ReadAllText(icoFile).Should().Be("converted_ico");
        }
        finally
        {
            ShortcutHelper.ImageToIcoConverter = null;
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, recursive: true); } catch { }
            }
        }
    }
}
