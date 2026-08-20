using Vela.Core.Models;

namespace Vela.Application.Tui;

/// <summary>
/// Base type for all TUI effects.
/// </summary>
public abstract record TuiEffect;

/// <summary>Initialize the data root and load the profile store.</summary>
public sealed record InitializeDataRootEffect(long Generation) : TuiEffect;

/// <summary>Run read-only preflight for a profile.</summary>
public sealed record StartPreflightEffect(
    Profile Profile,
    bool PreserveTargetSelection,
    long Generation) : TuiEffect;

/// <summary>Estimate the compaction impact for the locked target.</summary>
public sealed record EstimateImpactEffect(
    LockedCompactionTarget Target,
    long Revision) : TuiEffect;

/// <summary>Start the compaction worker. Single-flight.</summary>
public sealed record StartCompactionEffect(
    OperationRequest Request,
    long Generation) : TuiEffect;

/// <summary>Read the recent-run history for the log archive.</summary>
public sealed record ReadRunHistoryEffect(long Revision) : TuiEffect;

/// <summary>Read the detailed log for a trusted run ID.</summary>
public sealed record ReadLogDetailEffect(Guid TrustedRunId, long Revision) : TuiEffect;

/// <summary>Request cancellation of the currently running worker.</summary>
public sealed record RequestStopEffect : TuiEffect;
