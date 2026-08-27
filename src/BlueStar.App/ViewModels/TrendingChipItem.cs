using System;
using System.Windows.Media;

namespace BlueStar.App.ViewModels;

/// <summary>
/// Model for a trending suggestion chip (globito) styled with ranked opacity from Top 1 (solid) to Top 5 (tenue).
/// </summary>
public record TrendingChipItem
{
    public required string Name { get; init; }
    public int Rank { get; init; }
    public string BackgroundHex { get; init; } = "#331B90FF";
    public string BorderHex { get; init; } = "#801B90FF";
    public string TextHex { get; init; } = "#FFFFFF";
    public string RankBadgeBackgroundHex { get; init; } = "#1B90FF";
    public string RankBadgeTextHex { get; init; } = "#FFFFFF";
    public double Opacity { get; init; } = 1.0;

    public Brush BackgroundBrush => GetFrozenBrush(BackgroundHex);
    public Brush BorderBrush => GetFrozenBrush(BorderHex);
    public Brush TextBrush => GetFrozenBrush(TextHex);
    public Brush RankBadgeBackgroundBrush => GetFrozenBrush(RankBadgeBackgroundHex);
    public Brush RankBadgeTextBrush => GetFrozenBrush(RankBadgeTextHex);

    private static Brush GetFrozenBrush(string hex)
    {
        try
        {
            var color = (Color)ColorConverter.ConvertFromString(hex);
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }
        catch
        {
            return Brushes.Transparent;
        }
    }

    /// <summary>
    /// Creates a ranked TrendingChipItem with decreasing saturation and opacity from Rank 1 (solid) to Rank 5 (tenue).
    /// </summary>
    public static TrendingChipItem Create(string name, int rank)
    {
        return rank switch
        {
            1 => new TrendingChipItem
            {
                Name = name,
                Rank = 1,
                BackgroundHex = "#331B90FF",
                BorderHex = "#801B90FF",
                TextHex = "#FFFFFF",
                RankBadgeBackgroundHex = "#1B90FF",
                RankBadgeTextHex = "#FFFFFF",
                Opacity = 1.0
            },
            2 => new TrendingChipItem
            {
                Name = name,
                Rank = 2,
                BackgroundHex = "#241B90FF",
                BorderHex = "#551B90FF",
                TextHex = "#E2E8F0",
                RankBadgeBackgroundHex = "#3B82F6",
                RankBadgeTextHex = "#FFFFFF",
                Opacity = 0.88
            },
            3 => new TrendingChipItem
            {
                Name = name,
                Rank = 3,
                BackgroundHex = "#181B90FF",
                BorderHex = "#381B90FF",
                TextHex = "#CBD5E1",
                RankBadgeBackgroundHex = "#2563EB",
                RankBadgeTextHex = "#E2E8F0",
                Opacity = 0.75
            },
            4 => new TrendingChipItem
            {
                Name = name,
                Rank = 4,
                BackgroundHex = "#101B90FF",
                BorderHex = "#261B90FF",
                TextHex = "#94A3B8",
                RankBadgeBackgroundHex = "#1E293B",
                RankBadgeTextHex = "#94A3B8",
                Opacity = 0.62
            },
            _ => new TrendingChipItem
            {
                Name = name,
                Rank = rank,
                BackgroundHex = "#0A1B90FF",
                BorderHex = "#1A1B90FF",
                TextHex = "#64748B",
                RankBadgeBackgroundHex = "#0F172A",
                RankBadgeTextHex = "#64748B",
                Opacity = 0.50
            }
        };
    }
}
