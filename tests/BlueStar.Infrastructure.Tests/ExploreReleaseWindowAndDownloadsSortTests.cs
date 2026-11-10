using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using BlueStar.Core.Helpers;
using BlueStar.Core.Interfaces;
using BlueStar.Core.Models;
using BlueStar.Infrastructure.Catalog;
using BlueStar.Infrastructure.Emulators;
using BlueStar.Infrastructure.Search;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BlueStar.Infrastructure.Tests;

public sealed class ExploreReleaseWindowAndDownloadsSortTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 11, 9, 12, 0, 0, DateTimeKind.Utc);

    private readonly string _tempDb = Path.Combine(Path.GetTempPath(), $"test_release_window_{Guid.NewGuid():N}.sqlite");
    private readonly LocalCatalogRepository _repo;

    public ExploreReleaseWindowAndDownloadsSortTests()
    {
        _repo = new LocalCatalogRepository(NullLogger<LocalCatalogRepository>.Instance, _tempDb);
    }

    public void Dispose()
    {
        _repo.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { if (File.Exists(_tempDb)) File.Delete(_tempDb); } catch { }
    }

    [Theory]
    [InlineData("Nov 8, 2026", true)]
    [InlineData("Nov 9, 2026", true)]
    [InlineData("Nov 10, 2026", false)]
    [InlineData("12 Mar, 2019", true)]
    [InlineData("2026", false)]
    [InlineData("2019", true)]
    [InlineData("Q1 2026", false)]
    [InlineData("Q3 2025", true)]
    [InlineData("Nov 2026", false)]
    [InlineData("Coming soon", false)]
    [InlineData("To be announced", false)]
    public void IsReleased_TreatsOnlyPastDatesAsOut(string text, bool expected)
    {
        SearchPipeline.IsReleased(text, null, Now).Should().Be(expected);
    }

    [Fact]
    public void IsReleased_FallsBackToTimestampWhenThereIsNoText()
    {
        var past = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();
        var future = new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();

        SearchPipeline.IsReleased(null, past, Now).Should().BeTrue();
        SearchPipeline.IsReleased(null, future, Now).Should().BeFalse();
        SearchPipeline.IsReleased(null, null, Now).Should().BeFalse();
    }

    [Theory]
    [InlineData("comingsoon", "Released", false)]
    [InlineData("comingsoon", "", false)]
    [InlineData("all", "Released", true)]
    [InlineData(null, "Released", true)]
    [InlineData("popularnew", "Released", true)]
    [InlineData("all", "Reviews", null)]
    public void ReleaseWindowFor_ComingSoonIsUnreleasedAndReleaseSortIsReleased(string? pool, string sort, bool? expected)
    {
        SearchPipeline.ReleaseWindowFor(pool, sort).Should().Be(expected);
    }

    [Fact]
    public async Task Query_SortedByRelease_ExcludesUnreleasedGames_AndUpcomingOnlyKeepsThem()
    {
        await _repo.InitializeAsync();
        await _repo.UpsertAppsAsync(new[]
        {
            App(1, "Old Game", "Mar 12, 2019"),
            App(2, "Next Year Game", "Mar 12, 2099"),
            App(3, "Announced Game", "Coming soon")
        });

        var (released, _, _) = await _repo.QueryAsync(new LocalCatalogQuery { AppTypes = null, SortBy = "released", Released = true });
        var (upcoming, _, _) = await _repo.QueryAsync(new LocalCatalogQuery { AppTypes = null, SortBy = "released", Released = false });

        released.Select(a => a.AppId).Should().Equal(1u);
        upcoming.Select(a => a.AppId).Should().BeEquivalentTo(new[] { 2u, 3u });
    }

    [Fact]
    public async Task Query_SortedByDownloads_FollowsTheCommunityRanking()
    {
        await _repo.InitializeAsync();
        await _repo.UpsertAppsAsync(new[] { App(10, "A", "Jan 1, 2020"), App(20, "B", "Jan 1, 2020"), App(30, "C", "Jan 1, 2020") });

        var (items, total, _) = await _repo.QueryAsync(new LocalCatalogQuery
        {
            AppTypes = null,
            SortBy = SteamStoreFacets.SortDownloads,
            RestrictToAppIds = new uint[] { 30, 10, 20 }
        });

        total.Should().Be(3);
        items.Select(a => a.AppId).Should().Equal(30u, 10u, 20u);
    }

    [Fact]
    public void ApplyLocalSort_Downloads_PutsRankedGamesFirstInRankOrder()
    {
        var items = new[] { new SearchResult { AppId = 1 }, new SearchResult { AppId = 2 }, new SearchResult { AppId = 3 } };

        var sorted = SearchPipeline.ApplyLocalSort(items, SteamStoreFacets.SortDownloads, descending: true, downloadsRank: new uint[] { 3, 1 });

        sorted.Select(i => i.AppId).Should().Equal(3u, 1u, 2u);
    }

    [Fact]
    public void Downloads_IsNeverSentToSteam()
    {
        SteamStoreFacets.ComposeSort(SteamStoreFacets.SortDownloads, descending: true).Should().BeEmpty();
    }

    [Theory]
    [InlineData("refix_goldberg_offline", "ReFix LAN Offline")]
    [InlineData("refix_goldberg", "ReFix LAN + Online")]
    [InlineData("refix_valve", "ReFix Online Legacy (Steam)")]
    public void ReFixOptionNames_CoverTheThreeProducts(string optionId, string expected)
    {
        ReFixEmulator.GetOptionDisplayName(optionId).Should().Be(expected);
        ReFixEmulator.DescribeInstalledMode("ReFix LAN", optionId == "refix_valve" ? null : optionId).Should()
            .Be(optionId == "refix_valve" ? "ReFix LAN" : expected);
    }

    private static CatalogAppItem App(uint id, string name, string releaseText) => new()
    {
        AppId = id,
        Name = name,
        NormalizedName = DeterministicNormalizer.Normalize(name),
        CompactName = DeterministicNormalizer.ToCompactKey(name),
        AppType = "game",
        ReleaseDateText = releaseText
    };
}
