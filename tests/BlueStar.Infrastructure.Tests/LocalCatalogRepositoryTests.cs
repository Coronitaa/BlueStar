using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using BlueStar.Core.Helpers;
using BlueStar.Core.Interfaces;
using BlueStar.Core.Models;
using BlueStar.Infrastructure.Catalog;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BlueStar.Infrastructure.Tests;

public class LocalCatalogRepositoryTests : IDisposable
{
    private readonly string _tempDb;
    private readonly LocalCatalogRepository _repo;

    public LocalCatalogRepositoryTests()
    {
        _tempDb = Path.Combine(Path.GetTempPath(), $"test_catalog_{Guid.NewGuid():N}.sqlite");
        _repo = new LocalCatalogRepository(NullLogger<LocalCatalogRepository>.Instance, _tempDb);
    }

    public void Dispose()
    {
        _repo.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { if (File.Exists(_tempDb)) File.Delete(_tempDb); } catch { }
    }

    [Theory]
    [InlineData("Counter-Strike: Global Offensive", "counter strike global offensive", "counterstrikeglobaloffensive")]
    [InlineData("Cyberpunk 2077", "cyberpunk 2077", "cyberpunk2077")]
    [InlineData("Cyber-Punk", "cyber punk", "cyberpunk")]
    [InlineData("Portal 2", "portal 2", "portal2")]
    [InlineData("The Witcher® 3: Wild Hunt", "the witcher 3 wild hunt", "thewitcher3wildhunt")]
    [InlineData("Pokémon Trading Card Game", "pokemon trading card game", "pokemontradingcardgame")]
    [InlineData("R.E.P.O.", "repo", "repo")]
    [InlineData("S.T.A.L.K.E.R.: Shadow of Chernobyl", "stalker shadow of chernobyl", "stalkershadowofchernobyl")]
    [InlineData("F.E.A.R. 2: Project Origin", "fear 2 project origin", "fear2projectorigin")]
    public void DeterministicNormalizer_TransformsConsistently(string input, string expectedNorm, string expectedCompact)
    {
        var norm = DeterministicNormalizer.Normalize(input);
        var compact = DeterministicNormalizer.ToCompactKey(input);

        Assert.Equal(expectedNorm, norm);
        Assert.Equal(expectedCompact, compact);
    }

