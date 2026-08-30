using System.IO.Compression;
using BlueStar.Core.Models;

namespace BlueStar.Infrastructure.Services;

/// <summary>
/// Result of importing a depot package ZIP file.
/// </summary>
public record DepotPackageImportResult
{
    public bool Success { get; init; }
    public string? BuildId { get; init; }
    public string? BuildName { get; init; }
    public Dictionary<uint, ulong> ManifestMap { get; init; } = new();
    public Dictionary<uint, string> DepotKeys { get; init; } = new();
    public List<string> ExtractedManifestFiles { get; init; } = new();
    public string? ErrorMessage { get; init; }
}

/// <summary>
/// Imports depot and manifest ZIP archives (from cs.rin.ru, DepotBox, SteamDB archives, etc.)
/// extracting .manifest files, depot decryption keys, and detecting Build IDs automatically.
/// </summary>
public static class DepotPackageZipImporter
{
    /// <summary>
    /// Parses and extracts a depot package ZIP into the target instance manifest directory.
    /// </summary>
    public static async Task<DepotPackageImportResult> ImportZipAsync(
        string zipPath,
        string targetManifestDir,
        CancellationToken ct = default)
    {
        if (!File.Exists(zipPath))
            return new DepotPackageImportResult { Success = false, ErrorMessage = "ZIP file not found on disk." };

        Directory.CreateDirectory(targetManifestDir);

        var manifestMap = new Dictionary<uint, ulong>();
        var depotKeys = new Dictionary<uint, string>();
        var extractedFiles = new List<string>();
        string? detectedBuildId = null;

        try
        {
            using var zipStream = new FileStream(zipPath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true);
            using var archive = new ZipArchive(zipStream, ZipArchiveMode.Read);

            foreach (var entry in archive.Entries)
            {
                ct.ThrowIfCancellationRequested();
                var fileName = Path.GetFileName(entry.FullName);
                if (string.IsNullOrWhiteSpace(fileName)) continue;

                // 1. Check for .manifest files
                if (fileName.EndsWith(".manifest", StringComparison.OrdinalIgnoreCase))
                {
                    var dest = Path.Combine(targetManifestDir, fileName);
                    using (var es = entry.Open())
                    using (var fs = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None, 4096, useAsync: true))
                    {
                        await es.CopyToAsync(fs, ct).ConfigureAwait(false);
                    }
                    extractedFiles.Add(dest);

                    // Try parse <depotId>_<manifestId>.manifest
                    var match = System.Text.RegularExpressions.Regex.Match(fileName, @"^(\d+)_(\d+)\.manifest$");
                    if (match.Success &&
                        uint.TryParse(match.Groups[1].Value, out var dId) &&
                        ulong.TryParse(match.Groups[2].Value, out var mId))
                    {
                        manifestMap[dId] = mId;
                    }
                    else
                    {
                        var singleMatch = System.Text.RegularExpressions.Regex.Match(fileName, @"^(\d+)\.manifest$");
                        if (singleMatch.Success && uint.TryParse(singleMatch.Groups[1].Value, out var singleDepotId))
                        {
                            if (!manifestMap.ContainsKey(singleDepotId))
                            {
                                manifestMap[singleDepotId] = 0;
                            }
                        }
                    }
                }

                // 2. Check for keys files (depotkeys.txt, keys.txt, depot_keys.txt)
                else if (fileName.Equals("depotkeys.txt", StringComparison.OrdinalIgnoreCase) ||
                         fileName.Equals("keys.txt", StringComparison.OrdinalIgnoreCase) ||
                         fileName.Equals("depot_keys.txt", StringComparison.OrdinalIgnoreCase))
                {
                    using var reader = new StreamReader(entry.Open());
                    var text = await reader.ReadToEndAsync(ct).ConfigureAwait(false);
                    var lines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                    foreach (var rawLine in lines)
                    {
                        var line = rawLine.Trim();
                        if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#') || line.StartsWith("//")) continue;

                        var parts = line.Split(new[] { ';', ':', ' ', '\t', '=' }, 2, StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length == 2 && uint.TryParse(parts[0].Trim(), out var kDepotId))
                        {
                            var keyHex = parts[1].Trim().Trim('"', '\'');
                            if (!string.IsNullOrWhiteSpace(keyHex))
                            {
                                depotKeys[kDepotId] = keyHex;
                            }
                        }
                    }
                }

                // 3. Check for ACF (appmanifest_*.acf) or LUA for build info
                else if (fileName.StartsWith("appmanifest_", StringComparison.OrdinalIgnoreCase) && fileName.EndsWith(".acf", StringComparison.OrdinalIgnoreCase))
                {
                    using var reader = new StreamReader(entry.Open());
                    var acfContent = await reader.ReadToEndAsync(ct).ConfigureAwait(false);
                    var buildMatch = System.Text.RegularExpressions.Regex.Match(acfContent, @"""buildid""\s+""(\d+)""", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    if (buildMatch.Success)
                    {
                        detectedBuildId = buildMatch.Groups[1].Value;
                    }
                }
                else if (fileName.EndsWith(".lua", StringComparison.OrdinalIgnoreCase))
                {
                    using var reader = new StreamReader(entry.Open());
                    var luaContent = await reader.ReadToEndAsync(ct).ConfigureAwait(false);
                    var buildMatch = System.Text.RegularExpressions.Regex.Match(luaContent, @"(?:buildid|build_id|build)\s*=\s*[""']?(\d+)[""']?", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    if (buildMatch.Success)
                    {
                        detectedBuildId = buildMatch.Groups[1].Value;
                    }

                    var keyMatches = System.Text.RegularExpressions.Regex.Matches(luaContent, @"\[[""']?(\d+)[""']?\]\s*=\s*[""']([a-fA-F0-9]{32,128})[""']");
                    foreach (System.Text.RegularExpressions.Match km in keyMatches)
                    {
                        if (uint.TryParse(km.Groups[1].Value, out var lDepotId))
                        {
                            depotKeys[lDepotId] = km.Groups[2].Value;
                        }
                    }
                }

                // 4. Check for build.txt / version.txt / build_id.txt
                else if (fileName.Equals("build.txt", StringComparison.OrdinalIgnoreCase) ||
                         fileName.Equals("build_id.txt", StringComparison.OrdinalIgnoreCase) ||
                         fileName.Equals("version.txt", StringComparison.OrdinalIgnoreCase))
                {
                    using var reader = new StreamReader(entry.Open());
                    var buildText = (await reader.ReadToEndAsync(ct).ConfigureAwait(false)).Trim();
                    var numMatch = System.Text.RegularExpressions.Regex.Match(buildText, @"\b\d{5,10}\b");
                    if (numMatch.Success)
                    {
                        detectedBuildId = numMatch.Value;
                    }
                }
            }

            // 5. Fallback: inspect zip filename for Build ID (e.g. "PRAGMATA_build_22357085.zip" or "22357085_depots.zip")
            var zipBaseName = Path.GetFileNameWithoutExtension(zipPath);
            if (string.IsNullOrWhiteSpace(detectedBuildId))
            {
                var nameBuildMatch = System.Text.RegularExpressions.Regex.Match(zipBaseName, @"\b(\d{7,10})\b");
                if (nameBuildMatch.Success)
                {
                    detectedBuildId = nameBuildMatch.Groups[1].Value;
                }
            }

            var detectedBuildName = !string.IsNullOrWhiteSpace(detectedBuildId)
                ? $"Build {detectedBuildId} (ZIP: {zipBaseName})"
                : $"Package ({zipBaseName})";

            return new DepotPackageImportResult
            {
                Success = extractedFiles.Count > 0 || manifestMap.Count > 0,
                BuildId = detectedBuildId ?? "Custom",
                BuildName = detectedBuildName,
                ManifestMap = manifestMap,
                DepotKeys = depotKeys,
                ExtractedManifestFiles = extractedFiles
            };
        }
        catch (Exception ex)
        {
            return new DepotPackageImportResult
            {
                Success = false,
                ErrorMessage = $"Failed to parse depot ZIP package: {ex.Message}"
            };
        }
    }
}
