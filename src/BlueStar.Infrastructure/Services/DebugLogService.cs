using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using BlueStar.Core.Interfaces;
using BlueStar.Core.Models;
using BlueStar.Infrastructure.Storage;

namespace BlueStar.Infrastructure.Services;

/// <summary>
/// Thread-safe service for buffering application logs in memory,
/// providing real-time log event streams, and exporting clean diagnostic reports for GitHub issues and troubleshooting.
/// </summary>
public sealed class DebugLogService : IDebugLogService
{
    private const int DefaultCapacity = 3000;
    private readonly int _capacity;
    private readonly object _lock = new();
    private readonly LinkedList<LogMessageItem> _logs = new();
    private AppSettingsService? _appSettings;
    private ISteamStatusService? _steamStatus;
    private IPrerequisiteService? _prerequisiteService;
    private readonly string _logsDirectory;

    /// <summary>
    /// Connects runtime application services for enriched diagnostic reporting.
    /// </summary>
    public void AttachServices(
        AppSettingsService? appSettings,
        ISteamStatusService? steamStatus,
        IPrerequisiteService? prerequisiteService)
    {
        _appSettings = appSettings ?? _appSettings;
        _steamStatus = steamStatus ?? _steamStatus;
        _prerequisiteService = prerequisiteService ?? _prerequisiteService;
    }

    private int _warningsCount;
    private int _errorsCount;

    private static readonly Regex SensitiveDataRegex = new(
        @"(api[-_]?key|token|authorization|bearer|password|secret|auth)[\s:=]+[""']?([a-zA-Z0-9_\-\.]{6,})[""']?",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <inheritdoc />
    public event Action<LogMessageItem>? LogEmitted;

    /// <inheritdoc />
    public string LogsDirectoryPath => _logsDirectory;

    /// <inheritdoc />
    public int TotalLogsCount
    {
        get
        {
            lock (_lock) return _logs.Count;
        }
    }

    /// <inheritdoc />
    public int WarningsCount => Volatile.Read(ref _warningsCount);

    /// <inheritdoc />
    public int ErrorsCount => Volatile.Read(ref _errorsCount);

    /// <summary>
    /// Initializes a new instance of the <see cref="DebugLogService"/> class.
    /// </summary>
    public DebugLogService(
        AppSettingsService? appSettings = null,
        ISteamStatusService? steamStatus = null,
        IPrerequisiteService? prerequisiteService = null,
        string? logsDirectory = null,
        int capacity = DefaultCapacity)
    {
        _appSettings = appSettings;
        _steamStatus = steamStatus;
        _prerequisiteService = prerequisiteService;
        _capacity = capacity > 100 ? capacity : DefaultCapacity;

        _logsDirectory = logsDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "BlueStar", "logs");

        try
        {
            if (!Directory.Exists(_logsDirectory))
            {
                Directory.CreateDirectory(_logsDirectory);
            }
        }
        catch
        {
            // Ignore directory creation failures in sandboxed environments
        }
    }

