using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using BlueStar.Core.Helpers;
using BlueStar.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace BlueStar.Infrastructure.Steam;

/// <summary>
/// Service that monitors Steam process lifecycle and active logged-in user changes via registry and loginusers.vdf.
/// </summary>
public sealed class SteamStatusService : ISteamStatusService
{
    private readonly ILogger<SteamStatusService> _logger;
    private readonly Timer _timer;
    private SteamStatus _currentStatus;
    private bool _disposed;
    private int _evaluating;

    // loginusers.vdf is parsed again only when it changes or the active account does
    private (string Path, DateTime WriteTimeUtc, ulong? SteamId64, string? Persona, string? Account)? _loginUsersCache;

    public SteamStatus CurrentStatus => _currentStatus;
    public event EventHandler<SteamStatus>? StatusChanged;

    public SteamStatusService(ILogger<SteamStatusService> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _currentStatus = new SteamStatus(false, null, null, null);

        // Evaluate immediately
        EvaluateStatus();

        // Check periodically every 2.5 seconds (cheap: a registry read and one process lookup by id)
        _timer = new Timer(_ => EvaluateStatus(), null, TimeSpan.FromSeconds(2.5), TimeSpan.FromSeconds(2.5));
    }

    /// <inheritdoc />
    public void CheckStatusNow()
    {
        EvaluateStatus();
    }

    private void EvaluateStatus()
    {
        if (_disposed) return;
        // A slow tick (disk, AV scan) must not stack with the next one on the thread pool
        if (Interlocked.Exchange(ref _evaluating, 1) == 1) return;

        try
        {
            var isRunning = IsSteamRunning();

            if (!isRunning)
            {
                UpdateStatus(new SteamStatus(false, null, null, null));
                return;
            }

            // Steam is running, read active user details
            string? accountName = null;
            string? personaName = null;
            ulong? steamId64 = null;

            if (OperatingSystem.IsWindows())
            {
                try
                {
                    // 1. Check ActiveProcess
                    using var activeKey = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam\ActiveProcess");
                    var activeUserObj = activeKey?.GetValue("ActiveUser");
                    if (activeUserObj is int activeUser32 && activeUser32 > 0)
                    {
                        steamId64 = 76561197960265728UL + (ulong)(uint)activeUser32;
                    }

                    // 2. Check main Steam registry key
                    using var steamKey = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
                    if (steamKey != null)
                    {
                        personaName = steamKey.GetValue("PersonaName")?.ToString();
                        accountName = steamKey.GetValue("AutoLoginUser")?.ToString() ?? steamKey.GetValue("LastGameNameUsed")?.ToString();
                    }
                }
                catch { }
            }

            // 3. Inspect loginusers.vdf for exact Persona Name matching the active SteamId64
            var steamPath = ShortcutHelper.GetSteamPath();
            if (!string.IsNullOrWhiteSpace(steamPath))
            {
                var loginUsersFile = Path.Combine(steamPath, "config", "loginusers.vdf");
                if (File.Exists(loginUsersFile))
                {
                    try
                    {
                        var (vdfPersona, vdfAccount) = ReadLoginUsersCached(loginUsersFile, steamId64);
                        if (!string.IsNullOrWhiteSpace(vdfPersona))
                            personaName = vdfPersona;
                        if (!string.IsNullOrWhiteSpace(vdfAccount))
                            accountName = vdfAccount;
                    }
                    catch { }
                }
            }

            var newStatus = new SteamStatus(true, accountName, personaName, steamId64);
            UpdateStatus(newStatus);
        }
        catch (Exception ex)
        {
            _logger.LogTrace(ex, "Error while evaluating Steam status");
        }
        finally
        {
            Volatile.Write(ref _evaluating, 0);
        }
    }

    /// <summary>
    /// Steam writes its own PID to HKCU\Software\Valve\Steam\ActiveProcess. Checking that one process is far
    /// cheaper than snapshotting every process on the machine, which this did every 2.5 seconds.
    /// </summary>
    private static bool IsSteamRunning()
    {
        if (OperatingSystem.IsWindows())
        {
            try
            {
                using var activeKey = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam\ActiveProcess");
                if (activeKey?.GetValue("pid") is int pid)
                {
                    if (pid <= 0) return false;
                    try
                    {
                        using var process = Process.GetProcessById(pid);
                        return !process.HasExited &&
                               process.ProcessName.StartsWith("steam", StringComparison.OrdinalIgnoreCase);
                    }
                    catch (ArgumentException)
                    {
                        return false; // no process with that id: Steam is closed
                    }
                }
            }
            catch { }
        }

        try
        {
            var processes = Process.GetProcessesByName("steam");
            foreach (var process in processes) process.Dispose();
            return processes.Length > 0;
        }
        catch
        {
            return false;
        }
    }

