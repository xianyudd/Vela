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

    [Fact]
    public async Task Progress_refresh_advances_the_revision_and_reports_the_elapsed_time()
    {
        var profile = CreateProfile("Ubuntu 24.04");
        var completion = new TaskCompletionSource<DashboardViewModel>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var coordinator = new AutomaticPreflightCoordinator((_, _) => completion.Task);
        var published = new List<AutomaticPreflightState>();
        coordinator.StateChanged += published.Add;

        var running = coordinator.Start(profile);
        var refreshed = coordinator.TryRefreshChecking(TimeSpan.FromSeconds(4));

        Assert.True(refreshed);
        Assert.Equal(AutomaticPreflightStatus.Checking, coordinator.Current.Status);
        Assert.Equal(TimeSpan.FromSeconds(4), coordinator.Current.Elapsed);
        Assert.Equal(profile.Id, coordinator.Current.ProfileId);
        Assert.Equal(1, coordinator.Current.Generation);
        Assert.Equal(2, coordinator.Current.Revision);
        Assert.Contains("4", coordinator.Current.Message!, StringComparison.Ordinal);
        Assert.Equal(2, published.Count);

        completion.SetResult(DashboardViewModel.CreateInitial(profile));
        await running;
    }

    [Fact]
    public void Progress_refresh_below_one_second_reports_no_elapsed_time_yet()
    {
        var profile = CreateProfile("Ubuntu 24.04");
        var completion = new TaskCompletionSource<DashboardViewModel>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var coordinator = new AutomaticPreflightCoordinator((_, _) => completion.Task);

        _ = coordinator.Start(profile);
        var refreshed = coordinator.TryRefreshChecking(TimeSpan.FromMilliseconds(400));

        Assert.True(refreshed);
        Assert.DoesNotContain("已用", coordinator.Current.Message!, StringComparison.Ordinal);
        Assert.Equal(TimeSpan.FromMilliseconds(400), coordinator.Current.Elapsed);
        completion.SetResult(DashboardViewModel.CreateInitial(profile));
    }

    [Fact]
    public void Progress_refresh_clamps_a_negative_elapsed_time_to_zero()
    {
        var profile = CreateProfile("Ubuntu 24.04");
        var completion = new TaskCompletionSource<DashboardViewModel>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var coordinator = new AutomaticPreflightCoordinator((_, _) => completion.Task);

        _ = coordinator.Start(profile);
        coordinator.TryRefreshChecking(TimeSpan.FromSeconds(-5));

        Assert.Equal(TimeSpan.Zero, coordinator.Current.Elapsed);
        completion.SetResult(DashboardViewModel.CreateInitial(profile));
    }

    [Fact]
    public void Progress_refresh_is_rejected_before_any_preflight_starts()
    {
        using var coordinator = new AutomaticPreflightCoordinator(
            (profile, _) => Task.FromResult(DashboardViewModel.CreateInitial(profile)));

        Assert.False(coordinator.TryRefreshChecking(TimeSpan.FromSeconds(1)));
        Assert.Equal(AutomaticPreflightStatus.Idle, coordinator.Current.Status);
        Assert.Equal(0, coordinator.Current.Revision);
    }

    [Fact]
    public async Task Progress_refresh_stops_once_the_preflight_completed()
    {
        var profile = CreateProfile("Ubuntu 24.04");
        using var coordinator = new AutomaticPreflightCoordinator(
            (requested, _) => Task.FromResult(DashboardViewModel.CreateInitial(requested)));

        await coordinator.Start(profile);
        var revisionAfterCompletion = coordinator.Current.Revision;

        // The ticker races with completion by design; a refresh after the run
        // finished must be a no-op so the terminal state is never overwritten.
        Assert.False(coordinator.TryRefreshChecking(TimeSpan.FromSeconds(9)));
        Assert.Equal(revisionAfterCompletion, coordinator.Current.Revision);
        Assert.Null(coordinator.Current.Elapsed);
    }

    [Fact]
    public void Progress_refresh_is_rejected_after_disposal_without_throwing()
    {
        var profile = CreateProfile("Ubuntu 24.04");
        var completion = new TaskCompletionSource<DashboardViewModel>(TaskCreationOptions.RunContinuationsAsynchronously);
        var coordinator = new AutomaticPreflightCoordinator((_, _) => completion.Task);

        _ = coordinator.Start(profile);
        coordinator.Dispose();

        Assert.False(coordinator.TryRefreshChecking(TimeSpan.FromSeconds(2)));
        completion.SetResult(DashboardViewModel.CreateInitial(profile));
    }

    private static Profile CreateProfile(string name) => new(
        Guid.NewGuid(),
        name,
        name.Replace(' ', '-'),
        "D:\\Vela\\ext4.vhdx",
        ShutdownMode.Global,
        TimeSpan.FromSeconds(45));
}
