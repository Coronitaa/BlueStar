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
}
