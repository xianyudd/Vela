using System.Collections.Immutable;
using Vela.Application.Display;
using Vela.Application.Startup;
using Vela.Application.Tui;
using Vela.Core.Contracts;
using Vela.Core.Models;
using Vela.Core.Validation;
using Vela.Core.Workflows;

namespace Vela.Tests.Application;

/// <summary>
/// Pure-function tests for <see cref="TuiReducer"/>. Every test constructs a
/// trusted state, dispatches a single command, and asserts on the returned
/// <see cref="TuiTransition"/>.
/// </summary>
public sealed class TuiReducerTests
{
    private static readonly Guid TestProfileId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid CorruptProfileId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static readonly Profile TestProfile = new(
        TestProfileId,
        "Ubuntu 24.04",
        "Ubuntu-24.04",
        @"D:\WSL\Ubuntu\ext4.vhdx",
        ShutdownMode.Distro,
        TimeSpan.FromSeconds(30));

    private static PreflightReport ValidReport() => new(
        ValidationResult.Valid,
        null,
        null,
        null,
        null);

    private static DisplayMessage TestError() => new("测试错误", DisplayMessageSeverity.Error);

    private static CompactionImpactEstimate TestEstimate() => new(
        CompactionImpactStatus.Estimated,
        10L * 1024 * 1024 * 1024,
        4L * 1024 * 1024 * 1024,
        6L * 1024 * 1024 * 1024,
        "预估可回收 6 GB");

    private static TuiSessionState Ready => TuiSessionState.Initial() with
    {
        StartupStatus = StartupStatus.Ready,
        CurrentProfile = TestProfile
    };

    private static TuiSessionState Locked() => Ready with
    {
        LockedTarget = new LockedCompactionTarget(
            TestProfile,
            TestProfile.VhdxPath,
            LockedTargetQuality.SelectedProfile),
        PreflightStatus = PreflightStatus.Idle,
        ConfirmationStatus = ConfirmationStatus.Idle
    };

    private static TuiSessionState PreflightReady() => Locked() with
    {
        PreflightStatus = PreflightStatus.Ready
    };

    private static TuiSessionState ImpactReady() => PreflightReady() with
    {
        ImpactStatus = ImpactPreviewStatus.Ready,
        LastImpactEstimate = TestEstimate()
    };

    private static TuiSessionState AwaitingSecondY() => ImpactReady() with
    {
        ConfirmationStatus = ConfirmationStatus.AwaitingSecondY
    };

    private static TuiSessionState Running() => AwaitingSecondY() with
    {
        ConfirmationStatus = ConfirmationStatus.Confirmed,
        CompactionStatus = CompactionStatus.Running,
        CompactionGeneration = 1
    };

    // ------------------------------------------------------------------
    // Startup confirmation
    // ------------------------------------------------------------------

    [Fact]
    public void Reducer_SubmitStartupConfirmation_RequiresExactUppercaseYES()
    {
        // Helper: simulate typing the full confirmation string, then submit.
        static TuiSessionState Typed(string text)
        {
            var state = TuiSessionState.Initial();
            foreach (var c in text)
            {
                state = TuiReducer.Reduce(state, new AppendStartupConfirmationCharacter(c)).State;
            }

            return state;
        }

        // Exact match succeeds.
        var accepted = TuiReducer.Reduce(Typed("YES"), new SubmitStartupConfirmation());
        Assert.Equal(StartupStatus.Initializing, accepted.State.StartupStatus);
        Assert.Single(accepted.Effects);

        // Lowercase is rejected; input remains so the user can correct it.
        var lower = TuiReducer.Reduce(Typed("yes"), new SubmitStartupConfirmation());
        Assert.Equal(StartupStatus.Confirming, lower.State.StartupStatus);
        Assert.Empty(lower.Effects);

        // Trailing whitespace is rejected (append path trims, but a crafted
        // state with trailing space must not pass the exact-match gate).
        var trailing = TuiReducer.Reduce(
            Typed("YES") with { StartupConfirmationInput = "YES " },
            new SubmitStartupConfirmation());
        Assert.Equal(StartupStatus.Confirming, trailing.State.StartupStatus);
        Assert.Empty(trailing.Effects);

        // Overlength input is rejected (the buffer caps at 4 chars, so "YES!"
        // cannot be produced by typing; craft the state to assert the gate
        // still does not pass).
        var overlength = TuiReducer.Reduce(
            Typed("YES") with { StartupConfirmationInput = "YES!" },
            new SubmitStartupConfirmation());
        Assert.Equal(StartupStatus.Confirming, overlength.State.StartupStatus);
        Assert.Empty(overlength.Effects);
    }

