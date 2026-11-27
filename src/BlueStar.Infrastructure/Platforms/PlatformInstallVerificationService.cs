using System.Threading;
using System.Threading.Tasks;
using BlueStar.Core.Models;
using BlueStar.Infrastructure.Steam;

namespace BlueStar.Infrastructure.Platforms;

/// <summary>
/// Verifies whether instances imported from external platforms (Steam, Epic Games, Microsoft Store)
/// are still installed on their respective platform.
/// </summary>
public static class PlatformInstallVerificationService
{
    /// <summary>
    /// Returns true if the game is still installed on its origin platform (or if the instance is not an external platform game).
    /// Returns false if the platform installation is missing.
    /// </summary>
    public static Task<bool> IsPlatformInstallPresentAsync(GameInstance instance, CancellationToken ct = default)
    {
        if (instance == null || !instance.IsExternalPlatformGame)
            return Task.FromResult(true);

        return Task.Run(() => instance.Origin switch
        {
            InstanceOrigin.Steam => SteamLibraryScanner.IsGameInstalled(instance.AppId, instance.InstallPath),
            InstanceOrigin.EpicGames => EpicGamesLibraryScanner.IsGameInstalled(instance.PlatformAppId, instance.InstallPath),
            InstanceOrigin.MicrosoftStore => MicrosoftStoreLibraryScanner.IsGameInstalled(instance.PlatformAppId, instance.InstallPath),
            _ => true
        }, ct);
    }
}
