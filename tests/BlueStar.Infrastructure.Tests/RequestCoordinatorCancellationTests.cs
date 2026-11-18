using BlueStar.Infrastructure.Services;
using FluentAssertions;
using Xunit;

namespace BlueStar.Infrastructure.Tests;

public sealed class RequestCoordinatorCancellationTests
{
    // Caller A cancels; the shared request is still winding down when caller B (no token) asks for the same key.
    // B must get a fresh request, not A's cancellation. This is what made Scenario8 fail at random in CI.
    [Fact]
    public async Task NewCaller_DoesNotInheritCancellation_OfARequestStillWindingDown()
    {
        var coordinator = new RequestCoordinator();
        var release = new TaskCompletionSource();
        using var cts = new CancellationTokenSource();

        var first = coordinator.ExecuteAsync("app:730", async ct =>
        {
            await release.Task; // a slow request that only notices cancellation later
            ct.ThrowIfCancellationRequested();
            return 1;
        }, cts.Token);
        cts.Cancel();
        await first.Invoking(t => t).Should().ThrowAsync<OperationCanceledException>();

        var second = coordinator.ExecuteAsync("app:730", _ => Task.FromResult(2));
        release.SetResult();

        (await second).Should().Be(2);
    }

    [Fact]
    public async Task ConcurrentCallers_StillShareOneRequest()
    {
        var coordinator = new RequestCoordinator();
        var calls = 0;
        var release = new TaskCompletionSource();
        Func<CancellationToken, Task<int>> factory = async _ => { Interlocked.Increment(ref calls); await release.Task; return 7; };

        var a = coordinator.ExecuteAsync("k", factory);
        var b = coordinator.ExecuteAsync("k", factory);
        release.SetResult();

        (await Task.WhenAll(a, b)).Should().Equal(7, 7);
        calls.Should().Be(1);
    }
}
