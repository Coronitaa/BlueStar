using System.Security.Cryptography;
using BlueStar.Core.Models;
using BlueStar.Infrastructure.Update;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BlueStar.Infrastructure.Tests;

/// <summary>The updater runs the downloaded installer silently, so it must refuse a download whose hash is wrong.</summary>
public sealed class UpdateDownloadIntegrityTests
{
    private static readonly byte[] Payload = "fake installer"u8.ToArray();

    private sealed class FixedHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new ByteArrayContent(Payload) });
    }

    private static UpdateInfo Update(string? sha) => new()
    {
        Version = $"0.0.{Random.Shared.Next(1000, 9999)}-test",
        DownloadUrl = "https://example.invalid/BlueStar-test.exe",
        Sha256 = sha,
    };

    [Fact]
    public async Task Download_WithMatchingHash_KeepsTheFile()
    {
        var service = new GitHubUpdateService(new HttpClient(new FixedHandler()), NullLogger<GitHubUpdateService>.Instance);

        var update = Update(Convert.ToHexStringLower(SHA256.HashData(Payload)));
        var path = await service.DownloadUpdateAsync(update, null, CancellationToken.None);
        try
        {
            File.ReadAllBytes(path).Should().Equal(Payload);
        }
        finally
        {
            File.Delete(path);
            // The download also leaves a "pending update" marker; remove it only if it is ours.
            var marker = Path.Combine(Path.GetDirectoryName(path)!, "pending_update.json");
            if (File.Exists(marker) && File.ReadAllText(marker).Contains(update.Version))
                File.Delete(marker);
        }
    }

    [Fact]
    public async Task Download_WithWrongHash_DeletesTheFileAndThrows()
    {
        var service = new GitHubUpdateService(new HttpClient(new FixedHandler()), NullLogger<GitHubUpdateService>.Instance);
        var update = Update(new string('0', 64));

        var act = () => service.DownloadUpdateAsync(update, null, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidDataException>().WithMessage("*SHA-256*");
        Directory.GetFiles(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BlueStar", "updates"), $"BlueStar-v{update.Version}*")
            .Should().BeEmpty();
    }

    [Theory]
    [InlineData("sha256:4C3F5D9B4DED0CD4844160D5CE25B18F2F8B65071ACDF2BAC3CCF5EB4E757266", "4c3f5d9b4ded0cd4844160d5ce25b18f2f8b65071acdf2bac3ccf5eb4e757266")]
    [InlineData("sha512:abc", null)]
    [InlineData(null, null)]
    public void ParseSha256Digest(string? digest, string? expected) =>
        GitHubUpdateService.ParseSha256Digest(digest).Should().Be(expected);
}
