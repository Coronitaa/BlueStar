using System.Collections.Generic;

namespace BlueStar.Core.Models;

/// <summary>
/// One page of Steam store search results.
/// </summary>
/// <param name="Items">The results on this page.</param>
/// <param name="TotalCount">How many results the whole query matches, per Steam.</param>
/// <param name="Start">Zero-based index of the first item.</param>
public sealed record SteamSearchPage(
    IReadOnlyList<SearchResult> Items,
    int TotalCount,
    int Start,
    string? RawPayload = null)
{
    /// <summary>An empty page, for failed or cancelled requests.</summary>
    public static SteamSearchPage Empty { get; } = new([], 0, 0);
}

/// <summary>
/// A store tag with its display name and, once resolved, how many products carry it.
/// </summary>
public sealed class SteamTag
{
    /// <summary>Steam's numeric tag id, used in <c>tags=</c> and <c>untags=</c>.</summary>
    public int TagId { get; init; }

    /// <summary>Localized display name.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Canonical English name, used to match against the group definitions.</summary>
    public string CanonicalName { get; init; } = string.Empty;

    /// <summary>
    /// Number of games and software carrying the tag, or <c>null</c> while it is still unknown.
    /// </summary>
    public int? ProductCount { get; set; }

    /// <summary>The facet option this tag maps to.</summary>
    public SteamFacetOption ToFacet() =>
        new(SteamFacetKind.Tag, TagId.ToString(System.Globalization.CultureInfo.InvariantCulture), Name);
}

/// <summary>
/// A store event, sale or fest currently running, with the products it features.
/// </summary>
public sealed class SteamStoreEvent
{
    /// <summary>Stable identifier derived from the featured category and name.</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>Event title as Steam presents it.</summary>
    public string Title { get; init; } = string.Empty;

    /// <summary>Short line under the title: end date, item count, discount.</summary>
    public string Subtitle { get; init; } = string.Empty;

    /// <summary>Key of the <c>Icons.xaml</c> geometry to show.</summary>
    public string IconKey { get; init; } = "IconFlame";

    /// <summary>Store URL for the event, if it has a landing page.</summary>
    public string? Url { get; init; }

    /// <summary>
    /// The products featured in the event, as Steam already describes them in the featured
    /// feed — name, capsule, price and discount all arrive with the event itself, so showing
    /// them costs no further request.
    /// </summary>
    public IReadOnlyList<SearchResult> Items { get; init; } = [];

    /// <summary>AppIDs featured in the event.</summary>
    public IReadOnlyList<uint> AppIds { get; init; } = [];

    /// <summary>Whether picking this event can narrow the results, or only open the store.</summary>
    public bool CanFilter => Items.Count > 0;
}
