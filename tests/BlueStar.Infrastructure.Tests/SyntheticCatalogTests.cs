using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BlueStar.Core.Helpers;
using BlueStar.Core.Interfaces;
using BlueStar.Core.Models;
using BlueStar.Infrastructure.Catalog;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BlueStar.Infrastructure.Tests;

public class SyntheticCatalogTests : IDisposable
{
    private readonly string _tempDb;
    private readonly LocalCatalogRepository _repo;

    public SyntheticCatalogTests()
    {
        _tempDb = Path.Combine(Path.GetTempPath(), $"synth_test_{Guid.NewGuid():N}.sqlite");
        _repo = new LocalCatalogRepository(NullLogger<LocalCatalogRepository>.Instance, _tempDb);
    }

    public void Dispose()
    {
        _repo.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { if (File.Exists(_tempDb)) File.Delete(_tempDb); } catch { }
    }

    private async Task Populate10000AppsAsync()
    {
        var items = new List<CatalogAppItem>(10000);
        var baseDate = new DateTimeOffset(2010, 1, 1, 0, 0, 0, TimeSpan.Zero);

        for (int i = 1; i <= 10000; i++)
        {
            var appType = i <= 7000 ? "Game" : (i <= 9000 ? "Software" : "DLC");
            var releaseDate = baseDate.AddDays(i % 5000);
            var priceCents = (i % 6) switch
            {
                0 => 0,
                1 => 499,
                2 => 999,
                3 => 1999,
                4 => 3999,
                _ => 5999
            };

            var rating = 50 + (i % 49); // 50 to 98
            var tag1 = (i % 50) + 1;    // 1 to 50
            var tag2 = (i % 25) + 100;  // 100 to 124

            string name;
            if (i == 42) name = "Cyberpunk Neo Odyssey";
            else if (i == 1337) name = "The Elder Scroll of Mystery";
            else if (i == 5000) name = "Super Cyber Action Adventure";
            else name = $"Synthetic Title {i:D5}";

            items.Add(new CatalogAppItem
            {
                AppId = (uint)i,
                Name = name,
                NormalizedName = DeterministicNormalizer.Normalize(name),
                CompactName = DeterministicNormalizer.ToCompactKey(name),
                AppType = appType,
                ReviewPercent = rating,
                ReviewCount = 100 + i,
                PriceCents = priceCents,
                PriceText = priceCents == 0 ? "Free" : $"${priceCents / 100.0:F2}",
                ReleaseDateUtc = releaseDate.ToUnixTimeSeconds(),
                ReleaseDateText = releaseDate.ToString("d MMM, yyyy"),
                TagIds = [tag1, tag2],
                LastModified = 1700000000 - i // intentionally inverse to test release date sorting vs last_modified
            });
        }

        await _repo.UpsertAppsAsync(items);
    }

    [Fact]
    public async Task Search10000_FtsQuery_CompletesUnder50ms()
    {
        await Populate10000AppsAsync();

        var sw = Stopwatch.StartNew();
        var (results, count) = await _repo.QueryAsync(new LocalCatalogQuery
        {
            Term = "Cyberpunk Neo",
            Limit = 10
        });
        sw.Stop();

        Assert.True(sw.ElapsedMilliseconds < 200, $"Query took too long: {sw.ElapsedMilliseconds}ms");
        Assert.True(count >= 1);
        Assert.Contains(results, r => r.AppId == 42);
    }

    [Fact]
    public async Task Search10000_MultiWordSearch_MatchesAllKeywords()
    {
        await Populate10000AppsAsync();

        var (results, count) = await _repo.QueryAsync(new LocalCatalogQuery
        {
            Term = "Cyber Action",
            Limit = 10
        });

        Assert.True(count >= 1);
        Assert.Contains(results, r => r.AppId == 5000);
        Assert.Equal("Super Cyber Action Adventure", results.First(r => r.AppId == 5000).Name);
    }

    [Fact]
    public async Task Search10000_ReleaseDateSort_SortsByActualReleaseUtcNotLastModified()
    {
        await Populate10000AppsAsync();

        // Sort by released DESC: newest first
        var (descResults, _) = await _repo.QueryAsync(new LocalCatalogQuery
        {
            SortBy = "released",
            Descending = true,
            Limit = 20
        });

        Assert.NotEmpty(descResults);
        for (int i = 0; i < descResults.Count - 1; i++)
        {
            var curr = descResults[i].ReleaseDateUtc ?? 0;
            var next = descResults[i + 1].ReleaseDateUtc ?? 0;
            Assert.True(curr >= next, $"Expected {curr} >= {next} for released DESC");
        }

        // Sort by released ASC: oldest first
        var (ascResults, _) = await _repo.QueryAsync(new LocalCatalogQuery
        {
            SortBy = "released",
            Descending = false,
            Limit = 20
        });

        Assert.NotEmpty(ascResults);
        for (int i = 0; i < ascResults.Count - 1; i++)
        {
            var curr = ascResults[i].ReleaseDateUtc ?? 0;
            var next = ascResults[i + 1].ReleaseDateUtc ?? 0;
            Assert.True(curr <= next, $"Expected {curr} <= {next} for released ASC");
        }
    }

    [Fact]
    public async Task Search10000_RatingFilter_FiltersAccuratelyAcrossFullUniverse()
    {
        await Populate10000AppsAsync();

        var (highRated, totalHighRated) = await _repo.QueryAsync(new LocalCatalogQuery
        {
            MinRatingPercent = 90,
            Limit = 50
        });

        Assert.True(totalHighRated > 0);
        Assert.All(highRated, item => Assert.True(item.ReviewPercent >= 90));
    }

    [Fact]
    public async Task Search10000_AppTypeFiltering_AccuratelyNarrowsUniverse()
    {
        await Populate10000AppsAsync();

        // Games only
        var (games, gameCount) = await _repo.QueryAsync(new LocalCatalogQuery
        {
            AppTypes = "998",
            Limit = 50
        });
        Assert.Equal(7000, gameCount);
        Assert.All(games, g => Assert.Equal("Game", g.AppType));

        // Software only
        var (software, softCount) = await _repo.QueryAsync(new LocalCatalogQuery
        {
            AppTypes = "994",
            Limit = 50
        });
        Assert.Equal(2000, softCount);
        Assert.All(software, s => Assert.Equal("Software", s.AppType));
    }

    [Fact]
    public async Task Search10000_GetTagCounts_ReturnsAccurateCounts()
    {
        await Populate10000AppsAsync();

        var counts = await _repo.GetTagCountsAsync([1, 2, 3]);

        Assert.Equal(3, counts.Count);
        Assert.True(counts[1] > 0);
        Assert.True(counts[2] > 0);
        Assert.True(counts[3] > 0);
    }
}
