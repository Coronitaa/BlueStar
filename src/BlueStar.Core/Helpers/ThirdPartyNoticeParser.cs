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
    private static readonly Regex ParenthesesRegex = new(@"\s*\([^)]*\)", RegexOptions.Compiled);
    private static readonly Regex DrmPrefixRegex = new(@"Incorporates\s+3rd-party\s+DRM:\s*([^\r\n.]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex TrailingAntiTamperRegex = new(@"(?i)\s+Anti-tamper.*", RegexOptions.Compiled);
    private static readonly Regex TrailingLauncherRegex = new(@"(?i)\s+(launcher|account)$", RegexOptions.Compiled);

    /// <summary>
    /// Extracts a concise DRM system name (e.g. "DENUVO", "VMProtect", "SecuROM") from a raw Steam DRM notice string.
    /// </summary>
    public static string? ExtractDrmName(string? notice)
    {
        if (string.IsNullOrWhiteSpace(notice)) return null;

        var trimmed = notice.Trim();

        // 1. Direct system matches
        if (trimmed.Contains("Denuvo", StringComparison.OrdinalIgnoreCase)) return "DENUVO";
        if (trimmed.Contains("VMProtect", StringComparison.OrdinalIgnoreCase)) return "VMProtect";
        if (trimmed.Contains("SecuROM", StringComparison.OrdinalIgnoreCase)) return "SecuROM";
        if (trimmed.Contains("Easy Anti-Cheat", StringComparison.OrdinalIgnoreCase) || trimmed.Contains("EasyAntiCheat", StringComparison.OrdinalIgnoreCase)) return "Easy Anti-Cheat";
        if (trimmed.Contains("BattlEye", StringComparison.OrdinalIgnoreCase)) return "BattlEye";

        // 2. "Incorporates 3rd-party DRM: [Name]" format
        var match = DrmPrefixRegex.Match(trimmed);
        if (match.Success)
        {
            var extracted = match.Groups[1].Value.Trim();
            if (!string.IsNullOrWhiteSpace(extracted))
            {
                extracted = TrailingAntiTamperRegex.Replace(extracted, "").Trim();
                return extracted.Length > 20 ? extracted[..20].Trim() : extracted;
            }
        }

        // 3. Fallback: take first clean line
        var firstLine = trimmed.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim();
        if (!string.IsNullOrWhiteSpace(firstLine))
        {
            firstLine = TrailingAntiTamperRegex.Replace(firstLine, "").Trim();
            return firstLine.Length > 20 ? firstLine[..20].Trim() : firstLine;
        }

        return "DRM";
    }

    /// <summary>
    /// Extracts a concise external launcher or account name (e.g. "Rockstar", "EA App", "Ubisoft") from a raw Steam external account notice string.
    /// </summary>
    public static string? ExtractLauncherName(string? notice)
    {
        if (string.IsNullOrWhiteSpace(notice)) return null;

        // Strip parentheticals like "(Supports Linking to Steam Account)"
        var cleaned = ParenthesesRegex.Replace(notice.Trim(), "").Trim();

        if (cleaned.Contains("Rockstar", StringComparison.OrdinalIgnoreCase)) return "Rockstar";
        if (cleaned.Contains("Ubisoft", StringComparison.OrdinalIgnoreCase)) return "Ubisoft";
        if (cleaned.Contains("EA Account", StringComparison.OrdinalIgnoreCase) || cleaned.Contains("EA app", StringComparison.OrdinalIgnoreCase) || cleaned.Equals("EA", StringComparison.OrdinalIgnoreCase)) return "EA App";
        if (cleaned.Contains("Battle.net", StringComparison.OrdinalIgnoreCase) || cleaned.Contains("Blizzard", StringComparison.OrdinalIgnoreCase)) return "Battle.net";
        if (cleaned.Contains("PlayStation", StringComparison.OrdinalIgnoreCase) || cleaned.Contains("PSN", StringComparison.OrdinalIgnoreCase)) return "PlayStation";
        if (cleaned.Contains("Xbox", StringComparison.OrdinalIgnoreCase) || cleaned.Contains("Microsoft", StringComparison.OrdinalIgnoreCase)) return "Xbox Live";
        if (cleaned.Contains("Epic Games", StringComparison.OrdinalIgnoreCase) || cleaned.Contains("Epic Online", StringComparison.OrdinalIgnoreCase)) return "Epic Games";
        if (cleaned.Contains("2K", StringComparison.OrdinalIgnoreCase)) return "2K Games";
        if (cleaned.Contains("Paradox", StringComparison.OrdinalIgnoreCase)) return "Paradox";

        // Clean trailing "launcher" or "account" if present
        cleaned = TrailingLauncherRegex.Replace(cleaned, "").Trim();

        var firstLine = cleaned.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim();
        if (!string.IsNullOrWhiteSpace(firstLine))
        {
            return firstLine.Length > 25 ? firstLine[..25].Trim() : firstLine;
        }

        return "Launcher";
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

