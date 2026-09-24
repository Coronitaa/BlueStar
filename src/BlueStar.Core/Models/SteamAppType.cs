using System;

namespace BlueStar.Core.Models;

/// <summary>
/// Taxonomy of Steam application types.
/// Explicitly separates games from DLCs, demos, tools, software, and media.
/// </summary>
public enum SteamAppType
{
    Unknown = 0,
    Game = 1,
    DLC = 2,
    Software = 3,
    Demo = 4,
    Video = 5,
    Tool = 6,
    Music = 7
}

public static class SteamAppTaxonomy
{
    public static string ToTaxonomyString(SteamAppType type) => type switch
    {
        SteamAppType.Game => "game",
        SteamAppType.DLC => "dlc",
        SteamAppType.Software => "software",
        SteamAppType.Demo => "demo",
        SteamAppType.Video => "video",
        SteamAppType.Tool => "tool",
        SteamAppType.Music => "music",
        _ => "unknown"
    };

    public static SteamAppType Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return SteamAppType.Unknown;
        var clean = value.Trim().ToLowerInvariant();
        return clean switch
        {
            "game" or "games" or "998" => SteamAppType.Game,
            "dlc" or "downloadablecontent" or "21" => SteamAppType.DLC,
            "software" or "application" or "utility" or "utilities" or "994" => SteamAppType.Software,
            "demo" or "demos" or "10" => SteamAppType.Demo,
            "video" or "movie" or "series" or "episode" or "film" => SteamAppType.Video,
            "tool" or "tools" => SteamAppType.Tool,
            "music" or "soundtrack" => SteamAppType.Music,
            _ => SteamAppType.Unknown
        };
    }
}
