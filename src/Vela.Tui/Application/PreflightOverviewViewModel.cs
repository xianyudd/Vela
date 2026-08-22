using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using Vela.Core.Contracts;
using Vela.Core.Models;

namespace Vela.Tui.Application;

public enum PreflightGateStatus
{
    Matched,
    Attention,
    NotChecked,
    Failed
}

public sealed record PreflightGateViewModel(
    string Label,
    PreflightGateStatus Status,
    string Detail)
{
    public string StatusLabel => PreflightOverviewFormatter.FormatGateStatus(Status);
}

public sealed record PreflightEvidenceViewModel(
    bool IsAvailable,
    string FileSize,
    string SparseState,
    string HostTotalSize,
    string HostAvailableSpace,
    string? FilePath = null);

public sealed record PreflightHomeCheckViewModel(
    string Label,
    PreflightGateStatus Status,
    string Detail)
{
    public string Symbol => PreflightOverviewFormatter.FormatGateSymbol(Status);
}

public sealed record PreflightHomeDetailViewModel(
    string Title,
    string Detail,
    PreflightGateStatus Status)
{
    public string Symbol => PreflightOverviewFormatter.FormatHomeDetailSymbol(Status);
    public string Badge => PreflightOverviewFormatter.FormatHomeDetailBadge(Status);
}

public enum PreflightTargetRowStatus
{
    Ready,
    Running,
    Attention,
    Pending,
    Failed
}

/// <summary>
/// Safe, user-facing projection for the instance picker on menu 01. Storage
/// evidence is read-only and bounded before it reaches the terminal surface.
/// </summary>
public sealed record PreflightTargetRowViewModel(
    string DistroName,
    string CurrentSize,
    string VhdxPath,
    string StatusText,
    PreflightTargetRowStatus Status,
    bool IsSelected,
    bool IsLocked)
{
    public string Selector => IsSelected ? "❯" : " ";
}

public sealed record PreflightTargetCheckViewModel(
    string Label,
    string Detail,
    PreflightGateStatus Status)
{
    public string Symbol => PreflightOverviewFormatter.FormatGateSymbol(Status);
    public string StatusText => Status switch
    {
        PreflightGateStatus.Matched => "PASS",
        PreflightGateStatus.Attention => "处理",
        PreflightGateStatus.Failed => "FAIL",
        _ => "待检查"
    };
}

public sealed record PreflightTargetDetailViewModel(
    string DistroName,
    string CurrentSize,
    string VhdxPath,
    string FinalStatus,
    string StatusCode,
    string StatusTitle,
    string StatusSupport,
    string NextStep,
    ImmutableArray<PreflightTargetCheckViewModel> Checks,
    int BlockerCount)
{
    public bool IsReady => StatusCode == "✓ PASS";
}

/// <summary>
/// User-facing homepage projection. It deliberately removes implementation
/// vocabulary (for example registry/Lxss) and keeps each line tied to a fact
/// or an actionable destination.
/// </summary>
public sealed record PreflightHomeViewModel(
    AutomaticPreflightStatus Status,
    string StatusTitle,
    string StatusReason,
    string StatusMeta,
    string ProfileName,
    string DistroName,
    bool VhdxConfigured,
    string ImpactSummary,
    string PendingSummary,
    string ReadSummary,
    ImmutableArray<PreflightHomeCheckViewModel> Checks,
    ImmutableArray<PreflightHomeDetailViewModel> Details,
    string EvidenceSummary,
    string NextStep,
    ImmutableArray<PreflightTargetRowViewModel> Targets = default,
    int SelectedTargetIndex = -1,
    bool TargetLocked = false)
{
    public int PassedCount => Checks.Count(check => check.Status == PreflightGateStatus.Matched);
    public int AttentionCount => Checks.Count(check => check.Status == PreflightGateStatus.Attention);
    public int PendingCount => Checks.Count(check => check.Status == PreflightGateStatus.NotChecked);
    public int FailedCount => Checks.Count(check => check.Status == PreflightGateStatus.Failed);
    public int ProgressPercent => Checks.Length == 0 ? 0 : PassedCount * 100 / Checks.Length;

    public static PreflightHomeViewModel Create(
        PreflightOverviewViewModel overview,
        int selectedTargetIndex = 0,
        bool targetLocked = false)
    {
        ArgumentNullException.ThrowIfNull(overview);

        var checks = overview.Gates
            .Select(gate => new PreflightHomeCheckViewModel(
                PreflightOverviewFormatter.FormatHomeGateLabel(gate.Label),
                gate.Status,
                PreflightOverviewFormatter.FormatHomeGateDetail(gate, overview)))
            .ToImmutableArray();
        var matchedCount = checks.Count(check => check.Status == PreflightGateStatus.Matched);
        var attentionCount = checks.Count(check => check.Status == PreflightGateStatus.Attention);
        var pendingCount = checks.Count(check => check.Status == PreflightGateStatus.NotChecked);
        var failedCount = checks.Count(check => check.Status == PreflightGateStatus.Failed);

        return new PreflightHomeViewModel(
            overview.Status,
            PreflightOverviewFormatter.FormatHomeStatusTitle(overview.Status),
            PreflightOverviewFormatter.FormatHomeStatusReason(overview),
            PreflightOverviewFormatter.FormatHomeStatusMeta(
                matchedCount,
                attentionCount,
                pendingCount,
                failedCount),
            overview.ProfileName,
            overview.DistroName,
            overview.VhdxConfigured,
            PreflightOverviewFormatter.FormatHomeImpact(overview),
            PreflightOverviewFormatter.FormatHomePendingChecks(checks),
            PreflightOverviewFormatter.FormatHomeReadSummary(overview),
            checks,
            PreflightOverviewFormatter.CreateHomeDetails(overview),
            PreflightOverviewFormatter.FormatHomeEvidence(overview.Evidence),
            PreflightOverviewFormatter.FormatHomeNextStep(overview),
            PreflightOverviewFormatter.CreateTargetRows(
                overview,
                selectedTargetIndex,
                targetLocked),
            selectedTargetIndex,
            targetLocked);
    }
}

