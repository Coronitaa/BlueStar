using System.Collections.Generic;

namespace BlueStar.Core.Models;

/// <summary>
/// Resolution method that produced the search results.
/// </summary>
public enum SearchResolutionType
{
    ExactAppId,
    ExactName,
    Prefix,
    FullText,
    Alias,
    FallbackSteam
}

/// <summary>
/// Structured search request representing the user's intent across both selectors:
/// POOL (universo de datos) and SORT (ordenamiento determinista).
/// </summary>
public sealed record SearchRequest
{
    public string? RawQuery { get; init; }
    public string? Pool { get; init; } = "all";
    public string? SortBy { get; init; } = "Relevance";
    public bool Descending { get; init; } = true;
    public string AppTypes { get; init; } = SteamStoreFacets.AppTypeAll;
    public IReadOnlyCollection<int> IncludedTagIds { get; init; } = [];
    public IReadOnlyCollection<int> ExcludedTagIds { get; init; } = [];
    public bool? HasWindows { get; init; }
    public bool? HasMac { get; init; }
    public bool? HasLinux { get; init; }
    public int? MinRatingPercent { get; init; }
    public int? MaxRatingPercent { get; init; }
    public bool? NoDrm { get; init; }
    public bool? NoExternalLauncher { get; init; }
    public bool? NoAntiCheat { get; init; }
    public bool? NoAccount { get; init; }
    public bool? NoEula { get; init; }
    public bool? HideAdult { get; init; }
    public bool? DiscountedOnly { get; init; }
    public int? MaxPriceCents { get; init; }
    public int Start { get; init; } = 0;
    public int Count { get; init; } = 50;
    public IReadOnlyCollection<uint>? RestrictToAppIds { get; init; }
    public IReadOnlyDictionary<SteamFacetOption, FacetState> Facets { get; init; } = new Dictionary<SteamFacetOption, FacetState>();
}

/// <summary>
/// Result of executing a search request through the layered search pipeline.
/// </summary>
public sealed record SearchResponse
{
    public IReadOnlyList<SearchResult> Items { get; init; } = [];
    public int TotalCount { get; init; }
    public int Start { get; init; }
    public SearchResolutionType ResolutionType { get; init; }
    public bool IsFromLocalCatalog { get; init; }
    public string? AnomalyDetected { get; init; }
    public bool IsStaleData { get; init; }

    public static SearchResponse Empty => new()
    {
        Items = [],
        TotalCount = 0,
        Start = 0,
        ResolutionType = SearchResolutionType.FullText,
        IsFromLocalCatalog = true
    };
}
