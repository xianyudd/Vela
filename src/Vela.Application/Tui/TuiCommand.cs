using System.Collections.Immutable;
using Vela.Application.Display;
using Vela.Application.Startup;

namespace Vela.Application.Tui;

/// <summary>
/// Base type for all TUI commands.
/// </summary>
public abstract record TuiCommand;

/// <summary>Append a character to the startup confirmation buffer.</summary>
public sealed record AppendStartupConfirmationCharacter(char Value) : TuiCommand;

/// <summary>Remove the last character from the startup confirmation buffer.</summary>
public sealed record RemoveStartupConfirmationCharacter : TuiCommand;

/// <summary>Submit the startup confirmation buffer.</summary>
public sealed record SubmitStartupConfirmation : TuiCommand;

/// <summary>Startup initialization effect completed.</summary>
public sealed record StartupInitializationCompleted(
    long Generation,
    StartupInitializationOutcome Outcome) : TuiCommand;

/// <summary>Move the menu selection by a relative offset.</summary>
public sealed record NavigateMenu(int Offset) : TuiCommand;

/// <summary>Move the target/profile selection by a relative offset.</summary>
public sealed record SelectTarget(int Offset) : TuiCommand;

/// <summary>Lock the currently selected target for execution.</summary>
public sealed record LockSelectedTarget : TuiCommand;

/// <summary>Request a read-only re-scan of preflight.</summary>
public sealed record RefreshPreflight : TuiCommand;

/// <summary>Preflight effect completed for a profile.</summary>
public sealed record PreflightCompleted(
    Guid ProfileId,
    long Generation,
    Vela.Core.Workflows.PreflightReport Report) : TuiCommand;

/// <summary>Preflight effect failed for a profile.</summary>
public sealed record PreflightFailed(
    Guid ProfileId,
    long Generation,
    DisplayMessage Message) : TuiCommand;

/// <summary>Open the impact preview page.</summary>
public sealed record OpenImpactPreview : TuiCommand;

/// <summary>Impact-estimation effect completed.</summary>
public sealed record ImpactEstimateCompleted(
    long Revision,
    Vela.Core.Contracts.CompactionImpactEstimate Estimate) : TuiCommand;

/// <summary>Impact-estimation effect failed.</summary>
public sealed record ImpactEstimateFailed(long Revision, DisplayMessage Message) : TuiCommand;

/// <summary>Submit the first confirmation Y.</summary>
public sealed record SubmitFirstY : TuiCommand;

/// <summary>Submit the second confirmation Y.</summary>
public sealed record SubmitSecondY : TuiCommand;

/// <summary>Cancel the current action or go back.</summary>
public sealed record CancelOrBack : TuiCommand;

/// <summary>Open the log-archive page.</summary>
public sealed record OpenLogs : TuiCommand;

/// <summary>Move the log selection by a relative offset.</summary>
public sealed record MoveLogSelection(int Offset) : TuiCommand;

/// <summary>Open the selected log entry.</summary>
public sealed record OpenSelectedLog : TuiCommand;

/// <summary>Run-history effect completed.</summary>
public sealed record RunHistoryLoaded(long Revision, ImmutableArray<DisplayRunSummary> Entries, ImmutableArray<Guid> RunIds) : TuiCommand;

/// <summary>Run-history effect failed.</summary>
public sealed record RunHistoryFailed(long Revision, DisplayMessage Message) : TuiCommand;

/// <summary>Log-detail effect completed.</summary>
public sealed record LogDetailLoaded(
    long Revision,
    Guid TrustedRunId,
    ImmutableArray<DisplayRunEvent> Events) : TuiCommand;

/// <summary>Log-detail effect failed.</summary>
public sealed record LogDetailFailed(long Revision, DisplayMessage Message) : TuiCommand;

/// <summary>Execution journal event observed from the worker.</summary>
public sealed record ExecutionJournalEvent(long Generation, DisplayRunEvent Event) : TuiCommand;