public sealed record PreflightOverviewViewModel(
    AutomaticPreflightStatus Status,
    string Conclusion,
    string ReasonSummary,
    string ProfileName,
    string DistroName,
    bool VhdxConfigured,
    int RunningCount,
    string RunningSummary,
    ImmutableArray<WslDistribution> InstalledDistros,
    ImmutableArray<PreflightGateViewModel> Gates,
    PreflightEvidenceViewModel Evidence,
    int NoticeCount,
    string? FirstNotice,
    string NextStep,
    string? ConfiguredVhdxPath = null,
    TimeSpan? Elapsed = null)
{
    private const string MappingGate = "注册表 / Lxss 映射";
    private const string VhdxGate = "VHDX 快照";
    private const string RunningGate = "运行实例";
    private const string LogsGate = "日志可用性";
    private const string NoticesGate = "通知";

    public static PreflightOverviewViewModel Create(
        DashboardViewModel dashboard,
        AutomaticPreflightState state)
    {
        ArgumentNullException.ThrowIfNull(dashboard);
        ArgumentNullException.ThrowIfNull(state);

        var notices = dashboard.Notices.IsDefault
            ? ImmutableArray<string>.Empty
            : dashboard.Notices;
        var firstNotice = FirstNoticeFor(dashboard, notices);
        var runningCount = dashboard.RunningDistros.IsDefault
            ? 0
            : dashboard.RunningDistros.Length;
        var evidence = CreateEvidence(dashboard.VhdxEvidence);

        return new PreflightOverviewViewModel(
            state.Status,
            FormatConclusion(state.Status),
            FormatReason(state, firstNotice, notices.Length + (dashboard.ErrorMessage is null ? 0 : 1)),
            SanitizeProfileName(dashboard.ProfileTitle),
            TuiDisplayText.Sanitize(dashboard.DistroName, 64),
            dashboard.TargetConfigured,
            runningCount,
            PreflightOverviewFormatter.FormatRunningSummary(dashboard.RunningDistros),
            dashboard.InstalledDistros.IsDefault
                ? ImmutableArray<WslDistribution>.Empty
                : dashboard.InstalledDistros,
            CreateGates(dashboard, state, evidence, firstNotice, notices.Length + (dashboard.ErrorMessage is null ? 0 : 1)),
            evidence,
            notices.Length + (dashboard.ErrorMessage is null ? 0 : 1),
            firstNotice,
            FormatNextStep(state.Status),
            dashboard.ConfiguredVhdxPath,
            state.Elapsed);
    }

    private static ImmutableArray<PreflightGateViewModel> CreateGates(
        DashboardViewModel dashboard,
        AutomaticPreflightState state,
        PreflightEvidenceViewModel evidence,
        string? firstNotice,
        int noticeCount) =>
        ImmutableArray.Create(
            CreateMappingGate(dashboard),
            CreateVhdxGate(dashboard, evidence),
            CreateRunningGate(dashboard),
            CreateLogGate(dashboard),
            CreateNoticeGate(state, firstNotice, noticeCount));

    private static PreflightGateViewModel CreateMappingGate(DashboardViewModel dashboard)
    {
        var status = dashboard.MappingState switch
        {
            TargetMappingState.Matched => PreflightGateStatus.Matched,
            TargetMappingState.Mismatched or TargetMappingState.NotFound => PreflightGateStatus.Attention,
            TargetMappingState.Failed => PreflightGateStatus.Failed,
            _ => PreflightGateStatus.NotChecked
        };
        return new PreflightGateViewModel(
            MappingGate,
            status,
            TuiDisplayText.LabelForMapping(dashboard.MappingState));
    }

    private static PreflightGateViewModel CreateVhdxGate(
        DashboardViewModel dashboard,
        PreflightEvidenceViewModel evidence)
    {
        var status = dashboard.InspectionState switch
        {
            TargetInspectionState.Available when evidence.IsAvailable => PreflightGateStatus.Matched,
            TargetInspectionState.Available => PreflightGateStatus.Failed,
            TargetInspectionState.Missing => PreflightGateStatus.Attention,
            TargetInspectionState.Failed => PreflightGateStatus.Failed,
            _ => PreflightGateStatus.NotChecked
        };
        return new PreflightGateViewModel(
            VhdxGate,
            status,
            TuiDisplayText.LabelForInspection(dashboard.InspectionState));
    }

    private static PreflightGateViewModel CreateRunningGate(DashboardViewModel dashboard)
    {
        var dataState = dashboard.RunningInventoryState;
        if (dataState == PreflightDataState.NotChecked &&
            !dashboard.RunningDistros.IsDefaultOrEmpty)
        {
            dataState = PreflightDataState.Available;
        }

        var status = dataState switch
        {
            PreflightDataState.Available => PreflightGateStatus.Matched,
            PreflightDataState.Failed => PreflightGateStatus.Failed,
            _ => PreflightGateStatus.NotChecked
        };
        return new PreflightGateViewModel(
            RunningGate,
            status,
            dataState switch
            {
                PreflightDataState.Available => dashboard.RunningDistros.IsDefaultOrEmpty
                    ? "0 个（无运行实例）"
                    : PreflightOverviewFormatter.FormatRunningSummary(dashboard.RunningDistros),
                PreflightDataState.Failed => "读取失败",
                _ => "尚未检查"
            });
    }

    private static PreflightGateViewModel CreateLogGate(DashboardViewModel dashboard)
    {
        var status = dashboard.LogsAvailable ||
            dashboard.LogAvailabilityState == PreflightDataState.Available
            ? PreflightGateStatus.Matched
            : dashboard.LogAvailabilityState == PreflightDataState.Failed
                ? PreflightGateStatus.Failed
                : PreflightGateStatus.NotChecked;
        var detail = status switch
        {
            PreflightGateStatus.Matched => "可用",
            PreflightGateStatus.Failed => "不可用",
            _ => "尚未检查"
        };
        return new PreflightGateViewModel(LogsGate, status, detail);
    }

    private static PreflightGateViewModel CreateNoticeGate(
        AutomaticPreflightState state,
        string? firstNotice,
        int noticeCount)
    {
        var status = state.Status == AutomaticPreflightStatus.Failed
            ? PreflightGateStatus.Failed
            : noticeCount > 0
                ? PreflightGateStatus.Attention
                : state.Status is AutomaticPreflightStatus.Checking or AutomaticPreflightStatus.Idle
                    ? PreflightGateStatus.NotChecked
                    : PreflightGateStatus.Matched;
        var detail = noticeCount > 0
            ? $"{noticeCount} 条 · {TuiDisplayText.Sanitize(firstNotice, 64)}"
            : status == PreflightGateStatus.NotChecked
                ? "尚未检查"
                : "0 条";
        return new PreflightGateViewModel(NoticesGate, status, detail);
    }

    private static PreflightEvidenceViewModel CreateEvidence(VhdxEvidenceViewModel? evidence) =>
        evidence is null
            ? new PreflightEvidenceViewModel(
                IsAvailable: false,
                FileSize: "尚未采集",
                SparseState: PreflightOverviewFormatter.FormatSparseState(null),
                HostTotalSize: "尚未采集",
                HostAvailableSpace: "尚未采集",
                FilePath: null)
            : new PreflightEvidenceViewModel(
                IsAvailable: true,
                FileSize: PreflightOverviewFormatter.FormatCapacity(evidence.FileLengthBytes),
                SparseState: PreflightOverviewFormatter.FormatSparseState(evidence.IsSparse),
                HostTotalSize: PreflightOverviewFormatter.FormatCapacity(evidence.DriveTotalSizeBytes),
                HostAvailableSpace: PreflightOverviewFormatter.FormatCapacity(evidence.DriveAvailableFreeSpaceBytes),
                FilePath: evidence.FilePath);

    private static string? FirstNoticeFor(
        DashboardViewModel dashboard,
        ImmutableArray<string> notices)
    {
        var value = string.IsNullOrWhiteSpace(dashboard.ErrorMessage)
            ? notices.FirstOrDefault()
            : dashboard.ErrorMessage;
        return string.IsNullOrWhiteSpace(value)
            ? null
            : TuiDisplayText.Sanitize(value, 96);
    }

    private static string SanitizeProfileName(string profileTitle)
    {
        var withoutPrefix = profileTitle.Replace("档案：", string.Empty, StringComparison.Ordinal);
        return TuiDisplayText.Sanitize(withoutPrefix, 64);
    }

    private static string FormatConclusion(AutomaticPreflightStatus status) => status switch
    {
        AutomaticPreflightStatus.Ready => "✓ 预检通过",
        AutomaticPreflightStatus.Attention or AutomaticPreflightStatus.Stale => "! 预检未通过",
        AutomaticPreflightStatus.Failed => "× 预检失败",
        AutomaticPreflightStatus.Checking => "◌ 预检进行中",
        _ => "◌ 尚未预检"
    };

    private static string FormatReason(
        AutomaticPreflightState state,
        string? firstNotice,
        int noticeCount) => state.Status switch
        {
        AutomaticPreflightStatus.Checking =>
                $"正在核对目标、快照与运行状态{PreflightOverviewFormatter.FormatCheckingElapsed(state.Elapsed)}。",
            AutomaticPreflightStatus.Ready when noticeCount == 0 => "5 项检查通过，未发现阻断项。",
            AutomaticPreflightStatus.Ready => $"预检完成；{noticeCount} 项需要处理。",
            AutomaticPreflightStatus.Attention when firstNotice is not null =>
                firstNotice,
            AutomaticPreflightStatus.Attention => "预检结果不完整。",
            AutomaticPreflightStatus.Failed =>
                TuiDisplayText.Sanitize(state.Message, 96) is { Length: > 0 } message
                    ? message
                    : "预检数据读取失败。",
            AutomaticPreflightStatus.Stale =>
                TuiDisplayText.Sanitize(state.Message, 96) is { Length: > 0 } staleMessage
                    ? staleMessage
                    : "预检结果已过期，请重新运行。",
            _ => "尚未运行只读预检。"
        };

    private static string FormatNextStep(AutomaticPreflightStatus status) => status switch
    {
        AutomaticPreflightStatus.Checking => "等待预检完成",
        AutomaticPreflightStatus.Ready => "↓ 选择执行压缩查看影响范围",
        AutomaticPreflightStatus.Failed => "Enter 重试预检，日志页查看详情",
        _ => "Enter 重新运行只读预检"
    };
}

