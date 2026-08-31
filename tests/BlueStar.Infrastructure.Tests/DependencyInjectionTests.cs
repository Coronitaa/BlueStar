using System;
using BlueStar.Core.Interfaces;
using BlueStar.Infrastructure.Downloader;
using BlueStar.Infrastructure.Emulators;
using BlueStar.Infrastructure.Engine;
using BlueStar.Infrastructure.Mods;
using BlueStar.Infrastructure.Steam;
using BlueStar.Infrastructure.Storage;
using BlueStar.Infrastructure.Workshop;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace BlueStar.Infrastructure.Tests;

public class DependencyInjectionTests
{
    [Fact]
    public void ServiceProvider_ResolvesAllInstanceDetailDependencies()
    {
        var services = new ServiceCollection();

        // Core Logging
        services.AddLogging();
        services.AddHttpClient();

        // Mock external storage/download managers
        services.AddSingleton<IInstanceManager>(new Mock<IInstanceManager>().Object);
        services.AddSingleton<IDlcInstaller>(new Mock<IDlcInstaller>().Object);
        services.AddSingleton<AppSettingsService>();

        // Modding, engine, workshop, emulators
        services.AddSingleton<IEngineDetector, EngineDetector>();
        services.AddHttpClient<IBepInExService, BepInExService>();
        services.AddHttpClient<IWorkshopService, WorkshopService>();
        services.AddSingleton<ISteamStatusService, SteamStatusService>();
        services.AddSingleton<IModManager, UnrealModManager>();
        services.AddSingleton<IModManager, UnityModManager>();
        services.AddSingleton<IModManager, GenericModManager>();
        services.AddSingleton<IModManagerRegistry, ModManagerRegistry>();
        services.AddSingleton<IEmulator, ReFixEmulator>();
        services.AddSingleton<IEmulator, SmokeApiEmulator>();
        services.AddSingleton<IEmulatorRegistry, EmulatorRegistry>();
        services.AddSingleton<IEmulatorRatingService, EmulatorRatingService>();
        services.AddSingleton<IGameLauncher>(new Mock<IGameLauncher>().Object);

        var provider = services.BuildServiceProvider();

        // Act & Assert
        provider.GetRequiredService<IBepInExService>().Should().NotBeNull();
        provider.GetRequiredService<IWorkshopService>().Should().NotBeNull();
        provider.GetRequiredService<IEmulatorRatingService>().Should().NotBeNull();
        provider.GetRequiredService<IEngineDetector>().Should().NotBeNull();
        provider.GetRequiredService<IEmulatorRegistry>().Should().NotBeNull();
    }

    [Fact]
    public void ReFixEmulator_GetReFixDeployPath_FindsIntegratedToolsPath()
    {
        var deployPath = ReFixEmulator.GetReFixDeployPath();
        deployPath.Should().NotBeNullOrWhiteSpace();
        File.Exists(System.IO.Path.Combine(deployPath!, "bin", "steam_api64.dll")).Should().BeTrue();
    }

    [Fact]
    public async Task EngineDetector_DetectsGenericEngineWithModsAndWorkshopCapabilities()
    {
        var detector = new EngineDetector(NullLogger<EngineDetector>.Instance);
        var tempDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var engine = await detector.DetectEngineAsync(tempDir);
            engine.Should().NotBeNull();
            engine.Supports(Core.Models.EngineCapabilities.Mods).Should().BeTrue();
            engine.Supports(Core.Models.EngineCapabilities.WorkshopSupported).Should().BeTrue();
            engine.Supports(Core.Models.EngineCapabilities.Emulation).Should().BeTrue();
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void XamlResourceAudit_AllStaticResources_ExistInThemeDictionaries()
    {
        // Find solution root
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        string? appDir = null;
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "src", "BlueStar.App");
            if (Directory.Exists(candidate))
            {
                appDir = candidate;
                break;
            }
            dir = dir.Parent;
        }

        if (appDir == null) return;

        var xamlFiles = Directory.GetFiles(appDir, "*.xaml", SearchOption.AllDirectories);
        var definedKeys = new HashSet<string>(StringComparer.Ordinal);

        var keyRegex = new System.Text.RegularExpressions.Regex(@"x:Key=""([^""]+)""");
        var staticResourceRegex = new System.Text.RegularExpressions.Regex(@"StaticResource\s+([a-zA-Z0-9_]+)");
        var dynamicResourceRegex = new System.Text.RegularExpressions.Regex(@"DynamicResource\s+([a-zA-Z0-9_]+)");

        foreach (var file in xamlFiles)
        {
            var content = File.ReadAllText(file);
            foreach (System.Text.RegularExpressions.Match match in keyRegex.Matches(content))
            {
                definedKeys.Add(match.Groups[1].Value);
            }
        }

        var missing = new List<string>();
        foreach (var file in xamlFiles)
        {
            var content = File.ReadAllText(file);
            foreach (System.Text.RegularExpressions.Match match in staticResourceRegex.Matches(content))
            {
                var resourceKey = match.Groups[1].Value;
                if (!definedKeys.Contains(resourceKey))
                {
                    missing.Add($"[Static] {Path.GetFileName(file)} -> {resourceKey}");
                }
            }

            foreach (System.Text.RegularExpressions.Match match in dynamicResourceRegex.Matches(content))
            {
                var resourceKey = match.Groups[1].Value;
                if (!definedKeys.Contains(resourceKey))
                {
                    missing.Add($"[Dynamic] {Path.GetFileName(file)} -> {resourceKey}");
                }
            }
        }

        missing.Should().BeEmpty("all StaticResources and DynamicResources referenced in XAML must be defined in theme dictionaries");
    }

    [Theory]
    [InlineData("https://steamcommunity.com/sharedfiles/filedetails/?id=3732511659", 3732511659UL)]
    [InlineData("steamcommunity.com/sharedfiles/filedetails/?id=3732511659", 3732511659UL)]
    [InlineData("ncommunity.com/sharedfiles/filedetails/?id=3732511659", 3732511659UL)]
    [InlineData("https://steamcommunity.com/sharedfiles/filedetails/?id=3732511659&searchtext=plant", 3732511659UL)]
    [InlineData("3732511659", 3732511659UL)]
    public void WorkshopService_ParsePublishedFileId_ExtractsCorrectId(string input, ulong expectedId)
    {
        var service = new WorkshopService(new System.Net.Http.HttpClient(), NullLogger<WorkshopService>.Instance);
        var parsed = service.ParsePublishedFileId(input);
        parsed.Should().Be(expectedId);
    }
}
