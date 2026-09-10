using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BlueStar.Core.Models;
using BlueStar.Infrastructure.Services;
using FluentAssertions;
using Xunit;

namespace BlueStar.Infrastructure.Tests;

public class DebugLogServiceTests : IDisposable
{
    private readonly string _tempLogDir;

    public DebugLogServiceTests()
    {
        _tempLogDir = Path.Combine(Path.GetTempPath(), "BlueStarTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempLogDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempLogDir))
            {
                Directory.Delete(_tempLogDir, recursive: true);
            }
        }
        catch { }
    }

    [Fact]
    public void Emit_CapturesLogs_AndAccuratelyMaintainsCounters()
    {
        var service = new DebugLogService(logsDirectory: _tempLogDir, capacity: 50);

        service.Emit(new LogMessageItem { Severity = LogSeverity.Information, Message = "Info message 1" });
        service.Emit(new LogMessageItem { Severity = LogSeverity.Warning, Message = "Warning message 1" });
        service.Emit(new LogMessageItem { Severity = LogSeverity.Error, Message = "Error message 1" });
        service.Emit(new LogMessageItem { Severity = LogSeverity.Fatal, Message = "Fatal message 1" });
        service.Emit(new LogMessageItem { Severity = LogSeverity.Debug, Message = "Debug message 1" });

        service.TotalLogsCount.Should().Be(5);
        service.WarningsCount.Should().Be(1);
        service.ErrorsCount.Should().Be(2);

        var recent = service.GetRecentLogs();
        recent.Should().HaveCount(5);
        recent[0].Message.Should().Be("Info message 1");
        recent[4].Message.Should().Be("Debug message 1");
    }

    [Fact]
    public void Emit_CapacityBoundary_DropsOldestAndAdjustsCounters()
    {
        var service = new DebugLogService(logsDirectory: _tempLogDir, capacity: 105);

        // Emit 100 warnings
        for (int i = 0; i < 100; i++)
        {
            service.Emit(new LogMessageItem { Severity = LogSeverity.Warning, Message = $"Warning {i}" });
        }

        service.TotalLogsCount.Should().Be(100);
        service.WarningsCount.Should().Be(100);

        // Emit 10 errors (total 110, capacity is 105, so 5 oldest warnings dropped)
        for (int i = 0; i < 10; i++)
        {
            service.Emit(new LogMessageItem { Severity = LogSeverity.Error, Message = $"Error {i}" });
        }

        service.TotalLogsCount.Should().Be(105);
        service.ErrorsCount.Should().Be(10);
        service.WarningsCount.Should().Be(95);
    }

    [Fact]
    public void SanitizeSensitiveData_RedactsTokensAndKeys()
    {
        var dirty1 = "Request failed with api_key=abc123456789xyz on endpoint";
        var clean1 = DebugLogService.SanitizeSensitiveData(dirty1);
        clean1.Should().NotContain("abc123456789xyz");
        clean1.Should().Contain("api_key=[REDACTED]");

        var dirty2 = "Bearer token: eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9";
        var clean2 = DebugLogService.SanitizeSensitiveData(dirty2);
        clean2.Should().NotContain("eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9");
        clean2.Should().Contain("token=[REDACTED]");
    }

    [Fact]
    public async Task GenerateDiagnosticReportAsync_CreatesCleanReport_WithDeduplication()
    {
        var service = new DebugLogService(logsDirectory: _tempLogDir, capacity: 200);

        // Add regular logs
        service.Emit(new LogMessageItem { Severity = LogSeverity.Information, SourceContext = "TestContext", Message = "Application initialized successfully." });

        // Add repeated duplicate logs
        for (int i = 0; i < 5; i++)
        {
            service.Emit(new LogMessageItem { Severity = LogSeverity.Debug, SourceContext = "InstanceManager", Message = "Loaded 26 instances from disk" });
        }

        // Add error log
        service.Emit(new LogMessageItem { Severity = LogSeverity.Error, SourceContext = "NetworkService", Message = "Failed to connect to backend", Exception = "System.Net.Http.HttpRequestException: Connection refused" });

        var reportPath = await service.GenerateDiagnosticReportAsync(CancellationToken.None);

        File.Exists(reportPath).Should().BeTrue();
        var content = await File.ReadAllTextAsync(reportPath);

        // Verify sections
        content.Should().Contain("BLUESTAR SYSTEM DIAGNOSTIC REPORT");
        content.Should().Contain("### 1. APPLICATION & SYSTEM SPECIFICATIONS");
        content.Should().Contain("### 4. ERROR & WARNING TRIAGE");
        content.Should().Contain("### 5. RECENT EVENT TIMELINE");

        // Verify error triage captured the error
        content.Should().Contain("[ERR]");
        content.Should().Contain("Failed to connect to backend");
        content.Should().Contain("System.Net.Http.HttpRequestException");

        // Verify consecutive deduplication
        content.Should().Contain("[repeated 5 times]");
    }

    [Fact]
    public void Clear_EmptiesLogsAndCounters()
    {
        var service = new DebugLogService(logsDirectory: _tempLogDir, capacity: 100);

        service.Emit(new LogMessageItem { Severity = LogSeverity.Warning, Message = "Test Warning" });
        service.Emit(new LogMessageItem { Severity = LogSeverity.Error, Message = "Test Error" });

        service.TotalLogsCount.Should().Be(2);
        service.Clear();

        service.TotalLogsCount.Should().Be(0);
        service.WarningsCount.Should().Be(0);
        service.ErrorsCount.Should().Be(0);
        service.GetRecentLogs().Should().BeEmpty();
    }

    [Fact]
    public async Task AppSettings_EnableDebugSystem_DefaultsToFalse_AndPersists()
    {
        var tempSettingsPath = Path.Combine(_tempLogDir, "settings.json");
        var logger = Microsoft.Extensions.Logging.Abstractions.NullLogger<BlueStar.Infrastructure.Storage.AppSettingsService>.Instance;
        var settings = new BlueStar.Infrastructure.Storage.AppSettingsService(logger, tempSettingsPath);

        settings.EnableDebugSystem.Should().BeFalse();

        await settings.SetEnableDebugSystemAsync(true);
        settings.EnableDebugSystem.Should().BeTrue();

        var reloaded = new BlueStar.Infrastructure.Storage.AppSettingsService(logger, tempSettingsPath);
        reloaded.EnableDebugSystem.Should().BeTrue();
    }
}
