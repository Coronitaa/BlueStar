using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using BlueStar.Core.Interfaces;
using BlueStar.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BlueStar.App.ViewModels;

/// <summary>
/// ViewModel managing real-time log streaming, searching, level filtering,
/// and diagnostic report export for the Debug Console window.
/// </summary>
public partial class DebugConsoleViewModel : ObservableObject, IDisposable
{
    private readonly IDebugLogService _debugLogService;
    private readonly INotificationService _notificationService;
    private readonly List<LogMessageItem> _allLogs = new();
    private readonly object _syncLock = new();
    private bool _disposed;

    public Action? RequestScrollToEnd { get; set; }

    public ObservableCollection<LogMessageItem> FilteredLogs { get; } = new();

    public IReadOnlyList<string> LevelFilters { get; } =
    [
        "All Levels",
        "Info & Above",
        "Warnings Only",
        "Errors Only",
        "Debug & Verbose"
    ];

    [ObservableProperty]
    private string _selectedLevelFilter = "All Levels";

    partial void OnSelectedLevelFilterChanged(string value)
    {
        ApplyFilter();
    }

    [ObservableProperty]
    private string _searchQuery = string.Empty;

    partial void OnSearchQueryChanged(string value)
    {
        ApplyFilter();
    }

    [ObservableProperty]
    private bool _isAutoScrollEnabled = true;

    [ObservableProperty]
    private int _totalCount;

    [ObservableProperty]
    private int _warningCount;

    [ObservableProperty]
    private int _errorCount;

    [ObservableProperty]
    private string? _statusMessage;

    [ObservableProperty]
    private bool _isGeneratingReport;

    public DebugConsoleViewModel(
        IDebugLogService debugLogService,
        INotificationService notificationService)
    {
        _debugLogService = debugLogService ?? throw new ArgumentNullException(nameof(debugLogService));
        _notificationService = notificationService ?? throw new ArgumentNullException(nameof(notificationService));

        // Load existing captured logs from service
        var recent = _debugLogService.GetRecentLogs();
        lock (_syncLock)
        {
            _allLogs.AddRange(recent);
        }

        UpdateCounters();
        ApplyFilter();

        // Subscribe to real-time logs
        _debugLogService.LogEmitted += OnLogEmitted;
    }

    private void OnLogEmitted(LogMessageItem item)
    {
        if (_disposed) return;

        Application.Current?.Dispatcher?.BeginInvoke(() =>
        {
            if (_disposed) return;

            lock (_syncLock)
            {
                _allLogs.Add(item);
            }

            UpdateCounters();

            if (MatchesFilter(item))
            {
                FilteredLogs.Add(item);

                if (IsAutoScrollEnabled)
                {
                    RequestScrollToEnd?.Invoke();
                }
            }
        });
    }

    private void UpdateCounters()
    {
        TotalCount = _debugLogService.TotalLogsCount;
        WarningCount = _debugLogService.WarningsCount;
        ErrorCount = _debugLogService.ErrorsCount;
    }

    private void ApplyFilter()
    {
        var query = SearchQuery?.Trim();
        var level = SelectedLevelFilter;

        List<LogMessageItem> snapshot;
        lock (_syncLock)
        {
            snapshot = _allLogs.ToList();
        }

        var matching = snapshot.Where(item => MatchesFilter(item, query, level)).ToList();

        FilteredLogs.Clear();
        foreach (var log in matching)
        {
            FilteredLogs.Add(log);
        }

        if (IsAutoScrollEnabled && FilteredLogs.Count > 0)
        {
            RequestScrollToEnd?.Invoke();
        }
    }

    private bool MatchesFilter(LogMessageItem item)
    {
        return MatchesFilter(item, SearchQuery?.Trim(), SelectedLevelFilter);
    }

    private static bool MatchesFilter(LogMessageItem item, string? query, string level)
    {
        // Level filter
        bool levelMatch = level switch
        {
            "Info & Above" => item.Severity is LogSeverity.Information or LogSeverity.Warning or LogSeverity.Error or LogSeverity.Fatal,
            "Warnings Only" => item.Severity == LogSeverity.Warning,
            "Errors Only" => item.Severity is LogSeverity.Error or LogSeverity.Fatal,
            "Debug & Verbose" => item.Severity is LogSeverity.Debug or LogSeverity.Verbose,
            _ => true // "All Levels"
        };

        if (!levelMatch) return false;

        // Search text filter
        if (string.IsNullOrEmpty(query)) return true;

        if (item.Message.Contains(query, StringComparison.OrdinalIgnoreCase)) return true;
        if (!string.IsNullOrEmpty(item.SourceContext) && item.SourceContext.Contains(query, StringComparison.OrdinalIgnoreCase)) return true;
        if (!string.IsNullOrEmpty(item.Exception) && item.Exception.Contains(query, StringComparison.OrdinalIgnoreCase)) return true;
        if (item.SeverityCode.Contains(query, StringComparison.OrdinalIgnoreCase)) return true;

        return false;
    }

    [RelayCommand]
    private void ClearLogs()
    {
        _debugLogService.Clear();
        lock (_syncLock)
        {
            _allLogs.Clear();
        }
        FilteredLogs.Clear();
        UpdateCounters();
        StatusMessage = "Console and in-memory log buffer cleared.";
    }

    [RelayCommand]
    private void CopyAllLogs()
    {
        try
        {
            IReadOnlyList<LogMessageItem> logsToCopy;
            lock (_syncLock)
            {
                logsToCopy = FilteredLogs.Count > 0 ? FilteredLogs.ToList() : _allLogs.ToList();
            }

            var sb = new StringBuilder();
            foreach (var log in logsToCopy)
            {
                sb.AppendLine(log.FullText);
            }

            Clipboard.SetDataObject(sb.ToString(), true);
            StatusMessage = $"Copied {logsToCopy.Count} log lines to clipboard.";
            _notificationService.ShowSuccess("Logs Copied", $"Copied {logsToCopy.Count} log entries to clipboard.");
        }
        catch (Exception ex)
        {
            StatusMessage = $"Copy failed: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task GenerateDiagnosticReportAsync()
    {
        if (IsGeneratingReport) return;

        IsGeneratingReport = true;
        StatusMessage = "Compiling diagnostic report...";

        try
        {
            var filePath = await _debugLogService.GenerateDiagnosticReportAsync(CancellationToken.None).ConfigureAwait(true);
            var fileName = Path.GetFileName(filePath);

            StatusMessage = $"✅ Diagnostic log ready: {fileName}";
            _notificationService.ShowSuccess("Diagnostic Report Generated", $"Created: {fileName}\nReady for GitHub issues and troubleshooting.");

            try
            {
                Clipboard.SetDataObject(filePath, true);
            }
            catch { }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error generating report: {ex.Message}";
            _notificationService.ShowError("Report Generation Failed", ex.Message);
        }
        finally
        {
            IsGeneratingReport = false;
        }
    }

    [RelayCommand]
    private void OpenLogsFolder()
    {
        try
        {
            var path = _debugLogService.LogsDirectoryPath;
            if (!Directory.Exists(path))
            {
                Directory.CreateDirectory(path);
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            StatusMessage = $"Could not open folder: {ex.Message}";
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _debugLogService.LogEmitted -= OnLogEmitted;
    }
}
