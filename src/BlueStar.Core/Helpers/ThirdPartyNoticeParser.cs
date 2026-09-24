using System;
using System.Linq;
using System.Text.RegularExpressions;

namespace BlueStar.Core.Helpers;

/// <summary>
/// Parses 3rd-party DRM and external account/launcher notices provided by Steam into clean,
/// concise system names suitable for UI badge tags (e.g. "DENUVO", "Rockstar", "EA App", "Ubisoft").
/// </summary>
public static class ThirdPartyNoticeParser
{
    private static readonly Regex HtmlTagRegex = new(@"<[^>]+>", RegexOptions.Compiled);
    private static readonly Regex ParenthesesRegex = new(@"\s*\([^)]*\)", RegexOptions.Compiled);
    private static readonly Regex DrmPrefixRegex = new(@"Incorporates\s+3rd-party\s+DRM:\s*([^\r\n.]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex TrailingAntiTamperRegex = new(@"(?i)\s+Anti-tamper.*", RegexOptions.Compiled);
    private static readonly Regex TrailingLauncherRegex = new(@"(?i)\s+(launcher|account)$", RegexOptions.Compiled);

    /// <summary>
    /// Strips HTML tags and unescapes HTML entities from Steam notice strings.
    /// </summary>
    public static string CleanInput(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return string.Empty;
        var stripped = HtmlTagRegex.Replace(input, " ");
        var decoded = System.Net.WebUtility.HtmlDecode(stripped);
        return decoded.Trim();
    }

    /// <summary>
    /// Extracts a concise DRM system name (e.g. "DENUVO", "VMProtect", "SecuROM") from a raw Steam DRM notice string.
    /// </summary>
    public static string? ExtractDrmName(string? notice)
    {
        var cleaned = CleanInput(notice);
        if (string.IsNullOrWhiteSpace(cleaned)) return null;

        // 1. Direct system matches
        if (cleaned.Contains("Denuvo", StringComparison.OrdinalIgnoreCase)) return "DENUVO";
        if (cleaned.Contains("VMProtect", StringComparison.OrdinalIgnoreCase)) return "VMProtect";
        if (cleaned.Contains("SecuROM", StringComparison.OrdinalIgnoreCase)) return "SecuROM";
        if (cleaned.Contains("Easy Anti-Cheat", StringComparison.OrdinalIgnoreCase) || cleaned.Contains("EasyAntiCheat", StringComparison.OrdinalIgnoreCase)) return "Easy Anti-Cheat";
        if (cleaned.Contains("BattlEye", StringComparison.OrdinalIgnoreCase)) return "BattlEye";

        // 2. "Incorporates 3rd-party DRM: [Name]" format
        var match = DrmPrefixRegex.Match(cleaned);
        if (match.Success)
        {
            var extracted = match.Groups[1].Value.Trim();
            if (!string.IsNullOrWhiteSpace(extracted))
            {
                extracted = TrailingAntiTamperRegex.Replace(extracted, "").Trim();
                return extracted.Length > 20 ? extracted[..20].Trim() : extracted;
            }
        }

        // 3. Explicit DRM keyword fallback
        if (cleaned.Contains("DRM", StringComparison.OrdinalIgnoreCase) || cleaned.Contains("digital rights management", StringComparison.OrdinalIgnoreCase))
        {
            return "DRM";
        }

        return null;
    }

    /// <summary>
    /// Extracts a concise external launcher or account name (e.g. "Rockstar", "EA App", "Ubisoft") from a raw Steam external account notice string.
    /// </summary>
    public static string? ExtractLauncherName(string? notice)
    {
        var raw = CleanInput(notice);
        if (string.IsNullOrWhiteSpace(raw)) return null;

        // Strip parentheticals like "(Supports Linking to Steam Account)"
        var cleaned = ParenthesesRegex.Replace(raw, "").Trim();

        if (cleaned.Contains("Rockstar", StringComparison.OrdinalIgnoreCase)) return "Rockstar";
        if (cleaned.Contains("Ubisoft", StringComparison.OrdinalIgnoreCase)) return "Ubisoft";
        if (cleaned.Contains("EA Account", StringComparison.OrdinalIgnoreCase) || cleaned.Contains("EA app", StringComparison.OrdinalIgnoreCase) || cleaned.Equals("EA", StringComparison.OrdinalIgnoreCase)) return "EA App";
        if (cleaned.Contains("Battle.net", StringComparison.OrdinalIgnoreCase) || cleaned.Contains("Blizzard", StringComparison.OrdinalIgnoreCase)) return "Battle.net";
        if (cleaned.Contains("PlayStation", StringComparison.OrdinalIgnoreCase) || cleaned.Contains("PSN", StringComparison.OrdinalIgnoreCase)) return "PlayStation";
        if (cleaned.Contains("Xbox", StringComparison.OrdinalIgnoreCase) || cleaned.Contains("Microsoft", StringComparison.OrdinalIgnoreCase)) return "Xbox Live";
        if (cleaned.Contains("Epic Online", StringComparison.OrdinalIgnoreCase)) return "Epic Online Services";
        if (cleaned.Contains("Epic Games", StringComparison.OrdinalIgnoreCase)) return "Epic Games";
        if (cleaned.Contains("2K", StringComparison.OrdinalIgnoreCase)) return "2K Games";
        if (cleaned.Contains("Paradox", StringComparison.OrdinalIgnoreCase)) return "Paradox";
        if (cleaned.Contains("Riot", StringComparison.OrdinalIgnoreCase)) return "Riot Games";

        // Clean trailing "launcher" or "account" if present
        cleaned = TrailingLauncherRegex.Replace(cleaned, "").Trim();

        var firstLine = cleaned.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim();
        if (!string.IsNullOrWhiteSpace(firstLine) && (firstLine.Contains("account", StringComparison.OrdinalIgnoreCase) || firstLine.Contains("launcher", StringComparison.OrdinalIgnoreCase)))
        {
            return firstLine.Length > 25 ? firstLine[..25].Trim() : firstLine;
        }

        return null;
    }

    /// <summary>
    /// Extracts a concise anti-cheat system name (e.g. "Easy Anti-Cheat", "BattlEye", "Denuvo", "VAC") from notice or store page text.
    /// Strictly returns null if no recognized anti-cheat system is mentioned.
    /// </summary>
    public static string? ExtractAntiCheatName(string? notice)
    {
        var cleaned = CleanInput(notice);
        if (string.IsNullOrWhiteSpace(cleaned)) return null;

        if (cleaned.Contains("Easy Anti-Cheat", StringComparison.OrdinalIgnoreCase) || cleaned.Contains("EasyAntiCheat", StringComparison.OrdinalIgnoreCase))
            return "Easy Anti-Cheat";
        if (cleaned.Contains("BattlEye", StringComparison.OrdinalIgnoreCase))
            return "BattlEye";
        if (cleaned.Contains("Denuvo", StringComparison.OrdinalIgnoreCase) && (cleaned.Contains("anti-cheat", StringComparison.OrdinalIgnoreCase) || cleaned.Contains("anticheat", StringComparison.OrdinalIgnoreCase)))
            return "Denuvo Anti-Cheat";
        if (cleaned.Contains("Vanguard", StringComparison.OrdinalIgnoreCase))
            return "Vanguard";
        if (cleaned.Contains("Ricochet", StringComparison.OrdinalIgnoreCase))
            return "Ricochet";
        if (cleaned.Contains("nProtect", StringComparison.OrdinalIgnoreCase) || cleaned.Contains("GameGuard", StringComparison.OrdinalIgnoreCase))
            return "GameGuard";
        if (cleaned.Contains("VAC", StringComparison.OrdinalIgnoreCase) || cleaned.Contains("Valve Anti-Cheat", StringComparison.OrdinalIgnoreCase))
            return "VAC";
        if (cleaned.Contains("HoYoKProtect", StringComparison.OrdinalIgnoreCase))
            return "HoYoKProtect";
        if (cleaned.Contains("PunkBuster", StringComparison.OrdinalIgnoreCase))
            return "PunkBuster";
        if (cleaned.Contains("Xigncode", StringComparison.OrdinalIgnoreCase))
            return "Xigncode3";
        if (cleaned.Contains("Kernel", StringComparison.OrdinalIgnoreCase) && (cleaned.Contains("anti-cheat", StringComparison.OrdinalIgnoreCase) || cleaned.Contains("antitrampas", StringComparison.OrdinalIgnoreCase)))
            return "Kernel Anti-Cheat";

        if (cleaned.Contains("anti-cheat", StringComparison.OrdinalIgnoreCase) || cleaned.Contains("anticheat", StringComparison.OrdinalIgnoreCase) || cleaned.Contains("antitrampas", StringComparison.OrdinalIgnoreCase))
        {
            var match = Regex.Match(cleaned, @"([A-Za-z0-9\s\-]+?\s*(?:Anti-Cheat|Anticheat|Antitrampas))", RegexOptions.IgnoreCase);
            if (match.Success)
            {
                var val = match.Groups[1].Value.Trim();
                return val.Length > 20 ? val[..20].Trim() : val;
            }
            return "Anti-Cheat";
        }

        return null;
    }

    /// <summary>
    /// Extracts a concise 3rd-party account name (e.g. "Epic Online Services", "2K Sports", "Rockstar", "EA Account", "PlayStation Network").
    /// </summary>
    public static string? ExtractAccountName(string? notice)
    {
        var raw = CleanInput(notice);
        if (string.IsNullOrWhiteSpace(raw)) return null;

        var cleaned = ParenthesesRegex.Replace(raw, "").Trim();

        if (cleaned.Contains("Epic Online", StringComparison.OrdinalIgnoreCase)) return "Epic Online Services";
        if (cleaned.Contains("Epic Games", StringComparison.OrdinalIgnoreCase)) return "Epic Games";
        if (cleaned.Contains("2K Sports", StringComparison.OrdinalIgnoreCase)) return "2K Sports";
        if (cleaned.Contains("2K", StringComparison.OrdinalIgnoreCase)) return "2K Games";
        if (cleaned.Contains("Rockstar", StringComparison.OrdinalIgnoreCase)) return "Rockstar";
        if (cleaned.Contains("Ubisoft", StringComparison.OrdinalIgnoreCase)) return "Ubisoft";
        if (cleaned.Contains("EA Account", StringComparison.OrdinalIgnoreCase) || cleaned.Contains("EA app", StringComparison.OrdinalIgnoreCase) || cleaned.Equals("EA", StringComparison.OrdinalIgnoreCase)) return "EA Account";
        if (cleaned.Contains("PlayStation", StringComparison.OrdinalIgnoreCase) || cleaned.Contains("PSN", StringComparison.OrdinalIgnoreCase)) return "PlayStation";
        if (cleaned.Contains("Battle.net", StringComparison.OrdinalIgnoreCase) || cleaned.Contains("Blizzard", StringComparison.OrdinalIgnoreCase)) return "Battle.net";
        if (cleaned.Contains("Xbox", StringComparison.OrdinalIgnoreCase) || cleaned.Contains("Microsoft", StringComparison.OrdinalIgnoreCase)) return "Xbox Live";
        if (cleaned.Contains("Paradox", StringComparison.OrdinalIgnoreCase)) return "Paradox";
        if (cleaned.Contains("Riot", StringComparison.OrdinalIgnoreCase)) return "Riot Games";

        var prefixMatch = Regex.Match(cleaned, @"Requires\s+3rd-Party\s+Account:\s*([^\r\n(]+)", RegexOptions.IgnoreCase);
        if (prefixMatch.Success)
        {
            var val = prefixMatch.Groups[1].Value.Trim();
            if (!string.IsNullOrWhiteSpace(val)) return val.Length > 25 ? val[..25].Trim() : val;
        }

        if (cleaned.Contains("Account", StringComparison.OrdinalIgnoreCase))
        {
            cleaned = TrailingLauncherRegex.Replace(cleaned, "").Trim();
            var firstLine = cleaned.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim();
            if (!string.IsNullOrWhiteSpace(firstLine) && firstLine.Length <= 30) return firstLine;
            return "Account";
        }

        return null;
    }

    /// <summary>
    /// Extracts a concise EULA/ALUF name or returns standard "ALUF" indicator if explicitly present.
    /// Strictly returns null if no EULA or ALUF is mentioned.
    /// </summary>
    public static string? ExtractEulaName(string? notice)
    {
        var cleaned = CleanInput(notice);
        if (string.IsNullOrWhiteSpace(cleaned)) return null;

        if (cleaned.Contains("EULA", StringComparison.OrdinalIgnoreCase) || cleaned.Contains("ALUF", StringComparison.OrdinalIgnoreCase))
        {
            var match = Regex.Match(cleaned, @"([A-Za-z0-9\s:.'’®™\-]+?(?:EULA|ALUF))", RegexOptions.IgnoreCase);
            if (match.Success)
            {
                var val = match.Groups[1].Value.Replace("™", "").Replace("®", "").Trim();
                if (val.StartsWith("Tom Clancy's ", StringComparison.OrdinalIgnoreCase))
                {
                    val = val["Tom Clancy's ".Length..].Trim();
                }
                return val.Length > 30 ? val[..30].Trim() : val;
            }
            return "ALUF";
        }

        return null;
    }

    /// <summary>
    /// Alias for <see cref="ExtractDrmName"/>.
    /// </summary>
    public static string? ParseDrmName(string? notice) => ExtractDrmName(notice);

    /// <summary>
    /// Alias for <see cref="ExtractLauncherName"/>.
    /// </summary>
    public static string? ParseLauncherName(string? notice) => ExtractLauncherName(notice);
}

