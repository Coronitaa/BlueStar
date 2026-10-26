using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using BlueStar.Core.Interfaces;
using BlueStar.Infrastructure.Mods;
using BlueStar.Infrastructure.Storage;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BlueStar.Infrastructure.Tests;

public sealed class ModApiKeyServiceTests : IDisposable
{
    private readonly string _settingsPath = Path.Combine(Path.GetTempPath(), $"modkeys_test_{Guid.NewGuid():N}.json");

    private sealed class InMemorySecureStorage : ISecureStorage
    {
        public Dictionary<string, string> Values { get; } = new();

        public Task<string?> GetAsync(string key, CancellationToken ct) =>
            Task.FromResult(Values.TryGetValue(key, out var v) ? v : null);

        public Task SetAsync(string key, string value, CancellationToken ct)
        {
            Values[key] = value;
            return Task.CompletedTask;
        }

        public Task<bool> DeleteAsync(string key, CancellationToken ct) => Task.FromResult(Values.Remove(key));
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _body;
        public HttpRequestMessage? LastRequest { get; private set; }

        public StubHandler(HttpStatusCode status, string body = "")
        {
            _status = status;
            _body = body;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(new HttpResponseMessage(_status) { Content = new StringContent(_body) });
        }
    }

    private AppSettingsService CreateSettings(string? json = null)
    {
        if (json != null) File.WriteAllText(_settingsPath, json);
        return new AppSettingsService(NullLogger<AppSettingsService>.Instance, _settingsPath);
    }

    private static ModApiKeyService CreateService(
        InMemorySecureStorage storage,
        AppSettingsService? settings,
        HttpMessageHandler? handler = null) =>
        new(new HttpClient(handler ?? new StubHandler(HttpStatusCode.OK)), storage,
            NullLogger<ModApiKeyService>.Instance, settings);

    public void Dispose()
    {
        try { if (File.Exists(_settingsPath)) File.Delete(_settingsPath); } catch { }
    }

    [Fact]
    public async Task GetEffectiveKeyAsync_ReturnsBuiltInKey_WhenNoPersonalOverride()
    {
        // Arrange
        var settings = CreateSettings("""{ "modApiKeys": { "curseForge": "builtin-cf" } }""");
        var service = CreateService(new InMemorySecureStorage(), settings);

        // Act
        var key = await service.GetEffectiveKeyAsync(ModApiProvider.CurseForge);
        var source = await service.GetKeySourceAsync(ModApiProvider.CurseForge);

        // Assert
        key.Should().Be("builtin-cf");
        source.Should().Be(ModApiKeySource.BuiltIn);
    }

    [Fact]
    public async Task GetEffectiveKeyAsync_PrefersPersonalOverride_OverBuiltInKey()
    {
        // Arrange
        var settings = CreateSettings("""{ "modApiKeys": { "nexusMods": "builtin-nexus" } }""");
        var storage = new InMemorySecureStorage();
        var service = CreateService(storage, settings);

        // Act
        await service.SetCustomKeyAsync(ModApiProvider.NexusMods, "  personal-nexus  ");

        // Assert
        (await service.GetEffectiveKeyAsync(ModApiProvider.NexusMods)).Should().Be("personal-nexus");
        (await service.GetKeySourceAsync(ModApiProvider.NexusMods)).Should().Be(ModApiKeySource.CustomOverride);
        storage.Values.Should().ContainKey(ModApiKeyService.StorageKey(ModApiProvider.NexusMods));
    }

    [Fact]
    public async Task ClearCustomKeyAsync_RevertsToBuiltInKey()
    {
        // Arrange
        var settings = CreateSettings("""{ "modApiKeys": { "modIo": "builtin-modio" } }""");
        var service = CreateService(new InMemorySecureStorage(), settings);
        await service.SetCustomKeyAsync(ModApiProvider.ModIo, "personal-modio");

        // Act
        await service.ClearCustomKeyAsync(ModApiProvider.ModIo);

        // Assert
        (await service.GetEffectiveKeyAsync(ModApiProvider.ModIo)).Should().Be("builtin-modio");
    }

