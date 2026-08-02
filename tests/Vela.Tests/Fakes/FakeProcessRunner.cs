using System.Collections.Immutable;
using Vela.Core.Contracts;

namespace Vela.Tests.Fakes;

public sealed class FakeProcessRunner : IProcessRunner
{
    private ImmutableArray<ProcessInvocation> _invocations = ImmutableArray<ProcessInvocation>.Empty;

    public bool ThrowOnInvocation { get; init; } = true;

    public ProcessExecutionResult Result { get; init; } = CreateSucceededResult();

    public int InvocationCount => _invocations.Length;

    public ImmutableArray<ProcessInvocation> Invocations => _invocations;

    public Task<ProcessExecutionResult> RunAsync(
        ProcessInvocation invocation,
        IProgress<ProcessOutput>? output,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _invocations = _invocations.Add(invocation);

        if (ThrowOnInvocation)
        {
            throw new InvalidOperationException("The process runner must not be invoked by a read-only preflight.");
        }

        return Task.FromResult(Result);
    }

    private static ProcessExecutionResult CreateSucceededResult() => new(
        ProcessExecutionStatus.Succeeded,
        0,
        ImmutableArray<string>.Empty,
        ImmutableArray<string>.Empty,
        DateTimeOffset.UnixEpoch,
        DateTimeOffset.UnixEpoch);
}
