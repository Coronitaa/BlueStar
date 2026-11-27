using BlueStar.Core.Models;
using BlueStar.Infrastructure.Platforms;

namespace BlueStar.App.ViewModels;

/// <summary>
/// Display item for an installed Epic Games or Microsoft Store game in the platform import modal.
/// </summary>
public sealed record PlatformImportGameItem(
    InstanceOrigin Origin,
    string PlatformId,
    string DisplayName,
    string InstallLocation,
    string? ExecutablePath,
    string FormattedSize,
    string? LogoPath = null,
    InstalledEpicGame? EpicGame = null,
    InstalledMicrosoftStoreGame? MicrosoftGame = null)
{
    public bool IsEpicGames => Origin == InstanceOrigin.EpicGames;
    public bool IsMicrosoftStore => Origin == InstanceOrigin.MicrosoftStore;
}
