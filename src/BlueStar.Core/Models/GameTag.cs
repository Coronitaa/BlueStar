namespace BlueStar.Core.Models;

/// <summary>
/// Categorizes the visual styling and semantic meaning of a game tag badge.
/// </summary>
public enum TagType
{
    Origin,
    Engine,
    Status,
    AppType,
    Platform,
    DlcCount,
    Drm,
    Nsfw,
    UpdateAvailable,
    Genre,
    Custom
}

/// <summary>
/// Represents a structured badge tag to display in explore or library views.
/// </summary>
public sealed record GameTag(
    string Text,
    TagType Type,
    string? Tooltip = null
);
