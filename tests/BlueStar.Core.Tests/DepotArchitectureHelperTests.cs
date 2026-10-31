using System.Collections.Generic;
using BlueStar.Core.Helpers;
using BlueStar.Core.Models;
using FluentAssertions;
using Xunit;

namespace BlueStar.Core.Tests;

public class DepotArchitectureHelperTests
{
    [Theory]
    [InlineData("Game Win64 Content", "x64")]
    [InlineData("Depot 64-bit binaries", "x64")]
    [InlineData("Depot x64", "x64")]
    [InlineData("client amd64", "x64")]
    [InlineData("binaries_x86_64", "x64")]
    public void DetectArchitecture_RecognizesX64(string input, string expected)
    {
        DepotArchitectureHelper.DetectArchitecture(input).Should().Be(expected);
    }

    [Theory]
    [InlineData("Game Win32 Content", "x32")]
    [InlineData("Depot 32-bit binaries", "x32")]
    [InlineData("Depot x32", "x32")]
    [InlineData("client x86", "x32")]
    [InlineData("legacy i386 exe", "x32")]
    public void DetectArchitecture_RecognizesX32(string input, string expected)
    {
        DepotArchitectureHelper.DetectArchitecture(input).Should().Be(expected);
    }

    [Theory]
    [InlineData("Game Win32/Win64 Content", "x32/x64")]
    [InlineData("Depot x86/x64 Binaries", "x32/x64")]
    [InlineData("Depot 32/64-bit", "x32/x64")]
    [InlineData("Universal assets", "x32/x64")]
    [InlineData("All architectures supported", "x32/x64")]
    [InlineData("Game Base Content", "x32/x64")]
    [InlineData("", "x32/x64")]
    [InlineData(null, "x32/x64")]
    public void DetectArchitecture_RecognizesBothOrNeutral(string? input, string expected)
    {
        DepotArchitectureHelper.DetectArchitecture(input).Should().Be(expected);
    }

    [Fact]
    public void NormalizeArchitecture_ConvertsLegacyValues()
    {
        DepotArchitectureHelper.NormalizeArchitecture("64-bit").Should().Be("x64");
        DepotArchitectureHelper.NormalizeArchitecture("32-bit").Should().Be("x32");
        DepotArchitectureHelper.NormalizeArchitecture("Universal").Should().Be("x32/x64");
        DepotArchitectureHelper.NormalizeArchitecture("Both").Should().Be("x32/x64");
        DepotArchitectureHelper.NormalizeArchitecture("Ambos").Should().Be("x32/x64");
        DepotArchitectureHelper.NormalizeArchitecture(null, "Depot Win64").Should().Be("x64");
        DepotArchitectureHelper.NormalizeArchitecture(null, "Common Audio").Should().Be("x32/x64");
    }

    [Fact]
    public void GetDepotArchitecture_PrefersField_ThenSteamDbName_ThenName()
    {
        var depot1 = new DepotInfo { DepotId = 1, Architecture = "64-bit", Name = "Depot 1" };
        DepotArchitectureHelper.GetDepotArchitecture(depot1).Should().Be("x64");

        var depot2 = new DepotInfo { DepotId = 2, SteamDbName = "Release x86", Name = "Depot 2" };
        DepotArchitectureHelper.GetDepotArchitecture(depot2).Should().Be("x32");

        var depot3 = new DepotInfo { DepotId = 3, Name = "Shared Sound Assets" };
        DepotArchitectureHelper.GetDepotArchitecture(depot3).Should().Be("x32/x64");
    }

    [Fact]
    public void GetDlcArchitecture_InfersFromDepotsProperly()
    {
        // 1. All depots are x64
        var dlc64 = new DlcInfo
        {
            AppId = 10,
            Name = "DLC 1",
            Depots = [new DepotInfo { DepotId = 101, Architecture = "x64" }]
        };
        DepotArchitectureHelper.GetDlcArchitecture(dlc64).Should().Be("x64");

        // 2. All depots are x32
        var dlc32 = new DlcInfo
        {
            AppId = 20,
            Name = "DLC 2",
            Depots = [new DepotInfo { DepotId = 201, Architecture = "x32" }]
        };
        DepotArchitectureHelper.GetDlcArchitecture(dlc32).Should().Be("x32");

        // 3. Mixed x64 and x32 depots
        var dlcBoth = new DlcInfo
        {
            AppId = 30,
            Name = "DLC 3",
            Depots = [
                new DepotInfo { DepotId = 301, Architecture = "x64" },
                new DepotInfo { DepotId = 302, Architecture = "x32" }
            ]
        };
        DepotArchitectureHelper.GetDlcArchitecture(dlcBoth).Should().Be("x32/x64");

        // 4. DLC with neutral/universal depots
        var dlcNeutral = new DlcInfo
        {
            AppId = 40,
            Name = "Soundtrack DLC",
            Depots = [new DepotInfo { DepotId = 401, Name = "OST Files" }]
        };
        DepotArchitectureHelper.GetDlcArchitecture(dlcNeutral).Should().Be("x32/x64");
    }

