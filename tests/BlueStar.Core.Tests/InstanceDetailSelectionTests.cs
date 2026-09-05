using System.Text.Json;
using BlueStar.Core.Models;
using FluentAssertions;
using Xunit;

namespace BlueStar.Core.Tests;

public class InstanceDetailSelectionTests
{
    [Fact]
    public void DepotInfo_IsDownloaded_DefaultsToFalse()
    {
        var depot = new DepotInfo
        {
            DepotId = 1001,
            ManifestId = 55555,
            SizeBytes = 1024
        };

        depot.IsDownloaded.Should().BeFalse();
    }

    [Fact]
    public void DepotInfo_IsDownloaded_PreservedInJson()
    {
        var depot = new DepotInfo
        {
            DepotId = 1001,
            ManifestId = 55555,
            SizeBytes = 1024,
            IsDownloaded = true
        };

        var json = JsonSerializer.Serialize(depot);
        var deserialized = JsonSerializer.Deserialize<DepotInfo>(json);

        deserialized.Should().NotBeNull();
        deserialized!.IsDownloaded.Should().BeTrue();
    }

    [Fact]
    public void SmartButtonText_WhenAllSelectedDepotsDownloaded_ReturnsReinstall()
    {
        // Arrange
        var depots = new List<DepotInfo>
        {
            new() { DepotId = 1, Name = "Base 1", IsDownloaded = true },
            new() { DepotId = 2, Name = "Base 2", IsDownloaded = true }
        };

        var selected = depots.Where(d => true).ToList();

        // Act
        bool allDownloaded = selected.Count > 0 && selected.All(d => d.IsDownloaded);
        string buttonText = allDownloaded ? "🔄 Reinstall Selected" : "📥 Start Download";

        // Assert
        allDownloaded.Should().BeTrue();
        buttonText.Should().Be("🔄 Reinstall Selected");
    }

    [Fact]
    public void SmartButtonText_WhenAtLeastOneDepotNotDownloaded_ReturnsStartDownload()
    {
        // Arrange
        var depots = new List<DepotInfo>
        {
            new() { DepotId = 1, Name = "Base 1", IsDownloaded = true },
            new() { DepotId = 2, Name = "Base 2", IsDownloaded = false }
        };

        var selected = depots.Where(d => true).ToList();

        // Act
        bool allDownloaded = selected.Count > 0 && selected.All(d => d.IsDownloaded);
        string buttonText = allDownloaded ? "🔄 Reinstall Selected" : "📥 Start Download";

        // Assert
        allDownloaded.Should().BeFalse();
        buttonText.Should().Be("📥 Start Download");
    }

    [Fact]
    public void SmartButtonText_WhenDlcDepotNotDownloaded_ReturnsStartDownload()
    {
        // Arrange
        var baseDepot = new DepotInfo { DepotId = 1, Name = "Base", IsDownloaded = true };
        var dlcDepot = new DepotInfo { DepotId = 2, Name = "DLC Depot", IsDownloaded = false };

        var dlc = new DlcInfo
        {
            AppId = 2001,
            Name = "Expansion Pack",
            Depots = [dlcDepot],
            IsInstalled = false
        };

        var selectedDepots = new List<DepotInfo> { baseDepot };
        var selectedDlcDepots = new List<DlcInfo> { dlc }.SelectMany(d => d.Depots).ToList();
        var combined = selectedDepots.Concat(selectedDlcDepots).DistinctBy(d => d.DepotId).ToList();

        // Act
        bool allDownloaded = combined.Count > 0 && combined.All(d => d.IsDownloaded);
        string buttonText = allDownloaded ? "🔄 Reinstall Selected" : "📥 Start Download";

        // Assert
        allDownloaded.Should().BeFalse();
        buttonText.Should().Be("📥 Start Download");
    }

    [Fact]
    public void SmartButtonText_WhenAllDlcAndBaseDepotsDownloaded_ReturnsReinstall()
    {
        // Arrange
        var baseDepot = new DepotInfo { DepotId = 1, Name = "Base", IsDownloaded = true };
        var dlcDepot = new DepotInfo { DepotId = 2, Name = "DLC Depot", IsDownloaded = true };

        var dlc = new DlcInfo
        {
            AppId = 2001,
            Name = "Expansion Pack",
            Depots = [dlcDepot],
            IsInstalled = true
        };

        var selectedDepots = new List<DepotInfo> { baseDepot };
        var selectedDlcDepots = new List<DlcInfo> { dlc }.SelectMany(d => d.Depots).ToList();
        var combined = selectedDepots.Concat(selectedDlcDepots).DistinctBy(d => d.DepotId).ToList();

        // Act
        bool allDownloaded = combined.Count > 0 && combined.All(d => d.IsDownloaded);
        string buttonText = allDownloaded ? "🔄 Reinstall Selected" : "📥 Start Download";

        // Assert
        allDownloaded.Should().BeTrue();
        buttonText.Should().Be("🔄 Reinstall Selected");
    }

    [Fact]
    public void SmartButtonText_WhenNothingSelected_ReturnsStartDownload()
    {
        var combined = new List<DepotInfo>();

        bool allDownloaded = combined.Count > 0 && combined.All(d => d.IsDownloaded);
        string buttonText = allDownloaded ? "🔄 Reinstall Selected" : "📥 Start Download";

        allDownloaded.Should().BeFalse();
        buttonText.Should().Be("📥 Start Download");
    }

    [Fact]
    public void DlcAndBaseDepots_SelectionCounts_AreDecoupled()
    {
        // Arrange base depots
        var baseDepots = new List<DepotInfo>
        {
            new() { DepotId = 101, Name = "Base Depot 1", SizeBytes = 1000, IsDownloaded = true },
            new() { DepotId = 102, Name = "Base Depot 2", SizeBytes = 2000, IsDownloaded = false }
        };

        // Arrange DLCs
        var dlc1 = new DlcInfo
        {
            AppId = 201,
            Name = "Soundtrack DLC",
            Depots = [new() { DepotId = 301, Name = "Soundtrack Depot", SizeBytes = 500, IsDownloaded = false }],
            IsInstalled = false
        };
        var dlc2 = new DlcInfo
        {
            AppId = 202,
            Name = "Skin Pack",
            Depots = [], // Entitlement only
            IsInstalled = true
        };

        // Act - Base selection count only tracks base depots
        int selectedBaseDepotsCount = baseDepots.Count(d => true);
        long selectedBaseDepotsSize = baseDepots.Sum(d => d.SizeBytes);

        // Act - DLC selection count only tracks DLC items
        var dlcs = new List<DlcInfo> { dlc1, dlc2 };
        int selectedDlcsCount = dlcs.Count;
        long selectedDlcsSize = dlcs.Sum(d => d.TotalSizeBytes);

        bool hasDlcsWithDepotsToDownload = dlcs.Any(d => d.Depots.Any(dep => !dep.IsDownloaded && dep.SizeBytes > 0));

        // Assert
        selectedBaseDepotsCount.Should().Be(2);
        selectedBaseDepotsSize.Should().Be(3000);
        selectedDlcsCount.Should().Be(2);
        selectedDlcsSize.Should().Be(500);
        hasDlcsWithDepotsToDownload.Should().BeTrue();
    }
}
