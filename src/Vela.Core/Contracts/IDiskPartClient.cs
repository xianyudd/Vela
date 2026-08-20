namespace Vela.Core.Contracts;

public interface IDiskPartClient
{
    Task<ProcessExecutionResult> DetailVdiskAsync(
        Guid runId,
        string validatedVhdxPath,
        CancellationToken cancellationToken);

    Task<ProcessExecutionResult> CompactVdiskAsync(
        Guid runId,
        string validatedVhdxPath,
        CancellationToken cancellationToken);
}
