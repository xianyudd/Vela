using System.Collections.Immutable;
using Vela.Application.Display;
using Vela.Core.Contracts;
using Vela.Core.Models;

namespace Vela.Application.Tui;

/// <summary>
/// Snapshot of the trusted application state. The view layer never receives
/// this type directly; it goes through <see cref="TuiViewProjector"/>.
/// </summary>
public sealed record TuiSessionState(
    long StartupGeneration,
    string StartupConfirmationInput,
    StartupStatus StartupStatus,
    int SelectedMenuIndex,
    int SelectedProfileIndex,
    Profile? CurrentProfile,
    LockedCompactionTarget? LockedTarget,
    PreflightStatus PreflightStatus,
    long PreflightGeneration,
    Guid? PreflightProfileId,
    Vela.Core.Workflows.PreflightReport? LastPreflightReport,
    DisplayRunSummary? LastPreflightError,
    ImpactPreviewStatus ImpactStatus,
    long ImpactRevision,
    CompactionImpactEstimate? LastImpactEstimate,
    DisplayMessage? LastImpactError,
    ConfirmationStatus ConfirmationStatus,
    CompactionStatus CompactionStatus,
    long CompactionGeneration,
    ImmutableArray<DisplayRunSummary> RunHistoryEntries,
    long RunHistoryRevision,
    DisplayMessage? RunHistoryError,
    ImmutableArray<DisplayRunEvent> LogDetailEvents,
    long LogDetailRevision,
    Guid? CurrentLogDetailRunId)
{
    /// <summary>
    /// The startup confirmation text required to proceed.
    /// </summary>
    public const string RequiredConfirmationText = "YES";

    /// <summary>
    /// Initial state with no startup interaction yet.
    /// </summary>
    public static TuiSessionState Initial() => new(
        StartupGeneration: 0,
        StartupConfirmationInput: string.Empty,
        StartupStatus: StartupStatus.Idle,
        SelectedMenuIndex: 0,
        SelectedProfileIndex: 0,
        CurrentProfile: null,
        LockedTarget: null,
        PreflightStatus: PreflightStatus.Idle,
        PreflightGeneration: 0,
        PreflightProfileId: null,
        LastPreflightReport: null,
        LastPreflightError: null,
        ImpactStatus: ImpactPreviewStatus.Idle,
        ImpactRevision: 0,
        LastImpactEstimate: null,
        LastImpactError: null,
        ConfirmationStatus: ConfirmationStatus.Idle,
        CompactionStatus: CompactionStatus.Idle,
        CompactionGeneration: 0,
        RunHistoryEntries: ImmutableArray<DisplayRunSummary>.Empty,
        RunHistoryRevision: 0,
        RunHistoryError: null,
        LogDetailEvents: ImmutableArray<DisplayRunEvent>.Empty,
        LogDetailRevision: 0,
        CurrentLogDetailRunId: null);
}

/// <summary>
/// Startup interaction lifecycle.
/// </summary>
public enum StartupStatus
{
    /// <summary>No startup interaction has begun.</summary>
    Idle,
    /// <summary>Awaiting typed confirmation at the startup gate.</summary>
    Confirming,
    /// <summary>Cached state is being initialized.</summary>
    Initializing,
    /// <summary>Ready for use.</summary>
    Ready,
    /// <summary>Startup failed; see the last startup error.</summary>
    Failed,
}

/// <summary>
/// Preflight outcome status.
/// </summary>
public enum PreflightStatus
{
    /// <summary>Preflight has not been triggered.</summary>
    Idle,
    /// <summary>Preflight is running.</summary>
    Checking,
    /// <summary>Preflight succeeded and the result is current.</summary>
    Ready,
    /// <summary>Preflight completed but the target needs attention.</summary>
    Attention,
    /// <summary>Preflight failed.</summary>
    Failed,
    /// <summary>Preflight result is stale (profile changed since).</summary>
    Stale,
}

/// <summary>
/// Impact-preview status.
/// </summary>
public enum ImpactPreviewStatus
{
    /// <summary>Impact estimation has not been triggered.</summary>
    Idle,
    /// <summary>Impact estimation is running.</summary>
    Estimating,
    /// <summary>Impact estimate is available.</summary>
    Ready,
    /// <summary>Impact estimation failed.</summary>
    Failed,
}

/// <summary>
/// Compaction confirmation lifecycle.
/// </summary>
public enum ConfirmationStatus
{
    /// <summary>Confirmation has not been requested.</summary>
    Idle,
    /// <summary>Waiting for the first Y confirmation.</summary>
    AwaitingFirstY,
    /// <summary>Waiting for the second Y confirmation.</summary>
    AwaitingSecondY,
    /// <summary>Both confirmations received; compaction may proceed.</summary>
    Confirmed,
}

/// <summary>
/// Compaction lifecycle.
/// </summary>
public enum CompactionStatus
{
    /// <summary>Compaction has not been started.</summary>
    Idle,
    /// <summary>Worker is being launched.</summary>
    Launching,
    /// <summary>Compaction is in progress.</summary>
    Running,
    /// <summary>Compaction completed successfully.</summary>
    Succeeded,
    /// <summary>Compaction failed.</summary>
    Failed,
    /// <summary>Compaction was cancelled.</summary>
    Cancelled,
}
