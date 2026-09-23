using System.Collections.Generic;
using BlueStar.Core.Models;
using BlueStar.Infrastructure.Steam;
using Xunit;

namespace BlueStar.Infrastructure.Tests;

public class SteamResponseValidatorTests
{
    private readonly SteamResponseValidator _validator = new();

    [Fact]
    public void ValidateResponse_WhenPayloadIsEmpty_ReturnsEmptyPayloadAnomaly()
    {
        var query = new SteamSearchQuery { Term = "test" };
        var result = _validator.ValidateResponse(query, "", [], 0);

        Assert.False(result.IsValid);
        Assert.Equal(SteamAnomalyType.EmptyPayload, result.Anomaly);
    }

    [Fact]
    public void ValidateResponse_WhenResultsHtmlIsMissing_ReturnsMissingResultsHtmlAnomaly()
    {
        var query = new SteamSearchQuery { Term = "test" };
        var result = _validator.ValidateResponse(query, "{\"total_count\": 50}", [], 0);

        Assert.False(result.IsValid);
        Assert.Equal(SteamAnomalyType.MissingResultsHtml, result.Anomaly);
    }

    [Fact]
    public void ValidateResponse_WhenJsonIsMalformed_ReturnsMalformedHtmlAnomaly()
    {
        var query = new SteamSearchQuery { Term = "test" };
        var result = _validator.ValidateResponse(query, "{\"not_json: true", [], 0);

        Assert.False(result.IsValid);
        Assert.Equal(SteamAnomalyType.MalformedHtml, result.Anomaly);
    }

    [Fact]
    public void ValidateResponse_WhenTotalCountPositiveButZeroItemsOnStartZero_ReturnsInconsistentTotalCount()
    {
        var query = new SteamSearchQuery { Term = "test", Start = 0 };
        var result = _validator.ValidateResponse(query, "{\"results_html\": \"\"}", [], 10);

        Assert.False(result.IsValid);
        Assert.Equal(SteamAnomalyType.InconsistentTotalCount, result.Anomaly);
    }

    [Fact]
    public void ValidateResponse_WhenOpposingSortYieldsIdenticalAppIds_DetectsIgnoredParameter()
    {
        var itemsDesc = new List<SearchResult>
        {
            new() { AppId = 101, Name = "Game A" },
            new() { AppId = 102, Name = "Game B" },
            new() { AppId = 103, Name = "Game C" }
        };

        var queryDesc = new SteamSearchQuery { Term = "puzzle", SortBy = "Reviews_DESC" };
        var resDesc = _validator.ValidateResponse(queryDesc, "{\"results_html\": \"ok\"}", itemsDesc, 3);
        Assert.True(resDesc.IsValid);

        // Send opposing sort with same leading items (simulating Steam ignoring Reviews_ASC)
        var itemsAsc = new List<SearchResult>
        {
            new() { AppId = 101, Name = "Game A" },
            new() { AppId = 102, Name = "Game B" },
            new() { AppId = 103, Name = "Game C" }
        };

        var queryAsc = new SteamSearchQuery { Term = "puzzle", SortBy = "Reviews_ASC" };
        var resAsc = _validator.ValidateResponse(queryAsc, "{\"results_html\": \"ok\"}", itemsAsc, 3);

        Assert.True(resAsc.IsValid);
        Assert.Equal(SteamAnomalyType.SemanticParameterIgnored, resAsc.Anomaly);
        Assert.True(resAsc.RequiresLocalSortFallback);

        // Future queries with this parameter are automatically flagged as ignored
        Assert.True(SteamResponseValidator.IsKnownIgnoredParameter("Reviews_ASC"));
    }
}
