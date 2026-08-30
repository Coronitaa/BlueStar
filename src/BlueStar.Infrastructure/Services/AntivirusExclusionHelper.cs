using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace BlueStar.Infrastructure.Services;

/// <summary>
/// Helper for managing Windows Defender folder exclusions with UAC elevation.
/// </summary>
public static class AntivirusExclusionHelper
{
    /// <summary>
    /// Prompts for administrator elevation to add one or more Windows Defender folder exclusions in a single UAC prompt.
    /// </summary>
    public static async Task<bool> AddFolderExclusionAsync(IEnumerable<string> folderPaths, CancellationToken ct = default)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        var validPaths = folderPaths
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p =>
            {
                try { Directory.CreateDirectory(p); } catch { }
                return Path.GetFullPath(p).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            })
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (validPaths.Count == 0) return false;

        return await Task.Run(() =>
        {
            try
            {
                var pathsArray = string.Join(",", validPaths.Select(p => $"'{p.Replace("'", "''")}'"));
                var script = $"$p = @({pathsArray}); Add-MpPreference -ExclusionPath $p";

                var startInfo = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"{script}\"",
                    Verb = "runas",
                    UseShellExecute = true,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                };

                using var proc = Process.Start(startInfo);
                if (proc == null) return false;

                proc.WaitForExit(15000);
                return proc.ExitCode == 0;
            }
            catch
            {
                // User cancelled UAC prompt or Defender is not installed
                return false;
            }
        }, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Prompts for administrator elevation to add a Windows Defender folder exclusion.
    /// </summary>
    public static Task<bool> AddFolderExclusionAsync(string folderPath, CancellationToken ct = default)
        => AddFolderExclusionAsync(new[] { folderPath }, ct);

    /// <summary>
    /// Prompts for administrator elevation to remove one or more Windows Defender folder exclusions in a single UAC prompt.
    /// </summary>
    public static async Task<bool> RemoveFolderExclusionAsync(IEnumerable<string> folderPaths, CancellationToken ct = default)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        var validPaths = folderPaths
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => Path.GetFullPath(p).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (validPaths.Count == 0) return false;

        return await Task.Run(() =>
        {
            try
            {
                var pathsArray = string.Join(",", validPaths.Select(p => $"'{p.Replace("'", "''")}'"));
                var script = $"$p = @({pathsArray}); foreach ($x in $p) {{ try {{ Remove-MpPreference -ExclusionPath $x -ErrorAction SilentlyContinue }} catch {{ }} }}";

                var startInfo = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"{script}\"",
                    Verb = "runas",
                    UseShellExecute = true,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                };

                using var proc = Process.Start(startInfo);
                if (proc == null) return false;

                proc.WaitForExit(15000);
                return proc.ExitCode == 0;
            }
            catch
            {
                return false;
            }
        }, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Prompts for administrator elevation to remove a Windows Defender folder exclusion.
    /// </summary>
    public static Task<bool> RemoveFolderExclusionAsync(string folderPath, CancellationToken ct = default)
        => RemoveFolderExclusionAsync(new[] { folderPath }, ct);
}

