using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using BlueStar.Core.Interfaces;
using BlueStar.Core.Models;
using Microsoft.Extensions.Logging;

namespace BlueStar.Infrastructure.Mods.NexusMods;

/// <summary>
/// Client for Nexus Mods, supporting game domain resolution, metadata retrieval,
/// and downloading mods through the non-premium nxm:// web flow.
/// </summary>
public sealed class NexusModsService : INexusModsService
{
    private static readonly Regex NxmRegex = new(
        @"^(?:nxm|vortex|nxm-protocol)://(?<game>[^/?#]+)/mods/(?<mod>\d+)/files/(?<file>\d+)\?(?:.*&)?key=(?<key>[^&]+)(?:.*&)?expires=(?<expires>\d+)(?:.*&user_id=(?<user>\d+))?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly HttpClient _http;
    private readonly IModApiKeyService _apiKeyService;
    private readonly ILogger<NexusModsService> _logger;
    private readonly string _cacheRoot;

    private static readonly Dictionary<uint, string> KnownAppIdToGameDomain = new()
    {
        [489830]  = "skyrimspecialedition",
        [72850]   = "skyrim",
        [377160]  = "fallout4",
        [22380]   = "newvegas",
        [1091500] = "cyberpunk2077",
        [292030]  = "witcher3",
        [1086940] = "baldursgate3",
        [892970]  = "valheim",
        [1623730] = "palworld",
        [1966720] = "lethalcompany",
        [413150]  = "stardewvalley",
        [1716740] = "starfield",
        [1245620] = "eldenring",
        [582010]  = "monsterhunterworld",
        [1446780] = "monsterhunterrise",
        [1144200] = "readyornot",
        [264710]  = "subnautica",
        [848450]  = "subnauticabelowzero",
        [1145360] = "hades",
        [1145350] = "hades2",
        [1363080] = "manorlords",
        [261550]  = "mountandblade2bannerlord",
        [379430]  = "kingdomcomedeliverance",
        [990080]  = "hogwartslegacy",
        [2358720] = "blackmythwukong",
        [2054970] = "dragonsdogma2",
        [294100]  = "rimworld",
        [105600]  = "terraria",
        [374320]  = "darksouls3",
        [570940]  = "darksoulsremastered",
        [2050650] = "residentevil42023",
        [1196590] = "residentevilvillage",
        [883710]  = "residentevil22019",
        [952060]  = "residentevil32020",
        [275850]  = "nomanssky",
        [1326470] = "sonsoftheforest",
        [367520]  = "hollowknight",
        [814380]  = "sekiro",
        [1888160] = "armoredcore6firesofrubicon",
        [1282100] = "remnant2",
        [1627720] = "liesofp",
        [2215430] = "ghostoftsushima",
        [2183900] = "spacemarine2"
    };

    public NexusModsService(
        HttpClient http,
        IModApiKeyService apiKeyService,
        ILogger<NexusModsService> logger,
        string? cacheRoot = null)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _apiKeyService = apiKeyService ?? throw new ArgumentNullException(nameof(apiKeyService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _cacheRoot = cacheRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "BlueStar", "data", "nexus_cache");

        if (_http.DefaultRequestHeaders.UserAgent.Count == 0)
        {
            _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("BlueStar-ModManager", "1.0"));
        }
    }

    /// <inheritdoc />
    public async Task<string?> ResolveGameDomainAsync(GameInstance instance, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(instance);

        if (instance.AppId > 0 && KnownAppIdToGameDomain.TryGetValue(instance.AppId, out var directDomain))
        {
            return directDomain;
        }

        var slug = Slugify(instance.Name);
        if (string.IsNullOrWhiteSpace(slug)) return null;

        var key = await _apiKeyService.GetEffectiveKeyAsync(ModApiProvider.NexusMods, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(key)) return null;

        try
        {
            var baseUrl = await _apiKeyService.GetApiBaseUrlAsync(ModApiProvider.NexusMods, ct).ConfigureAwait(false);
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl.TrimEnd('/')}/games/{slug}.json");
            req.Headers.Add("apikey", key);
            req.Headers.Add("Application-Name", "BlueStar");

            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            if (resp.IsSuccessStatusCode)
            {
                return slug;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Probing Nexus game domain for {Name} failed", instance.Name);
        }

        return null;
    }

