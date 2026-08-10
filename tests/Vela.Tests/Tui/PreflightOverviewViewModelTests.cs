using Vela.Core.Models;
using Vela.Tui.Application;
using Vela.Tui.Menu;

namespace Vela.Tests.Tui;

public sealed class PreflightOverviewViewModelTests
{
    [Fact]
    public void Create_uses_fixed_gate_order_and_projects_read_only_evidence()
    {
        var profile = CreateProfile();
        var dashboard = new DashboardViewModel(
            MainMenu.ApplicationTitle,
            "档案：Ubuntu 24.04",
            "Ubuntu-24.04",
            TargetConfigured: true,
            TargetMappingState.Matched,
            TargetInspectionState.Available,
            new VhdxEvidenceViewModel(
                FileLengthBytes: 1_610_612_736,
                LastWriteUtc: DateTimeOffset.Parse("2026-08-10T00:00:00Z"),
                IsSparse: true,
                DriveTotalSizeBytes: 2L * PreflightOverviewFormatter.Tebibyte,
                DriveAvailableFreeSpaceBytes: 512L * PreflightOverviewFormatter.Gibibyte),
            ImmutableArray.Create("Ubuntu-24.04"),
            ImmutableArray<string>.Empty,
            ErrorMessage: null,
            LogsAvailable: true,
            RunningInventoryState: PreflightDataState.Available,
            LogAvailabilityState: PreflightDataState.Available);
        var state = new AutomaticPreflightState(
            profile.Id,
            Generation: 1,
            Revision: 1,
            AutomaticPreflightStatus.Ready,
            dashboard,
            "预检已完成。");

        var overview = PreflightOverviewViewModel.Create(dashboard, state);

        Assert.Equal(
            ["注册表 / Lxss 映射", "VHDX 快照", "运行实例", "日志可用性", "通知"],
            overview.Gates.Select(gate => gate.Label));
        Assert.All(overview.Gates, gate => Assert.Equal(PreflightGateStatus.Matched, gate.Status));
        Assert.Equal("1.50 GiB", overview.Evidence.FileSize);
        Assert.Equal("2.00 TiB", overview.Evidence.HostTotalSize);
        Assert.Equal("512.00 GiB", overview.Evidence.HostAvailableSpace);
        Assert.Equal("是", overview.Evidence.SparseState);
        Assert.Equal("1 个：Ubuntu-24.04", overview.RunningSummary);
        Assert.Equal("✓ 预检通过", overview.Conclusion);
        Assert.Contains("5 项检查通过", overview.ReasonSummary, StringComparison.Ordinal);
        Assert.Contains("选择执行压缩", overview.NextStep, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true, "是")]
    [InlineData(false, "否")]
    [InlineData(null, "未知")]
    public void Formatter_formats_sparse_state_without_leaking_native_values(
        bool? sparse,
        string expected)
    {
        Assert.Equal(expected, PreflightOverviewFormatter.FormatSparseState(sparse));
    }

    [Theory]
    [InlineData(0, "0.00 GiB")]
    [InlineData(1610612736, "1.50 GiB")]
    [InlineData(1099511627776, "1.00 TiB")]
    public void Formatter_uses_gib_and_tib_units(long bytes, string expected)
    {
        Assert.Equal(expected, PreflightOverviewFormatter.FormatCapacity(bytes));
    }

    [Fact]
    public void Create_maps_attention_failure_and_not_checked_gate_states()
    {
        var profile = CreateProfile();
        var dashboard = DashboardViewModel.CreateInitial(profile) with
        {
            MappingState = TargetMappingState.Mismatched,
            InspectionState = TargetInspectionState.Failed,
            RunningInventoryState = PreflightDataState.Failed,
            LogAvailabilityState = PreflightDataState.Failed,
            ErrorMessage = "目标映射不匹配",
            Notices = ImmutableArray.Create("稀疏状态未知")
        };
        var state = new AutomaticPreflightState(
            profile.Id,
            1,
            1,
            AutomaticPreflightStatus.Attention,
            dashboard,
            "预检已完成，发现需要关注的问题。");

        var overview = PreflightOverviewViewModel.Create(dashboard, state);

        Assert.Equal(
            [
                PreflightGateStatus.Attention,
                PreflightGateStatus.Failed,
                PreflightGateStatus.Failed,
                PreflightGateStatus.Failed,
                PreflightGateStatus.Attention
            ],
            overview.Gates.Select(gate => gate.Status));
        Assert.Equal(2, overview.NoticeCount);
        Assert.Equal("目标映射不匹配", overview.FirstNotice);
        Assert.Contains("重新运行", overview.NextStep, StringComparison.Ordinal);
    }

