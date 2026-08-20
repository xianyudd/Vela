using System.Collections.Immutable;
using Vela.Application.Display;
using Vela.Application.Tui;
using Vela.Core.Models;

namespace Vela.Tests.Application;

/// <summary>
/// Complementary branch tests for <see cref="TuiReducer"/>: startup buffer
/// editing, failure completions, and async-result stale guards not exercised
/// in the primary test class.
/// </summary>
public sealed class TuiReducerBranchTests
{
    private static readonly Guid ProfileId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private static readonly Profile TestProfile = new(
        ProfileId,
        "Ubuntu 24.04",
        "Ubuntu-24.04",
        @"D:\WSL\Ubuntu\ext4.vhdx",
        ShutdownMode.Distro,
        TimeSpan.FromSeconds(30));

    private static TuiSessionState Ready => TuiSessionState.Initial() with
    {
        StartupStatus = StartupStatus.Ready,
        CurrentProfile = TestProfile
    };

    // ------------------------------------------------------------------
    // Startup buffer editing
    // ------------------------------------------------------------------

    [Fact]
    public void Reducer_AppendCharacter_BuffersInputAndCapsLength()
    {
        var state = TuiSessionState.Initial();
        state = TuiReducer.Reduce(state, new AppendStartupConfirmationCharacter('Y')).State;
        state = TuiReducer.Reduce(state, new AppendStartupConfirmationCharacter('E')).State;
        state = TuiReducer.Reduce(state, new AppendStartupConfirmationCharacter('S')).State;

        Assert.Equal("YES", state.StartupConfirmationInput);
        Assert.Equal(StartupStatus.Confirming, state.StartupStatus);

        // Buffer is capped at four characters; a fifth is ignored.
        state = TuiReducer.Reduce(state, new AppendStartupConfirmationCharacter('!')).State;
        Assert.Equal("YES", state.StartupConfirmationInput);
    }

    [Fact]
    public void Reducer_RemoveCharacter_RemovesLastButNotBelowEmpty()
    {
        var state = TuiSessionState.Initial();
        state = TuiReducer.Reduce(state, new AppendStartupConfirmationCharacter('Y')).State;
        state = TuiReducer.Reduce(state, new RemoveStartupConfirmationCharacter()).State;
        Assert.Equal(string.Empty, state.StartupConfirmationInput);

        // Removing from an empty buffer is a no-op.
        var empty = TuiReducer.Reduce(state, new RemoveStartupConfirmationCharacter());
        Assert.Equal(string.Empty, empty.State.StartupConfirmationInput);
    }

    [Fact]
    public void Reducer_AppendCharacter_IgnoredOnceStartupIsReady()
    {
        var transition = TuiReducer.Reduce(Ready, new AppendStartupConfirmationCharacter('Y'));
        Assert.Equal(string.Empty, transition.State.StartupConfirmationInput);
        Assert.Equal(StartupStatus.Ready, transition.State.StartupStatus);
    }

    // ------------------------------------------------------------------
    // Startup completion failed branch
    // ------------------------------------------------------------------

    [Fact]
    public void Reducer_StartupInitializationFailed_MovesToFailed()
    {
        var state = TuiSessionState.Initial() with
        {
            StartupStatus = StartupStatus.Initializing,
            StartupGeneration = 1
        };

        var transition = TuiReducer.Reduce(
            state,
            new StartupInitializationCompleted(1, Vela.Application.Startup.StartupInitializationOutcome.Failed("数据目录不可用")));

        Assert.Equal(StartupStatus.Failed, transition.State.StartupStatus);
    }

    // ------------------------------------------------------------------
    // Preflight guards and failure completion
    // ------------------------------------------------------------------

    [Fact]
    public void Reducer_PreflightFailed_RequiresMatchingGeneration()
    {
        var state = Ready with
        {
            LockedTarget = new LockedCompactionTarget(TestProfile, TestProfile.VhdxPath, LockedTargetQuality.SelectedProfile),
            PreflightStatus = PreflightStatus.Checking,
            PreflightGeneration = 4,
            PreflightProfileId = ProfileId
        };

        var stale = TuiReducer.Reduce(state, new PreflightFailed(ProfileId, 3, new DisplayMessage("旧错误", DisplayMessageSeverity.Error)));
        Assert.Equal(PreflightStatus.Checking, stale.State.PreflightStatus);
        Assert.Null(stale.State.LastPreflightError);

        var current = TuiReducer.Reduce(state, new PreflightFailed(ProfileId, 4, new DisplayMessage("预检失败", DisplayMessageSeverity.Error)));
        Assert.Equal(PreflightStatus.Failed, current.State.PreflightStatus);
        Assert.NotNull(current.State.LastPreflightError);
    }

