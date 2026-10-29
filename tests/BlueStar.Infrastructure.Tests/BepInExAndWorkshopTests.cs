using System;



using System.IO;



using System.Net.Http;



using System.Threading.Tasks;



using BlueStar.Infrastructure.Mods;



using BlueStar.Infrastructure.Steam;



using BlueStar.Infrastructure.Workshop;



using FluentAssertions;



using Microsoft.Extensions.Logging.Abstractions;



using Xunit;







namespace BlueStar.Infrastructure.Tests;







public class BepInExAndWorkshopTests : IDisposable



{



    private readonly string _tempTestDir;







    public BepInExAndWorkshopTests()



    {



        _tempTestDir = Path.Combine(Path.GetTempPath(), "BlueStar_ModTests_" + Guid.NewGuid().ToString("N"));



        Directory.CreateDirectory(_tempTestDir);



    }







    public void Dispose()



    {



        try



        {



            if (Directory.Exists(_tempTestDir))



                Directory.Delete(_tempTestDir, recursive: true);



        }



        catch { }



    }







    [Fact]



    public void BepInExService_IsInstalled_ReturnsTrue_WhenWinhttpDllExists()



    {



        var gameDir = Path.Combine(_tempTestDir, "GameWithBep");



        Directory.CreateDirectory(gameDir);



        File.WriteAllText(Path.Combine(gameDir, "winhttp.dll"), "hook");







        var service = new BepInExService(new HttpClient(), NullLogger<BepInExService>.Instance);



        service.IsInstalled(gameDir).Should().BeTrue();



    }







    [Fact]



    public void BepInExService_IsInstalled_ReturnsFalse_WhenNoBepInExFiles()



    {



        var gameDir = Path.Combine(_tempTestDir, "CleanGame");



        Directory.CreateDirectory(gameDir);







        var service = new BepInExService(new HttpClient(), NullLogger<BepInExService>.Instance);



        service.IsInstalled(gameDir).Should().BeFalse();



    }







    [Fact]



    public void BepInExService_IsInstalled_ReturnsFalse_WhenOnlyPluginsFolderExists()



    {



        var gameDir = Path.Combine(_tempTestDir, "PluginsOnlyGame");



        Directory.CreateDirectory(Path.Combine(gameDir, "BepInEx", "plugins"));



        File.WriteAllText(Path.Combine(gameDir, "BepInEx", "plugins", "some_mod.dll"), "mod");







        var service = new BepInExService(new HttpClient(), NullLogger<BepInExService>.Instance);



        service.IsInstalled(gameDir).Should().BeFalse();



    }







    [Fact]



    public async Task BepInExService_UninstallAsync_RemovesWinhttpAndCoreFiles()



    {



        var gameDir = Path.Combine(_tempTestDir, "UninstallTest");



        Directory.CreateDirectory(Path.Combine(gameDir, "BepInEx", "core"));



        Directory.CreateDirectory(Path.Combine(gameDir, "BepInEx", "plugins"));



        File.WriteAllText(Path.Combine(gameDir, "winhttp.dll"), "hook");



        File.WriteAllText(Path.Combine(gameDir, "doorstop_config.ini"), "config");



        File.WriteAllText(Path.Combine(gameDir, "BepInEx", "plugins", "myplugin.dll"), "plugin");







        var service = new BepInExService(new HttpClient(), NullLogger<BepInExService>.Instance);



        var result = await service.UninstallAsync(gameDir, keepPluginsFolder: true);







        result.Should().BeTrue();



        File.Exists(Path.Combine(gameDir, "winhttp.dll")).Should().BeFalse();



        File.Exists(Path.Combine(gameDir, "doorstop_config.ini")).Should().BeFalse();



        Directory.Exists(Path.Combine(gameDir, "BepInEx", "core")).Should().BeFalse();



        Directory.Exists(Path.Combine(gameDir, "BepInEx", "plugins")).Should().BeTrue();



    }







    [Theory]



    [InlineData("123456789", 123456789UL)]



    [InlineData("https://steamcommunity.com/sharedfiles/filedetails/?id=987654321", 987654321UL)]



    [InlineData("https://steamcommunity.com/workshop/filedetails/?id=555666777&searchtext=mod", 555666777UL)]



    [InlineData("invalid_input", null)]



    public void WorkshopService_ParsePublishedFileId_ExtractsCorrectId(string input, ulong? expected)



    {



        var service = new WorkshopService(new HttpClient(), NullLogger<WorkshopService>.Instance);



        var id = service.ParsePublishedFileId(input);



        id.Should().Be(expected);



    }







    [Fact]



