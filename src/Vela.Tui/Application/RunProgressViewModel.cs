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
    int? Percent);