    [Fact]
    public async Task SearchCandidates_ResolvesExactAppId_Name_Prefix_And_Aliases()
    {
        await _repo.InitializeAsync();

        var apps = new[]
        {
            new CatalogAppItem
            {
                AppId = 570,
                Name = "Dota 2",
                NormalizedName = DeterministicNormalizer.Normalize("Dota 2"),
                CompactName = DeterministicNormalizer.ToCompactKey("Dota 2"),
                ReviewPercent = 82,
                ReviewCount = 2000000,
                PositiveReviews = 1640000,
                NegativeReviews = 360000,
                LastModified = 1700000000
            },
            new CatalogAppItem
            {
                AppId = 730,
                Name = "Counter-Strike 2",
                NormalizedName = DeterministicNormalizer.Normalize("Counter-Strike 2"),
                CompactName = DeterministicNormalizer.ToCompactKey("Counter-Strike 2"),
                ReviewPercent = 87,
                ReviewCount = 7500000,
                PositiveReviews = 6525000,
                NegativeReviews = 975000,
                LastModified = 1710000000
            },
            new CatalogAppItem
            {
                AppId = 620,
                Name = "Portal 2",
                NormalizedName = DeterministicNormalizer.Normalize("Portal 2"),
                CompactName = DeterministicNormalizer.ToCompactKey("Portal 2"),
                ReviewPercent = 98,
                ReviewCount = 350000,
                PositiveReviews = 343000,
                NegativeReviews = 7000,
                LastModified = 1650000000
            },
            new CatalogAppItem
            {
                AppId = 1091500,
                Name = "Cyberpunk 2077",
                NormalizedName = DeterministicNormalizer.Normalize("Cyberpunk 2077"),
                CompactName = DeterministicNormalizer.ToCompactKey("Cyberpunk 2077"),
                ReviewPercent = 83,
                ReviewCount = 650000,
                PositiveReviews = 539500,
                NegativeReviews = 110500,
                LastModified = 1690000000
            },
            new CatalogAppItem
            {
                AppId = 292030,
                Name = "The Witcher 3: Wild Hunt",
                NormalizedName = DeterministicNormalizer.Normalize("The Witcher 3: Wild Hunt"),
                CompactName = DeterministicNormalizer.ToCompactKey("The Witcher 3: Wild Hunt"),
                ReviewPercent = 96,
                ReviewCount = 700000,
                PositiveReviews = 672000,
                NegativeReviews = 28000,
                LastModified = 1680000000
            }
        };

        await _repo.UpsertAppsAsync(apps);

        // 1. Direct AppID lookup
        var dota = await _repo.SearchCandidatesAsync("570");
        Assert.Single(dota);
        Assert.Equal(570u, dota[0].AppId);

        // 2. Prefixed AppId lookup
        var portalById = await _repo.SearchCandidatesAsync("appid:620");
        Assert.Single(portalById);
        Assert.Equal(620u, portalById[0].AppId);

        // 3. Exact name lookup
        var portalByName = await _repo.SearchCandidatesAsync("Portal 2");
        Assert.Contains(portalByName, a => a.AppId == 620u);

        // 4. Prefix match
        var portalPrefix = await _repo.SearchCandidatesAsync("portal");
        Assert.Contains(portalPrefix, a => a.AppId == 620u);

        // 5. Hyphenated / space variations
        var csSpace = await _repo.SearchCandidatesAsync("counter strike");
        Assert.Contains(csSpace, a => a.AppId == 730u);

        var csHyphen = await _repo.SearchCandidatesAsync("counter-strike");
        Assert.Contains(csHyphen, a => a.AppId == 730u);

        var cyberSpace = await _repo.SearchCandidatesAsync("cyber punk");
        Assert.Contains(cyberSpace, a => a.AppId == 1091500u);

        var cyberTogether = await _repo.SearchCandidatesAsync("cyberpunk");
        Assert.Contains(cyberTogether, a => a.AppId == 1091500u);

        var witcher = await _repo.SearchCandidatesAsync("the witcher");
        Assert.Contains(witcher, a => a.AppId == 292030u);
    }

    [Fact]
    public async Task QueryAsync_FiltersRatings_And_SortsDeterministically()
    {
        await _repo.InitializeAsync();

        var apps = new[]
        {
            new CatalogAppItem { AppId = 1, Name = "Alpha Game", NormalizedName = "alpha game", ReviewPercent = 96, LastModified = 100 },
            new CatalogAppItem { AppId = 2, Name = "Beta Game", NormalizedName = "beta game", ReviewPercent = 75, LastModified = 200 },
            new CatalogAppItem { AppId = 3, Name = "Gamma Game", NormalizedName = "gamma game", ReviewPercent = 98, LastModified = 300 }
        };

        await _repo.UpsertAppsAsync(apps);

        // Filter 95%+ ratings
        var (highRated, count) = await _repo.QueryAsync(new LocalCatalogQuery
        {
            MinRatingPercent = 95,
            SortBy = "Reviews",
            Descending = true
        });

        Assert.Equal(2, count);
        Assert.Equal(2, highRated.Count);
        Assert.Equal(98, highRated[0].ReviewPercent);
        Assert.Equal(96, highRated[1].ReviewPercent);

        // Sort by Name ASC
        var (sortedByName, _) = await _repo.QueryAsync(new LocalCatalogQuery
        {
            SortBy = "Name",
            Descending = false
        });

        Assert.Equal("Alpha Game", sortedByName[0].Name);
        Assert.Equal("Beta Game", sortedByName[1].Name);
        Assert.Equal("Gamma Game", sortedByName[2].Name);
    }

