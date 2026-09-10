using System;
using BlueStar.Core.Interfaces;
using BlueStar.Core.Models;
using Serilog.Core;
using Serilog.Events;

namespace BlueStar.App.Services;

/// <summary>
/// Serilog sink that bridges Serilog log events into <see cref="IDebugLogService"/> in real time.
/// </summary>
public sealed class DebugLogSink : ILogEventSink
{
    private readonly IDebugLogService _debugLogService;

    public DebugLogSink(IDebugLogService debugLogService)
    {
        _debugLogService = debugLogService ?? throw new ArgumentNullException(nameof(debugLogService));
    }

    public void Emit(LogEvent logEvent)
    {
        if (logEvent == null) return;

        string? sourceContext = null;
        if (logEvent.Properties.TryGetValue("SourceContext", out var sourceVal) &&
            sourceVal is ScalarValue scalar &&
            scalar.Value is string s)
        {
            var idx = s.LastIndexOf('.');
            sourceContext = idx >= 0 ? s[(idx + 1)..] : s;
        }

        var severity = logEvent.Level switch
        {
            LogEventLevel.Verbose => LogSeverity.Verbose,
            LogEventLevel.Debug => LogSeverity.Debug,
            LogEventLevel.Information => LogSeverity.Information,
            LogEventLevel.Warning => LogSeverity.Warning,
            LogEventLevel.Error => LogSeverity.Error,
            LogEventLevel.Fatal => LogSeverity.Fatal,
            _ => LogSeverity.Information
        };

        var item = new LogMessageItem
        {
            Timestamp = logEvent.Timestamp,
            Severity = severity,
            SourceContext = sourceContext,
            Message = logEvent.RenderMessage(),
            Exception = logEvent.Exception?.ToString()
        };

        _debugLogService.Emit(item);
    }
}
