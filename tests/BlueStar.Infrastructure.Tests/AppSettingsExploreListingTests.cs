using System;
using System.IO;
using System.Threading.Tasks;
using BlueStar.Infrastructure.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BlueStar.Infrastructure.Tests;

public class AppSettingsExploreListingTests : IDisposable
{
    private readonly string _tempFile;

    public AppSettingsExploreListingTests()
    {
        _tempFile = Path.Combine(Path.GetTempPath(), $"settings_test_{Guid.NewGuid():N}.json");
    }

    public void Dispose()
    {
        try { if (File.Exists(_tempFile)) File.Delete(_tempFile); } catch { }
    }

    [Fact]
    public void DefaultExploreListing_HasExpectedDefaultValues()
    {
        var service = new AppSettingsService(NullLogger<AppSettingsService>.Instance, _tempFile);

        Assert.Equal("Reviews", service.DefaultExploreSort);
        Assert.True(service.DefaultExploreSortDescending);
        Assert.Equal("popularnew", service.DefaultExploreStoreList);
    }

    [Fact]
    public async Task SetDefaultExploreListing_PersistsAndFiresEvent()
    {
        var service = new AppSettingsService(NullLogger<AppSettingsService>.Instance, _tempFile);
        var eventFired = 0;
        service.SettingsChanged += (_, _) => eventFired++;

        await service.SetDefaultExploreSortAsync("Price");
        await service.SetDefaultExploreSortDescendingAsync(false);
        await service.SetDefaultExploreStoreListAsync("globaltopsellers");

        Assert.Equal("Price", service.DefaultExploreSort);
        Assert.False(service.DefaultExploreSortDescending);
        Assert.Equal("globaltopsellers", service.DefaultExploreStoreList);
        Assert.Equal(3, eventFired);

        // Reload from disk to verify persistence
        var reloaded = new AppSettingsService(NullLogger<AppSettingsService>.Instance, _tempFile);
        Assert.Equal("Price", reloaded.DefaultExploreSort);
        Assert.False(reloaded.DefaultExploreSortDescending);
        Assert.Equal("globaltopsellers", reloaded.DefaultExploreStoreList);
    }
}
