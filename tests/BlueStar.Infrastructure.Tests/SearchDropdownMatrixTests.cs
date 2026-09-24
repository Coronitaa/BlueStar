using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BlueStar.Core.Interfaces;
using BlueStar.Core.Models;
using BlueStar.Infrastructure.Catalog;
using BlueStar.Infrastructure.Search;
using BlueStar.Infrastructure.Steam;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BlueStar.Infrastructure.Tests;

public class SearchDropdownMatrixTests : IDisposable
{
    private readonly string _tempDb;
    private readonly LocalCatalogRepository _localRepo;
    private readonly FakeSteamService _fakeSteam;
    private readonly FakeMetadataProvider _fakeMeta;
    private readonly SearchPipeline _pipeline;

    public SearchDropdownMatrixTests()
    {
        _tempDb = Path.Combine(Path.GetTempPath(), $"test_matrix_{Guid.NewGuid():N}.sqlite");
        _localRepo = new LocalCatalogRepository(NullLogger<LocalCatalogRepository>.Instance, _tempDb);
        _fakeSteam = new FakeSteamService();
        _fakeMeta = new FakeMetadataProvider();
        _pipeline = new SearchPipeline(_localRepo, _fakeSteam, _fakeMeta, new SteamResponseValidator(), NullLogger<SearchPipeline>.Instance);

        // Seed fake items with distinct names, reviews, dates, prices, and deck compatibility
        _fakeSteam.StubResults.AddRange([
            new SearchResult
            {
                AppId = 100,
                Name = "Alpha Game",
                ReviewSummary = "Overwhelmingly Positive",
                ReviewPercent = 95,
                DiscountPercent = 10,
                PriceText = "$29.99",
                ReleaseDateText = "1 Jan, 2026",
                DeckCompatibility = "Verified"
            },
            new SearchResult
            {
                AppId = 200,
                Name = "Beta Game",
                ReviewSummary = "Mixed",
                ReviewPercent = 60,
                DiscountPercent = 75,
                PriceText = "Free",
                ReleaseDateText = "15 Jun, 2026",
                DeckCompatibility = "Playable"
            },
            new SearchResult
            {
                AppId = 300,
                Name = "Zeta Game",
                ReviewSummary = "Very Positive",
                ReviewPercent = 85,
                DiscountPercent = 50,
                PriceText = "$9.99",
                ReleaseDateText = "10 Dec, 2026",
                DeckCompatibility = "Unsupported"
            },
            new SearchResult
            {
                AppId = 291550,
                Name = "Brawlhalla",
                ReviewSummary = "Very Positive",
                ReviewPercent = 83,
                DiscountPercent = 0,
                PriceText = "Free to Play",
                ReleaseDateText = "17 Oct, 2017"
            }
        ]);

        // Seed metadata for deterministic AppID / DepotID tests
        _fakeMeta.MetaDatabase[730] = new GameMetadata
        {
            AppId = 730,
            Name = "Counter-Strike 2",
            ReleaseDate = "21 Aug, 2012",
            HeaderImageUrl = "https://cdn.steam.com/730.jpg",
            Platforms = ["Windows", "Linux"]
        };
        _fakeMeta.MetaDatabase[400] = new GameMetadata
        {
            AppId = 400,
            Name = "Portal",
            ReleaseDate = "10 Oct, 2007",
            HeaderImageUrl = "https://cdn.steam.com/400.jpg",
            Platforms = ["Windows", "macOS", "Linux"]
        };
    }

    public void Dispose()
    {
        _localRepo.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { if (File.Exists(_tempDb)) File.Delete(_tempDb); } catch { }
    }

