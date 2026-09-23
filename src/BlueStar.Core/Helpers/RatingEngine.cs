using System;

namespace BlueStar.Core.Helpers;

/// <summary>
/// Provides statistical rating calculations including Wilson Score lower bound confidence intervals
/// and canonical Steam review classification.
/// </summary>
public static class RatingEngine
{
    /// <summary>
    /// Computes the Wilson score lower bound of positive review proportion at a 95% confidence interval (z = 1.96).
    /// Prevents games with 1 review (100%) from outranking games with 100,000 reviews (98%).
    /// </summary>
    /// <param name="positiveReviews">Count of positive reviews.</param>
    /// <param name="negativeReviews">Count of negative reviews.</param>
    /// <returns>A confidence-adjusted score between 0.0 and 1.0.</returns>
    public static double CalculateWilsonScore(int positiveReviews, int negativeReviews)
    {
        var total = positiveReviews + negativeReviews;
        if (total <= 0 || positiveReviews <= 0)
        {
            return 0.0;
        }

        const double z = 1.96; // 95% confidence
        var phat = (double)positiveReviews / total;
        var z2 = z * z;
        var denominator = 1.0 + z2 / total;
        var numerator = phat + z2 / (2.0 * total) - z * Math.Sqrt((phat * (1.0 - phat) + z2 / (4.0 * total)) / total);

        return Math.Max(0.0, numerator / denominator);
    }

    /// <summary>
    /// Computes an estimated Wilson score when only the positive percentage (0-100) and approximate review count are known.
    /// </summary>
    public static double EstimateWilsonScore(int reviewPercent, int? reviewCount)
    {
        if (reviewPercent <= 0) return 0.0;
        var count = reviewCount ?? 1;
        var positive = (int)Math.Round(count * (reviewPercent / 100.0));
        var negative = Math.Max(0, count - positive);
        return CalculateWilsonScore(positive, negative);
    }

    /// <summary>
    /// Classifies positive percentage and review volume into Steam's standard summary tier.
    /// </summary>
    public static string GetReviewSummary(int reviewPercent, int reviewCount)
    {
        if (reviewCount < 1) return "No user reviews";

        return reviewPercent switch
        {
            >= 95 when reviewCount >= 500 => "Overwhelmingly Positive",
            >= 80 when reviewCount >= 50 => "Very Positive",
            >= 80 => "Positive",
            >= 70 => "Mostly Positive",
            >= 40 and < 70 => "Mixed",
            >= 20 and < 40 => "Mostly Negative",
            < 20 when reviewCount >= 500 => "Overwhelmingly Negative",
            _ => "Very Negative"
        };
    }
}
