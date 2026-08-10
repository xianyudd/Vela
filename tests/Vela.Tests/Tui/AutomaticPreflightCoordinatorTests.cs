using Vela.Core.Models;
using Vela.Tui.Application;

namespace Vela.Tests.Tui;

public sealed class AutomaticPreflightCoordinatorTests
{
    [Fact]
    public async Task Stale_mark_invalidates_an_inflight_preflight_result()
    {
        var completion = new TaskCompletionSource<DashboardViewModel>(TaskCreationOptions.RunContinuationsAsynchronously);
        var profile = CreateProfile("Ubuntu 24.04");
        using var coordinator = new AutomaticPreflightCoordinator((_, _) => completion.Task);

        var running = coordinator.Start(profile);
        coordinator.MarkStale(profile, "档案配置已变更。");
        completion.SetResult(DashboardViewModel.CreateInitial(profile));
        await running;

        Assert.Equal(AutomaticPreflightStatus.Stale, coordinator.Current.Status);
        Assert.Equal(2, coordinator.Current.Generation);
    }

    [Fact]
    public async Task New_profile_generation_wins_when_previous_preflight_finishes_last()
    {
        var first = new TaskCompletionSource<DashboardViewModel>(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = new TaskCompletionSource<DashboardViewModel>(TaskCreationOptions.RunContinuationsAsynchronously);
        var checks = new Queue<Func<Profile, CancellationToken, Task<DashboardViewModel>>>([
            (_, _) => first.Task,
            (_, _) => second.Task
        ]);
        var coordinator = new AutomaticPreflightCoordinator((profile, token) => checks.Dequeue().Invoke(profile, token));

        var firstRun = coordinator.Start(CreateProfile("Ubuntu 24.04"));
        var secondProfile = CreateProfile("Ubuntu 22.04");
        var secondRun = coordinator.Start(secondProfile);
        second.SetResult(DashboardViewModel.CreateInitial(secondProfile));
        await secondRun;
        first.SetResult(DashboardViewModel.CreateInitial(CreateProfile("Ubuntu 24.04")));
        await firstRun;

        Assert.Equal(secondProfile.Id, coordinator.Current.ProfileId);
        Assert.Equal(2, coordinator.Current.Generation);
        Assert.Equal(AutomaticPreflightStatus.Ready, coordinator.Current.Status);
    }

    [Fact]
    public async Task Warning_notice_keeps_preflight_in_attention_state()
    {
        var profile = CreateProfile("Ubuntu 24.04");
        var dashboard = DashboardViewModel.CreateInitial(profile) with
        {
            Notices = ImmutableArray.Create("稀疏状态未知")
        };
        using var coordinator = new AutomaticPreflightCoordinator(
            (_, _) => Task.FromResult(dashboard));

        await coordinator.Start(profile);

        Assert.Equal(AutomaticPreflightStatus.Attention, coordinator.Current.Status);
    }

    private static Profile CreateProfile(string name) => new(
        Guid.NewGuid(),
        name,
        name.Replace(' ', '-'),
        "D:\\Vela\\ext4.vhdx",
        ShutdownMode.Global,
        TimeSpan.FromSeconds(45));
}
