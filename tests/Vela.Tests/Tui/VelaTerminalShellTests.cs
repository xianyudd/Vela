using Terminal.Gui.App;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.Time;
using Vela.Core.Models;
using Vela.Tui;
using Vela.Tui.Application;
using Vela.Tui.Menu;
using Vela.Tui.Rendering;
using Vela.Tui.Views;

namespace Vela.Tests.Tui;

public sealed class VelaTerminalShellTests
{
    [Fact]
    public void Requesting_a_menu_item_raises_its_typed_action()
    {
        using var shell = new VelaTerminalShell(
            new MainMenu().ViewModel,
            DashboardViewModel.CreateInitial(CreateProfile()));
        MainMenuAction? selected = null;
        shell.ActionRequested += action => selected = action;

        shell.RequestAction(0);

        Assert.Equal(MainMenuAction.Preflight, selected);
    }

    [Fact]
    public void Navigation_has_one_focus_list_with_continuous_selection_in_every_layout()
    {
        using var shell = new VelaTerminalShell(
            new MainMenu().ViewModel,
            DashboardViewModel.CreateInitial(CreateProfile()));

        shell.SelectMenuIndex(1);
        shell.AdaptTo(new System.Drawing.Rectangle(0, 0, 121, 33));

        Assert.Equal(6, shell.NavigationItemCount);
        Assert.Equal(1, shell.SelectedMenuIndex);
        Assert.True(shell.HasSingleNavigationFocus);
        Assert.Equal(VelaShellLayout.TwoPane, shell.LayoutMode);

        shell.AdaptTo(new System.Drawing.Rectangle(0, 0, 80, 24));
        Assert.Equal(VelaShellLayout.TwoPane, shell.LayoutMode);

        shell.AdaptTo(new System.Drawing.Rectangle(0, 0, 79, 24));
        Assert.Equal(VelaShellLayout.SinglePane, shell.LayoutMode);

        shell.SelectMenuIndex(4);
        shell.AdaptTo(new System.Drawing.Rectangle(0, 0, 67, 19));

        Assert.Equal(4, shell.SelectedMenuIndex);
        Assert.True(shell.HasSingleNavigationFocus);
        Assert.Equal(VelaShellLayout.SinglePane, shell.LayoutMode);
    }

    [Fact]
    public void Changing_selection_only_moves_focus_until_enter()
    {
        using var shell = new VelaTerminalShell(
            new MainMenu().ViewModel,
            DashboardViewModel.CreateInitial(CreateProfile()));
        var accepted = new List<MainMenuAction>();
        shell.ActionRequested += accepted.Add;

        shell.SelectMenuIndex(2);

        Assert.Empty(accepted);
        Assert.Equal(2, shell.SelectedMenuIndex);
        Assert.Equal(VelaWorkspacePage.Profiles, shell.CurrentPage);
        Assert.Contains("目标档案", shell.ContentTitle, StringComparison.Ordinal);
    }

    [Fact]
    public void Moving_selection_updates_the_right_preview_without_dispatching_action()
    {
        using var shell = new VelaTerminalShell(
            new MainMenu().ViewModel,
            DashboardViewModel.CreateInitial(CreateProfile()));
        var accepted = new List<MainMenuAction>();
        shell.ActionRequested += accepted.Add;

        shell.SelectMenuIndex(4);

        Assert.Equal(VelaWorkspacePage.Logs, shell.CurrentPage);
        Assert.Contains("日志分析", shell.ContentTitle, StringComparison.Ordinal);
        Assert.Contains("按 Enter 读取", shell.WorkspaceText, StringComparison.Ordinal);
        Assert.Empty(accepted);
    }

    [Fact]
    public void Selection_preview_exposes_a_revision_for_async_data_without_stale_updates()
    {
        using var shell = new VelaTerminalShell(
            new MainMenu().ViewModel,
            DashboardViewModel.CreateInitial(CreateProfile()));
        var previews = new List<(MainMenuAction Action, long Revision)>();
        shell.SelectionPreviewRequested += (action, revision) => previews.Add((action, revision));

        shell.SelectMenuIndex(3);
        var recentRevision = shell.NavigationRevision;
        shell.SelectMenuIndex(4);

        Assert.Contains(previews, preview => preview.Action == MainMenuAction.RecentRuns);
        Assert.True(recentRevision > 0);
        Assert.False(shell.IsCurrentSelection(MainMenuAction.RecentRuns, recentRevision));
        Assert.True(shell.IsCurrentSelection(MainMenuAction.OpenLogs, shell.NavigationRevision));
    }

    [Fact]
    public void Confirmation_page_shows_impact_and_rejects_non_exact_input_without_requesting_an_action()
    {
        var profile = CreateProfile();
        using var shell = new VelaTerminalShell(
            new MainMenu().ViewModel,
            DashboardViewModel.CreateInitial(profile));
        var actions = new List<MainMenuAction>();
        shell.ActionRequested += actions.Add;

        shell.ShowConfirmation(MainMenu.CreateExecuteConfirmation(
            profile,
            System.Collections.Immutable.ImmutableArray<Vela.Core.Contracts.WslDistribution>.Empty));
        shell.SubmitConfirmation("yes");

        Assert.Equal(VelaWorkspacePage.Confirmation, shell.CurrentPage);
        Assert.Contains("确认输入未匹配 YES", shell.StatusText);
        Assert.Empty(actions);
    }

