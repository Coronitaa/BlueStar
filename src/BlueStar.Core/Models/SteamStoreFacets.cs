using System.Collections.Generic;

namespace BlueStar.Core.Models;

/// <summary>
/// Kind of Steam store search parameter a facet option maps to.
/// </summary>
public enum SteamFacetKind
{
    /// <summary>Store tag, emitted as <c>tags=</c> or <c>untags=</c>.</summary>
    Tag,

    /// <summary>Player support, emitted as <c>category3=</c>.</summary>
    PlayerSupport,

    /// <summary>Steam feature, emitted as <c>category2=</c>.</summary>
    Feature,

    /// <summary>Controller support, emitted as <c>controllersupport=</c>.</summary>
    Controller,

    /// <summary>Accessibility option, emitted as <c>accessibility=</c>.</summary>
    Accessibility,

    /// <summary>Operating system, emitted as <c>os=</c>.</summary>
    Os,

    /// <summary>Steam Deck rating, emitted as <c>deck_compatibility=</c>.</summary>
    Deck,

    /// <summary>VR support, emitted as <c>vrsupport=</c> / <c>unvrsupport=</c>.</summary>
    Vr,

    /// <summary>Interface/audio/subtitle language, emitted as <c>supportedlang=</c>.</summary>
    Language,

    /// <summary>Steam curated list, emitted as <c>filter=</c>. Mutually exclusive.</summary>
    StoreList,

    /// <summary>Standalone toggle such as <c>specials=1</c> or <c>hidef2p=1</c>.</summary>
    Toggle,

    /// <summary>
    /// Not a Steam facet. Resolved client-side from <c>appdetails</c> once a result has been
    /// enriched (DRM, external launcher, DLC count, adult content).
    /// </summary>
    LocalPostFilter,

    /// <summary>
    /// Not a Steam facet. Resolved client-side by comparing the detected hardware against the
    /// parsed <c>pc_requirements</c> of each result.
    /// </summary>
    LocalRequirements
}

/// <summary>
/// Tri-state a facet option can be in.
/// </summary>
public enum FacetState
{
    /// <summary>Not part of the query.</summary>
    Neutral = 0,

    /// <summary>Results must match it.</summary>
    Include = 1,

    /// <summary>Results must not match it.</summary>
    Exclude = 2
}

/// <summary>
/// A single selectable option inside a filter group.
/// </summary>
/// <param name="Kind">Which Steam parameter it maps to.</param>
/// <param name="Value">The parameter value (numeric id, or a token such as <c>win</c>).</param>
/// <param name="FallbackName">English label, used until the localized catalog resolves.</param>
/// <param name="SupportsExclude">Whether Steam accepts a negated form of this parameter.</param>
public sealed record SteamFacetOption(
    SteamFacetKind Kind,
    string Value,
    string FallbackName,
    bool SupportsExclude = false);

/// <summary>
/// Static catalog of every Steam store search facet BlueStar exposes.
/// Values were captured from the live store search page and are stable identifiers.
/// </summary>
public static class SteamStoreFacets
{
    /// <summary>Games. <c>category1=998</c>.</summary>
    public const string AppTypeGames = "998";

    /// <summary>Software. <c>category1=994</c>.</summary>
    public const string AppTypeSoftware = "994";

    /// <summary>
    /// The only product types BlueStar ever searches. DLC, soundtracks, videos, hardware,
    /// demos, playtests and mods are deliberately excluded.
    /// </summary>
    public const string AppTypeAll = AppTypeGames + "," + AppTypeSoftware;

    /// <summary>
    /// The default sort token. Empty leaves Steam on its natural ranking (popularity/trending for curated
    /// lists, relevance/popularity for catalog search).
    /// </summary>
    public const string DefaultSort = "";

    /// <summary>Steam sort tokens, in the order the store shows them.</summary>
    /// <remarks>
    /// Relevance is deliberately absent: <c>sort_by=_ASC</c> is byte-for-byte the answer Steam
    /// gives with no sort at all, so it would be a second "no particular order" entry.
    /// </remarks>
    public static readonly IReadOnlyList<(string Value, string FallbackName)> SortOptions =
    [
        ("Released", "Release date (newest first)"),
        ("Reviews", "User reviews (best first)"),
        ("Name", "Name (A-Z)"),
        ("Price", "Price"),
        ("DeckCompatDate", "Steam Deck review date (newest first)")
    ];

