using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BlueStar.Core.Helpers;
using Xunit;

namespace BlueStar.Core.Tests;

public class SingleFlightTests
{
    [Fact]
    public async Task ExecuteAsync_ConcurrentCallsSameKey_ExecutesFactoryOnlyOnce()
    {
        var sf = new SingleFlight();
        var callCount = 0;

        var tasks = Enumerable.Range(0, 10).Select(_ => sf.ExecuteAsync("shared_key", async ct =>
        {
            Interlocked.Increment(ref callCount);
            await Task.Delay(50, ct);
            return 42;
        })).ToList();

        var results = await Task.WhenAll(tasks);

        Assert.Equal(1, callCount);
        Assert.All(results, res => Assert.Equal(42, res));
    }

    [Fact]
    public async Task ExecuteAsync_SequentialCallsSameKey_ExecutesFactoryEachTime()
    {
        var sf = new SingleFlight();
        var callCount = 0;

        var res1 = await sf.ExecuteAsync("seq_key", _ =>
        {
            Interlocked.Increment(ref callCount);
            return Task.FromResult("one");
        });

        var res2 = await sf.ExecuteAsync("seq_key", _ =>
        {
            Interlocked.Increment(ref callCount);
            return Task.FromResult("two");
        });

        Assert.Equal(2, callCount);
        Assert.Equal("one", res1);
        Assert.Equal("two", res2);
    }

    [Fact]
    public async Task ExecuteAsync_WhenFactoryThrows_PropagatesToAllConcurrentCallers()
    {
        var sf = new SingleFlight();
        var callCount = 0;

        var tasks = Enumerable.Range(0, 5).Select(_ => sf.ExecuteAsync<string>("error_key", async ct =>
        {
            Interlocked.Increment(ref callCount);
            await Task.Delay(30, ct);
            throw new InvalidOperationException("Simulated error");
        })).ToList();

        foreach (var t in tasks)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => t);
        }

        Assert.Equal(1, callCount);
    }
}