    public async Task UnityModManager_GetInstalledModsAsync_ReadsWorkshopInfoAndDirectorySize()



    {



        var gameDir = Path.Combine(_tempTestDir, "TabletopSimulator");



        var modsFolder = Path.Combine(gameDir, "Mods", "Optimal Monopoly");



        Directory.CreateDirectory(modsFolder);







        // Dummy files inside mod directory



        File.WriteAllText(Path.Combine(modsFolder, "Optimal Monopoly.json"), "{\"SaveName\":\"Monopoly\"}");



        File.WriteAllText(Path.Combine(modsFolder, "workshop_info.json"), "{\"Title\":\"Optimal Monopoly Deluxe\",\"Author\":\"ModCreator\",\"Description\":\"Best monopoly mod\"}");







        // Staging directory that should be ignored



        var stagingDir = Path.Combine(gameDir, "Mods", ".DepotDownloader");



        Directory.CreateDirectory(stagingDir);



        File.WriteAllText(Path.Combine(stagingDir, "temp.bin"), "temp");







        var instance = new BlueStar.Core.Models.GameInstance



        {



            Name = "Unity Game With Mods",



            AppId = 999999,



            InstallPath = gameDir,



            Engine = new BlueStar.Core.Models.EngineInfo



            {



                Id = "unity",



                Name = "Unity",



                Type = BlueStar.Core.Models.EngineType.Unity,



                Capabilities = BlueStar.Core.Models.EngineCapabilities.Mods



            }



        };







        var manager = new UnityModManager(NullLogger<UnityModManager>.Instance);



        var mods = await manager.GetInstalledModsAsync(instance);







        mods.Should().HaveCount(1);



        mods[0].Name.Should().Be("Optimal Monopoly Deluxe");



        mods[0].Author.Should().Be("ModCreator");



        mods[0].Description.Should().Be("Best monopoly mod");



        mods[0].Category.Should().Be("Steam Workshop");



        mods[0].IsEnabled.Should().BeTrue();



        mods[0].SizeBytes.Should().BeGreaterThan(0);



    }







    [Fact]



    public async Task GenericModManager_GetInstalledModsAsync_ReadsWorkshopInfo()



    {



        var gameDir = Path.Combine(_tempTestDir, "GenericGame");



        var modsFolder = Path.Combine(gameDir, "mods", "CustomModFolder");



        Directory.CreateDirectory(modsFolder);







        File.WriteAllText(Path.Combine(modsFolder, "mod.pak"), "pakdata");



        File.WriteAllText(Path.Combine(modsFolder, "workshop_info.json"), "{\"Title\":\"Epic Workshop Mod\",\"Author\":\"AuthorGuy\"}");







        var instance = new BlueStar.Core.Models.GameInstance



        {



            Name = "Generic Game",



            AppId = 99999,



            InstallPath = gameDir



        };







        var manager = new GenericModManager(NullLogger<GenericModManager>.Instance);



        var mods = await manager.GetInstalledModsAsync(instance);







        mods.Should().HaveCount(1);



        mods[0].Name.Should().Be("Epic Workshop Mod");



        mods[0].Author.Should().Be("AuthorGuy");



        mods[0].Category.Should().Be("Steam Workshop");



        mods[0].IsEnabled.Should().BeTrue();



    }







    [Fact]



    public void GameModPathResolver_TabletopSimulator_ResolvesDocumentsAndWorkshop()



    {



        var gameDir = Path.Combine(_tempTestDir, "Tabletop Simulator");



        Directory.CreateDirectory(gameDir);







        var instance = new BlueStar.Core.Models.GameInstance



        {



            Name = "Tabletop Simulator",



            AppId = 286160,



            InstallPath = gameDir



        };







        var resolution = GameModPathResolver.ResolveModPaths(instance);







        resolution.GameCategory.Should().Be("TabletopSimulator");



        resolution.PrimaryDirectory.Should().Contain("Tabletop Simulator");



        resolution.PrimaryDirectory.Should().Contain("Workshop");



        resolution.RequiresSpecialDeployment.Should().BeTrue();



    }







    [Fact]



    public void GameModPathResolver_DontStarveTogether_ResolvesRootModsFolder()



    {



        var gameDir = Path.Combine(_tempTestDir, "DontStarveTogether");



        Directory.CreateDirectory(gameDir);







        var instance = new BlueStar.Core.Models.GameInstance



        {



            Name = "Don't Starve Together",



            AppId = 322330,



            InstallPath = gameDir



        };







        var resolution = GameModPathResolver.ResolveModPaths(instance);







        resolution.GameCategory.Should().Be("Klei");



        resolution.PrimaryDirectory.Should().Be(Path.Combine(gameDir, "mods"));



        resolution.RequiresSpecialDeployment.Should().BeTrue();



    }







