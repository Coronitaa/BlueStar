using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using BlueStar.Core.Interfaces;
using BlueStar.Core.Models;
using BlueStar.Infrastructure.Mods.CurseForge;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Moq.Protected;
using Xunit;

namespace BlueStar.Infrastructure.Tests;

public sealed class CurseForgeServiceTests : IDisposable
{
    private readonly string _cacheRoot = Path.Combine(Path.GetTempPath(), "bluestar_cf_tests_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_cacheRoot, recursive: true); } catch { }
    }

    [Fact]
    public async Task ResolveGameIdAsync_KnownAppId_ReturnsImmediately()
    {
        var apiKeyMock = new Mock<IModApiKeyService>();
        apiKeyMock.Setup(k => k.GetEffectiveKeyAsync(ModApiProvider.CurseForge, It.IsAny<CancellationToken>())).ReturnsAsync("fake-key");
        apiKeyMock.Setup(k => k.GetApiBaseUrlAsync(ModApiProvider.CurseForge, It.IsAny<CancellationToken>())).ReturnsAsync("https://api.curseforge.com/v1");

        var handler = new Mock<HttpMessageHandler>(MockBehavior.Strict);
        var client = new HttpClient(handler.Object);
        var service = new CurseForgeService(client, apiKeyMock.Object, NullLogger<CurseForgeService>.Instance, _cacheRoot);

        var instance = new GameInstance
        {
            Id = Guid.NewGuid(),
            Name = "Palworld",
            AppId = 1623730,
            InstallPath = @"C:\Games\Palworld"
        };

        var gameId = await service.ResolveGameIdAsync(instance);
        gameId.Should().Be(85196);
    }

    [Fact]
    public async Task SearchModsAsync_ParsesApiResponse_Correctly()
    {
        const string mockJson = """
        {
          "data": [
            {
              "id": 12345,
              "gameId": 82956,
              "name": "More Company",
              "slug": "more-company",
              "summary": "Expands lobby player cap.",
              "downloadCount": 3500000,
              "logo": {
                "thumbnailUrl": "https://media.forgecdn.net/avatars/123/thumbnail.png"
              },
              "categories": [
                { "id": 1, "name": "Gameplay" }
              ],
              "authors": [
                { "id": 10, "name": "notnotnotswipez" }
              ],
              "latestFiles": [
                {
                  "id": 556677,
                  "gameId": 82956,
                  "displayName": "MoreCompany-1.9.4.zip",
                  "fileName": "MoreCompany-1.9.4.zip",
                  "downloadUrl": "https://edge.forgecdn.net/files/556/677/MoreCompany-1.9.4.zip",
                  "fileLength": 1024000,
                  "fileDate": "2024-03-01T00:00:00Z"
                }
              ]
            }
          ],
          "pagination": {
            "index": 0,
            "pageSize": 50,
            "resultCount": 1,
            "totalCount": 1
          }
        }
        """;

        var apiKeyMock = new Mock<IModApiKeyService>();
        apiKeyMock.Setup(k => k.GetEffectiveKeyAsync(ModApiProvider.CurseForge, It.IsAny<CancellationToken>())).ReturnsAsync("fake-key");
        apiKeyMock.Setup(k => k.GetApiBaseUrlAsync(ModApiProvider.CurseForge, It.IsAny<CancellationToken>())).ReturnsAsync("https://api.curseforge.com/v1");

        var handler = new Mock<HttpMessageHandler>();
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new StringContent(mockJson)
            });

        var client = new HttpClient(handler.Object);
        var service = new CurseForgeService(client, apiKeyMock.Object, NullLogger<CurseForgeService>.Instance, _cacheRoot);

        var mods = await service.SearchModsAsync(82956, query: "More Company");

        mods.Should().HaveCount(1);
        mods[0].Id.Should().Be(12345);
        mods[0].Name.Should().Be("More Company");
        mods[0].PrimaryAuthor.Should().Be("notnotnotswipez");
        mods[0].LatestFile.Should().NotBeNull();
        mods[0].LatestFile!.FileName.Should().Be("MoreCompany-1.9.4.zip");
    }
}
