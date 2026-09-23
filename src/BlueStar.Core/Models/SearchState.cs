namespace BlueStar.Core.Models;

/// <summary>
/// Represents the distinct lifecycle states of a catalog search operation.
/// Used to eliminate visual flicker and coordinate empty, loading, and refreshing states.
/// </summary>
public enum SearchState
{
    /// <summary>
    /// Initial or inactive state before any search has been performed.
    /// </summary>
    Idle,

    /// <summary>
    /// First search or query with no existing results, displaying skeletons.
    /// </summary>
    LoadingInitial,

    /// <summary>
    /// Query updated while previous results remain displayed, showing a subtle refresh indicator.
    /// </summary>
    Refreshing,

    /// <summary>
    /// Search completed with one or more valid results to display.
    /// </summary>
    ShowingResults,

    /// <summary>
    /// Search completed normally but produced zero matching results.
    /// </summary>
    Empty,

    /// <summary>
    /// Search failed due to a network error, rate limit, timeout, or malformed response.
    /// </summary>
    Error
}
