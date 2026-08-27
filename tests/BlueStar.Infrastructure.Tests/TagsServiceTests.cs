using System;
using System.Collections.Generic;
using BlueStar.Core.Models;
using BlueStar.Infrastructure.Services;
using FluentAssertions;
using Xunit;

namespace BlueStar.Infrastructure.Tests;

public class TagsServiceTests
{
    private readonly TagsService _tagsService = new();

    [Fact]
    public void GetExploreTags_ReturnsCorrectTags_ForRichMetadata()
    {
        var item = new SearchResult
        {
            AppId = 730,
            Name = "Counter-Strike 2",
            AppType = "Game",
            HasWindows = true,
            HasLinux = true,
            HasMac = false,
            DlcCount = 5,
            HasDrm = true,
            DrmNotice = "Valve Anti-Cheat",
            IsNsfw = false,
            IsAvailable = true
        };

        var tags = _tagsService.GetExploreTags(item);

        tags.Should().Contain(t => t.Text == "Game" && t.Type == TagType.AppType);
        tags.Should().Contain(t => t.Text == "Windows" && t.Type == TagType.Platform);
        tags.Should().Contain(t => t.Text == "Linux" && t.Type == TagType.Platform);
        tags.Should().NotContain(t => t.Text == "macOS");
        tags.Should().Contain(t => t.Text == "5 DLCs" && t.Type == TagType.DlcCount);
        tags.Should().Contain(t => t.Text.Contains("DRM") && t.Type == TagType.Drm);
    }

    [Fact]
    public void GetInstanceTags_ExcludesDateTags_AndIncludesOriginAndEngine()
    {
        var instance = new GameInstance
        {
            Name = "Imported Unreal Game",
            InstallPath = @"C:\Games\UnrealGame",
            Origin = InstanceOrigin.ImportedFolder,
            IsDepotBoxAssociated = false,
            Status = InstanceStatus.Ready,
            Engine = new EngineInfo { Id = "unreal-5", Name = "Unreal Engine 5", Type = EngineType.UnrealEngine, Version = "5.4.0" },
            Dlcs = [new DlcInfo { AppId = 12345, Name = "DLC 1", Depots = [], IsInstalled = true }]
        };

        var tags = _tagsService.GetInstanceTags(instance, hasUpdateAvailable: true);

        // Must NOT contain any date formatted strings
        tags.Should().NotContain(t => t.Text.Contains("202"));

        // Must contain Imported badge
        tags.Should().Contain(t => t.Text == "Imported" && t.Type == TagType.Origin);

        // Must contain Engine badge
        tags.Should().Contain(t => t.Text.Contains("Unreal Engine 5") && t.Type == TagType.Engine);

        // Must contain Update Available
        tags.Should().Contain(t => t.Text == "Update Available" && t.Type == TagType.UpdateAvailable);

        // Must contain DLCs count
        tags.Should().Contain(t => t.Text.Contains("1 DLC") && t.Type == TagType.DlcCount);
    }

    [Fact]
    public void GetInstanceTags_ForSteamGame_ShowsSteamBadge()
    {
        var instance = new GameInstance
        {
            Name = "Steam Game",
            InstallPath = @"C:\Games\SteamGame",
            Origin = InstanceOrigin.Steam,
            Status = InstanceStatus.Ready
        };

        var tags = _tagsService.GetInstanceTags(instance, hasUpdateAvailable: false);

        tags.Should().Contain(t => t.Text == "Steam Game" && t.Type == TagType.Origin);
    }

    [Fact]
    public void GetInstanceDetailHeroTags_IncludesOriginEngineStatusAndDlcs()
    {
        var instance = new GameInstance
        {
            Name = "Hollow Knight",
            InstallPath = @"C:\Games\HollowKnight",
            Origin = InstanceOrigin.DepotBox,
            Status = InstanceStatus.Ready,
            Engine = new EngineInfo { Id = "unity", Name = "Unity", Type = EngineType.Unity, Version = "2021.3" },
            Dlcs = [new DlcInfo { AppId = 1, Name = "Godmaster", Depots = [], IsInstalled = true }]
        };

        var heroTags = _tagsService.GetInstanceDetailHeroTags(instance, hasUpdateAvailable: false);

        heroTags.Should().Contain(t => t.Text.Contains("Unity") && t.Type == TagType.Engine);
        heroTags.Should().Contain(t => t.Text == "Ready" && t.Type == TagType.Status);
        heroTags.Should().Contain(t => t.Text.Contains("1 DLC") && t.Type == TagType.DlcCount);
    }
}
