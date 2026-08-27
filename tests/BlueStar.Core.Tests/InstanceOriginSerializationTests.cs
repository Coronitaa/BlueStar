using System;
using System.Text.Json;
using BlueStar.Core.Models;
using FluentAssertions;
using Xunit;

namespace BlueStar.Core.Tests;

public class InstanceOriginSerializationTests
{
    [Fact]
    public void GameInstance_SerializesAndDeserializes_OriginAndDlcFields()
    {
        var original = new GameInstance
        {
            Id = Guid.NewGuid(),
            Name = "Cyberpunk 2077",
            AppId = 1091500,
            InstallPath = @"C:\Games\Cyberpunk2077",
            Origin = InstanceOrigin.ImportedFolder,
            IsDepotBoxAssociated = true,
            DlcUnlockerInstalled = true,
            UnlockedDlcIds = [1091501, 1091502],
            Status = InstanceStatus.Ready
        };

        var json = JsonSerializer.Serialize(original);
        var deserialized = JsonSerializer.Deserialize<GameInstance>(json);

        deserialized.Should().NotBeNull();
        deserialized!.Origin.Should().Be(InstanceOrigin.ImportedFolder);
        deserialized.IsDepotBoxAssociated.Should().BeTrue();
        deserialized.DlcUnlockerInstalled.Should().BeTrue();
        deserialized.UnlockedDlcIds.Should().BeEquivalentTo(new uint[] { 1091501, 1091502 });
    }

    [Fact]
    public void GameInstance_DefaultsOriginToDepotBox_ForLegacyJson()
    {
        // Legacy JSON without Origin or new fields
        var legacyJson = """
        {
            "Id": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
            "Name": "Legacy Game",
            "AppId": 480,
            "InstallPath": "C:\\Games\\LegacyGame",
            "Status": 1
        }
        """;

        var deserialized = JsonSerializer.Deserialize<GameInstance>(legacyJson);

        deserialized.Should().NotBeNull();
        deserialized!.Origin.Should().Be(InstanceOrigin.DepotBox);
        deserialized.IsDepotBoxAssociated.Should().BeFalse();
        deserialized.DlcUnlockerInstalled.Should().BeFalse();
        deserialized.UnlockedDlcIds.Should().BeEmpty();
    }
}