    [Fact]
    public async Task InitializeAsync_PreExistingOldSchemaWithoutReleaseDateUtc_MigratesSuccessfullyWithoutThrowing()
    {
        var legacyDbPath = Path.Combine(Path.GetTempPath(), $"legacy_catalog_{Guid.NewGuid():N}.sqlite");

        try
        {
            // 1. Manually create database with OLD schema (no release_date_utc, no price_cents)
            using (var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={legacyDbPath};"))
            {
                await conn.OpenAsync();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                    CREATE TABLE apps (
                        app_id INTEGER PRIMARY KEY,
                        name TEXT NOT NULL,
                        normalized_name TEXT NOT NULL,
                        compact_name TEXT NOT NULL,
                        app_type TEXT NOT NULL,
                        last_modified INTEGER NOT NULL,
                        price_change_number INTEGER NOT NULL DEFAULT 0,
                        review_percent INTEGER,
                        review_count INTEGER,
                        positive_reviews INTEGER,
                        negative_reviews INTEGER,
                        rating_updated_at INTEGER,
                        header_image_url TEXT,
                        price_text TEXT,
                        discount_percent INTEGER NOT NULL DEFAULT 0,
                        release_date_text TEXT,
                        has_windows INTEGER NOT NULL DEFAULT 1,
                        has_mac INTEGER NOT NULL DEFAULT 0,
                        has_linux INTEGER NOT NULL DEFAULT 0,
                        is_nsfw INTEGER NOT NULL DEFAULT 0,
                        has_drm INTEGER NOT NULL DEFAULT 0,
                        has_external_launcher INTEGER NOT NULL DEFAULT 0,
                        tag_ids TEXT
                    );
                    INSERT INTO apps (app_id, name, normalized_name, compact_name, app_type, last_modified)
                    VALUES (730, 'Counter-Strike 2', 'counter strike 2', 'counterstrike2', 'Game', 1700000000);
                    """;
                await cmd.ExecuteNonQueryAsync();
            }

            // 2. Initialize LocalCatalogRepository on top of the pre-existing legacy database
            using var repo = new LocalCatalogRepository(NullLogger<LocalCatalogRepository>.Instance, legacyDbPath);

            // Must NOT throw 'SQLite Error 1: no such column: release_date_utc'
            var exception = await Record.ExceptionAsync(() => repo.InitializeAsync());
            Assert.Null(exception);

            // 3. Completeness and query work seamlessly
            var completeness = await repo.GetCompletenessAsync();
            Assert.Equal(1, completeness.TotalIndexedApps);

            var (results, count) = await repo.QueryAsync(new LocalCatalogQuery { Term = "Counter-Strike" });
            Assert.Equal(1, count);
            Assert.Equal(730u, results[0].AppId);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { if (File.Exists(legacyDbPath)) File.Delete(legacyDbPath); } catch { }
        }
    }

    [Fact]
    public async Task Test_QueryAsync_PriceSort_TiesResolvedByReleaseDateAndAppId()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"price_sort_test_{Guid.NewGuid():N}.sqlite");
        try
        {
            using var repo = new LocalCatalogRepository(NullLogger<LocalCatalogRepository>.Instance, dbPath);
            await repo.InitializeAsync();

            var app1 = new CatalogAppItem
            {
                AppId = 300,
                Name = "Game C",
                PriceCents = 999,
                ReleaseDateUtc = 1600000000
            };
            var app2 = new CatalogAppItem
            {
                AppId = 100,
                Name = "Game A",
                PriceCents = 999,
                ReleaseDateUtc = 1700000000
            };
            var app3 = new CatalogAppItem
            {
                AppId = 200,
                Name = "Game B",
                PriceCents = 999,
                ReleaseDateUtc = 1700000000
            };

            await repo.UpsertAppsAsync([app1, app2, app3]);

            var (items, count) = await repo.QueryAsync(new LocalCatalogQuery
            {
                SortBy = "price",
                Descending = false,
                Limit = 10
            });

            Assert.Equal(3, count);
            Assert.Equal(100u, items[0].AppId);
            Assert.Equal(200u, items[1].AppId);
            Assert.Equal(300u, items[2].AppId);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { if (File.Exists(dbPath)) File.Delete(dbPath); } catch { }
        }
    }

    [Fact]
    public async Task Test_QueryAsync_ReviewsSort_Caps100PercentAt99AndTieBreaksByCount()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"reviews_sort_test_{Guid.NewGuid():N}.sqlite");
        try
        {
            using var repo = new LocalCatalogRepository(NullLogger<LocalCatalogRepository>.Instance, dbPath);
            await repo.InitializeAsync();

            var appA = new CatalogAppItem { AppId = 1, Name = "Obscure Game (100% 2 reviews)", ReviewPercent = 100, ReviewCount = 2 };
            var appB = new CatalogAppItem { AppId = 2, Name = "Popular Gem (99% 50000 reviews)", ReviewPercent = 99, ReviewCount = 50000 };
            var appC = new CatalogAppItem { AppId = 3, Name = "Indie Hit (99% 1000 reviews)", ReviewPercent = 99, ReviewCount = 1000 };
            var appD = new CatalogAppItem { AppId = 4, Name = "Niche (100% 500 reviews)", ReviewPercent = 100, ReviewCount = 500 };
            var appE = new CatalogAppItem { AppId = 5, Name = "Classic (98% 200000 reviews)", ReviewPercent = 98, ReviewCount = 200000 };

            await repo.UpsertAppsAsync([appA, appB, appC, appD, appE]);

            var (items, count) = await repo.QueryAsync(new LocalCatalogQuery
            {
                SortBy = "reviews",
                Descending = true,
                Limit = 10
            });

            Assert.Equal(5, count);
            // 99% with 50,000 reviews must beat 100% with 500 reviews and 100% with 2 reviews because 100% is capped at 99%!
            Assert.Equal(2u, items[0].AppId); // AppB: 99%, 50000
            Assert.Equal(3u, items[1].AppId); // AppC: 99%, 1000
            Assert.Equal(4u, items[2].AppId); // AppD: 100% -> 99%, 500
            Assert.Equal(1u, items[3].AppId); // AppA: 100% -> 99%, 2
            Assert.Equal(5u, items[4].AppId); // AppE: 98%
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { if (File.Exists(dbPath)) File.Delete(dbPath); } catch { }
        }
    }

    [Fact]
    public async Task Test_QueryAsync_SearchSymbolsAndAcronyms_REPO_RanksByPopularity()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"repo_symbol_test_{Guid.NewGuid():N}.sqlite");
        try
        {
            using var repo = new LocalCatalogRepository(NullLogger<LocalCatalogRepository>.Instance, dbPath);
            await repo.InitializeAsync();

            var game1 = new CatalogAppItem
            {
                AppId = 100,
                Name = "Repossession",
                NormalizedName = DeterministicNormalizer.Normalize("Repossession"),
                CompactName = DeterministicNormalizer.ToCompactKey("Repossession"),
                ReviewPercent = null,
                ReviewCount = 0
            };
            var game2 = new CatalogAppItem
            {
                AppId = 200,
                Name = "R.E.P.O.",
                NormalizedName = DeterministicNormalizer.Normalize("R.E.P.O."),
                CompactName = DeterministicNormalizer.ToCompactKey("R.E.P.O."),
                ReviewPercent = 96,
                ReviewCount = 65000
            };
            var game3 = new CatalogAppItem
            {
                AppId = 300,
                Name = "REPO MAN",
                NormalizedName = DeterministicNormalizer.Normalize("REPO MAN"),
                CompactName = DeterministicNormalizer.ToCompactKey("REPO MAN"),
                ReviewPercent = 75,
                ReviewCount = 300
            };

            await repo.UpsertAppsAsync([game1, game2, game3]);

            // Search by "repo" without dots
            var (itemsRepo, countRepo) = await repo.QueryAsync(new LocalCatalogQuery
            {
                Term = "repo",
                Limit = 10
            });

            Assert.Equal(3, countRepo);
            // R.E.P.O. must rank #1 due to exact match and overwhelming popularity!
            Assert.Equal(200u, itemsRepo[0].AppId); // R.E.P.O.
            Assert.Equal(300u, itemsRepo[1].AppId); // REPO MAN
            Assert.Equal(100u, itemsRepo[2].AppId); // Repossession (0 reviews)

            // Search by "R.E.P.O." with dots
            var (itemsDotted, countDotted) = await repo.QueryAsync(new LocalCatalogQuery
            {
                Term = "R.E.P.O.",
                Limit = 10
            });

            Assert.True(countDotted > 0);
            Assert.Equal(200u, itemsDotted[0].AppId); // R.E.P.O.
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { if (File.Exists(dbPath)) File.Delete(dbPath); } catch { }
        }
    }
}


