using System.Threading;
using System.Threading.Tasks;
using BlueStar.Core.Models;

namespace BlueStar.Core.Interfaces;

/// <summary>
/// Layered search pipeline executing ParseQuery → ResolveIntent → LocalCandidateSearch → Filter → Sort → Page → LiveEnrichment.
/// Prioritizes the local SQLite FTS5 catalog to eliminate unnecessary requests to Steam.
/// </summary>
public interface ISearchPipeline
{
    /// <summary>
    /// Executes the search request through the pipeline.
    /// </summary>
    Task<SearchResponse> ExecuteAsync(SearchRequest request, CancellationToken ct = default);
}