    /// <inheritdoc />
    public void Emit(LogMessageItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        lock (_lock)
        {
            if (_logs.Count >= _capacity)
            {
                var removed = _logs.First?.Value;
                _logs.RemoveFirst();

                if (removed != null)
                {
                    if (removed.Severity == LogSeverity.Warning)
                        Interlocked.Decrement(ref _warningsCount);
                    else if (removed.Severity is LogSeverity.Error or LogSeverity.Fatal)
                        Interlocked.Decrement(ref _errorsCount);
                }
            }

            _logs.AddLast(item);

            if (item.Severity == LogSeverity.Warning)
                Interlocked.Increment(ref _warningsCount);
            else if (item.Severity is LogSeverity.Error or LogSeverity.Fatal)
                Interlocked.Increment(ref _errorsCount);
        }

        try
        {
            LogEmitted?.Invoke(item);
        }
        catch
        {
            // Prevent subscriber exceptions from disrupting log flow
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<LogMessageItem> GetRecentLogs()
    {
        lock (_lock)
        {
            return _logs.ToList();
        }
    }

    /// <inheritdoc />
    public void Clear()
    {
        lock (_lock)
        {
            _logs.Clear();
            Interlocked.Exchange(ref _warningsCount, 0);
            Interlocked.Exchange(ref _errorsCount, 0);
        }
    }

    /// <inheritdoc />
    public async Task<string> GenerateDiagnosticReportAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<LogMessageItem> snapshot;
        lock (_lock)
        {
            snapshot = _logs.ToList();
        }

        var sb = new StringBuilder(16384);
        var now = DateTimeOffset.Now;

        // ══════════════════════════════════════════════════════════════════════════
        // SECTION 1: HEADER & ENVIRONMENT SUMMARY (Structured for troubleshooting)
        // ══════════════════════════════════════════════════════════════════════════
        sb.AppendLine("================================================================================");
        sb.AppendLine("BLUESTAR SYSTEM DIAGNOSTIC REPORT (FOR GITHUB ISSUES & TROUBLESHOOTING)");
        sb.AppendLine($"Generated: {now:yyyy-MM-dd HH:mm:ss zzz} (UTC: {now.ToUniversalTime():yyyy-MM-dd HH:mm:ss}Z)");
        sb.AppendLine("================================================================================");
        sb.AppendLine();

        sb.AppendLine("### 1. APPLICATION & SYSTEM SPECIFICATIONS");
        var appVersion = Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "1.4.0";
        sb.AppendLine($"- BlueStar Version: {appVersion}");
        sb.AppendLine($"- Operating System: {RuntimeInformation.OSDescription} ({Environment.OSVersion})");
        sb.AppendLine($"- OS Architecture: {RuntimeInformation.OSArchitecture}");
        sb.AppendLine($"- Process Architecture: {RuntimeInformation.ProcessArchitecture}");
        sb.AppendLine($"- .NET Framework: {RuntimeInformation.FrameworkDescription}");
        sb.AppendLine($"- CPU Cores: {Environment.ProcessorCount}");
        sb.AppendLine($"- Working Directory: {Environment.CurrentDirectory}");
        sb.AppendLine($"- AppData Path: {Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BlueStar")}");

        // Memory info
        try
        {
            var memInfo = GC.GetGCMemoryInfo();
            var totalGb = Math.Round((double)memInfo.TotalAvailableMemoryBytes / (1024 * 1024 * 1024), 2);
            var appMb = Math.Round((double)GC.GetTotalMemory(false) / (1024 * 1024), 2);
            sb.AppendLine($"- Total System RAM: {totalGb} GB | Current App Memory: {appMb} MB");
        }
        catch { }

        // Steam Integration status
        sb.AppendLine();
        sb.AppendLine("### 2. STEAM & RUNTIME STATUS");
        if (_steamStatus != null)
        {
            var status = _steamStatus.CurrentStatus;
            sb.AppendLine($"- Steam Running: {status.IsRunning}");
            sb.AppendLine($"- Steam Active User: {SanitizeString(status.DisplayName)}");
        }
        else
        {
            sb.AppendLine("- Steam Status Service: Not attached");
        }

        // Prerequisites check
        if (_prerequisiteService != null)
        {
            try
            {
                var prereqs = await _prerequisiteService.DetectSystemPrerequisitesAsync(cancellationToken).ConfigureAwait(false);
                sb.AppendLine("- System Runtimes Status:");
                foreach (var req in prereqs)
                {
                    sb.AppendLine($"  * {req.Name}: {req.Status}");
                }
            }
            catch (Exception ex)
            {
                sb.AppendLine($"  * Prerequisite detection error: {ex.Message}");
            }
        }

        // App Settings summary (strictly redacted)
        sb.AppendLine();
        sb.AppendLine("### 3. CONFIGURATION SUMMARY (Sanitized)");
        if (_appSettings != null)
        {
            sb.AppendLine($"- Language: {_appSettings.Language}");
            sb.AppendLine($"- Default Download Directory: {_appSettings.DefaultDownloadDirectory}");
            sb.AppendLine($"- Delete Depots After Install: {_appSettings.DeleteDepotsAfterInstall}");
            sb.AppendLine($"- Show NSFW Content: {_appSettings.ShowNsfwContent}");
            sb.AppendLine($"- Show DRM Content: {_appSettings.ShowDrmContent}");
            sb.AppendLine($"- Enable Experimental Mods: {_appSettings.EnableExperimentalMods}");
            sb.AppendLine($"- Enable Advanced Builds: {_appSettings.EnableAdvancedBuildOptions}");
            sb.AppendLine($"- Enable Debug System: {_appSettings.EnableDebugSystem}");
            sb.AppendLine($"- Custom API Key: {(string.IsNullOrWhiteSpace(_appSettings.DefaultApiKey) || _appSettings.DefaultApiKey == "YOUR-API-KEY" ? "Not set" : "[CONFIGURED - REDACTED]")}");
        }
        else
        {
            sb.AppendLine("- Settings Service: Default");
        }

        // ══════════════════════════════════════════════════════════════════════════
        // SECTION 2: ERROR & WARNING TRIAGE (Immediate focus for Issue Investigation & Maintainers)
        // ══════════════════════════════════════════════════════════════════════════
        sb.AppendLine();
        sb.AppendLine("================================================================================");
        sb.AppendLine("### 4. ERROR & WARNING TRIAGE");
        sb.AppendLine("================================================================================");

        var errorsAndWarnings = snapshot
            .Where(x => x.Severity is LogSeverity.Warning or LogSeverity.Error or LogSeverity.Fatal)
            .ToList();

        var totalErrors = errorsAndWarnings.Count(x => x.Severity is LogSeverity.Error or LogSeverity.Fatal);
        var totalWarnings = errorsAndWarnings.Count(x => x.Severity == LogSeverity.Warning);

        sb.AppendLine($"- Total Errors / Criticals in Session: {totalErrors}");
        sb.AppendLine($"- Total Warnings in Session: {totalWarnings}");
        sb.AppendLine();

        if (errorsAndWarnings.Count == 0)
        {
            sb.AppendLine("✅ No errors or warnings recorded in the current session.");
        }
        else
        {
            // Group identical errors to eliminate duplicates
            var groupedErrors = errorsAndWarnings
                .GroupBy(x => new { x.Severity, x.SourceContext, KeyMsg = TruncateForGroup(x.Message) })
                .OrderByDescending(g => g.Count())
                .ToList();

            foreach (var group in groupedErrors)
            {
                var sample = group.First();
                sb.AppendLine($"[{sample.SeverityCode}] ({group.Count()}x) [{sample.SourceContext ?? "General"}]: {SanitizeSensitiveData(sample.Message)}");
                if (!string.IsNullOrWhiteSpace(sample.Exception))
                {
                    sb.AppendLine("  Exception Details:");
                    foreach (var exLine in sample.Exception.Split('\n'))
                    {
                        var trimmed = exLine.Trim();
                        if (!string.IsNullOrEmpty(trimmed))
                        {
                            sb.AppendLine($"    {SanitizeSensitiveData(trimmed)}");
                        }
                    }
                }
            }
        }

        // ══════════════════════════════════════════════════════════════════════════
        // SECTION 3: RECENT CHRONOLOGICAL ACTIVITY (Condensed, deduplicated, clean)
        // ══════════════════════════════════════════════════════════════════════════
        sb.AppendLine();
        sb.AppendLine("================================================================================");
        sb.AppendLine("### 5. RECENT EVENT TIMELINE (Optimized & Deduplicated)");
        sb.AppendLine("================================================================================");

        // Keep last 1,000 entries max to prevent token blowup while maintaining full diagnostic value
        var timelineEntries = snapshot.TakeLast(1000).ToList();

        if (timelineEntries.Count == 0)
        {
            sb.AppendLine("No activity logged.");
        }
        else
        {
            int consecutiveCount = 1;
            LogMessageItem? previous = null;

            for (int i = 0; i < timelineEntries.Count; i++)
            {
                var current = timelineEntries[i];

                if (previous != null && AreMessagesEquivalent(previous, current))
                {
                    consecutiveCount++;
                    continue;
                }

                if (previous != null)
                {
                    AppendCondensedLog(sb, previous, consecutiveCount);
                    consecutiveCount = 1;
                }

                previous = current;
            }

            if (previous != null)
            {
                AppendCondensedLog(sb, previous, consecutiveCount);
            }
        }

        sb.AppendLine();
        sb.AppendLine("================================================================================");
        sb.AppendLine("END OF BLUESTAR DIAGNOSTIC REPORT");
        sb.AppendLine("================================================================================");

        // Save report to file
        if (!Directory.Exists(_logsDirectory))
        {
            Directory.CreateDirectory(_logsDirectory);
        }

        var fileName = $"bluestar-diagnostic-{now:yyyyMMdd-HHmmss}.log";
        var fullPath = Path.Combine(_logsDirectory, fileName);

        await File.WriteAllTextAsync(fullPath, sb.ToString(), Encoding.UTF8, cancellationToken).ConfigureAwait(false);

        return fullPath;
    }

