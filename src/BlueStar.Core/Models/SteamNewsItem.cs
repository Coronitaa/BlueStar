using System;

namespace BlueStar.Core.Models;

/// <summary>
/// A news or patch-note entry published by Steam for an app.
/// </summary>
public record SteamNewsItem
{
    /// <summary>Gets Steam's own identifier for the entry.</summary>
    public string Gid { get; init; } = string.Empty;

    /// <summary>Gets the entry title.</summary>
    public string Title { get; init; } = string.Empty;

    /// <summary>Gets the link to the full announcement.</summary>
    public string Url { get; init; } = string.Empty;

    /// <summary>Gets the body, with Steam's BBCode and HTML stripped down to readable text.</summary>
    public string Contents { get; init; } = string.Empty;

    /// <summary>Gets the human-readable feed name, e.g. "Community Announcements".</summary>
    public string FeedLabel { get; init; } = string.Empty;

    /// <summary>Gets the machine feed name, e.g. "steam_community_announcements".</summary>
    public string FeedName { get; init; } = string.Empty;

    /// <summary>Gets the author, when the feed provides one.</summary>
    public string? Author { get; init; }

    /// <summary>Gets when the entry was published.</summary>
    public DateTimeOffset PublishedAt { get; init; }

    /// <summary>
    /// Gets whether Steam tagged this entry as patch notes. Used to lead with the entries that
    /// actually describe what changed in a build.
    /// </summary>
    public bool IsPatchNote { get; init; }

    /// <summary>Gets the publication date formatted for display.</summary>
    public string FormattedDate => PublishedAt.LocalDateTime.ToString("d MMM yyyy");
}
