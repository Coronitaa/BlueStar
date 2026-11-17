using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using BlueStar.Core.Models;

namespace BlueStar.Infrastructure.Admin;

/// <summary>
/// Parses and validates the admin catalog JSON. The file comes from the network, so it is treated as
/// untrusted: anything that does not pass validation is dropped (and reported), never repaired.
/// The same rules are enforced by the admin panel before it commits (bluestar/admin/schema.js).
/// </summary>
public static partial class AdminCatalogParser
{
    public const int SupportedSchemaVersion = 1;
    public const int MaxBytes = 2 * 1024 * 1024;

    private const int MaxGames = 5000;
    private const int MaxPresetsPerGame = 20;
    private const int MaxManifests = 64;
    private const int MaxMods = 50;
    private const int MaxSettings = 64;

    /// <summary>Owner of the repos whose GitHub-hosted files are trusted without a checksum.</summary>
    private const string TrustedGitHubOwner = "Coronitaa";

    private static readonly HashSet<string> ImageHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "shared.steamstatic.com", "cdn.steamstatic.com", "shared.cloudflare.steamstatic.com", "cdn.cloudflare.steamstatic.com",
        "shared.akamai.steamstatic.com", "cdn.akamai.steamstatic.com", "steamcdn-a.akamaihd.net",
        "coronitaa.github.io", "raw.githubusercontent.com"
    };

    private static readonly HashSet<string> SettingsFileExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".ini", ".cfg", ".txt", ".json"
    };

    [GeneratedRegex("^[a-z0-9][a-z0-9_-]{0,63}$")]
    private static partial Regex PresetIdRegex();

    [GeneratedRegex("^[A-Za-z0-9_.-]{1,64}$")]
    private static partial Regex BranchRegex();

    [GeneratedRegex("^[0-9]{1,20}$")]
    private static partial Regex DigitsRegex();

    [GeneratedRegex("^[0-9a-f]{64}$")]
    private static partial Regex Sha256Regex();

    [GeneratedRegex("^[A-Za-z0-9_.-]{1,64}$")]
    private static partial Regex SettingKeyRegex();

    [GeneratedRegex("^[A-Za-z0-9 _.()+-]{1,64}$")]
    private static partial Regex PathSegmentRegex();

    public sealed record Result(AdminCatalog Catalog, IReadOnlyList<string> Errors);

    public static Result Parse(string json)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(json) || json.Length > MaxBytes)
        {
            errors.Add("catalog is empty or larger than the size limit");
            return new Result(AdminCatalog.Empty, errors);
        }

        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
            return new Result(ParseRoot(doc.RootElement, errors), errors);
        }
        catch (JsonException ex)
        {
            errors.Add("catalog is not valid JSON: " + ex.Message);
            return new Result(AdminCatalog.Empty, errors);
        }
    }

    private static AdminCatalog ParseRoot(JsonElement root, List<string> errors)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            errors.Add("catalog root is not an object");
            return AdminCatalog.Empty;
        }

        // A newer major format may mean something this version cannot honour; ignoring it is the safe failure.
        if (!root.TryGetProperty("schemaVersion", out var ver) || ver.ValueKind != JsonValueKind.Number
            || !ver.TryGetInt32(out var version) || version != SupportedSchemaVersion)
        {
            errors.Add($"unsupported schemaVersion (this BlueStar reads {SupportedSchemaVersion})");
            return AdminCatalog.Empty;
        }

        DateTimeOffset? updatedAt = null;
        if (root.TryGetProperty("updatedAt", out var ua) && ua.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(ua.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
        {
            updatedAt = parsed;
        }

        var games = new List<AdminCatalogGame>();
        var seen = new HashSet<uint>();
        if (root.TryGetProperty("games", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var g in arr.EnumerateArray().Take(MaxGames))
            {
                var game = ParseGame(g, errors);
                if (game == null) continue;
                if (!seen.Add(game.AppId))
                {
                    errors.Add($"game {game.AppId}: duplicate entry dropped");
                    continue;
                }
                games.Add(game);
            }
        }

        return new AdminCatalog { SchemaVersion = version, UpdatedAt = updatedAt, Games = games };
    }

    private static AdminCatalogGame? ParseGame(JsonElement g, List<string> errors)
    {
        if (g.ValueKind != JsonValueKind.Object)
        {
            errors.Add("game entry is not an object");
            return null;
        }

        if (!g.TryGetProperty("appId", out var idEl) || idEl.ValueKind != JsonValueKind.Number
            || !idEl.TryGetUInt32(out var appId) || appId == 0)
        {
            errors.Add("game entry without a valid appId dropped");
            return null;
        }

        var hidden = g.TryGetProperty("hidden", out var h) && h.ValueKind == JsonValueKind.True;
        var name = CleanText(GetString(g, "name"), 200);
        if (string.IsNullOrEmpty(name) && !hidden)
        {
            errors.Add($"game {appId}: a visible game needs a name");
            return null;
        }

        string? header = null;
        var headerRaw = GetString(g, "headerImageUrl");
        if (!string.IsNullOrEmpty(headerRaw))
        {
            if (TryHttpsUri(headerRaw, out var headerUri) && ImageHosts.Contains(headerUri.Host)) header = headerUri.AbsoluteUri;
            else errors.Add($"game {appId}: headerImageUrl must be https on an image host, ignored");
        }

        var presets = new List<AdminInstallPreset>();
        var presetIds = new HashSet<string>(StringComparer.Ordinal);
        if (g.TryGetProperty("presets", out var pa) && pa.ValueKind == JsonValueKind.Array)
        {
            foreach (var p in pa.EnumerateArray().Take(MaxPresetsPerGame))
            {
                var preset = ParsePreset(p, appId, errors);
                if (preset != null && presetIds.Add(preset.Id)) presets.Add(preset);
            }
        }

        return new AdminCatalogGame
        {
            AppId = appId,
            Name = name ?? string.Empty,
            Hidden = hidden,
            HeaderImageUrl = header,
            Description = CleanText(GetString(g, "description"), 2000, allowNewlines: true),
            Presets = presets
        };
    }

    private static AdminInstallPreset? ParsePreset(JsonElement p, uint appId, List<string> errors)
    {
        var id = GetString(p, "id");
        var where = $"game {appId} preset '{id}'";
        if (p.ValueKind != JsonValueKind.Object || id == null || !PresetIdRegex().IsMatch(id))
        {
            errors.Add($"game {appId}: preset without a valid id dropped");
            return null;
        }

        var name = CleanText(GetString(p, "name"), 120);
        if (string.IsNullOrEmpty(name))
        {
            errors.Add($"{where}: missing name");
            return null;
        }

        var branch = GetString(p, "branch") ?? "public";
        if (!BranchRegex().IsMatch(branch))
        {
            errors.Add($"{where}: invalid branch");
            return null;
        }

        var buildId = GetString(p, "buildId");
        if (!string.IsNullOrEmpty(buildId) && !DigitsRegex().IsMatch(buildId))
        {
            errors.Add($"{where}: buildId must be digits");
            return null;
        }

        var manifests = new Dictionary<uint, ulong>();
        if (p.TryGetProperty("manifests", out var m) && m.ValueKind == JsonValueKind.Object)
        {
            foreach (var kv in m.EnumerateObject().Take(MaxManifests))
            {
                // Manifest ids are 64-bit and do not survive a JS number, so both sides use strings.
                if (!uint.TryParse(kv.Name, NumberStyles.None, CultureInfo.InvariantCulture, out var depot) || depot == 0
                    || kv.Value.ValueKind != JsonValueKind.String
                    || !ulong.TryParse(kv.Value.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out var manifest) || manifest == 0)
                {
                    errors.Add($"{where}: invalid depot/manifest pair '{kv.Name}'");
                    return null;
                }
                manifests[depot] = manifest;
            }
        }

        var emulator = ParseEmulator(p, where, errors);
        if (emulator == null) return null;

        var mods = new List<AdminDownload>();
        if (p.TryGetProperty("mods", out var ma) && ma.ValueKind == JsonValueKind.Array)
        {
            foreach (var mod in ma.EnumerateArray().Take(MaxMods))
            {
                var dl = ParseDownload(mod, $"{where} mod", errors);
                if (dl == null) return null;
                mods.Add(dl);
            }
        }

        return new AdminInstallPreset
        {
            Id = id,
            Name = name,
            Branch = branch,
            BuildId = string.IsNullOrEmpty(buildId) ? null : buildId,
            Manifests = manifests,
            Emulator = emulator,
            Mods = mods,
            Note = CleanText(GetString(p, "note"), 2000, allowNewlines: true)
        };
    }

    private static AdminEmulator? ParseEmulator(JsonElement p, string where, List<string> errors)
    {
        if (!p.TryGetProperty("emulator", out var e) || e.ValueKind == JsonValueKind.Null) return new AdminEmulator();
        if (e.ValueKind != JsonValueKind.Object)
        {
            errors.Add($"{where}: emulator is not an object");
            return null;
        }

        AdminEmulatorType? type = GetString(e, "type") switch
        {
            null or "none" => AdminEmulatorType.None,
            "refix" => AdminEmulatorType.ReFix,
            "refix_goldberg" => AdminEmulatorType.ReFixGoldberg,
            "onlinefix" => AdminEmulatorType.OnlineFix,
            "custom" => AdminEmulatorType.Custom,
            _ => null
        };
        if (type == null)
        {
            errors.Add($"{where}: unknown emulator type");
            return null;
        }
        if (type != AdminEmulatorType.Custom) return new AdminEmulator { Type = type.Value };

        var name = CleanText(GetString(e, "name"), 80);
        var download = ParseDownload(e, $"{where} custom emulator", errors, fallbackName: name);
        if (string.IsNullOrEmpty(name) || download == null)
        {
            errors.Add($"{where}: a custom emulator needs a name and a valid download");
            return null;
        }

        string? settingsFile = GetString(e, "settingsFile");
        if (!string.IsNullOrEmpty(settingsFile) && !IsSafeRelativePath(settingsFile, SettingsFileExtensions))
        {
            errors.Add($"{where}: settingsFile must be a relative .ini/.cfg/.txt/.json path inside the game folder");
            return null;
        }

        var settings = new Dictionary<string, string>(StringComparer.Ordinal);
        if (e.TryGetProperty("settings", out var s) && s.ValueKind == JsonValueKind.Object)
        {
            foreach (var kv in s.EnumerateObject().Take(MaxSettings))
            {
                var value = kv.Value.ValueKind == JsonValueKind.String ? kv.Value.GetString() ?? string.Empty : null;
                if (!SettingKeyRegex().IsMatch(kv.Name) || value == null || value.Length > 500
                    || value.IndexOfAny(['\r', '\n', '\0']) >= 0)
                {
                    errors.Add($"{where}: invalid setting '{kv.Name}'");
                    return null;
                }
                settings[kv.Name] = value;
            }
        }
        if (settings.Count > 0 && string.IsNullOrEmpty(settingsFile))
        {
            errors.Add($"{where}: settings need a settingsFile");
            return null;
        }

        return new AdminEmulator
        {
            Type = AdminEmulatorType.Custom,
            Name = name,
            Download = download,
            SettingsFile = string.IsNullOrEmpty(settingsFile) ? null : settingsFile,
            Settings = settings
        };
    }

    private static AdminDownload? ParseDownload(JsonElement el, string where, List<string> errors, string? fallbackName = null)
    {
        if (el.ValueKind != JsonValueKind.Object)
        {
            errors.Add($"{where}: not an object");
            return null;
        }

        var url = GetString(el, "url");
        if (url == null || !TryHttpsUri(url, out var uri))
        {
            errors.Add($"{where}: url must be an absolute https URL");
            return null;
        }

        var sha = GetString(el, "sha256")?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(sha)) sha = null;
        else if (!Sha256Regex().IsMatch(sha))
        {
            errors.Add($"{where}: sha256 must be 64 hex characters");
            return null;
        }

        if (sha == null && !IsTrustedDownloadOrigin(uri))
        {
            errors.Add($"{where}: files outside the trusted GitHub origins need a sha256");
            return null;
        }

        var name = CleanText(GetString(el, "name"), 120) ?? fallbackName;
        if (string.IsNullOrEmpty(name))
        {
            errors.Add($"{where}: missing name");
            return null;
        }

        return new AdminDownload { Name = name, Url = uri, Sha256 = sha };
    }

    /// <summary>https, default port, no credentials, a real host name.</summary>
    public static bool TryHttpsUri(string raw, out Uri uri)
    {
        uri = null!;
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var parsed)) return false;
        if (parsed.Scheme != Uri.UriSchemeHttps || !parsed.IsDefaultPort || !string.IsNullOrEmpty(parsed.UserInfo)) return false;
        if (parsed.HostNameType != UriHostNameType.Dns) return false;
        uri = parsed;
        return true;
    }

    /// <summary>Files the owner publishes on GitHub: their Pages site, or their own repos.</summary>
    public static bool IsTrustedDownloadOrigin(Uri uri)
    {
        var host = uri.Host.ToLowerInvariant();
        if (host == "coronitaa.github.io") return true;
        if (host is "github.com" or "raw.githubusercontent.com")
        {
            var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            return segments.Length >= 2 && string.Equals(segments[0], TrustedGitHubOwner, StringComparison.OrdinalIgnoreCase);
        }
        return false;
    }

    /// <summary>A plain relative path with allowed characters only: no root, drive, "..", or stream syntax.</summary>
    public static bool IsSafeRelativePath(string path, IReadOnlySet<string>? allowedExtensions = null)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 200) return false;
        var segments = path.Replace('\\', '/').Split('/');
        foreach (var seg in segments)
        {
            if (seg.Length == 0 || seg == "." || seg == ".." || seg.EndsWith('.') || seg.EndsWith(' ')
                || !PathSegmentRegex().IsMatch(seg)) return false;
        }
        return allowedExtensions == null || allowedExtensions.Contains(System.IO.Path.GetExtension(path));
    }

    private static string? GetString(JsonElement el, string name) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    /// <summary>Trims, caps the length and drops control characters (newlines kept only for notes).</summary>
    private static string? CleanText(string? s, int max, bool allowNewlines = false)
    {
        if (s == null) return null;
        var chars = s.Where(c => !char.IsControl(c) || (allowNewlines && c == '\n')).ToArray();
        var clean = new string(chars).Trim();
        if (clean.Length > max) clean = clean[..max];
        return clean.Length == 0 ? null : clean;
    }
}
