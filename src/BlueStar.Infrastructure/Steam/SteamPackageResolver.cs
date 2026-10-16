using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BlueStar.Infrastructure.Steam;

/// <summary>
/// Resolves Steam packages (SubIDs) to their constituent Application IDs (AppIDs)
/// by inspecting local Steam client cache (appcache/packageinfo.vdf) and calling the Steam Store package API.
/// </summary>
public sealed class SteamPackageResolver
{
    private readonly HttpClient _http;
    private readonly ILogger<SteamPackageResolver> _logger;
    private readonly ConcurrentDictionary<uint, IReadOnlyList<uint>> _packageCache = new();

    public SteamPackageResolver(HttpClient http, ILogger<SteamPackageResolver>? logger = null)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _logger = logger ?? NullLogger<SteamPackageResolver>.Instance;
    }

    /// <summary>
    /// Resolves a single package (SubID) into its constituent AppIDs.
    /// </summary>
    public async Task<IReadOnlyList<uint>> ResolvePackageAppIdsAsync(uint packageId, string? steamPath = null, CancellationToken ct = default)
    {
        if (packageId == 0) return [];
        if (_packageCache.TryGetValue(packageId, out var cached))
            return cached;

        // 1. Try resolving via local packageinfo.vdf if path is available
        if (!string.IsNullOrWhiteSpace(steamPath))
        {
            var packageInfoVdf = Path.Combine(steamPath, "appcache", "packageinfo.vdf");
            if (File.Exists(packageInfoVdf))
            {
                try
                {
                    var localApps = ParsePackageInfoVdf(packageInfoVdf, packageId);
                    if (localApps.Count > 0)
                    {
                        _packageCache[packageId] = localApps;
                        return localApps;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Failed to parse local packageinfo.vdf for Package {PackageId}", packageId);
                }
            }
        }

        // 2. Fall back to Steam Store packagedetails Web API
        try
        {
            var webApps = await ResolvePackageViaWebApiAsync(packageId, ct).ConfigureAwait(false);
            if (webApps.Count > 0)
            {
                _packageCache[packageId] = webApps;
                return webApps;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to resolve Package {PackageId} via Steam Store API", packageId);
        }

        return [];
    }

    /// <summary>
    /// Batch-resolves multiple packages into AppIDs.
    /// </summary>
    public async Task<IReadOnlyDictionary<uint, IReadOnlyList<uint>>> BatchResolvePackagesAsync(
        IEnumerable<uint> packageIds,
        string? steamPath = null,
        CancellationToken ct = default)
    {
        var result = new Dictionary<uint, IReadOnlyList<uint>>();
        foreach (var pkgId in packageIds)
        {
            if (ct.IsCancellationRequested) break;
            var appIds = await ResolvePackageAppIdsAsync(pkgId, steamPath, ct).ConfigureAwait(false);
            result[pkgId] = appIds;
        }
        return result;
    }

    /// <summary>
    /// Calls https://store.steampowered.com/api/packagedetails/?packageids={packageId} to resolve package contents.
    /// </summary>
    private async Task<IReadOnlyList<uint>> ResolvePackageViaWebApiAsync(uint packageId, CancellationToken ct)
    {
        var url = $"https://store.steampowered.com/api/packagedetails/?packageids={packageId}";
        using var resp = await _http.GetAsync(url, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) return [];

        var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);

        if (!doc.RootElement.TryGetProperty(packageId.ToString(), out var pkgObj))
            return [];

        if (!pkgObj.TryGetProperty("success", out var successProp) || !successProp.GetBoolean())
            return [];

        if (!pkgObj.TryGetProperty("data", out var dataObj))
            return [];

        var appIds = new List<uint>();
        if (dataObj.TryGetProperty("apps", out var appsArray) && appsArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in appsArray.EnumerateArray())
            {
                if (item.TryGetProperty("id", out var idProp) && idProp.TryGetUInt32(out var id) && id > 0)
                {
                    appIds.Add(id);
                }
            }
        }

        return appIds.AsReadOnly();
    }

    /// <summary>
    /// Parses packageinfo.vdf scanning for the requested package ID and its associated appids section.
    /// Supports binary VDF patterns used by Steam client.
    /// </summary>
    public static IReadOnlyList<uint> ParsePackageInfoVdf(string vdfPath, uint targetPackageId)
    {
        var appIds = new List<uint>();
        if (!File.Exists(vdfPath)) return appIds;

        using var fs = new FileStream(vdfPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var br = new BinaryReader(fs);

        if (fs.Length < 8) return appIds;

        var magic = br.ReadUInt32();
        var universe = br.ReadUInt32();

        // Standard Steam packageinfo header check
        while (fs.Position + 28 < fs.Length)
        {
            var packageId = br.ReadUInt32();
            if (packageId == 0xFFFFFFFF || packageId == 0) break;

            // 20 bytes SHA-1 hash + 4 bytes change number
            fs.Seek(24, SeekOrigin.Current);

            // Optional 8-byte token for newer formats
            if ((magic & 0xFF) >= 0x28)
            {
                fs.Seek(8, SeekOrigin.Current);
            }

            // Read binary KV until end marker (0x08)
            if (packageId == targetPackageId)
            {
                // Found our package, extract appids
                return ExtractAppIdsFromBinaryKv(br);
            }
            else
            {
                // Skip this package's KV payload
                SkipBinaryKv(br);
            }
        }

        return appIds;
    }

    private static IReadOnlyList<uint> ExtractAppIdsFromBinaryKv(BinaryReader br)
    {
        var apps = new List<uint>();
        var stream = br.BaseStream;

        // Scan tokens in binary KV: 0x00 = subkey, 0x01 = string, 0x02 = int32, 0x08 = end
        while (stream.Position < stream.Length)
        {
            var type = br.ReadByte();
            if (type == 0x08) break; // End of package KV

            var name = ReadNullTerminatedString(br);
            if (string.Equals(name, "appids", StringComparison.OrdinalIgnoreCase))
            {
                // Subkey "appids" contains list of App IDs
                while (stream.Position < stream.Length)
                {
                    var innerType = br.ReadByte();
                    if (innerType == 0x08) break; // End of appids subkey

                    var _ = ReadNullTerminatedString(br); // index "0", "1", etc.
                    if (innerType == 0x02) // Int32
                    {
                        var appId = br.ReadUInt32();
                        if (appId > 0) apps.Add(appId);
                    }
                    else if (innerType == 0x01) // String
                    {
                        var valStr = ReadNullTerminatedString(br);
                        if (uint.TryParse(valStr, out var appId) && appId > 0)
                            apps.Add(appId);
                    }
                }
                return apps;
            }
            else if (type == 0x00) // Subkey
            {
                SkipBinaryKv(br);
            }
            else if (type == 0x01) // String
            {
                ReadNullTerminatedString(br);
            }
            else if (type == 0x02) // Int32
            {
                br.ReadUInt32();
            }
            else if (type == 0x07) // UInt64
            {
                br.ReadUInt64();
            }
        }

        return apps;
    }

    private static void SkipBinaryKv(BinaryReader br)
    {
        var depth = 1;
        var stream = br.BaseStream;
        while (depth > 0 && stream.Position < stream.Length)
        {
            var type = br.ReadByte();
            if (type == 0x08)
            {
                depth--;
                continue;
            }

            ReadNullTerminatedString(br);

            switch (type)
            {
                case 0x00: // Subkey starts
                    depth++;
                    break;
                case 0x01: // String
                    ReadNullTerminatedString(br);
                    break;
                case 0x02: // Int32
                case 0x03: // Float
                case 0x06: // Color
                    br.ReadUInt32();
                    break;
                case 0x07: // UInt64
                    br.ReadUInt64();
                    break;
            }
        }
    }

    private static string ReadNullTerminatedString(BinaryReader br)
    {
        var bytes = new List<byte>();
        while (br.BaseStream.Position < br.BaseStream.Length && bytes.Count < 65536)
        {
            var b = br.ReadByte();
            if (b == 0) break;
            bytes.Add(b);
        }
        return System.Text.Encoding.UTF8.GetString(bytes.ToArray());
    }
}
