using System;
using System.Text;

namespace BlueStar.Core.Helpers;

/// <summary>
/// Helper to identify and filter connectivity, multiplayer, hardware, and technical tags.
/// Used to keep recommendations (such as ""Similar games"") strictly focused on gameplay, genre, theme, and style.
/// </summary>
public static class SteamTagFilterHelper
{
    /// <summary>
    /// Normalizes a tag string by stripping non-alphanumeric characters and converting to lowercase.
    /// </summary>
    public static string CleanTagKey(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return string.Empty;
        var sb = new StringBuilder();
        foreach (var c in s)
        {
            if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
        }
        return sb.ToString();
    }

    /// <summary>
    /// Checks whether a tag represents network connectivity, multiplayer modes, hardware features,
    /// or Steam platform mechanisms rather than actual game/software genre, theme, gameplay, or style.
    /// </summary>
    public static bool IsConnectivityOrTechnicalTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return true;
        var clean = CleanTagKey(tag);

        // Multiplayer, player support and network connectivity
        switch (clean)
        {
            case "singleplayer":
            case "singleplayergame":
            case "multiplayer":
            case "multiplayergame":
            case "massivelymultiplayer":
            case "mmo":
            case "mmorpg":
            case "onlinecoop":
            case "coop":
            case "coopcampaign":
            case "localcoop":
            case "lancoop":
            case "sharedsplitscreencoop":
            case "sharedsplitscreen":
            case "splitscreen":
            case "localmultiplayer":
            case "asynchronousmultiplayer":
            case "crossplatformmultiplayer":
            case "pvp":
            case "onlinepvp":
            case "lanpvp":
            case "sharedsplitscreenpvp":
            case "pve":
                return true;
        }

        // Hardware, controller, and display features
        switch (clean)
        {
            case "fullcontrollersupport":
            case "controllersupport":
            case "controller":
            case "trackedcontrollersupport":
            case "steaminputapi":
            case "dualsensesupport":
            case "dualshocksupport":
            case "vrsupported":
            case "vronly":
            case "trackir":
            case "ultrawide":
            case "hdravailable":
            case "cameracomfort":
                return true;
        }

        // Steam platform features & store notices
        switch (clean)
        {
            case "steamachievements":
            case "steamcloud":
            case "steamtradingcards":
            case "steamworkshop":
            case "steamvrcollectibles":
            case "steamtimeline":
            case "remoteplay":
            case "remoteplaytogether":
            case "remoteplayonphone":
            case "remoteplayontablet":
            case "remoteplayontv":
            case "familysharing":
            case "includessourcesdk":
            case "includesleveleditor":
            case "captionsavailable":
            case "commentaryavailable":
            case "additionalhighqualityaudio":
            case "playablewithouttimedinput":
            case "adjustabledifficulty":
            case "customvolumecontrols":
                return true;
        }

        return false;
    }
}
