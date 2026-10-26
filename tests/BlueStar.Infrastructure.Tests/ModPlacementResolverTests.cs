using System;
using System.IO;
using System.Linq;
using BlueStar.Core.Models;
using BlueStar.Infrastructure.Mods.Installation;
using FluentAssertions;
using Xunit;

namespace BlueStar.Infrastructure.Tests;

public sealed class ModPlacementResolverTests : IDisposable
{
    private readonly ModInstallTestFixture _fx = new();
    private readonly ModPlacementResolver _resolver = new();

    public void Dispose() => _fx.Dispose();

    private ModInstallPlan Resolve(string package, EngineType engine = EngineType.Generic, ModPackageLayout layout = ModPackageLayout.Unknown, string name = "CoolMod") =>
        _resolver.Resolve(_fx.Instance(engine), package, new ModPlacementHints { ModName = name, Layout = layout });

    private static string[] Destinations(ModInstallPlan plan) =>
        plan.Operations.Select(o => o.RelativeDestination.Replace('\\', '/')).OrderBy(d => d).ToArray();

    [Fact]
    public void Resolve_AlignsArchiveWithExistingFolders_AndUnwrapsWrapperFolder()
    {
        // Arrange: archive "CoolMod-1.0/BepInEx/plugins/Cool.dll" + loose winhttp.dll, game already has BepInEx/
        _fx.GameFolder("BepInEx/plugins");
        var package = _fx.Package("pkg", ("CoolMod-1.0/BepInEx/plugins/Cool.dll", "x"), ("CoolMod-1.0/winhttp.dll", "x"));

        // Act
        var plan = Resolve(package);

        // Assert
        plan.Strategy.Should().Be("RootAligned");
        plan.Confidence.Should().Be(PlacementConfidence.High);
        Destinations(plan).Should().Equal("BepInEx/plugins/Cool.dll", "winhttp.dll");
    }

    [Fact]
    public void Resolve_AlignsNestedFolder_WhenGameKeepsItDeeper()
    {
        // Arrange: Unreal game where Paks lives at Game/Content/Paks; archive ships "Paks/~mods/x.pak"
        _fx.GameFolder("Game/Content/Paks");
        var package = _fx.Package("pkg", ("Paks/~mods/x.pak", "x"));

        // Act
        var plan = Resolve(package);

        // Assert
        Destinations(plan).Should().Equal("Game/Content/Paks/~mods/x.pak");
    }

    [Fact]
    public void Resolve_PlacesLoosePaksIntoTildeMods()
    {
        // Arrange
        _fx.GameFolder("Engine/Content/Paks");
        _fx.GameFolder("MyGame/Content/Paks");
        var package = _fx.Package("pkg", ("Cool_P.pak", "x"), ("Cool_P.utoc", "x"), ("Cool_P.ucas", "x"));

        // Act
        var plan = Resolve(package, EngineType.UnrealEngine);

        // Assert
        plan.Strategy.Should().Be("UnrealPaks");
        Destinations(plan).Should().OnlyContain(d => d.StartsWith("MyGame/Content/Paks/~mods/"));
    }

    [Fact]
    public void Resolve_PlacesUnityDllIntoBepInExPlugins_WhenLoaderInstalled()
    {
        // Arrange
        _fx.GameFolder("BepInEx/core");
        _fx.GameFolder("Game_Data/Managed");
        var package = _fx.Package("pkg", ("CoolMod/Cool.dll", "x"));

        // Act
        var plan = Resolve(package, EngineType.Unity);

        // Assert
        plan.Strategy.Should().Be("UnityBepInExPlugins");
        plan.Confidence.Should().Be(PlacementConfidence.High);
        Destinations(plan).Should().Equal("BepInEx/plugins/CoolMod/Cool.dll");
    }

    [Fact]
    public void Resolve_UnityDllWithoutLoader_IsLowConfidence()
    {
        // Arrange
        _fx.GameFolder("Game_Data/Managed");
        var package = _fx.Package("pkg", ("Cool.dll", "x"));

        // Act
        var plan = Resolve(package);

        // Assert
        plan.Confidence.Should().Be(PlacementConfidence.Low);
        plan.Reason.Should().Contain("BepInEx");
    }

    [Fact]
    public void Resolve_ThunderstorePackage_MapsFoldersIntoBepInEx_AndSkipsMetadata()
    {
        // Arrange
        var package = _fx.Package("pkg",
            ("manifest.json", "{}"), ("icon.png", "x"), ("README.md", "x"),
            ("plugins/Cool.dll", "x"), ("config/cool.cfg", "x"), ("Extra.dll", "x"));

        // Act
        var plan = Resolve(package, layout: ModPackageLayout.BepInExPackage, name: "Author-Cool");

        // Assert
        Destinations(plan).Should().Equal(
            "BepInEx/config/cool.cfg",
            "BepInEx/plugins/Author-Cool/Cool.dll",
            "BepInEx/plugins/Author-Cool/Extra.dll");
    }

