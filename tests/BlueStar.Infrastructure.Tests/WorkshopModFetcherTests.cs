using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using BlueStar.Core.Interfaces;
using BlueStar.Core.Models;
using BlueStar.Infrastructure.Workshop;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace BlueStar.Infrastructure.Tests;

public sealed class WorkshopModFetcherTests : IDisposable
{
    private const uint AppId = 4704690;
    private const ulong ItemId = 3100200300;

    private readonly string _cacheRoot = Path.Combine(Path.GetTempPath(), "bluestar_wscache_" + Guid.NewGuid().ToString("N"));
    private readonly Mock<IWorkshopService> _workshop = new();
    private int _downloads;
    private DateTimeOffset _updatedAt = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    public WorkshopModFetcherTests()
    {
        _workshop.Setup(w => w.GetItemDetailsAsync(ItemId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => Details(AppId));
        _workshop.Setup(w => w.DownloadItemContentAsync(It.IsAny<uint>(), ItemId, It.IsAny<string>(),
                It.IsAny<IProgress<double>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((uint _, ulong _, string dir, IProgress<double>? _, CancellationToken _) =>
            {
                _downloads++;
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, "item.pak"), $"download {_downloads}");
                return new WorkshopDownloadResult(true, Details(AppId));
            });
    }

    public void Dispose()
    {
        try { Directory.Delete(_cacheRoot, recursive: true); } catch (IOException) { }
    }

    private WorkshopItemInfo Details(uint appId) =>
        new(ItemId, appId, "Cool Item", "", null, 10, "author", _updatedAt);

    private WorkshopModFetcher CreateFetcher() =>
        new(_workshop.Object, NullLogger<WorkshopModFetcher>.Instance, _cacheRoot);

    [Fact]
    public async Task FetchAsync_DownloadsIntoCache_AndBuildsWorkshopPackage()
    {
        // Act
        var result = await CreateFetcher().FetchAsync(AppId, ItemId);

        // Assert
        result.Succeeded.Should().BeTrue();
        var package = result.Package!;
        package.Layout.Should().Be(ModPackageLayout.SteamWorkshop);
        package.WorkshopItemId.Should().Be(ItemId);
        package.Source.LedgerId.Should().Be($"workshop:{ItemId}");
        package.Source.Version.Should().Be("2026-10-01");
        File.Exists(Path.Combine(package.Path, "item.pak")).Should().BeTrue();
        package.Path.Should().StartWith(_cacheRoot);
    }

    [Fact]
    public async Task FetchAsync_ReusesCache_UntilSteamReportsANewerVersion()
    {
        // Arrange
        var fetcher = CreateFetcher();
        await fetcher.FetchAsync(AppId, ItemId);

        // Act: same version
        await fetcher.FetchAsync(AppId, ItemId);

        // Assert
        _downloads.Should().Be(1);

        // Act: the author updated the item
        _updatedAt = _updatedAt.AddDays(3);
        var updated = await fetcher.FetchAsync(AppId, ItemId);

        // Assert
        _downloads.Should().Be(2);
        File.ReadAllText(Path.Combine(updated.Package!.Path, "item.pak")).Should().Be("download 2");
    }

    [Fact]
    public async Task FetchAsync_ItemOfAnotherGame_IsRejectedWithoutDownloading()
    {
        // Act
        var result = await CreateFetcher().FetchAsync(appId: 480, ItemId);

        // Assert
        result.Succeeded.Should().BeFalse();
        result.Error.Should().Contain(AppId.ToString());
        _downloads.Should().Be(0);
    }

    [Fact]
    public async Task FetchAsync_FailedDownload_ReportsErrorAndLeavesNoCache()
    {
        // Arrange
        _workshop.Setup(w => w.DownloadItemContentAsync(It.IsAny<uint>(), ItemId, It.IsAny<string>(),
                It.IsAny<IProgress<double>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new WorkshopDownloadResult(false, Error: "Access denied"));

        // Act
        var result = await CreateFetcher().FetchAsync(AppId, ItemId);

        // Assert
        result.Succeeded.Should().BeFalse();
        result.Error.Should().Be("Access denied");
        Directory.Exists(Path.Combine(_cacheRoot, AppId.ToString())).Should().BeFalse();
    }
}
