using System.Collections.Immutable;

namespace Vela.Core.Models;

public sealed record RunEvent(
    long Sequence,
    DateTimeOffset OccurredAtUtc,
    Guid RunId,
    RunPhase Phase,
    RunEventLevel Level,
    string OperationName,
    ImmutableArray<string> Arguments,
    int? ExitCode,
    TimeSpan? Duration,
    string? Output);

public enum RunPhase
{
    Validation,
    Inventory,
    Snapshot,
    AwaitingConfirmation,
    Elevation,
    Shutdown,
    DiskPartPreflight,
    Compacting,
    Completed,
    Failed
}

public enum RunEventLevel
{
    Trace,
    Information,
    Warning,
    Error
}