    [Fact]
    public void Reducer_RefreshPreflight_RequiresLockedTarget()
    {
        // No locked target → no effect.
        var noTarget = TuiReducer.Reduce(Ready, new RefreshPreflight());
        Assert.Empty(noTarget.Effects);
        Assert.Equal(PreflightStatus.Idle, noTarget.State.PreflightStatus);
    }

    [Fact]
    public void Reducer_PreflightCompleted_InvalidReportGoesToAttention()
    {
        var invalid = new Vela.Core.Workflows.PreflightReport(
            new Vela.Core.Validation.ValidationResult(
                ImmutableArray.Create(new Vela.Core.Validation.ValidationError(
                    Vela.Core.Validation.ProfileValidationErrorCode.DistroNameRequired,
                    "错误"))),
            null,
            null,
            null,
            null);

        var state = Ready with
        {
            LockedTarget = new LockedCompactionTarget(TestProfile, TestProfile.VhdxPath, LockedTargetQuality.SelectedProfile),
            PreflightStatus = PreflightStatus.Checking,
            PreflightGeneration = 1,
            PreflightProfileId = ProfileId
        };

        var transition = TuiReducer.Reduce(state, new PreflightCompleted(ProfileId, 1, invalid));
        Assert.Equal(PreflightStatus.Attention, transition.State.PreflightStatus);
    }

    // ------------------------------------------------------------------
    // Impact / confirmation guards
    // ------------------------------------------------------------------

    [Fact]
    public void Reducer_ImpactEstimateFailed_RequiresMatchingRevision()
    {
        var state = Ready with
        {
            LockedTarget = new LockedCompactionTarget(TestProfile, TestProfile.VhdxPath, LockedTargetQuality.SelectedProfile),
            PreflightStatus = PreflightStatus.Ready,
            ImpactStatus = ImpactPreviewStatus.Estimating,
            ImpactRevision = 2
        };

        var stale = TuiReducer.Reduce(state, new ImpactEstimateFailed(1, new DisplayMessage("旧", DisplayMessageSeverity.Error)));
        Assert.Equal(ImpactPreviewStatus.Estimating, stale.State.ImpactStatus);

        var current = TuiReducer.Reduce(state, new ImpactEstimateFailed(2, new DisplayMessage("评估失败", DisplayMessageSeverity.Error)));
        Assert.Equal(ImpactPreviewStatus.Failed, current.State.ImpactStatus);
        Assert.NotNull(current.State.LastImpactError);
    }

    [Fact]
    public void Reducer_SubmitFirstY_RequiresReadyImpact()
    {
        // Locked but impact not ready → nothing happens.
        var state = Ready with
        {
            LockedTarget = new LockedCompactionTarget(TestProfile, TestProfile.VhdxPath, LockedTargetQuality.SelectedProfile),
            PreflightStatus = PreflightStatus.Ready
        };

        var transition = TuiReducer.Reduce(state, new SubmitFirstY());
        Assert.Equal(ConfirmationStatus.Idle, transition.State.ConfirmationStatus);
        Assert.Empty(transition.Effects);
    }

    [Fact]
    public void Reducer_SubmitSecondY_RequiresAwaitingSecondY()
    {
        // First Y never submitted → second Y is ignored.
        var state = Ready with
        {
            LockedTarget = new LockedCompactionTarget(TestProfile, TestProfile.VhdxPath, LockedTargetQuality.SelectedProfile),
            PreflightStatus = PreflightStatus.Ready,
            ImpactStatus = ImpactPreviewStatus.Ready
        };

        var transition = TuiReducer.Reduce(state, new SubmitSecondY());
        Assert.Empty(transition.Effects);
        Assert.Equal(CompactionStatus.Idle, transition.State.CompactionStatus);
    }

    [Fact]
    public void Reducer_CancelOrBack_WhenRunningEmitsRequestStop()
    {
        var running = Ready with
        {
            LockedTarget = new LockedCompactionTarget(TestProfile, TestProfile.VhdxPath, LockedTargetQuality.SelectedProfile),
            ConfirmationStatus = ConfirmationStatus.Confirmed,
            CompactionStatus = CompactionStatus.Running,
            CompactionGeneration = 1
        };

        var transition = TuiReducer.Reduce(running, new CancelOrBack());
        Assert.IsType<RequestStopEffect>(Assert.Single(transition.Effects));
    }

