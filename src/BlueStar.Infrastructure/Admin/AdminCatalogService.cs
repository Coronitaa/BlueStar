using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BlueStar.Core.Interfaces;
using BlueStar.Core.Models;
using Microsoft.Extensions.Logging;

namespace BlueStar.Infrastructure.Admin;

/// <summary>
/// Downloads the admin catalog from the BlueStar admin panel's GitHub Pages site, validates it and
/// keeps the last good copy on disk so Explore and the installer work offline.
/// </summary>
public sealed class AdminCatalogService : IAdminCatalogService
{
    /// <summary>Written by the admin panel; older BlueStar versions never read it.</summary>
    public const string DefaultCatalogUrl = "https://coronitaa.github.io/bluestar/catalog.json";

    private readonly HttpClient _http;
    private readonly ILogger<AdminCatalogService> _logger;
    private readonly string _cachePath;
    private readonly Uri _catalogUrl;
    private readonly SemaphoreSlim _lock = new(1, 1);
    // One network attempt per run (unless forced): offline, every caller would otherwise wait out the timeout.
    private bool _attempted;

    public AdminCatalog Current { get; private set; } = AdminCatalog.Empty;

    public AdminCatalogService(HttpClient http, ILogger<AdminCatalogService> logger, string? cachePath = null, Uri? catalogUrl = null)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _cachePath = cachePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BlueStar", "cache", "admin_catalog.json");
        _catalogUrl = catalogUrl ?? new Uri(DefaultCatalogUrl);
    }

    public async Task<AdminCatalog> GetAsync(bool forceRefresh = false, CancellationToken ct = default)
    {
        if (_attempted && !forceRefresh) return Current;

        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_attempted && !forceRefresh) return Current;

            _attempted = true;
            var json = await TryDownloadAsync(ct).ConfigureAwait(false);
            if (json != null && TryApply(json, "network"))
            {
                await TryWriteCacheAsync(json, ct).ConfigureAwait(false);
                return Current;
            }

            if (ReferenceEquals(Current, AdminCatalog.Empty) && File.Exists(_cachePath))
            {
                try
                {
                    var cached = await File.ReadAllTextAsync(_cachePath, ct).ConfigureAwait(false);
                    TryApply(cached, "cache");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _logger.LogWarning(ex, "Could not read the cached admin catalog");
                }
            }
            return Current;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<IReadOnlyList<AdminInstallPreset>> GetPresetsAsync(uint appId, CancellationToken ct = default)
    {
        var catalog = await GetAsync(ct: ct).ConfigureAwait(false);
        return catalog.Games.FirstOrDefault(g => g.AppId == appId)?.Presets ?? [];
    }

    private bool TryApply(string json, string source)
    {
        var result = AdminCatalogParser.Parse(json);
        foreach (var err in result.Errors.Take(50))
        {
            _logger.LogWarning("Admin catalog ({Source}): {Error}", source, err);
        }

        // A file whose root failed validation parses to Empty; keep what we had instead.
        if (ReferenceEquals(result.Catalog, AdminCatalog.Empty)) return false;

        Current = result.Catalog;
        _logger.LogInformation("Admin catalog loaded from {Source}: {Count} games", source, Current.Games.Count);
        return true;
    }

    private async Task<string?> TryDownloadAsync(CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(15));
            using var response = await _http.GetAsync(_catalogUrl, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogInformation("Admin catalog not available ({Status})", (int)response.StatusCode);
                return null;
            }

            // Redirects must not have left https (HttpClient refuses https->http, this is belt and braces).
            if (response.RequestMessage?.RequestUri?.Scheme != Uri.UriSchemeHttps) return null;
            if (response.Content.Headers.ContentLength > AdminCatalogParser.MaxBytes) return null;

            await using var stream = await response.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);
            var buffer = new byte[AdminCatalogParser.MaxBytes + 1];
            var total = 0;
            int read;
            while (total < buffer.Length && (read = await stream.ReadAsync(buffer.AsMemory(total), cts.Token).ConfigureAwait(false)) > 0)
            {
                total += read;
            }
            if (total > AdminCatalogParser.MaxBytes) return null;
            return Encoding.UTF8.GetString(buffer, 0, total);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            _logger.LogInformation("Admin catalog download failed: {Message}", ex.Message);
            return null;
        }
    }

    private async Task TryWriteCacheAsync(string json, CancellationToken ct)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_cachePath)!);
            await File.WriteAllTextAsync(_cachePath, json, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not cache the admin catalog");
        }
    }
}
