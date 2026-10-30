using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading.Tasks;
using BlueStar.Core.Interfaces;
using BlueStar.Infrastructure.Storage;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BlueStar.Infrastructure.Tests;

public class BundledSecretsTests : IDisposable
{
    private readonly string _dir;
    private readonly string _settingsPath;

    private static readonly BundledSecrets Secrets = new()
    {
        DefaultApiKey = "builtin-depotbox-key",
        CurseForge = "builtin-cf",
        ModIoApiUrl = "https://u-1.modapi.io/v1"
    };

    public BundledSecretsTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "BlueStar_Secrets_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _settingsPath = Path.Combine(_dir, "settings.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void Encrypted_secrets_round_trip_and_hide_the_plain_text()
    {
        var key = RandomNumberGenerator.GetBytes(64);

        var payload = BundledSecrets.Encrypt(Secrets, key);

        payload.Should().NotContain("builtin-depotbox-key");
        BundledSecrets.Decrypt(payload, key).Should().Be(Secrets);
    }

    [Fact]
    public void Tampered_or_wrongly_keyed_secrets_are_rejected()
    {
        var key = RandomNumberGenerator.GetBytes(64);
        var data = Convert.FromBase64String(BundledSecrets.Encrypt(Secrets, key));
        data[20] ^= 0xFF;

        var tampered = () => BundledSecrets.Decrypt(Convert.ToBase64String(data), key);
        var wrongKey = () => BundledSecrets.Decrypt(BundledSecrets.Encrypt(Secrets, key), RandomNumberGenerator.GetBytes(64));

        tampered.Should().Throw<CryptographicException>();
        wrongKey.Should().Throw<CryptographicException>();
    }

    [Fact]
    public void LoadFromModKeysJson_FindsModKeysJson_InWorkspace()
    {
        var secrets = BundledSecrets.Load();
        secrets.Should().NotBeNull();
        secrets!.CurseForge.Should().NotBeNullOrEmpty();
        secrets.NexusMods.Should().NotBeNullOrEmpty();
        secrets.ModIo.Should().NotBeNullOrEmpty();
        secrets.DefaultApiKey.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void Builds_without_a_key_have_no_bundled_secrets()
    {
        File.WriteAllText(Path.Combine(_dir, BundledSecrets.FileName), "anything");

        BundledSecrets.Load(_dir).Should().BeNull();
    }

    [Fact]
    public async Task Built_in_key_is_used_but_never_shown_or_saved()
    {
        var settings = new AppSettingsService(NullLogger<AppSettingsService>.Instance, _settingsPath, Secrets);

        settings.DefaultApiKey.Should().Be("builtin-depotbox-key");
        settings.UserDefaultApiKey.Should().BeNull();
        settings.GetBuiltInModApiKey(ModApiProvider.CurseForge).Should().Be("builtin-cf");
        settings.GetBuiltInModApiBaseUrl(ModApiProvider.ModIo).Should().Be("https://u-1.modapi.io/v1");

        await settings.SetDefaultApiUrlAsync("https://depotbox.org");

        File.ReadAllText(_settingsPath).Should().NotContain("builtin-depotbox-key").And.NotContain("builtin-cf");
    }

    [Fact]
    public void Plain_text_built_in_key_left_by_older_releases_is_removed()
    {
        File.WriteAllText(_settingsPath, """{ "defaultApiKey": "builtin-depotbox-key", "language": "es" }""");

        var settings = new AppSettingsService(NullLogger<AppSettingsService>.Instance, _settingsPath, Secrets);

        File.ReadAllText(_settingsPath).Should().NotContain("builtin-depotbox-key").And.Contain("\"es\"");
        settings.DefaultApiKey.Should().Be("builtin-depotbox-key");
    }

    [Fact]
    public void A_key_entered_by_the_user_wins_and_is_shown()
    {
        File.WriteAllText(_settingsPath, """{ "defaultApiKey": "my-own-key" }""");

        var settings = new AppSettingsService(NullLogger<AppSettingsService>.Instance, _settingsPath, Secrets);

        settings.DefaultApiKey.Should().Be("my-own-key");
        settings.UserDefaultApiKey.Should().Be("my-own-key");
    }
}
