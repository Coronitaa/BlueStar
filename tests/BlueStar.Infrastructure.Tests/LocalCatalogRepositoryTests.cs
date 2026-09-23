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
}