    [Fact]



    public void GameModPathResolver_Left4Dead2_ResolvesAddonsWorkshop()



    {



        var gameDir = Path.Combine(_tempTestDir, "Left 4 Dead 2");



        var l4d2Dir = Path.Combine(gameDir, "left4dead2");



        Directory.CreateDirectory(l4d2Dir);







        var instance = new BlueStar.Core.Models.GameInstance



        {



            Name = "Left 4 Dead 2",



            AppId = 550,



            InstallPath = gameDir



        };







        var resolution = GameModPathResolver.ResolveModPaths(instance);







        resolution.GameCategory.Should().Be("SourceEngine");



        resolution.PrimaryDirectory.Should().Be(Path.Combine(l4d2Dir, "addons", "workshop"));



        resolution.RequiresSpecialDeployment.Should().BeTrue();



    }







    [Fact]



    public void GameModPathResolver_UnrealPaks_ResolvesTildeMods()



    {



        var gameDir = Path.Combine(_tempTestDir, "UnrealGame");



        var paksDir = Path.Combine(gameDir, "GameName", "Content", "Paks");



        Directory.CreateDirectory(paksDir);







        var instance = new BlueStar.Core.Models.GameInstance



        {



            Name = "Unreal Game",



            AppId = 88888,



            InstallPath = gameDir,



            Engine = new BlueStar.Core.Models.EngineInfo



            {



                Id = "unreal",



                Name = "Unreal Engine",



                Type = BlueStar.Core.Models.EngineType.UnrealEngine



            }



        };







        var resolution = GameModPathResolver.ResolveModPaths(instance);







        resolution.GameCategory.Should().Be("UnrealPaks");



        resolution.PrimaryDirectory.Should().Be(Path.Combine(paksDir, "~mods"));



    }







    [Fact]



    public void GameModPathResolver_AdaptAndDeploy_KleiMod_CreatesWorkshopIdFolder()



    {



        var gameDir = Path.Combine(_tempTestDir, "DST_DeployTest");



        Directory.CreateDirectory(gameDir);







        var stagingDir = Path.Combine(_tempTestDir, "DST_Staging");



        Directory.CreateDirectory(stagingDir);



        File.WriteAllText(Path.Combine(stagingDir, "modinfo.lua"), "name = 'Epic DST Mod'");



        File.WriteAllText(Path.Combine(stagingDir, "modmain.lua"), "-- script");







        var instance = new BlueStar.Core.Models.GameInstance



        {



            Name = "Don't Starve Together",



            AppId = 322330,



            InstallPath = gameDir



        };







        var targetFolder = GameModPathResolver.GetItemTargetFolder(instance, 12345678);



        GameModPathResolver.AdaptAndDeployWorkshopMod(instance, 12345678, stagingDir, targetFolder, null);







        var expectedModDir = Path.Combine(gameDir, "mods", "workshop-12345678");



        Directory.Exists(expectedModDir).Should().BeTrue();



        File.Exists(Path.Combine(expectedModDir, "modinfo.lua")).Should().BeTrue();



        File.Exists(Path.Combine(expectedModDir, "modmain.lua")).Should().BeTrue();



    }







    [Fact]



    public void GameModPathResolver_AdaptAndDeploy_SourceEngine_ExtractsVpkDirectly()



    {



        var gameDir = Path.Combine(_tempTestDir, "L4D2_DeployTest");



        var l4d2Dir = Path.Combine(gameDir, "left4dead2");



        Directory.CreateDirectory(l4d2Dir);







        var stagingDir = Path.Combine(_tempTestDir, "L4D2_Staging");



        Directory.CreateDirectory(stagingDir);



        File.WriteAllText(Path.Combine(stagingDir, "mod.vpk"), "vpk content");







        var instance = new BlueStar.Core.Models.GameInstance



        {



            Name = "Left 4 Dead 2",



            AppId = 550,



            InstallPath = gameDir



        };







        var targetFolder = GameModPathResolver.GetItemTargetFolder(instance, 87654321);



        GameModPathResolver.AdaptAndDeployWorkshopMod(instance, 87654321, stagingDir, targetFolder, null);







        var expectedVpk = Path.Combine(l4d2Dir, "addons", "workshop", "87654321.vpk");



        File.Exists(expectedVpk).Should().BeTrue();



    }







    [Fact]



    public void GameModPathResolver_UnityWithWorkshopAndBepInEx_ResolvesBothDirectories()



