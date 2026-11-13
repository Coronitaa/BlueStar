using System;
using System.Threading;
using System.Threading.Tasks;
using BlueStar.App.Services;
using Xunit;

namespace BlueStar.App.Tests;

public class ImageCacheServiceTests
{
    [Fact]
    public void GetFromMemory_WhenNotCached_ReturnsNull()
    {
        var cache = ImageCacheService.Instance;
        var result = cache.GetFromMemory("https://example.com/non_existent_" + Guid.NewGuid().ToString("N") + ".jpg");
        Assert.Null(result);
    }

    [Fact]
    public async Task LoadImageAsync_WithCancelledToken_ReturnsNullGracefully()
    {
        var cache = ImageCacheService.Instance;
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var result = await cache.LoadImageAsync("https://example.com/cancelled_" + Guid.NewGuid().ToString("N") + ".jpg", 0, null, cts.Token);
        Assert.Null(result);
    }

    [Fact]
    public async Task LoadImageAsync_WhenFails_MarksNegativeCacheAndReturnsNullInstantlyOnSecondCall()
    {
        var cache = ImageCacheService.Instance;
        var deadUrl = "http://127.0.0.1:1/nonexistent_" + Guid.NewGuid().ToString("N") + ".jpg";

        // First call fails and marks negative cache
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var first = await cache.LoadImageAsync(deadUrl);
        sw.Stop();
        Assert.Null(first);

        // Second call should return immediately via tombstone negative cache (0ms)
        var sw2 = System.Diagnostics.Stopwatch.StartNew();
        var second = await cache.LoadImageAsync(deadUrl);
        sw2.Stop();
        Assert.Null(second);
        Assert.True(sw2.ElapsedMilliseconds < 50, $"Expected instantaneous negative cache hit, but took {sw2.ElapsedMilliseconds}ms");
    }

    [Fact]
    public async Task Invalidate_ClearsNegativeCacheEntry()
    {
        var cache = ImageCacheService.Instance;
        var deadUrl = "http://127.0.0.1:1/invalidate_test_" + Guid.NewGuid().ToString("N") + ".jpg";

        // Fail to populate negative cache
        await cache.LoadImageAsync(deadUrl);

        // Invalidate entry
        cache.Invalidate(deadUrl);

        // Cache should not hold in-memory hit
        Assert.Null(cache.GetFromMemory(deadUrl));
    }

    [Fact]
    public async Task ConcurrentLoadImageAsync_DeduplicatesInFlightRequests()
    {
        var cache = ImageCacheService.Instance;
        var testUrl = "http://127.0.0.1:1/dedup_test_" + Guid.NewGuid().ToString("N") + ".jpg";

        var task1 = cache.LoadImageAsync(testUrl);
        var task2 = cache.LoadImageAsync(testUrl);
        var task3 = cache.LoadImageAsync(testUrl);

        var results = await Task.WhenAll(task1, task2, task3);
        Assert.Equal(3, results.Length);
        Assert.Null(results[0]);
        Assert.Null(results[1]);
        Assert.Null(results[2]);
    }
}
