using System.Collections.Immutable;

namespace Vela.Core.Contracts;

public sealed record ProcessInvocation(
    string ExecutablePath,
    ImmutableArray<string> Arguments,
    TimeSpan? Timeout);

public sealed record ProcessOutput(
    DateTimeOffset OccurredAtUtc,
    ProcessOutputStream Stream,
    string Text);

public enum ProcessOutputStream
{
    StandardOutput,
    StandardError
}

public sealed record ProcessExecutionResult(
    ProcessExecutionStatus Status,
    int? ExitCode,
    ImmutableArray<string> StandardOutput,
    ImmutableArray<string> StandardError,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset CompletedAtUtc)
{
    public TimeSpan Duration => CompletedAtUtc - StartedAtUtc;
}

public enum ProcessExecutionStatus
{
    Succeeded,
    Failed,
    TimedOut,
    Cancelled,
    LaunchFailed
}
