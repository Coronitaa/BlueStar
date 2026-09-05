using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BlueStar.Core.Interfaces;
using BlueStar.Core.Models;
using BlueStar.Infrastructure.Cache;
using BlueStar.Infrastructure.DepotBox;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace BlueStar.Infrastructure.Tests;

public class DepotBoxPersistentCachingTests : IDisposable
{
    private readonly string _testCacheDir;
    private readonly FileCacheService _cacheService;

    public DepotBoxPersistentCachingTests()
    {
        _testCacheDir = Path.Combine(Path.GetTempPath(), "BlueStarTests_DepotBox_" + Guid.NewGuid().ToString("N"));
        _cacheService = new FileCacheService(NullLogger<FileCacheService>.Instance, _testCacheDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testCacheDir))
            {
                Directory.Delete(_testCacheDir, recursive: true);
            }
        }
        catch { }
    }

    private class TestManifestsHandler : HttpMessageHandler
    {
        public int ManifestRequestsCount { get; private set; }
        public int AvailabilityRequestsCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri?.ToString() ?? "";
            if (uri.Contains("/manifests"))
            {
                ManifestRequestsCount++;
                var manifests = new List<object>
                {
                    new
                    {
                        depotId = 1245621,
                        manifestId = 987654321,
                        date = "2024-06-20T10:00:00Z"
                    }
                };
                var json = JsonSerializer.Serialize(manifests);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(json)
                });
            }
            else if (uri.Contains("/availability"))
            {
                AvailabilityRequestsCount++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"available\":true}")
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    [Fact]
    public async Task GetManifestsAsync_UsesCacheOnSecondCall_AndBypassesWithForceRefresh()
    {
        var handler = new TestManifestsHandler();
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://depotbox.org") };
        var authMock = new Mock<IDepotBoxAuthService>();
        authMock.Setup(a => a.GetEffectiveApiKeyAsync(It.IsAny<CancellationToken>())).ReturnsAsync("test-key");

        var client = new DepotBoxApiClient(httpClient, authMock.Object, NullLogger<DepotBoxApiClient>.Instance, cacheService: _cacheService);

        uint testAppId = 1245620;

        // First call: hits HTTP
        var result1 = await client.GetManifestsAsync(testAppId);
        result1.Should().HaveCount(1);
        handler.ManifestRequestsCount.Should().Be(1);

        // Second call without forceRefresh: should come from cache, no HTTP hit
        var result2 = await client.GetManifestsAsync(testAppId);
        result2.Should().HaveCount(1);
        handler.ManifestRequestsCount.Should().Be(1);

        // Third call with forceRefresh: true -> should hit HTTP again
        var result3 = await client.GetManifestsAsync(testAppId, forceRefresh: true);
        result3.Should().HaveCount(1);
        handler.ManifestRequestsCount.Should().Be(2);
    }

    [Fact]
    public async Task InvalidateAppCache_ClearsCache_ForcesNextCallToHitHttp()
    {
        var handler = new TestManifestsHandler();
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://depotbox.org") };
        var authMock = new Mock<IDepotBoxAuthService>();
        authMock.Setup(a => a.GetEffectiveApiKeyAsync(It.IsAny<CancellationToken>())).ReturnsAsync("test-key");

        var client = new DepotBoxApiClient(httpClient, authMock.Object, NullLogger<DepotBoxApiClient>.Instance, cacheService: _cacheService);

        uint testAppId = 730;

        // First call
        var result1 = await client.GetManifestsAsync(testAppId);
        result1.Should().HaveCount(1);
        handler.ManifestRequestsCount.Should().Be(1);

        // Invalidate cache for AppId
        client.InvalidateAppCache(testAppId);

        // Next call should hit network because cache was invalidated
        var result2 = await client.GetManifestsAsync(testAppId);
        result2.Should().HaveCount(1);
        handler.ManifestRequestsCount.Should().Be(2);
    }

    [Fact]
    public async Task CheckAvailabilityAsync_UsesCacheOnSecondCall()
    {
        var handler = new TestManifestsHandler();
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://depotbox.org") };
        var authMock = new Mock<IDepotBoxAuthService>();
        authMock.Setup(a => a.GetEffectiveApiKeyAsync(It.IsAny<CancellationToken>())).ReturnsAsync("test-key");

        var client = new DepotBoxApiClient(httpClient, authMock.Object, NullLogger<DepotBoxApiClient>.Instance, cacheService: _cacheService);

        uint testAppId = 570;

        var avail1 = await client.CheckAvailabilityAsync(testAppId);
        avail1.Should().BeTrue();
        handler.AvailabilityRequestsCount.Should().Be(1);

        var avail2 = await client.CheckAvailabilityAsync(testAppId);
        avail2.Should().BeTrue();
        handler.AvailabilityRequestsCount.Should().Be(1);
    }
}