    [Fact]
    public void Confirmation_input_is_bounded_before_policy_evaluation()
    {
        using var shell = new VelaTerminalShell(
            new MainMenu().ViewModel,
            DashboardViewModel.CreateInitial(CreateProfile()));
        ConfirmationInputResult? submitted = null;
        shell.ConfirmationSubmitted += result => submitted = result;

        shell.ShowConfirmation(MainMenu.CreateExecuteConfirmation(
            CreateProfile(),
            System.Collections.Immutable.ImmutableArray<Vela.Core.Contracts.WslDistribution>.Empty));
        shell.SubmitConfirmation("YES-THIS-INPUT-IS-LONGER-THAN-SIXTEEN");

        Assert.NotNull(submitted);
        Assert.Equal(ConfirmationInputStatus.Rejected, submitted!.Status);
        Assert.Equal(16, submitted.Response.Length);
    }

    [Fact]
    public void Cancelling_confirmation_returns_focus_to_overview_item()
    {
        var profile = CreateProfile();
        using var shell = new VelaTerminalShell(
            new MainMenu().ViewModel,
            DashboardViewModel.CreateInitial(profile));

        shell.SelectMenuIndex(1);
        shell.ShowConfirmation(MainMenu.CreateExecuteConfirmation(
            profile,
            System.Collections.Immutable.ImmutableArray<Vela.Core.Contracts.WslDistribution>.Empty));
        shell.CancelConfirmation();

        Assert.Equal(VelaWorkspacePage.Overview, shell.CurrentPage);
        Assert.Equal(0, shell.SelectedMenuIndex);
        Assert.Equal(MainMenuAction.Preflight, shell.SelectedAction);
    }

    [Fact]
    public void Compaction_action_is_held_until_a_current_preflight_is_ready()
    {
        using var shell = new VelaTerminalShell(
            new MainMenu().ViewModel,
            DashboardViewModel.CreateInitial(CreateProfile()));
        var actions = new List<MainMenuAction>();
        shell.ActionRequested += actions.Add;

        shell.RequestAction(1);

        Assert.Empty(actions);
        Assert.Contains("预检", shell.StatusText);
    }

    [Fact]
    public void Ready_preflight_allows_compaction_action()
    {
        var profile = CreateProfile();
        var dashboard = CreateReadyDashboard(profile);
        using var shell = new VelaTerminalShell(
            new MainMenu().ViewModel,
            dashboard);
        MainMenuAction? selected = null;
        shell.ActionRequested += action => selected = action;
        shell.SetCurrentProfile(profile);
        shell.ApplyPreflight(new AutomaticPreflightState(
            profile.Id,
            1,
            1,
            AutomaticPreflightStatus.Ready,
            dashboard,
            "预检已完成。"));
        shell.ShowOverview();
        shell.NewKeyDownEvent(Key.Enter);

        shell.RequestAction(1);

        Assert.Equal(MainMenuAction.ExecuteCompaction, selected);
    }

