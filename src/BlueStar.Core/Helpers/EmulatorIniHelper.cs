using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace BlueStar.Core.Helpers;

/// <summary>
/// Helper for reading and overriding emulator configuration (.ini files) for ReFix, OnlineFix, and Goldberg LAN.
/// </summary>
public static class EmulatorIniHelper
{
    private static readonly EnumerationOptions SearchOptions = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
        MatchCasing = MatchCasing.CaseInsensitive,
        AttributesToSkip = FileAttributes.ReparsePoint
    };

    /// <summary>
    /// Generates a valid random SteamID64 (starts with standard prefix 76561198...).
    /// </summary>
    public static string GenerateRandomSteamId64()
    {
        var randomNum = Random.Shared.Next(100000000, 999999999);
        return $"76561198{randomNum}";
    }

    /// <summary>
    /// Applies username and steamId overrides to all detected ReFix and OnlineFix .ini files and steam_settings in the target install directory.
    /// </summary>
    public static void ApplyOverrides(string? installPath, string? username, string? steamId)
    {
        if (string.IsNullOrWhiteSpace(installPath) || !Directory.Exists(installPath)) return;
        if (string.IsNullOrWhiteSpace(username) && string.IsNullOrWhiteSpace(steamId)) return;

        username = username?.Trim();
        steamId = steamId?.Trim();

        try
        {
            // 1. ReFix.ini files
            var refixFiles = Directory.GetFiles(installPath, "ReFix.ini", SearchOptions);
            foreach (var file in refixFiles)
            {
                UpdateReFixIni(file, username, steamId);
            }

            // 2. OnlineFix.ini & OnlineFix64.ini files
            var onlineFixFiles = Directory.GetFiles(installPath, "OnlineFix*.ini", SearchOptions);
            foreach (var file in onlineFixFiles)
            {
                UpdateOnlineFixIni(file, username, steamId);
            }

            // 3. steam_settings directories (Goldberg / Steam emulators)
            var steamSettingsDirs = Directory.GetDirectories(installPath, "steam_settings", SearchOptions);
            foreach (var dir in steamSettingsDirs)
            {
                UpdateSteamSettingsDir(dir, username, steamId);
            }
        }
        catch
        {
            // Non-fatal if file access is restricted
        }
    }

    /// <summary>
    /// Updates or adds user identity settings to a ReFix.ini file.
    /// </summary>
    public static void UpdateReFixIni(string iniPath, string? username, string? steamId)
    {
        if (!File.Exists(iniPath)) return;

        try
        {
            var lines = File.ReadAllLines(iniPath).ToList();
            bool hasUserSection = false;
            int userSectionIndex = -1;
            int nextSectionIndex = -1;

            bool updatedName = false;
            bool updatedUserName = false;
            bool updatedSteamId = false;
            bool updatedAutoGenerate = false;

            for (int i = 0; i < lines.Count; i++)
            {
                var trimmed = lines[i].Trim();
                if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
                {
                    var secName = trimmed[1..^1].Trim();
                    if (secName.Equals("User", StringComparison.OrdinalIgnoreCase) ||
                        secName.Equals("Identity", StringComparison.OrdinalIgnoreCase))
                    {
                        hasUserSection = true;
                        userSectionIndex = i;
                    }
                    else if (hasUserSection && nextSectionIndex == -1)
                    {
                        nextSectionIndex = i;
                    }
                }
                else if (hasUserSection && nextSectionIndex == -1)
                {
                    if (!string.IsNullOrWhiteSpace(username))
                    {
                        if (trimmed.StartsWith("Name", StringComparison.OrdinalIgnoreCase) && trimmed.Contains('='))
                        {
                            lines[i] = $"Name = {username}";
                            updatedName = true;
                        }
                        else if (trimmed.StartsWith("UserName", StringComparison.OrdinalIgnoreCase) && trimmed.Contains('='))
                        {
                            lines[i] = $"UserName = {username}";
                            updatedUserName = true;
                        }
                    }

                    if (!string.IsNullOrWhiteSpace(steamId))
                    {
                        if (trimmed.StartsWith("SteamId", StringComparison.OrdinalIgnoreCase) && trimmed.Contains('='))
                        {
                            lines[i] = $"SteamId = {steamId}";
                            updatedSteamId = true;
                        }
                        else if (trimmed.StartsWith("AutoGenerateSteamId", StringComparison.OrdinalIgnoreCase) && trimmed.Contains('='))
                        {
                            lines[i] = "AutoGenerateSteamId = false";
                            updatedAutoGenerate = true;
                        }
                    }
                }
            }

            var insertPosition = nextSectionIndex != -1 ? nextSectionIndex : lines.Count;

            if (hasUserSection)
            {
                if (!string.IsNullOrWhiteSpace(username) && !updatedName)
                {
                    lines.Insert(insertPosition++, $"Name = {username}");
                }
                if (!string.IsNullOrWhiteSpace(username) && !updatedUserName)
                {
                    lines.Insert(insertPosition++, $"UserName = {username}");
                }
                if (!string.IsNullOrWhiteSpace(steamId) && !updatedSteamId)
                {
                    lines.Insert(insertPosition++, $"SteamId = {steamId}");
                }
                if (!string.IsNullOrWhiteSpace(steamId) && !updatedAutoGenerate)
                {
                    lines.Insert(insertPosition++, "AutoGenerateSteamId = false");
                }
            }
            else
            {
                // Add [User] section at the end
                lines.Add(string.Empty);
                lines.Add("[User]");
                if (!string.IsNullOrWhiteSpace(username))
                {
                    lines.Add($"Name = {username}");
                    lines.Add($"UserName = {username}");
                }
                if (!string.IsNullOrWhiteSpace(steamId))
                {
                    lines.Add($"SteamId = {steamId}");
                    lines.Add("AutoGenerateSteamId = false");
                }
            }

            File.WriteAllLines(iniPath, lines, new UTF8Encoding(false));
        }
        catch { }
    }

    /// <summary>
    /// Updates or adds user identity settings to an OnlineFix.ini or OnlineFix64.ini file.
    /// </summary>
    public static void UpdateOnlineFixIni(string iniPath, string? username, string? steamId)
    {
        if (!File.Exists(iniPath)) return;

        try
        {
            var lines = File.ReadAllLines(iniPath).ToList();
            bool hasSettingsSection = false;
            int settingsSectionIndex = -1;
            int nextSectionIndex = -1;

            bool updatedUserName = false;
            bool updatedSteamId = false;
            bool updatedAccountId = false;

            for (int i = 0; i < lines.Count; i++)
            {
                var trimmed = lines[i].Trim();
                if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
                {
                    var secName = trimmed[1..^1].Trim();
                    if (secName.Equals("Settings", StringComparison.OrdinalIgnoreCase) ||
                        secName.Equals("Main", StringComparison.OrdinalIgnoreCase))
                    {
                        hasSettingsSection = true;
                        settingsSectionIndex = i;
                    }
                    else if (hasSettingsSection && nextSectionIndex == -1)
                    {
                        nextSectionIndex = i;
                    }
                }
                else if (hasSettingsSection && nextSectionIndex == -1)
                {
                    if (!string.IsNullOrWhiteSpace(username))
                    {
                        if (trimmed.StartsWith("UserName", StringComparison.OrdinalIgnoreCase) && trimmed.Contains('='))
                        {
                            lines[i] = $"UserName = {username}";
                            updatedUserName = true;
                        }
                        else if (trimmed.StartsWith("account_name", StringComparison.OrdinalIgnoreCase) && trimmed.Contains('='))
                        {
                            lines[i] = $"account_name = {username}";
                            updatedUserName = true;
                        }
                    }

                    if (!string.IsNullOrWhiteSpace(steamId))
                    {
                        if (trimmed.StartsWith("SteamId", StringComparison.OrdinalIgnoreCase) && trimmed.Contains('='))
                        {
                            lines[i] = $"SteamId = {steamId}";
                            updatedSteamId = true;
                        }
                        else if (trimmed.StartsWith("AccountId", StringComparison.OrdinalIgnoreCase) && trimmed.Contains('='))
                        {
                            if (ulong.TryParse(steamId, out var id64))
                            {
                                lines[i] = $"AccountId = {(uint)(id64 & 0xFFFFFFFF)}";
                            }
                            else
                            {
                                lines[i] = $"AccountId = {steamId}";
                            }
                            updatedAccountId = true;
                        }
                    }
                }
            }

            var insertPosition = nextSectionIndex != -1 ? nextSectionIndex : lines.Count;

            if (hasSettingsSection)
            {
                if (!string.IsNullOrWhiteSpace(username) && !updatedUserName)
                {
                    lines.Insert(insertPosition++, $"UserName = {username}");
                }
                if (!string.IsNullOrWhiteSpace(steamId) && !updatedSteamId)
                {
                    lines.Insert(insertPosition++, $"SteamId = {steamId}");
                }
                if (!string.IsNullOrWhiteSpace(steamId) && !updatedAccountId && ulong.TryParse(steamId, out var id64))
                {
                    lines.Insert(insertPosition++, $"AccountId = {(uint)(id64 & 0xFFFFFFFF)}");
                }
            }
            else
            {
                // Add [Settings] section at the end
                lines.Add(string.Empty);
                lines.Add("[Settings]");
                if (!string.IsNullOrWhiteSpace(username))
                {
                    lines.Add($"UserName = {username}");
                }
                if (!string.IsNullOrWhiteSpace(steamId))
                {
                    lines.Add($"SteamId = {steamId}");
                    if (ulong.TryParse(steamId, out var id64))
                    {
                        lines.Add($"AccountId = {(uint)(id64 & 0xFFFFFFFF)}");
                    }
                }
            }

            File.WriteAllLines(iniPath, lines, new UTF8Encoding(false));
        }
        catch { }
    }

    /// <summary>
    /// Updates username and steamid files inside a steam_settings folder.
    /// </summary>
    public static void UpdateSteamSettingsDir(string steamSettingsDir, string? username, string? steamId)
    {
        if (!Directory.Exists(steamSettingsDir)) return;

        try
        {
            if (!string.IsNullOrWhiteSpace(username))
            {
                File.WriteAllText(Path.Combine(steamSettingsDir, "force_account_name.txt"), username, new UTF8Encoding(false));
                File.WriteAllText(Path.Combine(steamSettingsDir, "account_name.txt"), username, new UTF8Encoding(false));
            }

            if (!string.IsNullOrWhiteSpace(steamId))
            {
                File.WriteAllText(Path.Combine(steamSettingsDir, "force_steamid.txt"), steamId, new UTF8Encoding(false));
                File.WriteAllText(Path.Combine(steamSettingsDir, "user_steam_id.txt"), steamId, new UTF8Encoding(false));
            }
        }
        catch { }
    }
}
