using System;
using System.Collections.Concurrent;
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

namespace BlueStar.Infrastructure.Mods.Thunderstore;

/// <summary>
/// Client for discovering and downloading mods from Thunderstore's public community API.
/// </summary>
public sealed class ThunderstoreService : IThunderstoreService
{
    private readonly HttpClient _http;
    private readonly ILogger<ThunderstoreService> _logger;
    private readonly string _cacheRoot;

    private static readonly TimeSpan CommunityCacheExpiry = TimeSpan.FromHours(1);
    private static readonly TimeSpan PackageCacheExpiry = TimeSpan.FromMinutes(10);

    private IReadOnlyList<ThunderstoreCommunity>? _cachedCommunities;
    private DateTimeOffset _communityExpiry = DateTimeOffset.MinValue;

    private readonly ConcurrentDictionary<string, (IReadOnlyList<ThunderstorePackage> Packages, DateTimeOffset Expiry)>
        _cachedCommunityPackages = new(StringComparer.OrdinalIgnoreCase);

    // Fast-path lookup for well-known Steam games with official Thunderstore communities
    private static readonly Dictionary<uint, string> KnownAppIdToCommunity = new()
    {
        [3241660] = "repo",
        [1966720] = "lethal-company",
        [892970] = "valheim",
        [632360] = "riskofrain2",
        [1229490] = "ultrakill",
        [1623730] = "palworld",
        [945360] = "among-us",
        [1366540] = "dyson-sphere-program",
        [1557740] = "rounds",
        [1604030] = "v-rising",
        [2881650] = "content-warning",
        [960090] = "bloons-td-6",
        [976730] = "halo-the-master-chief-collection",
        [629730] = "blade-and-sorcery",
        [1086940] = "baldurs-gate-3",
        [1145360] = "hades"
    };

