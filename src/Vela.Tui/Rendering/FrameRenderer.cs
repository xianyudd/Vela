using System.Collections.Immutable;
using Spectre.Console;
using Spectre.Console.Rendering;
using Vela.Core.Models;
using Vela.Tui.Application;
using Vela.Tui.Menu;

namespace Vela.Tui.Rendering;

public sealed record TuiFrameViewModel
{
    public TuiFrameViewModel(
        MainMenuViewModel menu,
        DashboardViewModel dashboard,
        RunProgressViewModel progress,
        int selectedMenuIndex = 0,
        TuiPageViewModel? page = null)
    {
        Menu = menu ?? throw new ArgumentNullException(nameof(menu));
        Dashboard = dashboard ?? throw new ArgumentNullException(nameof(dashboard));
        Progress = progress ?? throw new ArgumentNullException(nameof(progress));
        SelectedMenuIndex = selectedMenuIndex;
        Page = page ?? new DashboardPageViewModel();
    }

    public MainMenuViewModel Menu { get; init; }

    public DashboardViewModel Dashboard { get; init; }

    public RunProgressViewModel Progress { get; init; }

    public int SelectedMenuIndex { get; init; }

    public TuiPageViewModel Page { get; init; }
}

public sealed class FrameRenderer
{
    public const int MinimumTerminalWidth = 80;
    public const int WideTerminalWidth = 120;
    public const int NarrowTerminalWidth = MinimumTerminalWidth;
    private const int LowTerminalHeight = 22;

