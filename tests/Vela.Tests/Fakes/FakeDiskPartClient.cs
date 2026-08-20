using System.Collections.Immutable;
using Vela.Core.Contracts;

namespace Vela.Tests.Fakes;

public sealed class FakeDiskPartClient : IDiskPartClient
{
    private ImmutableArray<string> _detailVdiskPaths = ImmutableArray<string>.Empty;
    private ImmutableArray<string> _compactVdiskPaths = ImmutableArray<string>.Empty;

    public bool ThrowOnInvocation { get; init; } = true;

    public ProcessExecutionResult Result { get; init; } = CreateSucceededResult();

    public int DetailVdiskCalls => _detailVdiskPaths.Length;

    public int CompactVdiskCalls => _compactVdiskPaths.Length;

    public int TotalCalls => DetailVdiskCalls + CompactVdiskCalls;

    public ImmutableArray<string> DetailVdiskPaths => _detailVdiskPaths;

    public ImmutableArray<string> CompactVdiskPaths => _compactVdiskPaths;

    public Task<ProcessExecutionResult> DetailVdiskAsync(
        Guid runId,
        string validatedVhdxPath,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _detailVdiskPaths = _detailVdiskPaths.Add(validatedVhdxPath);
        ThrowWhenActionsAreForbidden();

        return Task.FromResult(Result);
    }

    public Task<ProcessExecutionResult> CompactVdiskAsync(
        Guid runId,
        string validatedVhdxPath,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _compactVdiskPaths = _compactVdiskPaths.Add(validatedVhdxPath);
        ThrowWhenActionsAreForbidden();

        return Task.FromResult(Result);
    }

    private void ThrowWhenActionsAreForbidden()
    {
        if (ThrowOnInvocation)
        {
            throw new InvalidOperationException("DiskPart must not be invoked by a read-only preflight.");
        }
    }

    private static ProcessExecutionResult CreateSucceededResult() => new(
        ProcessExecutionStatus.Succeeded,
        0,
        ImmutableArray<string>.Empty,
        ImmutableArray<string>.Empty,
        DateTimeOffset.UnixEpoch,
        DateTimeOffset.UnixEpoch);
}