    // ------------------------------------------------------------------
    // Run history / log detail async completions
    // ------------------------------------------------------------------

    [Fact]
    public void Reducer_RunHistoryFailed_RequiresMatchingRevision()
    {
        var state = Ready with { RunHistoryRevision = 3 };

        var stale = TuiReducer.Reduce(state, new RunHistoryFailed(2, new DisplayMessage("旧", DisplayMessageSeverity.Error)));
        Assert.Null(stale.State.RunHistoryError);

        var current = TuiReducer.Reduce(state, new RunHistoryFailed(3, new DisplayMessage("读取失败", DisplayMessageSeverity.Error)));
        Assert.NotNull(current.State.RunHistoryError);
    }

    [Fact]
    public void Reducer_LogDetailLoaded_RequiresMatchingRunIdAndRevision()
    {
        var runId = Guid.Parse("33333333-3333-3333-3333-333333333333");
        var logEvent = new DisplayRunEvent(
            1,
            DateTimeOffset.UtcNow,
            "DiskPart 预检",
            RunEventLevel.Information,
            null,
            null,
            "完成");
        var state = Ready with
        {
            LogDetailRevision = 5,
            CurrentLogDetailRunId = runId
        };

        var wrongRun = TuiReducer.Reduce(state, new LogDetailLoaded(5, Guid.NewGuid(), ImmutableArray.Create(logEvent)));
        Assert.Empty(wrongRun.State.LogDetailEvents);

        var wrongRevision = TuiReducer.Reduce(state, new LogDetailLoaded(4, runId, ImmutableArray.Create(logEvent)));
        Assert.Empty(wrongRevision.State.LogDetailEvents);

        var ok = TuiReducer.Reduce(state, new LogDetailLoaded(5, runId, ImmutableArray.Create(logEvent)));
        Assert.Single(ok.State.LogDetailEvents);
    }

    [Fact]
    public void Reducer_LogDetailFailed_RequiresMatchingRevision()
    {
        var state = Ready with { LogDetailRevision = 2 };

        var stale = TuiReducer.Reduce(state, new LogDetailFailed(1, new DisplayMessage("旧", DisplayMessageSeverity.Error)));
        Assert.Empty(stale.State.LogDetailEvents);

        var current = TuiReducer.Reduce(state, new LogDetailFailed(2, new DisplayMessage("失败", DisplayMessageSeverity.Error)));
        Assert.Empty(current.State.LogDetailEvents);
    }

    // ------------------------------------------------------------------
    // Execution journal
    // ------------------------------------------------------------------

    [Fact]
    public void Reducer_ExecutionJournalEvent_RequiresCurrentGeneration()
    {
        var logEvent = new DisplayRunEvent(
            1,
            DateTimeOffset.UtcNow,
            "DiskPart 压缩",
            RunEventLevel.Information,
            null,
            null,
            "进行中");
        var running = Ready with
        {
            CompactionStatus = CompactionStatus.Running,
            CompactionGeneration = 2
        };

        var stale = TuiReducer.Reduce(running, new ExecutionJournalEvent(1, logEvent));
        Assert.Empty(stale.State.LogDetailEvents);

        var current = TuiReducer.Reduce(running, new ExecutionJournalEvent(2, logEvent));
        Assert.Single(current.State.LogDetailEvents);
    }

    [Fact]
    public void Reducer_ExecutionJournalEvent_TransitionsLaunchingToRunning()
    {
        var logEvent = new DisplayRunEvent(
            1,
            DateTimeOffset.UtcNow,
            "DiskPart 压缩",
            RunEventLevel.Information,
            null,
            null,
            "启动");
        var launching = Ready with
        {
            CompactionStatus = CompactionStatus.Launching,
            CompactionGeneration = 1
        };

        var transition = TuiReducer.Reduce(launching, new ExecutionJournalEvent(1, logEvent));
        Assert.Equal(CompactionStatus.Running, transition.State.CompactionStatus);
    }

    [Fact]
    public void Reducer_MoveLogSelection_NoOpWhenEmpty()
    {
        var transition = TuiReducer.Reduce(Ready, new MoveLogSelection(1));
        Assert.Equal(0, transition.State.SelectedMenuIndex);
    }
}
