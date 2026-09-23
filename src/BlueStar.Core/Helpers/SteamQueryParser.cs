using System;
using System.Text.RegularExpressions;

namespace BlueStar.Core.Helpers;

/// <summary>
/// Deterministic parser for Steam search queries, recognizing AppIDs from numbers,
/// prefixes, web URLs, and client URI protocols.
/// </summary>
public static partial class SteamQueryParser
{
    // Pure numeric AppId: e.g. "570", " 730 "
    [GeneratedRegex(@"^\s*(?<appid>\d{1,10})\s*$", RegexOptions.Compiled)]
    private static partial Regex PureNumericRegex();

    // Prefixed: e.g. "appid:570", "appid=570", "app:570", "app=570"
    [GeneratedRegex(@"^\s*app(?:id)?(?:\s*[:=]\s*|\s+)(?<appid>\d{1,10})\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex PrefixedAppIdRegex();

    // Store / Community URL: e.g. "https://store.steampowered.com/app/570/Dota_2/", "steamcommunity.com/app/570"
    [GeneratedRegex(@"(?:https?://)?(?:store\.steampowered|steamcommunity)\.com/app/(?<appid>\d{1,10})(?:[/?#]|$)", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex WebUrlAppIdRegex();

    // Protocol URI: e.g. "steam://rungameid/570", "steam://app/570", "steam://install/570"
    [GeneratedRegex(@"steam://(?:rungameid|app|install|run)/(?<appid>\d{1,10})(?:[/?#]|$)", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex ProtocolUriAppIdRegex();

    // Depot ID: e.g. "depot:731", "depotid:731", "depot 731", "depot=731"
    [GeneratedRegex(@"^\s*depot(?:id)?(?:\s*[:=]\s*|\s+)(?<depotid>\d{1,10})\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex PrefixedDepotIdRegex();

    // Depot Web URL: e.g. "https://steamdb.info/depot/731/", "steamcommunity.com/depot/731"
    [GeneratedRegex(@"(?:https?://)?(?:steamdb\.info|steamcommunity\.com)/depot/(?<depotid>\d{1,10})(?:[/?#]|$)", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex WebUrlDepotIdRegex();

    // Depot Name prefix: e.g. "depot:Counter-Strike", "depotname:Source 2 Binaries"
    [GeneratedRegex(@"^\s*depot(?:name)?(?:\s*[:=]\s*)(?<depotname>.+)$", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex PrefixedDepotNameRegex();

    /// <summary>
    /// Attempts to parse a deterministic Steam AppID from the input string.
    /// Handles raw numbers, prefixes, store URLs, and steam:// protocols.
    /// </summary>
    /// <param name="query">The user search input string.</param>
    /// <param name="appId">The extracted 32-bit unsigned application identifier, or 0 if not detected.</param>
    /// <returns><c>true</c> if a valid non-zero AppID was recognized; otherwise, <c>false</c>.</returns>
    public static bool TryParseAppId(string? query, out uint appId)
    {
        appId = 0;
        if (string.IsNullOrWhiteSpace(query))
            return false;

        var trimmed = query.Trim();

        // 1. Pure numeric
        var match = PureNumericRegex().Match(trimmed);
        if (match.Success && uint.TryParse(match.Groups["appid"].Value, out var numId) && numId > 0)
        {
            appId = numId;
            return true;
        }

        // 2. Prefixed: appid:123 or app:123
        match = PrefixedAppIdRegex().Match(trimmed);
        if (match.Success && uint.TryParse(match.Groups["appid"].Value, out var prefId) && prefId > 0)
        {
            appId = prefId;
            return true;
        }

        // 3. Web URL: store.steampowered.com/app/123
        match = WebUrlAppIdRegex().Match(trimmed);
        if (match.Success && uint.TryParse(match.Groups["appid"].Value, out var urlId) && urlId > 0)
        {
            appId = urlId;
            return true;
        }

        // 4. Protocol URI: steam://rungameid/123
        match = ProtocolUriAppIdRegex().Match(trimmed);
        if (match.Success && uint.TryParse(match.Groups["appid"].Value, out var protoId) && protoId > 0)
        {
            appId = protoId;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Attempts to parse an explicit Steam Depot ID from the input string.
    /// Handles "depot:731", "depotid:731", "depot 731", and SteamDB depot URLs.
    /// </summary>
    public static bool TryParseDepotId(string? query, out uint depotId)
    {
        depotId = 0;
        if (string.IsNullOrWhiteSpace(query))
            return false;

        var trimmed = query.Trim();

        var match = PrefixedDepotIdRegex().Match(trimmed);
        if (match.Success && uint.TryParse(match.Groups["depotid"].Value, out var id) && id > 0)
        {
            depotId = id;
            return true;
        }

        match = WebUrlDepotIdRegex().Match(trimmed);
        if (match.Success && uint.TryParse(match.Groups["depotid"].Value, out var urlId) && urlId > 0)
        {
            depotId = urlId;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Attempts to parse an explicit depot name search pattern, e.g. "depot:Counter-Strike".
    /// </summary>
    public static bool TryParseDepotName(string? query, out string depotName)
    {
        depotName = string.Empty;
        if (string.IsNullOrWhiteSpace(query))
            return false;

        var match = PrefixedDepotNameRegex().Match(query.Trim());
        if (match.Success)
        {
            depotName = match.Groups["depotname"].Value.Trim();
            return !string.IsNullOrWhiteSpace(depotName);
        }

        return false;
    }

    /// <summary>
    /// Parsed representation of a user search query into deterministic AppID, DepotID, or normalized term.
    /// </summary>
    public sealed record ParsedSteamQuery(uint? AppId, uint? DepotId, string? NormalizedTerm)
    {
        public ParsedSteamQuery(uint? appId, string? normalizedTerm) : this(appId, null, normalizedTerm) { }
    }

    /// <summary>
    /// Parses a raw user query into a deterministic AppID, DepotID, or a normalized search term.
    /// </summary>
    public static ParsedSteamQuery Parse(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return new ParsedSteamQuery(null, null, null);
        }

        // 1. Explicit depot ID prefix or URL (depot:731, steamdb.info/depot/731)
        if (TryParseDepotId(query, out var depotId))
        {
            return new ParsedSteamQuery(null, depotId, null);
        }

        // 2. Explicit depot name prefix (depot:name, depotname:name)
        if (TryParseDepotName(query, out var depotName))
        {
            var normDepot = DeterministicNormalizer.Normalize(depotName);
            return new ParsedSteamQuery(null, null, string.IsNullOrWhiteSpace(normDepot) ? null : normDepot);
        }

        // 3. AppID (raw number, app:123, store URL, etc.)
        if (TryParseAppId(query, out var appId))
        {
            return new ParsedSteamQuery(appId, null, null);
        }

        var normalized = DeterministicNormalizer.Normalize(query);
        return new ParsedSteamQuery(null, null, string.IsNullOrWhiteSpace(normalized) ? null : normalized);
    }
}

