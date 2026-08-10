using System.Collections.Immutable;

namespace Vela.Tui.Application;

public enum RunProgressState
{
    Idle,
    Preflighting,
    AwaitingConfirmation,
    Running,
    Succeeded,
    Failed,
    Cancelled,
    TimedOut,
    ReadFailed
}

public sealed record RunProgressViewModel(
    RunProgressState State,
    string Message,
    int? Percent,
    string? TargetName = null,
    string? VhdxPath = null,
    TimeSpan? Elapsed = null,
    long? ReclaimedBytes = null,
    ImmutableArray<string> LogLines = default)
{
    public ImmutableArray<string> VisibleLogLines =>
        LogLines.IsDefault ? ImmutableArray<string>.Empty : LogLines;
}
