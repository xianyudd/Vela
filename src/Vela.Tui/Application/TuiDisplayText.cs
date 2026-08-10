using System.Text;
using Vela.Core.Models;
using Vela.Core.Workflows;
using Vela.Tui.ProgramModes;

namespace Vela.Tui.Application;

internal static class TuiDisplayText
{
    public static string Sanitize(string? value, int maxCells = 240)
    {
        if (maxCells <= 0 || string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var normalized = StripControlAndEscapeSequences(value);
        if (DisplayWidth(normalized) <= maxCells)
        {
            return normalized;
        }

        if (maxCells == 1)
        {
            return "…";
        }

        var builder = new StringBuilder(normalized.Length);
        var width = 0;
        foreach (var rune in normalized.EnumerateRunes())
        {
            var runeWidth = GetRuneWidth(rune.Value);
            if (width + runeWidth > maxCells - 1)
            {
                break;
            }

            builder.Append(rune.ToString());
            width += runeWidth;
        }

        return builder.Append('…').ToString();
    }

    public static string PadRight(string? value, int totalCells)
    {
        var sanitized = Sanitize(value, totalCells);
        var padding = Math.Max(0, totalCells - DisplayWidth(sanitized));
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

    public static string LabelForMapping(TargetMappingState state) => state switch
    {
        TargetMappingState.Matched => "已匹配",
        TargetMappingState.Mismatched => "不匹配",
        TargetMappingState.NotFound => "未找到",
        TargetMappingState.Failed => "解析失败",
        _ => "尚未检查"
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

    private static string StripControlAndEscapeSequences(string value)
    {
        var builder = new StringBuilder(value.Length);
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (character == '')
            {
                index = SkipEscapeSequence(value, index);
                continue;
            }

            if (char.IsControl(character) || character == '')
            {
                builder.Append(' ');
            }
            else
            {
                builder.Append(character);
            }
        }

        return builder.ToString();
    }

    private static int SkipEscapeSequence(string value, int escapeIndex)
    {
        if (escapeIndex + 1 >= value.Length)
        {
            return escapeIndex;
        }

        var next = value[escapeIndex + 1];
        if (next == '[')
        {
            var index = escapeIndex + 2;
            while (index < value.Length)
            {
                var character = value[index];
                if (character >= '@' && character <= '~')
                {
                    return index;
                }

                index++;
            }

            return value.Length - 1;
        }

        if (next == ']')
        {
            for (var index = escapeIndex + 2; index < value.Length; index++)
            {
                if (value[index] == '')
                {
                    return index;
                }

                if (value[index] == '' && index + 1 < value.Length && value[index + 1] == '\\')
                {
                    return index + 1;
                }
            }

            return value.Length - 1;
        }

        return escapeIndex + 1;
    }

    private static int DisplayWidth(string value)
    {
        var width = 0;
        foreach (var rune in value.EnumerateRunes())
        {
            width += GetRuneWidth(rune.Value);
        }

        return width;
    }

    private static int GetRuneWidth(int value)
    {
        if (value == 0 || value is >= 0x0300 and <= 0x036f ||
            value is >= 0x1ab0 and <= 0x1aff ||
            value is >= 0x1dc0 and <= 0x1dff ||
            value is >= 0x20d0 and <= 0x20ff ||
            value is >= 0xfe00 and <= 0xfe0f ||
            value is >= 0xfe20 and <= 0xfe2f ||
            value is >= 0xe0100 and <= 0xe01ef)
        {
            return 0;
        }

        return value switch
        {
            >= 0x1100 and <= 0x115f => 2,
            >= 0x2329 and <= 0x232a => 2,
            >= 0x2e80 and <= 0xa4cf => 2,
            >= 0xac00 and <= 0xd7a3 => 2,
            >= 0xf900 and <= 0xfaff => 2,
            >= 0xfe10 and <= 0xfe19 => 2,
            >= 0xfe30 and <= 0xfe6f => 2,
            >= 0xff00 and <= 0xff60 => 2,
            >= 0xffe0 and <= 0xffe6 => 2,
            >= 0x1f300 and <= 0x1faff => 2,
            _ => 1
        };
    }
}