public static class PreflightOverviewFormatter
{
    public const long Gibibyte = 1024L * 1024L * 1024L;
    public const long Tebibyte = 1024L * Gibibyte;

    public static string FormatCapacity(long bytes)
    {
        if (bytes < 0)
        {
            return "未知";
        }

        var divisor = bytes >= Tebibyte ? Tebibyte : Gibibyte;
        var unit = bytes >= Tebibyte ? "TiB" : "GiB";
        var value = bytes / (double)divisor;
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{value:0.00} {unit}");
    }

    public static string FormatVhdxPath(string? path) => FormatVhdxPath(path, 80);

    public static string FormatVhdxPath(string? path, int maxCells)
    {
        if (maxCells <= 0)
        {
            return string.Empty;
        }

        var normalized = TuiDisplayText.Sanitize(path, 160);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return string.Empty;
        }

        normalized = normalized.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)
            ? @"\\" + normalized[8..]
            : normalized.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase)
                ? normalized[4..]
                : normalized;

        if (normalized.Length <= maxCells)
        {
            return normalized;
        }

        try
        {
            var parts = normalized.Split(
                ['\\', '/'],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length > 2)
            {
                var suffix = string.Join('\\', parts.TakeLast(2));
                var best = string.Empty;
                for (var prefixLength = 1; prefixLength < parts.Length - 1; prefixLength++)
                {
                    var prefix = string.Join('\\', parts.Take(prefixLength));
                    var candidate = $"{prefix}\\…\\{suffix}";
                    if (candidate.Length > maxCells)
                    {
                        break;
                    }

                    best = candidate;
                }

                if (!string.IsNullOrWhiteSpace(best))
                {
                    return best;
                }
            }
        }
        catch (Exception)
        {
            // Fall through to the already sanitized, bounded value.
        }

        return TuiDisplayText.Sanitize(normalized, maxCells);
    }

    public static string FormatSparseState(bool? isSparse) => isSparse switch
    {
        true => "是",
        false => "否",
        _ => "未知"
    };

    public static string FormatGateStatus(PreflightGateStatus status) => status switch
    {
        PreflightGateStatus.Matched => "✓ 已匹配",
        PreflightGateStatus.Attention => "! 需要关注",
        PreflightGateStatus.Failed => "× 检查失败",
        _ => "◌ 尚未检查"
    };

    public static string FormatGateSymbol(PreflightGateStatus status) => status switch
    {
        PreflightGateStatus.Matched => "✓",
        PreflightGateStatus.Attention => "!",
        PreflightGateStatus.Failed => "×",
        _ => "◌"
    };

    public static string FormatHomeDetailBadge(PreflightGateStatus status) => status switch
    {
        PreflightGateStatus.Matched => "通过",
        PreflightGateStatus.Attention => "需处理",
        PreflightGateStatus.Failed => "失败",
        _ => "待检查"
    };

    public static string FormatHomeDetailSymbol(PreflightGateStatus status) => status switch
    {
        PreflightGateStatus.Matched => "✓",
        PreflightGateStatus.Attention => "!",
        PreflightGateStatus.Failed => "×",
        _ => "•"
    };

    // A read-only preflight shells out to wsl.exe, which can stall for tens of
    // seconds. Surfacing the elapsed time is what separates "still working" from
    // "wedged" for the operator. Below one second there is nothing to show yet.
    private static readonly TimeSpan MinimumReportedElapsed = TimeSpan.FromSeconds(1);

    public static string FormatCheckingElapsed(TimeSpan? elapsed) =>
        elapsed is { } value && value >= MinimumReportedElapsed
            ? $"（已用 {((long)value.TotalSeconds).ToString(CultureInfo.InvariantCulture)} 秒）"
            : string.Empty;

    /// <summary>Status-line text for an in-flight read-only preflight.</summary>
    public static string FormatCheckingStatus(TimeSpan? elapsed) =>
        $"只读预检进行中{FormatCheckingElapsed(elapsed)}，完成后自动更新。";

    public static string FormatHomeStatusTitle(AutomaticPreflightStatus status) => status switch
    {
        AutomaticPreflightStatus.Ready => "预检通过",
        AutomaticPreflightStatus.Attention or AutomaticPreflightStatus.Stale => "预检未通过",
        AutomaticPreflightStatus.Failed => "预检失败",
        AutomaticPreflightStatus.Checking => "预检进行中",
        _ => "尚未预检"
    };

    public static string FormatHomeStatusReason(PreflightOverviewViewModel overview) =>
        overview.Status switch
        {
            AutomaticPreflightStatus.Ready when overview.NoticeCount == 0 =>
                "5 项已通过，未发现阻断项",
            AutomaticPreflightStatus.Ready =>
                $"{overview.NoticeCount} 项需要处理",
            AutomaticPreflightStatus.Checking =>
                $"正在读取目标映射、VHDX 快照、运行实例和日志{FormatCheckingElapsed(overview.Elapsed)}",
            AutomaticPreflightStatus.Attention or AutomaticPreflightStatus.Stale
                when overview.FirstNotice is not null => overview.FirstNotice,
            AutomaticPreflightStatus.Failed when overview.FirstNotice is not null => overview.FirstNotice,
            AutomaticPreflightStatus.Stale => "预检结果已过期，请重新运行",
            AutomaticPreflightStatus.Failed => "预检数据读取失败",
            _ => "尚未运行只读预检"
        };

    public static ImmutableArray<PreflightHomeDetailViewModel> CreateHomeDetails(
        PreflightOverviewViewModel overview)
    {
        ArgumentNullException.ThrowIfNull(overview);

        var vhdx = Gate(overview, "VHDX 快照");
        var running = Gate(overview, "运行实例");
        var logs = Gate(overview, "日志可用性");
        var mapping = Gate(overview, "注册表 / Lxss 映射");
        var snapshotStatus = CombineStatuses(vhdx.Status, running.Status, logs.Status);
        var mappingStatus = IsMissingTargetNotice(overview.FirstNotice)
            ? PreflightGateStatus.Attention
            : mapping.Status;
        var finalStatus = overview.Status == AutomaticPreflightStatus.Ready
            ? PreflightGateStatus.Matched
            : overview.Status == AutomaticPreflightStatus.Failed
                ? PreflightGateStatus.Failed
                : PreflightGateStatus.NotChecked;

        return ImmutableArray.Create(
            new PreflightHomeDetailViewModel(
                "目标档案已读取",
                "档案结构有效，可用于后续压缩流程。",
                PreflightGateStatus.Matched),
            new PreflightHomeDetailViewModel(
                "VHDX 已配置",
                overview.VhdxConfigured
                    ? "目标磁盘路径已解析。"
                    : "目标磁盘路径尚未配置。",
                overview.VhdxConfigured ? vhdx.Status : PreflightGateStatus.Attention),
            new PreflightHomeDetailViewModel(
                "快照与日志可用",
                $"{SnapshotDetail(overview, vhdx, running, logs)}",
                snapshotStatus),
            new PreflightHomeDetailViewModel(
                "发行版映射",
                FormatMappingDetail(overview),
                mappingStatus),
            new PreflightHomeDetailViewModel(
                "执行前最终校验",
                finalStatus == PreflightGateStatus.Matched
                    ? "所有执行前检查已通过。"
                    : "将在阻断项解决后自动进行。",
                finalStatus));
    }

    public static string FormatHomeAlertTitle(PreflightHomeViewModel home) =>
        home.Status switch
        {
            AutomaticPreflightStatus.Ready => "✓ 已通过",
            AutomaticPreflightStatus.Checking => "◌ 预检进行中",
            AutomaticPreflightStatus.Failed => "× 检查失败",
            AutomaticPreflightStatus.Attention or AutomaticPreflightStatus.Stale => "! 需要处理",
            _ => "◌ 等待预检"
        };

    public static string FormatHomeAlertReason(PreflightHomeViewModel home)
    {
        if (home.StatusReason == "目标发行版未安装")
        {
            return $"目标发行版尚未安装：{home.DistroName}";
        }

        if (home.StatusReason == "目标映射不匹配")
        {
            return $"发行版映射失败：{home.DistroName}";
        }

        return home.StatusReason;
    }

    public static string FormatHomeAlertSupport(PreflightHomeViewModel home) =>
        home.Status switch
        {
            AutomaticPreflightStatus.Ready => "所有执行前检查已通过，可以进入压缩影响预览。",
            AutomaticPreflightStatus.Checking => "正在读取目标映射、VHDX 快照、运行状态和日志。",
            AutomaticPreflightStatus.Failed => "检查结果不完整；请按 R 重试并查看日志。",
            AutomaticPreflightStatus.Attention or AutomaticPreflightStatus.Stale =>
                "该问题会阻止压缩执行；其余已通过项无需重复处理。",
            _ => "按 R 运行只读预检，先确认目标和 VHDX 状态。"
        };

    public static string FormatHomeActionTitle(PreflightHomeViewModel home) =>
        home.Status switch
        {
            AutomaticPreflightStatus.Ready => "下一步：进入 02 执行压缩",
            AutomaticPreflightStatus.Checking => "下一步：等待预检完成",
            AutomaticPreflightStatus.Failed => "下一步：重试预检并查看日志",
            AutomaticPreflightStatus.Attention or AutomaticPreflightStatus.Stale
                when home.StatusReason == "目标发行版未安装" => "下一步：安装 / 修正目标发行版",
            AutomaticPreflightStatus.Attention or AutomaticPreflightStatus.Stale => "下一步：处理阻断项",
            _ => "下一步：运行只读预检"
        };

    public static string FormatHomeActionHint(PreflightHomeViewModel home) =>
        home.Status == AutomaticPreflightStatus.Ready
            ? "查看影响范围"
            : home.Status == AutomaticPreflightStatus.Checking
                ? "检查完成后自动更新"
                : "完成后按 R 重新检查";

    public static string FormatHomeBlocker(PreflightHomeViewModel home)
    {
        if (home.StatusReason == "目标发行版未安装")
        {
            return $"发行版映射失败 — 未发现 {home.DistroName} 的已安装实例";
        }

        if (home.Status == AutomaticPreflightStatus.Ready)
        {
            return "当前没有阻断项";
        }

        return home.StatusReason;
    }

    public static string FormatHomeNextPriority(PreflightHomeViewModel home) =>
        home.Status switch
        {
            AutomaticPreflightStatus.Ready => "优先级 P2",
            AutomaticPreflightStatus.Checking => "进行中",
            _ => "优先级 P1"
        };

    public static string FormatHomeNextMain(PreflightHomeViewModel home) =>
        home.Status switch
        {
            AutomaticPreflightStatus.Ready => "进入“02 执行压缩”，先查看影响范围。",
            AutomaticPreflightStatus.Checking => "等待预检完成，再决定下一步。",
            AutomaticPreflightStatus.Failed => "按 R 重试预检，并在“02 日志归档”查看详情。",
            AutomaticPreflightStatus.Attention or AutomaticPreflightStatus.Stale
                when home.StatusReason == "目标发行版未安装" =>
                "先修复“目标发行版未安装”，再重新预检。",
            AutomaticPreflightStatus.Attention or AutomaticPreflightStatus.Stale =>
                "先处理当前阻断项，再重新预检。",
            _ => "按 R 运行只读预检，确认执行前状态。"
        };

    public static string FormatHomeNextSupport(PreflightHomeViewModel home) =>
        home.Status switch
        {
            AutomaticPreflightStatus.Ready => "所有执行前检查已通过；下一步只展示影响范围，不会立即启动压缩。",
            AutomaticPreflightStatus.Checking => "检查完成后，页面会保留已经确认的数据。",
            AutomaticPreflightStatus.Failed => "保留当前诊断上下文；重试后仍有问题时打开日志分析。",
            _ => "预检通过后，“执行压缩”才会解锁。已确认的数据不会重复采集。"
        };

    private static PreflightGateViewModel Gate(
        PreflightOverviewViewModel overview,
        string label) =>
        overview.Gates.First(gate => gate.Label == label);

    private static PreflightGateStatus CombineStatuses(params PreflightGateStatus[] statuses)
    {
        if (statuses.Any(status => status == PreflightGateStatus.Failed))
        {
            return PreflightGateStatus.Failed;
        }

        if (statuses.Any(status => status == PreflightGateStatus.Attention))
        {
            return PreflightGateStatus.Attention;
        }

        if (statuses.Any(status => status == PreflightGateStatus.NotChecked))
        {
            return PreflightGateStatus.NotChecked;
        }

        return PreflightGateStatus.Matched;
    }

    private static string SnapshotDetail(
        PreflightOverviewViewModel overview,
        PreflightGateViewModel vhdx,
        PreflightGateViewModel running,
        PreflightGateViewModel logs)
    {
        if (vhdx.Status == PreflightGateStatus.Matched &&
            running.Status == PreflightGateStatus.Matched &&
            logs.Status == PreflightGateStatus.Matched)
        {
            return $"已采集 VHDX 快照，发现 {overview.RunningCount} 个系统运行发行版。";
        }

        return CombineStatuses(vhdx.Status, running.Status, logs.Status) switch
        {
            PreflightGateStatus.Failed => "VHDX 快照、运行状态或日志读取失败。",
            PreflightGateStatus.Attention => "VHDX 快照、运行状态或日志需要处理。",
            _ => "等待读取 VHDX 快照、运行状态和日志。"
        };
    }

    private static string FormatMappingDetail(PreflightOverviewViewModel overview)
    {
        if (IsMissingTargetNotice(overview.FirstNotice))
        {
            return $"目标 {overview.DistroName} 未安装，暂不进入执行阶段。";
        }

        if (overview.FirstNotice == "目标映射不匹配")
        {
            return $"目标 {overview.DistroName} 与当前 VHDX 映射不一致。";
        }

        return Gate(overview, "注册表 / Lxss 映射").Status switch
        {
            PreflightGateStatus.Matched => "目标发行版与 VHDX 映射一致。",
            PreflightGateStatus.Failed => "目标映射读取失败。",
            _ => "等待读取目标映射。"
        };
    }

    private static bool IsMissingTargetNotice(string? notice) =>
        string.Equals(notice, "目标发行版未安装", StringComparison.Ordinal);

    public static string FormatHomeStatusMeta(
        int matchedCount,
        int attentionCount,
        int pendingCount,
        int failedCount)
    {
        var parts = new List<string>(capacity: 3);
        if (matchedCount > 0) parts.Add($"{matchedCount} 项已通过");
        if (attentionCount > 0) parts.Add($"{attentionCount} 项需处理");
        if (failedCount > 0) parts.Add($"{failedCount} 项失败");
        if (pendingCount > 0) parts.Add($"{pendingCount} 项待检查");
        return parts.Count == 0 ? "尚未开始" : string.Join(" · ", parts);
    }

    public static string FormatHomeCompactStatusMeta(
        ImmutableArray<PreflightHomeCheckViewModel> checks)
    {
        var matchedCount = checks.Count(check => check.Status == PreflightGateStatus.Matched);
        var attentionCount = checks.Count(check => check.Status == PreflightGateStatus.Attention);
        var failedCount = checks.Count(check => check.Status == PreflightGateStatus.Failed);
        var pendingCount = checks.Count(check => check.Status == PreflightGateStatus.NotChecked);
        var parts = new List<string>(capacity: 3);
        if (matchedCount > 0) parts.Add($"{matchedCount}通过");
        if (attentionCount > 0) parts.Add($"{attentionCount}处理");
        if (failedCount > 0) parts.Add($"{failedCount}失败");
        if (pendingCount > 0) parts.Add($"{pendingCount}待查");
        return parts.Count == 0 ? "尚未开始" : string.Join(" · ", parts);
    }

    public static string FormatHomeGateLabel(string label) => label switch
    {
        "注册表 / Lxss 映射" => "目标映射",
        "VHDX 快照" => "VHDX 快照",
        "运行实例" => "运行状态",
        "日志可用性" => "运行日志",
        "通知" => "关注项",
        _ => label
    };

    public static string FormatHomeGateDetail(
        PreflightGateViewModel gate,
        PreflightOverviewViewModel overview)
    {
        if (gate.Label == "VHDX 快照" && gate.Status == PreflightGateStatus.Matched)
        {
            return $"已采集 · {overview.Evidence.FileSize}";
        }

        if (gate.Label == "运行实例" && gate.Status == PreflightGateStatus.Matched)
        {
            return overview.RunningCount == 0
                ? "无运行实例"
                : $"{overview.RunningCount} 个运行实例";
        }

        if (gate.Label == "通知")
        {
            return overview.NoticeCount == 0
                ? gate.Status == PreflightGateStatus.NotChecked ? "尚未检查" : "无关注项"
                : $"{overview.NoticeCount} 条 · {TuiDisplayText.Sanitize(overview.FirstNotice, 64)}";
        }

        return gate.Detail;
    }

    public static string FormatHomeImpact(PreflightOverviewViewModel overview) =>
        overview.Gates.FirstOrDefault(gate => gate.Label == "运行实例") is { } running
            ? running.Status switch
            {
                PreflightGateStatus.Matched when overview.RunningCount == 0 => "系统中无运行发行版",
                PreflightGateStatus.Matched => $"系统中运行 {overview.RunningCount} 个发行版",
                PreflightGateStatus.Failed => "运行实例读取失败",
                _ => "运行实例尚未检查"
            }
            : "运行实例尚未检查";

    public static string FormatHomePendingChecks(
        ImmutableArray<PreflightHomeCheckViewModel> checks)
    {
        var pending = checks
            .Where(check => check.Status == PreflightGateStatus.NotChecked)
            .Select(check => check.Label)
            .ToArray();
        return pending.Length == 0 ? "无" : string.Join("、", pending);
    }

    public static string FormatHomeReadSummary(PreflightOverviewViewModel overview)
    {
        var values = new List<string>(capacity: 3);
        var vhdx = overview.Gates.FirstOrDefault(gate => gate.Label == "VHDX 快照");
        values.Add(vhdx?.Status == PreflightGateStatus.Matched
            ? "VHDX 快照已采集"
            : vhdx?.Status == PreflightGateStatus.Failed
                ? "VHDX 快照读取失败"
                : "VHDX 快照未采集");

        var running = overview.Gates.FirstOrDefault(gate => gate.Label == "运行实例");
        values.Add(running?.Status == PreflightGateStatus.Matched
            ? $"系统运行 {overview.RunningCount} 个发行版"
            : running?.Status == PreflightGateStatus.Failed
                ? "运行状态读取失败"
                : "运行状态未读取");

        var logs = overview.Gates.FirstOrDefault(gate => gate.Label == "日志可用性");
        values.Add(logs?.Status == PreflightGateStatus.Matched
            ? "日志可用"
            : logs?.Status == PreflightGateStatus.Failed
                ? "日志不可用"
                : "日志未读取");
        return string.Join(" · ", values);
    }

    public static string FormatHomeEvidence(PreflightEvidenceViewModel evidence) =>
        evidence.IsAvailable
            ? $"VHDX {evidence.FileSize} · 宿主盘可用 {evidence.HostAvailableSpace} / {evidence.HostTotalSize}"
            : "VHDX 快照尚未采集";

    public static string FormatHomeNextStep(PreflightOverviewViewModel overview)
    {
        if (overview.Status == AutomaticPreflightStatus.Checking)
        {
            return "等待预检完成";
        }

        if (overview.Status == AutomaticPreflightStatus.Ready)
        {
            return "02 执行压缩，查看影响范围";
        }

        if (overview.FirstNotice is not null)
        {
            return overview.FirstNotice switch
            {
                "目标发行版未安装" => "03 目标档案，核对发行版后按 R 重跑",
                "目标映射不匹配" => "03 目标档案，核对发行版与 VHDX 后按 R 重跑",
                "目标 VHDX 不存在" => "03 目标档案，配置 VHDX 后按 R 重跑",
                "稀疏状态未知" => "按 R 重跑预检；仍未知时查看 02 日志归档",
                "运行日志不可用" => "02 日志归档，检查日志后按 R 重跑",
                _ => "按 R 重跑预检"
            };
        }

        return overview.Status == AutomaticPreflightStatus.Failed
            ? "按 R 重试预检；选择 02 日志归档"
            : "按 R 运行只读预检";
    }

    public static ImmutableArray<PreflightTargetRowViewModel> CreateTargetRows(
        PreflightOverviewViewModel overview,
        int selectedTargetIndex,
        bool targetLocked)
    {
        ArgumentNullException.ThrowIfNull(overview);

        var installed = overview.InstalledDistros.IsDefault
            ? ImmutableArray<WslDistribution>.Empty
            : overview.InstalledDistros;
        var candidates = installed
            .Where(static distribution => !string.IsNullOrWhiteSpace(distribution.Name))
            .GroupBy(static distribution => distribution.Name, StringComparer.OrdinalIgnoreCase)
            .Select(static group => group.First())
            .ToList();
        if (candidates.Count == 0)
        {
            return ImmutableArray<PreflightTargetRowViewModel>.Empty;
        }

        var boundedIndex = Math.Clamp(selectedTargetIndex, 0, candidates.Count - 1);
        return candidates
            .Select((distribution, index) => CreateTargetRow(
                overview,
                distribution,
                index == boundedIndex,
                targetLocked && index == boundedIndex))
            .ToImmutableArray();
    }

    public static PreflightTargetDetailViewModel CreateTargetDetail(
        PreflightOverviewViewModel overview,
        PreflightHomeViewModel home)
    {
        ArgumentNullException.ThrowIfNull(overview);
        ArgumentNullException.ThrowIfNull(home);

        var selected = home.Targets.FirstOrDefault(row => row.IsSelected)
            ?? home.Targets.FirstOrDefault();
        var selectedDistribution = selected is null
            ? null
            : overview.InstalledDistros.FirstOrDefault(distribution =>
                string.Equals(
                    distribution.Name,
                    selected.DistroName,
                    StringComparison.OrdinalIgnoreCase));
        var isConfiguredTarget = selected is not null && string.Equals(
            selected.DistroName,
            overview.DistroName,
            StringComparison.OrdinalIgnoreCase);
        var isRunning = selectedDistribution?.State == WslDistributionState.Running;
        var vhdxPath = selectedDistribution?.VhdxPath;
        if (string.IsNullOrWhiteSpace(vhdxPath) && isConfiguredTarget)
        {
            vhdxPath = string.IsNullOrWhiteSpace(overview.Evidence.FilePath)
                ? overview.ConfiguredVhdxPath
                : overview.Evidence.FilePath;
        }
        var hasVhdxPath = !string.IsNullOrWhiteSpace(vhdxPath);

        var checks = CreateTargetChecks(
                overview,
                selected,
                selectedDistribution,
                isConfiguredTarget,
                hasVhdxPath,
                isRunning)
            .ToImmutableArray();
        var blockerCount = checks.Count(check => check.Status != PreflightGateStatus.Matched);
        var isReady = selected is not null &&
            overview.Status is not (AutomaticPreflightStatus.Idle or AutomaticPreflightStatus.Checking or AutomaticPreflightStatus.Failed or AutomaticPreflightStatus.Stale) &&
            blockerCount == 0;
        var statusCode = isReady
            ? "✓ PASS"
            : overview.Status == AutomaticPreflightStatus.Failed
                ? "× FAIL"
                : overview.Status is AutomaticPreflightStatus.Idle or AutomaticPreflightStatus.Checking or AutomaticPreflightStatus.Stale
                    ? "◌ CHECK"
                    : "! BLOCKED";

        return new PreflightTargetDetailViewModel(
            selected?.DistroName ?? overview.DistroName,
            selected?.CurrentSize ?? overview.Evidence.FileSize,
            FormatVhdxPath(vhdxPath),
            selected is null ? "待检查" : FormatDetailTargetStatus(selected.Status),
            statusCode,
            isReady
                ? "5 项已通过，未发现阻断项"
                : blockerCount > 0
                    ? $"{blockerCount} 项检查需要处理"
                    : "预检尚未完成",
            isReady
                ? "所有执行前检查已通过，可以进入压缩影响预览。"
                : "完成当前检查后再进入压缩预览。",
            isReady
                ? "下一步：[Enter] 预览压缩"
                : "下一步：处理检查项后按 R 重扫",
            checks,
            blockerCount);
    }

    private static IEnumerable<PreflightTargetCheckViewModel> CreateTargetChecks(
        PreflightOverviewViewModel overview,
        PreflightTargetRowViewModel? selected,
        WslDistribution? selectedDistribution,
        bool isConfiguredTarget,
        bool hasVhdxPath,
        bool isRunning)
    {
        var isChecking = overview.Status is AutomaticPreflightStatus.Idle or AutomaticPreflightStatus.Checking or AutomaticPreflightStatus.Stale;
        var mapping = Gate(overview, "注册表 / Lxss 映射");
        var vhdx = Gate(overview, "VHDX 快照");
        var logs = Gate(overview, "日志可用性");

        var targetStatus = selected is null
            ? PreflightGateStatus.Attention
            : isChecking
                ? PreflightGateStatus.NotChecked
                : PreflightGateStatus.Matched;
        var vhdxStatus = !hasVhdxPath
            ? PreflightGateStatus.Attention
            : isChecking
                ? PreflightGateStatus.NotChecked
                : isConfiguredTarget
                    ? vhdx.Status
                    : PreflightGateStatus.NotChecked;
        var storageStatus = !hasVhdxPath
            ? PreflightGateStatus.Attention
            : isChecking
                ? PreflightGateStatus.NotChecked
                : !isConfiguredTarget
                    ? PreflightGateStatus.NotChecked
                    : logs.Status == PreflightGateStatus.Failed
                        ? PreflightGateStatus.Failed
                        : logs.Status == PreflightGateStatus.NotChecked
                            ? PreflightGateStatus.NotChecked
                            : PreflightGateStatus.Matched;
        var mappingStatus = selected is null
            ? PreflightGateStatus.Attention
            : isChecking
                ? PreflightGateStatus.NotChecked
                : isConfiguredTarget
                    ? mapping.Status
                    : PreflightGateStatus.NotChecked;
        var finalStatus = selected is null
            ? PreflightGateStatus.Attention
            : isChecking
                ? PreflightGateStatus.NotChecked
                : overview.Status == AutomaticPreflightStatus.Failed
                    ? PreflightGateStatus.Failed
                    : isRunning
                        ? PreflightGateStatus.Attention
                        : !isConfiguredTarget
                            ? PreflightGateStatus.NotChecked
                            : PreflightGateStatus.Matched;

        yield return new PreflightTargetCheckViewModel(
            FormatTargetCheckLabel("目标档案已读取"),
            selected is null ? "未在当前 WSL 清单中找到。" : "实例已从当前清单读取。",
            targetStatus);
        yield return new PreflightTargetCheckViewModel(
            FormatTargetCheckLabel("VHDX 已配置"),
            hasVhdxPath ? "目标 VHDX 路径已解析。" : "目标没有可用的 VHDX 路径。",
            vhdxStatus);
        yield return new PreflightTargetCheckViewModel(
            FormatTargetCheckLabel("快照与日志可用"),
            storageStatus == PreflightGateStatus.Matched
                ? selectedDistribution?.VhdxSizeBytes is not null
                    ? "VHDX 路径和日志均可用。"
                    : "VHDX 路径和日志均可用，体积待采集。"
                : !isConfiguredTarget && !isChecking
                    ? "当前锁定实例尚未完成快照与日志预检。"
                    : logs.Detail,
            storageStatus);
        yield return new PreflightTargetCheckViewModel(
            FormatTargetCheckLabel("发行版映射"),
            isConfiguredTarget
                ? mapping.Detail
                : "当前锁定实例尚未完成发行版映射预检。",
            mappingStatus);
        yield return new PreflightTargetCheckViewModel(
            FormatTargetCheckLabel("执行前最终校验"),
            finalStatus == PreflightGateStatus.Matched
                ? "目标已锁定，可进入影响评估。"
                : isRunning
                    ? "实例正在运行，请先停止目标。"
                    : !isConfiguredTarget && !isChecking
                        ? "锁定目标后将运行该实例的完整预检。"
                        : "完成阻断项后重新检查。",
            finalStatus);
    }

    private static string FormatTargetCheckLabel(string title) => title switch
    {
        "发行版映射" => "发行版映射匹配",
        "执行前最终校验" => "无进程独占锁定",
        _ => title
    };

    public static string FormatDetailTargetStatus(PreflightTargetRowStatus status) => status switch
    {
        PreflightTargetRowStatus.Ready => "Ready ✓",
        PreflightTargetRowStatus.Running => "Running ⚠",
        PreflightTargetRowStatus.Attention => "Blocked !",
        PreflightTargetRowStatus.Failed => "Failed ×",
        _ => "Checking …"
    };

    private static PreflightTargetRowViewModel CreateTargetRow(
        PreflightOverviewViewModel overview,
        WslDistribution distribution,
        bool isSelected,
        bool isLocked)
    {
        var isTarget = string.Equals(
            distribution.Name,
            overview.DistroName,
            StringComparison.OrdinalIgnoreCase);
        var currentSize = distribution.VhdxSizeBytes is { } sizeBytes
            ? PreflightOverviewFormatter.FormatCapacity(sizeBytes)
            : isTarget && overview.Evidence.IsAvailable
                ? overview.Evidence.FileSize
                : "尚未采集";
        var evidencePath = distribution.VhdxPath;
        if (string.IsNullOrWhiteSpace(evidencePath) &&
            isTarget &&
            overview.Evidence.IsAvailable)
        {
            evidencePath = overview.Evidence.FilePath;
        }

        if (string.IsNullOrWhiteSpace(evidencePath) && isTarget)
        {
            evidencePath = overview.ConfiguredVhdxPath;
        }

        var vhdx = PreflightOverviewFormatter.FormatVhdxPath(evidencePath);
        if (string.IsNullOrWhiteSpace(vhdx))
        {
            vhdx = isTarget
                ? overview.VhdxConfigured ? "已配置" : "未配置"
                : "未读取";
        }
        var status = GetTargetRowStatus(
            overview,
            distribution,
            isTarget,
            !string.IsNullOrWhiteSpace(evidencePath));

        return new PreflightTargetRowViewModel(
            TuiDisplayText.Sanitize(distribution.Name, 64),
            currentSize,
            vhdx,
            FormatTargetStatus(status),
            status,
            isSelected,
            isLocked);
    }

    private static PreflightTargetRowStatus GetTargetRowStatus(
        PreflightOverviewViewModel overview,
        WslDistribution distribution,
        bool isTarget,
        bool hasVhdxPath)
    {
        if (distribution.State == WslDistributionState.Running)
        {
            return PreflightTargetRowStatus.Running;
        }

        if (!isTarget)
        {
            return hasVhdxPath
                ? PreflightTargetRowStatus.Pending
                : PreflightTargetRowStatus.Attention;
        }

        var mapping = overview.Gates.FirstOrDefault(gate => gate.Label == "注册表 / Lxss 映射");
        var vhdx = overview.Gates.FirstOrDefault(gate => gate.Label == "VHDX 快照");
        if (mapping?.Status == PreflightGateStatus.Failed ||
            vhdx?.Status == PreflightGateStatus.Failed ||
            overview.Status == AutomaticPreflightStatus.Failed)
        {
            return PreflightTargetRowStatus.Failed;
        }

        if (mapping?.Status == PreflightGateStatus.Attention ||
            vhdx?.Status == PreflightGateStatus.Attention ||
            overview.FirstNotice == "目标发行版未安装")
        {
            return PreflightTargetRowStatus.Attention;
        }

        if (mapping?.Status != PreflightGateStatus.Matched ||
            vhdx?.Status != PreflightGateStatus.Matched)
        {
            return PreflightTargetRowStatus.Pending;
        }

        return PreflightTargetRowStatus.Ready;
    }

    public static string FormatTargetStatus(PreflightTargetRowStatus status) => status switch
    {
        PreflightTargetRowStatus.Ready => "READY ✓",
        PreflightTargetRowStatus.Running => "RUNNING ⚠",
        PreflightTargetRowStatus.Attention => "BLOCKED !",
        PreflightTargetRowStatus.Failed => "FAILED ×",
        _ => "CHECKING …"
    };

    public static string FormatRunningSummary(ImmutableArray<string> runningDistros)
    {
        if (runningDistros.IsDefaultOrEmpty)
        {
            return "0 个（无运行实例）";
        }

        var names = runningDistros
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Take(4)
            .Select(name => TuiDisplayText.Sanitize(name, 32))
            .ToArray();
        if (names.Length == 0)
        {
            return $"{runningDistros.Length} 个";
        }

        var suffix = runningDistros.Length > names.Length
            ? $"、+{runningDistros.Length - names.Length} 个"
            : string.Empty;
        return $"{runningDistros.Length} 个：{BoundedNames(names)}{suffix}";
    }

    private static string BoundedNames(IEnumerable<string> names)
    {
        var builder = new StringBuilder();
        foreach (var name in names)
        {
            var separator = builder.Length == 0 ? string.Empty : "、";
            var candidate = builder.ToString() + separator + name;
            var bounded = TuiDisplayText.Sanitize(candidate, 72);
            if (!string.Equals(candidate, bounded, StringComparison.Ordinal))
            {
                return bounded;
            }

            builder.Append(separator).Append(name);
        }

        return builder.ToString();
    }
}
