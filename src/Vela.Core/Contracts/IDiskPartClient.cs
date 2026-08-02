namespace Vela.Core.Contracts;

public interface IDiskPartClient
{
    Task<ProcessExecutionResult> DetailVdiskAsync(
        string validatedVhdxPath,
        CancellationToken cancellationToken);

    Task<ProcessExecutionResult> CompactVdiskAsync(
        string validatedVhdxPath,
        CancellationToken cancellationToken);
}
