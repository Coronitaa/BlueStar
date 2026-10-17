using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using BlueStar.Core.Helpers;
using BlueStar.Core.Interfaces;
using BlueStar.Core.Models;
using BlueStar.Infrastructure.Metadata;
using BlueStar.Infrastructure.Steam;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BlueStar.Infrastructure.Services;

/// <summary>
/// Implements <see cref="ISteamLicenseDiscoveryService"/>, discovering owned AppIDs and packages
/// from the user's authentic Steam account via secure in-memory DPAPI cookies and local client cache.
/// 
/// PRIVACY & INTEGRITY INVARIANTS:
/// 1. NEVER transmits SteamID, credentials, session cookies, tokens, usernames, or the full license list
///    to the community Manifest Hub, telemetry servers, or any external host.
/// 2. Session cookies are decrypted in-memory using DPAPI and zero plain text secrets are ever stored on disk or logged.
/// 3. If Steam licenses cannot be obtained (e.g. unauthenticated or no session), reports the real blockage cause
///    and NEVER silently falls back to local installed game manifests.
/// 4. Applies <see cref="IDlcPayloadClassifier"/> to DLCs: includes DownloadablePayload DLCs, excludes EntitlementOnly DLCs
///    from community contribution generation.
/// </summary>
public sealed class SteamLicenseDiscoveryService : ISteamLicenseDiscoveryService
{
    private readonly HttpClient _http;
    private readonly ISteamSessionCookieProvider _cookieProvider;
    private readonly SteamStoreApiClient _metadataProvider;
    private readonly IDlcPayloadClassifier _dlcClassifier;
    private readonly SteamPackageResolver _packageResolver;
    private readonly ILogger<SteamLicenseDiscoveryService> _logger;

