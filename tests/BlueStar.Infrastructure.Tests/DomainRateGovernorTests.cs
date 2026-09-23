using System;
using System.Net;
using System.Threading.Tasks;
using BlueStar.Infrastructure.Steam;
using Xunit;

namespace BlueStar.Infrastructure.Tests;

public class DomainRateGovernorTests
{
    [Fact]
    public void DomainRateGovernor_IsolatesStoreAndApiBuckets()
    {
        var store = DomainRateGovernor.Store;
        var api = DomainRateGovernor.Api;

        Assert.NotEqual(store.Domain, api.Domain);
        Assert.Equal(DomainRateGovernor.StoreDomain, store.Domain);
        Assert.Equal(DomainRateGovernor.ApiDomain, api.Domain);

        // Report block on store, API must remain closed (healthy)
        store.ReportFailure(HttpStatusCode.TooManyRequests);

        Assert.Equal(CircuitState.Open, store.State);
        Assert.Equal(CircuitState.Closed, api.State);

        // Clean up
        store.ReportSuccess();
    }

    [Fact]
    public void CircuitBreaker_TripsOnFailure_AndRecoversOnSuccess()
    {
        var bucket = DomainRateGovernor.GetBucket("test.domain.com", TimeSpan.FromMilliseconds(10));

        Assert.Equal(CircuitState.Closed, bucket.State);

        bucket.ReportFailure(HttpStatusCode.Forbidden);
        Assert.Equal(CircuitState.Open, bucket.State);
        Assert.True(bucket.CooldownUntil > DateTimeOffset.UtcNow);

        // Report success resets to Closed
        bucket.ReportSuccess();
        Assert.Equal(CircuitState.Closed, bucket.State);
    }

    [Fact]
    public async Task AcquireAsync_WhenCircuitOpen_ReturnsNullFastFail()
    {
        var bucket = DomainRateGovernor.GetBucket("fastfail.test.com", TimeSpan.FromMilliseconds(10));
        bucket.ReportFailure(HttpStatusCode.TooManyRequests);

        var lease = await bucket.AcquireAsync(SteamRequestPriority.Interactive);
        Assert.Null(lease);

        bucket.ReportSuccess();
    }

    [Fact]
    public async Task AcquireAsync_WhenCircuitClosed_ReturnsDisposableLease()
    {
        var bucket = DomainRateGovernor.GetBucket("lease.test.com", TimeSpan.FromMilliseconds(10));
        bucket.ReportSuccess();

        using var lease = await bucket.AcquireAsync(SteamRequestPriority.Interactive);
        Assert.NotNull(lease);
    }
}