    {



        var gameDir = Path.Combine(_tempTestDir, "UnityGameWithBoth");



        Directory.CreateDirectory(Path.Combine(gameDir, "Mods"));



        Directory.CreateDirectory(Path.Combine(gameDir, "BepInEx", "plugins"));







        var instance = new BlueStar.Core.Models.GameInstance



        {



            Name = "Unity Game With Workshop and BepInEx",



            AppId = 777777,



            InstallPath = gameDir,



            Engine = new BlueStar.Core.Models.EngineInfo



            {



                Id = "unity",



                Name = "Unity",



                Type = BlueStar.Core.Models.EngineType.Unity,



                Capabilities = BlueStar.Core.Models.EngineCapabilities.Mods |



                               BlueStar.Core.Models.EngineCapabilities.BepInExSupported |



                               BlueStar.Core.Models.EngineCapabilities.WorkshopSupported



            }



        };







        var resolution = GameModPathResolver.ResolveModPaths(instance);







        resolution.WorkshopDirectory.Should().NotBeNullOrWhiteSpace();



        resolution.WorkshopDirectory.Should().Be(Path.Combine(gameDir, "Mods"));







        resolution.BepInExDirectory.Should().NotBeNullOrWhiteSpace();



        resolution.BepInExDirectory.Should().Be(Path.Combine(gameDir, "BepInEx", "plugins"));







        resolution.ScanDirectories.Should().Contain(resolution.WorkshopDirectory);



        resolution.ScanDirectories.Should().Contain(resolution.BepInExDirectory);







        // Workshop item deployment must target the Workshop directory, not BepInEx



        var targetFolder = GameModPathResolver.GetItemTargetFolder(instance, 12345, "Cool Mod");



        targetFolder.Should().StartWith(resolution.WorkshopDirectory);



        targetFolder.Should().NotContain("BepInEx");



    }







    [Fact]



    public async Task UnityModManager_GetInstalledModsAsync_DiscoversModsFromBothWorkshopAndBepInExDirectories()



    {



        var gameDir = Path.Combine(_tempTestDir, "UnityDualMods");



        var workshopFolder = Path.Combine(gameDir, "Mods", "WorkshopItem123");



        var bepPluginsFolder = Path.Combine(gameDir, "BepInEx", "plugins");



        Directory.CreateDirectory(workshopFolder);



        Directory.CreateDirectory(bepPluginsFolder);







        // 1. Mod in Workshop/Native folder



        File.WriteAllText(Path.Combine(workshopFolder, "workshop_info.json"),



            """{"Title":"Workshop Super Mod","Author":"Community"}""");







        // 2. Plugin in BepInEx folder



        File.WriteAllText(Path.Combine(bepPluginsFolder, "CoolPlugin.dll"), "dll_content");







        var instance = new BlueStar.Core.Models.GameInstance



        {



            Name = "Unity Dual Mod Game",



            AppId = 888888,



            InstallPath = gameDir,



            Engine = new BlueStar.Core.Models.EngineInfo



            {



                Id = "unity",



                Name = "Unity",



                Type = BlueStar.Core.Models.EngineType.Unity,



                Capabilities = BlueStar.Core.Models.EngineCapabilities.Mods |



                               BlueStar.Core.Models.EngineCapabilities.BepInExSupported |



                               BlueStar.Core.Models.EngineCapabilities.WorkshopSupported



            }



        };







        var manager = new UnityModManager(NullLogger<UnityModManager>.Instance);



        var mods = await manager.GetInstalledModsAsync(instance);







        mods.Should().HaveCount(2);







        var workshopMod = mods.FirstOrDefault(m => m.Name == "Workshop Super Mod");



        workshopMod.Should().NotBeNull();



        workshopMod!.Category.Should().Be("Steam Workshop");



        workshopMod.FilePath.Should().StartWith(Path.Combine(gameDir, "Mods"));







        var bepPlugin = mods.FirstOrDefault(m => m.Name == "CoolPlugin");



        bepPlugin.Should().NotBeNull();



        bepPlugin!.Category.Should().Contain("BepInEx");



        bepPlugin.FilePath.Should().StartWith(Path.Combine(gameDir, "BepInEx", "plugins"));



    }




