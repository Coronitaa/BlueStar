using System;
using System.Collections.Generic;
using System.Text.Json;
using BlueStar.Core.Models;
using Xunit;

namespace BlueStar.Core.Tests;

public class GameFixModelTests
{
    [Fact]
    public void GameFixInfo_TagProperties_ReturnExpectedValues()
    {
        var fix = new GameFixInfo
        {
            Id = "game_bypass_hypervisor",
            Name = "Super Game Fix",
            DownloadName = "Super_Game_Fix.zip",
            Tags = ["bypass", "hypervisor", "online"],
            SizeBytes = 15_728_640 // 15 MB
        };

        Assert.True(fix.IsBypass);
        Assert.True(fix.IsHypervisor);
        Assert.True(fix.IsOnline);
        Assert.Equal("BYPASS + HYPERVISOR + ONLINE", fix.TagsSummary);
        Assert.Equal("15.0 MB", fix.FormattedSize);
    }

    [Fact]
    public void GameFixInfo_SingleTag_And_SizeFormatting()
    {
        var fix = new GameFixInfo
        {
            Id = "game_bypass",
            Name = "Bypass Only",
            DownloadName = "Bypass_Only.zip",
            Tags = ["bypass"],
            SizeBytes = 2_147_483_648 // 2 GB
        };

        Assert.True(fix.IsBypass);
        Assert.False(fix.IsHypervisor);
        Assert.False(fix.IsOnline);
        Assert.Equal("BYPASS", fix.TagsSummary);
        Assert.Equal("2.00 GB", fix.FormattedSize);
    }

    [Fact]
    public void FixLayerInfo_Serialization_And_Deserialization()
    {
        var layer = new FixLayerInfo
        {
            LayerId = "depotbox_fix_123_456",
            FixId = "fix_123",
            SourceType = "depotbox_gamefix",
            DisplayName = "Custom Hypervisor Fix",
            Tags = ["hypervisor", "bypass"],
            DeployedFiles = ["steam_api64.dll", "hypervisor.ini", "winmm.dll"],
            BackupDirectory = @"C:\AppData\BlueStar\fix_backups\test\layer1",
            Version = "1.0",
            InstalledAt = DateTimeOffset.UtcNow
        };

        var json = JsonSerializer.Serialize(layer);
        var deserialized = JsonSerializer.Deserialize<FixLayerInfo>(json);

        Assert.NotNull(deserialized);
        Assert.Equal(layer.LayerId, deserialized.LayerId);
        Assert.Equal(layer.FixId, deserialized.FixId);
        Assert.Equal(layer.SourceType, deserialized.SourceType);
        Assert.Equal(layer.DisplayName, deserialized.DisplayName);
        Assert.Equal(3, deserialized.DeployedFiles.Count);
        Assert.True(deserialized.IsHypervisor);
        Assert.True(deserialized.IsBypass);
        Assert.False(deserialized.IsOnline);
    }

    [Fact]
    public void GameInstance_With_InstalledFixLayers_Supports_MultiLayerStack()
    {
        var layer1 = new FixLayerInfo
        {
            LayerId = "layer_bypass",
            SourceType = "depotbox_gamefix",
            DisplayName = "Bypass Layer",
            Tags = ["bypass"],
            DeployedFiles = ["bypass.dll"]
        };

        var layer2 = new FixLayerInfo
        {
            LayerId = "layer_hypervisor",
            SourceType = "depotbox_gamefix",
            DisplayName = "Hypervisor Layer",
            Tags = ["hypervisor"],
            DeployedFiles = ["hypervisor.dll"]
        };

        var instance = new GameInstance
        {
            Name = "Test Game",
            InstallPath = @"C:\Games\TestGame",
            InstalledFixLayers = [layer1, layer2]
        };

        Assert.Equal(2, instance.InstalledFixLayers.Count);
        Assert.Equal("layer_bypass", instance.InstalledFixLayers[0].LayerId);
        Assert.Equal("layer_hypervisor", instance.InstalledFixLayers[1].LayerId);

        // Verify JSON roundtrip of GameInstance with InstalledFixLayers
        var json = JsonSerializer.Serialize(instance);
        var restored = JsonSerializer.Deserialize<GameInstance>(json);

        Assert.NotNull(restored);
        Assert.Equal(2, restored.InstalledFixLayers.Count);
        Assert.Equal("Bypass Layer", restored.InstalledFixLayers[0].DisplayName);
        Assert.Equal("Hypervisor Layer", restored.InstalledFixLayers[1].DisplayName);
    }
}
