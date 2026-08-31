using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using BlueStar.Infrastructure.Cache;
using BlueStar.Infrastructure.Metadata;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BlueStar.Infrastructure.Tests;

public class SteamStoreUpdateDateCacheTests
{
    private class MockSteamNewsHandler : HttpMessageHandler
    {
        public int NewsRequestCount { get; private set; }
        public int DepotRequestCount { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri?.ToString() ?? "";

            if (url.Contains("/v1/info/", StringComparison.OrdinalIgnoreCase))
            {
                DepotRequestCount++;
                // Return SteamCMD depot info with no build date to force news fallback
                var json = @"{ ""data"": { ""1245620"": { ""common"": { ""type"": ""Game"" } } } }";
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(json)
                };
            }
            else if (url.Contains("/ISteamNews/GetNewsForApp/", StringComparison.OrdinalIgnoreCase))
            {
                NewsRequestCount++;
                await Task.Delay(50, cancellationToken);
                var newsJson = @"{
                    ""appnews"": {
                        ""appid"": 1245620,
                        ""newsitems"": [
                            { ""gid"": ""1"", ""title"": ""Patch 1.10"", ""date"": 1700000000 }
                        ]
                    }
                }";
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(newsJson)
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }

    [Fact]
    public async Task GetLatestAppUpdateDateAsync_CachesResult_AvoidsDuplicateNewsApiRequests()
    {
        var handler = new MockSteamNewsHandler();
        using var httpClient = new HttpClient(handler);
        var cache = new FileCacheService(NullLogger<FileCacheService>.Instance);
        await cache.ClearAsync();

        var client = new SteamStoreApiClient(httpClient, NullLogger<SteamStoreApiClient>.Instance, cache);

        var first = await client.GetLatestAppUpdateDateAsync(1245620);
        var second = await client.GetLatestAppUpdateDateAsync(1245620);

        first.Should().NotBeNull();
        second.Should().NotBeNull();
        first.Should().Be(second);

        handler.NewsRequestCount.Should().Be(1);
    }

    [Fact]
    public async Task GetLatestAppUpdateDateAsync_ConcurrentRequests_DeduplicatesInFlightTasks()
    {
        var handler = new MockSteamNewsHandler();
        using var httpClient = new HttpClient(handler);
        var cache = new FileCacheService(NullLogger<FileCacheService>.Instance);
        await cache.ClearAsync();

        var client = new SteamStoreApiClient(httpClient, NullLogger<SteamStoreApiClient>.Instance, cache);

        var tasks = new Task<DateTimeOffset?>[5];
        for (int i = 0; i < 5; i++)
        {
            tasks[i] = Task.Run(() => client.GetLatestAppUpdateDateAsync(1245620));
        }

        var results = await Task.WhenAll(tasks);
        foreach (var r in results)
        {
            r.Should().NotBeNull();
            r.Should().Be(results[0]);
        }

        handler.NewsRequestCount.Should().Be(1);
    }
}
