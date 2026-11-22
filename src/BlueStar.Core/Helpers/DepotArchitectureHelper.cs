using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using BlueStar.Core.Models;

namespace BlueStar.Core.Helpers;

/// <summary>
/// Helper methods for classifying, normalizing, and filtering depots and DLCs
/// by target processor architecture (arm64, x64, x32, and x32/x64).
/// </summary>
public static partial class DepotArchitectureHelper
{
    public const string Arm64 = "arm64";
    public const string X64 = "x64";
    public const string X32 = "x32";
    public const string Both = "x32/x64";

    private static readonly Regex BothRegex = new(
        @"(?<![a-zA-Z0-9])(?:both|ambos|universal|all[\s-]arch(?:itectures)?|x86[+/&/\\]\s*(?:x?64)|32[+/&/\\]\s*64|win32[+/&/\\]\s*win64|x32[+/&/\\]\s*x?64|x64[+/&/\\]\s*x?32)(?![a-zA-Z0-9])",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex Arm64Regex = new(
        @"(?<![a-zA-Z0-9])(?:arm64|aarch64|win[_-]?arm64)(?![a-zA-Z0-9])",
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
    /// Returns "arm64", "x64", "x32", or "x32/x64" if both or neither are found.
    /// </summary>
    public static string DetectArchitecture(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return Both;

        if (BothRegex.IsMatch(text)) return Both;

        bool hasArm64 = Arm64Regex.IsMatch(text);
        bool has64 = X64Regex.IsMatch(text) && !hasArm64;
        bool has32 = X32Regex.IsMatch(text) && !X8664ExcludeRegex.IsMatch(text);

        if ((has64 && has32) || (hasArm64 && (has64 || has32))) return Both;
        if (hasArm64) return Arm64;
        if (has64) return X64;
        if (has32) return X32;

        return Both;
    }

    /// <summary>
    /// Normalizes any architecture string (e.g. "64-bit", "arm64", "x86", "32", "Universal", "Ambos")
    /// to standard values: "arm64", "x64", "x32", or "x32/x64".
    /// If <paramref name="arch"/> is empty or null, checks <paramref name="name"/>.
    /// If both are empty or neutral, returns "x32/x64".
    /// </summary>
    public static string NormalizeArchitecture(string? arch, string? name = null)
    {
        if (!string.IsNullOrWhiteSpace(arch))
        {
            var trimmed = arch.Trim();
            if (trimmed.Equals("arm64", StringComparison.OrdinalIgnoreCase) ||
                trimmed.Equals("aarch64", StringComparison.OrdinalIgnoreCase) ||
                trimmed.Equals("win-arm64", StringComparison.OrdinalIgnoreCase) ||
                trimmed.Equals("win_arm64", StringComparison.OrdinalIgnoreCase))
            {
                return Arm64;
            }

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
    /// On an ARM64 OS:
    ///   - If arm64 depots exist -> "arm64"
    ///   - Else if x64 depots exist -> "x64" (via Prism emulation on Windows 11)
    ///   - Else if x32 depots exist -> "x32"
    ///   - Else if x32/x64 depots exist -> "x32/x64"
    ///   - Else -> "x64"
    /// On a 64-bit OS (x64):
    ///   - If x64 depots exist -> "x64"
    ///   - Else if x32 depots exist -> "x32"
    ///   - Else if x32/x64 depots exist -> "x32/x64"
    ///   - Else -> "x64"
    /// On a 32-bit OS:
    ///   - If x32 depots exist -> "x32"
    ///   - Else if x32/x64 depots exist -> "x32/x64"
    ///   - Else -> "x32"
    /// </summary>
    public static string GetDefaultArchitecturePreference(
        IEnumerable<DepotInfo>? depots,
        bool? is64BitOperatingSystem = null,
        Architecture? targetHostArch = null)
    {
        var hostArch = targetHostArch ?? RuntimeInformation.OSArchitecture;
        bool isArm64Host = hostArch == Architecture.Arm64;
        bool is64 = is64BitOperatingSystem ?? (isArm64Host || Environment.Is64BitOperatingSystem);

        if (depots == null) return isArm64Host ? Arm64 : (is64 ? X64 : X32);

        var depotList = depots as IList<DepotInfo> ?? depots.ToList();
        if (depotList.Count == 0) return isArm64Host ? Arm64 : (is64 ? X64 : X32);

        bool hasArm64 = depotList.Any(d => GetDepotArchitecture(d) == Arm64);
        if (isArm64Host && hasArm64) return Arm64;

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
    /// and the specified architecture preference ("arm64", "x64", "x32", or "x32/x64").
    /// </summary>
    /// <param name="depot">The depot to inspect.</param>
    /// <param name="archPreference">Target architecture preference ("arm64", "x64", "x32", "x32/x64", or null for system default).</param>
    /// <param name="has64Depots">Whether the game title has any 64-bit depots available.</param>
    /// <param name="is64BitOperatingSystem">Optional OS bitness override (defaults to current system).</param>
    /// <param name="targetHostArch">Optional host processor architecture override (defaults to RuntimeInformation.OSArchitecture).</param>
    public static bool IsDepotCompatible(
        DepotInfo depot,
        string? archPreference = null,
        bool has64Depots = true,
        bool? is64BitOperatingSystem = null,
        Architecture? targetHostArch = null)
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

        var hostArch = targetHostArch ?? RuntimeInformation.OSArchitecture;
        bool isArm64Host = hostArch == Architecture.Arm64;
        bool is64 = is64BitOperatingSystem ?? (isArm64Host || Environment.Is64BitOperatingSystem);
        var depotArch = GetDepotArchitecture(depot);

        // An x86 or x64 PC can never execute an arm64 depot
        if (depotArch == Arm64 && !isArm64Host)
        {
            return false;
        }

        var pref = archPreference ?? (isArm64Host
            ? (depotArch == Arm64 ? Arm64 : (has64Depots ? X64 : X32))
            : (is64 ? (has64Depots ? X64 : X32) : X32));

        if (pref.Equals(Arm64, StringComparison.OrdinalIgnoreCase))
        {
            return isArm64Host && (depotArch == Arm64 || depotArch == Both);
        }

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

            if (depotArch == Arm64)
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
            if (depotArch == Arm64)
            {
                return false;
            }
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
        bool? is64BitOperatingSystem = null,
        Architecture? targetHostArch = null)
    {
        if (dlc == null) return false;

        var hostArch = targetHostArch ?? RuntimeInformation.OSArchitecture;
        bool isArm64Host = hostArch == Architecture.Arm64;
        bool is64 = is64BitOperatingSystem ?? (isArm64Host || Environment.Is64BitOperatingSystem);
        var dlcArch = GetDlcArchitecture(dlc);

        if (dlcArch == Arm64 && !isArm64Host)
        {
            return false;
        }

        var pref = archPreference ?? (isArm64Host
            ? (dlcArch == Arm64 ? Arm64 : (has64Depots ? X64 : X32))
            : (is64 ? (has64Depots ? X64 : X32) : X32));

        if (pref.Equals(Arm64, StringComparison.OrdinalIgnoreCase))
        {
            if (!isArm64Host) return false;
            if (dlcArch == Arm64 || dlcArch == Both) return true;
            if (dlc.Depots != null && dlc.Depots.Count > 0)
            {
                return dlc.Depots.Any(d => IsDepotCompatible(d, pref, has64Depots, is64, hostArch));
            }
            return false;
        }

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

            if (dlcArch == Arm64)
            {
                return false;
            }

            if (dlcArch == X64 || dlcArch == Both)
            {
                return true;
            }

            if (dlc.Depots != null && dlc.Depots.Count > 0)
            {
                return dlc.Depots.Any(d => IsDepotCompatible(d, pref, has64Depots, is64, hostArch));
            }

            return !has64Depots;
        }

        if (pref.Equals(X32, StringComparison.OrdinalIgnoreCase))
        {
            if (dlcArch == Arm64)
            {
                return false;
            }

            if (dlcArch == X32 || dlcArch == Both)
            {
                return true;
            }

            if (dlc.Depots != null && dlc.Depots.Count > 0)
            {
                return dlc.Depots.Any(d => IsDepotCompatible(d, pref, has64Depots, is64, hostArch));
            }

            return false;
        }

        return true;
    }
}
