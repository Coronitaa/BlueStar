using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using BlueStar.Core.Models;
using BlueStar.Infrastructure.Mods.Thunderstore;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Moq.Protected;
using Xunit;

namespace BlueStar.Infrastructure.Tests;

public sealed class ThunderstoreServiceTests : IDisposable
{
    private readonly string _cacheRoot = Path.Combine(Path.GetTempPath(), "bluestar_ts_tests_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_cacheRoot, recursive: true); } catch { }
    }

    [Fact]
    public async Task ResolveCommunityAsync_KnownAppId_ReturnsInstantlyWithoutHttp()
    {
        // Arrange
        var handler = new Mock<HttpMessageHandler>(MockBehavior.Strict);
        var client = new HttpClient(handler.Object);
        var service = new ThunderstoreService(client, NullLogger<ThunderstoreService>.Instance, _cacheRoot);

        var instance = new GameInstance
        {
            Id = Guid.NewGuid(),
            Name = "Lethal Company",
            AppId = 1966720,
            InstallPath = @"C:\Games\Lethal Company"
        };

        // Act
        var community = await service.ResolveCommunityAsync(instance);

        // Assert
        community.Should().Be("lethal-company");
    }

    [Fact]
    public async Task GetPackagesAsync_ParsesJson_AndPopulatesLatestVersion()
    {
        // Arrange
        const string json = """
        [
          {
            "name": "MoreCompany",
            "full_name": "notnotnotswipez-MoreCompany-1.9.3",
            "owner": "notnotnotswipez",
            "package_url": "https://thunderstore.io/c/lethal-company/p/notnotnotswipez/MoreCompany/",
            "date_created": "2023-11-20T00:00:00Z",
            "date_updated": "2024-03-01T00:00:00Z",
            "rating_score": 1420,
            "is_pinned": false,
            "is_deprecated": false,
            "total_downloads": 4500000,
            "has_nsfw_content": false,
            "categories": ["Cosmetics", "Server-side"],
            "versions": [
              {
                "name": "MoreCompany",
                "full_name": "notnotnotswipez-MoreCompany-1.9.3",
                "version_number": "1.9.3",
                "description": "A mod for Lethal Company that expands the player limit to 32 players.",
                "icon": "https://thunderstore.io/icon.png",
                "download_url": "https://thunderstore.io/package/download/notnotnotswipez/MoreCompany/1.9.3/",
                "downloads": 4500000,
                "date_created": "2024-03-01T00:00:00Z",
                "file_size": 2500000,
                "dependencies": ["bbepis-BepInExPack-5.4.2100"]
              }
            ]
          }
        ]
        """;

        var handler = new Mock<HttpMessageHandler>();
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json)
            });

        var client = new HttpClient(handler.Object);
        var service = new ThunderstoreService(client, NullLogger<ThunderstoreService>.Instance, _cacheRoot);

        // Act
        var packages = await service.GetPackagesAsync("lethal-company");

        // Assert
        packages.Should().ContainSingle();
        var pkg = packages[0];
        pkg.Name.Should().Be("MoreCompany");
        pkg.Owner.Should().Be("notnotnotswipez");
        pkg.TotalDownloads.Should().Be(4500000);
        pkg.Description.Should().Contain("expands the player limit");
        pkg.Categories.Should().Contain("Cosmetics");
        pkg.LatestVersion.Should().NotBeNull();
        pkg.LatestVersion!.VersionNumber.Should().Be("1.9.3");
    }

    [Fact]
    public async Task SearchPackagesAsync_FiltersByQueryAndCategory()
    {
        // Arrange
        const string json = """
        [
          {
            "name": "MoreCompany",
            "full_name": "notnotnotswipez-MoreCompany-1.9.3",
            "owner": "notnotnotswipez",
            "package_url": "https://thunderstore.io/c/lethal-company/p/notnotnotswipez/MoreCompany/",
            "total_downloads": 4500000,
            "rating_score": 1420,
            "categories": ["Cosmetics"],
            "versions": [{ "name": "MoreCompany", "full_name": "notnotnotswipez-MoreCompany-1.9.3", "version_number": "1.9.3", "download_url": "https://test.zip" }]
          },
          {
            "name": "LateCompany",
            "full_name": "alphy-LateCompany-1.0.10",
            "owner": "alphy",
            "package_url": "https://thunderstore.io/c/lethal-company/p/alphy/LateCompany/",
            "total_downloads": 3000000,
            "rating_score": 800,
            "categories": ["Gameplay"],
            "versions": [{ "name": "LateCompany", "full_name": "alphy-LateCompany-1.0.10", "version_number": "1.0.10", "download_url": "https://test2.zip" }]
          }
        ]
        """;

        var handler = new Mock<HttpMessageHandler>();
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json)
            });

        var client = new HttpClient(handler.Object);
        var service = new ThunderstoreService(client, NullLogger<ThunderstoreService>.Instance, _cacheRoot);

        // Act: search by query
        var results = await service.SearchPackagesAsync("lethal-company", query: "Late");
        results.Should().ContainSingle(p => p.Name == "LateCompany");

        // Act: search with multi-term
        var multiTerm = await service.SearchPackagesAsync("lethal-company", query: "Late Comp");
        multiTerm.Should().ContainSingle(p => p.Name == "LateCompany");

        // Act: filter by category
        var cosmetics = await service.SearchPackagesAsync("lethal-company", category: "Cosmetics");
        cosmetics.Should().ContainSingle(p => p.Name == "MoreCompany");

        // Act: sort ascending by downloads (least downloaded first)
        var ascDownloads = await service.SearchPackagesAsync("lethal-company", sortBy: "Downloads", isAscending: true);
        ascDownloads[0].Name.Should().Be("LateCompany");
        ascDownloads[1].Name.Should().Be("MoreCompany");

        // Act: sort descending by downloads (most downloaded first)
        var descDownloads = await service.SearchPackagesAsync("lethal-company", sortBy: "Downloads", isAscending: false);
        descDownloads[0].Name.Should().Be("MoreCompany");
        descDownloads[1].Name.Should().Be("LateCompany");
    }
}
