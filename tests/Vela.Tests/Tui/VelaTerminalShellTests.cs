using Terminal.Gui;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.Time;
using Vela.Core.Contracts;
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
    public void Design_sidebar_exposes_workspace_and_log_archive_modules()
    {
        using var app = Terminal.Gui.App.Application.Create(new VirtualTimeProvider());
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
            shell.ShowOverview();
            app.LayoutAndDraw(forceRedraw: true);

            var rendered = app.Driver.ToString();
            Assert.Contains("01  工作区", rendered, StringComparison.Ordinal);
            Assert.Contains("02  日志归档", rendered, StringComparison.Ordinal);
            Assert.DoesNotContain("03  日志归档", rendered, StringComparison.Ordinal);
            Assert.DoesNotContain("04  最近运行", rendered, StringComparison.Ordinal);
            Assert.DoesNotContain("05  日志分析", rendered, StringComparison.Ordinal);
        }
        finally
        {
            app.End(session!);
        }
    }

    [Fact]
    public void Design_detail_uses_target_info_check_details_and_breadcrumb_copy()
    {
        var profile = CreateProfile();
        var dashboard = CreateReadyDashboard(profile);
        using var app = Terminal.Gui.App.Application.Create(new VirtualTimeProvider());
        app.Init(DriverRegistry.Names.ANSI);
        VelaTerminalTheme.Register();
        using var shell = new VelaTerminalShell(new MainMenu().ViewModel, dashboard);
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
                AutomaticPreflightStatus.Ready,
                dashboard,
                "预检已完成。"));
            shell.ShowOverview();
            Assert.True(app.Keyboard.RaiseKeyDownEvent(Key.Enter));
            app.LayoutAndDraw(forceRedraw: true);

            var rendered = app.Driver.ToString();
            Assert.Contains("TARGET INFO", rendered, StringComparison.Ordinal);
            Assert.Contains("CHECK DETAILS", rendered, StringComparison.Ordinal);
            Assert.Contains("目标发行版", rendered, StringComparison.Ordinal);
            Assert.Contains("实例锁定状态", rendered, StringComparison.Ordinal);
            Assert.Contains("✓ Ubuntu-24.04 预检通过", rendered, StringComparison.Ordinal);
            Assert.Contains("> ② 环境预检", rendered, StringComparison.Ordinal);
            Assert.Contains("③ 影响评估", rendered, StringComparison.Ordinal);
        }
        finally
        {
            app.End(session!);
        }
    }

    [Fact]
    public void Design_impact_view_renders_actual_reclaimable_space_for_locked_target()
    {
        var profile = CreateProfile();
        var dashboard = CreateReadyDashboard(profile);
        using var app = Terminal.Gui.App.Application.Create(new VirtualTimeProvider());
        app.Init(DriverRegistry.Names.ANSI);
        VelaTerminalTheme.Register();
        using var shell = new VelaTerminalShell(new MainMenu().ViewModel, dashboard);
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
                AutomaticPreflightStatus.Ready,
                dashboard,
                "预检已完成。"));
            shell.ShowOverview();
            Assert.True(app.Keyboard.RaiseKeyDownEvent(Key.Enter));
            shell.SelectMenuIndex(1);
            var revision = shell.NavigationRevision;
            Assert.True(shell.ApplyCompactionImpactEstimate(
                revision,
                profile.DistroName,
                new CompactionImpactEstimate(
                    CompactionImpactStatus.Estimated,
                    124L * PreflightOverviewFormatter.Gibibyte,
                    118L * PreflightOverviewFormatter.Gibibyte,
                    6L * PreflightOverviewFormatter.Gibibyte,
                    "按根文件系统已用空间估算。")));
            app.LayoutAndDraw(forceRedraw: true);

            var rendered = app.Driver.ToString();
            Assert.Contains("影响评估（Impact Assessment）", rendered, StringComparison.Ordinal);
            Assert.Contains("Sparse diskpart", rendered, StringComparison.Ordinal);
            Assert.Contains("预计可回收空间", rendered, StringComparison.Ordinal);
            Assert.Contains("6.00 GiB", rendered, StringComparison.Ordinal);
            Assert.Contains("[Y]  确认执行", rendered, StringComparison.Ordinal);
            Assert.DoesNotContain("进入 YES", shell.WorkspaceText, StringComparison.Ordinal);
        }
        finally
        {
            app.End(session!);
        }
    }

    [Theory]
    [InlineData('q')]
    [InlineData('Q')]
    public void Design_q_shortcut_requests_exit_from_read_only_views(char input)
    {
        using var shell = new VelaTerminalShell(
            new MainMenu().ViewModel,
            DashboardViewModel.CreateInitial(CreateProfile()));
        var actions = new List<MainMenuAction>();
        shell.ActionRequested += actions.Add;

        Assert.True(shell.NewKeyDownEvent(new Key(input)));

        Assert.Equal([MainMenuAction.Exit], actions);
    }

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
        Assert.Contains("日志归档", shell.ContentTitle, StringComparison.Ordinal);
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
    public void Confirmation_page_requires_the_second_y_without_requesting_an_action_for_other_input()
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
        Assert.Contains("请按 Y 再次确认执行", shell.StatusText);
        Assert.Empty(actions);
    }

    [Theory]
    [InlineData('y')]
    [InlineData('Y')]
    public void Compaction_confirmation_accepts_the_second_y_without_text_entry(char input)
    {
        var profile = CreateProfile();
        using var shell = new VelaTerminalShell(
            new MainMenu().ViewModel,
            DashboardViewModel.CreateInitial(profile));
        ConfirmationInputResult? submitted = null;
        shell.ConfirmationSubmitted += result => submitted = result;

        shell.ShowConfirmation(MainMenu.CreateExecuteConfirmation(
            profile,
            System.Collections.Immutable.ImmutableArray<Vela.Core.Contracts.WslDistribution>.Empty));

        Assert.True(shell.NewKeyDownEvent(new Key(input)));

        Assert.NotNull(submitted);
        Assert.Equal(ConfirmationInputStatus.Accepted, submitted!.Status);
        Assert.Equal("Y", submitted.Response);
        Assert.NotEqual(VelaWorkspacePage.Confirmation, shell.CurrentPage);
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
    public void Missing_profile_does_not_block_a_locked_installed_target()
    {
        var profile = CreateProfile();
        var dockerPath = @"D:\Docker\wsl\data\ext4.vhdx";
        var dashboard = CreateReadyDashboard(profile) with
        {
            MappingState = LxssResolutionStatus.NotFound,
            Notices = ImmutableArray.Create("目标发行版未安装"),
            ErrorMessage = "目标发行版未安装",
            InstalledDistros = ImmutableArray.Create(
                new Vela.Core.Contracts.WslDistribution(
                    "docker-desktop",
                    Vela.Core.Contracts.WslDistributionState.Stopped,
                    2,
                    true,
                    dockerPath,
                    10L * PreflightOverviewFormatter.Gibibyte))
        };
        using var shell = new VelaTerminalShell(new MainMenu().ViewModel, dashboard);
        var targetDashboard = dashboard with
        {
            ProfileTitle = "档案：docker-desktop",
            DistroName = "docker-desktop",
            TargetConfigured = true,
            MappingState = LxssResolutionStatus.Matched,
            InspectionState = TargetInspectionState.Available,
            VhdxEvidence = new VhdxEvidenceViewModel(
                10L * PreflightOverviewFormatter.Gibibyte,
                DateTimeOffset.UtcNow,
                true,
                2L * PreflightOverviewFormatter.Tebibyte,
                512L * PreflightOverviewFormatter.Gibibyte,
                dockerPath),
            Notices = ImmutableArray<string>.Empty,
            ErrorMessage = null,
            ConfiguredVhdxPath = dockerPath
        };
        shell.TargetPreflightRequested += () => shell.ApplyPreflight(new AutomaticPreflightState(
            profile.Id,
            2,
            2,
            AutomaticPreflightStatus.Ready,
            targetDashboard,
            "目标预检已完成。"));
        var actions = new List<MainMenuAction>();
        shell.ActionRequested += actions.Add;
        shell.SetCurrentProfile(profile);
        shell.ApplyPreflight(new AutomaticPreflightState(
            profile.Id,
            1,
            1,
            AutomaticPreflightStatus.Attention,
            dashboard,
            "预检已完成，发现需要关注的问题。"));
        shell.ShowOverview();

        shell.NewKeyDownEvent(Key.Enter);
        Assert.Equal("docker-desktop", shell.LockedTargetName);
        Assert.Equal(VelaWorkspacePage.TargetDetail, shell.CurrentPage);

        shell.NewKeyDownEvent(Key.Enter);

        Assert.Equal(VelaWorkspacePage.ActionPreview, shell.CurrentPage);
        shell.RequestAction(1);

        Assert.Equal([MainMenuAction.ExecuteCompaction], actions);
        var request = shell.CreateLockedCompactionRequest(profile, Guid.NewGuid());
        Assert.NotNull(request);
        Assert.Equal("docker-desktop", request!.Profile.DistroName);
        Assert.Equal(dockerPath, request.Profile.VhdxPath);
    }

    [Fact]
    public void Running_locked_target_stays_blocked_until_the_instance_is_stopped()
    {
        var profile = CreateProfile();
        var dashboard = CreateReadyDashboard(profile) with
        {
            InstalledDistros = ImmutableArray.Create(
                new Vela.Core.Contracts.WslDistribution(
                    profile.DistroName,
                    Vela.Core.Contracts.WslDistributionState.Running,
                    2,
                    true,
                    profile.VhdxPath,
                    10L * PreflightOverviewFormatter.Gibibyte))
        };
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

        shell.NewKeyDownEvent(Key.Enter);

        Assert.Equal(VelaWorkspacePage.TargetDetail, shell.CurrentPage);
        Assert.Contains("尚未通过", shell.StatusText, StringComparison.Ordinal);
        Assert.Empty(actions);

        shell.NewKeyDownEvent(Key.CursorRight);

        Assert.Equal(VelaWorkspacePage.TargetDetail, shell.CurrentPage);
        Assert.Contains("尚未通过", shell.StatusText, StringComparison.Ordinal);
        Assert.Empty(actions);
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
        Assert.Contains("预计可回收空间", shell.WorkspaceText, StringComparison.Ordinal);
        Assert.DoesNotContain(profile.DistroName, shell.WorkspaceText, StringComparison.Ordinal);
    }

    [Fact]
    public void Locking_a_nonconfigured_target_requests_a_target_specific_preflight()
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
                    profile.VhdxPath,
                    124L * PreflightOverviewFormatter.Gibibyte),
                new Vela.Core.Contracts.WslDistribution(
                    "docker-desktop",
                    Vela.Core.Contracts.WslDistributionState.Stopped,
                    2,
                    false,
                    @"D:\Docker\wsl\data\ext4.vhdx",
                    65L * PreflightOverviewFormatter.Gibibyte))
        };
        using var shell = new VelaTerminalShell(new MainMenu().ViewModel, dashboard);
        var requested = 0;
        shell.TargetPreflightRequested += () => requested++;
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
        Assert.Equal(1, requested);
        Assert.Equal(VelaWorkspacePage.TargetDetail, shell.CurrentPage);
        Assert.Contains("目标预检详情", shell.ContentTitle, StringComparison.Ordinal);
    }

    [Fact]
    public void Compaction_preview_renders_the_estimated_reclaimable_space_for_the_locked_target()
    {
        var profile = CreateProfile();
        var dashboard = CreateReadyDashboard(profile) with
        {
            InstalledDistros = ImmutableArray.Create(
                new Vela.Core.Contracts.WslDistribution(
                    "docker-desktop",
                    Vela.Core.Contracts.WslDistributionState.Stopped,
                    2,
                    false,
                    @"D:\Docker\wsl\data\ext4.vhdx",
                    10L * PreflightOverviewFormatter.Gibibyte))
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
        shell.NewKeyDownEvent(Key.Enter);

        shell.SelectMenuIndex(1);
        var revision = shell.NavigationRevision;

        var applied = shell.ApplyCompactionImpactEstimate(
            revision,
            "docker-desktop",
            new CompactionImpactEstimate(
                CompactionImpactStatus.Estimated,
                CurrentVhdxSizeBytes: 10L * PreflightOverviewFormatter.Gibibyte,
                UsedBytes: 4L * PreflightOverviewFormatter.Gibibyte,
                ReclaimableBytes: 6L * PreflightOverviewFormatter.Gibibyte,
                "按根文件系统已用空间估算。"));

        Assert.True(applied);
        Assert.Contains("预计可回收空间  6.00 GiB", shell.WorkspaceText, StringComparison.Ordinal);
    }

    [Fact]
    public void Locked_target_uses_target_preflight_evidence_for_current_vhdx_size()
    {
        var profile = CreateProfile();
        var inventorySize = 124L * PreflightOverviewFormatter.Gibibyte;
        var targetSnapshotSize = 126L * PreflightOverviewFormatter.Gibibyte;
        var dashboard = CreateReadyDashboard(profile) with
        {
            VhdxEvidence = new VhdxEvidenceViewModel(
                targetSnapshotSize,
                DateTimeOffset.UtcNow,
                true,
                551L * PreflightOverviewFormatter.Gibibyte,
                59L * PreflightOverviewFormatter.Gibibyte,
                profile.VhdxPath),
            InstalledDistros = ImmutableArray.Create(
                new Vela.Core.Contracts.WslDistribution(
                    profile.DistroName,
                    Vela.Core.Contracts.WslDistributionState.Stopped,
                    2,
                    true,
                    profile.VhdxPath,
                    inventorySize))
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

        shell.NewKeyDownEvent(Key.Enter);

        Assert.Equal(profile.DistroName, shell.LockedTargetName);
        Assert.Equal(targetSnapshotSize, shell.LockedTargetVhdxSizeBytes);
        shell.SelectMenuIndex(1);
        Assert.Contains("当前体积   126.00 GiB", shell.WorkspaceText, StringComparison.Ordinal);
    }

    [Fact]
    public void Compaction_preview_ignores_an_estimate_from_an_old_navigation_revision()
    {
        var profile = CreateProfile();
        var dashboard = CreateReadyDashboard(profile) with
        {
            InstalledDistros = ImmutableArray.Create(
                new Vela.Core.Contracts.WslDistribution(
                    "docker-desktop",
                    Vela.Core.Contracts.WslDistributionState.Stopped,
                    2,
                    false,
                    @"D:\Docker\wsl\data\ext4.vhdx",
                    10L * PreflightOverviewFormatter.Gibibyte))
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
        shell.NewKeyDownEvent(Key.Enter);
        shell.SelectMenuIndex(1);
        var oldRevision = shell.NavigationRevision;
        shell.SelectMenuIndex(2);

        var applied = shell.ApplyCompactionImpactEstimate(
            oldRevision,
            "docker-desktop",
            new CompactionImpactEstimate(
                CompactionImpactStatus.Estimated,
                10L * PreflightOverviewFormatter.Gibibyte,
                4L * PreflightOverviewFormatter.Gibibyte,
                6L * PreflightOverviewFormatter.Gibibyte,
                "按根文件系统已用空间估算。"));

        Assert.False(applied);
        Assert.DoesNotContain("6.00 GiB", shell.WorkspaceText, StringComparison.Ordinal);
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

        using var app = Terminal.Gui.App.Application.Create(new VirtualTimeProvider());
        app.Init(DriverRegistry.Names.ANSI);
        VelaTerminalTheme.Register();
        using var host = new TerminalGuiShellHost(app, shell);
        var session = app.Begin(shell);

        try
        {
            Assert.True(app.Keyboard.RaiseKeyDownEvent(new Key('y')));
            Assert.True(app.Keyboard.RaiseKeyDownEvent(Key.Y.WithShift));
        }
        finally
        {
            app.End(session!);
        }

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
    [InlineData(160, 45)]
    [InlineData(120, 35)]
    [InlineData(100, 30)]
    [InlineData(80, 24)]
    [InlineData(60, 16)]
    public void Run_state_views_keep_the_locked_target_and_fixed_action_bar(
        int width,
        int height)
    {
        using var app = Terminal.Gui.App.Application.Create(new VirtualTimeProvider());
        app.Init(DriverRegistry.Names.ANSI);
        VelaTerminalTheme.Register();
        using var shell = new VelaTerminalShell(
            new MainMenu().ViewModel,
            DashboardViewModel.CreateInitial(CreateProfile()));
        app.Driver!.SetScreenSize(width, height);
        using var host = new TerminalGuiShellHost(app, shell);
        var session = app.Begin(shell);

        try
        {
            shell.ShowRunProgress(new RunProgressViewModel(
                RunProgressState.Running,
                "正在读取 journal 事件。",
                Percent: null,
                TargetName: "docker-desktop",
                VhdxPath: @"D:\Docker\wsl\data\ext4.vhdx",
                LogLines: ["[INFO] compact target locked"]));
            app.LayoutAndDraw(forceRedraw: true);

            Assert.Equal(VelaWorkspacePage.Running, shell.CurrentPage);
            Assert.Contains("STEP2_RUNNING", shell.WorkspaceText, StringComparison.Ordinal);
            Assert.Contains("docker-desktop", shell.WorkspaceText, StringComparison.Ordinal);
            Assert.Contains("Console Log", shell.WorkspaceText, StringComparison.Ordinal);
            var rendered = app.Driver.ToString();
            Assert.Contains(
                width < 120 ? "VHDX OPTIMIZING" : "Optimizing VHDX Block Allocations",
                rendered,
                StringComparison.Ordinal);
            if (width >= 80 && height >= 24)
            {
                Assert.Contains("Console Log · LIVE", rendered, StringComparison.Ordinal);
                Assert.Contains("compact target locked", rendered, StringComparison.Ordinal);
                Assert.Contains("░", rendered, StringComparison.Ordinal);
            }
            Assert.Contains("导航 / 操作", rendered, StringComparison.Ordinal);
            Assert.True(app.Keyboard.RaiseKeyDownEvent(Key.CursorDown));

            shell.ShowRunProgress(new RunProgressViewModel(
                RunProgressState.Succeeded,
                "运行终态：已完成。",
                Percent: null,
                TargetName: "docker-desktop",
                Elapsed: TimeSpan.FromSeconds(42),
                ReclaimedBytes: 53L * PreflightOverviewFormatter.Gibibyte));

            app.LayoutAndDraw(forceRedraw: true);
            Assert.Contains("DONE", shell.WorkspaceText, StringComparison.Ordinal);
            Assert.Contains("53.00 GiB", shell.WorkspaceText, StringComparison.Ordinal);
            if (width >= 80 && height >= 24)
            {
                var resultRendered = app.Driver.ToString();
                Assert.Contains("DONE", resultRendered, StringComparison.Ordinal);
                Assert.Contains("53.00 GiB", resultRendered, StringComparison.Ordinal);
            }
            Assert.True(app.Keyboard.RaiseKeyDownEvent(Key.Enter));
            Assert.Equal(VelaWorkspacePage.Overview, shell.CurrentPage);
        }
        finally
        {
            app.End(session!);
        }
    }

    [Theory]
    [InlineData("目标档案", "[Enter]刷新")]
    [InlineData("最近运行", "[Enter]刷新")]
    [InlineData("运行日志", "[Enter]查看日志")]
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
        using var app = Terminal.Gui.App.Application.Create(new VirtualTimeProvider());
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
        using var app = Terminal.Gui.App.Application.Create(new VirtualTimeProvider());
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
            Assert.Contains("Console Log · TUI", rendered, StringComparison.Ordinal);
            Assert.DoesNotContain("文件管理器", rendered, StringComparison.Ordinal);
            Assert.DoesNotContain("打开日志目录", rendered, StringComparison.Ordinal);
            Assert.Contains("[Enter]刷新日志", shell.StatusText, StringComparison.Ordinal);
        }
        finally
        {
            app.End(session!);
        }
    }

    [Fact]
    public void Design_log_archive_renders_history_table_and_selection_affordance()
    {
        var started = new DateTimeOffset(2026, 8, 10, 14, 15, 4, TimeSpan.Zero);
        var entries = new RunHistorySnapshot(
            ImmutableArray.Create(
                new RunHistoryEntry(
                    Guid.NewGuid(),
                    started,
                    started.AddSeconds(4),
                    "Ubuntu 24.04",
                    OperationIntent.Compact,
                    TerminalResult.Succeeded,
                    82L * PreflightOverviewFormatter.Gibibyte,
                    IsMalformed: false,
                    ErrorMessage: null),
                new RunHistoryEntry(
                    Guid.NewGuid(),
                    started.AddDays(-1),
                    started.AddDays(-1).AddSeconds(3),
                    "Debian",
                    OperationIntent.Compact,
                    TerminalResult.DiskPartCompactFailed,
                    null,
                    IsMalformed: false,
                    ErrorMessage: "压缩失败")),
            ErrorMessage: null);

        using var app = Terminal.Gui.App.Application.Create(new VirtualTimeProvider());
        app.Init(DriverRegistry.Names.ANSI);
        VelaTerminalTheme.Register();
        using var shell = new VelaTerminalShell(
            new MainMenu().ViewModel,
            DashboardViewModel.CreateInitial(CreateProfile()));
        var requested = new List<MainMenuAction>();
        shell.ActionRequested += requested.Add;
        app.Driver!.SetScreenSize(160, 45);
        using var host = new TerminalGuiShellHost(app, shell);
        var session = app.Begin(shell);

        try
        {
            shell.ShowLogArchive(entries);
            app.LayoutAndDraw(forceRedraw: true);

            var rendered = app.Driver.ToString();
            Assert.Equal(VelaWorkspacePage.Logs, shell.CurrentPage);
            Assert.Contains("日志归档（2）", rendered, StringComparison.Ordinal);
            Assert.Contains("执行时间 (UTC+8)", rendered, StringComparison.Ordinal);
            Assert.Contains("Ubuntu 24.04", rendered, StringComparison.Ordinal);
            Assert.Contains("82.00 GiB", rendered, StringComparison.Ordinal);
            Assert.Contains("SUCCESS", rendered, StringComparison.Ordinal);
            Assert.Contains("❯", rendered, StringComparison.Ordinal);
            Assert.Contains("[Enter] 查看详细日志", shell.StatusText, StringComparison.Ordinal);
            Assert.True(app.Keyboard.RaiseKeyDownEvent(Key.Enter));
            Assert.Equal(new[] { MainMenuAction.OpenLogs }, requested);
        }
        finally
        {
            app.End(session!);
        }
    }

    [Fact]
    public void Design_log_detail_renders_inline_log_viewer_and_returns_to_archive()
    {
        var started = new DateTimeOffset(2026, 8, 10, 14, 15, 4, TimeSpan.Zero);
        var entry = new RunHistoryEntry(
            Guid.Parse("e0d6d9f3-9ec2-43b5-9f90-76d949d17f08"),
            started,
            started.AddSeconds(4),
            "Ubuntu 24.04",
            OperationIntent.Compact,
            TerminalResult.Succeeded,
            82L * PreflightOverviewFormatter.Gibibyte,
            IsMalformed: false,
            ErrorMessage: null);

        using var app = Terminal.Gui.App.Application.Create(new VirtualTimeProvider());
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
            shell.ShowLogArchive(new RunHistorySnapshot(
                ImmutableArray.Create(entry),
                ErrorMessage: null));
            shell.ShowLogDetail(
                entry,
                new RunLogSnapshot(
                    ImmutableArray.Create(
                        new RunLogLine("[1] 2026-08-10T14:15:04.102Z Information Validation RunCreated", RunEventLevel.Information),
                        new RunLogLine("[2] 2026-08-10T14:15:04.450Z Information Snapshot VhdxSnapshot", RunEventLevel.Information)),
                    WasTailTruncated: false,
                    ErrorMessage: null));
            app.LayoutAndDraw(forceRedraw: true);

            var rendered = app.Driver.ToString();
            Assert.Equal(VelaWorkspacePage.LogAnalysis, shell.CurrentPage);
            Assert.Contains("Task ID: v-task-e0d6d9f3", rendered, StringComparison.Ordinal);
            Assert.Contains("UTF-8 / LF", rendered, StringComparison.Ordinal);
            Assert.Contains("Console Log · TUI", rendered, StringComparison.Ordinal);
            Assert.Contains("2026-08-10", rendered, StringComparison.Ordinal);
            Assert.Contains("RunCreated", rendered, StringComparison.Ordinal);
            Assert.DoesNotContain("分析范围", rendered, StringComparison.Ordinal);
            Assert.Contains("[Esc] 返回日志归档", shell.StatusText, StringComparison.Ordinal);

            Assert.True(app.Keyboard.RaiseKeyDownEvent(Key.Esc));
            app.LayoutAndDraw(forceRedraw: true);
            Assert.Equal(VelaWorkspacePage.Logs, shell.CurrentPage);
            Assert.Contains("日志归档（1）", app.Driver.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            app.End(session!);
        }
    }

    [Fact]
    public void Design_module_shortcuts_switch_between_workspace_and_log_archive()
    {
        using var app = Terminal.Gui.App.Application.Create(new VirtualTimeProvider());
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
            shell.ShowOverview();
            Assert.True(app.Keyboard.RaiseKeyDownEvent(Key.D2));
            Assert.Equal(MainMenuAction.OpenLogs, shell.SelectedAction);
            Assert.Equal(VelaWorkspacePage.Logs, shell.CurrentPage);

            Assert.True(app.Keyboard.RaiseKeyDownEvent(Key.D1));
            Assert.Equal(MainMenuAction.Preflight, shell.SelectedAction);
            Assert.Equal(VelaWorkspacePage.Overview, shell.CurrentPage);
        }
        finally
        {
            app.End(session!);
        }
    }

    [Fact]
    public void Narrow_log_analysis_keeps_summary_and_error_signal_visible()
    {
        using var app = Terminal.Gui.App.Application.Create(new VirtualTimeProvider());
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
        using var app = Terminal.Gui.App.Application.Create(new VirtualTimeProvider());
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
            Assert.Contains("Console Log · TUI", rendered, StringComparison.Ordinal);
            Assert.DoesNotContain("打开日志目录", rendered, StringComparison.Ordinal);
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
        using var app = Terminal.Gui.App.Application.Create(new VirtualTimeProvider());
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
            Assert.Contains("工作区", rendered);
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
        using var app = Terminal.Gui.App.Application.Create(new VirtualTimeProvider());
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
                MappingState: null,
                TargetInspectionState.Available,
                VhdxEvidence: null,
                RunningDistros: System.Collections.Immutable.ImmutableArray.Create("Ubuntu-24.04"),
                Notices: System.Collections.Immutable.ImmutableArray.Create("稀疏状态未知"),
                ErrorMessage: "稀疏状态未知",
                LogsAvailable: true,
                RunningInventoryState: PreflightDataState.Available,
                LogAvailabilityState: PreflightDataState.Available,
                InstalledDistros: ImmutableArray.Create(
                    new Vela.Core.Contracts.WslDistribution(
                        "Ubuntu-24.04",
                        Vela.Core.Contracts.WslDistributionState.Stopped,
                        2,
                        true,
                        profile.VhdxPath,
                        2L * PreflightOverviewFormatter.Gibibyte))),
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
        using var app = Terminal.Gui.App.Application.Create(new VirtualTimeProvider());
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
    public void Driver_resize_from_minimal_canvas_restores_page_heading()
    {
        using var app = Terminal.Gui.App.Application.Create(new VirtualTimeProvider());
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
            shell.ShowRunProgress(new RunProgressViewModel(
                RunProgressState.Running,
                "正在读取 journal 事件。",
                Percent: null,
                TargetName: "docker-desktop"));
            app.LayoutAndDraw(forceRedraw: true);

            app.Driver.SetScreenSize(80, 24);
            app.LayoutAndDraw(forceRedraw: true);

            Assert.Contains("运行进度", app.Driver.ToString(), StringComparison.Ordinal);
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
        Assert.Contains("[R]重扫", shell.StatusText, StringComparison.Ordinal);
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
        Assert.Contains("[↑↓]实例", shell.StatusText, StringComparison.Ordinal);
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
        using var app = Terminal.Gui.App.Application.Create(new VirtualTimeProvider());
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
            MappingState = LxssResolutionStatus.Matched,
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
                    Vela.Core.Contracts.WslDistributionState.Stopped,
                    2,
                    false,
                    @"D:\Docker\wsl\data\ext4.vhdx",
                    65L * PreflightOverviewFormatter.Gibibyte)),
            RunningInventoryState = PreflightDataState.Available,
            LogAvailabilityState = PreflightDataState.Available,
            LogsAvailable = true
        };
        using var app = Terminal.Gui.App.Application.Create(new VirtualTimeProvider());
        app.Init(DriverRegistry.Names.ANSI);
        VelaTerminalTheme.Register();
        using var shell = new VelaTerminalShell(new MainMenu().ViewModel, dashboard);
        var targetDashboard = dashboard with
        {
            ProfileTitle = "档案：docker-desktop",
            DistroName = "docker-desktop",
            TargetConfigured = true,
            MappingState = LxssResolutionStatus.Matched,
            InspectionState = TargetInspectionState.Available,
            VhdxEvidence = new VhdxEvidenceViewModel(
                65L * PreflightOverviewFormatter.Gibibyte,
                DateTimeOffset.UtcNow,
                true,
                2L * PreflightOverviewFormatter.Tebibyte,
                512L * PreflightOverviewFormatter.Gibibyte,
                @"D:\Docker\wsl\data\ext4.vhdx"),
            Notices = ImmutableArray<string>.Empty,
            ErrorMessage = null,
            ConfiguredVhdxPath = @"D:\Docker\wsl\data\ext4.vhdx"
        };
        shell.TargetPreflightRequested += () => shell.ApplyPreflight(new AutomaticPreflightState(
            profile.Id,
            2,
            2,
            AutomaticPreflightStatus.Ready,
            targetDashboard,
            "目标预检已完成。"));
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
            Assert.Equal(VelaWorkspacePage.TargetDetail, shell.CurrentPage);
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
    public void Horizontal_arrows_move_through_workspace_steps_without_starting_compaction(
        int width,
        int height)
    {
        var profile = CreateProfile();
        var dashboard = CreateReadyDashboard(profile);
        using var app = Terminal.Gui.App.Application.Create(new VirtualTimeProvider());
        app.Init(DriverRegistry.Names.ANSI);
        VelaTerminalTheme.Register();
        using var shell = new VelaTerminalShell(new MainMenu().ViewModel, dashboard);
        app.Driver!.SetScreenSize(width, height);
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

            Assert.True(app.Keyboard.RaiseKeyDownEvent(Key.CursorRight));
            Assert.Equal(VelaWorkspacePage.TargetDetail, shell.CurrentPage);
            Assert.Contains(
                width < 110 ? "[←→]步骤" : "[←→]  切换步骤",
                shell.StatusText,
                StringComparison.Ordinal);

            Assert.True(app.Keyboard.RaiseKeyDownEvent(Key.CursorRight));
            Assert.Equal(VelaWorkspacePage.ActionPreview, shell.CurrentPage);
            Assert.Empty(actions);

            Assert.True(app.Keyboard.RaiseKeyDownEvent(Key.CursorLeft));
            Assert.Equal(VelaWorkspacePage.TargetDetail, shell.CurrentPage);
            Assert.True(app.Keyboard.RaiseKeyDownEvent(Key.CursorLeft));
            Assert.Equal(VelaWorkspacePage.Overview, shell.CurrentPage);
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
        using var app = Terminal.Gui.App.Application.Create(new VirtualTimeProvider());
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
            if (width >= 80)
            {
                Assert.Contains("TARGET INFO", rendered, StringComparison.Ordinal);
            }
            if (width >= 140 && height >= 40)
            {
                Assert.Contains("CHECK DETAILS", rendered, StringComparison.Ordinal);
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
                LxssResolutionStatus.Mismatched,
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
        using var app = Terminal.Gui.App.Application.Create(new VirtualTimeProvider());
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
                LxssResolutionStatus.Matched,
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
        using var app = Terminal.Gui.App.Application.Create(new VirtualTimeProvider());
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
                    MappingState: null,
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
        LxssResolutionStatus.Matched,
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
