using System.Collections.Immutable;
using Vela.Core.Models;
using Vela.Core.Contracts;
using Vela.Tui.Application;
using Vela.Tui.Menu;
using Vela.Tui.Views;

namespace Vela.Tests.Tui;

public sealed class VelaTerminalHostTests
{
    [Fact]
    public async Task Start_runs_preflight_automatically_and_applies_the_current_result_on_the_ui_dispatcher()
    {
        var profile = CreateProfile();
        var expected = DashboardViewModel.CreateInitial(profile);
        var completion = new TaskCompletionSource<DashboardViewModel>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var coordinator = new AutomaticPreflightCoordinator((_, _) => completion.Task);
        var dispatcher = new QueuedDispatcher();
        using var shell = new VelaTerminalShell(new MainMenu().ViewModel, expected);
        using var host = new VelaTerminalHost(shell, coordinator, dispatcher);

        var running = host.Start(profile);
        dispatcher.RunAll();

        Assert.Equal(AutomaticPreflightStatus.Checking, shell.PreflightState.Status);

        completion.SetResult(expected);
        await running;
        dispatcher.RunAll();

        Assert.Equal(AutomaticPreflightStatus.Ready, shell.PreflightState.Status);
        Assert.Equal(profile.Id, shell.PreflightState.ProfileId);
    }

