using System.Text.RegularExpressions;
using BlueStar.Core.Models;
using Microsoft.Extensions.Logging;

namespace BlueStar.Infrastructure.DepotBox;

/// <summary>
/// Parses DepotBox Lua metadata files into <see cref="DepotBoxArchive"/> domain models.
/// </summary>
public sealed partial class DepotBoxLuaParser
{
    private readonly ILogger<DepotBoxLuaParser> _logger;

    public DepotBoxLuaParser(ILogger<DepotBoxLuaParser> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    // Matches: addappid(AppId) or addappid(AppId, Flag) or addappid(AppId, Flag, "Key")
    [GeneratedRegex(@"addappid\s*\(\s*(\d+)(?:\s*,\s*(\d+))?(?:\s*,\s*""([^""]*)"")?", RegexOptions.Compiled)]
    private static partial Regex AddAppIdRegex();

    // Matches: setManifestid(DepotId, ManifestId, SizeBytes)
    // ManifestId is optionally quoted — both variants handled via ""? around \d+
    [GeneratedRegex(@"setManifestid\s*\(\s*(\d+)\s*,\s*""?(\d+)""?\s*,\s*(\d+)\s*\)", RegexOptions.Compiled)]
    private static partial Regex SetManifestIdRegex();

    /// <summary>
    /// Parses a DepotBox Lua string into a <see cref="DepotBoxArchive"/>.
    /// </summary>
    public DepotBoxArchive Parse(string luaContent)
    {
        ArgumentNullException.ThrowIfNull(luaContent);

        var games = new List<DepotBoxGame>();
        var currentDepots = new List<DepotBoxDepot>();
        string? currentName = null;
        uint currentAppId = 0;
        string currentKey = "";
        int currentFlag = 0;
        bool currentIsDlc = false;
        bool isFirstApp = true;
        uint archiveAppId = 0;

        // Per-depot pending context — read from comment lines, consumed on each setManifestid
        string? pendingDepotComment = null;
        string? pendingDepotCategory = null;
        string? pendingDepotPlatform = null;

        var lines = luaContent.Split('\n', StringSplitOptions.None);

        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (string.IsNullOrWhiteSpace(line)) continue;

            // ── Comment lines ─────────────────────────────────────────────────────────
            if (line.StartsWith("--"))
            {
                var comment = line.TrimStart('-').Trim();

                if (comment.StartsWith("Downloaded using", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (IsDepotComment(comment))
                {
                    // This comment describes the NEXT depot in the file
                    pendingDepotComment = comment;
                    pendingDepotCategory = ClassifyDepotCategory(comment);
                    pendingDepotPlatform = DetectPlatform(comment);
                }
                else if (!string.IsNullOrWhiteSpace(comment))
                {
                    // Game/DLC name comment
                    var cleaned = CleanName(comment);
                    if (!string.IsNullOrWhiteSpace(cleaned))
                        currentName = cleaned;
                }

                continue;
            }

            // ── addappid() ────────────────────────────────────────────────────────────
            var addMatch = AddAppIdRegex().Match(line);
            if (addMatch.Success)
            {
                // Flush the previous game block
                if (currentAppId != 0)
                {
                    games.Add(new DepotBoxGame
                    {
                        AppId = currentAppId,
                        Flag = currentFlag,
                        DepotKey = currentKey,
                        Name = CleanName(currentName),
                        Depots = currentDepots.AsReadOnly(),
                        IsDlc = currentIsDlc
                    });
                    currentDepots = [];
                    currentName = null;
                    // Reset pending context on new app block
                    pendingDepotComment = null;
                    pendingDepotCategory = null;
                    pendingDepotPlatform = null;
                }

                currentAppId = uint.Parse(addMatch.Groups[1].Value);
                currentFlag = addMatch.Groups[2].Success ? int.Parse(addMatch.Groups[2].Value) : 0;
                currentKey = addMatch.Groups[3].Success ? addMatch.Groups[3].Value : string.Empty;

                // Extract inline comment from this addappid line
                var inlineComment = ExtractInlineComment(line);
                if (!string.IsNullOrWhiteSpace(inlineComment))
                {
                    if (IsDepotComment(inlineComment))
                    {
                        // The addappid comment describes its own depot
                        pendingDepotComment ??= inlineComment;
                        pendingDepotCategory ??= ClassifyDepotCategory(inlineComment);
                        pendingDepotPlatform ??= DetectPlatform(inlineComment);
                        // Determine DLC flag from the comment text
                        currentIsDlc = IsDlcByComment(inlineComment);
                    }
                    else
                    {
                        var cleaned = CleanName(inlineComment);
                        if (!string.IsNullOrWhiteSpace(cleaned))
                            currentName ??= cleaned;
                        currentIsDlc = IsDlcByComment(inlineComment);
                    }
                }
                else
                {
                    // No inline comment: first app = main game, rest = DLC (fallback heuristic)
                    currentIsDlc = games.Count > 0;
                }

                if (isFirstApp)
                {
                    archiveAppId = currentAppId;
                    isFirstApp = false;
                    currentIsDlc = false; // First app is always the main game
                }

                _logger.LogDebug("Parsed addappid: AppId={AppId}, Flag={Flag}, IsDlc={IsDlc}", currentAppId, currentFlag, currentIsDlc);
                continue;
            }

            // ── setManifestid() ───────────────────────────────────────────────────────
            var manifestMatch = SetManifestIdRegex().Match(line);
            if (manifestMatch.Success)
            {
                var depotId = uint.Parse(manifestMatch.Groups[1].Value);
                var manifestId = ulong.Parse(manifestMatch.Groups[2].Value);
                var sizeBytes = long.Parse(manifestMatch.Groups[3].Value);

                // Determine category using pending context, then fallback
                var category = pendingDepotCategory
                    ?? (IsRedistDepotId(depotId) ? "Redistributable"
                        : (currentIsDlc ? "DLC" : "Base Game"));

                var platform = pendingDepotPlatform ?? "Universal";
                var architecture = DetectArchitecture(pendingDepotComment ?? "");
                var depotName = pendingDepotComment ?? $"Depot {depotId}";

                // Also extract inline comment from setManifestid line if pending comment is missing
                var mInline = ExtractInlineComment(line);
                if (!string.IsNullOrWhiteSpace(mInline) && string.Equals(depotName, $"Depot {depotId}", StringComparison.Ordinal))
                {
                    depotName = mInline;
                    category = ClassifyDepotCategory(mInline);
                    platform = DetectPlatform(mInline);
                    architecture = DetectArchitecture(mInline);
                }

                currentDepots.Add(new DepotBoxDepot
                {
                    DepotId = depotId,
                    ManifestId = manifestId,
                    SizeBytes = sizeBytes,
                    ManifestFileName = $"{depotId}_{manifestId}.manifest",
                    Name = depotName,
                    Category = category,
                    Platform = platform,
                    Architecture = architecture
                });

                _logger.LogDebug("Parsed setManifestid: DepotId={DepotId}, ManifestId={ManifestId}, Size={Size}B, Cat={Cat}, Plat={Plat}",
                    depotId, manifestId, sizeBytes, category, platform);

                // Consume the pending depot context
                pendingDepotComment = null;
                pendingDepotCategory = null;
                pendingDepotPlatform = null;
                continue;
            }

            _logger.LogDebug("Unrecognized Lua line: {Line}", line);
        }

        // Flush last game block
        if (currentAppId != 0)
        {
            games.Add(new DepotBoxGame
            {
                AppId = currentAppId,
                Flag = currentFlag,
                DepotKey = currentKey,
                Name = CleanName(currentName),
                Depots = currentDepots.AsReadOnly(),
                IsDlc = currentIsDlc
            });
        }

        _logger.LogInformation("Parsed DepotBox Lua: AppId={AppId}, {GameCount} entries, {TotalDepots} total depots",
            archiveAppId, games.Count, games.Sum(g => g.Depots.Count));

        return new DepotBoxArchive
        {
            AppId = archiveAppId,
            Games = games.AsReadOnly()
        };
    }

    // ── Helpers ───────────────────────────────────────────────────────────────────────

    private static bool IsRedistDepotId(uint depotId) =>
        (depotId >= 228980 && depotId <= 229007) ||
        (depotId >= 1004 && depotId <= 1010);

    /// <summary>
    /// Returns true when a comment line is describing a depot (not a game name).
    /// </summary>
    private static bool IsDepotComment(string text) =>
        text.Contains("Depot", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("Share", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("Redist", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("DirectX", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("VCRedist", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("VC ", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("maindepot", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("maincontent", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Returns true when a comment specifically mentions DLC, meaning the block should be tagged as DLC.
    /// </summary>
    private static bool IsDlcByComment(string text) =>
        (text.Contains("DLC", StringComparison.OrdinalIgnoreCase) ||
         text.Contains("Dlcname", StringComparison.OrdinalIgnoreCase)) &&
        !text.Contains("maindepot", StringComparison.OrdinalIgnoreCase) &&
        !text.Contains("main", StringComparison.OrdinalIgnoreCase);

    private static string ClassifyDepotCategory(string text)
    {
        if (text.Contains("Redist", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("DirectX", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("VCRedist", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("VC ", StringComparison.OrdinalIgnoreCase))
            return "Redistributable";

        if (text.Contains("DLC", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Dlcname", StringComparison.OrdinalIgnoreCase))
            return "DLC";

        return "Base Game";
    }

    private static string? DetectArchitecture(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        // Whole-word matching, same reason as DetectPlatform: a title should not be able to
        // smuggle an architecture in as a substring.
        if (HasPattern(text, @"\b(?:64[\s-]?bit|x64|win64|amd64|x86[_-]64)\b"))
            return "64-bit";

        if (HasPattern(text, @"\b(?:32[\s-]?bit|x86|win32|i386|i686)\b"))
            return "32-bit";

        return null;
    }

    /// <summary>
    /// Infers a depot's platform from its DepotBox comment.
    /// <para>
    /// Matching is done on whole words. A naive <c>Contains("Mac")</c> tagged
    /// "Main Windows Depot Anomalous Coffee Machine 2" as macOS, because "Ma-chine" contains
    /// "Mac" — and that check ran before the Windows one, so the depot was mislabelled. Any game
    /// whose title contains "mac" as a substring (Machine, Machinarium, Macabre…) hit this.
    /// </para>
    /// <para>
    /// An explicit depot marker ("Windows Depot", "win64") outranks a bare word match, because a
    /// title can legitimately mention another platform.
    /// </para>
    /// </summary>
    private static string DetectPlatform(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "Universal";

        // 1. Explicit depot markers win outright.
        if (HasPattern(text, @"\b(?:windows|win)\s*(?:depot|build|content|binaries)\b") ||
            HasPattern(text, @"\bwin(?:32|64)\b"))
            return "Windows";

        if (HasPattern(text, @"\b(?:linux|ubuntu|steamos)\s*(?:depot|build|content|binaries)\b"))
            return "Linux";

        if (HasPattern(text, @"\b(?:mac|macos|osx|darwin)\s*(?:depot|build|content|binaries)\b"))
            return "macOS";

        // 2. Whole-word platform names.
        if (HasPattern(text, @"\b(?:linux|ubuntu|steamos)\b"))
            return "Linux";

        if (HasPattern(text, @"\b(?:mac|macos|mac\s?os|osx|os\s?x|darwin)\b"))
            return "macOS";

        if (HasPattern(text, @"\bwindows\b"))
            return "Windows";

        // 3. Shared / common content.
        if (HasPattern(text, @"\b(?:shared|common|universal|share)\b"))
            return "Universal";

        // 4. An unqualified main depot is the Windows one in practice.
        if (HasPattern(text, @"\bmain\s*depot\b") || HasPattern(text, @"\bmaindepot\b"))
            return "Windows";

        return "Universal";
    }

    /// <summary>Case-insensitive whole-word regex test.</summary>
    private static bool HasPattern(string text, string pattern) =>
        System.Text.RegularExpressions.Regex.IsMatch(
            text, pattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private static string? ExtractInlineComment(string line)
    {
        var idx = line.IndexOf("--", StringComparison.Ordinal);
        return idx >= 0 ? line[(idx + 2)..].Trim() : null;
    }

    private static string? CleanName(string? rawName)
    {
        if (string.IsNullOrWhiteSpace(rawName)) return rawName;

        var name = rawName.Trim();
        var prefixes = new[]
        {
            "Gamename ", "Gamename", "Dlcname ", "Dlcname",
            "Game Name:", "Game Name ", "Game:", "Name:", "App:",
            "Mainappid ", "Mainappid", "mainappid ", "mainappid",
            "Maindepot ", "Maindepot", "maindepot ", "maindepot",
            "Main Windows Depot", "Main Linux Depot", "Main macOS Depot",
            "Main Depot", "Windows Depot", "Linux Depot", "macOS Depot"
        };
        foreach (var p in prefixes)
        {
            if (name.StartsWith(p, StringComparison.OrdinalIgnoreCase))
            {
                name = name[p.Length..].Trim();
            }
        }
        return string.IsNullOrWhiteSpace(name) ? null : name;
    }
}
