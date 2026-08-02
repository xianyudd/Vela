using System.Collections.Immutable;
using Vela.Core.Contracts;

namespace Vela.Windows.Processes;

internal sealed record ProcessResult(
    ProcessExecutionStatus Status,
    int? ExitCode,
    ImmutableArray<string> StandardOutput,
    ImmutableArray<string> StandardError,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset CompletedAtUtc)
{
    public ProcessExecutionResult ToExecutionResult() => new(
        Status,
        ExitCode,
        StandardOutput,
        StandardError,
        StartedAtUtc,
        CompletedAtUtc);
}
