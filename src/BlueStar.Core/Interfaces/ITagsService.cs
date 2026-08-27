using System.Collections.Generic;
using BlueStar.Core.Models;

namespace BlueStar.Core.Interfaces;

/// <summary>
/// Unified single source of truth for generating metadata and status badge tags across Explore, Library cards, and Instance Detail views.
/// </summary>
public interface ITagsService
{
    /// <summary>
    /// Extracts structured badge tags for an Explore catalog search result (AppType, Platform, DLCs, DRM, NSFW, Genres).
    /// </summary>
    IReadOnlyList<GameTag> GetExploreTags(SearchResult result);

    /// <summary>
    /// Extracts structured badge tags for a Library instance card (Origin, Engine, Status, AppType, Platform, DLCs, DRM, NSFW, Update Available).
    /// Strictly excludes date tags from instance tags.
    /// </summary>
    IReadOnlyList<GameTag> GetInstanceTags(GameInstance instance, bool hasUpdateAvailable = false);

    /// <summary>
    /// Extracts structured badge tags for the Instance Detail header/hero section.
    /// </summary>
    IReadOnlyList<GameTag> GetInstanceDetailHeroTags(GameInstance instance, bool hasUpdateAvailable = false);
}
