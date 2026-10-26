using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using BlueStar.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace BlueStar.Infrastructure.Workshop;

/// <summary>
/// Service for detecting Steam Workshop support and downloading mods via Steam Web API and WorkshopDL.
/// </summary>
public sealed class WorkshopService : IWorkshopService
{
    private readonly HttpClient _http;
    private readonly ILogger<WorkshopService> _logger;
    private readonly IDepotKeyRepository? _depotKeys;

    private static readonly Regex WorkshopUrlRegex = new(@"[?&]id=(\d+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex GenericIdRegex = new(@"\b(\d{6,15})\b", RegexOptions.Compiled);
    private static readonly Regex PercentageRegex = new(@"(\d+(?:[.,]\d+)?)\s*%", RegexOptions.Compiled);

    public WorkshopService(HttpClient http, ILogger<WorkshopService> logger, IDepotKeyRepository? depotKeys = null)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _depotKeys = depotKeys;
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<uint, bool> WorkshopSupportCache = new();

    private static readonly HashSet<uint> KnownWorkshopAppIds = new()
    {
        214950, // Don't Starve
        322330, // Don't Starve Together
        457140, // Oxygen Not Included
        730,    // Counter-Strike 2
        550,    // Left 4 Dead 2
        4000,   // Garry's Mod
        286160, // Tabletop Simulator
        294100, // RimWorld
        108600, // Project Zomboid
        255710, // Cities: Skylines
        1118200,// People Playground
        1167630,// Teardown
        620,    // Portal 2
        440,    // Team Fortress 2
        252490, // Rust
        105600, // Terraria
        250900, // The Binding of Isaac: Rebirth
        431960, // Wallpaper Engine
        281990, // Stellaris
        394360, // Hearts of Iron IV
        1158310,// Crusader Kings III
        236850, // Europa Universalis IV
        381210, // Dead by Daylight
        211820, // Starbound
        304930, // Unturned
        230410, // Warframe
        221100, // DayZ
        107410, // Arma 3
        244850, // Space Engineers
        220200, // Kerbal Space Program
        48700,  // Mount & Blade: Warband
        261550, // Mount & Blade II: Bannerlord
        289070, // Civilization VI
        8930,   // Civilization V
        346110, // ARK: Survival Evolved
        362890, // Black Mesa
        227300, // Euro Truck Simulator 2
        270880, // American Truck Simulator
        435150, // Divinity: Original Sin 2
        1091500,// Cyberpunk 2077
        392110, // Endless Space 2
        289070, // Sid Meier's Civilization VI
        206440, // To the Moon
        1222670,// The Sims 4
        255710  // Cities: Skylines
    };

    /// <inheritdoc />
    public async Task<bool> HasWorkshopSupportAsync(uint appId, CancellationToken ct = default)
    {
        if (appId == 0) return false;

        if (WorkshopSupportCache.TryGetValue(appId, out var cached))
            return cached;

        if (KnownWorkshopAppIds.Contains(appId))
        {
            WorkshopSupportCache[appId] = true;
            return true;
        }

        try
        {
            var url = $"https://store.steampowered.com/api/appdetails?appids={appId}&filters=categories";
            using var resp = await _http.GetAsync(url, ct).ConfigureAwait(false);
            if (resp.IsSuccessStatusCode)
            {
                using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);

                if (doc.RootElement.TryGetProperty(appId.ToString(), out var appElement) &&
                    appElement.TryGetProperty("data", out var dataElement) &&
                    dataElement.TryGetProperty("categories", out var categories))
                {
                    foreach (var cat in categories.EnumerateArray())
                    {
                        if (cat.TryGetProperty("id", out var catId) && catId.GetInt32() == 30) // Category 30 = Steam Workshop
                        {
                            WorkshopSupportCache[appId] = true;
                            return true;
                        }
                    }

                    // Explicitly answered categories without category 30
                    WorkshopSupportCache[appId] = false;
                    return false;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not check Store API Workshop support for AppId={AppId}", appId);
        }

        // Secondary fallback: Probe Steam Workshop web browse endpoint
        try
        {
            var workshopUrl = $"https://steamcommunity.com/workshop/browse/?appid={appId}";
            using var req = new HttpRequestMessage(HttpMethod.Head, workshopUrl);
            using var webResp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            if (webResp.IsSuccessStatusCode && webResp.RequestMessage?.RequestUri?.ToString().Contains("workshop", StringComparison.OrdinalIgnoreCase) == true)
            {
                WorkshopSupportCache[appId] = true;
                return true;
            }
        }
        catch { }

        // Default to false if unconfirmed, but cache so we do not spam
        WorkshopSupportCache[appId] = false;
        return false;
    }

    /// <inheritdoc />
    public ulong? ParsePublishedFileId(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;

        input = input.Trim();

        // 1. Check if raw number
        if (ulong.TryParse(input, out var rawId) && rawId > 0)
            return rawId;

        // 2. Check URL match (?id=123456 or &id=123456)
        var match = WorkshopUrlRegex.Match(input);
        if (match.Success && ulong.TryParse(match.Groups[1].Value, out var urlId))
            return urlId;

        // 3. Fallback: extract any 6-15 digit sequence
        var genericMatch = GenericIdRegex.Match(input);
        if (genericMatch.Success && ulong.TryParse(genericMatch.Groups[1].Value, out var genId))
            return genId;

        return null;
    }

    /// <inheritdoc />
    public async Task<WorkshopItemInfo?> GetItemDetailsAsync(ulong publishedFileId, CancellationToken ct = default)
    {
        if (publishedFileId == 0) return null;

        try
        {
            var url = "https://api.steampowered.com/ISteamRemoteStorage/GetPublishedFileDetails/v1/";
            var form = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("itemcount", "1"),
                new KeyValuePair<string, string>("publishedfileids[0]", publishedFileId.ToString())
            });

            using var resp = await _http.PostAsync(url, form, ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();

            using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);

            if (doc.RootElement.TryGetProperty("response", out var responseElem) &&
                responseElem.TryGetProperty("publishedfiledetails", out var detailsArr) &&
                detailsArr.GetArrayLength() > 0)
            {
                var item = detailsArr[0];
                int result = 0;
                if (item.TryGetProperty("result", out var r))
                {
                    if (r.ValueKind == JsonValueKind.Number && r.TryGetInt32(out var rNum)) result = rNum;
                    else if (r.ValueKind == JsonValueKind.String && int.TryParse(r.GetString(), out var rStr)) result = rStr;
                }

                if (result != 1) return null; // Result 1 = k_EResultOK

                uint appId = 0;
                if (item.TryGetProperty("consumer_app_id", out var cApp))
                {
                    if (cApp.ValueKind == JsonValueKind.Number && cApp.TryGetUInt32(out var cNum)) appId = cNum;
                    else if (cApp.ValueKind == JsonValueKind.String && uint.TryParse(cApp.GetString(), out var cStr)) appId = cStr;
                }
                if (appId == 0 && item.TryGetProperty("creator_app_id", out var crApp))
                {
                    if (crApp.ValueKind == JsonValueKind.Number && crApp.TryGetUInt32(out var crNum)) appId = crNum;
                    else if (crApp.ValueKind == JsonValueKind.String && uint.TryParse(crApp.GetString(), out var crStr)) appId = crStr;
                }

                var title = item.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() ?? "" : "";
                var desc = item.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() ?? "" : "";
                var previewUrl = item.TryGetProperty("preview_url", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
                var author = item.TryGetProperty("creator", out var cr) && cr.ValueKind == JsonValueKind.String ? cr.GetString() : null;

                ulong fileSize = 0;
                if (item.TryGetProperty("file_size", out var fs))
                {
                    if (fs.ValueKind == JsonValueKind.Number && fs.TryGetInt64(out var fsNum)) fileSize = (ulong)fsNum;
                    else if (fs.ValueKind == JsonValueKind.String && ulong.TryParse(fs.GetString(), out var fsStr)) fileSize = fsStr;
                }

                DateTimeOffset? updatedAt = null;
                if (item.TryGetProperty("time_updated", out var tu))
                {
                    long epoch = 0;
                    if (tu.ValueKind == JsonValueKind.Number && tu.TryGetInt64(out var epNum)) epoch = epNum;
                    else if (tu.ValueKind == JsonValueKind.String && long.TryParse(tu.GetString(), out var epStr)) epoch = epStr;
                    if (epoch > 0) updatedAt = DateTimeOffset.FromUnixTimeSeconds(epoch);
                }

                ulong hcontentFile = 0;
                if (item.TryGetProperty("hcontent_file", out var hc))
                {
                    if (hc.ValueKind == JsonValueKind.Number && hc.TryGetUInt64(out var hcNum)) hcontentFile = hcNum;
                    else if (hc.ValueKind == JsonValueKind.String && ulong.TryParse(hc.GetString(), out var hcStr)) hcontentFile = hcStr;
                }

                var fileUrl = item.TryGetProperty("file_url", out var fu) && fu.ValueKind == JsonValueKind.String ? fu.GetString() : null;
                var fileName = item.TryGetProperty("filename", out var fnProp) && fnProp.ValueKind == JsonValueKind.String ? fnProp.GetString() : null;

                // If file_url is not directly in details, resolve direct Valve UGC CDN URL from hcontent_file
                if (string.IsNullOrWhiteSpace(fileUrl) && hcontentFile > 0)
                {
                    try
                    {
                        var ugcUrl = $"https://api.steampowered.com/ISteamRemoteStorage/GetUGCFileDetails/v1/?ugcid={hcontentFile}&appid={appId}";
                        using var ugcResp = await _http.GetAsync(ugcUrl, ct).ConfigureAwait(false);
                        if (ugcResp.IsSuccessStatusCode)
                        {
                            using var ugcStream = await ugcResp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                            using var ugcDoc = await JsonDocument.ParseAsync(ugcStream, cancellationToken: ct).ConfigureAwait(false);
                            if (ugcDoc.RootElement.TryGetProperty("data", out var dataObj) &&
                                dataObj.TryGetProperty("url", out var directUrlProp) &&
                                directUrlProp.ValueKind == JsonValueKind.String)
                            {
                                fileUrl = directUrlProp.GetString();
                            }
                        }
                    }
                    catch { }
                }

                return new WorkshopItemInfo(
                    PublishedFileId: publishedFileId,
                    AppId: appId,
                    Title: string.IsNullOrWhiteSpace(title) ? $"Workshop Item {publishedFileId}" : title,
                    Description: desc,
                    PreviewUrl: previewUrl,
                    FileSizeBytes: fileSize,
                    Author: author,
                    UpdatedAt: updatedAt,
                    FileUrl: fileUrl,
                    FileName: string.IsNullOrWhiteSpace(fileName) ? null : fileName
                );
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get details for Workshop item {Id}", publishedFileId);
        }

        return null;
    }

    /// <inheritdoc />
    public string ResolveModDirectory(BlueStar.Core.Models.GameInstance instance)
    {
        if (instance == null) return string.Empty;
        var resolution = Mods.GameModPathResolver.ResolveModPaths(instance);
        return resolution.PrimaryDirectory;
    }

    /// <inheritdoc />
    public async Task<bool> DownloadAndInstallItemAsync(
        BlueStar.Core.Models.GameInstance instance,
        ulong publishedFileId,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        if (instance == null || publishedFileId == 0) return false;

        var resolution = Mods.GameModPathResolver.ResolveModPaths(instance);
        var targetFolder = Mods.GameModPathResolver.GetItemTargetFolder(instance, publishedFileId);
        var stagingFolder = Path.Combine(Path.GetTempPath(), $"bluestar_ws_{publishedFileId}_{Guid.NewGuid():N}");

        try
        {
            Directory.CreateDirectory(stagingFolder);
            progress?.Report(5.0);

            var details = await GetItemDetailsAsync(publishedFileId, ct).ConfigureAwait(false);
            if (details == null)
            {
                _logger.LogWarning("Workshop item {Id} not found or private", publishedFileId);
                return false;
            }

            var safeTitle = Core.Helpers.PathHelper.SanitizeFolderName(details.Title);
            if (string.IsNullOrWhiteSpace(safeTitle)) safeTitle = $"WorkshopMod_{publishedFileId}";

            _logger.LogInformation("Downloading Steam Workshop item: {Title} ({Id}) for {Game} ({AppId})", details.Title, publishedFileId, instance.Name, instance.AppId);
            progress?.Report(15.0);

            var effectiveAppId = details.AppId > 0 ? details.AppId : instance.AppId;
            var ddPath = GetDepotDownloaderPath();
            bool downloaded = false;

            // 1. Direct file_url from Steam API if available (Valve Akamai/Fastly CDN)
            if (!string.IsNullOrWhiteSpace(details.FileUrl))
            {
                try
                {
                    _logger.LogInformation("Attempting direct download via Steam FileUrl for {Id}", publishedFileId);
                    using var fileResp = await _http.GetAsync(details.FileUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                    if (fileResp.IsSuccessStatusCode)
                    {
                        var tempFile = Path.Combine(Path.GetTempPath(), $"steamws_{publishedFileId}_{Guid.NewGuid():N}.bin");
                        using (var stream = await fileResp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
                        using (var fs = new FileStream(tempFile, FileMode.Create, FileAccess.Write, FileShare.None))
                        {
                            await stream.CopyToAsync(fs, ct).ConfigureAwait(false);
                        }

                        if (File.Exists(tempFile) && new FileInfo(tempFile).Length > 0)
                        {
                            bool isHtml = false;
                            try
                            {
                                var headBytes = new byte[Math.Min(256, (int)new FileInfo(tempFile).Length)];
                                using (var fs = File.OpenRead(tempFile)) { fs.Read(headBytes, 0, headBytes.Length); }
                                var headStr = Encoding.UTF8.GetString(headBytes).TrimStart();
                                if (headStr.StartsWith("<!DOCTYPE", StringComparison.OrdinalIgnoreCase) ||
                                    headStr.StartsWith("<html", StringComparison.OrdinalIgnoreCase))
                                {
                                    isHtml = true;
                                }
                            }
                            catch { }

                            if (!isHtml)
                            {
                                try
                                {
                                    using var zip = ZipFile.OpenRead(tempFile);
                                    zip.ExtractToDirectory(stagingFolder, overwriteFiles: true);
                                    downloaded = true;
                                }
                                catch
                                {
                                    var rawDest = Path.Combine(stagingFolder, "WorkshopUpload");
                                    File.Copy(tempFile, rawDest, overwrite: true);

                                    var jsonDest = Path.Combine(stagingFolder, $"{publishedFileId}.json");
                                    File.Copy(tempFile, jsonDest, overwrite: true);
                                    downloaded = true;
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Direct FileUrl download failed for {Id}", publishedFileId);
                }
            }

            // 2. DepotDownloader with Workshop pubfile
            if (!downloaded && !string.IsNullOrEmpty(ddPath) && File.Exists(ddPath))
            {
                try
                {
                    var psi = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = ddPath,
                        Arguments = $"-app {effectiveAppId} -pubfile {publishedFileId} -dir \"{stagingFolder}\"",
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        WorkingDirectory = Path.GetDirectoryName(ddPath)
                    };

                    using var proc = new System.Diagnostics.Process { StartInfo = psi };
                    proc.OutputDataReceived += (_, e) =>
                    {
                        if (string.IsNullOrEmpty(e.Data)) return;
                        var match = PercentageRegex.Match(e.Data);
                        if (match.Success && double.TryParse(match.Groups[1].Value.Replace(',', '.'), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var pct))
                        {
                            var scaled = 15.0 + (pct * 0.7);
                            progress?.Report(Math.Min(85.0, scaled));
                        }
                    };

                    proc.Start();
                    proc.BeginOutputReadLine();
                    proc.BeginErrorReadLine();

                    await proc.WaitForExitAsync(ct).ConfigureAwait(false);
                    var filesCount = Directory.Exists(stagingFolder)
                        ? Directory.GetFiles(stagingFolder, "*.*", SearchOption.AllDirectories).Length
                        : 0;

                    if (proc.ExitCode == 0 && filesCount > 0)
                    {
                        downloaded = true;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "DepotDownloader failed for Workshop item {Id}", publishedFileId);
                }
            }

            // 3. Multi-tier web download fallbacks
            if (!downloaded)
            {
                var candidateUrls = new[]
                {
                    $"https://steamcommunity.com/sharedfiles/filedetails/?id={publishedFileId}&raw=file",
                    $"https://backend-02-download.steamworkshop.download/download/{publishedFileId}",
                    $"https://steamworkshop.download/download/{publishedFileId}",
                    $"https://steamworkshopdownload.infamous.workers.dev/?id={publishedFileId}"
                };

                foreach (var directDownloadUrl in candidateUrls)
                {
                    if (downloaded) break;
                    var tempZip = Path.Combine(Path.GetTempPath(), $"workshop_{publishedFileId}_{Guid.NewGuid():N}.zip");

                    try
                    {
                        using var resp = await _http.GetAsync(directDownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                        if (resp.IsSuccessStatusCode)
                        {
                            using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                            using var fs = new FileStream(tempZip, FileMode.Create, FileAccess.Write, FileShare.None);
                            await stream.CopyToAsync(fs, ct).ConfigureAwait(false);

                            if (File.Exists(tempZip) && new FileInfo(tempZip).Length > 100)
                            {
                                bool isHtml = false;
                                try
                                {
                                    var headBytes = new byte[Math.Min(512, (int)new FileInfo(tempZip).Length)];
                                    using (var fsCheck = File.OpenRead(tempZip)) { fsCheck.Read(headBytes, 0, headBytes.Length); }
                                    var headStr = Encoding.UTF8.GetString(headBytes).TrimStart();
                                    if (headStr.StartsWith("<!DOCTYPE", StringComparison.OrdinalIgnoreCase) ||
                                        headStr.StartsWith("<html", StringComparison.OrdinalIgnoreCase) ||
                                        headStr.StartsWith("<head", StringComparison.OrdinalIgnoreCase) ||
                                        headStr.Contains("<title>Steam Community", StringComparison.OrdinalIgnoreCase))
                                    {
                                        isHtml = true;
                                    }
                                }
                                catch { }

                                if (!isHtml)
                                {
                                    try
                                    {
                                        using var zip = ZipFile.OpenRead(tempZip);
                                        zip.ExtractToDirectory(stagingFolder, overwriteFiles: true);
                                        downloaded = true;
                                    }
                                    catch
                                    {
                                        // Not a zip — treat as raw file (could be TTS JSON save, etc.)
                                        var rawDest = Path.Combine(stagingFolder, "WorkshopUpload");
                                        File.Copy(tempZip, rawDest, overwrite: true);

                                        var jsonDest = Path.Combine(stagingFolder, $"{publishedFileId}.json");
                                        File.Copy(tempZip, jsonDest, overwrite: true);
                                        downloaded = true;
                                    }
                                }
                            }
                        }
                    }
                    catch { }
                }
            }

            // Remove .DepotDownloader from staging
            var ddDir = Path.Combine(stagingFolder, ".DepotDownloader");
            if (Directory.Exists(ddDir))
            {
                try { Directory.Delete(ddDir, recursive: true); } catch { }
            }

            // Verify that files were actually downloaded
            var downloadedFiles = Directory.Exists(stagingFolder)
                ? Directory.GetFiles(stagingFolder, "*.*", SearchOption.AllDirectories)
                : [];

            if (downloadedFiles.Length == 0)
            {
                _logger.LogWarning("No files could be downloaded for Workshop item {Title} ({Id})", details.Title, publishedFileId);
                return false;
            }

            // Post-download content validation: reject HTML error pages and corrupt data
            bool hasValidContent = false;
            foreach (var file in downloadedFiles)
            {
                var fileName = Path.GetFileName(file);
                if (fileName.StartsWith(".") || fileName.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
                    continue;

                try
                {
                    var fi = new FileInfo(file);
                    if (fi.Length < 10) continue; // Too small to be meaningful

                    var headBytes = new byte[Math.Min(512, (int)fi.Length)];
                    using (var fs = File.OpenRead(file)) { fs.Read(headBytes, 0, headBytes.Length); }
                    var headStr = Encoding.UTF8.GetString(headBytes).TrimStart();

                    // Reject HTML error pages
                    if (headStr.StartsWith("<!DOCTYPE", StringComparison.OrdinalIgnoreCase) ||
                        headStr.StartsWith("<html", StringComparison.OrdinalIgnoreCase) ||
                        headStr.StartsWith("<head", StringComparison.OrdinalIgnoreCase))
                    {
                        _logger.LogWarning("Staging file {File} appears to be an HTML error page, removing", fileName);
                        try { File.Delete(file); } catch { }
                        continue;
                    }

                    hasValidContent = true;
                }
                catch { }
            }

            if (!hasValidContent)
            {
                _logger.LogWarning("All downloaded files for Workshop item {Title} ({Id}) were invalid (HTML errors or corrupt)", details.Title, publishedFileId);
                return false;
            }

            // Clean up stale files from target directory before deploying (prevents showing old mods)
            if (resolution.GameCategory == "TabletopSimulator")
            {
                // Remove stale <publishedFileId>.json and <publishedFileId>.png from TTS Workshop directory
                var staleJson = Path.Combine(targetFolder, $"{publishedFileId}.json");
                if (File.Exists(staleJson)) try { File.Delete(staleJson); } catch { }

                var stalePng = Path.Combine(targetFolder, $"{publishedFileId}.png");
                if (File.Exists(stalePng)) try { File.Delete(stalePng); } catch { }
            }

            progress?.Report(90.0);

            // Adapt and deploy to the exact location expected by this game
            Mods.GameModPathResolver.AdaptAndDeployWorkshopMod(instance, publishedFileId, stagingFolder, targetFolder, details);

            progress?.Report(100.0);
            _logger.LogInformation("Successfully installed Workshop item {Title} ({Id}) to {Path}", details.Title, publishedFileId, targetFolder);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to download and install Workshop item {Id} for {Game}", publishedFileId, instance.Name);
            return false;
        }
        finally
        {
            try
            {
                if (Directory.Exists(stagingFolder)) Directory.Delete(stagingFolder, recursive: true);
            }
            catch { }
        }
    }

    /// <inheritdoc />
    public async Task<bool> DownloadAndInstallItemAsync(
        uint appId,
        ulong publishedFileId,
        string targetModDirectory,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        if (publishedFileId == 0 || string.IsNullOrWhiteSpace(targetModDirectory))
            return false;

        try
        {
            Directory.CreateDirectory(targetModDirectory);
            progress?.Report(5.0);

            // 1. Fetch item details
            var details = await GetItemDetailsAsync(publishedFileId, ct).ConfigureAwait(false);
            if (details == null)
            {
                _logger.LogWarning("Workshop item {Id} not found or private", publishedFileId);
                return false;
            }

            var safeTitle = Core.Helpers.PathHelper.SanitizeFolderName(details.Title);
            if (string.IsNullOrWhiteSpace(safeTitle)) safeTitle = $"WorkshopMod_{publishedFileId}";
            var modFolder = Path.Combine(targetModDirectory, safeTitle);
            Directory.CreateDirectory(modFolder);

            _logger.LogInformation("Downloading Steam Workshop item: {Title} ({Id}) for App {AppId}", details.Title, publishedFileId, appId);
            progress?.Report(15.0);

            var effectiveAppId = details.AppId > 0 ? details.AppId : appId;
            var ddPath = GetDepotDownloaderPath();

            if (!string.IsNullOrEmpty(ddPath) && File.Exists(ddPath))
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = ddPath,
                    Arguments = $"-app {effectiveAppId} -pubfile {publishedFileId} -dir \"{modFolder}\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = Path.GetDirectoryName(ddPath)
                };
                psi.EnvironmentVariables["DOTNET_ROOT"] = AppContext.BaseDirectory;

                using var proc = new System.Diagnostics.Process { StartInfo = psi };
                proc.OutputDataReceived += (_, e) =>
                {
                    if (string.IsNullOrEmpty(e.Data)) return;
                    var match = PercentageRegex.Match(e.Data);
                    if (match.Success && double.TryParse(match.Groups[1].Value.Replace(',', '.'), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var pct))
                    {
                        var scaled = 15.0 + (pct * 0.8);
                        progress?.Report(Math.Min(95.0, scaled));
                    }
                };

                proc.Start();
                proc.BeginOutputReadLine();
                proc.BeginErrorReadLine();

                await proc.WaitForExitAsync(ct).ConfigureAwait(false);

                if (proc.ExitCode == 0)
                {
                    // Unpack any zip archives or JSON mod files and clean .DepotDownloader
                    ProcessDownloadedWorkshopFiles(modFolder, safeTitle);

                    // Write metadata
                    var metaFile = Path.Combine(modFolder, "workshop_info.json");
                    var json = JsonSerializer.Serialize(details, new JsonSerializerOptions { WriteIndented = true });
                    await File.WriteAllTextAsync(metaFile, json, ct).ConfigureAwait(false);

                    progress?.Report(100.0);
                    _logger.LogInformation("Successfully installed Workshop item {Title} ({Id})", details.Title, publishedFileId);
                    return true;
                }
            }

            // Fallback: Web API or Direct Zip Endpoint
            var directDownloadUrl = $"https://steamworkshop.download/download/{publishedFileId}";
            var tempDest = Path.Combine(Path.GetTempPath(), $"workshop_{publishedFileId}_{Guid.NewGuid():N}.zip");

            try
            {
                using var resp = await _http.GetAsync(directDownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                if (resp.IsSuccessStatusCode)
                {
                    using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                    using var fs = new FileStream(tempDest, FileMode.Create, FileAccess.Write, FileShare.None);
                    await stream.CopyToAsync(fs, ct).ConfigureAwait(false);

                    progress?.Report(85.0);

                    if (File.Exists(tempDest))
                    {
                        try
                        {
                            using var zip = ZipFile.OpenRead(tempDest);
                            zip.ExtractToDirectory(modFolder, overwriteFiles: true);
                        }
                        catch
                        {
                            var rawDest = Path.Combine(modFolder, $"{safeTitle}.bin");
                            File.Copy(tempDest, rawDest, overwrite: true);
                        }
                    }

                    var metaFile = Path.Combine(modFolder, "workshop_info.json");
                    var json = JsonSerializer.Serialize(details, new JsonSerializerOptions { WriteIndented = true });
                    await File.WriteAllTextAsync(metaFile, json, ct).ConfigureAwait(false);

                    progress?.Report(100.0);
                    return true;
                }
            }
            catch { }

            // Write metadata file placeholder
            var placeholderMetaFile = Path.Combine(modFolder, "workshop_info.json");
            var placeholderJson = JsonSerializer.Serialize(details, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(placeholderMetaFile, placeholderJson, ct).ConfigureAwait(false);

            progress?.Report(100.0);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to download and install Workshop item {Id}", publishedFileId);
            return false;
        }
    }

    /// <inheritdoc />
    public async Task<WorkshopDownloadResult> DownloadItemContentAsync(
        uint appId,
        ulong publishedFileId,
        string destinationDirectory,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        if (publishedFileId == 0 || string.IsNullOrWhiteSpace(destinationDirectory))
            return new WorkshopDownloadResult(false, Error: "Invalid Workshop item.");

        var details = await GetItemDetailsAsync(publishedFileId, ct).ConfigureAwait(false);
        if (details == null)
            return new WorkshopDownloadResult(false, Error: "The item does not exist or is private.");

        Directory.CreateDirectory(destinationDirectory);
        progress?.Report(5.0);
        var effectiveAppId = details.AppId > 0 ? details.AppId : appId;

        // 1. Legacy single-file items expose their file on Valve's UGC CDN.
        if (!string.IsNullOrWhiteSpace(details.FileUrl) &&
            await TryDownloadCdnFileAsync(details, destinationDirectory, ct).ConfigureAwait(false))
        {
            progress?.Report(100.0);
            return new WorkshopDownloadResult(true, details);
        }

        // 2. Everything else is a depot-style item that DepotDownloader fetches with -pubfile.
        var (ok, error) = await RunDepotDownloaderAsync(effectiveAppId, publishedFileId, destinationDirectory, progress, ct).ConfigureAwait(false);
        var ddMetadata = Path.Combine(destinationDirectory, ".DepotDownloader");
        if (Directory.Exists(ddMetadata))
        {
            Directory.Delete(ddMetadata, recursive: true);
        }

        if (!ok || !Directory.EnumerateFiles(destinationDirectory, "*", SearchOption.AllDirectories).Any())
        {
            return new WorkshopDownloadResult(false, details, error ?? "Steam returned no files for this item.");
        }

        progress?.Report(100.0);
        return new WorkshopDownloadResult(true, details);
    }

    private async Task<bool> TryDownloadCdnFileAsync(WorkshopItemInfo details, string destinationDirectory, CancellationToken ct)
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"steamws_{details.PublishedFileId}_{Guid.NewGuid():N}.bin");
        try
        {
            using (var response = await _http.GetAsync(details.FileUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
            {
                if (!response.IsSuccessStatusCode) return false;
                await using var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                await using var output = File.Create(tempFile);
                await input.CopyToAsync(output, ct).ConfigureAwait(false);
            }

            var head = new byte[512];
            int read;
            await using (var probe = File.OpenRead(tempFile))
            {
                read = await probe.ReadAsync(head, ct).ConfigureAwait(false);
            }

            if (read == 0) return false;
            var text = Encoding.UTF8.GetString(head, 0, read).TrimStart();
            if (text.StartsWith("<!DOCTYPE", StringComparison.OrdinalIgnoreCase) || text.StartsWith("<html", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning("Workshop CDN returned an HTML page for item {Id}", details.PublishedFileId);
                return false;
            }

            var isZip = read >= 4 && head[0] == 0x50 && head[1] == 0x4B && head[2] is 0x03 or 0x05 or 0x07;
            if (isZip)
            {
                ZipFile.ExtractToDirectory(tempFile, destinationDirectory, overwriteFiles: true);
            }
            else
            {
                var name = Path.GetFileName(details.FileName ?? string.Empty);
                if (string.IsNullOrWhiteSpace(name)) name = $"{details.PublishedFileId}.bin";
                File.Copy(tempFile, Path.Combine(destinationDirectory, name), overwrite: true);
            }

            return true;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException)
        {
            _logger.LogWarning("Workshop CDN download failed for item {Id}: {Message}", details.PublishedFileId, ex.Message);
            return false;
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    private async Task<(bool Ok, string? Error)> RunDepotDownloaderAsync(
        uint appId, ulong publishedFileId, string destinationDirectory, IProgress<double>? progress, CancellationToken ct)
    {
        var ddPath = GetDepotDownloaderPath();
        if (string.IsNullOrEmpty(ddPath)) return (false, "DepotDownloader is missing from the tools folder.");

        // Workshop content is encrypted with the key of the depot that shares the game's AppID. Steam does not hand it
        // to anonymous accounts, so pass it in when BlueStar knows it (DepotBox lua files list it as addappid(appid, 1, key)).
        var arguments = $"-app {appId} -pubfile {publishedFileId} -dir \"{destinationDirectory}\"";
        string? keyFile = null;
        var appKey = _depotKeys != null ? await _depotKeys.GetKeyAsync(appId, ct).ConfigureAwait(false) : null;
        if (!string.IsNullOrWhiteSpace(appKey))
        {
            keyFile = Path.Combine(Path.GetTempPath(), $"bluestar_wskeys_{Guid.NewGuid():N}.txt");
            await File.WriteAllTextAsync(keyFile, $"{appId};{appKey.Trim()}", ct).ConfigureAwait(false);
            arguments += $" -depotkeys \"{keyFile}\"";
        }

        try
        {
            return await RunDepotDownloaderProcessAsync(ddPath, arguments, publishedFileId, appId, progress, ct).ConfigureAwait(false);
        }
        finally
        {
            if (keyFile != null && File.Exists(keyFile)) File.Delete(keyFile);
        }
    }

    private async Task<(bool Ok, string? Error)> RunDepotDownloaderProcessAsync(
        string ddPath, string arguments, ulong publishedFileId, uint appId, IProgress<double>? progress, CancellationToken ct)
    {
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = ddPath,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(ddPath)
        };

        // DepotDownloaderMod.exe is framework-dependent. Point it at a runtime shipped next to BlueStar only if one
        // really is there (host/fxr layout); otherwise let it find the .NET installed on the machine.
        if (Directory.Exists(Path.Combine(AppContext.BaseDirectory, "host", "fxr")))
        {
            psi.EnvironmentVariables["DOTNET_ROOT"] = AppContext.BaseDirectory;
        }

        string? lastError = null;
        var accessDenied = false;
        using var proc = new System.Diagnostics.Process { StartInfo = psi };
        proc.OutputDataReceived += (_, e) =>
        {
            if (string.IsNullOrEmpty(e.Data)) return;
            if (e.Data.Contains("AccessDenied", StringComparison.OrdinalIgnoreCase) ||
                e.Data.Contains("No valid depot key", StringComparison.OrdinalIgnoreCase)) accessDenied = true;
            if (e.Data.Contains("error", StringComparison.OrdinalIgnoreCase) ||
                e.Data.Contains("denied", StringComparison.OrdinalIgnoreCase)) lastError = e.Data.Trim();

            var match = PercentageRegex.Match(e.Data);
            if (match.Success && double.TryParse(match.Groups[1].Value.Replace(',', '.'),
                    System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var pct))
            {
                progress?.Report(Math.Min(95.0, 5.0 + pct * 0.9));
            }
        };
        proc.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data)) lastError = e.Data.Trim();
        };

        proc.Start();
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
        try
        {
            await proc.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw;
        }

        // 0x80008083 / 0x80008096: the .NET host could not find a runtime for the downloader.
        if (proc.ExitCode is unchecked((int)0x80008083) or unchecked((int)0x80008096))
        {
            _logger.LogWarning("DepotDownloader could not start for Workshop item {Id}: no .NET runtime found", publishedFileId);
            return (false, "The Workshop downloader needs the .NET runtime (8 or newer), which is not installed.");
        }

        // DepotDownloader exits with 0 even when Steam refuses the depot key, so the output decides.
        if (accessDenied)
        {
            _logger.LogWarning("Steam denied the Workshop depot key of AppID {AppId} for item {Id}", appId, publishedFileId);
            return (false, $"Steam only serves this game's Workshop to accounts that own it. BlueStar needs the depot key of AppID {appId} to download it.");
        }

        if (proc.ExitCode != 0)
        {
            _logger.LogWarning("DepotDownloader exited with {Code} for Workshop item {Id}: {Error}", proc.ExitCode, publishedFileId, lastError);
            return (false, lastError ?? $"DepotDownloader exited with code {proc.ExitCode}.");
        }

        return (true, null);
    }

    private static string? GetDepotDownloaderPath()
    {
        var candidates = new List<string>
        {
            Path.Combine(AppContext.BaseDirectory, "tools", "DepotDownloaderMod.exe"),
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "tools", "DepotDownloaderMod.exe"),
            Path.Combine(Directory.GetCurrentDirectory(), "tools", "DepotDownloaderMod.exe")
        };

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            candidates.Add(Path.Combine(dir.FullName, "tools", "DepotDownloaderMod.exe"));
            candidates.Add(Path.Combine(dir.FullName, "src", "BlueStar.App", "tools", "DepotDownloaderMod.exe"));
            dir = dir.Parent;
        }

        foreach (var p in candidates)
        {
            if (File.Exists(p)) return p;
        }
        return null;
    }

    private void ProcessDownloadedWorkshopFiles(string modFolder, string safeTitle)
    {
        try
        {
            if (!Directory.Exists(modFolder)) return;

            // 1. Remove .DepotDownloader directory if present
            var depotDownloaderDir = Path.Combine(modFolder, ".DepotDownloader");
            if (Directory.Exists(depotDownloaderDir))
            {
                try { Directory.Delete(depotDownloaderDir, recursive: true); } catch { }
            }

            // 2. Check for zip files or files that are zip archives without .zip extension
            var files = Directory.GetFiles(modFolder, "*", SearchOption.TopDirectoryOnly);
            foreach (var file in files)
            {
                var fileName = Path.GetFileName(file);
                if (fileName.Equals("workshop_info.json", StringComparison.OrdinalIgnoreCase)) continue;

                bool isZip = false;
                if (file.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    isZip = true;
                }
                else
                {
                    // Check magic bytes PK\x03\x04
                    try
                    {
                        using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                        var buffer = new byte[4];
                        if (fs.Read(buffer, 0, 4) == 4 && buffer[0] == 0x50 && buffer[1] == 0x4B && (buffer[2] == 0x03 || buffer[2] == 0x05 || buffer[2] == 0x07))
                        {
                            isZip = true;
                        }
                    }
                    catch { }
                }

                if (isZip)
                {
                    try
                    {
                        ZipFile.ExtractToDirectory(file, modFolder, overwriteFiles: true);
                        _logger.LogInformation("Extracted Workshop archive: {File}", file);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to extract zip archive {File}", file);
                    }
                }
                else
                {
                    // If it's a JSON file (e.g. Tabletop Simulator save / object or Unity mod json) named WorkshopUpload without extension
                    if (fileName.Equals("WorkshopUpload", StringComparison.OrdinalIgnoreCase) || !Path.HasExtension(file))
                    {
                        try
                        {
                            var text = File.ReadAllText(file).TrimStart();
                            if (text.StartsWith('{') || text.StartsWith('['))
                            {
                                var jsonPath = Path.Combine(modFolder, $"{safeTitle}.json");
                                if (!File.Exists(jsonPath))
                                {
                                    File.Copy(file, jsonPath, overwrite: true);
                                    _logger.LogInformation("Created JSON mod file: {Path}", jsonPath);
                                }
                            }
                        }
                        catch { }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error processing downloaded workshop files in {Dir}", modFolder);
        }
    }
}
