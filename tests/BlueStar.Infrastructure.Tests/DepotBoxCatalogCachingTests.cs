using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BlueStar.Core.Interfaces;
using BlueStar.Core.Models;
using BlueStar.Infrastructure.DepotBox;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace BlueStar.Infrastructure.Tests;

public class DepotBoxCatalogCachingTests
{
    private class MockCountingHandler : HttpMessageHandler
    {
        public int FullCatalogRequestCount { get; private set; }
        public int QueryRequestCount { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.PathAndQuery ?? "";
            if (path.StartsWith("/api/game-fixes?", StringComparison.OrdinalIgnoreCase))
            {
                QueryRequestCount++;
                // Return empty list so fallback to catalog is tested
                var emptyResp = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("[]")
                };
                return emptyResp;
            }
            else if (path.Equals("/api/game-fixes", StringComparison.OrdinalIgnoreCase))
            {
                FullCatalogRequestCount++;
                await Task.Delay(50, cancellationToken); // Simulate small network latency
                var fixes = new List<object>
                {
                    new
                    {
                        id = "fix_elden_ring",
                        name = "Elden Ring Online Fix",
                        downloadName = "eldenring_fix.zip",
                        description = "Multiplayer fix for Elden Ring",
                        tags = new[] { "Online", "1245620" }
                    },
                    new
                    {
                        id = "fix_cyberpunk",
                        name = "Cyberpunk 2077 Mod Fix",
                        downloadName = "cp2077_fix.zip",
                        description = "Audio and graphics fix",
                        tags = new[] { "Offline", "1091500" }
                    }
                };

                var json = JsonSerializer.Serialize(fixes);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(json)
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }

    [Fact]
    public async Task GetFullGameFixesCatalog_CachesResult_AndHitsNetworkOnce()
    {
        var handler = new MockCountingHandler();
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://depotbox.org") };
        var authMock = new Mock<IDepotBoxAuthService>();
        authMock.Setup(a => a.GetEffectiveApiKeyAsync(It.IsAny<CancellationToken>())).ReturnsAsync("test-key");

        var client = new DepotBoxApiClient(httpClient, authMock.Object, NullLogger<DepotBoxApiClient>.Instance);

        var first = await client.GetFullGameFixesCatalogAsync();
        var second = await client.GetFullGameFixesCatalogAsync();

        first.Should().HaveCount(2);
        second.Should().HaveCount(2);
        handler.FullCatalogRequestCount.Should().Be(1);
    }

    [Fact]
    public async Task GetFullGameFixesCatalog_ConcurrentCalls_SingleFlightOnlyOneRequest()
    {
        var handler = new MockCountingHandler();
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://depotbox.org") };
        var authMock = new Mock<IDepotBoxAuthService>();
        authMock.Setup(a => a.GetEffectiveApiKeyAsync(It.IsAny<CancellationToken>())).ReturnsAsync("test-key");

        var client = new DepotBoxApiClient(httpClient, authMock.Object, NullLogger<DepotBoxApiClient>.Instance);

        var tasks = new Task<IReadOnlyList<GameFixInfo>>[5];
        for (int i = 0; i < 5; i++)
        {
            tasks[i] = Task.Run(() => client.GetFullGameFixesCatalogAsync());
        }

        var results = await Task.WhenAll(tasks);
        foreach (var r in results)
        {
            r.Should().HaveCount(2);
        }

        handler.FullCatalogRequestCount.Should().Be(1);
    }

    [Fact]
    public async Task GetGameFixesAsync_WhenParamQueryEmpty_FallsBackToCachedCatalogMatching()
    {
        var handler = new MockCountingHandler();
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://depotbox.org") };
        var authMock = new Mock<IDepotBoxAuthService>();
        authMock.Setup(a => a.GetEffectiveApiKeyAsync(It.IsAny<CancellationToken>())).ReturnsAsync("test-key");

        var client = new DepotBoxApiClient(httpClient, authMock.Object, NullLogger<DepotBoxApiClient>.Instance);

        var results = await client.GetGameFixesAsync(query: "Elden");
        results.Should().HaveCount(1);
        results[0].Id.Should().Be("fix_elden_ring");

        handler.QueryRequestCount.Should().Be(1);
        handler.FullCatalogRequestCount.Should().Be(1);

        // Second search with different query uses the cached catalog without re-downloading
        var results2 = await client.GetGameFixesAsync(query: "Cyberpunk");
        results2.Should().HaveCount(1);
        results2[0].Id.Should().Be("fix_cyberpunk");

        handler.QueryRequestCount.Should().Be(2);
        handler.FullCatalogRequestCount.Should().Be(1); // STILL 1!
    }
}