    public ThunderstoreService(
        HttpClient http,
        ILogger<ThunderstoreService> logger,
        string? cacheRoot = null)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _cacheRoot = cacheRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "BlueStar", "data", "thunderstore_cache");

        if (_http.DefaultRequestHeaders.UserAgent.Count == 0)
        {
            _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("BlueStar-ModManager", "1.0"));
        }
    }

    /// <inheritdoc />
    public async Task<string?> ResolveCommunityAsync(GameInstance instance, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(instance);

        // 1. Direct AppID lookup
        if (instance.AppId > 0 && KnownAppIdToCommunity.TryGetValue(instance.AppId, out var directCommunity))
        {
            return directCommunity;
        }

        var slug = Slugify(instance.Name);

        // 2. Direct probe fallback: check if community packages endpoint exists for slug
        if (!string.IsNullOrWhiteSpace(slug))
        {
            try
            {
                var probeUrl = $"https://thunderstore.io/c/{slug}/api/v1/package/";
                using var req = new HttpRequestMessage(HttpMethod.Head, probeUrl);
                using var probeResp = await _http.SendAsync(req, ct).ConfigureAwait(false);
                if (probeResp.IsSuccessStatusCode)
                {
                    return slug;
                }
            }
            catch { }
        }

        // 3. Query communities list
        var communities = await GetCommunitiesAsync(ct).ConfigureAwait(false);
        if (communities.Count == 0) return null;

        // Match exact identifier or exact name
        var matched = communities.FirstOrDefault(c =>
            c.Identifier.Equals(slug, StringComparison.OrdinalIgnoreCase) ||
            c.Name.Equals(instance.Name, StringComparison.OrdinalIgnoreCase));

        if (matched != null) return matched.Identifier;

        // Substring / fuzzy match
        matched = communities.FirstOrDefault(c =>
            slug.Contains(c.Identifier, StringComparison.OrdinalIgnoreCase) ||
            c.Identifier.Contains(slug, StringComparison.OrdinalIgnoreCase) ||
            c.Name.Contains(instance.Name, StringComparison.OrdinalIgnoreCase));

        return matched?.Identifier;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ThunderstoreCommunity>> GetCommunitiesAsync(CancellationToken ct = default)
    {
        if (_cachedCommunities != null && DateTimeOffset.UtcNow < _communityExpiry)
        {
            return _cachedCommunities;
        }

        try
        {
            const string url = "https://thunderstore.io/api/experimental/community/";
            using var response = await _http.GetAsync(url, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Thunderstore communities returned HTTP {Code}", response.StatusCode);
                return _cachedCommunities ?? [];
            }

            using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);

            JsonElement itemsElement;
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                itemsElement = doc.RootElement;
            }
            else if (doc.RootElement.TryGetProperty("results", out var resultsElem) && resultsElem.ValueKind == JsonValueKind.Array)
            {
                itemsElement = resultsElem;
            }
            else
            {
                _logger.LogWarning("Unexpected Thunderstore communities format");
                return _cachedCommunities ?? [];
            }

            var list = new List<ThunderstoreCommunity>();
            foreach (var elem in itemsElement.EnumerateArray())
            {
                var identifier = elem.GetProperty("identifier").GetString();
                var name = elem.GetProperty("name").GetString();
                if (string.IsNullOrWhiteSpace(identifier) || string.IsNullOrWhiteSpace(name)) continue;

                var discordUrl = elem.TryGetProperty("discord_url", out var d) ? d.GetString() : null;
                var wikiUrl = elem.TryGetProperty("wiki_url", out var w) ? w.GetString() : null;
                var packages = elem.TryGetProperty("packages", out var p) ? p.GetInt32() : 0;
                var downloads = elem.TryGetProperty("download_count", out var dl) ? dl.GetInt64() : 0;

                list.Add(new ThunderstoreCommunity
                {
                    Identifier = identifier,
                    Name = name,
                    DiscordUrl = discordUrl,
                    WikiUrl = wikiUrl,
                    Packages = packages,
                    DownloadCount = downloads
                });
            }

            _cachedCommunities = list;
            _communityExpiry = DateTimeOffset.UtcNow.Add(CommunityCacheExpiry);
            return list;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load Thunderstore communities");
            return _cachedCommunities ?? [];
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ThunderstorePackage>> GetPackagesAsync(string community, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(community)) return [];

        if (_cachedCommunityPackages.TryGetValue(community, out var cacheEntry) &&
            DateTimeOffset.UtcNow < cacheEntry.Expiry)
        {
            return cacheEntry.Packages;
        }

        try
        {
            var url = $"https://thunderstore.io/c/{community.Trim().ToLowerInvariant()}/api/v1/package/";
            using var response = await _http.GetAsync(url, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Thunderstore packages for community {Community} returned HTTP {Code}", community, response.StatusCode);
                return [];
            }

            using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);

            var packages = new List<ThunderstorePackage>();
            foreach (var elem in doc.RootElement.EnumerateArray())
            {
                var name = elem.GetProperty("name").GetString() ?? string.Empty;
                var fullName = elem.GetProperty("full_name").GetString() ?? string.Empty;
                var owner = elem.GetProperty("owner").GetString() ?? string.Empty;
                var packageUrl = elem.GetProperty("package_url").GetString() ?? string.Empty;
                var donation = elem.TryGetProperty("donation_link", out var don) ? don.GetString() : null;
                var dateCreated = elem.TryGetProperty("date_created", out var dc) && dc.TryGetDateTimeOffset(out var dtoCreated) ? dtoCreated : DateTimeOffset.MinValue;
                var dateUpdated = elem.TryGetProperty("date_updated", out var du) && du.TryGetDateTimeOffset(out var dtoUpdated) ? dtoUpdated : DateTimeOffset.MinValue;
                var rating = elem.TryGetProperty("rating_score", out var r) ? r.GetInt32() : 0;
                var isPinned = elem.TryGetProperty("is_pinned", out var ip) && ip.GetBoolean();
                var isDeprecated = elem.TryGetProperty("is_deprecated", out var idep) && idep.GetBoolean();
                var totalDownloads = elem.TryGetProperty("total_downloads", out var td) ? td.GetInt64() : 0;
                var hasNsfw = elem.TryGetProperty("has_nsfw_content", out var nsfw) && nsfw.GetBoolean();

                var categories = new List<string>();
                if (elem.TryGetProperty("categories", out var cats) && cats.ValueKind == JsonValueKind.Array)
                {
                    foreach (var c in cats.EnumerateArray())
                    {
                        var cs = c.GetString();
                        if (!string.IsNullOrWhiteSpace(cs)) categories.Add(cs);
                    }
                }

                var versions = new List<ThunderstorePackageVersion>();
                if (elem.TryGetProperty("versions", out var vers) && vers.ValueKind == JsonValueKind.Array)
                {
                    var isLatest = true;
                    foreach (var v in vers.EnumerateArray())
                    {
                        var vName = v.GetProperty("name").GetString() ?? string.Empty;
                        var vFull = v.GetProperty("full_name").GetString() ?? string.Empty;
                        var vNum = v.GetProperty("version_number").GetString() ?? string.Empty;
                        var vDlUrl = v.TryGetProperty("download_url", out var dl) ? dl.GetString() ?? string.Empty : string.Empty;
                        var vDownloads = v.TryGetProperty("downloads", out var vdls) ? vdls.GetInt32() : 0;
                        var vDate = v.TryGetProperty("date_created", out var vdc) && vdc.TryGetDateTimeOffset(out var vdto) ? vdto : DateTimeOffset.MinValue;
                        var vFileSize = v.TryGetProperty("file_size", out var fs) ? fs.GetInt64() : 0;

                        string vDesc;
                        string vIcon;
                        string? vWebsite;
                        bool vActive;
                        IReadOnlyList<string> deps;

                        if (isLatest)
                        {
                            // First / latest version: full parse for package card presentation
                            vDesc = v.TryGetProperty("description", out var desc) ? desc.GetString() ?? string.Empty : string.Empty;
                            vIcon = v.TryGetProperty("icon", out var icon) ? icon.GetString() ?? string.Empty : string.Empty;
                            vWebsite = v.TryGetProperty("website_url", out var web) ? web.GetString() : null;
                            vActive = !v.TryGetProperty("is_active", out var act) || act.GetBoolean();

                            var depList = new List<string>();
                            if (v.TryGetProperty("dependencies", out var depArr) && depArr.ValueKind == JsonValueKind.Array)
                            {
                                foreach (var dep in depArr.EnumerateArray())
                                {
                                    var ds = dep.GetString();
                                    if (!string.IsNullOrWhiteSpace(ds)) depList.Add(ds);
                                }
                            }
                            deps = depList;
                            isLatest = false;
                        }
                        else
                        {
                            // Older versions: lightweight parse for dropdown selection & installation
                            vDesc = string.Empty;
                            vIcon = string.Empty;
                            vWebsite = null;
                            vActive = true;
                            deps = [];
                        }

                        versions.Add(new ThunderstorePackageVersion
                        {
                            Name = vName,
                            FullName = vFull,
                            VersionNumber = vNum,
                            Description = vDesc,
                            IconUrl = vIcon,
                            DownloadUrl = vDlUrl,
                            Downloads = vDownloads,
                            DateCreated = vDate,
                            WebsiteUrl = vWebsite,
                            IsActive = vActive,
                            FileSize = vFileSize,
                            Dependencies = deps
                        });
                    }
                }

                packages.Add(new ThunderstorePackage
                {
                    Name = name,
                    FullName = fullName,
                    Owner = owner,
                    PackageUrl = packageUrl,
                    DonationLink = donation,
                    DateCreated = dateCreated,
                    DateUpdated = dateUpdated,
                    RatingScore = rating,
                    IsPinned = isPinned,
                    IsDeprecated = isDeprecated,
                    TotalDownloads = totalDownloads,
                    HasNsfwContent = hasNsfw,
                    Categories = categories,
                    Versions = versions
                });
            }

            _cachedCommunityPackages[community] = (packages, DateTimeOffset.UtcNow.Add(PackageCacheExpiry));
            return packages;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load packages for Thunderstore community {Community}", community);
            return [];
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ThunderstorePackage>> SearchPackagesAsync(
        string community,
        string? query = null,
        string? category = null,
        string sortBy = "Downloads",
        bool isAscending = false,
        CancellationToken ct = default)
    {
        var all = await GetPackagesAsync(community, ct).ConfigureAwait(false);
        if (all.Count == 0) return [];

        var filtered = all.Where(p => !p.IsDeprecated);

        if (!string.IsNullOrWhiteSpace(category) && !category.Equals("All", StringComparison.OrdinalIgnoreCase))
        {
            filtered = filtered.Where(p => p.Categories.Any(c => c.Equals(category, StringComparison.OrdinalIgnoreCase)));
        }

        if (!string.IsNullOrWhiteSpace(query))
        {
            var terms = query.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            filtered = filtered.Where(p => terms.All(term =>
                p.Name.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                p.Owner.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                p.Description.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                p.Categories.Any(c => c.Contains(term, StringComparison.OrdinalIgnoreCase))));
        }

        if (isAscending)
        {
            return sortBy switch
            {
                "Rating" => filtered.OrderBy(p => p.RatingScore).ToList(),
                "Updated" => filtered.OrderBy(p => p.DateUpdated).ToList(),
                "Name" => filtered.OrderByDescending(p => p.Name).ToList(),
                _ => filtered.OrderBy(p => p.TotalDownloads).ToList()
            };
        }
        else
        {
            return sortBy switch
            {
                "Rating" => filtered.OrderByDescending(p => p.IsPinned).ThenByDescending(p => p.RatingScore).ToList(),
                "Updated" => filtered.OrderByDescending(p => p.IsPinned).ThenByDescending(p => p.DateUpdated).ToList(),
                "Name" => filtered.OrderBy(p => p.Name).ToList(),
                _ => filtered.OrderByDescending(p => p.IsPinned).ThenByDescending(p => p.TotalDownloads).ToList()
            };
        }
    }

    /// <inheritdoc />
    public async Task<ModPackage?> DownloadPackageAsync(
        string community,
        ThunderstorePackage package,
        ThunderstorePackageVersion? version = null,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(package);
        if (string.IsNullOrWhiteSpace(community)) throw new ArgumentException("Community is required", nameof(community));

        var targetVersion = version ?? package.LatestVersion;
        if (targetVersion == null || string.IsNullOrWhiteSpace(targetVersion.DownloadUrl))
        {
            _logger.LogWarning("No download URL for package {Package}", package.FullName);
            return null;
        }

        var dir = Path.Combine(_cacheRoot, community);
        Directory.CreateDirectory(dir);

        var fileName = $"{package.Owner}_{package.Name}_{targetVersion.VersionNumber}.zip";
        var zipPath = Path.Combine(dir, fileName);

        if (File.Exists(zipPath) && new FileInfo(zipPath).Length > 0)
        {
            progress?.Report(100.0);
            return CreatePackage(zipPath, package, community, targetVersion);
        }

        try
        {
            progress?.Report(5.0);
            using var response = await _http.GetAsync(targetVersion.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var totalBytes = response.Content.Headers.ContentLength ?? targetVersion.FileSize;
            var tempFile = Path.Combine(Path.GetTempPath(), $"ts_{Guid.NewGuid():N}.zip");

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

            File.Move(tempFile, zipPath, overwrite: true);
            progress?.Report(100.0);

            return CreatePackage(zipPath, package, community, targetVersion);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to download Thunderstore package {Package} version {Version}", package.FullName, targetVersion.VersionNumber);
            return null;
        }
    }

    private static ModPackage CreatePackage(string zipPath, ThunderstorePackage package, string community, ThunderstorePackageVersion version)
    {
        return new ModPackage
        {
            Path = zipPath,
            Name = package.Name,
            Source = new ModSourceRef
            {
                Provider = "thunderstore",
                RemoteId = $"{community}:{package.Owner}:{package.Name}",
                Version = version.VersionNumber,
                PageUrl = package.PackageUrl
            },
            Layout = ModPackageLayout.BepInExPackage
        };
    }

    private static string Slugify(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return string.Empty;
        var clean = Regex.Replace(name, @"[^\w\s-]", "").Trim().ToLowerInvariant();
        return Regex.Replace(clean, @"[-\s]+", "-");
    }
}