    public string BuildMarkup(
        TuiFrameViewModel viewModel,
        int? terminalWidth = null,
        int? terminalHeight = null)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        var sections = Compose(viewModel, terminalWidth, terminalHeight);
        return string.Join(
            Environment.NewLine,
            sections.Header,
            BuildSectionHeading(sections.NavigationTitle, sections.Menu),
            BuildSectionHeading(sections.WorkspaceTitle, sections.Details),
            sections.Footer);
    }

    public IRenderable Build(TuiFrameViewModel viewModel) =>
        Build(viewModel, null, null);

    public IRenderable Build(
        TuiFrameViewModel viewModel,
        int? terminalWidth,
        int? terminalHeight = null)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        var sections = Compose(viewModel, terminalWidth, terminalHeight);
        var header = CreatePanel(sections.Header, border: true);
        var footer = CreatePanel(sections.Footer, border: false);

        if (terminalWidth is < MinimumTerminalWidth)
        {
            return new Layout("Root").SplitRows(
                new Layout("Header").Update(header),
                new Layout("Body").Update(CreatePanel(
                    sections.Details,
                    border: true,
                    sections.WorkspaceTitle)),
                new Layout("Footer").Update(footer));
        }

        if (terminalWidth is >= WideTerminalWidth)
        {
            var body = new Layout("Body").SplitColumns(
                new Layout("Navigation")
                    .Ratio(2)
                    .Update(CreatePanel(
                        sections.Menu,
                        border: true,
                        sections.NavigationTitle)),
                new Layout("Workspace")
                    .Ratio(5)
                    .Update(CreatePanel(
                        sections.Details,
                        border: true,
                        sections.WorkspaceTitle)));
            return new Layout("Root").SplitRows(
                new Layout("Header").Update(header),
                body,
                new Layout("Footer").Update(footer));
        }

        var compactBody = new Layout("Body").SplitRows(
            new Layout("Navigation").Update(CreatePanel(
                sections.Menu,
                border: true,
                sections.NavigationTitle)),
            new Layout("Workspace").Update(CreatePanel(
                sections.Details,
                border: true,
                sections.WorkspaceTitle)));
        return new Layout("Root").SplitRows(
            new Layout("Header").Update(header),
            compactBody,
            new Layout("Footer").Update(footer));
    }

    public void Render(IAnsiConsole console, TuiFrameViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(console);
        ArgumentNullException.ThrowIfNull(viewModel);
        console.Clear(true);
        console.Write(Build(viewModel, console.Profile.Width, console.Profile.Height));
    }

    public void RenderRedirected(IAnsiConsole console, TuiFrameViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(console);
        ArgumentNullException.ThrowIfNull(viewModel);
        console.Write(new Markup(
            BuildMarkup(viewModel, console.Profile.Width, console.Profile.Height) +
            Environment.NewLine));
    }

    private static RenderSections Compose(
        TuiFrameViewModel viewModel,
        int? terminalWidth,
        int? terminalHeight)
    {
        var minimum = terminalWidth is < MinimumTerminalWidth;
        var low = terminalHeight is < LowTerminalHeight;
        if (minimum)
        {
            return new RenderSections(
                BuildHeader(viewModel),
                "操作",
                string.Empty,
                PageTitle(viewModel.Page),
                BuildMinimumDetails(viewModel),
                BuildFooter(viewModel.Page));
        }

        var menu = BuildMenu(viewModel.Menu, viewModel.SelectedMenuIndex);
        var details = low
            ? string.Join(
                Environment.NewLine,
                BuildCompactDashboard(viewModel.Dashboard),
                BuildProgress(viewModel.Progress),
                BuildPage(viewModel.Page, maxRows: 4))
            : string.Join(
                Environment.NewLine,
                BuildDashboard(viewModel.Dashboard),
                BuildProgress(viewModel.Progress),
                BuildPage(viewModel.Page, maxRows: 10));
        return new RenderSections(
            BuildHeader(viewModel),
            "操作",
            menu,
            PageTitle(viewModel.Page),
            details,
            BuildFooter(viewModel.Page));
    }

    private static Panel CreatePanel(
        string markup,
        bool border,
        string? header = null)
    {
        var panel = new Panel(new Markup(markup))
        {
            Border = border ? BoxBorder.Square : BoxBorder.None,
            Expand = true,
            Padding = new Padding(1, 0)
        };
        if (header is not null)
        {
            panel.Header = new PanelHeader(
                $"[{VelaTheme.Section}] {Safe(header, 48)} [/]",
                Justify.Left);
        }

        return panel;
    }

    private static string BuildSectionHeading(string title, string content) =>
        string.IsNullOrEmpty(content)
            ? string.Empty
            : string.Join(
                Environment.NewLine,
                $"[bold {VelaTheme.Section}]{Safe(title, 48)}[/]",
                content);

    private static string BuildHeader(TuiFrameViewModel viewModel) =>
        $"[bold {VelaTheme.Focus}]{Safe(viewModel.Menu.Title, 64)}[/]" +
        $" [{VelaTheme.Muted}]·[/] [bold]{Safe(viewModel.Dashboard.ProfileTitle, 64)}[/]";

    private static string BuildMenu(MainMenuViewModel viewModel, int selectedIndex)
    {
        var items = viewModel.Items.IsDefault
            ? ImmutableArray<MainMenuItem>.Empty
            : viewModel.Items;
        return string.Join(
            Environment.NewLine,
            items.Select((item, index) => BuildSelectableRow(
                Safe(item.Label, 48),
                index == selectedIndex)));
    }

    private static string BuildDashboard(DashboardViewModel viewModel)
    {
        var evidence = viewModel.VhdxEvidence;
        var snapshotText = evidence is null
            ? TuiDisplayText.LabelForInspection(viewModel.InspectionState)
            : $"{PreflightOverviewFormatter.FormatCapacity(evidence.FileLengthBytes)} " +
              $"（{evidence.FileLengthBytes:N0} 字节）；{FormatTimestamp(evidence.LastWriteUtc)}";
        var driveText = evidence is null
            ? "尚未采集"
            : $"可用 {PreflightOverviewFormatter.FormatCapacity(evidence.DriveAvailableFreeSpaceBytes)} / " +
              $"{PreflightOverviewFormatter.FormatCapacity(evidence.DriveTotalSizeBytes)}";
        var sparseText = PreflightOverviewFormatter.FormatSparseState(evidence?.IsSparse);
        var runningDistros = viewModel.RunningDistros.IsDefaultOrEmpty
            ? "未发现运行中的发行版"
            : JoinBounded(viewModel.RunningDistros, "、", 96);
        var notices = viewModel.Notices.IsDefaultOrEmpty
            ? "无"
            : JoinBounded(viewModel.Notices.Take(3), "；", 96);
        var status = viewModel.ErrorMessage is null ? "正常" : "需要关注";
        var statusStyle = viewModel.ErrorMessage is null
            ? VelaTheme.Success
            : VelaTheme.Error;

        return string.Join(
            Environment.NewLine,
            $"[bold {VelaTheme.Section}]目标证据[/]",
            BuildLabelValueRow("档案", $"[bold]{Safe(viewModel.ProfileTitle, 64)}[/]"),
            BuildLabelValueRow("发行版", Safe(viewModel.DistroName, 64)),
            BuildLabelValueRow("VHDX", viewModel.TargetConfigured ? "已配置" : "未配置"),
            BuildLabelValueRow(
                "映射",
                Safe(TuiDisplayText.LabelForMapping(viewModel.MappingState), 48)),
            BuildLabelValueRow("快照", Safe(snapshotText, 96)),
            BuildLabelValueRow("驱动器", Safe(driveText, 96)),
            BuildLabelValueRow("稀疏", Safe(sparseText, 16)),
            BuildLabelValueRow("运行中", Safe(runningDistros, 96)),
            BuildLabelValueRow("通知", Safe(notices, 96)),
            BuildLabelValueRow(
                "状态",
                $"[bold {statusStyle}]{Safe(status, 32)}[/]"),
            viewModel.ErrorMessage is null
                ? BuildLabelValueRow("日志", viewModel.LogsAvailable ? "已创建" : "未创建")
                : BuildLabelValueRow(
                    "阻断",
                    $"[{VelaTheme.Error}]{Safe(viewModel.ErrorMessage, 96)}[/]"),
            viewModel.ErrorMessage is null
                ? string.Empty
                : BuildLabelValueRow("日志", viewModel.LogsAvailable ? "已创建" : "未创建"));
    }

    private static string BuildCompactDashboard(DashboardViewModel viewModel)
    {
        var status = viewModel.ErrorMessage is null ? "正常" : "需要关注";
        var statusStyle = viewModel.ErrorMessage is null
            ? VelaTheme.Success
            : VelaTheme.Error;
        return string.Join(
            Environment.NewLine,
            $"[bold {VelaTheme.Section}]目标[/] " +
            $"{Safe(viewModel.DistroName, 48)} · " +
            $"VHDX {(viewModel.TargetConfigured ? "已配置" : "未配置")} · " +
            $"{Safe(TuiDisplayText.LabelForMapping(viewModel.MappingState), 32)}",
            BuildLabelValueRow(
                "状态",
                $"[bold {statusStyle}]{Safe(status, 32)}[/] " +
                Safe(viewModel.ErrorMessage ?? "无阻断问题", 96)));
    }

    private static string BuildProgress(RunProgressViewModel viewModel)
    {
        var style = GetProgressStyle(viewModel.State);
        var percentage = viewModel.Percent is int value ? $"（{value}%）" : string.Empty;
        return string.Join(
            Environment.NewLine,
            $"[bold {VelaTheme.Section}]当前状态[/] " +
            $"[bold {style}]{Safe(GetProgressLabel(viewModel.State), 32)}[/]" +
            $" [{VelaTheme.Muted}]{Safe(percentage, 16)}[/]",
            $"[{VelaTheme.Muted}]进度[/]   {Safe(viewModel.Message, 160)}");
    }

    private static string BuildMinimumDetails(TuiFrameViewModel viewModel)
    {
        var selected = viewModel.Menu.Items.IsDefaultOrEmpty
            ? "无可用操作"
            : viewModel.Menu.Items[
                Math.Clamp(
                    viewModel.SelectedMenuIndex,
                    0,
                    viewModel.Menu.Items.Length - 1)].Label;
        var focus = viewModel.Page switch
        {
            ProfileEditPageViewModel edit =>
                $"{edit.FieldLabel}：{edit.DisplayValue}",
            ConfirmationPageViewModel confirmation =>
                $"确认：{confirmation.Prompt} / 输入：{confirmation.Response}",
            RecentRunDetailPageViewModel detail =>
                $"运行终态：{TuiDisplayText.LabelForTerminal(detail.TerminalResult)}",
            RecentRunsPageViewModel runs =>
                $"最近运行：{runs.Entries.Length} 条",
            ProfileListPageViewModel profiles =>
                $"档案：{profiles.Profiles.Profiles.Length} 个",
            _ => $"选择：{selected}"
        };

        return string.Join(
            Environment.NewLine,
            $"[{VelaTheme.Muted}]目标[/]   " +
            $"{Safe(viewModel.Dashboard.DistroName, 48)} · " +
            $"VHDX {(viewModel.Dashboard.TargetConfigured ? "已配置" : "未配置")}",
            $"[{VelaTheme.Muted}]状态[/]   " +
            $"[bold {GetProgressStyle(viewModel.Progress.State)}]" +
            $"{Safe(GetProgressLabel(viewModel.Progress.State), 24)}[/] " +
            $"{Safe(viewModel.Progress.Message, 88)}",
            $"[{VelaTheme.Muted}]焦点[/]   " +
            $"[bold {VelaTheme.Focus}]{Safe(focus, 88)}[/]");
    }

    private static string BuildPage(TuiPageViewModel page, int maxRows) => page switch
    {
        ProfileListPageViewModel profileList => BuildProfileList(profileList.Profiles, maxRows),
        ProfileEditPageViewModel profileEdit => BuildProfileEditor(profileEdit),
        RecentRunsPageViewModel recentRuns => BuildRecentRuns(recentRuns, maxRows),
        RecentRunDetailPageViewModel detail => BuildRecentDetail(detail),
        ConfirmationPageViewModel confirmation => BuildConfirmation(confirmation),
        _ => string.Empty
    };

    private static string BuildProfileList(
        ProfileManagementViewModel viewModel,
        int maxRows)
    {
        var lines = viewModel.Profiles
            .Take(maxRows)
            .Select(profile => BuildSelectableRow(
                $"{(profile.IsCurrent ? "[bold]当前[/]" : "    ")} " +
                $"{Safe(profile.DisplayName, 40)} · " +
                $"{Safe(profile.DistroName, 40)} · " +
                $"{Safe(TuiDisplayText.LabelForShutdownMode(profile.ShutdownMode), 24)} · " +
                $"{profile.ShutdownTimeout.TotalSeconds:0} 秒 · " +
                $"VHDX {(profile.TargetConfigured ? "已配置" : "未配置")}",
                profile.IsSelected));
        var validation = viewModel.ValidationError is null
            ? string.Empty
            : $"[{VelaTheme.Attention}]{Safe(viewModel.ValidationError, 120)}[/]";
        return string.Join(
            Environment.NewLine,
            string.Join(Environment.NewLine, lines.DefaultIfEmpty("暂无档案。")),
            validation);
    }

    private static string BuildProfileEditor(ProfileEditPageViewModel viewModel)
    {
        var value = viewModel.Sensitive
            ? Safe(viewModel.DisplayValue, 96)
            : $"[bold]{Safe(viewModel.DisplayValue, 96)}[/]";
        return string.Join(
            Environment.NewLine,
            $"[bold {VelaTheme.Section}]{Safe(viewModel.Title, 48)}[/]",
            BuildLabelValueRow("字段", Safe(viewModel.FieldLabel, 48)),
            BuildLabelValueRow("值", value),
            BuildLabelValueRow(
                "验证",
                viewModel.ValidationError is null
                    ? "待保存时检查"
                    : $"[{VelaTheme.Error}]" +
                      $"{Safe(viewModel.ValidationError, 120)}[/]"));
    }

    private static string BuildRecentRuns(
        RecentRunsPageViewModel viewModel,
        int maxRows)
    {
        if (viewModel.ErrorMessage is not null)
        {
            return $"[{VelaTheme.Error}]{Safe(viewModel.ErrorMessage, 120)}[/]";
        }

        var lines = viewModel.Entries
            .Take(maxRows)
            .Select((entry, index) =>
            {
                var content = entry.IsMalformed
                    ? $"[{VelaTheme.Error}]损坏[/] " +
                      Safe(entry.ErrorMessage ?? "summary 无效", 96)
                    : $"{FormatTimestamp(entry.StartedAtUtc)} · " +
                      $"{Safe(entry.ProfileDisplayName, 40)} · " +
                      $"{Safe(TuiDisplayText.LabelForIntent(entry.Intent), 24)} · " +
                      $"[{GetTerminalStyle(entry.TerminalResult)}]" +
                      $"{Safe(TuiDisplayText.LabelForTerminal(entry.TerminalResult), 32)}[/] · " +
                      $"回收 {entry.ReclaimedBytes?.ToString("N0") ?? "未知"} 字节";
                return BuildSelectableRow(content, index == viewModel.SelectedIndex);
            });
        return string.Join(
            Environment.NewLine,
            $"[{VelaTheme.Muted}]最多显示 20 条可信摘要[/]",
            string.Join(Environment.NewLine, lines.DefaultIfEmpty("暂无运行记录。")));
    }

    private static string BuildRecentDetail(RecentRunDetailPageViewModel viewModel) =>
        string.Join(
            Environment.NewLine,
            BuildLabelValueRow(
                "状态",
                viewModel.IsMalformed ? $"[{VelaTheme.Error}]损坏[/]" : "可读"),
            BuildLabelValueRow("档案", Safe(viewModel.ProfileDisplayName, 64)),
            BuildLabelValueRow(
                "意图",
                Safe(TuiDisplayText.LabelForIntent(viewModel.Intent), 32)),
            BuildLabelValueRow(
                "终态",
                $"[bold {GetTerminalStyle(viewModel.TerminalResult)}]" +
                $"{Safe(TuiDisplayText.LabelForTerminal(viewModel.TerminalResult), 48)}[/]"),
            BuildLabelValueRow("开始", FormatTimestamp(viewModel.StartedAtUtc)),
            BuildLabelValueRow("完成", FormatTimestamp(viewModel.CompletedAtUtc)),
            BuildLabelValueRow(
                "耗时",
                Safe(viewModel.Elapsed?.ToString() ?? "未知", 48)),
            BuildLabelValueRow(
                "回收",
                $"{viewModel.ReclaimedBytes?.ToString("N0") ?? "未知"} 字节"),
            BuildLabelValueRow("日志", viewModel.LogsAvailable ? "可在 TUI 查看" : "不可用"),
            BuildLabelValueRow("错误", Safe(viewModel.ErrorMessage ?? "无", 96)));

    private static string BuildConfirmation(ConfirmationPageViewModel viewModel) =>
        string.Join(
            Environment.NewLine,
            $"[bold {VelaTheme.Attention}]破坏性操作确认[/]",
            Safe(viewModel.Prompt, 160),
            BuildLabelValueRow(
                "输入",
                $"[bold {VelaTheme.Focus}]{Safe(viewModel.Response, 16)}[/]"));

    private static string BuildFooter(TuiPageViewModel page)
    {
        var text = page.Screen switch
        {
            TuiScreen.ProfileList => "↑↓ 选择 · Enter 切换 · N 新建 · E 编辑 · D 删除 · Esc 返回",
            TuiScreen.ProfileEdit => "输入 / Backspace 编辑 · 方向键选择 · Enter 下一步 · Esc 取消",
            TuiScreen.RecentRuns => "↑↓ 选择 · Enter 详情 · Esc 返回",
            TuiScreen.RecentRunDetail => "日志归档查看 · Esc 返回列表",
            TuiScreen.Confirmation => "输入精确 YES · Backspace 删除 · Enter 提交 · Esc 取消",
            TuiScreen.Running => "运行中 · 等待可信终态",
            _ => "↑↓ 选择 · Enter 执行 · Esc 退出"
        };
        return $"[{VelaTheme.Muted}]{Safe(text, 160)}[/]";
    }

    private static string PageTitle(TuiPageViewModel page) => page.Screen switch
    {
        TuiScreen.ProfileList => "档案",
        TuiScreen.ProfileEdit => "编辑",
        TuiScreen.RecentRuns => "追溯",
        TuiScreen.RecentRunDetail => "运行证据",
        TuiScreen.Confirmation => "确认",
        TuiScreen.Running => "执行",
        TuiScreen.Result => "结果",
        _ => "目标与状态"
    };

    private static string BuildSelectableRow(string content, bool selected) =>
        selected
            ? $"[bold {VelaTheme.Focus}]› {content}[/]"
            : $"  {content}";

    private static string BuildLabelValueRow(string label, string value) =>
        $"[{VelaTheme.Muted}]{Markup.Escape(TuiDisplayText.PadRight(label, 10))}[/] {value}";

    private static string GetTerminalStyle(TerminalResult? result) => result switch
    {
        TerminalResult.Succeeded => VelaTheme.Success,
        TerminalResult.CompletedWithNoReclaim or
        TerminalResult.CancelledBeforeElevation or
        TerminalResult.ShutdownTimedOut or
        TerminalResult.WorkerInterrupted => VelaTheme.Attention,
        TerminalResult.ValidationFailed or
        TerminalResult.DiskPartPreflightFailed or
        TerminalResult.DiskPartCompactFailed => VelaTheme.Error,
        _ => VelaTheme.Muted
    };

    private static string JoinBounded(
        IEnumerable<string> values,
        string separator,
        int maxLength)
    {
        var result = string.Empty;
        foreach (var value in values)
        {
            var sanitized = TuiDisplayText.Sanitize(value);
            var candidate = result.Length == 0
                ? sanitized
                : result + separator + sanitized;
            if (TuiDisplayText.Sanitize(candidate, maxLength) != candidate)
            {
                return TuiDisplayText.Sanitize(candidate, maxLength);
            }

            result = candidate;
        }

        return result;
    }

    private static string FormatTimestamp(DateTimeOffset? value) =>
        value is { } timestamp
            ? timestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")
            : "未知时间";

    private static string GetProgressLabel(RunProgressState state) => state switch
    {
        RunProgressState.Idle => "空闲",
        RunProgressState.Preflighting => "预检中",
        RunProgressState.AwaitingConfirmation => "等待确认",
        RunProgressState.Running => "运行中",
        RunProgressState.Succeeded => "已完成",
        RunProgressState.Failed => "失败",
        RunProgressState.Cancelled => "已取消",
        RunProgressState.TimedOut => "已超时",
        RunProgressState.ReadFailed => "读取失败",
        _ => "未知状态"
    };

    private static string GetProgressStyle(RunProgressState state) => state switch
    {
        RunProgressState.Succeeded => VelaTheme.Success,
        RunProgressState.Failed => VelaTheme.Error,
        RunProgressState.AwaitingConfirmation => VelaTheme.Attention,
        RunProgressState.Running or RunProgressState.Preflighting => VelaTheme.Focus,
        RunProgressState.Cancelled or RunProgressState.TimedOut or RunProgressState.ReadFailed =>
            VelaTheme.Attention,
        _ => VelaTheme.Muted
    };

    private static string Safe(string? value, int maxCells) =>
        Markup.Escape(TuiDisplayText.Sanitize(value, maxCells));

    private sealed record RenderSections(
        string Header,
        string NavigationTitle,
        string Menu,
        string WorkspaceTitle,
        string Details,
        string Footer);
}
