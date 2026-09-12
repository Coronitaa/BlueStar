using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BlueStar.Core.Models;

namespace BlueStar.Core.Interfaces;

/// <summary>
/// Faceted search against the Steam store, plus the tag catalog and store events that drive
/// the Explore filter panel.
/// </summary>
public interface ISteamCatalogSearchService
{
    /// <summary>
    /// Runs a faceted query and returns one page of results together with the total match count.
    /// Each result carries the store tags Steam already ships in the response, so no extra
    /// request is needed to show them.
    /// </summary>
    Task<SteamSearchPage> SearchAsync(SteamSearchQuery query, CancellationToken ct = default);

    /// <summary>
    /// Reads only the number of products a query matches, using a single-result probe.
    /// Results are cached, since these counts move slowly.
    /// </summary>
    /// <returns>
    /// The match count, or <c>null</c> when it could not be read — Steam was unreachable or is
    /// refusing requests. Callers show nothing rather than a zero they cannot stand behind.
    /// </returns>
    Task<int?> GetMatchCountAsync(SteamSearchQuery query, CancellationToken ct = default);

    /// <summary>
    /// Store events, sales and fests running right now, each with the AppIDs it features.
    /// </summary>
    Task<IReadOnlyList<SteamStoreEvent>> GetStoreEventsAsync(CancellationToken ct = default);

    /// <summary>
    /// Resolves a partial title into the full product names Steam's autocomplete offers for it.
    /// </summary>
    /// <remarks>
    /// The faceted search matches whole words and answers a half-typed title with nothing, so
    /// this is what a search falls back to before giving up. It returns names rather than
    /// results on purpose: a name can be fed straight back into the faceted search, which keeps
    /// the filters, the tags and the review scores that this endpoint does not carry.
    /// </remarks>
    /// <returns>At most ten products, in Steam's own relevance order; empty if nothing matched.</returns>
    Task<IReadOnlyList<SteamTitleSuggestion>> SuggestTitlesAsync(string term, CancellationToken ct = default);
}

/// <summary>
/// The Steam store tag catalog, grouped for the filter panel.
/// </summary>
public interface ISteamTagCatalogService
{
    /// <summary>
    /// All store tags Steam publishes, keyed by tag id. Cached for a month.
    /// </summary>
    Task<IReadOnlyDictionary<int, SteamTag>> GetCatalogAsync(CancellationToken ct = default);

    /// <summary>
    /// The tags belonging to a thematic group, in display order. Tags no group claims are
    /// returned under <see cref="SteamTagGroups.OtherGroupKey"/>.
    /// </summary>
    Task<IReadOnlyList<SteamTag>> GetGroupAsync(string groupKey, CancellationToken ct = default);

    /// <summary>
    /// Fills in <see cref="SteamTag.ProductCount"/> for the given tags, one probe each, cached.
    /// Call it only for the tags currently on screen.
    /// </summary>
    Task ResolveCountsAsync(IEnumerable<SteamTag> tags, CancellationToken ct = default);

    /// <summary>
    /// Picks a tag at random from a group, skipping any the caller has already selected.
    /// </summary>
    Task<SteamTag?> GetRandomAsync(string groupKey, IReadOnlyCollection<int> excludeTagIds, CancellationToken ct = default);

    /// <summary>
    /// Resolves tag ids to display names, for the bubbles shown on a result card.
    /// </summary>
    Task<IReadOnlyList<string>> ResolveNamesAsync(IEnumerable<int> tagIds, CancellationToken ct = default);
}
