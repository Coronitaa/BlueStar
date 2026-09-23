using BlueStar.Core.Helpers;
using Xunit;

namespace BlueStar.Core.Tests;

public class RatingEngineTests
{
    [Fact]
    public void CalculateWilsonScore_ZeroReviews_ReturnsZero()
    {
        var score = RatingEngine.CalculateWilsonScore(0, 0);
        Assert.Equal(0.0, score);
    }

    [Fact]
    public void CalculateWilsonScore_SingleReviewAt100Percent_DoesNotBeatThousandsOfReviewsAt95Percent()
    {
        // 1 review: 1 pos, 0 neg (100% positive)
        var singleReviewScore = RatingEngine.CalculateWilsonScore(1, 0);

        // 10,000 reviews: 9,500 pos, 500 neg (95% positive)
        var largeGameScore = RatingEngine.CalculateWilsonScore(9500, 500);

        // A game with 10k reviews and 95% satisfaction must rank higher than a game with 1 review and 100%!
        Assert.True(largeGameScore > singleReviewScore, $"Large game ({largeGameScore}) should beat 1-review game ({singleReviewScore})");
        Assert.True(singleReviewScore < 0.3, $"Single positive review lower bound confidence should be low, was {singleReviewScore}");
    }

    [Theory]
    [InlineData(98, 1000, "Overwhelmingly Positive")]
    [InlineData(98, 10, "Positive")]
    [InlineData(85, 100, "Very Positive")]
    [InlineData(75, 50, "Mostly Positive")]
    [InlineData(50, 100, "Mixed")]
    [InlineData(30, 100, "Mostly Negative")]
    [InlineData(10, 1000, "Overwhelmingly Negative")]
    [InlineData(0, 0, "No user reviews")]
    public void GetReviewSummary_MapsToExpectedSteamTier(int percent, int count, string expectedSummary)
    {
        var summary = RatingEngine.GetReviewSummary(percent, count);
        Assert.Equal(expectedSummary, summary);
    }
}