    /// <summary>
    /// The direction Steam actually implements for a field that only has one.
    /// </summary>
    public static bool PrefersDescending(string? sortBase)
    {
        if (string.IsNullOrEmpty(sortBase)) return false;
        return string.Equals(sortBase, "Released", System.StringComparison.OrdinalIgnoreCase)
            || string.Equals(sortBase, "Reviews", System.StringComparison.OrdinalIgnoreCase)
            || string.Equals(sortBase, "DeckCompatDate", System.StringComparison.OrdinalIgnoreCase)
            || string.Equals(sortBase, "Deck", System.StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Whether Steam honours both directions of this sort. Only price does.
    /// </summary>
    /// <remarks>
    /// Checked against the live endpoint: <c>Released_ASC</c>, <c>Name_DESC</c>,
    /// <c>Reviews_ASC</c> and <c>DeckCompatDate_ASC</c> are not sort tokens Steam knows, and an
    /// unknown token is not an error there — it answers with the unsorted catalogue instead. So
    /// "invert" on those fields quietly replaced the results with the default listing, which is
    /// why reversing "user reviews" handed back well-reviewed games. Only <c>Price</c> has a
    /// real opposite, and the button is offered for it alone.
    /// </remarks>
    public static bool SupportsBothDirections(string? sortBase) =>
        string.Equals(sortBase, "Price", System.StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Turns a sort base and a direction into the token the store expects.
    /// </summary>
    /// <remarks>
    /// A field with only one direction is pinned to it, whatever the flag says: sending the
    /// other one would throw the query back to the unsorted catalogue.
    /// </remarks>
    public static string ComposeSort(string? sortBase, bool descending)
    {
        if (string.IsNullOrEmpty(sortBase)) return string.Empty;
        if (sortBase.StartsWith('_')) return sortBase;

        var canonical = sortBase.ToLowerInvariant() switch
        {
            "released" => "Released",
            "reviews" => "Reviews",
            "name" => "Name",
            "price" => "Price",
            "deckcompatdate" or "deck" => "DeckCompatDate",
            _ => sortBase
        };

        if (!SupportsBothDirections(canonical)) descending = PrefersDescending(canonical);

        return canonical + (descending ? "_DESC" : "_ASC");
    }

    /// <summary>
    /// The order to apply when nobody has chosen one, given the curated list in play.
    /// Empty leaves Steam on its natural ranking (e.g. popularnew, globaltopsellers, or general catalog relevance).
    /// </summary>
    public static string DefaultSortFor(string? storeList) => string.Empty;

    /// <summary>
    /// Whether a curated list can be reordered at all.
    /// Always returns true: curated pools (including Coming Soon) can be sorted locally by SearchPipeline.
    /// </summary>
    public static bool AllowsSorting(string? storeList) => true;

    /// <summary>
    /// The curated lists Steam offers, as the toolbar presents them.
    /// </summary>
    /// <remarks>
    /// <c>topsellers</c> is gone. It is the regional chart and <c>globaltopsellers</c> the world
    /// one, but every request here carries the same country code, so the two answered with
    /// near-identical lists — two entries, one meaning. The global one stays, under the plain
    /// name.
    /// <para>
    /// <c>popularnew</c> keeps its "new releases" label because <see cref="DefaultSortFor"/>
    /// now orders it by release date, which makes the label true.
    /// </para>
    /// </remarks>
    public static readonly IReadOnlyList<SteamFacetOption> StoreLists =
    [
        new(SteamFacetKind.StoreList, "popularnew", "Popular new releases"),
        new(SteamFacetKind.StoreList, "globaltopsellers", "Top sellers"),
        new(SteamFacetKind.StoreList, "comingsoon", "Coming soon")
    ];

    /// <summary><c>category3</c> — how many people can play and how.</summary>
    public static readonly IReadOnlyList<SteamFacetOption> PlayerSupport =
    [
        new(SteamFacetKind.PlayerSupport, "2", "Single-player"),
        new(SteamFacetKind.PlayerSupport, "1", "Multi-player"),
        new(SteamFacetKind.PlayerSupport, "9", "Co-op"),
        new(SteamFacetKind.PlayerSupport, "38", "Online co-op"),
        new(SteamFacetKind.PlayerSupport, "48", "LAN co-op"),
        new(SteamFacetKind.PlayerSupport, "39", "Shared/split screen co-op"),
        new(SteamFacetKind.PlayerSupport, "49", "PvP"),
        new(SteamFacetKind.PlayerSupport, "36", "Online PvP"),
        new(SteamFacetKind.PlayerSupport, "47", "LAN PvP"),
        new(SteamFacetKind.PlayerSupport, "37", "Shared/split screen PvP"),
        new(SteamFacetKind.PlayerSupport, "24", "Shared/split screen"),
        new(SteamFacetKind.PlayerSupport, "27", "Cross-platform multiplayer")
    ];

    /// <summary><c>category2</c> — Steam platform features.</summary>
    public static readonly IReadOnlyList<SteamFacetOption> Features =
    [
        new(SteamFacetKind.Feature, "22", "Steam Achievements"),
        new(SteamFacetKind.Feature, "23", "Steam Cloud"),
        new(SteamFacetKind.Feature, "30", "Steam Workshop"),
        new(SteamFacetKind.Feature, "29", "Steam Trading Cards"),
        new(SteamFacetKind.Feature, "44", "Remote Play Together"),
        new(SteamFacetKind.Feature, "41", "Remote Play on phone"),
        new(SteamFacetKind.Feature, "42", "Remote Play on tablet"),
        new(SteamFacetKind.Feature, "43", "Remote Play on TV"),
        new(SteamFacetKind.Feature, "62", "Family Sharing"),
        new(SteamFacetKind.Feature, "63", "Steam Timeline"),
        new(SteamFacetKind.Feature, "61", "HDR available"),
        new(SteamFacetKind.Feature, "13", "Captions available"),
        new(SteamFacetKind.Feature, "50", "Additional high-quality audio"),
        new(SteamFacetKind.Feature, "52", "Tracked controller support"),
        new(SteamFacetKind.Feature, "40", "SteamVR Collectibles"),
        new(SteamFacetKind.Feature, "16", "Includes Source SDK")
    ];

    /// <summary><c>controllersupport</c>.</summary>
    public static readonly IReadOnlyList<SteamFacetOption> Controller =
    [
        new(SteamFacetKind.Controller, "28", "Full controller support"),
        new(SteamFacetKind.Controller, "18", "Xbox controller support"),
        new(SteamFacetKind.Controller, "60", "Playable with controller"),
        new(SteamFacetKind.Controller, "59", "Steam Input API"),
        new(SteamFacetKind.Controller, "57", "DualSense support"),
        new(SteamFacetKind.Controller, "55", "DUALSHOCK support")
    ];

    /// <summary><c>accessibility</c> — Steam's 19 accessibility declarations.</summary>
    public static readonly IReadOnlyList<SteamFacetOption> Accessibility =
    [
        new(SteamFacetKind.Accessibility, "78", "Adjustable difficulty"),
        new(SteamFacetKind.Accessibility, "79", "Save anytime"),
        new(SteamFacetKind.Accessibility, "80", "Playable at your own pace"),
        new(SteamFacetKind.Accessibility, "64", "Adjustable text size"),
        new(SteamFacetKind.Accessibility, "65", "Subtitle options"),
        new(SteamFacetKind.Accessibility, "66", "Color alternatives"),
        new(SteamFacetKind.Accessibility, "82", "Contrast controls"),
        new(SteamFacetKind.Accessibility, "75", "Keyboard only option"),
        new(SteamFacetKind.Accessibility, "76", "Mouse only option"),
        new(SteamFacetKind.Accessibility, "77", "Touch only option"),
        new(SteamFacetKind.Accessibility, "68", "Custom volume controls"),
        new(SteamFacetKind.Accessibility, "74", "Playable without timed input"),
        new(SteamFacetKind.Accessibility, "71", "Narrated game menus"),
        new(SteamFacetKind.Accessibility, "81", "Playable without sight"),
        new(SteamFacetKind.Accessibility, "67", "Camera comfort"),
        new(SteamFacetKind.Accessibility, "69", "Stereo sound"),
        new(SteamFacetKind.Accessibility, "70", "Surround sound"),
        new(SteamFacetKind.Accessibility, "72", "Chat speech-to-text"),
        new(SteamFacetKind.Accessibility, "73", "Chat text-to-speech")
    ];

    /// <summary><c>os</c>, <c>deck_compatibility</c> and <c>vrsupport</c> in one group.</summary>
    public static readonly IReadOnlyList<SteamFacetOption> Platform =
    [
        new(SteamFacetKind.Os, "win", "Windows"),
        new(SteamFacetKind.Os, "mac", "macOS"),
        new(SteamFacetKind.Os, "linux", "SteamOS + Linux"),
        new(SteamFacetKind.Deck, "3", "Deck Verified"),
        new(SteamFacetKind.Deck, "2", "Deck Playable"),
        new(SteamFacetKind.Vr, "401", "VR only", SupportsExclude: true),
        new(SteamFacetKind.Vr, "402", "VR supported")
    ];

    /// <summary><c>specials</c> and <c>hidef2p</c>.</summary>
    public static readonly IReadOnlyList<SteamFacetOption> Price =
    [
        new(SteamFacetKind.Toggle, "specials", "Discounted only"),
        new(SteamFacetKind.Toggle, "hidef2p", "Hide free to play")
    ];

    /// <summary>
    /// Resolved from <c>appdetails</c> after the page loads, not from Steam's search facets.
    /// </summary>
    public static readonly IReadOnlyList<SteamFacetOption> Content =
    [
        new(SteamFacetKind.LocalPostFilter, "no_drm", "No third-party DRM"),
        new(SteamFacetKind.LocalPostFilter, "no_launcher", "No external launcher"),
        new(SteamFacetKind.LocalPostFilter, "no_anticheat", "No third-party anti-cheat"),
        new(SteamFacetKind.LocalPostFilter, "no_account", "No third-party account"),
        new(SteamFacetKind.LocalPostFilter, "no_eula", "No third-party EULA"),
        new(SteamFacetKind.LocalPostFilter, "has_dlc", "Has DLC"),
        new(SteamFacetKind.LocalPostFilter, "hide_adult", "Hide adult content")
    ];

    /// <summary>
    /// Review standing, judged locally.
    /// </summary>
    /// <remarks>
    /// Steam has no review-score facet — <c>review_score</c> and friends are ignored by the
    /// search endpoint, which returns the whole catalog regardless. The summary Steam prints on
    /// every result row carries its positive-review percentage, and that arrives with the search,
    /// so judging the band locally costs nothing.
    /// </remarks>
    public static readonly IReadOnlyList<SteamFacetOption> Ratings =
    [
        new(SteamFacetKind.LocalPostFilter, "rating_min_95", "Overwhelmingly positive (95%+)"),
        new(SteamFacetKind.LocalPostFilter, "rating_min_80", "Very positive (80%+)"),
        new(SteamFacetKind.LocalPostFilter, "rating_min_70", "Mostly positive (70%+)"),
        new(SteamFacetKind.LocalPostFilter, "rating_min_40", "Mixed or better (40%+)"),
        new(SteamFacetKind.LocalPostFilter, "rating_below_40", "Mostly negative (under 40%)")
    ];

    /// <summary>
    /// <c>supportedlang</c>. The store accepts well over a hundred; these are the ones worth
    /// showing before the group's search box takes over.
    /// </summary>
    public static readonly IReadOnlyList<SteamFacetOption> Languages =
    [
        new(SteamFacetKind.Language, "spanish", "Spanish - Spain"),
        new(SteamFacetKind.Language, "latam", "Spanish - Latin America"),
        new(SteamFacetKind.Language, "english", "English"),
        new(SteamFacetKind.Language, "brazilian", "Portuguese - Brazil"),
        new(SteamFacetKind.Language, "portuguese", "Portuguese - Portugal"),
        new(SteamFacetKind.Language, "french", "French"),
        new(SteamFacetKind.Language, "german", "German"),
        new(SteamFacetKind.Language, "italian", "Italian"),
        new(SteamFacetKind.Language, "japanese", "Japanese"),
        new(SteamFacetKind.Language, "koreana", "Korean"),
        new(SteamFacetKind.Language, "schinese", "Simplified Chinese"),
        new(SteamFacetKind.Language, "tchinese", "Traditional Chinese"),
        new(SteamFacetKind.Language, "russian", "Russian"),
        new(SteamFacetKind.Language, "polish", "Polish"),
        new(SteamFacetKind.Language, "turkish", "Turkish"),
        new(SteamFacetKind.Language, "ukrainian", "Ukrainian"),
        new(SteamFacetKind.Language, "dutch", "Dutch"),
        new(SteamFacetKind.Language, "swedish", "Swedish"),
        new(SteamFacetKind.Language, "norwegian", "Norwegian"),
        new(SteamFacetKind.Language, "danish", "Danish"),
        new(SteamFacetKind.Language, "finnish", "Finnish"),
        new(SteamFacetKind.Language, "czech", "Czech"),
        new(SteamFacetKind.Language, "hungarian", "Hungarian"),
        new(SteamFacetKind.Language, "greek", "Greek"),
        new(SteamFacetKind.Language, "romanian", "Romanian"),
        new(SteamFacetKind.Language, "bulgarian", "Bulgarian"),
        new(SteamFacetKind.Language, "thai", "Thai"),
        new(SteamFacetKind.Language, "vietnamese", "Vietnamese"),
        new(SteamFacetKind.Language, "arabic", "Arabic"),
        new(SteamFacetKind.Language, "indonesian", "Indonesian"),
        new(SteamFacetKind.Language, "hindi", "Hindi"),
        new(SteamFacetKind.Language, "catalan", "Catalan")
    ];
}