    private static void AppendCondensedLog(StringBuilder sb, LogMessageItem item, int count)
    {
        var cleanMessage = SanitizeSensitiveData(item.Message);
        var repeatSuffix = count > 1 ? $" [repeated {count} times]" : "";
        var contextTag = !string.IsNullOrWhiteSpace(item.SourceContext) ? $"[{item.SourceContext}] " : "";

        sb.AppendLine($"[{item.FormattedTime}] [{item.SeverityCode}] {contextTag}{cleanMessage}{repeatSuffix}");

        if (!string.IsNullOrWhiteSpace(item.Exception))
        {
            sb.AppendLine($"    Stack: {SanitizeSensitiveData(item.Exception.Replace('\r', ' '))}");
        }
    }

    private static bool AreMessagesEquivalent(LogMessageItem a, LogMessageItem b)
    {
        return a.Severity == b.Severity &&
               string.Equals(a.SourceContext, b.SourceContext, StringComparison.Ordinal) &&
               string.Equals(a.Message, b.Message, StringComparison.Ordinal);
    }

    private static string TruncateForGroup(string text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        return text.Length > 100 ? text[..100] : text;
    }

    /// <summary>
    /// Redacts sensitive keys, tokens, and authorization headers from logs.
    /// </summary>
    public static string SanitizeSensitiveData(string text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        return SensitiveDataRegex.Replace(text, "$1=[REDACTED]");
    }

    private static string SanitizeString(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        // Strip common invalid characters or paths if needed
        return text.Trim();
    }
}
