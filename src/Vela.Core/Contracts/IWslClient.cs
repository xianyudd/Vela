namespace Vela.Core.Contracts;

public interface IWslClient : IWslInventoryReader
{
    Task<ProcessExecutionResult> ShutdownAllAsync(CancellationToken cancellationToken);

    Task<ProcessExecutionResult> TerminateDistroAsync(
        string distroName,
        CancellationToken cancellationToken);
}
