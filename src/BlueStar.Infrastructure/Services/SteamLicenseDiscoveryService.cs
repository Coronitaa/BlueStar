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
                    _logger.LogWarning(ex, "Network error communicating with Steam Web API during license discovery");
                    return SteamLicenseDiscoveryResult.Blocked(
                        SteamLicenseBlockageReason.NetworkError,
                        "Network error communicating with Steam servers: " + ex.Message);
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

        var cookieHeader = string.Join("; ", cookies.Select(kv => $"{kv.Key}={kv.Value}"));
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

        var cookieHeader = string.Join("; ", cookies.Select(kv => $"{kv.Key}={kv.Value}"));
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

    private static List<uint> InspectLocalSteamClientLicenses(string steamPath)
    {
        var appIds = new HashSet<uint>();
        var userdata = Path.Combine(steamPath, "userdata");
        if (!Directory.Exists(userdata)) return [];

        var userDirs = Directory.GetDirectories(userdata);
        foreach (var userDir in userDirs)
        {
            var localConfig = Path.Combine(userDir, "config", "localconfig.vdf");
            if (File.Exists(localConfig))
            {
                try
                {
                    var text = File.ReadAllText(localConfig);
                    // Match app definitions under "apps" section
                    var matches = Regex.Matches(text, @"""apps""\s*\{(?<appsContent>[^}]*)\}", RegexOptions.Singleline | RegexOptions.IgnoreCase);
                    foreach (Match m in matches)
                    {
                        var inner = m.Groups["appsContent"].Value;
                        var appMatches = Regex.Matches(inner, @"""(\d+)""", RegexOptions.IgnoreCase);
                        foreach (Match appM in appMatches)
                        {
                            if (uint.TryParse(appM.Groups[1].Value, out var appId) && appId > 0)
                            {
                                appIds.Add(appId);
                            }
                        }
                    }
                }
                catch { }
            }
        }

        return appIds.ToList();
    }
}
