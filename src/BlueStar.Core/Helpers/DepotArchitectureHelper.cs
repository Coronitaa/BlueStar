using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using BlueStar.Core.Models;

namespace BlueStar.Core.Helpers;

/// <summary>
/// Helper methods for classifying, normalizing, and filtering depots and DLCs
/// by target processor architecture (x64, x32, and x32/x64).
/// </summary>
public static partial class DepotArchitectureHelper
{
    public const string X64 = "x64";
    public const string X32 = "x32";
    public const string Both = "x32/x64";

    private static readonly Regex BothRegex = new(
        @"(?<![a-zA-Z0-9])(?:both|ambos|universal|all[\s-]arch(?:itectures)?|x86[+/&/\\]\s*(?:x?64)|32[+/&/\\]\s*64|win32[+/&/\\]\s*win64|x32[+/&/\\]\s*x?64|x64[+/&/\\]\s*x?32)(?![a-zA-Z0-9])",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex X64Regex = new(
        @"(?<![a-zA-Z0-9])(?:64[\s-]?bit|x64|win64|amd64|x86[_-]64)(?![a-zA-Z0-9])",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex X32Regex = new(
        @"(?<![a-zA-Z0-9])(?:32[\s-]?bit|x32|win32|x86|i386|i686)(?![a-zA-Z0-9])",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex X8664ExcludeRegex = new(
        @"(?<![a-zA-Z0-9])x86[_-]64(?![a-zA-Z0-9])",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Detects target architecture from comment or depot name.
    /// Returns "x64", "x32", or "x32/x64" if both or neither are found.
    /// </summary>
    public static string DetectArchitecture(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return Both;

        if (BothRegex.IsMatch(text)) return Both;

        bool has64 = X64Regex.IsMatch(text);
        bool has32 = X32Regex.IsMatch(text) && !X8664ExcludeRegex.IsMatch(text);

        if (has64 && has32) return Both;
        if (has64) return X64;
        if (has32) return X32;

        return Both;
    }

    /// <summary>
    /// Normalizes any architecture string (e.g. "64-bit", "x86", "32", "Universal", "Ambos")
    /// to standard values: "x64", "x32", or "x32/x64".
    /// If <paramref name="arch"/> is empty or null, checks <paramref name="name"/>.
    /// If both are empty or neutral, returns "x32/x64".
    /// </summary>
    public static string NormalizeArchitecture(string? arch, string? name = null)
    {
        if (!string.IsNullOrWhiteSpace(arch))
        {
            var trimmed = arch.Trim();
            if (trimmed.Equals("x64", StringComparison.OrdinalIgnoreCase) ||
                trimmed.Equals("64", StringComparison.OrdinalIgnoreCase) ||
                trimmed.Equals("64-bit", StringComparison.OrdinalIgnoreCase) ||
                trimmed.Equals("win64", StringComparison.OrdinalIgnoreCase) ||
                trimmed.Equals("amd64", StringComparison.OrdinalIgnoreCase))
            {
                return X64;
            }

            if (trimmed.Equals("x32", StringComparison.OrdinalIgnoreCase) ||
                trimmed.Equals("32", StringComparison.OrdinalIgnoreCase) ||
                trimmed.Equals("32-bit", StringComparison.OrdinalIgnoreCase) ||
                trimmed.Equals("x86", StringComparison.OrdinalIgnoreCase) ||
                trimmed.Equals("win32", StringComparison.OrdinalIgnoreCase) ||
                trimmed.Equals("i386", StringComparison.OrdinalIgnoreCase))
            {
                return X32;
            }

            if (trimmed.Equals("both", StringComparison.OrdinalIgnoreCase) ||
                trimmed.Equals("ambos", StringComparison.OrdinalIgnoreCase) ||
                trimmed.Equals("universal", StringComparison.OrdinalIgnoreCase) ||
                trimmed.Equals("all", StringComparison.OrdinalIgnoreCase) ||
                trimmed.Equals("x32/x64", StringComparison.OrdinalIgnoreCase) ||
                trimmed.Equals("x64/x32", StringComparison.OrdinalIgnoreCase) ||
                trimmed.Equals("32/64", StringComparison.OrdinalIgnoreCase) ||
                trimmed.Equals("64/32", StringComparison.OrdinalIgnoreCase) ||
                BothRegex.IsMatch(trimmed))
            {
                return Both;
            }

            return DetectArchitecture(trimmed);
        }

        if (!string.IsNullOrWhiteSpace(name))
        {
            return DetectArchitecture(name);
        }

        return Both;
    }

    /// <summary>
    /// Resolves the architecture tag for a depot based on its Architecture field, SteamDbName, or Name.
    /// </summary>
    public static string GetDepotArchitecture(DepotInfo depot)
    {
        if (depot == null) return Both;

        if (!string.IsNullOrWhiteSpace(depot.Architecture))
        {
            return NormalizeArchitecture(depot.Architecture, depot.SteamDbName ?? depot.Name);
        }

        if (!string.IsNullOrWhiteSpace(depot.SteamDbName))
        {
            var detected = DetectArchitecture(depot.SteamDbName);
            if (detected != Both) return detected;
        }

        if (!string.IsNullOrWhiteSpace(depot.Name))
        {
            return DetectArchitecture(depot.Name);
        }

        return Both;
    }

    /// <summary>
    /// Resolves the architecture tag for a DLC.
    /// If the DLC specifies an architecture, normalizes it.
    /// Otherwise inspects its depots: if all are x64 -> "x64", if all are x32 -> "x32",
    /// if mixed or neutral -> "x32/x64".
    /// </summary>
    public static string GetDlcArchitecture(DlcInfo dlc)
    {
        if (dlc == null) return Both;

        if (!string.IsNullOrWhiteSpace(dlc.Architecture))
        {
            return NormalizeArchitecture(dlc.Architecture, dlc.Name);
        }

        if (dlc.Depots != null && dlc.Depots.Count > 0)
        {
            var archs = dlc.Depots
                .Select(GetDepotArchitecture)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (archs.Count == 1)
            {
                return archs[0];
            }

            bool has64 = archs.Contains(X64);
            bool has32 = archs.Contains(X32);

            if (has64 && !has32) return X64;
            if (has32 && !has64) return X32;

            return Both;
        }

        return DetectArchitecture(dlc.Name);
    }

    /// <summary>
    /// Determines the best default architecture preference for a game based on its depots
    /// and the host operating system architecture.
    /// On a 64-bit OS:
    ///   - If x64 depots exist -> "x64"
    ///   - Else if x32 depots exist -> "x32"
    ///   - Else if x32/x64 depots exist -> "x32/x64"
    ///   - Else -> "x64"
    /// On a 32-bit OS:
    ///   - If x32 depots exist -> "x32"
    ///   - Else if x32/x64 depots exist -> "x32/x64"
    ///   - Else -> "x32"
    /// </summary>
    public static string GetDefaultArchitecturePreference(IEnumerable<DepotInfo>? depots, bool? is64BitOperatingSystem = null)
    {
        bool is64 = is64BitOperatingSystem ?? Environment.Is64BitOperatingSystem;
        if (depots == null) return is64 ? X64 : X32;

        var depotList = depots as IList<DepotInfo> ?? depots.ToList();
        if (depotList.Count == 0) return is64 ? X64 : X32;

        bool has64 = depotList.Any(d => GetDepotArchitecture(d) == X64);
        bool has32 = depotList.Any(d => GetDepotArchitecture(d) == X32);
        bool hasBoth = depotList.Any(d => GetDepotArchitecture(d) == Both);

        if (is64)
        {
            if (has64) return X64;
            if (has32) return X32;
            if (hasBoth) return Both;
            return X64;
        }
        else
        {
            if (has32) return X32;
            if (hasBoth) return Both;
            return X32;
        }
    }

    /// <summary>
    /// Returns true if a depot is compatible with the target operating system (Windows)
    /// and the specified architecture preference ("x64", "x32", or "x32/x64").
    /// </summary>
    /// <param name="depot">The depot to inspect.</param>
    /// <param name="archPreference">Target architecture preference ("x64", "x32", "x32/x64", or null for system default).</param>
    /// <param name="has64Depots">Whether the game title has any 64-bit depots available.</param>
    /// <param name="is64BitOperatingSystem">Optional OS bitness override (defaults to current system).</param>
    public static bool IsDepotCompatible(
        DepotInfo depot,
        string? archPreference = null,
        bool has64Depots = true,
        bool? is64BitOperatingSystem = null)
    {
        if (depot == null) return false;

        var plat = (depot.Platform ?? "Universal").Trim();

        // Incompatible platforms on Windows
        if (plat.Equals("Linux", StringComparison.OrdinalIgnoreCase) ||
            plat.Equals("macOS", StringComparison.OrdinalIgnoreCase) ||
            plat.Equals("OSX", StringComparison.OrdinalIgnoreCase) ||
            plat.Equals("Ubuntu", StringComparison.OrdinalIgnoreCase) ||
            plat.Contains("Linux", StringComparison.OrdinalIgnoreCase) ||
            plat.Contains("Mac", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (depot.Name != null)
        {
            if (depot.Name.Contains("Linux", StringComparison.OrdinalIgnoreCase) ||
                depot.Name.Contains("macOS", StringComparison.OrdinalIgnoreCase) ||
                depot.Name.Contains("OSX", StringComparison.OrdinalIgnoreCase) ||
                depot.Name.Contains("Ubuntu", StringComparison.OrdinalIgnoreCase) ||
                depot.Name.Contains("SteamOS", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        bool is64 = is64BitOperatingSystem ?? Environment.Is64BitOperatingSystem;
        var pref = archPreference ?? (is64 ? (has64Depots ? X64 : X32) : X32);
        var depotArch = GetDepotArchitecture(depot);

        if (pref.Equals(Both, StringComparison.OrdinalIgnoreCase) ||
            pref.Equals("Ambos", StringComparison.OrdinalIgnoreCase) ||
            pref.Equals("Both", StringComparison.OrdinalIgnoreCase))
        {
            // If on a 32-bit OS, an x64 depot can never execute
            if (!is64 && depotArch == X64)
            {
                return false;
            }
            return true;
        }

        if (pref.Equals(X64, StringComparison.OrdinalIgnoreCase))
        {
            // On a 32-bit OS, x64 depots cannot execute
            if (!is64 && depotArch == X64)
            {
                return false;
            }

            if (depotArch == X64 || depotArch == Both)
            {
                return true;
            }

            // If depot is x32, it is only accepted on 64-bit if no x64 depots exist for the game
            return !has64Depots;
        }

        if (pref.Equals(X32, StringComparison.OrdinalIgnoreCase))
        {
            return depotArch == X32 || depotArch == Both;
        }

        return true;
    }

    /// <summary>
    /// Returns true if a DLC is compatible with the specified architecture preference.
    /// </summary>
    public static bool IsDlcCompatible(
        DlcInfo dlc,
        string? archPreference = null,
        bool has64Depots = true,
        bool? is64BitOperatingSystem = null)
    {
        if (dlc == null) return false;

        bool is64 = is64BitOperatingSystem ?? Environment.Is64BitOperatingSystem;
        var pref = archPreference ?? (is64 ? (has64Depots ? X64 : X32) : X32);
        var dlcArch = GetDlcArchitecture(dlc);

        if (pref.Equals(Both, StringComparison.OrdinalIgnoreCase) ||
            pref.Equals("Ambos", StringComparison.OrdinalIgnoreCase) ||
            pref.Equals("Both", StringComparison.OrdinalIgnoreCase))
        {
            if (!is64 && dlcArch == X64)
            {
                return false;
            }
            return true;
        }

        if (pref.Equals(X64, StringComparison.OrdinalIgnoreCase))
        {
            if (!is64 && dlcArch == X64)
            {
                return false;
            }

            if (dlcArch == X64 || dlcArch == Both)
            {
                return true;
            }

            // If DLC has specific depots, check if any depot matches
            if (dlc.Depots != null && dlc.Depots.Count > 0)
            {
                return dlc.Depots.Any(d => IsDepotCompatible(d, pref, has64Depots, is64));
            }

            return !has64Depots;
        }

        if (pref.Equals(X32, StringComparison.OrdinalIgnoreCase))
        {
            if (dlcArch == X32 || dlcArch == Both)
            {
                return true;
            }

            if (dlc.Depots != null && dlc.Depots.Count > 0)
            {
                return dlc.Depots.Any(d => IsDepotCompatible(d, pref, has64Depots, is64));
            }

            return false;
        }

        return true;
    }
}
