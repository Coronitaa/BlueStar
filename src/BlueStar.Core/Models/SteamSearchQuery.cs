using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace BlueStar.Core.Models;

/// <summary>
/// A faceted query against the Steam store search endpoint.
/// </summary>
/// <remarks>
/// Only the parameters Steam actually honours are emitted. Facets whose
/// <see cref="SteamFacetKind"/> is a local one are ignored here and applied client-side after
/// the results come back.
/// </remarks>
public sealed class SteamSearchQuery
{
    private const string Endpoint = "https://store.steampowered.com/search/results/";

    /// <summary>Free-text term. Empty means "browse".</summary>
    public string? Term { get; init; }

    /// <summary>
    /// Product types to search, as a <c>category1</c> value. Defaults to games and software;
    /// DLC, soundtracks, videos, hardware, demos, playtests and mods are never included.
    /// </summary>
    public string AppTypes { get; init; } = SteamStoreFacets.AppTypeAll;

    /// <summary>Sort token, e.g. <c>Reviews_DESC</c>. Empty leaves the order to Steam.</summary>
    public string SortBy { get; init; } = string.Empty;

    /// <summary>Curated list token, e.g. <c>popularnew</c>. Narrows the pool, not the order.</summary>
    public string? StoreList { get; init; }

    /// <summary>Zero-based index of the first result.</summary>
    public int Start { get; init; }

    /// <summary>How many results to ask for.</summary>
    public int Count { get; init; } = 20;

    /// <summary>Store language, e.g. <c>english</c>.</summary>
    public string Language { get; init; } = "english";

    /// <summary>Store country, which decides currency and availability.</summary>
    public string CountryCode { get; init; } = "US";

    /// <summary>Selected facet options and their state.</summary>
    public IReadOnlyDictionary<SteamFacetOption, FacetState> Facets { get; init; } =
        new Dictionary<SteamFacetOption, FacetState>();

    /// <summary>
    /// Restrict results to these AppIDs (e.g. used by store-event chips or local candidate sets).
    /// Applied locally to matching results as Steam's store search does not filter by arbitrary AppID lists.
    /// </summary>
    public IReadOnlyCollection<uint>? RestrictToAppIds { get; init; }

    /// <summary>
    /// Builds the request URL. Multi-valued parameters are comma-joined, which is how the store
    /// search page itself submits them.
    /// </summary>
    public string ToUrl()
    {
        var bag = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        void Add(string key, string value)
        {
            if (!bag.TryGetValue(key, out var list))
            {
                list = [];
                bag[key] = list;
            }

            if (!list.Contains(value, StringComparer.Ordinal))
            {
                list.Add(value);
            }
        }

        foreach (var (option, state) in Facets)
        {
            if (state == FacetState.Neutral) continue;

            switch (option.Kind)
            {
                case SteamFacetKind.Tag:
                    Add(state == FacetState.Exclude ? "untags" : "tags", option.Value);
                    break;

                case SteamFacetKind.PlayerSupport when state == FacetState.Include:
                    Add("category3", option.Value);
                    break;

                case SteamFacetKind.Feature when state == FacetState.Include:
                    Add("category2", option.Value);
                    break;

                case SteamFacetKind.Controller when state == FacetState.Include:
                    Add("controllersupport", option.Value);
                    break;

                case SteamFacetKind.Accessibility when state == FacetState.Include:
                    Add("accessibility", option.Value);
                    break;

                case SteamFacetKind.Os when state == FacetState.Include:
                    Add("os", option.Value);
                    break;

                case SteamFacetKind.Deck when state == FacetState.Include:
                    Add("deck_compatibility", option.Value);
                    break;

                case SteamFacetKind.Vr:
                    Add(state == FacetState.Exclude && option.SupportsExclude ? "unvrsupport" : "vrsupport", option.Value);
                    break;

                case SteamFacetKind.Language when state == FacetState.Include:
                    Add("supportedlang", option.Value);
                    break;

                case SteamFacetKind.Toggle when state == FacetState.Include:
                    Add(option.Value, "1");
                    break;

                // StoreList is carried by the dedicated property; local kinds never reach Steam.
            }
        }

        var sb = new StringBuilder(Endpoint);
        sb.Append("?query=");

        if (!string.IsNullOrWhiteSpace(Term))
        {
            sb.Append("&term=").Append(Uri.EscapeDataString(Term.Trim()));
        }

        sb.Append("&category1=").Append(Uri.EscapeDataString(AppTypes));

        // The store honours both at once: filter= picks the pool, sort_by= orders it. Treating
        // them as rivals is what left one of the two selectors showing a value nothing read.
        if (!string.IsNullOrWhiteSpace(StoreList))
        {
            sb.Append("&filter=").Append(Uri.EscapeDataString(StoreList));
        }

        if (!string.IsNullOrWhiteSpace(SortBy))
        {
            sb.Append("&sort_by=").Append(Uri.EscapeDataString(SortBy));
        }

        foreach (var (key, values) in bag.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            sb.Append('&').Append(key).Append('=')
              .Append(Uri.EscapeDataString(string.Join(',', values)));
        }

        sb.Append("&start=").Append(Start.ToString(CultureInfo.InvariantCulture));
        sb.Append("&count=").Append(Count.ToString(CultureInfo.InvariantCulture));
        sb.Append("&l=").Append(Uri.EscapeDataString(Language));
        sb.Append("&cc=").Append(Uri.EscapeDataString(CountryCode));
        sb.Append("&infinite=1&json=1");

        return sb.ToString();
    }

    /// <summary>
    /// A copy of this query that asks for a single result, used to read <c>total_count</c>
    /// without paying for a full page.
    /// </summary>
    public SteamSearchQuery AsCountProbe() => new()
    {
        Term = Term,
        AppTypes = AppTypes,
        SortBy = SortBy,
        StoreList = StoreList,
        Start = 0,
        Count = 1,
        Language = Language,
        CountryCode = CountryCode,
        Facets = Facets
    };
}