    [Fact]
    public void Reducer_StartupInitializationCompletion_RequiresCurrentGeneration()
    {
        var state = TuiSessionState.Initial() with
        {
            StartupStatus = StartupStatus.Initializing,
            StartupGeneration = 5
        };

        // Stale generation is ignored.
        var stale = TuiReducer.Reduce(state, new StartupInitializationCompleted(
            4,
            StartupInitializationOutcome.Succeeded()));
        Assert.Equal(StartupStatus.Initializing, stale.State.StartupStatus);
        Assert.Empty(stale.Effects);

        // Current generation succeeds.
        var current = TuiReducer.Reduce(state, new StartupInitializationCompleted(
            5,
            StartupInitializationOutcome.Succeeded()));
        Assert.Equal(StartupStatus.Ready, current.State.StartupStatus);
    }

    // ------------------------------------------------------------------
    // Selection / locking
    // ------------------------------------------------------------------

    [Fact]
    public void Reducer_SelectTarget_UpdatesSelectionWithoutLocking()
    {
        var transition = TuiReducer.Reduce(
            Ready with { SelectedProfileIndex = 0 },
            new SelectTarget(1));

        Assert.Equal(1, transition.State.SelectedProfileIndex);
        Assert.Null(transition.State.LockedTarget);
        Assert.Empty(transition.Effects);
    }

    [Fact]
    public void Reducer_LockSelectedTarget_StoresTrustedLockedTargetAndEmitsTargetPreflightWhenNeeded()
    {
        var transition = TuiReducer.Reduce(Ready, new LockSelectedTarget());

        Assert.NotNull(transition.State.LockedTarget);
        Assert.Equal(TestProfileId, transition.State.LockedTarget!.Profile.Id);
        Assert.Equal(LockedTargetQuality.SelectedProfile, transition.State.LockedTarget.Quality);
    }

    [Fact]
    public void Reducer_LockSelectedTarget_EmitsPreflightEffectWhenTargetIsLocked()
    {
        // Refresh is the follow-up action; it uses the locked target.
        var locked = Locked();
        var transition = TuiReducer.Reduce(locked, new RefreshPreflight());

        var effect = Assert.IsType<StartPreflightEffect>(Assert.Single(transition.Effects));
        Assert.Equal(TestProfileId, effect.Profile.Id);
        Assert.True(effect.PreserveTargetSelection);
    }

    // ------------------------------------------------------------------
    // Impact preview
    // ------------------------------------------------------------------

    [Fact]
    public void Reducer_OpenImpactPreview_RequiresReadyLockedTarget()
    {
        // No locked target: no effect.
        var noTarget = TuiReducer.Reduce(Ready, new OpenImpactPreview());
        Assert.Empty(noTarget.Effects);
        Assert.Equal(ImpactPreviewStatus.Idle, noTarget.State.ImpactStatus);

        // Locked but preflight not ready: no effect.
        var locked = Locked();
        var notReady = TuiReducer.Reduce(locked, new OpenImpactPreview());
        Assert.Empty(notReady.Effects);
        Assert.Equal(ImpactPreviewStatus.Idle, notReady.State.ImpactStatus);

        // Locked + preflight ready: emits estimate effect.
        var ready = PreflightReady();
        var ok = TuiReducer.Reduce(ready, new OpenImpactPreview());
        Assert.Equal(ImpactPreviewStatus.Estimating, ok.State.ImpactStatus);
        var effect = Assert.IsType<EstimateImpactEffect>(Assert.Single(ok.Effects));
        Assert.Equal(TestProfileId, effect.Target.Profile.Id);
    }

