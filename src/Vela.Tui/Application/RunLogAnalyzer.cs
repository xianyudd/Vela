using Vela.Core.Models;

namespace Vela.Tui.Application;

/// <summary>Deterministic, local-only summary of the already projected run journal.</summary>
public sealed record RunLogAnalysisViewModel(
    int TotalCount,
    int TraceCount,
    int InformationCount,
    int WarningCount,
    int ErrorCount,
    string LatestTimestamp,
    string LatestPhase,
    string LatestOperation,
    string Recommendation,
    bool WasTailTruncated,
    string? ReadError)
{
    public bool HasEntries => TotalCount > 0;
}

public static class RunLogAnalyzer
{
    public static RunLogAnalysisViewModel Analyze(RunLogSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var traceCount = snapshot.Lines.Count(line => line.Level == RunEventLevel.Trace);
        var informationCount = snapshot.Lines.Count(line => line.Level == RunEventLevel.Information);
        var warningCount = snapshot.Lines.Count(line => line.Level == RunEventLevel.Warning);
        var errorCount = snapshot.Lines.Count(line => line.Level == RunEventLevel.Error);
        var latest = snapshot.Lines
            .Reverse()
            .Select(TryReadMetadata)
            .FirstOrDefault(metadata => metadata is not null);

        return new RunLogAnalysisViewModel(
            snapshot.Lines.Length,
            traceCount,
            informationCount,
            warningCount,
            errorCount,
            latest?.Timestamp ?? "--:--:--",
            latest?.Phase ?? "未知阶段",
            latest?.Operation ?? "未知事件",
            BuildRecommendation(snapshot, warningCount, errorCount),
            snapshot.WasTailTruncated,
            SanitizeError(snapshot.ErrorMessage));
    }

    private static string BuildRecommendation(
        RunLogSnapshot snapshot,
        int warningCount,
        int errorCount)
    {
        if (!string.IsNullOrWhiteSpace(snapshot.ErrorMessage))
        {
            return "先处理日志读取状态，再进行运行分析。";
        }

        if (snapshot.Lines.IsDefaultOrEmpty)
        {
            return "暂无可分析条目；先运行一次只读预检。";
        }

        if (errorCount > 0)
        {
            return "优先查看错误条目；当前摘要只描述记录，不推断根因。";
        }

        if (warningCount > 0)
        {
            return "核对警告对应阶段，再决定是否重新运行只读预检。";
        }

        return "未发现错误级记录；可继续查看最新条目。";
    }

    private static string? SanitizeError(string? error) =>
        string.IsNullOrWhiteSpace(error) ? null : TuiDisplayText.Sanitize(error, 96);

    private static LogMetadata? TryReadMetadata(RunLogLine line)
    {
        var fields = line.Text.Split(' ', 6, StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length < 5 || !DateTimeOffset.TryParse(
                fields[1],
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind,
                out var timestamp))
        {
            return null;
        }

        var phase = Enum.TryParse<RunPhase>(fields[3], ignoreCase: true, out var parsedPhase)
            ? TuiDisplayText.LabelForPhase(parsedPhase)
            : "未知阶段";
        return new LogMetadata(
            timestamp.ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture),
            phase,
            TuiDisplayText.SafeToken(fields[4], 40, "未知事件"));
    }

    private sealed record LogMetadata(string Timestamp, string Phase, string Operation);
}