    [Theory]
    [InlineData("all")]
    [InlineData("popularnew")]
    [InlineData("globaltopsellers")]
    [InlineData("comingsoon")]
    public async Task ExecuteAsync_AllPools_ReturnResultsWithoutCrashing(string pool)
    {
        var request = new SearchRequest
        {
            Pool = pool,
            SortBy = string.Empty,
            Descending = true
        };

        var response = await _pipeline.ExecuteAsync(request);

        Assert.NotNull(response);
        Assert.True(response.TotalCount > 0);
        Assert.NotEmpty(response.Items);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ExecuteAsync_SortByName_OrdersAlphabeticallyCorrectly(bool descending)
    {
        var request = new SearchRequest
        {
            Pool = "popularnew",
            SortBy = "name",
            Descending = descending
        };

        var response = await _pipeline.ExecuteAsync(request);

        Assert.Equal(3, response.Items.Count);
        if (descending)
        {
            Assert.Equal("Zeta Game", response.Items[0].Name);
            Assert.Equal("Alpha Game", response.Items[2].Name);
        }
        else
        {
            Assert.Equal("Alpha Game", response.Items[0].Name);
            Assert.Equal("Zeta Game", response.Items[2].Name);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ExecuteAsync_SortByReviews_OrdersByReviewPercentCorrectly(bool descending)
    {
        var request = new SearchRequest
        {
            Pool = "popularnew",
            SortBy = "reviews",
            Descending = descending
        };

        var response = await _pipeline.ExecuteAsync(request);

        Assert.Equal(3, response.Items.Count);
        if (descending)
        {
            Assert.Equal(95, response.Items[0].ReviewPercent);
            Assert.Equal(60, response.Items[2].ReviewPercent);
        }
        else
        {
            Assert.Equal(60, response.Items[0].ReviewPercent);
            Assert.Equal(95, response.Items[2].ReviewPercent);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ExecuteAsync_SortByPrice_OrdersByActualPriceCorrectly(bool descending)
    {
        var request = new SearchRequest
        {
            Pool = "popularnew",
            SortBy = "price",
            Descending = descending
        };

        var response = await _pipeline.ExecuteAsync(request);

        Assert.Equal(3, response.Items.Count);
        if (descending)
        {
            // Highest price first: $29.99
            Assert.Equal("Alpha Game", response.Items[0].Name);
            Assert.Equal("Beta Game", response.Items[2].Name); // Free
        }
        else
        {
            // Lowest price first: Free (0.00)
            Assert.Equal("Beta Game", response.Items[0].Name);
            Assert.Equal("Alpha Game", response.Items[2].Name); // $29.99
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ExecuteAsync_SortByDiscount_OrdersByDiscountPercentCorrectly(bool descending)
    {
        var request = new SearchRequest
        {
            Pool = "popularnew",
            SortBy = "discount",
            Descending = descending
        };

        var response = await _pipeline.ExecuteAsync(request);

        Assert.Equal(3, response.Items.Count);
        if (descending)
        {
            Assert.Equal(75, response.Items[0].DiscountPercent);
            Assert.Equal(10, response.Items[2].DiscountPercent);
        }
        else
        {
            Assert.Equal(10, response.Items[0].DiscountPercent);
            Assert.Equal(75, response.Items[2].DiscountPercent);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ExecuteAsync_SortByReleased_OrdersByReleaseDateCorrectly(bool descending)
    {
        var request = new SearchRequest
        {
            Pool = "popularnew",
            SortBy = "released",
            Descending = descending
        };

        var response = await _pipeline.ExecuteAsync(request);

        Assert.Equal(3, response.Items.Count);
        if (descending)
        {
            // 2025 is newest
            Assert.Equal("Zeta Game", response.Items[0].Name);
            // 2021 is oldest
            Assert.Equal("Alpha Game", response.Items[2].Name);
        }
        else
        {
            // 2021 is oldest
            Assert.Equal("Alpha Game", response.Items[0].Name);
            // 2025 is newest
            Assert.Equal("Zeta Game", response.Items[2].Name);
        }
    }

    [Theory]
    [InlineData("all", "")]
    [InlineData("all", "name")]
    [InlineData("all", "reviews")]
    [InlineData("all", "price")]
    [InlineData("all", "released")]
    [InlineData("all", "deckcompatdate")]
    [InlineData("popularnew", "")]
    [InlineData("popularnew", "name")]
    [InlineData("popularnew", "reviews")]
    [InlineData("popularnew", "price")]
    [InlineData("popularnew", "released")]
    [InlineData("popularnew", "deckcompatdate")]
    [InlineData("globaltopsellers", "")]
    [InlineData("globaltopsellers", "name")]
    [InlineData("globaltopsellers", "reviews")]
    [InlineData("globaltopsellers", "price")]
    [InlineData("globaltopsellers", "released")]
    [InlineData("globaltopsellers", "deckcompatdate")]
    [InlineData("comingsoon", "")]
    public async Task ExecuteAsync_MatrixOfPoolsAndSorts_ExecutesDeterministically(string pool, string sortBy)
    {
        foreach (var desc in new[] { true, false })
        {
            var req = new SearchRequest
            {
                Pool = pool,
                SortBy = sortBy,
                Descending = desc
            };

            var res = await _pipeline.ExecuteAsync(req);
            Assert.NotNull(res);
            Assert.NotNull(res.Items);
        }
    }

    [Fact]
    public async Task ExecuteAsync_PopularNewReleases_FiltersOutLegacyReleasesLikeBrawlhalla2017()
    {
        var request = new SearchRequest
        {
            Pool = "popularnew",
            SortBy = "price",
            Descending = false
        };

        var response = await _pipeline.ExecuteAsync(request);

        // Brawlhalla (2017) MUST NOT be present under Popular New Releases!
        Assert.DoesNotContain(response.Items, i => i.Name == "Brawlhalla");
        Assert.All(response.Items, i => Assert.Contains("2026", i.ReleaseDateText ?? ""));
    }

    [Fact]
    public async Task ExecuteAsync_GlobalTopSellers_IncludesLegacyReleases()
    {
        var request = new SearchRequest
        {
            Pool = "globaltopsellers",
            SortBy = "name",
            Descending = false
        };

        var response = await _pipeline.ExecuteAsync(request);

        // Brawlhalla is allowed in top sellers
        Assert.Contains(response.Items, i => i.Name == "Brawlhalla");
    }

    [Fact]
    public async Task ExecuteAsync_AppIdSearch_ResolvesDeterministically()
    {
        var request = new SearchRequest
        {
            RawQuery = "730"
        };

        var response = await _pipeline.ExecuteAsync(request);

        Assert.NotNull(response);
        Assert.Equal(1, response.TotalCount);
        Assert.Single(response.Items);
        Assert.Equal("Counter-Strike 2", response.Items[0].Name);
        Assert.Equal(730u, response.Items[0].AppId);
        Assert.Equal(SearchResolutionType.ExactAppId, response.ResolutionType);
    }

    [Fact]
    public async Task ExecuteAsync_DepotIdSearch_ResolvesParentAppDeterministically()
    {
        // 731 is the primary depot of CS2 (730)
        var request = new SearchRequest
        {
            RawQuery = "depot:731"
        };

        var response = await _pipeline.ExecuteAsync(request);

        Assert.NotNull(response);
        Assert.Equal(1, response.TotalCount);
        Assert.Single(response.Items);
        Assert.Equal("Counter-Strike 2", response.Items[0].Name);
        Assert.Equal(730u, response.Items[0].AppId);
    }

    [Fact]
    public async Task ExecuteAsync_BareDepotIdNumber_ResolvesParentAppDeterministically()
    {
        // User typed bare number 731 into search box; when AppID 731 misses, depot fallback resolves 730
        var request = new SearchRequest
        {
            RawQuery = "731"
        };

        var response = await _pipeline.ExecuteAsync(request);

        Assert.NotNull(response);
        Assert.Equal(1, response.TotalCount);
        Assert.Single(response.Items);
        Assert.Equal("Counter-Strike 2", response.Items[0].Name);
    }

    [Fact]
    public void ReviewDisplayText_FormatsDescriptionAndPercentageCorrectly()
    {
        var item1 = new SearchResult
        {
            ReviewSummary = "Overwhelmingly Positive",
            ReviewPercent = 100
        };
        Assert.Equal("Overwhelmingly Positive (100%)", item1.ReviewDisplayText);

        var item2 = new SearchResult
        {
            ReviewSummary = "Very Positive",
            ReviewPercent = 89
        };
        Assert.Equal("Very Positive (89%)", item2.ReviewDisplayText);

        var item3 = new SearchResult
        {
            ReviewSummary = null,
            ReviewPercent = 92
        };
        Assert.Equal("92%", item3.ReviewDisplayText);

        var item4 = new SearchResult
        {
            ReviewSummary = "Positive",
            ReviewPercent = null
        };
        Assert.Equal("Positive", item4.ReviewDisplayText);

        // Crucial fix: when ReviewSummary is already just a percentage string like "35%" or "35",
        // it must derive the proper text summary and never output "35% (35%)"
        var item5 = new SearchResult
        {
            ReviewSummary = "35%",
            ReviewPercent = 35
        };
        Assert.Equal("Mostly Negative (35%)", item5.ReviewDisplayText);
        Assert.DoesNotContain("35% (35%)", item5.ReviewDisplayText);

        var item6 = new SearchResult
        {
            ReviewSummary = "35",
            ReviewPercent = 35
        };
        Assert.Equal("Mostly Negative (35%)", item6.ReviewDisplayText);
    }

    [Fact]
    public async Task ExecuteAsync_AppTypesFilter_FiltersGamesAndSoftwareDeterministically()
    {
        // Seed 1 Game and 1 Software into local repo
        await _localRepo.UpsertAppsAsync([
            new CatalogAppItem
            {
                AppId = 400,
                Name = "Portal",
                NormalizedName = "portal",
                CompactName = "portal",
                AppType = "Game"
            },
            new CatalogAppItem
            {
                AppId = 365670,
                Name = "Blender",
                NormalizedName = "blender",
                CompactName = "blender",
                AppType = "Application"
            }
        ]);

        _fakeSteam.StubResults.Add(new SearchResult
        {
            AppId = 400,
            Name = "Portal",
            AppType = "Game",
            ReleaseDateText = "10 Oct, 2026"
        });
        _fakeSteam.StubResults.Add(new SearchResult
        {
            AppId = 365670,
            Name = "Blender",
            AppType = "Application",
            ReleaseDateText = "10 Oct, 2026"
        });

        // 1. Games only (998)
        var gamesReq = new SearchRequest
        {
            Pool = "all",
            AppTypes = "998"
        };
        var gamesResp = await _pipeline.ExecuteAsync(gamesReq);
        Assert.Contains(gamesResp.Items, i => i.AppId == 400);
        Assert.DoesNotContain(gamesResp.Items, i => i.AppId == 365670);

        // 2. Software only (994)
        var softReq = new SearchRequest
        {
            Pool = "all",
            AppTypes = "994"
        };
        var softResp = await _pipeline.ExecuteAsync(softReq);
        Assert.Contains(softResp.Items, i => i.AppId == 365670);
        Assert.DoesNotContain(softResp.Items, i => i.AppId == 400);

        // 3. All (998,994)
        var allReq = new SearchRequest
        {
            Pool = "all",
            AppTypes = "998,994"
        };
        var allResp = await _pipeline.ExecuteAsync(allReq);
        Assert.Contains(allResp.Items, i => i.AppId == 400);
        Assert.Contains(allResp.Items, i => i.AppId == 365670);
    }

    [Fact]
    public async Task ExecuteAsync_PopularNewWithNoSort_PassesEmptySortToSteamAndFiltersLegacy()
    {
        var req = new SearchRequest
        {
            Pool = "popularnew",
            SortBy = string.Empty,
            Descending = true
        };

        var resp = await _pipeline.ExecuteAsync(req);

        // 1. Must NOT send Released_DESC or any sort to Steam, preserving Steam's native popularity ranking
        Assert.NotNull(_fakeSteam.LastQuery);
        Assert.Equal(string.Empty, _fakeSteam.LastQuery!.SortBy);
        Assert.Equal("popularnew", _fakeSteam.LastQuery.StoreList);

        // 2. Must filter out 2017 legacy games like Brawlhalla
        Assert.DoesNotContain(resp.Items, i => i.AppId == 291550);
        Assert.All(resp.Items, i => Assert.NotEqual("Brawlhalla", i.Name));
    }

    [Fact]
    public async Task ExecuteAsync_SoftwareWithPreExistingLocalCache_QueriesSteamAndDoesNotGetStuck()
    {
        // Pre-populate local SQLite cache with 2 software items
        await _localRepo.UpsertAppsAsync([
            new CatalogAppItem { AppId = 1, Name = "Local Tool 1", AppType = "software" },
            new CatalogAppItem { AppId = 2, Name = "Local Tool 2", AppType = "software" }
        ], CancellationToken.None);

        // Steam has 5 software items
        _fakeSteam.StubResults.Clear();
        _fakeSteam.StubResults.AddRange([
            new SearchResult { AppId = 10, Name = "Wallpaper Engine", AppType = "Software", ReleaseDateText = "1 Jan, 2026" },
            new SearchResult { AppId = 20, Name = "Lossless Scaling", AppType = "Software", ReleaseDateText = "2 Jan, 2026" },
            new SearchResult { AppId = 30, Name = "Substance 3D Painter", AppType = "Software", ReleaseDateText = "3 Jan, 2026" },
            new SearchResult { AppId = 40, Name = "Aseprite", AppType = "Software", ReleaseDateText = "4 Jan, 2026" },
            new SearchResult { AppId = 50, Name = "RPG Maker MZ", AppType = "Software", ReleaseDateText = "5 Jan, 2026" }
        ]);

        var req = new SearchRequest
        {
            Pool = "all",
            AppTypes = "994",
            RawQuery = ""
        };

        var resp = await _pipeline.ExecuteAsync(req);

        // The pipeline must query Steam and return all 5 items, rather than getting stuck at the 2 local items
        Assert.NotNull(_fakeSteam.LastQuery);
        Assert.Equal("994", _fakeSteam.LastQuery!.AppTypes);
        Assert.Null(_fakeSteam.LastQuery.StoreList);
        Assert.Equal(5, resp.Items.Count);
        Assert.Contains(resp.Items, i => i.AppId == 10);
        Assert.Contains(resp.Items, i => i.AppId == 50);
    }

    [Fact]
    public async Task ExecuteAsync_ComingSoonPool_SortOptionsDisabled_EnforcesNoParticularOrder()
    {
        _fakeSteam.StubResults.Clear();
        _fakeSteam.StubResults.Add(new SearchResult { AppId = 12345, Name = "Upcoming Game", ReleaseDateText = "Coming soon" });

        var req = new SearchRequest
        {
            Pool = "comingsoon",
            SortBy = "reviews", // User reviews was selected previously
            Descending = true,
            RawQuery = ""
        };

        var resp = await _pipeline.ExecuteAsync(req);

        // Sort must be forced to empty for comingsoon pool
        Assert.NotNull(_fakeSteam.LastQuery);
        Assert.Equal("comingsoon", _fakeSteam.LastQuery!.StoreList);
        Assert.True(string.IsNullOrEmpty(_fakeSteam.LastQuery.SortBy), "Expected empty sort_by for comingsoon pool");
        Assert.NotEmpty(resp.Items);
    }

    private sealed class FakeMetadataProvider : IMetadataProvider
    {
        public Dictionary<uint, GameMetadata> MetaDatabase { get; } = new();

        public Task<GameMetadata?> GetMetadataAsync(uint appId, CancellationToken ct = default)
        {
            MetaDatabase.TryGetValue(appId, out var meta);
            return Task.FromResult(meta);
        }

        public Task<IReadOnlyList<DlcInfo>> GetDlcListAsync(uint appId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<DlcInfo>>([]);

        public Task EnrichSearchResultAsync(SearchResult result, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<string>> GetStoreTagsAsync(uint appId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<string>>([]);

        public Task<IReadOnlyList<SteamStoreSearchItem>> SearchStoreAsync(string query, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<SteamStoreSearchItem>>([]);
    }

    private sealed class FakeSteamService : ISteamCatalogSearchService
    {
        public List<SearchResult> StubResults { get; } = [];
        public SteamSearchQuery? LastQuery { get; private set; }

        public Task<SteamSearchPage> SearchAsync(SteamSearchQuery query, CancellationToken ct = default)
        {
            LastQuery = query;
            var items = StubResults.ToList();
            if (!string.IsNullOrWhiteSpace(query.Term))
            {
                items = items.Where(i => i.Name.Contains(query.Term, StringComparison.OrdinalIgnoreCase)).ToList();
            }
            if (!string.IsNullOrWhiteSpace(query.AppTypes))
            {
                var types = query.AppTypes.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                var hasGames = types.Contains("998");
                var hasSoftware = types.Contains("994");
                items = items.Where(i =>
                {
                    var isSoft = string.Equals(i.AppType, "software", StringComparison.OrdinalIgnoreCase)
                                 || string.Equals(i.AppType, "application", StringComparison.OrdinalIgnoreCase);
                    if (hasGames && !hasSoftware && isSoft) return false;
                    if (hasSoftware && !hasGames && !isSoft) return false;
                    return true;
                }).ToList();
            }
            return Task.FromResult(new SteamSearchPage(items, items.Count, query.Start));
        }

        public Task<int?> GetMatchCountAsync(SteamSearchQuery query, CancellationToken ct = default) =>
            Task.FromResult<int?>(StubResults.Count);

        public Task<IReadOnlyList<SteamStoreEvent>> GetStoreEventsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<SteamStoreEvent>>([]);

        public Task<IReadOnlyList<SteamTitleSuggestion>> SuggestTitlesAsync(string term, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<SteamTitleSuggestion>>([]);
    }
}