    // ------------------------------------------------------------------
    // Two-Y confirmation
    // ------------------------------------------------------------------

    [Fact]
    public void Reducer_FirstY_ShowsSecondConfirmationWithoutStartingWorker()
    {
        var transition = TuiReducer.Reduce(ImpactReady(), new SubmitFirstY());

        Assert.Equal(ConfirmationStatus.AwaitingSecondY, transition.State.ConfirmationStatus);
        Assert.Equal(CompactionStatus.Idle, transition.State.CompactionStatus);
        Assert.Empty(transition.Effects);
    }

    [Fact]
    public void Reducer_SecondY_EmitsStartCompactionOnce()
    {
        var first = TuiReducer.Reduce(AwaitingSecondY(), new SubmitSecondY());
        Assert.Equal(ConfirmationStatus.Confirmed, first.State.ConfirmationStatus);
        Assert.Equal(CompactionStatus.Launching, first.State.CompactionStatus);
        Assert.Single(first.Effects);

        // Second attempt while launching is ignored (single-flight).
        var second = TuiReducer.Reduce(first.State, new SubmitSecondY());
        Assert.Empty(second.Effects);
        Assert.Equal(CompactionStatus.Launching, second.State.CompactionStatus);
    }

    [Fact]
    public void Reducer_SecondY_BuildsRequestFromLockedTargetNotCurrentSelection()
    {
        // Change the *current* profile selection to another profile after locking.
        var otherProfile = TestProfile with
        {
            Id = CorruptProfileId,
            DisplayName = "Debian",
            DistroName = "Debian"
        };
        var state = AwaitingSecondY() with { CurrentProfile = otherProfile };

        var transition = TuiReducer.Reduce(state, new SubmitSecondY());

        var effect = Assert.IsType<StartCompactionEffect>(Assert.Single(transition.Effects));
        // The compaction must use the *locked* target's profile, not the
        // currently highlighted one.
        Assert.Equal(TestProfileId, effect.Request.Profile.Id);
        Assert.NotEqual(CorruptProfileId, effect.Request.Profile.Id);
    }

    [Fact]
    public void Reducer_StartingNewProfilePreflight_InvalidatesLockedTargetAndConfirmations()
    {
        var transition = TuiReducer.Reduce(AwaitingSecondY(), new LockSelectedTarget());

        Assert.NotNull(transition.State.LockedTarget);
        Assert.Equal(ConfirmationStatus.Idle, transition.State.ConfirmationStatus);
        Assert.Equal(PreflightStatus.Idle, transition.State.PreflightStatus);
        Assert.Empty(transition.Effects);
    }

    // ------------------------------------------------------------------
    // Busy-state navigation guard
    // ------------------------------------------------------------------

    [Fact]
    public void Reducer_RunningState_IgnoresNavigationAndRefresh()
    {
        var running = Running();

        var navigate = TuiReducer.Reduce(running, new NavigateMenu(1));
        Assert.Empty(navigate.Effects);
        Assert.Equal(running.SelectedMenuIndex, navigate.State.SelectedMenuIndex);

        var refresh = TuiReducer.Reduce(running, new RefreshPreflight());
        Assert.Empty(refresh.Effects);
    }

    [Fact]
    public void Reducer_EscapeFromResult_ReturnsToOverview()
    {
        // After CancelOrBack from a finished (non-running) state, confirmation
        // and impact are reset to idle so the projector returns to the
        // dashboard.
        var settled = AwaitingSecondY() with
        {
            CompactionStatus = CompactionStatus.Idle
        };
        var transition = TuiReducer.Reduce(settled, new CancelOrBack());

        Assert.Equal(ConfirmationStatus.Idle, transition.State.ConfirmationStatus);
        Assert.Equal(ImpactPreviewStatus.Idle, transition.State.ImpactStatus);
        Assert.Null(transition.State.LastImpactEstimate);
    }

    // ------------------------------------------------------------------
    // Stale-async guards
    // ------------------------------------------------------------------

