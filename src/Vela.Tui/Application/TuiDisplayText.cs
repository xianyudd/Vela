using Vela.Application.Display;
using Vela.Core.Models;
using Vela.Core.Workflows;
using Vela.Tui.ProgramModes;

namespace Vela.Tui.Application;

internal static class TuiDisplayText
{
    /// <summary>
    /// Bounds <paramref name="value"/> to <paramref name="maxCells"/> display
    /// cells. Delegates to <see cref="DisplayTextSanitizer"/> so the TUI and the
    /// application-layer projection cannot disagree about what is renderable.
    /// </summary>
    public static string Sanitize(string? value, int maxCells = 240) =>
        DisplayTextSanitizer.Sanitize(value, maxCells);

    public static string PadRight(string? value, int totalCells)
    {
        var sanitized = Sanitize(value, totalCells);
        var padding = Math.Max(0, totalCells - DisplayTextSanitizer.DisplayWidth(sanitized));
        return sanitized + new string(' ', padding);
    }

    public static string SafeToken(string? value, int maxCells, string fallback)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fallback);
        var sanitized = Sanitize(value, maxCells);
        if (string.IsNullOrEmpty(sanitized) ||
            sanitized.Any(character =>
                !(character is >= 'A' and <= 'Z') &&
                !(character is >= 'a' and <= 'z') &&
                !(character is >= '0' and <= '9') &&
                character is not ('_' or '-' or '.')))
        {
            return fallback;
        }

        return sanitized;
    }

    public static string LabelForPhase(RunPhase phase) => phase switch
    {
        RunPhase.Validation => "验证",
        RunPhase.Inventory => "检查环境",
        RunPhase.Snapshot => "采集快照",
        RunPhase.AwaitingConfirmation => "等待确认",
        RunPhase.Elevation => "请求权限",
        RunPhase.Shutdown => "停止发行版",
        RunPhase.DiskPartPreflight => "检查压缩条件",
        RunPhase.Compacting => "压缩目标",
        RunPhase.Completed => "完成",
        RunPhase.Failed => "失败",
        _ => "处理中"
    };

    public static string LabelForOperation(string? operationName) => operationName switch
    {
        "RunCreated" => "已创建运行",
        "WorkerCompleted" => "Worker 已完成",
        "WorkerFailed" => "Worker 失败",
        "UacCancelled" => "权限请求已取消",
        "UacLaunchFailed" => "Worker 启动失败",
        "WorkerRequestInvalid" => "请求验证失败",
        "WorkerAdministratorProbeFailed" => "权限检查失败",
        "WorkerNotElevated" => "权限不足",
        "WorkerLxssResolutionFailed" => "目标解析失败",
        "WorkerLxssMappingMismatch" => "目标映射不匹配",
        _ => "处理中"
    };

    public static string LabelForIntent(OperationIntent? intent) => intent switch
    {
        OperationIntent.Preflight => "只读预检",
        OperationIntent.Compact => "执行压缩",
        _ => "未知操作"
    };

    public static string LabelForShutdownMode(ShutdownMode mode) => mode switch
    {
        ShutdownMode.Global => "全局停止",
        ShutdownMode.Distro => "目标发行版停止",
        _ => "未知停止范围"
    };

    public static string LabelForTerminal(TerminalResult? result) => result switch
    {
        TerminalResult.Succeeded => "成功",
        TerminalResult.CompletedWithNoReclaim => "完成但未回收空间",
        TerminalResult.ValidationFailed => "验证失败",
        TerminalResult.ShutdownTimedOut => "停止超时",
        TerminalResult.DiskPartPreflightFailed => "压缩条件检查失败",
        TerminalResult.DiskPartCompactFailed => "压缩失败",
        TerminalResult.WorkerInterrupted => "Worker 中断",
        TerminalResult.CancelledBeforeElevation => "权限请求前取消",
        _ => "失败"
    };

    public static string LabelForPollStatus(RunJournalPollStatus status) => status switch
    {
        RunJournalPollStatus.Cancelled => "等待已取消",
        RunJournalPollStatus.TimedOut => "等待已超时",
        RunJournalPollStatus.ReadFailed => "日志读取失败",
        RunJournalPollStatus.Terminal => "已收到终态",
        _ => "等待运行结果"
    };

    public static string LabelForDiagnostic(WorkflowDiagnosticCode code) => code switch
    {
        WorkflowDiagnosticCode.RequestInvalid => "运行请求无效",
        WorkflowDiagnosticCode.ProfileValidationFailed => "档案验证失败",
        WorkflowDiagnosticCode.InstalledInventoryFailed => "无法读取已安装发行版",
        WorkflowDiagnosticCode.DistroNotInstalled => "目标发行版未安装",
        WorkflowDiagnosticCode.LxssResolutionNotFound => "未找到目标映射",
        WorkflowDiagnosticCode.LxssResolutionFailed => "目标映射解析失败",
        WorkflowDiagnosticCode.LxssMappingMismatch => "目标映射不匹配",
        WorkflowDiagnosticCode.VhdxMissing => "目标 VHDX 不存在",
        WorkflowDiagnosticCode.VhdxInspectionFailed => "VHDX 快照采集失败",
        WorkflowDiagnosticCode.SparseStateUnknown => "稀疏状态未知",
        WorkflowDiagnosticCode.RunningInventoryFailed => "无法读取运行中发行版",
        WorkflowDiagnosticCode.ShutdownTimedOut => "停止发行版超时",
        WorkflowDiagnosticCode.DiskPartPreflightFailed => "压缩条件检查失败",
        WorkflowDiagnosticCode.DiskPartCompactFailed => "压缩失败",
        WorkflowDiagnosticCode.JournalFailure => "运行日志不可用",
        _ => "运行状态异常"
    };

    public static string LabelForInspection(TargetInspectionState state) => state switch
    {
        TargetInspectionState.Available => "已采集",
        TargetInspectionState.Missing => "目标不存在",
        TargetInspectionState.Failed => "采集失败",
        _ => "尚未采集"
    };

    public static string PathStatus(string? value, string configuredText = "已配置") =>
        string.IsNullOrWhiteSpace(value) ? "未配置" : configuredText;

    public static string MappingStatus(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? "未找到映射"
            : value.Contains("未", StringComparison.Ordinal)
                ? "未找到映射"
                : value.Contains("尚未", StringComparison.Ordinal)
                    ? "尚未检查"
                    : "已解析";
}