    [Fact]
    public void GetDefaultArchitecturePreference_ResolvesProperlyForSystem()
    {
        var depotsWith64And32 = new List<DepotInfo>
        {
            new() { DepotId = 1, Architecture = "x64" },
            new() { DepotId = 2, Architecture = "x32" },
            new() { DepotId = 3, Architecture = "x32/x64" }
        };

        var depotsRetro32 = new List<DepotInfo>
        {
            new() { DepotId = 2, Architecture = "x32" },
            new() { DepotId = 3, Architecture = "x32/x64" }
        };

        var depotsUniversalOnly = new List<DepotInfo>
        {
            new() { DepotId = 3, Architecture = "x32/x64" }
        };

        // On 64-bit system:
        DepotArchitectureHelper.GetDefaultArchitecturePreference(depotsWith64And32, is64BitOperatingSystem: true)
            .Should().Be("x64");
        DepotArchitectureHelper.GetDefaultArchitecturePreference(depotsRetro32, is64BitOperatingSystem: true)
            .Should().Be("x32");
        DepotArchitectureHelper.GetDefaultArchitecturePreference(depotsUniversalOnly, is64BitOperatingSystem: true)
            .Should().Be("x32/x64");

        // On 32-bit system:
        DepotArchitectureHelper.GetDefaultArchitecturePreference(depotsWith64And32, is64BitOperatingSystem: false)
            .Should().Be("x32");
        DepotArchitectureHelper.GetDefaultArchitecturePreference(depotsRetro32, is64BitOperatingSystem: false)
            .Should().Be("x32");
        DepotArchitectureHelper.GetDefaultArchitecturePreference(depotsUniversalOnly, is64BitOperatingSystem: false)
            .Should().Be("x32/x64");
    }

    [Fact]
    public void IsDepotCompatible_FiltersCorrectlyByArchitecturePreference()
    {
        var depot64 = new DepotInfo { DepotId = 1, Architecture = "x64", Platform = "Windows" };
        var depot32 = new DepotInfo { DepotId = 2, Architecture = "x32", Platform = "Windows" };
        var depotBoth = new DepotInfo { DepotId = 3, Architecture = "x32/x64", Platform = "Windows" };
        var depotLinux = new DepotInfo { DepotId = 4, Architecture = "x64", Platform = "Linux" };

        // Under x64 preference on 64-bit OS:
        DepotArchitectureHelper.IsDepotCompatible(depot64, "x64", has64Depots: true, is64BitOperatingSystem: true).Should().BeTrue();
        DepotArchitectureHelper.IsDepotCompatible(depotBoth, "x64", has64Depots: true, is64BitOperatingSystem: true).Should().BeTrue();
        DepotArchitectureHelper.IsDepotCompatible(depot32, "x64", has64Depots: true, is64BitOperatingSystem: true).Should().BeFalse();
        // Pure 32-bit game running on 64-bit OS:
        DepotArchitectureHelper.IsDepotCompatible(depot32, "x64", has64Depots: false, is64BitOperatingSystem: true).Should().BeTrue();
        DepotArchitectureHelper.IsDepotCompatible(depotLinux, "x64", has64Depots: true, is64BitOperatingSystem: true).Should().BeFalse();

        // Under x32 preference:
        DepotArchitectureHelper.IsDepotCompatible(depot32, "x32", has64Depots: true, is64BitOperatingSystem: true).Should().BeTrue();
        DepotArchitectureHelper.IsDepotCompatible(depotBoth, "x32", has64Depots: true, is64BitOperatingSystem: true).Should().BeTrue();
        DepotArchitectureHelper.IsDepotCompatible(depot64, "x32", has64Depots: true, is64BitOperatingSystem: true).Should().BeFalse();

        // Under x32/x64 preference on 64-bit OS (accepts all Windows depots):
        DepotArchitectureHelper.IsDepotCompatible(depot64, "x32/x64", has64Depots: true, is64BitOperatingSystem: true).Should().BeTrue();
        DepotArchitectureHelper.IsDepotCompatible(depot32, "x32/x64", has64Depots: true, is64BitOperatingSystem: true).Should().BeTrue();
        DepotArchitectureHelper.IsDepotCompatible(depotBoth, "x32/x64", has64Depots: true, is64BitOperatingSystem: true).Should().BeTrue();

        // Under x32 or x32/x64 preference on 32-bit OS (must REJECT x64 depots):
        DepotArchitectureHelper.IsDepotCompatible(depot64, "x32/x64", has64Depots: true, is64BitOperatingSystem: false).Should().BeFalse();
        DepotArchitectureHelper.IsDepotCompatible(depot32, "x32/x64", has64Depots: true, is64BitOperatingSystem: false).Should().BeTrue();
        DepotArchitectureHelper.IsDepotCompatible(depotBoth, "x32/x64", has64Depots: true, is64BitOperatingSystem: false).Should().BeTrue();

        DepotArchitectureHelper.IsDepotCompatible(depot64, "x32", has64Depots: true, is64BitOperatingSystem: false).Should().BeFalse();
        DepotArchitectureHelper.IsDepotCompatible(depot32, "x32", has64Depots: true, is64BitOperatingSystem: false).Should().BeTrue();
        DepotArchitectureHelper.IsDepotCompatible(depotBoth, "x32", has64Depots: true, is64BitOperatingSystem: false).Should().BeTrue();
    }