    private (string? PersonaName, string? AccountName) ReadLoginUsersCached(string vdfPath, ulong? activeSteamId64)
    {
        var writeTime = File.GetLastWriteTimeUtc(vdfPath);
        if (_loginUsersCache is { } c && c.Path == vdfPath && c.WriteTimeUtc == writeTime && c.SteamId64 == activeSteamId64)
        {
            return (c.Persona, c.Account);
        }

        var (persona, account) = ParseLoginUsers(vdfPath, activeSteamId64);
        _loginUsersCache = (vdfPath, writeTime, activeSteamId64, persona, account);
        return (persona, account);
    }

    private void UpdateStatus(SteamStatus newStatus)
    {
        if (_currentStatus == newStatus) return;

        _currentStatus = newStatus;
        _logger.LogDebug("Steam status changed: IsRunning={IsRunning}, User={User}", newStatus.IsRunning, newStatus.DisplayName);

        try
        {
            StatusChanged?.Invoke(this, newStatus);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in StatusChanged event handler");
        }
    }

    internal static (string? PersonaName, string? AccountName) ParseLoginUsers(string vdfPath, ulong? activeSteamId64)
    {
        try
        {
            var content = File.ReadAllText(vdfPath, Encoding.UTF8);

            // If we have a specific SteamId64, try finding its block
            if (activeSteamId64.HasValue)
            {
                var idStr = activeSteamId64.Value.ToString();
                var blockMatch = Regex.Match(content, $"\"{idStr}\"[\\s\\r\\n]*\\{{([^\\}}]+)\\}}", RegexOptions.Singleline | RegexOptions.IgnoreCase);
                if (blockMatch.Success)
                {
                    var block = blockMatch.Groups[1].Value;
                    var persona = Regex.Match(block, "\"PersonaName\"[\\s\\t]+\"([^\"]+)\"", RegexOptions.IgnoreCase).Groups[1].Value;
                    var account = Regex.Match(block, "\"AccountName\"[\\s\\t]+\"([^\"]+)\"", RegexOptions.IgnoreCase).Groups[1].Value;
                    return (string.IsNullOrWhiteSpace(persona) ? null : persona, string.IsNullOrWhiteSpace(account) ? null : account);
                }
            }

            // Fallback 1: look for block with "mostrecent"\s+"1"
            var recentMatch = Regex.Match(content, "\"([0-9]{17})\"[\\s\\r\\n]*\\{([^\\}]+)\"mostrecent\"[\\s\\t]+\"1\"([^\\}]*)\\}", RegexOptions.Singleline | RegexOptions.IgnoreCase);
            if (recentMatch.Success)
            {
                var fullBlock = recentMatch.Groups[2].Value + recentMatch.Groups[3].Value;
                var persona = Regex.Match(fullBlock, "\"PersonaName\"[\\s\\t]+\"([^\"]+)\"", RegexOptions.IgnoreCase).Groups[1].Value;
                var account = Regex.Match(fullBlock, "\"AccountName\"[\\s\\t]+\"([^\"]+)\"", RegexOptions.IgnoreCase).Groups[1].Value;
                return (string.IsNullOrWhiteSpace(persona) ? null : persona, string.IsNullOrWhiteSpace(account) ? null : account);
            }

            // Fallback 2: look for block with "AutoLogin"\s+"1"
            var autoLoginMatch = Regex.Match(content, "\"([0-9]{17})\"[\\s\\r\\n]*\\{([^\\}]+)\"AutoLogin\"[\\s\\t]+\"1\"([^\\}]*)\\}", RegexOptions.Singleline | RegexOptions.IgnoreCase);
            if (autoLoginMatch.Success)
            {
                var fullBlock = autoLoginMatch.Groups[2].Value + autoLoginMatch.Groups[3].Value;
                var persona = Regex.Match(fullBlock, "\"PersonaName\"[\\s\\t]+\"([^\"]+)\"", RegexOptions.IgnoreCase).Groups[1].Value;
                var account = Regex.Match(fullBlock, "\"AccountName\"[\\s\\t]+\"([^\"]+)\"", RegexOptions.IgnoreCase).Groups[1].Value;
                return (string.IsNullOrWhiteSpace(persona) ? null : persona, string.IsNullOrWhiteSpace(account) ? null : account);
            }

            // Fallback 3: pick the user block with highest "Timestamp"
            var allBlocks = Regex.Matches(content, "\"([0-9]{17})\"[\\s\\r\\n]*\\{([^\\}]+)\\}", RegexOptions.Singleline | RegexOptions.IgnoreCase);
            long highestTs = -1;
            string? bestPersona = null;
            string? bestAccount = null;

            foreach (Match m in allBlocks)
            {
                var block = m.Groups[2].Value;
                var tsStr = Regex.Match(block, "\"Timestamp\"[\\s\\t]+\"([0-9]+)\"", RegexOptions.IgnoreCase).Groups[1].Value;
                long ts = long.TryParse(tsStr, out var val) ? val : 0;
                if (ts >= highestTs)
                {
                    highestTs = ts;
                    var p = Regex.Match(block, "\"PersonaName\"[\\s\\t]+\"([^\"]+)\"", RegexOptions.IgnoreCase).Groups[1].Value;
                    var a = Regex.Match(block, "\"AccountName\"[\\s\\t]+\"([^\"]+)\"", RegexOptions.IgnoreCase).Groups[1].Value;
                    if (!string.IsNullOrWhiteSpace(p)) bestPersona = p;
                    if (!string.IsNullOrWhiteSpace(a)) bestAccount = a;
                }
            }

            if (!string.IsNullOrWhiteSpace(bestPersona) || !string.IsNullOrWhiteSpace(bestAccount))
            {
                return (bestPersona, bestAccount);
            }
        }
        catch { }

        return (null, null);
    }

