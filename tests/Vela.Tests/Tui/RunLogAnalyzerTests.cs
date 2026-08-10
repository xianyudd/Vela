using System.Collections.Immutable;
using Vela.Core.Models;
using Vela.Tui.Application;

namespace Vela.Tests.Tui;

public sealed class RunLogAnalyzerTests
{
    [Fact]
    public void Analyze_counts_levels_and_projects_latest_stage()
    {
        var snapshot = new RunLogSnapshot(
            [
                new RunLogLine("最新运行日志：", RunEventLevel.Information),
                new RunLogLine("[6] 2026-08-10T02:00:00Z Error Inventory PreflightDiagnostic", RunEventLevel.Error),
                new RunLogLine("[7] 2026-08-10T02:00:01Z Warning Snapshot Preflight", RunEventLevel.Warning)
            ],
            WasTailTruncated: true,
            ErrorMessage: null);

        var analysis = RunLogAnalyzer.Analyze(snapshot);

        Assert.Equal(3, analysis.TotalCount);
        Assert.Equal(1, analysis.InformationCount);
        Assert.Equal(1, analysis.WarningCount);
        Assert.Equal(1, analysis.ErrorCount);
        Assert.Equal("02:00:01", analysis.LatestTimestamp);
        Assert.Equal("采集快照", analysis.LatestPhase);
        Assert.Equal("Preflight", analysis.LatestOperation);
        Assert.Contains("错误", analysis.Recommendation, StringComparison.Ordinal);
        Assert.True(analysis.WasTailTruncated);
    }

    [Fact]
    public void Analyze_returns_a_stable_empty_state_and_sanitizes_reader_error()
    {
        var snapshot = new RunLogSnapshot(
            ImmutableArray<RunLogLine>.Empty,
            WasTailTruncated: false,
            ErrorMessage: "日志读取失败。\u001b[31m");

        var analysis = RunLogAnalyzer.Analyze(snapshot);

        Assert.False(analysis.HasEntries);
        Assert.Equal(0, analysis.ErrorCount);
        Assert.Equal("--:--:--", analysis.LatestTimestamp);
        Assert.Contains("读取状态", analysis.Recommendation, StringComparison.Ordinal);
        Assert.NotNull(analysis.ReadError);
        Assert.DoesNotContain('\u001b', analysis.ReadError!);
    }

    [Fact]
    public void Analyze_replaces_untrusted_operation_tokens()
    {
        var analysis = RunLogAnalyzer.Analyze(new RunLogSnapshot(
            [new RunLogLine(
                "[8] 2026-08-10T02:00:02Z Error Inventory D:\\private\\ext4.vhdx",
                RunEventLevel.Error)],
            WasTailTruncated: false,
            ErrorMessage: null));

        Assert.Equal("未知事件", analysis.LatestOperation);
        Assert.DoesNotContain("private", analysis.LatestOperation, StringComparison.OrdinalIgnoreCase);
    }
}
