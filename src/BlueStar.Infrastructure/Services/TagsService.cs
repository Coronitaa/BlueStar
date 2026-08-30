using System;
using System.Collections.Generic;
using BlueStar.Core.Interfaces;
using BlueStar.Core.Models;

namespace BlueStar.Infrastructure.Services;

/// <summary>
/// Centralized implementation of ITagsService providing a single source of truth for all tag calculations across BlueStar.
/// </summary>
public sealed class TagsService : ITagsService
{
    /// <inheritdoc />
    public IReadOnlyList<GameTag> GetExploreTags(SearchResult result)
    {
        if (result is null) return [];

        var tags = new List<GameTag>();

        // 1. App Type
        var appType = string.IsNullOrWhiteSpace(result.AppType) ? "Game" : result.AppType;
        tags.Add(new GameTag(appType, TagType.AppType));

        // 2. Platforms
        if (result.HasWindows)
            tags.Add(new GameTag("⊞ Windows", TagType.Platform, "Compatible with Windows"));
        if (result.HasLinux)
            tags.Add(new GameTag("🐧 Linux", TagType.Platform, "Compatible with Linux / SteamOS"));
        if (result.HasMac)
            tags.Add(new GameTag("🍎 macOS", TagType.Platform, "Compatible with macOS"));

        // 3. DLC Count
        if (result.DlcCount > 0)
        {
            tags.Add(new GameTag($"{result.DlcCount} DLCs", TagType.DlcCount, $"{result.DlcCount} downloadable content items available"));
        }

        // 4. DRM Notice
        if (result.HasDrm)
        {
            var tooltip = !string.IsNullOrWhiteSpace(result.DrmNotice) ? result.DrmNotice : "Incorporates 3rd-party DRM / Account";
            tags.Add(new GameTag("DRM", TagType.Drm, tooltip));
        }

        // 5. NSFW / Age Rating
        if (result.IsNsfw)
        {
            tags.Add(new GameTag("18+ Adults Only", TagType.Nsfw, "Mature content / Adult Only rating"));
        }

        // 6. Specific Tags / Emulators / Bypasses (e.g. BYPASS, ONLINE, REFIX)
        if (result.Tags != null && result.Tags.Count > 0)
        {
            foreach (var t in result.Tags)
            {
                var upper = t.ToUpperInvariant();
                var tagType = upper.Contains("BYPASS") ? TagType.Engine : TagType.Custom;
                tags.Add(new GameTag(upper, tagType, $"{upper} option available on DepotBox"));
            }
        }

        return tags.AsReadOnly();
    }

    /// <inheritdoc />
    public IReadOnlyList<GameTag> GetInstanceTags(GameInstance instance, bool hasUpdateAvailable = false)
    {
        if (instance is null) return [];

        var tags = new List<GameTag>();

        // Status Badge
        var statusText = instance.Status switch
        {
            InstanceStatus.Ready => "Ready",
            InstanceStatus.Running => "Running",
            InstanceStatus.Downloading => "Downloading",
            InstanceStatus.Updating => "Updating",
            InstanceStatus.Error => "Error",
            _ => "Not Installed"
        };
        tags.Add(new GameTag(statusText, TagType.Status));

        // 4. Engine Badge
        if (instance.Engine != null && instance.Engine.Type != EngineType.Generic)
        {
            tags.Add(new GameTag(instance.Engine.DisplayText, TagType.Engine));
        }

        // 5. DLCs Badge
        if (instance.Dlcs != null && instance.Dlcs.Count > 0)
        {
            tags.Add(new GameTag($"🎁 {instance.Dlcs.Count} DLCs", TagType.DlcCount, $"{instance.Dlcs.Count} DLCs registered"));
        }

        // 6. Platform
        tags.Add(new GameTag("⊞ Windows", TagType.Platform));

        // Note: Date tags (e.g. 'Nov 2024') are strictly excluded from instance tags per requirements.

        return tags.AsReadOnly();
    }

    /// <inheritdoc />
    public IReadOnlyList<GameTag> GetInstanceDetailHeroTags(GameInstance instance, bool hasUpdateAvailable = false)
    {
        // Reuses instance tags with complete metadata consistency
        return GetInstanceTags(instance, hasUpdateAvailable);
    }
}
