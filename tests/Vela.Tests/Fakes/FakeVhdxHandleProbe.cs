using System.Collections.Immutable;
using Vela.Core.Contracts;

namespace Vela.Tests.Fakes;

public sealed class FakeVhdxHandleProbe : IVhdxHandleProbe
{
    private ImmutableArray<string> _probedPaths = ImmutableArray<string>.Empty;

    public VhdxHandleState State { get; init; } = VhdxHandleState.Free;

    public Exception? Failure { get; init; }

    public ImmutableArray<string> ProbedPaths => _probedPaths;

    public int ProbeCalls => _probedPaths.Length;

    public Task<VhdxHandleState> ProbeAsync(string vhdxPath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _probedPaths = _probedPaths.Add(vhdxPath);
        return Failure is null
            ? Task.FromResult(State)
            : Task.FromException<VhdxHandleState>(Failure);
    }
}