    [Fact]
    public void Home_projection_prioritizes_decision_context_over_internal_gate_names()
    {
        var profile = CreateProfile();
        var dashboard = new DashboardViewModel(
            MainMenu.ApplicationTitle,
            "档案：Ubuntu 24.04",
            "Ubuntu-24.04",
            TargetConfigured: true,
            TargetMappingState.NotChecked,
            TargetInspectionState.Available,
            new VhdxEvidenceViewModel(
                FileLengthBytes: 1_610_612_736,
                LastWriteUtc: DateTimeOffset.UtcNow,
                IsSparse: null,
                DriveTotalSizeBytes: 2L * PreflightOverviewFormatter.Tebibyte,
                DriveAvailableFreeSpaceBytes: 512L * PreflightOverviewFormatter.Gibibyte),
            ImmutableArray.Create("Ubuntu-24.04", "docker-desktop", "podman-machine"),
            ImmutableArray.Create("目标发行版未安装", "稀疏状态未知"),
            "目标发行版未安装",
            LogsAvailable: true,
            RunningInventoryState: PreflightDataState.Available,
            LogAvailabilityState: PreflightDataState.Available);
        var state = new AutomaticPreflightState(
            profile.Id,
            1,
            1,
            AutomaticPreflightStatus.Attention,
            dashboard,
            "预检已完成，发现需要关注的问题。");

        var home = PreflightHomeViewModel.Create(
            PreflightOverviewViewModel.Create(dashboard, state));

        Assert.Equal("预检未通过", home.StatusTitle);
        Assert.Equal("目标发行版未安装", home.StatusReason);
        Assert.Equal("3 项已通过 · 1 项需处理 · 1 项待检查", home.StatusMeta);
        Assert.Equal("系统中运行 3 个发行版", home.ImpactSummary);
        Assert.Equal(
            ["目标映射", "VHDX 快照", "运行状态", "运行日志", "关注项"],
            home.Checks.Select(check => check.Label));
        Assert.DoesNotContain("注册表 / Lxss", string.Join(" ", home.Checks.Select(check => check.Label)), StringComparison.Ordinal);
        Assert.Equal("目标映射", home.PendingSummary);
        Assert.Equal("VHDX 快照已采集 · 系统运行 3 个发行版 · 日志可用", home.ReadSummary);
        Assert.Equal("VHDX 1.50 GiB · 宿主盘可用 512.00 GiB / 2.00 TiB", home.EvidenceSummary);
        Assert.Equal("03 目标档案，核对发行版后按 R 重跑", home.NextStep);
    }

    [Fact]
    public void Home_projection_uses_a_clear_ready_message_and_compact_running_impact()
    {
        var profile = CreateProfile();
        var dashboard = DashboardViewModel.CreateInitial(profile) with
        {
            MappingState = TargetMappingState.Matched,
            InspectionState = TargetInspectionState.Available,
            VhdxEvidence = new VhdxEvidenceViewModel(
                1_610_612_736,
                DateTimeOffset.UtcNow,
                true,
                2L * PreflightOverviewFormatter.Tebibyte,
                512L * PreflightOverviewFormatter.Gibibyte),
            RunningDistros = ImmutableArray<string>.Empty,
            LogsAvailable = true,
            RunningInventoryState = PreflightDataState.Available,
            LogAvailabilityState = PreflightDataState.Available
        };
        var state = new AutomaticPreflightState(
            profile.Id,
            1,
            1,
            AutomaticPreflightStatus.Ready,
            dashboard,
            "预检已完成。");

        var home = PreflightHomeViewModel.Create(
            PreflightOverviewViewModel.Create(dashboard, state));

        Assert.Equal("预检通过", home.StatusTitle);
        Assert.Equal("5 项已通过，未发现阻断项", home.StatusReason);
        Assert.Equal("系统中无运行发行版", home.ImpactSummary);
        Assert.Equal("02 执行压缩，查看影响范围", home.NextStep);
        Assert.Equal("✓", home.Checks[^1].Symbol);
    }