    [Fact]
    public void Resolve_ThunderstoreBepInExPack_ExtractsAtGameRoot()
    {
        // Arrange: BepInExPack wraps winhttp.dll + BepInEx/ in a folder next to the manifest
        var package = _fx.Package("pkg",
            ("manifest.json", "{}"), ("icon.png", "x"),
            ("BepInExPack/winhttp.dll", "x"), ("BepInExPack/BepInEx/core/BepInEx.dll", "x"));

        // Act
        var plan = Resolve(package, layout: ModPackageLayout.BepInExPackage);

        // Assert
        plan.Strategy.Should().Be("GameRoot");
        Destinations(plan).Should().Equal("BepInEx/core/BepInEx.dll", "winhttp.dll");
    }

    [Fact]
    public void Resolve_BethesdaPlugin_GoesIntoData()
    {
        // Arrange
        _fx.GameFile("Data/Skyrim.esm");
        var package = _fx.Package("pkg", ("Cool.esp", "x"), ("meshes/cool.nif", "x"));

        // Act
        var plan = Resolve(package);

        // Assert
        plan.Strategy.Should().Be("BethesdaData");
        Destinations(plan).Should().Equal("Data/Cool.esp", "Data/meshes/cool.nif");
    }

    [Fact]
    public void Resolve_SourceVpk_GoesIntoAddonsNextToGameinfo()
    {
        // Arrange
        _fx.GameFile("left4dead2/gameinfo.txt");
        var package = _fx.Package("pkg", ("cool.vpk", "x"));

        // Act
        var plan = Resolve(package);

        // Assert
        Destinations(plan).Should().Equal("left4dead2/addons/cool.vpk");
    }

    [Fact]
    public void Resolve_UnknownPayload_FallsBackToModsFolderWithLowConfidence()
    {
        // Arrange
        var package = _fx.Package("pkg", ("data.bin", "x"), ("README.txt", "docs"));

        // Act
        var plan = Resolve(package);

        // Assert
        plan.Strategy.Should().Be("ModsFolder");
        plan.Confidence.Should().Be(PlacementConfidence.Low);
        Destinations(plan).Should().Equal("mods/CoolMod/data.bin");
    }

    private ModInstallPlan ResolveWorkshop(string package, EngineType engine = EngineType.Generic) =>
        _resolver.Resolve(_fx.Instance(engine), package,
            new ModPlacementHints { ModName = "Cool Item", Layout = ModPackageLayout.SteamWorkshop, WorkshopItemId = 123456 });

    [Fact]
    public void Resolve_WorkshopItem_WithEmulatorSettings_GoesToSteamSettingsMods_AsUploaded()
    {
        // Arrange: emulator config next to steam_api; the item keeps its README and folder layout
        _fx.GameFolder("Game/Binaries/Win64/steam_settings");
        _fx.GameFolder("Game/Content/Paks");
        var package = _fx.Package("ws", ("Item/data.pak", "x"), ("README.md", "docs"));

        // Act
        var plan = ResolveWorkshop(package, EngineType.UnrealEngine);

        // Assert
        plan.Strategy.Should().Be("SteamWorkshopEmulator");
        plan.Confidence.Should().Be(PlacementConfidence.High);
        Destinations(plan).Should().Equal(
            "Game/Binaries/Win64/steam_settings/mods/123456/Item/data.pak",
            "Game/Binaries/Win64/steam_settings/mods/123456/README.md");
    }

    [Fact]
    public void Resolve_WorkshopItem_WithoutEmulator_UsesFileRules()
    {
        // Arrange
        _fx.GameFolder("Game/Content/Paks");
        var package = _fx.Package("ws", ("Cool_P.pak", "x"));

        // Act
        var plan = ResolveWorkshop(package, EngineType.UnrealEngine);

        // Assert
        plan.Strategy.Should().Be("UnrealPaks");
        Destinations(plan).Should().Equal("Game/Content/Paks/~mods/Cool_P.pak");
    }

    [Fact]
    public void Resolve_WorkshopItem_NoRuleMatches_OffersSteamSettingsNextToSteamApi_ForConfirmation()
    {
        // Arrange
        _fx.GameFile("Bin/steam_api64.dll");
        var package = _fx.Package("ws", ("level.dat", "x"));

        // Act
        var plan = ResolveWorkshop(package);

        // Assert
        plan.Strategy.Should().Be("SteamWorkshopEmulator");
        plan.Confidence.Should().Be(PlacementConfidence.Low);
        Destinations(plan).Should().Equal("Bin/steam_settings/mods/123456/level.dat");
    }

    [Fact]
    public void Resolve_WorkshopItem_WithoutId_Throws()
    {
        // Arrange
        var package = _fx.Package("ws", ("a.pak", "x"));

        // Act
        var act = () => _resolver.Resolve(_fx.Instance(), package, new ModPlacementHints { Layout = ModPackageLayout.SteamWorkshop });

        // Assert
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Resolve_EmptyPackage_Throws()
    {
        // Arrange
        var package = _fx.Package("pkg", ("README.md", "only docs"));

        // Act
        var act = () => Resolve(package);

        // Assert
        act.Should().Throw<InvalidOperationException>();
    }
}
