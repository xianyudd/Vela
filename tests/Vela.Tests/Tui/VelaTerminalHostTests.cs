using Vela.Core.Models;
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