    [Fact]
    public void IsDlcCompatible_FiltersCorrectlyByArchitecturePreference()
    {
        var dlc64 = new DlcInfo
        {
            AppId = 1,
            Name = "64-bit expansion",
            Depots = [new DepotInfo { DepotId = 10, Architecture = "x64", Platform = "Windows" }]
        };
        var dlc32 = new DlcInfo
        {
            AppId = 2,
            Name = "32-bit expansion",
            Depots = [new DepotInfo { DepotId = 20, Architecture = "x32", Platform = "Windows" }]
        };
        var dlcBoth = new DlcInfo
        {
            AppId = 3,
            Name = "Costume pack",
            Depots = [new DepotInfo { DepotId = 30, Architecture = "x32/x64", Platform = "Windows" }]
        };

        // Preference x64 on 64-bit OS:
        DepotArchitectureHelper.IsDlcCompatible(dlc64, "x64", has64Depots: true, is64BitOperatingSystem: true).Should().BeTrue();
        DepotArchitectureHelper.IsDlcCompatible(dlcBoth, "x64", has64Depots: true, is64BitOperatingSystem: true).Should().BeTrue();
        DepotArchitectureHelper.IsDlcCompatible(dlc32, "x64", has64Depots: true, is64BitOperatingSystem: true).Should().BeFalse();

        // Preference x32:
        DepotArchitectureHelper.IsDlcCompatible(dlc32, "x32", has64Depots: true).Should().BeTrue();
        DepotArchitectureHelper.IsDlcCompatible(dlcBoth, "x32", has64Depots: true).Should().BeTrue();
        DepotArchitectureHelper.IsDlcCompatible(dlc64, "x32", has64Depots: true).Should().BeFalse();

        // Preference x32/x64 on 64-bit OS:
        DepotArchitectureHelper.IsDlcCompatible(dlc64, "x32/x64", has64Depots: true, is64BitOperatingSystem: true).Should().BeTrue();
        DepotArchitectureHelper.IsDlcCompatible(dlc32, "x32/x64", has64Depots: true, is64BitOperatingSystem: true).Should().BeTrue();
        DepotArchitectureHelper.IsDlcCompatible(dlcBoth, "x32/x64", has64Depots: true, is64BitOperatingSystem: true).Should().BeTrue();

        // Preference x32/x64 on 32-bit OS:
        DepotArchitectureHelper.IsDlcCompatible(dlc64, "x32/x64", has64Depots: true, is64BitOperatingSystem: false).Should().BeFalse();
        DepotArchitectureHelper.IsDlcCompatible(dlc32, "x32/x64", has64Depots: true, is64BitOperatingSystem: false).Should().BeTrue();
        DepotArchitectureHelper.IsDlcCompatible(dlcBoth, "x32/x64", has64Depots: true, is64BitOperatingSystem: false).Should().BeTrue();
    }
}