    [Fact]
    public async Task Delayed_update_for_superseded_profile_does_not_replace_current_shell_state()
    {
        var first = new TaskCompletionSource<DashboardViewModel>(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = new TaskCompletionSource<DashboardViewModel>(TaskCreationOptions.RunContinuationsAsynchronously);
        var sources = new Queue<Func<Task<DashboardViewModel>>>([() => first.Task, () => second.Task]);
        using var coordinator = new AutomaticPreflightCoordinator((_, _) => sources.Dequeue().Invoke());
        var dispatcher = new QueuedDispatcher();
        using var shell = new VelaTerminalShell(new MainMenu().ViewModel, DashboardViewModel.CreateInitial(CreateProfile("Initial")));
        using var host = new VelaTerminalHost(shell, coordinator, dispatcher);
        var firstProfile = CreateProfile("First");
        var secondProfile = CreateProfile("Second");

        var firstRun = host.Start(firstProfile);
        var secondRun = host.Start(secondProfile);
        second.SetResult(DashboardViewModel.CreateInitial(secondProfile));
        await secondRun;
        first.SetResult(DashboardViewModel.CreateInitial(firstProfile));
        await firstRun;
        dispatcher.RunAll();

        Assert.Equal(secondProfile.Id, shell.PreflightState.ProfileId);
        Assert.Equal(AutomaticPreflightStatus.Ready, shell.PreflightState.Status);
    }

    [Fact]
    public async Task Reordered_ui_updates_keep_the_newest_preflight_state()
    {
        var profile = CreateProfile();
        var completion = new TaskCompletionSource<DashboardViewModel>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var coordinator = new AutomaticPreflightCoordinator((_, _) => completion.Task);
        var dispatcher = new ReorderingDispatcher();
        using var shell = new VelaTerminalShell(new MainMenu().ViewModel, DashboardViewModel.CreateInitial(profile));
        using var host = new VelaTerminalHost(shell, coordinator, dispatcher);

        var running = host.Start(profile);
        completion.SetResult(DashboardViewModel.CreateInitial(profile));
        await running;
        dispatcher.RunInReverseOrder();

        Assert.Equal(AutomaticPreflightStatus.Ready, shell.PreflightState.Status);
    }

    [Fact]
    public void Dispose_prevents_queued_updates_from_touching_the_shell()
    {
        var profile = CreateProfile();
        using var coordinator = new AutomaticPreflightCoordinator(
            (_, _) => Task.FromResult(DashboardViewModel.CreateInitial(profile)));
        var dispatcher = new QueuedDispatcher();
        using var shell = new VelaTerminalShell(new MainMenu().ViewModel, DashboardViewModel.CreateInitial(profile));
        var host = new VelaTerminalHost(shell, coordinator, dispatcher);

        _ = host.Start(profile);
        host.Dispose();
        dispatcher.RunAll();

        Assert.Equal(AutomaticPreflightStatus.Idle, shell.PreflightState.Status);
    }

    [Fact]
    public async Task Start_preserving_target_selection_keeps_the_locked_row_through_target_preflight()
    {
        var baseProfile = CreateProfile();
        var targetProfile = baseProfile with
        {
            DisplayName = "docker-desktop",
            DistroName = "docker-desktop",
            VhdxPath = @"D:\Docker\wsl\data\ext4.vhdx"
        };
        var baseDashboard = DashboardViewModel.CreateInitial(baseProfile) with
        {
            MappingState = LxssResolutionStatus.Matched,
            InspectionState = TargetInspectionState.Available,
            VhdxEvidence = new VhdxEvidenceViewModel(
                10L * PreflightOverviewFormatter.Gibibyte,
                DateTimeOffset.UtcNow,
                true,
                2L * PreflightOverviewFormatter.Tebibyte,
                512L * PreflightOverviewFormatter.Gibibyte),
            InstalledDistros = ImmutableArray.Create(
                new WslDistribution(
                    "docker-desktop",
                    WslDistributionState.Stopped,
                    2,
                    false,
                    targetProfile.VhdxPath,
                    10L * PreflightOverviewFormatter.Gibibyte)),
            RunningInventoryState = PreflightDataState.Available,
            LogAvailabilityState = PreflightDataState.Available,
            LogsAvailable = true
        };
        var targetDashboard = DashboardViewModel.CreateInitial(targetProfile) with
        {
            MappingState = LxssResolutionStatus.Matched,
            InspectionState = TargetInspectionState.Available,
            VhdxEvidence = baseDashboard.VhdxEvidence,
            InstalledDistros = baseDashboard.InstalledDistros,
            RunningInventoryState = PreflightDataState.Available,
            LogAvailabilityState = PreflightDataState.Available,
            LogsAvailable = true
        };
        var completion = new TaskCompletionSource<DashboardViewModel>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var coordinator = new AutomaticPreflightCoordinator(
            (requested, _) =>
            {
                Assert.Equal(targetProfile.DistroName, requested.DistroName);
                return completion.Task;
            });
        var dispatcher = new QueuedDispatcher();
        using var shell = new VelaTerminalShell(new MainMenu().ViewModel, baseDashboard);
        using var host = new VelaTerminalHost(shell, coordinator, dispatcher);

        shell.SetCurrentProfile(baseProfile);
        shell.ApplyPreflight(new AutomaticPreflightState(
            baseProfile.Id,
            1,
            1,
            AutomaticPreflightStatus.Ready,
            baseDashboard,
            "预检已完成。"));
        shell.ShowOverview();
        shell.NewKeyDownEvent(Terminal.Gui.Input.Key.Enter);

        var running = host.Start(targetProfile, preserveTargetSelection: true);
        dispatcher.RunAll();

        Assert.Equal("docker-desktop", shell.LockedTargetName);
        Assert.Equal(VelaWorkspacePage.TargetDetail, shell.CurrentPage);
        Assert.Equal(AutomaticPreflightStatus.Checking, shell.PreflightState.Status);

        completion.SetResult(targetDashboard);
        await running;
        dispatcher.RunAll();

        Assert.Equal("docker-desktop", shell.LockedTargetName);
        Assert.Equal("docker-desktop", shell.Overview.DistroName);
        Assert.Equal(AutomaticPreflightStatus.Ready, shell.PreflightState.Status);
    }

    [Fact]
    public async Task Checking_progress_is_written_to_the_status_line_with_the_elapsed_time()
    {
        var profile = CreateProfile();
        var completion = new TaskCompletionSource<DashboardViewModel>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var coordinator = new AutomaticPreflightCoordinator((_, _) => completion.Task);
        var dispatcher = new QueuedDispatcher();
        using var shell = new VelaTerminalShell(new MainMenu().ViewModel, DashboardViewModel.CreateInitial(profile));
        // A very long interval keeps the host's own ticker out of this assertion,
        // so the refresh below is the only progress publication in play.
        using var host = new VelaTerminalHost(shell, coordinator, dispatcher, TimeSpan.FromMinutes(10));

        var running = host.Start(profile);
        Assert.True(coordinator.TryRefreshChecking(TimeSpan.FromSeconds(7)));
        dispatcher.RunAll();

        Assert.Equal(AutomaticPreflightStatus.Checking, shell.PreflightState.Status);
        Assert.Contains("已用 7 秒", shell.StatusText, StringComparison.Ordinal);

        completion.SetResult(DashboardViewModel.CreateInitial(profile));
        await running;
        dispatcher.RunAll();

        // A terminal state restores the navigation hint: the progress text is
        // transient and must not survive the check it described.
        Assert.Equal(AutomaticPreflightStatus.Ready, shell.PreflightState.Status);
        Assert.DoesNotContain("已用 7 秒", shell.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Checking_progress_ticker_runs_while_the_preflight_is_in_flight_and_stops_after_it()
    {
        var profile = CreateProfile();
        var completion = new TaskCompletionSource<DashboardViewModel>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var coordinator = new AutomaticPreflightCoordinator((_, _) => completion.Task);
        var dispatcher = new QueuedDispatcher();
        using var shell = new VelaTerminalShell(new MainMenu().ViewModel, DashboardViewModel.CreateInitial(profile));
        using var host = new VelaTerminalHost(shell, coordinator, dispatcher, TimeSpan.FromMilliseconds(15));

        var running = host.Start(profile);
        await WaitForRevisionAsync(coordinator, atLeast: 3);

        Assert.True(coordinator.Current.Revision >= 3, "An in-flight check must keep publishing progress.");
        Assert.True(coordinator.Current.Elapsed > TimeSpan.Zero);

        completion.SetResult(DashboardViewModel.CreateInitial(profile));
        await running;
        var revisionAtCompletion = coordinator.Current.Revision;
        await Task.Delay(TimeSpan.FromMilliseconds(150));

        // The ticker stops itself the moment the status leaves Checking.
        Assert.Equal(revisionAtCompletion, coordinator.Current.Revision);
        Assert.Equal(AutomaticPreflightStatus.Ready, coordinator.Current.Status);
    }

    [Fact]
    public async Task Dispose_stops_the_checking_progress_ticker()
    {
        var profile = CreateProfile();
        var completion = new TaskCompletionSource<DashboardViewModel>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var coordinator = new AutomaticPreflightCoordinator((_, _) => completion.Task);
        var dispatcher = new QueuedDispatcher();
        using var shell = new VelaTerminalShell(new MainMenu().ViewModel, DashboardViewModel.CreateInitial(profile));
        var host = new VelaTerminalHost(shell, coordinator, dispatcher, TimeSpan.FromMilliseconds(15));

        var running = host.Start(profile);
        await WaitForRevisionAsync(coordinator, atLeast: 2);
        host.Dispose();
        // Let a tick that was already awake finish before the baseline is taken,
        // so the assertion measures the stopped ticker and not that last tick.
        await Task.Delay(TimeSpan.FromMilliseconds(80));
        var revisionAtDisposal = coordinator.Current.Revision;
        await Task.Delay(TimeSpan.FromMilliseconds(200));

        Assert.Equal(revisionAtDisposal, coordinator.Current.Revision);

        completion.SetResult(DashboardViewModel.CreateInitial(profile));
        await running;
    }

    private static async Task WaitForRevisionAsync(AutomaticPreflightCoordinator coordinator, long atLeast)
    {
        var deadline = Environment.TickCount64 + 10_000;
        while (coordinator.Current.Revision < atLeast && Environment.TickCount64 < deadline)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(10));
        }
    }

    private static Profile CreateProfile(string name = "Ubuntu 24.04") => new(
        Guid.NewGuid(),
        name,
        name.Replace(' ', '-'),
        "D:\\Vela\\ext4.vhdx",
        ShutdownMode.Global,
        TimeSpan.FromSeconds(45));

    private sealed class QueuedDispatcher : ITuiDispatcher
    {
        private readonly Queue<Action> _pending = new();

        public void Post(Action action) => _pending.Enqueue(action);

        public void RunAll()
        {
            while (_pending.TryDequeue(out var action))
            {
                action();
            }
        }
    }

    private sealed class ReorderingDispatcher : ITuiDispatcher
    {
        private readonly List<Action> _pending = [];

        public void Post(Action action) => _pending.Add(action);

        public void RunInReverseOrder()
        {
            foreach (var action in _pending.AsEnumerable().Reverse())
            {
                action();
            }
        }
    }
}