    [Fact]
    public async Task SetCustomKeyAsync_WithBlankKey_ClearsOverride()
    {
        // Arrange
        var storage = new InMemorySecureStorage();
        var service = CreateService(storage, CreateSettings());
        await service.SetCustomKeyAsync(ModApiProvider.CurseForge, "personal");

        // Act
        await service.SetCustomKeyAsync(ModApiProvider.CurseForge, "   ");

        // Assert
        storage.Values.Should().BeEmpty();
    }

    [Fact]
    public async Task GetKeySourceAsync_ReturnsNone_WhenPlaceholderOrMissing()
    {
        // Arrange
        var settings = CreateSettings("""{ "modApiKeys": { "curseForge": "YOUR-API-KEY" } }""");
        var service = CreateService(new InMemorySecureStorage(), settings);

        // Act
        var source = await service.GetKeySourceAsync(ModApiProvider.CurseForge);

        // Assert
        source.Should().Be(ModApiKeySource.None);
        (await service.GetEffectiveKeyAsync(ModApiProvider.CurseForge)).Should().BeNull();
    }

    [Fact]
    public void GetBuiltInModApiKey_EnvironmentVariable_TakesPrecedenceOverSettings()
    {
        // Arrange
        var variable = AppSettingsService.ModApiKeyEnvironmentVariable(ModApiProvider.ModIo);
        var previous = Environment.GetEnvironmentVariable(variable);
        var settings = CreateSettings("""{ "modApiKeys": { "modIo": "from-settings" } }""");

        try
        {
            Environment.SetEnvironmentVariable(variable, "from-env");

            // Act
            var key = settings.GetBuiltInModApiKey(ModApiProvider.ModIo);

            // Assert
            key.Should().Be("from-env");
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, previous);
        }
    }

    [Theory]
    [InlineData(HttpStatusCode.OK, ModApiKeyCheckStatus.Valid)]
    [InlineData(HttpStatusCode.Unauthorized, ModApiKeyCheckStatus.Invalid)]
    [InlineData(HttpStatusCode.Forbidden, ModApiKeyCheckStatus.Invalid)]
    [InlineData(HttpStatusCode.TooManyRequests, ModApiKeyCheckStatus.RateLimited)]
    [InlineData(HttpStatusCode.InternalServerError, ModApiKeyCheckStatus.NetworkError)]
    public async Task ValidateKeyAsync_MapsHttpStatus(HttpStatusCode httpStatus, ModApiKeyCheckStatus expected)
    {
        // Arrange
        var service = CreateService(new InMemorySecureStorage(), CreateSettings(), new StubHandler(httpStatus));

        // Act
        var result = await service.ValidateKeyAsync(ModApiProvider.CurseForge, "some-key");

        // Assert
        result.Status.Should().Be(expected);
        result.HttpStatusCode.Should().Be((int)httpStatus);
    }

    [Fact]
    public async Task ValidateKeyAsync_ReturnsMissingKey_WhenNothingConfigured()
    {
        // Arrange
        var service = CreateService(new InMemorySecureStorage(), CreateSettings());

        // Act
        var result = await service.ValidateKeyAsync(ModApiProvider.NexusMods);

        // Assert
        result.Status.Should().Be(ModApiKeyCheckStatus.MissingKey);
    }

    [Theory]
    [InlineData(ModApiProvider.CurseForge, "x-api-key")]
    [InlineData(ModApiProvider.NexusMods, "apikey")]
    public void BuildValidationRequest_SendsKeyInProviderHeader(ModApiProvider provider, string header)
    {
        // Act
        using var request = ModApiKeyService.BuildValidationRequest(provider, "secret");

        // Assert
        request.Headers.GetValues(header).Should().ContainSingle().Which.Should().Be("secret");
    }

    [Theory]
    [InlineData("""{ "username": "abc.sa@thunderstore.io" }""", ModApiKeyCheckStatus.Valid)]
    [InlineData("""{ "username": null }""", ModApiKeyCheckStatus.Invalid)]
    public async Task ValidateKeyAsync_Thunderstore_RequiresNamedUser(string body, ModApiKeyCheckStatus expected)
    {
        // Arrange
        var handler = new StubHandler(HttpStatusCode.OK, body);
        var service = CreateService(new InMemorySecureStorage(), CreateSettings(), handler);

        // Act
        var result = await service.ValidateKeyAsync(ModApiProvider.Thunderstore, "tss_token");

        // Assert
        result.Status.Should().Be(expected);
        handler.LastRequest!.Headers.Authorization!.ToString().Should().Be("Bearer tss_token");
        handler.LastRequest.RequestUri!.ToString().Should().Be("https://thunderstore.io/api/experimental/current-user/");
    }

    [Fact]
    public async Task GetApiBaseUrlAsync_ModIo_UsesBuiltInAccountPath()
    {
        // Arrange
        var settings = CreateSettings("""{ "modApiKeys": { "modIoApiUrl": "https://u-123.modapi.io/v1/" } }""");
        var handler = new StubHandler(HttpStatusCode.OK);
        var service = CreateService(new InMemorySecureStorage(), settings, handler);

        // Act
        var url = await service.GetApiBaseUrlAsync(ModApiProvider.ModIo);
        await service.ValidateKeyAsync(ModApiProvider.ModIo, "k");

        // Assert
        url.Should().Be("https://u-123.modapi.io/v1");
        handler.LastRequest!.RequestUri!.Host.Should().Be("u-123.modapi.io");
    }

    [Fact]
    public async Task GetApiBaseUrlAsync_PersonalUrl_OverridesBuiltInPath()
    {
        // Arrange
        var settings = CreateSettings("""{ "modApiKeys": { "modIoApiUrl": "https://u-123.modapi.io/v1" } }""");
        var service = CreateService(new InMemorySecureStorage(), settings);

        // Act
        await service.SetCustomApiBaseUrlAsync(ModApiProvider.ModIo, "https://u-999.modapi.io/v1");

        // Assert
        (await service.GetApiBaseUrlAsync(ModApiProvider.ModIo)).Should().Be("https://u-999.modapi.io/v1");
    }

    [Fact]
    public async Task GetApiBaseUrlAsync_IgnoresBuiltInUrlOutsideProviderDomains()
    {
        // Arrange
        var settings = CreateSettings("""{ "modApiKeys": { "modIoApiUrl": "https://evil.example.com/v1" } }""");
        var service = CreateService(new InMemorySecureStorage(), settings);

        // Act
        var url = await service.GetApiBaseUrlAsync(ModApiProvider.ModIo);

        // Assert
        url.Should().Be("https://api.mod.io/v1");
    }

    [Theory]
    [InlineData("http://u-1.modapi.io/v1")]
    [InlineData("https://modapi.io.evil.com/v1")]
    [InlineData("not a url")]
    public async Task SetCustomApiBaseUrlAsync_RejectsUrlsOutsideProviderDomains(string url)
    {
        // Arrange
        var service = CreateService(new InMemorySecureStorage(), CreateSettings());

        // Act
        var act = () => service.SetCustomApiBaseUrlAsync(ModApiProvider.ModIo, url);

        // Assert
        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task ClearCustomKeyAsync_AlsoClearsPersonalUrl()
    {
        // Arrange
        var storage = new InMemorySecureStorage();
        var service = CreateService(storage, CreateSettings());
        await service.SetCustomKeyAsync(ModApiProvider.ModIo, "k");
        await service.SetCustomApiBaseUrlAsync(ModApiProvider.ModIo, "https://u-1.modapi.io/v1");

        // Act
        await service.ClearCustomKeyAsync(ModApiProvider.ModIo);

        // Assert
        storage.Values.Should().BeEmpty();
    }

    [Fact]
    public void BuildValidationRequest_ModIo_SendsKeyAsQueryParameter()
    {
        // Act
        using var request = ModApiKeyService.BuildValidationRequest(ModApiProvider.ModIo, "a b");

        // Assert
        request.RequestUri!.Query.Should().Contain("api_key=a%20b");
    }
}
