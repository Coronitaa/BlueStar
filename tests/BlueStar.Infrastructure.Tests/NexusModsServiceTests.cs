using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using BlueStar.Core.Interfaces;
using BlueStar.Core.Models;
using BlueStar.Infrastructure.Mods.NexusMods;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Moq.Protected;
using Xunit;

namespace BlueStar.Infrastructure.Tests;

public sealed class NexusModsServiceTests : IDisposable
{
    private readonly string _cacheRoot = Path.Combine(Path.GetTempPath(), "bluestar_nexus_tests_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_cacheRoot, recursive: true); } catch { }
    }

    [Theory]
    [InlineData("nxm://skyrimspecialedition/mods/12345/files/67890?key=abcdef123456&expires=1730000000&user_id=999")]
    [InlineData("vortex://skyrimspecialedition/mods/12345/files/67890?key=abcdef123456&expires=1730000000&user_id=999")]
    [InlineData("nxm-protocol://skyrimspecialedition/mods/12345/files/67890?key=abcdef123456&expires=1730000000&user_id=999")]
    public void CanHandleNxmUrl_ValidUrl_ReturnsTrue(string validUrl)
    {
        var service = new NexusModsService(
            new HttpClient(),
            Mock.Of<IModApiKeyService>(),
            NullLogger<NexusModsService>.Instance,
            _cacheRoot);

        service.CanHandleNxmUrl(validUrl).Should().BeTrue();
    }

    [Theory]
    [InlineData("https://nexusmods.com/skyrim/mods/123")]
    [InlineData("nxm://invalid")]
    [InlineData("")]
    [InlineData(null)]
    public void CanHandleNxmUrl_InvalidUrl_ReturnsFalse(string? url)
    {
        var service = new NexusModsService(
            new HttpClient(),
            Mock.Of<IModApiKeyService>(),
            NullLogger<NexusModsService>.Instance,
            _cacheRoot);

        service.CanHandleNxmUrl(url).Should().BeFalse();
    }

    [Fact]
    public void ParseNxmUrl_ExtractsComponentsCorrectly()
    {
        var service = new NexusModsService(
            new HttpClient(),
            Mock.Of<IModApiKeyService>(),
            NullLogger<NexusModsService>.Instance,
            _cacheRoot);

        const string url = "nxm://cyberpunk2077/mods/107/files/54321?key=SecRetKey789&expires=1739999999&user_id=42";
        var parsed = service.ParseNxmUrl(url);

        parsed.Should().NotBeNull();
        parsed!.GameDomain.Should().Be("cyberpunk2077");
        parsed.ModId.Should().Be(107);
        parsed.FileId.Should().Be(54321);
        parsed.Key.Should().Be("SecRetKey789");
        parsed.Expires.Should().Be(1739999999);
        parsed.UserId.Should().Be(42);
    }

    [Fact]
    public void ParseNxmUrl_VortexScheme_ExtractsComponentsCorrectly()
    {
        var service = new NexusModsService(
            new HttpClient(),
            Mock.Of<IModApiKeyService>(),
            NullLogger<NexusModsService>.Instance,
            _cacheRoot);

        const string url = "vortex://baldursgate3/mods/999/files/111?key=SecRetKeyVortex&expires=1739999999&user_id=123";
        var parsed = service.ParseNxmUrl(url);

        parsed.Should().NotBeNull();
        parsed!.GameDomain.Should().Be("baldursgate3");
        parsed.ModId.Should().Be(999);
        parsed.FileId.Should().Be(111);
        parsed.Key.Should().Be("SecRetKeyVortex");
    }

    [Fact]
    public async Task ResolveGameDomainAsync_KnownAppId_ReturnsInstantly()
    {
        var service = new NexusModsService(
            new HttpClient(),
            Mock.Of<IModApiKeyService>(),
            NullLogger<NexusModsService>.Instance,
            _cacheRoot);

        var instance = new GameInstance
        {
            Id = Guid.NewGuid(),
            Name = "The Elder Scrolls V: Skyrim Special Edition",
            AppId = 489830,
            InstallPath = @"C:\Games\SkyrimSE"
        };

        var domain = await service.ResolveGameDomainAsync(instance);
        domain.Should().Be("skyrimspecialedition");
    }

    [Fact]
    public async Task GetDownloadUrlFromNxmAsync_FreeAccountWithKeyAndExpires_ReturnsCdnUri()
    {
        const string mockDownloadLinkJson = """
        [
          {
            "name": "Nexus CDN - Premium/Free Fast Mirror",
            "short_name": "Nexus CDN",
            "URI": "https://file-delivery.nexusmods.com/1704/SkyUI_5_2_SE.7z?validtoken=xyz"
          }
        ]
        """;

        var apiKeyMock = new Mock<IModApiKeyService>();
        apiKeyMock.Setup(k => k.GetEffectiveKeyAsync(ModApiProvider.NexusMods, It.IsAny<CancellationToken>())).ReturnsAsync("nexus-api-key");
        apiKeyMock.Setup(k => k.GetApiBaseUrlAsync(ModApiProvider.NexusMods, It.IsAny<CancellationToken>())).ReturnsAsync("https://api.nexusmods.com/v1");

        var handler = new Mock<HttpMessageHandler>();
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.Is<HttpRequestMessage>(req =>
                    req.RequestUri != null &&
                    req.RequestUri.ToString().Contains("download_link.json?key=myKey&expires=1700000000")),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new StringContent(mockDownloadLinkJson)
            });

        var client = new HttpClient(handler.Object);
        var service = new NexusModsService(client, apiKeyMock.Object, NullLogger<NexusModsService>.Instance, _cacheRoot);

        const string nxmUrl = "nxm://skyrimspecialedition/mods/12604/files/35390?key=myKey&expires=1700000000&user_id=101";
        var directUrl = await service.GetDownloadUrlFromNxmAsync(nxmUrl);

        directUrl.Should().Be("https://file-delivery.nexusmods.com/1704/SkyUI_5_2_SE.7z?validtoken=xyz");
    }

    [Fact]
    public async Task GetDownloadUrlFromNxmAsync_MissingApiKey_ThrowsInvalidOperationException()
    {
        var apiKeyMock = new Mock<IModApiKeyService>();
        apiKeyMock.Setup(k => k.GetEffectiveKeyAsync(ModApiProvider.NexusMods, It.IsAny<CancellationToken>())).ReturnsAsync((string?)null);

        var service = new NexusModsService(new HttpClient(), apiKeyMock.Object, NullLogger<NexusModsService>.Instance, _cacheRoot);
        const string nxmUrl = "nxm://skyrimspecialedition/mods/12604/files/35390?key=myKey&expires=1700000000&user_id=101";

        var act = () => service.GetDownloadUrlFromNxmAsync(nxmUrl);
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*API Key de Nexus Mods*");
    }
}
