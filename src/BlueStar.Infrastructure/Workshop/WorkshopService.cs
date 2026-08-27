using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
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

    private static readonly Regex WorkshopUrlRegex = new(@"[?&]id=(\d+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex GenericIdRegex = new(@"\b(\d{6,15})\b", RegexOptions.Compiled);
    private static readonly Regex PercentageRegex = new(@"(\d+(?:[.,]\d+)?)\s*%", RegexOptions.Compiled);

    public WorkshopService(HttpClient http, ILogger<WorkshopService> logger)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
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

                var fileUrl = item.TryGetProperty("file_url", out var fu) && fu.ValueKind == JsonValueKind.String ? fu.GetString() : null;

                return new WorkshopItemInfo(
                    PublishedFileId: publishedFileId,
                    AppId: appId,
                    Title: string.IsNullOrWhiteSpace(title) ? $"Workshop Item {publishedFileId}" : title,
                    Description: desc,
                    PreviewUrl: previewUrl,
                    FileSizeBytes: fileSize,
                    Author: author,
                    UpdatedAt: updatedAt,
                    FileUrl: fileUrl
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

            // 1. Direct file_url from Steam API if available
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
                            try
                            {
                                using var zip = ZipFile.OpenRead(tempFile);
                                zip.ExtractToDirectory(stagingFolder, overwriteFiles: true);
                                downloaded = true;
                            }
                            catch
                            {
                                var rawDest = Path.Combine(stagingFolder, $"{safeTitle}.bin");
                                File.Copy(tempFile, rawDest, overwrite: true);
                                downloaded = true;
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
                                try
                                {
                                    using var zip = ZipFile.OpenRead(tempZip);
                                    zip.ExtractToDirectory(stagingFolder, overwriteFiles: true);
                                    downloaded = true;
                                }
                                catch
                                {
                                    var rawDest = Path.Combine(stagingFolder, $"{safeTitle}.bin");
                                    File.Copy(tempZip, rawDest, overwrite: true);
                                    downloaded = true;
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