    [Fact]
    public void Ready_preflight_requires_a_locked_target_before_compaction_action()
    {
        var profile = CreateProfile();
        var dashboard = CreateReadyDashboard(profile);
        using var shell = new VelaTerminalShell(
            new MainMenu().ViewModel,
            dashboard);
        var actions = new List<MainMenuAction>();
        shell.ActionRequested += actions.Add;
        shell.SetCurrentProfile(profile);
        shell.ApplyPreflight(new AutomaticPreflightState(
            profile.Id,
            1,
            1,
            AutomaticPreflightStatus.Ready,
            dashboard,
            "预检已完成。"));

        shell.RequestAction(1);

        Assert.Empty(actions);
        Assert.Contains("锁定", shell.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public void Locked_target_profile_and_preview_use_the_selected_instance()
    {
        var profile = CreateProfile();
        var dockerPath = @"D:\Docker\wsl\data\ext4.vhdx";
        var dashboard = CreateReadyDashboard(profile) with
        {
            InstalledDistros = ImmutableArray.Create(
                new Vela.Core.Contracts.WslDistribution(
                    profile.DistroName,
                    Vela.Core.Contracts.WslDistributionState.Stopped,
                    2,
                    true,
                    profile.VhdxPath,
                    124L * PreflightOverviewFormatter.Gibibyte),
                new Vela.Core.Contracts.WslDistribution(
                    "docker-desktop",
                    Vela.Core.Contracts.WslDistributionState.Stopped,
                    2,
                    false,
                    dockerPath,
                    65L * PreflightOverviewFormatter.Gibibyte))
        };
        using var shell = new VelaTerminalShell(new MainMenu().ViewModel, dashboard);
        shell.SetCurrentProfile(profile);
        shell.ApplyPreflight(new AutomaticPreflightState(
            profile.Id,
            1,
            1,
            AutomaticPreflightStatus.Ready,
            dashboard,
            "预检已完成。"));
        shell.ShowOverview();

        shell.NewKeyDownEvent(Key.CursorDown);
        shell.NewKeyDownEvent(Key.Enter);

        var targetProfile = shell.CreateLockedTargetProfile(profile);
        Assert.NotNull(targetProfile);
        Assert.Equal("docker-desktop", targetProfile!.DistroName);
        Assert.Equal(dockerPath, targetProfile.VhdxPath);

        shell.SelectMenuIndex(1);

        Assert.Contains("docker-desktop", shell.WorkspaceText, StringComparison.Ordinal);
        Assert.Contains(dockerPath, shell.WorkspaceText, StringComparison.Ordinal);
        Assert.DoesNotContain(profile.DistroName, shell.WorkspaceText, StringComparison.Ordinal);
    }

    [Fact]
    public void Locked_target_is_cleared_when_a_new_inventory_drops_that_instance()
    {
        var profile = CreateProfile();
        var dashboard = CreateReadyDashboard(profile) with
        {
            InstalledDistros = ImmutableArray.Create(
                new Vela.Core.Contracts.WslDistribution(
                    profile.DistroName,
                    Vela.Core.Contracts.WslDistributionState.Stopped,
                    2,
                    true,
                    profile.VhdxPath),
                new Vela.Core.Contracts.WslDistribution(
                    "docker-desktop",
                    Vela.Core.Contracts.WslDistributionState.Stopped,
                    2,
                    false,
                    @"D:\Docker\wsl\data\ext4.vhdx"))
        };
        using var shell = new VelaTerminalShell(new MainMenu().ViewModel, dashboard);
        shell.SetCurrentProfile(profile);
        shell.ApplyPreflight(new AutomaticPreflightState(
            profile.Id,
            1,
            1,
            AutomaticPreflightStatus.Ready,
            dashboard,
            "预检已完成。"));
        shell.ShowOverview();
        shell.NewKeyDownEvent(Key.CursorDown);
        shell.NewKeyDownEvent(Key.Enter);

        Assert.Equal("docker-desktop", shell.LockedTargetName);

        shell.ApplyPreflight(new AutomaticPreflightState(
            profile.Id,
            2,
            2,
            AutomaticPreflightStatus.Ready,
            CreateReadyDashboard(profile),
            "预检已完成。"));

        Assert.Null(shell.LockedTarget);
        Assert.Null(shell.LockedTargetName);
    }

    [Fact]
    public void Run_progress_view_uses_real_state_without_a_synthetic_percent()
    {
        using var shell = new VelaTerminalShell(
            new MainMenu().ViewModel,
            DashboardViewModel.CreateInitial(CreateProfile()));

        shell.ShowRunProgress(new RunProgressViewModel(
            RunProgressState.Running,
            "正在读取 journal 事件。",
            Percent: null,
            TargetName: "docker-desktop",
            VhdxPath: @"D:\Docker\wsl\data\ext4.vhdx",
            LogLines: ["[INFO] target locked: docker-desktop"]));

        Assert.Equal(VelaWorkspacePage.Running, shell.CurrentPage);
        Assert.Contains("执行中", shell.StatusText, StringComparison.Ordinal);
        Assert.Contains("docker-desktop", shell.WorkspaceText, StringComparison.Ordinal);
        Assert.Contains("[INFO] target locked", shell.WorkspaceText, StringComparison.Ordinal);
    }

    [Fact]
    public void Action_preview_accepts_lowercase_and_shifted_uppercase_y()
    {
        var profile = CreateProfile();
        var dashboard = CreateReadyDashboard(profile);
        using var shell = new VelaTerminalShell(new MainMenu().ViewModel, dashboard);
        var actions = new List<MainMenuAction>();
        shell.ActionRequested += actions.Add;
        shell.SetCurrentProfile(profile);
        shell.ApplyPreflight(new AutomaticPreflightState(
            profile.Id,
            1,
            1,
            AutomaticPreflightStatus.Ready,
            dashboard,
            "预检已完成。"));
        shell.ShowOverview();
        shell.NewKeyDownEvent(Key.Enter);
        shell.SelectMenuIndex(1);

        shell.NewKeyDownEvent(new Key('y'));
        shell.NewKeyDownEvent(Key.Y.WithShift);

        Assert.Equal(
            [MainMenuAction.ExecuteCompaction, MainMenuAction.ExecuteCompaction],
            actions);
    }

    [Fact]
    public void Completed_run_view_shows_release_summary_and_returns_to_instance_list()
    {
        using var shell = new VelaTerminalShell(
            new MainMenu().ViewModel,
            DashboardViewModel.CreateInitial(CreateProfile()));

        shell.ShowRunProgress(new RunProgressViewModel(
            RunProgressState.Succeeded,
            "运行终态：已完成。",
            Percent: null,
            TargetName: "docker-desktop",
            Elapsed: TimeSpan.FromSeconds(42),
            ReclaimedBytes: 53L * PreflightOverviewFormatter.Gibibyte));

        Assert.Equal(VelaWorkspacePage.Result, shell.CurrentPage);
        Assert.Contains("DONE", shell.WorkspaceText, StringComparison.Ordinal);
        Assert.Contains("53.00 GiB", shell.WorkspaceText, StringComparison.Ordinal);

        shell.NewKeyDownEvent(Key.Enter);

        Assert.Equal(VelaWorkspacePage.Overview, shell.CurrentPage);
        Assert.Equal(0, shell.SelectedMenuIndex);
    }

    [Theory]
    [InlineData("目标档案", "[Enter] 刷新档案摘要")]
    [InlineData("最近运行", "[Enter] 刷新运行记录")]
    [InlineData("运行日志", "[Enter] 打开日志目录")]
    public void Read_only_workspace_pages_expose_contextual_shortcuts(string title, string shortcut)
    {
        using var shell = new VelaTerminalShell(
            new MainMenu().ViewModel,
            DashboardViewModel.CreateInitial(CreateProfile()));

        shell.ShowWorkspacePage(title, ["sample"]);

        Assert.Contains(shortcut, shell.StatusText, StringComparison.Ordinal);
        Assert.DoesNotContain("Enter 选择", shell.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public void Narrow_log_page_prioritizes_error_and_warning_rows()
    {
        using var app = Application.Create(new VirtualTimeProvider());
        app.Init(DriverRegistry.Names.ANSI);
        VelaTerminalTheme.Register();
        using var shell = new VelaTerminalShell(
            new MainMenu().ViewModel,
            DashboardViewModel.CreateInitial(CreateProfile()));
        app.Driver!.SetScreenSize(60, 16);
        using var host = new TerminalGuiShellHost(app, shell);
        var session = app.Begin(shell);

        try
        {
            shell.ShowLogPage([
                new RunLogLine("最新运行日志：", RunEventLevel.Information),
                new RunLogLine("[6]  2026-08-10T02:00:00+08:00 Error Inventory Preflight", RunEventLevel.Error),
                new RunLogLine("[7]  2026-08-10T02:00:01+08:00 Warning Snapshot Preflight", RunEventLevel.Warning)]);
            app.LayoutAndDraw(forceRedraw: true);

            var rendered = app.Driver.ToString();
            Assert.Contains("ERROR", rendered);
            Assert.Contains("WARN", rendered);
        }
        finally
        {
            app.End(session!);
        }
    }

    [Fact]
    public void Log_analysis_page_renders_counts_and_contextual_navigation()
    {
        using var app = Application.Create(new VirtualTimeProvider());
        app.Init(DriverRegistry.Names.ANSI);
        VelaTerminalTheme.Register();
        using var shell = new VelaTerminalShell(
            new MainMenu().ViewModel,
            DashboardViewModel.CreateInitial(CreateProfile()));
        app.Driver!.SetScreenSize(80, 24);
        using var host = new TerminalGuiShellHost(app, shell);
        var session = app.Begin(shell);

        try
        {
            shell.ShowLogAnalysis(new RunLogSnapshot(
                [
                    new RunLogLine("[6] 2026-08-10T02:00:00Z Error Inventory Preflight", RunEventLevel.Error),
                    new RunLogLine("[7] 2026-08-10T02:00:01Z Warning Snapshot Preflight", RunEventLevel.Warning)
                ],
                WasTailTruncated: false,
                ErrorMessage: null));
            app.LayoutAndDraw(forceRedraw: true);

            var rendered = app.Driver.ToString();
            Assert.Equal(VelaWorkspacePage.LogAnalysis, shell.CurrentPage);
            Assert.Contains("只读日志摘要", rendered);
            Assert.Contains("ERROR", rendered);
            Assert.Contains("E1", rendered);
            Assert.Contains("Inventory", rendered);
            Assert.Contains("[Enter] 打开日志目录", shell.StatusText, StringComparison.Ordinal);
        }
        finally
        {
            app.End(session!);
        }
    }

    [Fact]
    public void Narrow_log_analysis_keeps_summary_and_error_signal_visible()
    {
        using var app = Application.Create(new VirtualTimeProvider());
        app.Init(DriverRegistry.Names.ANSI);
        VelaTerminalTheme.Register();
        using var shell = new VelaTerminalShell(
            new MainMenu().ViewModel,
            DashboardViewModel.CreateInitial(CreateProfile()));
        app.Driver!.SetScreenSize(60, 16);
        using var host = new TerminalGuiShellHost(app, shell);
        var session = app.Begin(shell);

        try
        {
            shell.ShowLogAnalysis(new RunLogSnapshot(
                [
                    new RunLogLine("[6] 2026-08-10T02:00:00Z Error Inventory Preflight", RunEventLevel.Error),
                    new RunLogLine("[7] 2026-08-10T02:00:01Z Warning Snapshot Preflight", RunEventLevel.Warning)
                ],
                WasTailTruncated: false,
                ErrorMessage: null));
            app.LayoutAndDraw(forceRedraw: true);

            var rendered = app.Driver.ToString();
            Assert.Contains("日志 2 条", rendered);
            Assert.Contains("E1", rendered);
            Assert.Contains("ERROR Inventory", rendered);
        }
        finally
        {
            app.End(session!);
        }
    }

    [Fact]
    public void Wide_log_analysis_uses_a_single_context_rail_without_hiding_log_rows()
    {
        using var app = Application.Create(new VirtualTimeProvider());
        app.Init(DriverRegistry.Names.ANSI);
        VelaTerminalTheme.Register();
        using var shell = new VelaTerminalShell(
            new MainMenu().ViewModel,
            DashboardViewModel.CreateInitial(CreateProfile()));
        app.Driver!.SetScreenSize(160, 45);
        using var host = new TerminalGuiShellHost(app, shell);
        var session = app.Begin(shell);

        try
        {
            shell.ShowLogAnalysis(new RunLogSnapshot(
                [new RunLogLine("[6] 2026-08-10T02:00:00Z Error Inventory Preflight", RunEventLevel.Error)],
                WasTailTruncated: true,
                ErrorMessage: null));
            app.LayoutAndDraw(forceRedraw: true);

            var rendered = app.Driver.ToString();
            Assert.Contains("分析范围", rendered);
            Assert.Contains("原文已隐藏路径与命令输出", rendered);
            Assert.Contains("ERROR Inventory", rendered);
        }
        finally
        {
            app.End(session!);
        }
    }

    [Fact]
    public void Checking_preflight_renders_task_card_summary_and_evidence_table()
    {
        var profile = CreateProfile();
        using var app = Application.Create(new VirtualTimeProvider());
        app.Init(DriverRegistry.Names.ANSI);
        VelaTerminalTheme.Register();
        using var shell = new VelaTerminalShell(
            new MainMenu().ViewModel,
            DashboardViewModel.CreateInitial(profile));
        shell.SetCurrentProfile(profile);
        shell.ApplyPreflight(new AutomaticPreflightState(
            profile.Id,
            1,
            1,
            AutomaticPreflightStatus.Checking,
            DashboardViewModel.CreateInitial(profile),
            "正在进行只读预检。"));
        app.Driver!.SetScreenSize(121, 33);
        using var host = new TerminalGuiShellHost(app, shell);
        var session = app.Begin(shell);

        try
        {
            app.LayoutAndDraw(forceRedraw: true);
            var rendered = app.Driver.ToString();

            Assert.Contains("执行目标选择", rendered);
            Assert.Contains("正在扫描 WSL 实例", rendered);
            Assert.Contains("INFO", rendered);
            Assert.Contains("实例列表", rendered);
            Assert.Contains("检查 / 执行", rendered);
            Assert.Contains("追溯", rendered);
        }
        finally
        {
            app.End(session!);
        }
    }

    [Fact]
    public void Medium_overview_card_keeps_evidence_next_step_and_action_bar_visible()
    {
        var profile = CreateProfile();
        using var app = Application.Create(new VirtualTimeProvider());
        app.Init(DriverRegistry.Names.ANSI);
        VelaTerminalTheme.Register();
        using var shell = new VelaTerminalShell(
            new MainMenu().ViewModel,
            DashboardViewModel.CreateInitial(profile));
        shell.SetCurrentProfile(profile);
        shell.ApplyPreflight(new AutomaticPreflightState(
            profile.Id,
            1,
            1,
            AutomaticPreflightStatus.Attention,
            new DashboardViewModel(
                MainMenu.ApplicationTitle,
                "档案：Ubuntu 24.04",
                "Ubuntu-24.04",
                TargetConfigured: true,
                TargetMappingState.NotChecked,
                TargetInspectionState.Available,
                VhdxEvidence: null,
                RunningDistros: System.Collections.Immutable.ImmutableArray.Create("Ubuntu-24.04"),
                Notices: System.Collections.Immutable.ImmutableArray.Create("稀疏状态未知"),
                ErrorMessage: "稀疏状态未知",
                LogsAvailable: true),
            "预检已完成，发现需要关注的问题。"));
        app.Driver!.SetScreenSize(80, 24);
        using var host = new TerminalGuiShellHost(app, shell);
        var session = app.Begin(shell);

        try
        {
            app.LayoutAndDraw(forceRedraw: true);
            var rendered = app.Driver.ToString();

            Assert.Contains("实例列表", rendered);
            Assert.Contains("Ubuntu-24.04", rendered);
            Assert.Contains("操作", rendered);
        }
        finally
        {
            app.End(session!);
        }
    }

    [Fact]
    public void Driver_resize_reflows_shell_without_losing_preflight_status()
    {
        using var app = Application.Create(new VirtualTimeProvider());
        app.Init(DriverRegistry.Names.ANSI);
        VelaTerminalTheme.Register();
        using var shell = new VelaTerminalShell(
            new MainMenu().ViewModel,
            DashboardViewModel.CreateInitial(CreateProfile()));
        app.Driver!.SetScreenSize(121, 33);
        using var host = new TerminalGuiShellHost(app, shell);
        var session = app.Begin(shell);

        try
        {
            shell.ApplyPreflight(new AutomaticPreflightState(
                CreateProfile().Id,
                1,
                1,
                AutomaticPreflightStatus.Checking,
                Dashboard: null,
                Message: "正在进行只读预检。"));
            app.LayoutAndDraw(forceRedraw: true);

            Assert.Equal(VelaShellLayout.TwoPane, shell.LayoutMode);
            Assert.Contains("扫描中", app.Driver.ToString());

            app.Driver.SetScreenSize(67, 19);
            app.LayoutAndDraw(forceRedraw: true);

            Assert.Equal(VelaShellLayout.SinglePane, shell.LayoutMode);
            Assert.Contains("实例 0 个", app.Driver.ToString());
        }
        finally
        {
            app.End(session!);
        }
    }

    [Fact]
    public void Menu_one_exposes_r_refresh_without_dispatching_a_compaction_action()
    {
        using var shell = new VelaTerminalShell(
            new MainMenu().ViewModel,
            DashboardViewModel.CreateInitial(CreateProfile()));
        var actions = new List<MainMenuAction>();
        shell.ActionRequested += actions.Add;
        shell.SelectMenuIndex(0);

        shell.RequestPreflightRefresh();

        Assert.Equal([MainMenuAction.Preflight], actions);
        Assert.Contains("[R] 重新扫描", shell.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public void Menu_one_detail_accepts_lowercase_and_shifted_uppercase_r_for_refresh()
    {
        var profile = CreateProfile();
        var dashboard = CreateReadyDashboard(profile);
        using var shell = new VelaTerminalShell(new MainMenu().ViewModel, dashboard);
        var actions = new List<MainMenuAction>();
        shell.ActionRequested += actions.Add;
        shell.SetCurrentProfile(profile);
        shell.ApplyPreflight(new AutomaticPreflightState(
            profile.Id,
            1,
            1,
            AutomaticPreflightStatus.Ready,
            dashboard,
            "预检已完成。"));
        shell.ShowOverview();
        shell.NewKeyDownEvent(Key.Enter);

        Assert.Equal(VelaWorkspacePage.TargetDetail, shell.CurrentPage);
        shell.NewKeyDownEvent(new Key('r'));
        Assert.Equal(VelaWorkspacePage.Overview, shell.CurrentPage);
        shell.NewKeyDownEvent(Key.Enter);
        shell.NewKeyDownEvent(Key.R.WithShift);

        Assert.Equal([MainMenuAction.Preflight, MainMenuAction.Preflight], actions);
        Assert.Equal(VelaWorkspacePage.Overview, shell.CurrentPage);
        Assert.Contains("切换实例", shell.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public void Menu_one_accepts_shifted_uppercase_r_from_terminal_input()
    {
        using var shell = new VelaTerminalShell(
            new MainMenu().ViewModel,
            DashboardViewModel.CreateInitial(CreateProfile()));
        var actions = new List<MainMenuAction>();
        shell.ActionRequested += actions.Add;

        shell.NewKeyDownEvent(Key.R.WithShift);

        Assert.Equal([MainMenuAction.Preflight], actions);
    }

    [Theory]
    [InlineData('r')]
    [InlineData('R')]
    public void Menu_one_refresh_is_handled_when_the_navigation_list_has_focus(char input)
    {
        using var app = Application.Create(new VirtualTimeProvider());
        app.Init(DriverRegistry.Names.ANSI);
        VelaTerminalTheme.Register();
        using var shell = new VelaTerminalShell(
            new MainMenu().ViewModel,
            DashboardViewModel.CreateInitial(CreateProfile()));
        app.Driver!.SetScreenSize(160, 45);
        using var host = new TerminalGuiShellHost(app, shell);
        var session = app.Begin(shell);
        var actions = new List<MainMenuAction>();
        shell.ActionRequested += actions.Add;

        try
        {
            shell.ShowOverview();

            var handled = app.Keyboard.RaiseKeyDownEvent(new Key(input));

            Assert.True(handled);
            Assert.Equal([MainMenuAction.Preflight], actions);
        }
        finally
        {
            app.End(session!);
        }
    }

    [Fact]
    public void Menu_one_opens_selected_instance_detail_and_enters_compaction_preview()
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
                    true),
                new Vela.Core.Contracts.WslDistribution(
                    "docker-desktop",
                    Vela.Core.Contracts.WslDistributionState.Running,
                    2,
                    false)),
            RunningInventoryState = PreflightDataState.Available,
            LogAvailabilityState = PreflightDataState.Available,
            LogsAvailable = true
        };
        using var app = Application.Create(new VirtualTimeProvider());
        app.Init(DriverRegistry.Names.ANSI);
        VelaTerminalTheme.Register();
        using var shell = new VelaTerminalShell(new MainMenu().ViewModel, dashboard);
        app.Driver!.SetScreenSize(160, 45);
        using var host = new TerminalGuiShellHost(app, shell);
        var session = app.Begin(shell);
        var actions = new List<MainMenuAction>();
        shell.ActionRequested += actions.Add;

        try
        {
            shell.SetCurrentProfile(profile);
            shell.ApplyPreflight(new AutomaticPreflightState(
                profile.Id,
                1,
                1,
                AutomaticPreflightStatus.Ready,
                dashboard,
                "预检已完成。"));
            shell.ShowOverview();
            app.LayoutAndDraw(forceRedraw: true);

            Assert.True(app.Keyboard.RaiseKeyDownEvent(Key.CursorDown));
            Assert.Equal(1, shell.SelectedTargetIndex);
            Assert.Equal(0, shell.SelectedMenuIndex);
            Assert.Empty(actions);

            Assert.True(app.Keyboard.RaiseKeyDownEvent(Key.Enter));
            Assert.Equal("docker-desktop", shell.LockedTargetName);
            Assert.Equal(VelaWorkspacePage.TargetDetail, shell.CurrentPage);
            Assert.Contains("目标预检详情", shell.ContentTitle, StringComparison.Ordinal);
            app.LayoutAndDraw(forceRedraw: true);
            var rendered = app.Driver.ToString();
            Assert.Contains("PASS", rendered, StringComparison.Ordinal);
            Assert.Contains("目标信息", rendered, StringComparison.Ordinal);
            Assert.Contains("检查明细（5/5）", rendered, StringComparison.Ordinal);
            Assert.Empty(actions);

            Assert.True(app.Keyboard.RaiseKeyDownEvent(Key.Enter));
            Assert.Equal(VelaWorkspacePage.ActionPreview, shell.CurrentPage);
            Assert.Contains("影响预览", shell.ContentTitle, StringComparison.Ordinal);
            Assert.Empty(actions);

            Assert.True(app.Keyboard.RaiseKeyDownEvent(Key.Esc));
            Assert.Equal(VelaWorkspacePage.Overview, shell.CurrentPage);
            Assert.True(app.Keyboard.RaiseKeyDownEvent(Key.Tab));
            Assert.True(app.Keyboard.RaiseKeyDownEvent(Key.CursorDown));
            Assert.Equal(1, shell.SelectedMenuIndex);
        }
        finally
        {
            app.End(session!);
        }
    }

    [Theory]
    [InlineData(160, 45)]
    [InlineData(120, 35)]
    [InlineData(100, 30)]
    [InlineData(80, 24)]
    [InlineData(60, 16)]
    public void Target_detail_keeps_status_and_keyboard_bar_visible_across_reference_sizes(
        int width,
        int height)
    {
        var profile = CreateProfile();
        var dashboard = CreateReadyDashboard(profile);
        using var app = Application.Create(new VirtualTimeProvider());
        app.Init(DriverRegistry.Names.ANSI);
        VelaTerminalTheme.Register();
        using var shell = new VelaTerminalShell(new MainMenu().ViewModel, dashboard);
        app.Driver!.SetScreenSize(width, height);
        using var host = new TerminalGuiShellHost(app, shell);
        var session = app.Begin(shell);

        try
        {
            shell.SetCurrentProfile(profile);
            shell.ApplyPreflight(new AutomaticPreflightState(
                profile.Id,
                1,
                1,
                AutomaticPreflightStatus.Ready,
                dashboard,
                "预检已完成。"));
            shell.ShowOverview();
            Assert.True(app.Keyboard.RaiseKeyDownEvent(Key.Enter));
            app.LayoutAndDraw(forceRedraw: true);

            var rendered = app.Driver.ToString();
            Assert.Equal(VelaWorkspacePage.TargetDetail, shell.CurrentPage);
            Assert.Contains("目标预检详情", rendered, StringComparison.Ordinal);
            if (width >= 80)
            {
                Assert.Contains("状态总览", rendered, StringComparison.Ordinal);
            }
            Assert.Contains("PASS", rendered, StringComparison.Ordinal);
            Assert.Contains("导航 / 操作", rendered, StringComparison.Ordinal);
        }
        finally
        {
            app.End(session!);
        }
    }

    [Fact]
    public void Narrow_homepage_keeps_the_decision_line_single_row_and_actionable()
    {
        var profile = CreateProfile();
        using var shell = new VelaTerminalShell(
            new MainMenu().ViewModel,
            DashboardViewModel.CreateInitial(profile));
        shell.SetCurrentProfile(profile);
        shell.ApplyPreflight(new AutomaticPreflightState(
            profile.Id,
            1,
            1,
            AutomaticPreflightStatus.Attention,
            DashboardViewModel.CreateInitial(profile) with
            {
                ErrorMessage = "目标发行版未安装",
                Notices = System.Collections.Immutable.ImmutableArray.Create("目标发行版未安装")
            },
            "预检已完成，发现需要关注的问题。"));

        shell.AdaptTo(new System.Drawing.Rectangle(0, 0, 60, 16));
        shell.ShowOverview();

        Assert.DoesNotContain(Environment.NewLine, shell.WorkspaceText, StringComparison.Ordinal);
        Assert.Contains("未发现实例", shell.WorkspaceText, StringComparison.Ordinal);
        Assert.Contains("[R] 重新扫描", shell.WorkspaceText, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(160, "实例列表", "VHDX 路径")]
    [InlineData(120, "实例列表", "当前体积")]
    [InlineData(100, "实例列表", "当前体积")]
    [InlineData(80, "实例列表", "当前体积")]
    [InlineData(60, "未发现实例", "重新扫描")]
    public void Menu_one_projects_content_for_each_width_band(
        int width,
        string expected,
        string secondaryExpected)
    {
        var profile = CreateProfile();
        using var shell = new VelaTerminalShell(
            new MainMenu().ViewModel,
            DashboardViewModel.CreateInitial(profile));
        shell.SetCurrentProfile(profile);
        shell.ApplyPreflight(new AutomaticPreflightState(
            profile.Id,
            1,
            1,
            AutomaticPreflightStatus.Attention,
            new DashboardViewModel(
                MainMenu.ApplicationTitle,
                "档案：Ubuntu 24.04",
                "Ubuntu-24.04",
                true,
                TargetMappingState.Mismatched,
                TargetInspectionState.Available,
                new VhdxEvidenceViewModel(
                    1610612736,
                    DateTimeOffset.UtcNow,
                    true,
                    2L * PreflightOverviewFormatter.Tebibyte,
                    PreflightOverviewFormatter.Gibibyte),
                ImmutableArray.Create("Ubuntu-24.04"),
                ImmutableArray.Create("稀疏状态未知"),
                "目标映射不匹配",
                true,
                PreflightDataState.Available,
                PreflightDataState.Available),
            "预检已完成，发现需要关注的问题。"));
        shell.AdaptTo(new System.Drawing.Rectangle(0, 0, width, 30));
        shell.ShowOverview();

        Assert.Contains(expected, shell.WorkspaceText, StringComparison.Ordinal);
        if (width == 80)
        {
            Assert.DoesNotContain(secondaryExpected, shell.WorkspaceText, StringComparison.Ordinal);
        }
        else
        {
            Assert.Contains(secondaryExpected, shell.WorkspaceText, StringComparison.Ordinal);
        }
        Assert.Contains("[R]", shell.StatusText, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(160, 45, "实例列表")]
    [InlineData(120, 35, "实例列表")]
    [InlineData(100, 30, "实例列表")]
    [InlineData(80, 24, "实例列表")]
    [InlineData(60, 16, "实例 0 个")]
    public void Menu_one_visual_bands_keep_content_and_fixed_action_bar(
        int width,
        int height,
        string expectedContent)
    {
        using var app = Application.Create(new VirtualTimeProvider());
        app.Init(DriverRegistry.Names.ANSI);
        VelaTerminalTheme.Register();
        var profile = CreateProfile();
        using var shell = new VelaTerminalShell(
            new MainMenu().ViewModel,
            DashboardViewModel.CreateInitial(profile));
        app.Driver!.SetScreenSize(width, height);
        using var host = new TerminalGuiShellHost(app, shell);
        var session = app.Begin(shell);

        try
        {
            shell.SetCurrentProfile(profile);
            shell.ApplyPreflight(new AutomaticPreflightState(
                profile.Id,
                1,
                1,
                AutomaticPreflightStatus.Attention,
                DashboardViewModel.CreateInitial(profile),
                "预检已完成，发现需要关注的问题。"));
            shell.ShowOverview();
            app.LayoutAndDraw(forceRedraw: true);

            var rendered = app.Driver.ToString();
            Assert.Contains(expectedContent, rendered, StringComparison.Ordinal);
            Assert.Contains("导航 / 操作", rendered, StringComparison.Ordinal);
            Assert.Contains("[R]", rendered, StringComparison.Ordinal);
        }
        finally
        {
            app.End(session!);
        }
    }

    [Fact]
    public void Ready_home_shows_capacity_as_execution_context_not_as_a_blocker()
    {
        var profile = CreateProfile();
        using var shell = new VelaTerminalShell(
            new MainMenu().ViewModel,
            DashboardViewModel.CreateInitial(profile));
        shell.SetCurrentProfile(profile);
        shell.ApplyPreflight(new AutomaticPreflightState(
            profile.Id,
            1,
            1,
            AutomaticPreflightStatus.Ready,
            new DashboardViewModel(
                MainMenu.ApplicationTitle,
                "档案：Ubuntu 24.04",
                "Ubuntu-24.04",
                true,
                TargetMappingState.Matched,
                TargetInspectionState.Available,
                new VhdxEvidenceViewModel(
                    1_610_612_736,
                    DateTimeOffset.UtcNow,
                    true,
                    2L * PreflightOverviewFormatter.Tebibyte,
                    512L * PreflightOverviewFormatter.Gibibyte),
                ImmutableArray<string>.Empty,
                ImmutableArray<string>.Empty,
                null,
                true,
                PreflightDataState.Available,
                PreflightDataState.Available,
                ImmutableArray.Create(
                    new Vela.Core.Contracts.WslDistribution(
                        "Ubuntu-24.04",
                        Vela.Core.Contracts.WslDistributionState.Stopped,
                        2,
                        true))),
            "预检已完成。"));
        shell.AdaptTo(new System.Drawing.Rectangle(0, 0, 160, 30));
        shell.ShowOverview();

        Assert.Contains("实例列表", shell.WorkspaceText, StringComparison.Ordinal);
        Assert.Contains("Ubuntu-24.04", shell.WorkspaceText, StringComparison.Ordinal);
        Assert.Contains("READY", shell.WorkspaceText, StringComparison.Ordinal);
        Assert.DoesNotContain("阻断原因", shell.WorkspaceText, StringComparison.Ordinal);
    }

    [Fact]
    public void Overview_renders_reference_dashboard_cards_with_actionable_copy()
    {
        var profile = CreateProfile();
        using var app = Application.Create(new VirtualTimeProvider());
        app.Init(DriverRegistry.Names.ANSI);
        VelaTerminalTheme.Register();
        using var shell = new VelaTerminalShell(
            new MainMenu().ViewModel,
            DashboardViewModel.CreateInitial(profile));
        app.Driver!.SetScreenSize(160, 45);
        using var host = new TerminalGuiShellHost(app, shell);
        var session = app.Begin(shell);

        try
        {
            shell.SetCurrentProfile(profile);
            shell.ApplyPreflight(new AutomaticPreflightState(
                profile.Id,
                1,
                1,
                AutomaticPreflightStatus.Attention,
                new DashboardViewModel(
                    MainMenu.ApplicationTitle,
                    "档案：Ubuntu 24.04",
                    "Ubuntu-24.04",
                    true,
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
                    true,
                    PreflightDataState.Available,
                    PreflightDataState.Available,
                    ImmutableArray.Create(
                        new Vela.Core.Contracts.WslDistribution("Ubuntu-24.04", Vela.Core.Contracts.WslDistributionState.Stopped, 2, true),
                        new Vela.Core.Contracts.WslDistribution("docker-desktop", Vela.Core.Contracts.WslDistributionState.Running, 2, false),
                        new Vela.Core.Contracts.WslDistribution("podman-machine", Vela.Core.Contracts.WslDistributionState.Stopped, 2, false))),
                "预检已完成，发现需要关注的问题。"));
            shell.ShowOverview();
            app.LayoutAndDraw(forceRedraw: true);

            var rendered = app.Driver.ToString();
            Assert.Contains("执行目标选择", rendered);
            Assert.Contains("INFO", rendered);
            Assert.Contains("目标发行版未安装", rendered);
            Assert.Contains("实例列表（3）", rendered);
            Assert.Contains("Ubuntu-24.04", rendered);
            Assert.Contains("docker-desktop", rendered);
            Assert.Contains("状态（Status）", rendered);
        }
        finally
        {
            app.End(session!);
        }
    }

    private static Profile CreateProfile() => new(
        Guid.Parse("ed979041-296f-49fd-9aae-61ceacbb06c0"),
        "Ubuntu 24.04",
        "Ubuntu-24.04",
        "D:\\Vela\\ext4.vhdx",
        ShutdownMode.Global,
        TimeSpan.FromSeconds(45));

    private static DashboardViewModel CreateReadyDashboard(Profile profile) => new(
        MainMenu.ApplicationTitle,
        $"档案：{profile.DisplayName}",
        profile.DistroName,
        TargetConfigured: true,
        TargetMappingState.Matched,
        TargetInspectionState.Available,
        new VhdxEvidenceViewModel(
            124L * PreflightOverviewFormatter.Gibibyte,
            DateTimeOffset.UtcNow,
            true,
            551L * PreflightOverviewFormatter.Gibibyte,
            59L * PreflightOverviewFormatter.Gibibyte),
        ImmutableArray<string>.Empty,
        ImmutableArray<string>.Empty,
        ErrorMessage: null,
        LogsAvailable: true,
        RunningInventoryState: PreflightDataState.Available,
        LogAvailabilityState: PreflightDataState.Available,
        InstalledDistros: ImmutableArray.Create(
            new Vela.Core.Contracts.WslDistribution(
                profile.DistroName,
                Vela.Core.Contracts.WslDistributionState.Stopped,
                2,
                true,
                VhdxPath: profile.VhdxPath,
                VhdxSizeBytes: 124L * PreflightOverviewFormatter.Gibibyte)));
}
