namespace Vela.Core.Contracts;

public interface IProcessRunner
{
    Task<ProcessExecutionResult> RunAsync(
        ProcessInvocation invocation,
        IProgress<ProcessOutput>? output,
        CancellationToken cancellationToken);
}