    [Fact]
    public void Home_projection_builds_reference_dashboard_cards_from_facts()
    {
        var profile = CreateProfile();
        var dashboard = new DashboardViewModel(
            MainMenu.ApplicationTitle,
            "档案：Ubuntu 24.04",
            "Ubuntu-24.04",
            TargetConfigured: true,
            TargetMappingState.NotChecked,
            TargetInspectionState.Available,
            new VhdxEvidenceViewModel(
                1_610_612_736,
                DateTimeOffset.UtcNow,
                true,
                2L * PreflightOverviewFormatter.Tebibyte,
                512L * PreflightOverviewFormatter.Gibibyte),
            ImmutableArray.Create("Ubuntu-24.04", "docker-desktop", "podman-machine"),
            ImmutableArray.Create("目标发行版未安装"),
            "目标发行版未安装",
            LogsAvailable: true,
            RunningInventoryState: PreflightDataState.Available,
            LogAvailabilityState: PreflightDataState.Available);
        var state = new AutomaticPreflightState(
            profile.Id,
            1,
            1,
            AutomaticPreflightStatus.Attention,
            dashboard,
            "预检已完成，发现需要关注的问题。");

        var home = PreflightHomeViewModel.Create(
            PreflightOverviewViewModel.Create(dashboard, state));

        Assert.Equal(60, home.ProgressPercent);
        Assert.Equal(
            ["目标档案已读取", "VHDX 已配置", "快照与日志可用", "发行版映射", "执行前最终校验"],
            home.Details.Select(detail => detail.Title));
        Assert.Equal(
            [
                PreflightGateStatus.Matched,
                PreflightGateStatus.Matched,
                PreflightGateStatus.Matched,
                PreflightGateStatus.Attention,
                PreflightGateStatus.NotChecked
            ],
            home.Details.Select(detail => detail.Status));
        Assert.Equal("通过", home.Details[0].Badge);
        Assert.Equal("需处理", home.Details[3].Badge);
        Assert.Equal("待检查", home.Details[4].Badge);
        Assert.Equal("! 需要处理", PreflightOverviewFormatter.FormatHomeAlertTitle(home));
        Assert.Equal("目标发行版尚未安装：Ubuntu-24.04", PreflightOverviewFormatter.FormatHomeAlertReason(home));
        Assert.Equal(
            "该问题会阻止压缩执行；其余已通过项无需重复处理。",
            PreflightOverviewFormatter.FormatHomeAlertSupport(home));
        Assert.Equal("下一步：安装 / 修正目标发行版", PreflightOverviewFormatter.FormatHomeActionTitle(home));
        Assert.Equal("完成后按 R 重新检查", PreflightOverviewFormatter.FormatHomeActionHint(home));
    }

    [Fact]
    public void Home_projection_ready_state_exposes_execution_card_copy()
    {
        var profile = CreateProfile();
        var dashboard = DashboardViewModel.CreateInitial(profile) with
        {
            MappingState = TargetMappingState.Matched,
            InspectionState = TargetInspectionState.Available,
            VhdxEvidence = new VhdxEvidenceViewModel(
                1_610_612_736,
                DateTimeOffset.UtcNow,
                true,
                2L * PreflightOverviewFormatter.Tebibyte,
                512L * PreflightOverviewFormatter.Gibibyte),
            RunningInventoryState = PreflightDataState.Available,
            LogAvailabilityState = PreflightDataState.Available,
            LogsAvailable = true
        };
        var state = new AutomaticPreflightState(
            profile.Id,
            1,
            1,
            AutomaticPreflightStatus.Ready,
            dashboard,
            "预检已完成。");
        var home = PreflightHomeViewModel.Create(
            PreflightOverviewViewModel.Create(dashboard, state));

        Assert.Equal("✓ 已通过", PreflightOverviewFormatter.FormatHomeAlertTitle(home));
        Assert.Equal("下一步：进入 02 执行压缩", PreflightOverviewFormatter.FormatHomeActionTitle(home));
        Assert.Contains("查看影响范围", PreflightOverviewFormatter.FormatHomeActionHint(home), StringComparison.Ordinal);
        Assert.Contains("进入“02 执行压缩”", PreflightOverviewFormatter.FormatHomeNextMain(home), StringComparison.Ordinal);
        Assert.Contains("所有执行前检查已通过", PreflightOverviewFormatter.FormatHomeNextSupport(home), StringComparison.Ordinal);
    }