    /// <inheritdoc />
    public async Task<bool> LaunchAndWaitForSteamFullyLoadedAsync(
        TimeSpan? timeout = null,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        // 1. Check if Steam is already running and user data is loaded
        if (IsFullyLoaded())
        {
            EvaluateStatus();
            progress?.Report("Steam is running and user profile is loaded.");
            return true;
        }

        // 2. Launch Steam if not running
        var steamProcesses = Process.GetProcessesByName("steam");
        if (steamProcesses.Length == 0)
        {
            progress?.Report("Starting Steam client...");
            try
            {
                var steamPath = ShortcutHelper.GetSteamPath();
                if (!string.IsNullOrWhiteSpace(steamPath))
                {
                    var steamExe = Path.Combine(steamPath, "steam.exe");
                    if (File.Exists(steamExe))
                    {
                        Process.Start(new ProcessStartInfo(steamExe) { UseShellExecute = true });
                    }
                    else
                    {
                        Process.Start(new ProcessStartInfo("steam://open/main") { UseShellExecute = true });
                    }
                }
                else
                {
                    Process.Start(new ProcessStartInfo("steam://open/main") { UseShellExecute = true });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to launch Steam process");
            }
        }

        // 3. Poll until Steam process is alive AND user data is fully loaded (ActiveUser > 0 in ActiveProcess)
        var maxWait = timeout ?? TimeSpan.FromSeconds(60);
        var stopwatch = Stopwatch.StartNew();

        while (stopwatch.Elapsed < maxWait && !cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(500, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            var procs = Process.GetProcessesByName("steam");
            if (procs.Length == 0)
            {
                progress?.Report("Waiting for Steam process to start...");
                continue;
            }

            if (IsFullyLoaded())
            {
                progress?.Report("Steam loaded! Initializing game connection...");
                // Brief stabilization delay to ensure IPC pipes and overlay hooks are fully ready
                try
                {
                    await Task.Delay(2500, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { }

                EvaluateStatus();
                return true;
            }
            else
            {
                progress?.Report("Steam is logging in and loading user data...");
            }
        }

        EvaluateStatus();
        return IsFullyLoaded();
    }

    private static bool IsFullyLoaded()
    {
        try
        {
            var steamProcesses = Process.GetProcessesByName("steam");
            if (steamProcesses.Length == 0) return false;

            if (!OperatingSystem.IsWindows()) return true;

            using var activeKey = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam\ActiveProcess");
            if (activeKey == null) return false;

            var activeUserObj = activeKey.GetValue("ActiveUser");
            if (activeUserObj != null)
            {
                long activeUser = Convert.ToInt64(activeUserObj);
                if (activeUser > 0)
                {
                    return true;
                }
            }
        }
        catch { }

        return false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Dispose();
    }
}