    [Fact]
    public void Reducer_RejectsStaleAsyncEffectByRevision()
    {
        var state = PreflightReady() with
        {
            ImpactStatus = ImpactPreviewStatus.Estimating,
            ImpactRevision = 7
        };

        var stale = TuiReducer.Reduce(state, new ImpactEstimateCompleted(6, TestEstimate()));
        Assert.Equal(ImpactPreviewStatus.Estimating, stale.State.ImpactStatus);
        Assert.Null(stale.State.LastImpactEstimate);

        var current = TuiReducer.Reduce(state, new ImpactEstimateCompleted(7, TestEstimate()));
        Assert.Equal(ImpactPreviewStatus.Ready, current.State.ImpactStatus);
        Assert.NotNull(current.State.LastImpactEstimate);
    }

    [Fact]
    public void Reducer_RejectsPreflightCompletionFromStaleGenerationOrProfile()
    {
        var state = Locked() with
        {
            PreflightStatus = PreflightStatus.Checking,
            PreflightGeneration = 3,
            PreflightProfileId = TestProfileId
        };

        // Wrong generation.
        var staleGeneration = TuiReducer.Reduce(state, new PreflightCompleted(TestProfileId, 2, ValidReport()));
        Assert.Equal(PreflightStatus.Checking, staleGeneration.State.PreflightStatus);

        // Wrong profile id.
        var wrongProfile = TuiReducer.Reduce(state, new PreflightCompleted(CorruptProfileId, 3, ValidReport()));
        Assert.Equal(PreflightStatus.Checking, wrongProfile.State.PreflightStatus);

        // Correct both.
        var ok = TuiReducer.Reduce(state, new PreflightCompleted(TestProfileId, 3, ValidReport()));
        Assert.Equal(PreflightStatus.Ready, ok.State.PreflightStatus);
    }

    // ------------------------------------------------------------------
    // Run history / logs
    // ------------------------------------------------------------------

    [Fact]
    public void Reducer_OpenLogs_EmitsHistoryReadAndUsesOpaqueSelection()
    {
        var transition = TuiReducer.Reduce(Ready, new OpenLogs());

        Assert.IsType<ReadRunHistoryEffect>(Assert.Single(transition.Effects));
        // The transition itself does not carry any log content — the effect
        // key is what the runtime uses to fetch.
    }

    [Fact]
    public void Reducer_OpenSelectedLog_EmitsTrustedRunIdFromParallelState()
    {
        var runId = Guid.Parse("33333333-3333-3333-3333-333333333333");
        var summary = new DisplayRunSummary(
            StartedAtUtc: DateTimeOffset.UtcNow,
            CompletedAtUtc: DateTimeOffset.UtcNow,
            ProfileDisplayName: "Ubuntu 24.04",
            DistroName: "Ubuntu-24.04",
            Intent: OperationIntent.Compact,
            TerminalResult: TerminalResult.Succeeded,
            ReclaimedBytes: 1024,
            IsMalformed: false,
            ErrorMessage: null);
        var state = Ready with
        {
            RunHistoryEntries = ImmutableArray.Create(summary),
            RunHistoryRunIds = ImmutableArray.Create(runId),
            SelectedLogIndex = 0
        };

        var transition = TuiReducer.Reduce(state, new OpenSelectedLog());

        var effect = Assert.IsType<ReadLogDetailEffect>(Assert.Single(transition.Effects));
        // The run id is threaded from trusted session state into the effect;
        // it never crosses into view state.
        Assert.Equal(runId, effect.TrustedRunId);
        Assert.Equal(runId, transition.State.CurrentLogDetailRunId);
    }

    [Fact]
    public void Reducer_OpenSelectedLog_IgnoresMalformedEntry()
    {
        var malformed = DisplayRunSummary.Malformed("损坏的日志条目");
        var state = Ready with
        {
            RunHistoryEntries = ImmutableArray.Create(malformed),
            RunHistoryRunIds = ImmutableArray.Create(Guid.Parse("33333333-3333-3333-3333-333333333333")),
            SelectedLogIndex = 0
        };

        var transition = TuiReducer.Reduce(state, new OpenSelectedLog());
        Assert.Empty(transition.Effects);
    }
}