    [Fact]
    public void Home_projection_builds_instance_rows_from_installed_inventory()
    {
        var profile = CreateProfile();
        var dashboard = DashboardViewModel.CreateInitial(profile) with
        {
            MappingState = TargetMappingState.Matched,
            InspectionState = TargetInspectionState.Available,
            VhdxEvidence = new VhdxEvidenceViewModel(
                1_610_612_736,
                DateTimeOffset.UtcNow,
                true,
                2L * PreflightOverviewFormatter.Tebibyte,
                512L * PreflightOverviewFormatter.Gibibyte),
            InstalledDistros = ImmutableArray.Create(
                new Vela.Core.Contracts.WslDistribution(
                    "Ubuntu-24.04",
                    Vela.Core.Contracts.WslDistributionState.Stopped,
                    2,
                    true,
                    VhdxPath: @"D:\WSL\Ubuntu-24.04\ext4.vhdx",
                    VhdxSizeBytes: 124L * PreflightOverviewFormatter.Gibibyte),
                new Vela.Core.Contracts.WslDistribution(
                    "docker-desktop",
                    Vela.Core.Contracts.WslDistributionState.Running,
                    2,
                    false,
                    VhdxPath: @"D:\Docker\wsl\data\ext4.vhdx",
                    VhdxSizeBytes: 65L * PreflightOverviewFormatter.Gibibyte)),
            RunningInventoryState = PreflightDataState.Available,
            LogAvailabilityState = PreflightDataState.Available,
            LogsAvailable = true
        };
        var state = new AutomaticPreflightState(
            profile.Id,
            1,
            1,
            AutomaticPreflightStatus.Ready,
            dashboard,
            "预检已完成。");

        var home = PreflightHomeViewModel.Create(
            PreflightOverviewViewModel.Create(dashboard, state));

        Assert.Equal(["Ubuntu-24.04", "docker-desktop"], home.Targets.Select(row => row.DistroName));
        Assert.Equal(
            [PreflightTargetRowStatus.Ready, PreflightTargetRowStatus.Running],
            home.Targets.Select(row => row.Status));
        Assert.True(home.Targets[0].IsSelected);
        Assert.Equal("124.00 GiB", home.Targets[0].CurrentSize);
        Assert.Contains("ext4.vhdx", home.Targets[0].VhdxPath, StringComparison.Ordinal);
        Assert.Equal("65.00 GiB", home.Targets[1].CurrentSize);
        Assert.Contains("ext4.vhdx", home.Targets[1].VhdxPath, StringComparison.Ordinal);
        Assert.Equal("RUNNING ⚠", home.Targets[1].StatusText);
    }

    [Fact]
    public void Home_projection_keeps_missing_target_out_of_instance_count()
    {
        var profile = CreateProfile();
        var dashboard = DashboardViewModel.CreateInitial(profile) with
        {
            MappingState = TargetMappingState.NotFound,
            InspectionState = TargetInspectionState.Available,
            InstalledDistros = ImmutableArray.Create(
                new Vela.Core.Contracts.WslDistribution(
                    "docker-desktop",
                    Vela.Core.Contracts.WslDistributionState.Stopped,
                    2,
                    true)),
            Notices = ImmutableArray.Create("目标发行版未安装"),
            ErrorMessage = "目标发行版未安装"
        };
        var state = new AutomaticPreflightState(
            profile.Id,
            1,
            1,
            AutomaticPreflightStatus.Attention,
            dashboard,
            "预检已完成，发现需要关注的问题。");

        var home = PreflightHomeViewModel.Create(
            PreflightOverviewViewModel.Create(dashboard, state));

        Assert.Single(home.Targets);
        Assert.Equal("docker-desktop", home.Targets[0].DistroName);
        Assert.DoesNotContain(home.Targets, row => row.DistroName == profile.DistroName);
    }

    [Theory]
    [InlineData(AutomaticPreflightStatus.Checking, "等待预检完成")]
    [InlineData(AutomaticPreflightStatus.Attention, "重新运行只读预检")]
    [InlineData(AutomaticPreflightStatus.Failed, "重试预检")]
    public void Create_projects_status_specific_next_step(
        AutomaticPreflightStatus status,
        string expectedNextStep)
    {
        var profile = CreateProfile();
        var dashboard = DashboardViewModel.CreateInitial(profile);
        var state = new AutomaticPreflightState(
            profile.Id,
            1,
            1,
            status,
            status == AutomaticPreflightStatus.Failed ? null : dashboard,
            "预检状态。");

        var overview = PreflightOverviewViewModel.Create(dashboard, state);

        Assert.Contains(expectedNextStep, overview.NextStep, StringComparison.Ordinal);
    }

    private static Profile CreateProfile() => new(
        Guid.Parse("ed979041-296f-49fd-9aae-61ceacbb06c0"),
        "Ubuntu 24.04",
        "Ubuntu-24.04",
        "D:\\Vela\\ext4.vhdx",
        ShutdownMode.Global,
        TimeSpan.FromSeconds(45));
}
