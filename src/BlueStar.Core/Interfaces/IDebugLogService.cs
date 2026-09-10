using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BlueStar.Core.Models;

namespace BlueStar.Core.Interfaces;

/// <summary>
/// Service providing in-memory log stream buffering, live console subscriptions,
/// and structured diagnostic report generation.
/// </summary>
public interface IDebugLogService
{
    /// <summary>
    /// Event triggered whenever a new log entry is captured.
    /// </summary>
    event Action<LogMessageItem>? LogEmitted;

    /// <summary>
    /// Appends a new log message item to the in-memory buffer and notifies subscribers.
    /// </summary>
    /// <param name="item">The log message item to record.</param>
    void Emit(LogMessageItem item);

    /// <summary>
    /// Retrieves a snapshot of the buffered log messages (up to buffer capacity).
    /// </summary>
    IReadOnlyList<LogMessageItem> GetRecentLogs();

    /// <summary>
    /// Clears all buffered logs in memory.
    /// </summary>
    void Clear();

    /// <summary>
    /// Generates a clean, structured diagnostic report file for GitHub issue reporting and troubleshooting.
    /// Deduplicates repetitive events, strips framework spam, and redacts sensitive credentials.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The full absolute path to the generated diagnostic report file.</returns>
    Task<string> GenerateDiagnosticReportAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// The directory where application logs and diagnostic reports are stored.
    /// </summary>
    string LogsDirectoryPath { get; }

    /// <summary>
    /// Total number of log entries currently stored in the circular buffer.
    /// </summary>
    int TotalLogsCount { get; }

    /// <summary>
    /// Total count of warnings captured.
    /// </summary>
    int WarningsCount { get; }

    /// <summary>
    /// Total count of errors or fatal events captured.
    /// </summary>
    int ErrorsCount { get; }
}