    /// <inheritdoc />
    public bool CanHandleNxmUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        return NxmRegex.IsMatch(url.Trim());
    }

    /// <inheritdoc />
    public NexusNxmLink? ParseNxmUrl(string? nxmUrl)
    {
        if (string.IsNullOrWhiteSpace(nxmUrl)) return null;

        var match = NxmRegex.Match(nxmUrl.Trim());
        if (!match.Success) return null;

        if (!long.TryParse(match.Groups["mod"].Value, out var modId) ||
            !long.TryParse(match.Groups["file"].Value, out var fileId) ||
            !long.TryParse(match.Groups["expires"].Value, out var expires))
        {
            return null;
        }

        long? userId = null;
        if (match.Groups["user"].Success && long.TryParse(match.Groups["user"].Value, out var uid))
        {
            userId = uid;
        }

        return new NexusNxmLink
        {
            GameDomain = match.Groups["game"].Value,
            ModId = modId,
            FileId = fileId,
            Key = match.Groups["key"].Value,
            Expires = expires,
            UserId = userId
        };
    }

    /// <inheritdoc />
    public async Task<NexusModInfo?> GetModDetailsAsync(string gameDomain, long modId, CancellationToken ct = default)
    {
        var key = await _apiKeyService.GetEffectiveKeyAsync(ModApiProvider.NexusMods, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(key)) return null;

        try
        {
            var baseUrl = await _apiKeyService.GetApiBaseUrlAsync(ModApiProvider.NexusMods, ct).ConfigureAwait(false);
            var url = $"{baseUrl.TrimEnd('/')}/games/{gameDomain}/mods/{modId}.json";

            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Add("apikey", key);
            req.Headers.Add("Application-Name", "BlueStar");

            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;

            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            return new NexusModInfo
            {
                ModId = modId,
                Name = root.TryGetProperty("name", out var n) ? n.GetString() ?? $"Mod {modId}" : $"Mod {modId}",
                Summary = root.TryGetProperty("summary", out var s) ? s.GetString() ?? string.Empty : string.Empty,
                Description = root.TryGetProperty("description", out var d) ? d.GetString() : null,
                Author = root.TryGetProperty("author", out var a) ? a.GetString() : null,
                PictureUrl = root.TryGetProperty("picture_url", out var p) ? p.GetString() : null,
                GameDomain = gameDomain,
                TotalDownloads = root.TryGetProperty("mod_downloads", out var md) ? md.GetInt64() : 0,
                EndorsementCount = root.TryGetProperty("endorsement_count", out var ec) ? ec.GetInt64() : 0
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch mod info for Nexus mod {Domain}/{ModId}", gameDomain, modId);
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<NexusFileInfo?> GetFileDetailsAsync(string gameDomain, long modId, long fileId, CancellationToken ct = default)
    {
        var key = await _apiKeyService.GetEffectiveKeyAsync(ModApiProvider.NexusMods, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(key)) return null;

        try
        {
            var baseUrl = await _apiKeyService.GetApiBaseUrlAsync(ModApiProvider.NexusMods, ct).ConfigureAwait(false);
            var url = $"{baseUrl.TrimEnd('/')}/games/{gameDomain}/mods/{modId}/files/{fileId}.json";

            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Add("apikey", key);
            req.Headers.Add("Application-Name", "BlueStar");

            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;

            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            return new NexusFileInfo
            {
                FileId = fileId,
                Name = root.TryGetProperty("name", out var n) ? n.GetString() ?? $"File {fileId}" : $"File {fileId}",
                Version = root.TryGetProperty("version", out var v) ? v.GetString() : null,
                FileName = root.TryGetProperty("file_name", out var fn) ? fn.GetString() : null,
                SizeInBytes = root.TryGetProperty("size_in_bytes", out var sb) ? sb.GetInt64() :
                              root.TryGetProperty("size_kb", out var skb) ? skb.GetInt64() * 1024 : 0,
                Description = root.TryGetProperty("description", out var d) ? d.GetString() : null
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch file info for Nexus mod {Domain}/{ModId}/{FileId}", gameDomain, modId, fileId);
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<string?> GetDownloadUrlFromNxmAsync(string nxmUrl, CancellationToken ct = default)
    {
        var parsed = ParseNxmUrl(nxmUrl);
        if (parsed == null)
        {
            _logger.LogWarning("Invalid nxm:// URL: {Url}", nxmUrl);
            return null;
        }

        var key = await _apiKeyService.GetEffectiveKeyAsync(ModApiProvider.NexusMods, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(key))
        {
            _logger.LogWarning("Nexus Mods API key is not configured for NXM download");
            throw new InvalidOperationException("Se requiere una API Key de Nexus Mods en Ajustes para resolver descargas de Mod Manager (Vortex / nxm). Para cuentas gratuitas sin clave, pulsa 'Manual Download' en el mod y BlueStar lo instalará automáticamente.");
        }

        try
        {
            var baseUrl = await _apiKeyService.GetApiBaseUrlAsync(ModApiProvider.NexusMods, ct).ConfigureAwait(false);
            var endpoint = $"{baseUrl.TrimEnd('/')}/games/{parsed.GameDomain}/mods/{parsed.ModId}/files/{parsed.FileId}/download_link.json?key={Uri.EscapeDataString(parsed.Key)}&expires={parsed.Expires}";

            using var req = new HttpRequestMessage(HttpMethod.Get, endpoint);
            req.Headers.Add("apikey", key);
            req.Headers.Add("Application-Name", "BlueStar");

            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                _logger.LogError("Nexus download link request returned {StatusCode}: {Body}", resp.StatusCode, body);
                if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized || resp.StatusCode == System.Net.HttpStatusCode.Forbidden)
                {
                    throw new InvalidOperationException("La API Key de Nexus Mods no es válida o no tiene permisos. Verifica tu clave en Ajustes.");
                }
                if (resp.StatusCode == (System.Net.HttpStatusCode)429)
                {
                    throw new InvalidOperationException("Límite de peticiones de Nexus Mods alcanzado. Espera unos minutos e inténtalo de nuevo.");
                }
                throw new InvalidOperationException($"Nexus Mods devolvió error {(int)resp.StatusCode} ({resp.StatusCode}).");
            }

            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return null;

            foreach (var elem in doc.RootElement.EnumerateArray())
            {
                if (elem.TryGetProperty("URI", out var uri) && !string.IsNullOrWhiteSpace(uri.GetString()))
                {
                    return uri.GetString();
                }
            }

            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to generate download link from nxm:// link");
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<ModPackage?> DownloadFromNxmAsync(
        GameInstance instance,
        string nxmUrl,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(instance);

        var parsed = ParseNxmUrl(nxmUrl);
        if (parsed == null)
        {
            _logger.LogWarning("Cannot download invalid nxm URL: {Url}", nxmUrl);
            return null;
        }

        var directUrl = await GetDownloadUrlFromNxmAsync(nxmUrl, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(directUrl))
        {
            _logger.LogWarning("Failed to resolve CDN download link for nxm URL");
            return null;
        }

        // Fetch mod & file details in parallel for metadata
        var modInfoTask = GetModDetailsAsync(parsed.GameDomain, parsed.ModId, ct);
        var fileInfoTask = GetFileDetailsAsync(parsed.GameDomain, parsed.ModId, parsed.FileId, ct);

        var dir = Path.Combine(_cacheRoot, parsed.GameDomain);
        Directory.CreateDirectory(dir);

        var fileInfo = await fileInfoTask.ConfigureAwait(false);
        var modInfo = await modInfoTask.ConfigureAwait(false);

        var safeFileName = !string.IsNullOrWhiteSpace(fileInfo?.FileName)
            ? $"{parsed.ModId}_{parsed.FileId}_{Path.GetFileName(fileInfo.FileName)}"
            : $"{parsed.ModId}_{parsed.FileId}.zip";
        var destPath = Path.Combine(dir, safeFileName);

        if (File.Exists(destPath) && new FileInfo(destPath).Length > 0)
        {
            progress?.Report(100.0);
            return CreatePackage(destPath, parsed, modInfo, fileInfo);
        }

        try
        {
            progress?.Report(5.0);
            using var response = await _http.GetAsync(directUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var totalBytes = response.Content.Headers.ContentLength ?? fileInfo?.SizeInBytes ?? 0;
            var tempFile = Path.Combine(Path.GetTempPath(), $"nx_{Guid.NewGuid():N}.tmp");

            await using (var contentStream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            await using (var fileStream = new FileStream(tempFile, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                var buffer = new byte[81920];
                long totalRead = 0;
                int bytesRead;

                while ((bytesRead = await contentStream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false)) > 0)
                {
                    await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), ct).ConfigureAwait(false);
                    totalRead += bytesRead;

                    if (totalBytes > 0)
                    {
                        var pct = Math.Min(95.0, 5.0 + ((double)totalRead / totalBytes * 90.0));
                        progress?.Report(pct);
                    }
                }
            }

            File.Move(tempFile, destPath, overwrite: true);
            progress?.Report(100.0);

            return CreatePackage(destPath, parsed, modInfo, fileInfo);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to download Nexus mod {ModId} file {FileId}", parsed.ModId, parsed.FileId);
            return null;
        }
    }

    private static ModPackage CreatePackage(string filePath, NexusNxmLink parsed, NexusModInfo? mod, NexusFileInfo? file)
    {
        var modName = mod?.Name ?? file?.Name ?? $"Nexus Mod {parsed.ModId}";
        return new ModPackage
        {
            Path = filePath,
            Name = modName,
            Source = new ModSourceRef
            {
                Provider = "nexus",
                RemoteId = $"{parsed.GameDomain}:{parsed.ModId}:{parsed.FileId}",
                Version = file?.Version,
                FileId = parsed.FileId.ToString(),
                PageUrl = $"https://www.nexusmods.com/{parsed.GameDomain}/mods/{parsed.ModId}",
                IconUrl = mod?.PictureUrl
            },
            Layout = ModPackageLayout.Unknown
        };
    }

    /// <inheritdoc />
    public void RegisterProtocolHandler()
    {
        if (!OperatingSystem.IsWindows()) return;

        try
        {
            var exePath = Environment.ProcessPath ?? System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
            if (string.IsNullOrEmpty(exePath)) return;

            string[] schemes = ["nxm", "vortex", "nxm-protocol"];
            foreach (var scheme in schemes)
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey($@"Software\Classes\{scheme}");
                if (key == null) continue;
                key.SetValue("", $"URL:{scheme.ToUpperInvariant()} Protocol");
                key.SetValue("URL Protocol", "");

                using var iconKey = key.CreateSubKey("DefaultIcon");
                iconKey?.SetValue("", $"\"{exePath}\",0");

                using var cmdKey = key.CreateSubKey(@"shell\open\command");
                cmdKey?.SetValue("", $"\"{exePath}\" \"%1\"");
            }

            _logger.LogInformation("Successfully registered nxm/vortex protocol handlers for BlueStar");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not register nxm/vortex protocol handlers in registry");
        }
    }

    private static string Slugify(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return string.Empty;
        var clean = Regex.Replace(name, @"[^\w\s-]", "").Trim().ToLowerInvariant();
        return Regex.Replace(clean, @"[-\s]+", "");
    }
}
