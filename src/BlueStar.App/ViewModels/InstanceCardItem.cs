using System;
using System.Collections.Generic;
using BlueStar.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;

namespace BlueStar.App.ViewModels;

/// <summary>
/// Display item representing an instance card in the Library view with dynamically calculated tags and state badges.
/// </summary>
public sealed class InstanceCardItem
{
    public GameInstance Instance { get; }
    public IReadOnlyList<GameTag> Tags { get; }

    public Guid Id => Instance.Id;
    public string Name => Instance.Name;
    public uint AppId => Instance.AppId;
    public string HeaderImageUrl => Instance.HeaderImageUrl;
    public InstanceStatus Status => Instance.Status;
    public EngineInfo? Engine => Instance.Engine;
    public string InstallPath => Instance.InstallPath;

    public bool IsDepotBoxBacked => Instance.Origin == InstanceOrigin.DepotBox || Instance.IsDepotBoxAssociated;
    public bool IsNotInstalled => Instance.Status == InstanceStatus.NotInstalled && Instance.Origin != InstanceOrigin.Steam;
    public bool IsSteamGame => Instance.Origin == InstanceOrigin.Steam;
    public bool IsImported => Instance.Origin == InstanceOrigin.ImportedFolder && !Instance.IsDepotBoxAssociated;
    public bool HasUpdateAvailable => Instance.HasUpdateAvailable;

    public InstanceCardItem(GameInstance instance, IReadOnlyList<GameTag> tags)
    {
        Instance = instance ?? throw new ArgumentNullException(nameof(instance));
        Tags = tags ?? [];
    }
}