    public SteamLicenseDiscoveryService(
        HttpClient http,
        ISteamSessionCookieProvider cookieProvider,
        SteamStoreApiClient metadataProvider,
        IDlcPayloadClassifier dlcClassifier,
        SteamPackageResolver? packageResolver = null,
        ILogger<SteamLicenseDiscoveryService>? logger = null)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _cookieProvider = cookieProvider ?? throw new ArgumentNullException(nameof(cookieProvider));
        _metadataProvider = metadataProvider ?? throw new ArgumentNullException(nameof(metadataProvider));
        _dlcClassifier = dlcClassifier ?? throw new ArgumentNullException(nameof(dlcClassifier));
        _logger = logger ?? NullLogger<SteamLicenseDiscoveryService>.Instance;
        _packageResolver = packageResolver ?? new SteamPackageResolver(_http, NullLogger<SteamPackageResolver>.Instance);
    }

    /// <inheritdoc />
    public Task<SteamLicenseDiscoveryResult> DiscoverLicensesAsync(CancellationToken ct = default)
    {
        return DiscoverLicensesAsync(new SteamLicenseDiscoveryOptions(), ct);
    }

    /// <inheritdoc />
    public async Task<SteamLicenseDiscoveryResult> DiscoverLicensesAsync(
        SteamLicenseDiscoveryOptions options,
        CancellationToken ct = default)
    {
        options ??= new SteamLicenseDiscoveryOptions();

        var discoveredAppIds = new HashSet<uint>();
        var discoveredPackageIds = new HashSet<uint>();
        var packageToAppsMap = new Dictionary<uint, List<uint>>();
        var appToPackagesMap = new Dictionary<uint, List<uint>>();
        string sourceRoute = "SteamWebSession";

        bool hasSessionCookies = false;
        bool webSessionSucceeded = false;

        // 1. Check active Steam session cookies via in-memory DPAPI
        if (options.IncludeWebSession)
        {
            var cookies = await _cookieProvider.GetSteamSessionCookiesAsync(ct).ConfigureAwait(false);
            if (cookies != null && cookies.TryGetValue("steamLoginSecure", out var loginSecure) &&
                !string.IsNullOrWhiteSpace(loginSecure) &&
                !loginSecure.Equals("deleted", StringComparison.OrdinalIgnoreCase))
            {
                hasSessionCookies = true;

                try
                {
                    var webResult = await QuerySteamUserDataAsync(cookies, ct).ConfigureAwait(false);
                    if (webResult.IsAuthenticated)
                    {
                        webSessionSucceeded = true;
                        foreach (var app in webResult.OwnedApps) discoveredAppIds.Add(app);
                        foreach (var pkg in webResult.OwnedPackages) discoveredPackageIds.Add(pkg);
                        sourceRoute = "SteamWebSession";
                    }
                    else
                    {
                        // Check /account/licenses/ as secondary authenticated verification
                        var accountLicensesResult = await QueryAccountLicensesPageAsync(cookies, ct).ConfigureAwait(false);
                        if (accountLicensesResult.IsAuthenticated)
                        {
                            webSessionSucceeded = true;
                            foreach (var app in accountLicensesResult.OwnedApps) discoveredAppIds.Add(app);
                            foreach (var pkg in accountLicensesResult.OwnedPackages) discoveredPackageIds.Add(pkg);
                            sourceRoute = "SteamWebLicenses";
                        }
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (HttpRequestException ex)
                {
                    _logger.LogWarning(ex, "Network error communicating with Steam Web API during license discovery. Attempting local client cache fallback.");
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Unexpected error querying Steam Web API");
                }
            }
        }

        // 2. Check local Steam client cache inspection when available or requested
        var steamPath = ShortcutHelper.GetSteamPath();
        if (options.IncludeLocalClientCache && !string.IsNullOrWhiteSpace(steamPath))
        {
            try
            {
                var localLicenses = InspectLocalSteamClientLicenses(steamPath);
                if (localLicenses.Count > 0)
                {
                    foreach (var app in localLicenses) discoveredAppIds.Add(app);
                    sourceRoute = webSessionSucceeded ? "Hybrid" : "LocalClientCache";
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to read local Steam client licenses from {Path}", steamPath);
            }
        }

        // 3. Evaluate Blockage: NEVER fall back silently to installed game manifests!
        if (!webSessionSucceeded && discoveredAppIds.Count == 0 && discoveredPackageIds.Count == 0)
        {
            if (hasSessionCookies)
            {
                _logger.LogWarning("Steam license discovery blocked: Active session cookie is expired or unauthenticated");
                return SteamLicenseDiscoveryResult.Blocked(
                    SteamLicenseBlockageReason.Unauthenticated,
                    "Steam session is expired or unauthenticated. Please log in to Steam or open the Steam client.");
            }
            else
            {
                _logger.LogWarning("Steam license discovery blocked: No active Steam session or cookies detected");
                return SteamLicenseDiscoveryResult.Blocked(
                    SteamLicenseBlockageReason.NoSteamSession,
                    "No active Steam session detected. Please log in to the Steam client or browser.");
            }
        }

        // 4. Resolve packages/subs into AppIDs
        if (options.ResolvePackages && discoveredPackageIds.Count > 0)
        {
            try
            {
                foreach (var pkgId in discoveredPackageIds)
                {
                    ct.ThrowIfCancellationRequested();
                    var appsInPkg = await _packageResolver.ResolvePackageAppIdsAsync(pkgId, steamPath, ct).ConfigureAwait(false);
                    packageToAppsMap[pkgId] = appsInPkg.ToList();

                    foreach (var appId in appsInPkg)
                    {
                        discoveredAppIds.Add(appId);
                        if (!appToPackagesMap.TryGetValue(appId, out var pkgList))
                        {
                            pkgList = [];
                            appToPackagesMap[appId] = pkgList;
                        }
                        if (!pkgList.Contains(pkgId)) pkgList.Add(pkgId);
                    }
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Error resolving Steam packages into AppIDs");
            }
        }

        if (discoveredAppIds.Count == 0)
        {
            return SteamLicenseDiscoveryResult.Blocked(
                SteamLicenseBlockageReason.NoLicensesFound,
                "Authenticated account owns 0 application licenses.");
        }

        // 5. Build license items, enrich metadata and apply IDlcPayloadClassifier
        var resultList = new List<SteamLicenseItem>();

        foreach (var appId in discoveredAppIds.OrderBy(id => id))
        {
            ct.ThrowIfCancellationRequested();

            string name = $"App {appId}";
            bool isDlc = false;
            uint? parentAppId = null;
            DlcClassification? classification = null;
            bool isEligible = true;

            var packagesForApp = appToPackagesMap.TryGetValue(appId, out var pList)
                ? pList.AsReadOnly()
                : (IReadOnlyList<uint>)[];

            if (options.EnrichMetadata)
            {
                try
                {
                    var meta = await _metadataProvider.GetMetadataAsync(appId, ct).ConfigureAwait(false);
                    if (meta != null && !string.IsNullOrWhiteSpace(meta.Name))
                    {
                        name = meta.Name;
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Metadata enrichment failed for AppId {AppId}", appId);
                }
            }

            resultList.Add(new SteamLicenseItem
            {
                AppId = appId,
                Name = name,
                IsDlc = isDlc,
                ParentAppId = parentAppId,
                Classification = classification,
                IsEligibleForContribution = isEligible,
                PackageIds = packagesForApp,
                SourceRoute = sourceRoute
            });
        }

        // 6. Discover and classify associated DLCs for base games
        if (options.ClassifyDlcs)
        {
            var baseApps = resultList.Where(a => !a.IsDlc).ToList();
            foreach (var baseApp in baseApps)
            {
                ct.ThrowIfCancellationRequested();

                try
                {
                    var dlcs = await _metadataProvider.GetDlcListAsync(baseApp.AppId, ct).ConfigureAwait(false);
                    if (dlcs.Count > 0)
                    {
                        var classifications = _dlcClassifier.ClassifyAll(baseApp.AppId, dlcs);
                        foreach (var c in classifications)
                        {
                            // If user owns this DLC or if discovered under base game:
                            // Classify DLC: include DownloadablePayload, exclude EntitlementOnly
                            var isDownloadable = c.PayloadType == DlcPayloadType.DownloadablePayload;

                            // Check if already in list
                            var existingIdx = resultList.FindIndex(item => item.AppId == c.DlcAppId);
                            var dlcItem = new SteamLicenseItem
                            {
                                AppId = c.DlcAppId,
                                Name = c.Name,
                                IsDlc = true,
                                ParentAppId = baseApp.AppId,
                                Classification = c,
                                IsEligibleForContribution = isDownloadable,
                                SourceRoute = sourceRoute
                            };

                            if (existingIdx >= 0)
                            {
                                resultList[existingIdx] = dlcItem;
                            }
                            else if (discoveredAppIds.Contains(c.DlcAppId))
                            {
                                resultList.Add(dlcItem);
                            }
                        }
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "DLC inspection failed for Base Game {AppId}", baseApp.AppId);
                }
            }
        }

        var result = SteamLicenseDiscoveryResult.Success(
            resultList.OrderBy(a => a.AppId).ToList(),
            discoveredPackageIds.ToList());

        // PRIVACY INVARIANT: Never log credentials, session tokens, SteamID, or license list
        _logger.LogInformation(
            "Discovered {Total} licensed Steam applications ({DownloadableDlc} downloadable DLCs, {EntitlementOnlyDlc} entitlement DLCs). Zero user data transmitted.",
            result.TotalLicensedAppsCount,
            result.DownloadableDlcCount,
            result.EntitlementOnlyDlcCount);

        return result;
    }

    private async Task<(bool IsAuthenticated, List<uint> OwnedApps, List<uint> OwnedPackages)> QuerySteamUserDataAsync(
        IReadOnlyDictionary<string, string> cookies,
        CancellationToken ct)
    {
        var ownedApps = new List<uint>();
        var ownedPackages = new List<uint>();

        var cookieHeader = BuildSanitizedCookieHeader(cookies);
        if (string.IsNullOrWhiteSpace(cookieHeader))
            return (false, ownedApps, ownedPackages);

        using var req = new HttpRequestMessage(HttpMethod.Get, "https://store.steampowered.com/dynamicstore/userdata/");
        req.Headers.Add("Cookie", cookieHeader);
        req.Headers.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");

        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            return (false, ownedApps, ownedPackages);

        var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);

        // When authenticated, Steam returns rgOwnedApps and rgOwnedPackages
        bool hasApps = false;
        if (doc.RootElement.TryGetProperty("rgOwnedApps", out var appsEl) && appsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in appsEl.EnumerateArray())
            {
                if (item.TryGetUInt32(out var appId) && appId > 0)
                {
                    ownedApps.Add(appId);
                    hasApps = true;
                }
            }
        }

        if (doc.RootElement.TryGetProperty("rgOwnedPackages", out var pkgsEl) && pkgsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in pkgsEl.EnumerateArray())
            {
                if (item.TryGetUInt32(out var pkgId) && pkgId > 0)
                {
                    ownedPackages.Add(pkgId);
                }
            }
        }

        return (hasApps || ownedPackages.Count > 0, ownedApps, ownedPackages);
    }

    private async Task<(bool IsAuthenticated, List<uint> OwnedApps, List<uint> OwnedPackages)> QueryAccountLicensesPageAsync(
        IReadOnlyDictionary<string, string> cookies,
        CancellationToken ct)
    {
        var ownedApps = new List<uint>();
        var ownedPackages = new List<uint>();

        var cookieHeader = BuildSanitizedCookieHeader(cookies);
        if (string.IsNullOrWhiteSpace(cookieHeader))
            return (false, ownedApps, ownedPackages);

        using var req = new HttpRequestMessage(HttpMethod.Get, "https://store.steampowered.com/account/licenses/");
        req.Headers.Add("Cookie", cookieHeader);
        req.Headers.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");

        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            return (false, ownedApps, ownedPackages);

        var html = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        // Check if page redirected to login
        if (html.Contains("id=\"login_btn_signin\"", StringComparison.OrdinalIgnoreCase) ||
            html.Contains("action=\"https://store.steampowered.com/login", StringComparison.OrdinalIgnoreCase))
        {
            return (false, ownedApps, ownedPackages);
        }

        // Parse AppIDs and PackageIDs from /account/licenses/
        var appMatches = Regex.Matches(html, @"https?://store\.steampowered\.com/app/(\d+)", RegexOptions.IgnoreCase);
        foreach (Match m in appMatches)
        {
            if (uint.TryParse(m.Groups[1].Value, out var appId) && appId > 0)
            {
                ownedApps.Add(appId);
            }
        }

        var removeMatches = Regex.Matches(html, @"RemoveFreeLicense\(\s*(\d+)", RegexOptions.IgnoreCase);
        foreach (Match m in removeMatches)
        {
            if (uint.TryParse(m.Groups[1].Value, out var pkgId) && pkgId > 0)
            {
                ownedPackages.Add(pkgId);
            }
        }

        var subMatches = Regex.Matches(html, @"https?://store\.steampowered\.com/sub/(\d+)", RegexOptions.IgnoreCase);
        foreach (Match m in subMatches)
        {
            if (uint.TryParse(m.Groups[1].Value, out var pkgId) && pkgId > 0)
            {
                ownedPackages.Add(pkgId);
            }
        }

        return (ownedApps.Count > 0 || ownedPackages.Count > 0, ownedApps, ownedPackages);
    }

    internal static string BuildSanitizedCookieHeader(IReadOnlyDictionary<string, string> cookies)
    {
        if (cookies == null || cookies.Count == 0) return string.Empty;

        var allowedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "steamLoginSecure",
            "sessionid",
            "steamCountry",
            "browserid",
            "timezoneOffset"
        };

        var validPairs = new List<string>();
        foreach (var (key, rawValue) in cookies)
        {
            if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(rawValue)) continue;
            if (!allowedKeys.Contains(key)) continue;

            if (!IsStrictAscii(key)) continue;

            var safeValue = IsStrictAscii(rawValue) ? rawValue : Uri.EscapeDataString(rawValue);
            validPairs.Add($"{key}={safeValue}");
        }

        return string.Join("; ", validPairs);
    }

    private static bool IsStrictAscii(string str)
    {
        if (string.IsNullOrEmpty(str)) return false;
        foreach (char c in str)
        {
            if (c < 32 || c > 126) return false;
        }
        return true;
    }

    internal static List<uint> InspectLocalSteamClientLicenses(string steamPath)
    {
        var appIds = new HashSet<uint>();
        var userdata = Path.Combine(steamPath, "userdata");
        if (Directory.Exists(userdata))
        {
            var userDirs = Directory.GetDirectories(userdata);
            foreach (var userDir in userDirs)
            {
                var localConfig = Path.Combine(userDir, "config", "localconfig.vdf");
                if (File.Exists(localConfig))
                {
                    try
                    {
                        ExtractAppIdsFromVdf(File.ReadAllText(localConfig), appIds);
                    }
                    catch { }
                }

                var sharedConfig = Path.Combine(userDir, "7", "remote", "sharedconfig.vdf");
                if (File.Exists(sharedConfig))
                {
                    try
                    {
                        ExtractAppIdsFromVdf(File.ReadAllText(sharedConfig), appIds);
                    }
                    catch { }
                }
            }
        }

        var mainConfig = Path.Combine(steamPath, "config", "config.vdf");
        if (File.Exists(mainConfig))
        {
            try
            {
                ExtractAppIdsFromVdf(File.ReadAllText(mainConfig), appIds);
            }
            catch { }
        }

        // 3. appcache/packageinfo.vdf and appcache/appinfo.vdf
        var appcache = Path.Combine(steamPath, "appcache");
        if (Directory.Exists(appcache))
        {
            ParseAppCacheVdf(Path.Combine(appcache, "packageinfo.vdf"), appIds);
            ParseAppCacheVdf(Path.Combine(appcache, "appinfo.vdf"), appIds);
        }

        return appIds.ToList();
    }

    internal static void ParseAppCacheVdf(string filePath, HashSet<uint> appIds)
    {
        if (!File.Exists(filePath)) return;

        // Try reading as text first (supports mock VDF test fixtures)
        try
        {
            var text = File.ReadAllText(filePath);
            if (!string.IsNullOrWhiteSpace(text) &&
                (text.Contains('{') || text.Contains("\"apps\"") || text.Contains("\"appinfo\"") || text.Contains("\"packageinfo\"") || text.Contains("\"appid\"")))
            {
                ExtractAppIdsFromVdf(text, appIds);
            }
        }
        catch { }

        // Also parse binary Steam VDF structure
        try
        {
            var bytes = File.ReadAllBytes(filePath);
            if (bytes.Length >= 8)
            {
                ParseBinaryAppCache(bytes, appIds);
            }
        }
        catch { }
    }

    internal static void ParseBinaryAppCache(byte[] bytes, HashSet<uint> appIds)
    {
        if (bytes == null || bytes.Length < 8) return;

        var magic = BitConverter.ToUInt32(bytes, 0);

        // Canonical Valve appinfo.vdf headers (0x07564427, 0x07564428, 0x07564429)
        if (magic == 0x07564427 || magic == 0x07564428 || magic == 0x07564429)
        {
            int offset = 8;
            while (offset + 8 <= bytes.Length)
            {
                var appId = BitConverter.ToUInt32(bytes, offset);
                if (appId == 0) break;
                var entrySize = BitConverter.ToUInt32(bytes, offset + 4);

                if (appId > 0 && appId < 50_000_000)
                {
                    appIds.Add(appId);
                }

                if (entrySize == 0 || entrySize > int.MaxValue || (long)offset + 8 + entrySize > bytes.Length) break;
                offset += 8 + (int)entrySize;
            }
        }

        // Canonical Valve packageinfo.vdf headers (0x275C4406, 0x285C4406)
        if (magic == 0x275C4406 || magic == 0x285C4406)
        {
            int offset = 8;
            while (offset + 4 <= bytes.Length)
            {
                var pkgId = BitConverter.ToUInt32(bytes, offset);
                if (pkgId == 0xFFFFFFFF) break;
                offset += 4;
                if (offset + 24 <= bytes.Length)
                {
                    // Skip hash (20 bytes) + changeNumber (4 bytes)
                    offset += 24;
                }
                else break;
            }
        }

        // Generic pattern scan for "appids" or "appid" binary KeyValues in appcache
        byte[] appidKey = [0x61, 0x70, 0x70, 0x69, 0x64, 0x00]; // "appid\0"
        for (int i = 0; i <= bytes.Length - 10; i++)
        {
            if (bytes[i] == appidKey[0] &&
                bytes[i + 1] == appidKey[1] &&
                bytes[i + 2] == appidKey[2] &&
                bytes[i + 3] == appidKey[3] &&
                bytes[i + 4] == appidKey[4] &&
                bytes[i + 5] == appidKey[5])
            {
                var candidate = BitConverter.ToUInt32(bytes, i + 6);
                if (candidate > 0 && candidate < 50_000_000)
                {
                    appIds.Add(candidate);
                }
            }
        }
    }

    internal static void ExtractAppIdsFromVdf(string content, HashSet<uint> appIds)
    {
        if (string.IsNullOrWhiteSpace(content)) return;

        // Balanced brace depth scanner for "apps" blocks
        var appsIndex = 0;
        while ((appsIndex = content.IndexOf("\"apps\"", appsIndex, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            var openBrace = content.IndexOf('{', appsIndex);
            if (openBrace < 0) break;

            int depth = 1;
            int pos = openBrace + 1;
            while (pos < content.Length && depth > 0)
            {
                char c = content[pos];
                if (c == '{')
                {
                    depth++;
                }
                else if (c == '}')
                {
                    depth--;
                }
                else if (depth == 1 && c == '"')
                {
                    int endQuote = content.IndexOf('"', pos + 1);
                    if (endQuote > pos)
                    {
                        var key = content.Substring(pos + 1, endQuote - pos - 1);
                        if (uint.TryParse(key, out var appId) && appId > 0)
                        {
                            appIds.Add(appId);
                        }
                        pos = endQuote;
                    }
                }
                pos++;
            }

            appsIndex = openBrace + 1;
        }

        // Also match any direct appid entries
        var directMatches = Regex.Matches(content, @"""(?:appid|app)""\s+""(\d+)""", RegexOptions.IgnoreCase);
        foreach (Match m in directMatches)
        {
            if (uint.TryParse(m.Groups[1].Value, out var aId) && aId > 0)
            {
                appIds.Add(aId);
            }
        }
    }
}
