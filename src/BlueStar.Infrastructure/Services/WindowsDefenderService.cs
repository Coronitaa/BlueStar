using System;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using BlueStar.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace BlueStar.Infrastructure.Services;

/// <summary>
/// Service to manage Windows Defender folder exclusions for emulators and game directories.
/// </summary>
public sealed class WindowsDefenderService : IWindowsDefenderService
{
    private readonly ILogger<WindowsDefenderService> _logger;

    public WindowsDefenderService(ILogger<WindowsDefenderService> logger)
    {
        _logger = logger;
    }

    public async Task<bool> AddFolderExclusionAsync(string folderPath, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folderPath);

        if (!OperatingSystem.IsWindows())
            return true;

        var fullPath = Path.GetFullPath(folderPath);
        _logger.LogInformation("Adding Windows Defender exclusion for: {Path}", fullPath);

        return await RunDefenderCommandAsync($"Add-MpPreference -ExclusionPath '{fullPath.Replace("'", "''")}'", ct).ConfigureAwait(false);
    }

    public async Task<bool> RemoveFolderExclusionAsync(string folderPath, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folderPath);

        if (!OperatingSystem.IsWindows())
            return true;

        var fullPath = Path.GetFullPath(folderPath);
        _logger.LogInformation("Removing Windows Defender exclusion for: {Path}", fullPath);

        return await RunDefenderCommandAsync($"Remove-MpPreference -ExclusionPath '{fullPath.Replace("'", "''")}'", ct).ConfigureAwait(false);
    }

    private async Task<bool> RunDefenderCommandAsync(string command, CancellationToken ct)
    {
        return await Task.Run(() =>
        {
            try
            {
                bool isAdmin = false;
                try
                {
                    using var identity = WindowsIdentity.GetCurrent();
                    var principal = new WindowsPrincipal(identity);
                    isAdmin = principal.IsInRole(WindowsBuiltInRole.Administrator);
                }
                catch { }

                var psi = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-NoProfile -NonInteractive -WindowStyle Hidden -Command \"{command}\"",
                    UseShellExecute = !isAdmin,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                };

                if (!isAdmin)
                {
                    psi.Verb = "runas";
                }
                else
                {
                    psi.RedirectStandardError = true;
                    psi.RedirectStandardOutput = true;
                }

                using var process = Process.Start(psi);
                if (process == null) return false;

                process.WaitForExit(10000);
                return process.ExitCode == 0;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed executing Windows Defender command: {Command}", command);
                return false;
            }
        }, ct).ConfigureAwait(false);
    }
}
