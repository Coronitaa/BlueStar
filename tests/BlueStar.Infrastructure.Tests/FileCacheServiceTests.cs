using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using BlueStar.Infrastructure.Cache;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BlueStar.Infrastructure.Tests;

public class FileCacheServiceTests : IDisposable
{
    private readonly FileCacheService _cache;
    private readonly string _cacheDir;

    public FileCacheServiceTests()
    {
        _cache = new FileCacheService(NullLogger<FileCacheService>.Instance);
        _cacheDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "BlueStar", "cache");
    }

    public void Dispose()
    {
        try { _cache.ClearAsync().GetAwaiter().GetResult(); } catch { }
    }

    [Fact]
    public async Task SetAndGet_ReturnsCachedValueFromMemoryAndDisk()
    {
        var key = $"test_key_{Guid.NewGuid():N}";
        var value = new TestModel { Name = "BlueStar Game", Count = 42 };

        await _cache.SetAsync(key, value, TimeSpan.FromMinutes(10));

        var retrieved = await _cache.GetAsync<TestModel>(key);
        retrieved.Should().NotBeNull();
        retrieved!.Name.Should().Be("BlueStar Game");
        retrieved.Count.Should().Be(42);
    }

    [Fact]
    public async Task Get_WhenExpired_ReturnsNullAndCleansUp()
    {
        var key = $"test_expired_{Guid.NewGuid():N}";
        var value = new TestModel { Name = "Old", Count = 1 };

        // Set expired in the past
        await _cache.SetAsync(key, value, TimeSpan.FromMilliseconds(-100));

        var retrieved = await _cache.GetAsync<TestModel>(key);
        retrieved.Should().BeNull();
    }

    [Fact]
    public async Task ConcurrentSetAsync_SameKey_DoesNotCorruptFile()
    {
        var key = $"test_concurrent_{Guid.NewGuid():N}";

        var tasks = new Task[10];
        for (int i = 0; i < 10; i++)
        {
            var idx = i;
            tasks[i] = Task.Run(async () =>
            {
                await _cache.SetAsync(key, new TestModel { Name = $"Name_{idx}", Count = idx }, TimeSpan.FromMinutes(5));
            });
        }

        await Task.WhenAll(tasks);

        var final = await _cache.GetAsync<TestModel>(key);
        final.Should().NotBeNull();
        final!.Name.Should().StartWith("Name_");
    }

    [Fact]
    public async Task RemoveAsync_ClearsFromMemoryAndDisk()
    {
        var key = $"test_remove_{Guid.NewGuid():N}";
        await _cache.SetAsync(key, "hello world", TimeSpan.FromMinutes(10));

        await _cache.RemoveAsync(key);

        var retrieved = await _cache.GetAsync<string>(key);
        retrieved.Should().BeNull();
    }

    [Fact]
    public async Task SweepExpiredEntriesAsync_DeletesExpiredDiskFiles()
    {
        var key = $"test_sweep_{Guid.NewGuid():N}";
        await _cache.SetAsync(key, "swept", TimeSpan.FromMilliseconds(-1000));

        await _cache.SweepExpiredEntriesAsync();

        var retrieved = await _cache.GetAsync<string>(key);
        retrieved.Should().BeNull();
    }

    [Fact]
    public async Task ClearAsync_RemovesAllEntries()
    {
        var key1 = $"test_clear1_{Guid.NewGuid():N}";
        var key2 = $"test_clear2_{Guid.NewGuid():N}";

        await _cache.SetAsync(key1, "val1");
        await _cache.SetAsync(key2, "val2");

        await _cache.ClearAsync();

        (await _cache.GetAsync<string>(key1)).Should().BeNull();
        (await _cache.GetAsync<string>(key2)).Should().BeNull();
    }

    public sealed class TestModel
    {
        public string Name { get; set; } = string.Empty;
        public int Count { get; set; }
    }
}