        [Fact]
    public async Task UnityModManager_GetInstalledModsAsync_DiscoversMultiExtensionWorkshopFiles()
    {
        var gameDir = Path.Combine(_tempTestDir, "Unity_Test_MultiExt");
        var wsDir = Path.Combine(gameDir, "Mods", "Workshop");
        Directory.CreateDirectory(wsDir);

        // 1. Mod with .json save and companion .png preview
        File.WriteAllText(Path.Combine(wsDir, "10001.json"), "{\"SaveName\":\"Cool Board Game\"}");
        File.WriteAllBytes(Path.Combine(wsDir, "10001.png"), new byte[] { 1, 2, 3 });

        // 2. Mod with only .png file (e.g. custom card deck or mat)
        File.WriteAllBytes(Path.Combine(wsDir, "10002.png"), new byte[] { 4, 5, 6 });

        // 3. Mod with .cjc format
        File.WriteAllText(Path.Combine(wsDir, "10003.cjc"), "custom game data");

        // 4. Mod with .obj 3D model
        File.WriteAllText(Path.Combine(wsDir, "10004.obj"), "v 0 0 0");

        // 5. WorkshopFileInfos.json index mapping
        var indexJson = @"[
            { ""Directory"": ""Workshop/10001.json"", ""Name"": ""Cool Board Game"", ""Id"": ""10001"" },
            { ""Directory"": ""Workshop/10002.png"", ""Name"": ""Custom Poker Deck"", ""Id"": ""10002"" },
            { ""Directory"": ""Workshop/10003.cjc"", ""Name"": ""Custom Chess Set"", ""Id"": ""10003"" }
        ]";
        File.WriteAllText(Path.Combine(wsDir, "WorkshopFileInfos.json"), indexJson);

        var instance = new BlueStar.Core.Models.GameInstance
        {
            Id = Guid.NewGuid(),
            Name = "Tabletop Game Simulator",
            AppId = 987654,
            InstallPath = gameDir,
            Engine = new BlueStar.Core.Models.EngineInfo { Id = "unity", Name = "Unity", Type = BlueStar.Core.Models.EngineType.Unity }
        };

        var manager = new UnityModManager(NullLogger<UnityModManager>.Instance);
        var mods = await manager.GetInstalledModsAsync(instance);

        // Should discover 10001 (.json), 10002 (.png), 10003 (.cjc), 10004 (.obj)
        // And should NOT produce a duplicate for 10001.png because it's a thumbnail companion of 10001.json!
        mods.Should().HaveCount(4);

        var m1 = mods.FirstOrDefault(m => m.Id == "10001.json");
        m1.Should().NotBeNull();
        m1!.Name.Should().Be("Cool Board Game");
        m1.IconPath.Should().EndWith("10001.png");

        var m2 = mods.FirstOrDefault(m => m.Id == "10002.png");
        m2.Should().NotBeNull();
        m2!.Name.Should().Be("Custom Poker Deck");
        m2.IconPath.Should().EndWith("10002.png");

        var m3 = mods.FirstOrDefault(m => m.Id == "10003.cjc");
        m3.Should().NotBeNull();
        m3!.Name.Should().Be("Custom Chess Set");

        var m4 = mods.FirstOrDefault(m => m.Id == "10004.obj");
        m4.Should().NotBeNull();
        m4!.Name.Should().Be("Workshop Item #10004");
    }

    [Fact]
    public async Task UnityModManager_GetInstalledModsAsync_DiscoversRealTabletopSimulatorWorkshopFiles()
    {
        var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var ttsWorkshop = Path.Combine(docs, "My Games", "Tabletop Simulator", "Mods", "Workshop");
        if (!Directory.Exists(ttsWorkshop) || !Directory.EnumerateFiles(ttsWorkshop, "*.png", SearchOption.AllDirectories).Any()) return; // Skip if machine doesn't have TTS workshop files

        var instance = new BlueStar.Core.Models.GameInstance
        {
            Id = Guid.NewGuid(),
            Name = "Tabletop Simulator",
            AppId = 286160,
            InstallPath = @"D:\Games\Tabletop Simulator",
            Engine = new BlueStar.Core.Models.EngineInfo { Id = "unity", Name = "Unity", Type = BlueStar.Core.Models.EngineType.Unity }
        };

        var manager = new UnityModManager(NullLogger<UnityModManager>.Instance);
        var mods = await manager.GetInstalledModsAsync(instance);

        mods.Should().NotBeEmpty();
        mods.Should().Contain(m => m.Category == "Steam Workshop" || m.Category == "Tabletop Simulator Mod");
        
        // Should find the installed Workshop items
        var itemWithPng = mods.FirstOrDefault(m => !string.IsNullOrEmpty(m.IconPath) && m.IconPath.EndsWith(".png", StringComparison.OrdinalIgnoreCase));
        itemWithPng.Should().NotBeNull();
        itemWithPng!.IconPath.Should().NotBeNullOrEmpty();
    }
}
