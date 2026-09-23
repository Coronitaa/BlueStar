using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BlueStar.Core.Models;

namespace BlueStar.Infrastructure.Steam;

/// <summary>
/// Categorization of semantic or structural anomalies detected in Steam responses.
/// </summary>
public enum SteamAnomalyType
{
    None,
    SemanticParameterIgnored,
    EmptyPayload,
    MissingResultsHtml,
    InconsistentTotalCount,
    MalformedHtml
}

/// <summary>
/// Fingerprint of a Steam search response used to detect when Steam ignores filters,
/// discards sort orders, or returns identical listings for opposite queries.
/// </summary>
public sealed record SteamResponseFingerprint
{
    public string CanonicalKey { get; init; } = string.Empty;
    public string Term { get; init; } = string.Empty;
    public string SortBy { get; init; } = string.Empty;
    public int TotalCount { get; init; }
    public IReadOnlyList<uint> AppIds { get; init; } = [];
    public string Hash { get; init; } = string.Empty;

    public static SteamResponseFingerprint Create(string canonicalKey, string term, string sortBy, int totalCount, IReadOnlyList<uint> appIds)
    {
        var raw = $"{canonicalKey}|{totalCount}|{string.Join(',', appIds.Take(10))}";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)))[..16];

        return new SteamResponseFingerprint
        {
            CanonicalKey = canonicalKey,
            Term = term,
            SortBy = sortBy,
            TotalCount = totalCount,
            AppIds = appIds,
            Hash = hash
        };
    }
}

/// <summary>
/// Validation result indicating whether a Steam response respected the requested semantics.
/// </summary>
public sealed record SteamValidationResult
{
    public bool IsValid { get; init; } = true;
    public SteamAnomalyType Anomaly { get; init; } = SteamAnomalyType.None;
    public string? Description { get; init; }
    public bool RequiresLocalSortFallback { get; init; }

    public static SteamValidationResult Success => new() { IsValid = true, Anomaly = SteamAnomalyType.None };
}

/// <summary>
/// Detects semantic anomalies in Steam HTTP 200 responses where requested parameters
/// (such as sort direction or specific filters) were quietly ignored by the server.
/// </summary>
public sealed class SteamResponseValidator
{
    private static readonly ConcurrentDictionary<string, SteamResponseFingerprint> Fingerprints = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, bool> IgnoredParameters = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Checks whether a given sort token is known to be ignored by Steam.
    /// </summary>
    public static bool IsKnownIgnoredParameter(string parameter) =>
        IgnoredParameters.ContainsKey(parameter);

    /// <summary>
    /// Validates a raw JSON payload and parsed results against the query parameters.
    /// Detects ignored sort orders (e.g. Reviews_ASC producing the same output as Reviews_DESC),
    /// payload anomalies, and inconsistent counts.
    /// </summary>
    public SteamValidationResult ValidateResponse(
        SteamSearchQuery query,
        string? rawPayload,
        IReadOnlyList<SearchResult> items,
        int totalCount)
    {
        // 1. Empty payload detection
        if (string.IsNullOrWhiteSpace(rawPayload))
        {
            return new SteamValidationResult
            {
                IsValid = false,
                Anomaly = SteamAnomalyType.EmptyPayload,
                Description = "Steam response payload was completely empty."
            };
        }

        // 2. Missing results_html detection
        try
        {
            using var doc = JsonDocument.Parse(rawPayload);
            if (!doc.RootElement.TryGetProperty("results_html", out var htmlProp) || htmlProp.ValueKind != JsonValueKind.String)
            {
                return new SteamValidationResult
                {
                    IsValid = false,
                    Anomaly = SteamAnomalyType.MissingResultsHtml,
                    Description = "JSON response did not contain expected 'results_html' property."
                };
            }
        }
        catch (JsonException ex)
        {
            return new SteamValidationResult
            {
                IsValid = false,
                Anomaly = SteamAnomalyType.MalformedHtml,
                Description = $"Invalid JSON envelope: {ex.Message}"
            };
        }

        // 3. Inconsistent total count detection
        if (totalCount > 0 && query.Start == 0 && items.Count == 0)
        {
            return new SteamValidationResult
            {
                IsValid = false,
                Anomaly = SteamAnomalyType.InconsistentTotalCount,
                Description = $"Steam reported total_count={totalCount} but returned 0 items on start=0."
            };
        }

        var appIds = items.Select(i => i.AppId).Take(10).ToList();
        var termKey = (query.Term ?? "").Trim().ToLowerInvariant();
        var sortKey = (query.SortBy ?? "").Trim();

        // 4. Check if the current sort token is already known to be ignored
        if (!string.IsNullOrEmpty(sortKey) && IgnoredParameters.ContainsKey(sortKey))
        {
            return new SteamValidationResult
            {
                IsValid = true,
                Anomaly = SteamAnomalyType.SemanticParameterIgnored,
                Description = $"Parameter '{sortKey}' is a known ignored parameter by Steam; local sorting required.",
                RequiresLocalSortFallback = true
            };
        }

        var canonicalKey = $"{termKey}:{sortKey}";
        var fingerprint = SteamResponseFingerprint.Create(canonicalKey, termKey, sortKey, totalCount, appIds);
        Fingerprints[canonicalKey] = fingerprint;

        // 5. Compare against opposing query (e.g. Reviews_DESC vs Reviews_ASC, Name_ASC vs Name_DESC)
        if (!string.IsNullOrEmpty(sortKey) && appIds.Count >= 3)
        {
            var oppositeSort = GetOppositeSort(sortKey);
            if (!string.IsNullOrEmpty(oppositeSort))
            {
                var oppositeKey = $"{termKey}:{oppositeSort}";
                if (Fingerprints.TryGetValue(oppositeKey, out var oppositeFp) && oppositeFp.AppIds.Count >= 3)
                {
                    // Check if first 3+ items are identical despite opposing sort directions
                    var identicalLeading = appIds.Take(5).SequenceEqual(oppositeFp.AppIds.Take(5));
                    if (identicalLeading)
                    {
                        // Mark parameter as ignored
                        var ignoredParam = sortKey.EndsWith("_ASC", StringComparison.OrdinalIgnoreCase) ? sortKey : oppositeSort;
                        IgnoredParameters[ignoredParam] = true;

                        return new SteamValidationResult
                        {
                            IsValid = true,
                            Anomaly = SteamAnomalyType.SemanticParameterIgnored,
                            Description = $"Opposing sort '{sortKey}' and '{oppositeSort}' yielded identical top results. Steam ignored the sort parameter.",
                            RequiresLocalSortFallback = true
                        };
                    }
                }
            }
        }

        return SteamValidationResult.Success;
    }

    private static string? GetOppositeSort(string sort)
    {
        if (sort.EndsWith("_DESC", StringComparison.OrdinalIgnoreCase))
            return sort[..^5] + "_ASC";
        if (sort.EndsWith("_ASC", StringComparison.OrdinalIgnoreCase))
            return sort[..^4] + "_DESC";
        return null;
    }
}
