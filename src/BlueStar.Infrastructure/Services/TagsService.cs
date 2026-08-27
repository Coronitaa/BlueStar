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
            tags.Add(new GameTag("Windows", TagType.Platform, "Compatible with Windows"));
        if (result.HasLinux)
            tags.Add(new GameTag("Linux", TagType.Platform, "Compatible with Linux / SteamOS"));
        if (result.HasMac)
            tags.Add(new GameTag("macOS", TagType.Platform, "Compatible with macOS"));

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

        return tags.AsReadOnly();
    }

    /// <inheritdoc />
    public IReadOnlyList<GameTag> GetInstanceTags(GameInstance instance, bool hasUpdateAvailable = false)
    {
        if (instance is null) return [];

        var tags = new List<GameTag>();

        // 1. Instance Origin Badge (Steam Game or Imported)
        if (instance.Origin == InstanceOrigin.Steam)
        {
            tags.Add(new GameTag("Steam Game", TagType.Origin, "Imported from local Steam installation"));
        }
        else if (instance.Origin == InstanceOrigin.ImportedFolder && !instance.IsDepotBoxAssociated)
        {
            tags.Add(new GameTag("Imported", TagType.Origin, "Imported game folder (not yet linked to DepotBox)"));
        }

        // 2. Dynamic Update Available (derived dynamically, not a static timestamp)
        if (hasUpdateAvailable || instance.HasUpdateAvailable)
        {
            tags.Add(new GameTag("Update Available", TagType.UpdateAvailable, instance.UpdateDescription ?? "A newer version is available on DepotBox"));
        }

        // 3. Status Badge
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
        tags.Add(new GameTag("Windows", TagType.Platform));

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
