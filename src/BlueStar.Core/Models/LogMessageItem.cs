using System;

namespace BlueStar.Core.Models;

/// <summary>
/// Severity level of a log entry.
/// </summary>
public enum LogSeverity
{
    Verbose,
    Debug,
    Information,
    Warning,
    Error,
    Fatal
}

/// <summary>
/// Represents a structured log entry captured in memory for the live debug console and diagnostics.
/// </summary>
public sealed record LogMessageItem
{
    /// <summary>
    /// Timestamp when the log event occurred.
    /// </summary>
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.Now;

    /// <summary>
    /// Severity level of the log message.
    /// </summary>
    public LogSeverity Severity { get; init; } = LogSeverity.Information;

    /// <summary>
    /// Source context or category (e.g. class name or logger name).
    /// </summary>
    public string? SourceContext { get; init; }

    /// <summary>
    /// The formatted log message text.
    /// </summary>
    public required string Message { get; init; }

    /// <summary>
    /// Exception details if an error or exception occurred.
    /// </summary>
    public string? Exception { get; init; }

    /// <summary>
    /// Short 3-letter severity tag for terminal/console display.
    /// </summary>
    public string SeverityCode => Severity switch
    {
        LogSeverity.Verbose => "VRB",
        LogSeverity.Debug => "DBG",
        LogSeverity.Information => "INF",
        LogSeverity.Warning => "WRN",
        LogSeverity.Error => "ERR",
        LogSeverity.Fatal => "FTL",
        _ => "INF"
    };

    /// <summary>
    /// Formatted time string (HH:mm:ss.fff).
    /// </summary>
    public string FormattedTime => Timestamp.ToString("HH:mm:ss.fff");

    /// <summary>
    /// Full single-line or multi-line representation for copying.
    /// </summary>
    public string FullText =>
        $"[{FormattedTime}] [{SeverityCode}] {(!string.IsNullOrWhiteSpace(SourceContext) ? $"[{SourceContext}] " : "")}{Message}{(string.IsNullOrWhiteSpace(Exception) ? "" : $"\n{Exception}")}";
}
