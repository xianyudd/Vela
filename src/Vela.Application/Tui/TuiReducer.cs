using System.Collections.Immutable;
using Vela.Application.Display;
using Vela.Application.Startup;
using Vela.Core.Contracts;
using Vela.Core.Models;

namespace Vela.Application.Tui;

/// <summary>
/// Pure reducer for TUI state. No Terminal.Gui, Spectre, file I/O, registry,
/// process, or async work belongs here. The reducer is deterministic: the
/// same state + command produce the same transition every time.
/// </summary>
public static class TuiReducer
{
    /// <summary>
    /// Applies a command to the current state and returns the resulting
    /// transition.
    /// </summary>
    /// <param name="state">Current trusted session state.</param>
    /// <param name="command">The command to reduce.</param>
    /// <returns>The new state and any effects to schedule.</returns>
    public static TuiTransition Reduce(TuiSessionState state, TuiCommand command)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(command);

        return command switch
        {
            AppendStartupConfirmationCharacter append => ReduceAppendStartupCharacter(state, append),
            RemoveStartupConfirmationCharacter => ReduceRemoveStartupCharacter(state),
            SubmitStartupConfirmation => ReduceSubmitStartupConfirmation(state),
            StartupInitializationCompleted completed => ReduceStartupInitializationCompleted(state, completed),
            NavigateMenu navigate => ReduceNavigateMenu(state, navigate),
            SelectTarget select => ReduceSelectTarget(state, select),
            LockSelectedTarget => ReduceLockSelectedTarget(state),
            RefreshPreflight => ReduceRefreshPreflight(state),
            PreflightCompleted completed => ReducePreflightCompleted(state, completed),
            PreflightFailed failed => ReducePreflightFailed(state, failed),
            OpenImpactPreview => ReduceOpenImpactPreview(state),
            ImpactEstimateCompleted completed => ReduceImpactEstimateCompleted(state, completed),
            ImpactEstimateFailed failed => ReduceImpactEstimateFailed(state, failed),
            SubmitFirstY => ReduceSubmitFirstY(state),
            SubmitSecondY => ReduceSubmitSecondY(state),
            CancelOrBack => ReduceCancelOrBack(state),
            OpenLogs => ReduceOpenLogs(state),
            MoveLogSelection move => ReduceMoveLogSelection(state, move),
            OpenSelectedLog => ReduceOpenSelectedLog(state),
            RunHistoryLoaded loaded => ReduceRunHistoryLoaded(state, loaded),
            RunHistoryFailed failed => ReduceRunHistoryFailed(state, failed),
            LogDetailLoaded loaded => ReduceLogDetailLoaded(state, loaded),
            LogDetailFailed failed => ReduceLogDetailFailed(state, failed),
            ExecutionJournalEvent journalEvent => ReduceExecutionJournalEvent(state, journalEvent),
            _ => TuiTransition.NoEffect(state),
        };
    }

    private static TuiTransition ReduceAppendStartupCharacter(
        TuiSessionState state,
        AppendStartupConfirmationCharacter append)
    {
        if (state.StartupStatus != StartupStatus.Idle &&
            state.StartupStatus != StartupStatus.Confirming)
        {
            return TuiTransition.NoEffect(state);
        }

        // The buffer never grows past the required confirmation length; that
        // way, typing more characters can never overflow the fixed gate.
        var maxLength = TuiSessionState.RequiredConfirmationText.Length;
        var next = state.StartupConfirmationInput.Length >= maxLength
            ? state.StartupConfirmationInput
            : (state.StartupConfirmationInput + append.Value).Trim();
        return TuiTransition.NoEffect(
            state with
            {
                StartupConfirmationInput = next,
                StartupStatus = StartupStatus.Confirming
            });
    }

    private static TuiTransition ReduceRemoveStartupCharacter(TuiSessionState state)
    {
        if (state.StartupStatus != StartupStatus.Confirming ||
            state.StartupConfirmationInput.Length == 0)
        {
            return TuiTransition.NoEffect(state);
        }

        return TuiTransition.NoEffect(
            state with { StartupConfirmationInput = state.StartupConfirmationInput[..^1] });
    }

    private static TuiTransition ReduceSubmitStartupConfirmation(TuiSessionState state)
    {
        if (state.StartupStatus != StartupStatus.Confirming &&
            state.StartupStatus != StartupStatus.Idle)
        {
            return TuiTransition.NoEffect(state);
        }

        if (!string.Equals(
                state.StartupConfirmationInput,
                TuiSessionState.RequiredConfirmationText,
                StringComparison.Ordinal))
        {
            return TuiTransition.NoEffect(state);
        }

        var nextGeneration = state.StartupGeneration + 1;
        return TuiTransition.WithEffect(
            state with
            {
                StartupStatus = StartupStatus.Initializing,
                StartupGeneration = nextGeneration,
                StartupConfirmationInput = string.Empty
            },
            new InitializeDataRootEffect(nextGeneration));
    }

    private static TuiTransition ReduceStartupInitializationCompleted(
        TuiSessionState state,
        StartupInitializationCompleted completed)
    {
        if (completed.Generation != state.StartupGeneration)
        {
            return TuiTransition.NoEffect(state);
        }

        return TuiTransition.NoEffect(
            state with
            {
                StartupStatus = completed.Outcome.Kind == StartupInitializationKind.Succeeded
                    ? StartupStatus.Ready
                    : StartupStatus.Failed
            });
    }

    private static TuiTransition ReduceNavigateMenu(
        TuiSessionState state,
        NavigateMenu navigate)
    {
        if (IsBusy(state))
        {
            return TuiTransition.NoEffect(state);
        }

        return TuiTransition.NoEffect(
            state with { SelectedMenuIndex = Math.Clamp(state.SelectedMenuIndex + navigate.Offset, 0, 5) });
    }

    private static TuiTransition ReduceSelectTarget(
        TuiSessionState state,
        SelectTarget select)
    {
        if (IsBusy(state))
        {
            return TuiTransition.NoEffect(state);
        }

        return TuiTransition.NoEffect(
            state with { SelectedProfileIndex = Math.Max(0, state.SelectedProfileIndex + select.Offset) });
    }

    private static TuiTransition ReduceLockSelectedTarget(TuiSessionState state)
    {
        if (IsBusy(state) || state.CurrentProfile is not { } profile)
        {
            return TuiTransition.NoEffect(state);
        }

        var locked = new LockedCompactionTarget(
            profile,
            profile.VhdxPath,
            LockedTargetQuality.SelectedProfile);
        return TuiTransition.NoEffect(
            state with
            {
                LockedTarget = locked,
                PreflightStatus = PreflightStatus.Idle,
                ConfirmationStatus = ConfirmationStatus.Idle
            });
    }

    private static TuiTransition ReduceRefreshPreflight(TuiSessionState state)
    {
        if (IsBusy(state) || state.LockedTarget is not { } target)
        {
            return TuiTransition.NoEffect(state);
        }

        var nextGeneration = state.PreflightGeneration + 1;
        return TuiTransition.WithEffect(
            state with
            {
                PreflightStatus = PreflightStatus.Checking,
                PreflightGeneration = nextGeneration,
                PreflightProfileId = target.Profile.Id
            },
            new StartPreflightEffect(target.Profile, PreserveTargetSelection: true, nextGeneration));
    }

    private static TuiTransition ReducePreflightCompleted(
        TuiSessionState state,
        PreflightCompleted completed)
    {
        if (completed.Generation != state.PreflightGeneration ||
            completed.ProfileId != state.PreflightProfileId)
        {
            return TuiTransition.NoEffect(state);
        }

        var ready = completed.Report.Validation?.IsValid == true;
        return TuiTransition.NoEffect(
            state with
            {
                PreflightStatus = ready ? PreflightStatus.Ready : PreflightStatus.Attention,
                LastPreflightReport = completed.Report
            });
    }

    private static TuiTransition ReducePreflightFailed(
        TuiSessionState state,
        PreflightFailed failed)
    {
        if (failed.Generation != state.PreflightGeneration ||
            failed.ProfileId != state.PreflightProfileId)
        {
            return TuiTransition.NoEffect(state);
        }

        return TuiTransition.NoEffect(
            state with
            {
                PreflightStatus = PreflightStatus.Failed,
                LastPreflightError = DisplayRunSummary.Malformed(failed.Message.Text)
            });
    }

    private static TuiTransition ReduceOpenImpactPreview(TuiSessionState state)
    {
        if (IsBusy(state) ||
            state.LockedTarget is not { } target ||
            state.PreflightStatus != PreflightStatus.Ready)
        {
            return TuiTransition.NoEffect(state);
        }

        var nextRevision = state.ImpactRevision + 1;
        return TuiTransition.WithEffect(
            state with
            {
                ImpactStatus = ImpactPreviewStatus.Estimating,
                ImpactRevision = nextRevision
            },
            new EstimateImpactEffect(target, nextRevision));
    }

    private static TuiTransition ReduceImpactEstimateCompleted(
        TuiSessionState state,
        ImpactEstimateCompleted completed)
    {
        if (completed.Revision != state.ImpactRevision)
        {
            return TuiTransition.NoEffect(state);
        }

        return TuiTransition.NoEffect(
            state with
            {
                ImpactStatus = ImpactPreviewStatus.Ready,
                LastImpactEstimate = completed.Estimate
            });
    }

    private static TuiTransition ReduceImpactEstimateFailed(
        TuiSessionState state,
        ImpactEstimateFailed failed)
    {
        if (failed.Revision != state.ImpactRevision)
        {
            return TuiTransition.NoEffect(state);
        }

        return TuiTransition.NoEffect(
            state with
            {
                ImpactStatus = ImpactPreviewStatus.Failed,
                LastImpactError = failed.Message
            });
    }

    private static TuiTransition ReduceSubmitFirstY(TuiSessionState state)
    {
        if (IsBusy(state) ||
            state.LockedTarget is null ||
            state.ImpactStatus != ImpactPreviewStatus.Ready)
        {
            return TuiTransition.NoEffect(state);
        }

        return TuiTransition.NoEffect(
            state with { ConfirmationStatus = ConfirmationStatus.AwaitingSecondY });
    }

    private static TuiTransition ReduceSubmitSecondY(TuiSessionState state)
    {
        if (state.ConfirmationStatus != ConfirmationStatus.AwaitingSecondY ||
            state.LockedTarget is null ||
            IsBusy(state))
        {
            return TuiTransition.NoEffect(state);
        }

        var nextGeneration = state.CompactionGeneration + 1;
        var request = new OperationRequest(
            Guid.NewGuid(),
            state.LockedTarget.Profile,
            OperationIntent.Compact);
        return TuiTransition.WithEffect(
            state with
            {
                ConfirmationStatus = ConfirmationStatus.Confirmed,
                CompactionStatus = CompactionStatus.Launching,
                CompactionGeneration = nextGeneration
            },
            new StartCompactionEffect(request, nextGeneration));
    }

    private static TuiTransition ReduceCancelOrBack(TuiSessionState state)
    {
        if (state.CompactionStatus == CompactionStatus.Running)
        {
            return TuiTransition.WithEffect(state, new RequestStopEffect());
        }

        return TuiTransition.NoEffect(
            state with
            {
                ConfirmationStatus = ConfirmationStatus.Idle,
                ImpactStatus = ImpactPreviewStatus.Idle,
                LastImpactEstimate = null
            });
    }

    private static TuiTransition ReduceOpenLogs(TuiSessionState state)
    {
        if (IsBusy(state))
        {
            return TuiTransition.NoEffect(state);
        }

        var nextRevision = state.RunHistoryRevision + 1;
        return TuiTransition.WithEffect(
            state with { RunHistoryRevision = nextRevision },
            new ReadRunHistoryEffect(nextRevision));
    }

    private static TuiTransition ReduceMoveLogSelection(
        TuiSessionState state,
        MoveLogSelection move)
    {
        if (IsBusy(state) || state.RunHistoryEntries.IsDefaultOrEmpty)
        {
            return TuiTransition.NoEffect(state);
        }

        var next = (state.SelectedLogIndex + move.Offset + state.RunHistoryEntries.Length) % state.RunHistoryEntries.Length;
        return TuiTransition.NoEffect(state with { SelectedLogIndex = next });
    }

    private static TuiTransition ReduceOpenSelectedLog(TuiSessionState state)
    {
        if (IsBusy(state) ||
            state.RunHistoryEntries.IsDefaultOrEmpty ||
            state.SelectedLogIndex >= state.RunHistoryEntries.Length ||
            state.SelectedLogIndex >= state.RunHistoryRunIds.Length)
        {
            return TuiTransition.NoEffect(state);
        }

        var selected = state.RunHistoryEntries[state.SelectedLogIndex];
        if (selected.IsMalformed)
        {
            return TuiTransition.NoEffect(state);
        }

        var trustedRunId = state.RunHistoryRunIds[state.SelectedLogIndex];
        var nextRevision = state.LogDetailRevision + 1;
        return TuiTransition.WithEffect(
            state with
            {
                LogDetailRevision = nextRevision,
                CurrentLogDetailRunId = trustedRunId
            },
            new ReadLogDetailEffect(trustedRunId, nextRevision));
    }

    private static TuiTransition ReduceRunHistoryLoaded(
        TuiSessionState state,
        RunHistoryLoaded loaded)
    {
        if (loaded.Revision != state.RunHistoryRevision)
        {
            return TuiTransition.NoEffect(state);
        }

        return TuiTransition.NoEffect(
            state with
            {
                RunHistoryEntries = loaded.Entries,
                RunHistoryRunIds = loaded.RunIds,
                RunHistoryError = null
            });
    }

    private static TuiTransition ReduceRunHistoryFailed(
        TuiSessionState state,
        RunHistoryFailed failed)
    {
        if (failed.Revision != state.RunHistoryRevision)
        {
            return TuiTransition.NoEffect(state);
        }

        return TuiTransition.NoEffect(
            state with { RunHistoryError = failed.Message });
    }

    private static TuiTransition ReduceLogDetailLoaded(
        TuiSessionState state,
        LogDetailLoaded loaded)
    {
        if (loaded.Revision != state.LogDetailRevision ||
            loaded.TrustedRunId != state.CurrentLogDetailRunId)
        {
            return TuiTransition.NoEffect(state);
        }

        return TuiTransition.NoEffect(
            state with { LogDetailEvents = loaded.Events });
    }

    private static TuiTransition ReduceLogDetailFailed(
        TuiSessionState state,
        LogDetailFailed failed)
    {
        if (failed.Revision != state.LogDetailRevision)
        {
            return TuiTransition.NoEffect(state);
        }

        return TuiTransition.NoEffect(
            state with { LogDetailEvents = ImmutableArray<DisplayRunEvent>.Empty });
    }

    private static TuiTransition ReduceExecutionJournalEvent(
        TuiSessionState state,
        ExecutionJournalEvent journalEvent)
    {
        if (journalEvent.Generation != state.CompactionGeneration)
        {
            return TuiTransition.NoEffect(state);
        }

        var nextStatus = state.CompactionStatus == CompactionStatus.Launching
            ? CompactionStatus.Running
            : state.CompactionStatus;
        return TuiTransition.NoEffect(
            state with
            {
                CompactionStatus = nextStatus,
                LogDetailEvents = state.LogDetailEvents.Add(journalEvent.Event)
            });
    }

    private static bool IsBusy(TuiSessionState state) =>
        state.StartupStatus == StartupStatus.Initializing ||
        state.PreflightStatus == PreflightStatus.Checking ||
        state.ImpactStatus == ImpactPreviewStatus.Estimating ||
        state.CompactionStatus == CompactionStatus.Launching ||
        state.CompactionStatus == CompactionStatus.Running;
}
