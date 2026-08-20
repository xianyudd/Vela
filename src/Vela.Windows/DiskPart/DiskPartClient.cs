using System.Collections.Immutable;
using Vela.Core.Contracts;
using Vela.Windows.Processes;

namespace Vela.Windows.DiskPart;

public sealed class DiskPartClient : IDiskPartClient
{
    private readonly IProcessRunner _processRunner;
    private readonly NativeToolPaths _nativeToolPaths;
    private readonly DiskPartScriptBuilder _scriptBuilder;
    private readonly IPrivilegedDiskPartWorkspace _workspace;

    public DiskPartClient()
        : this(
            new WindowsProcessRunner(),
            new NativeToolPaths(),
            new DiskPartScriptBuilder(),
            new PrivilegedDiskPartWorkspace())
    {
    }

    public DiskPartClient(
        IProcessRunner processRunner,
        NativeToolPaths nativeToolPaths,
        DiskPartScriptBuilder scriptBuilder,
        IPrivilegedDiskPartWorkspace workspace)
    {
        ArgumentNullException.ThrowIfNull(processRunner);
        ArgumentNullException.ThrowIfNull(nativeToolPaths);
        ArgumentNullException.ThrowIfNull(scriptBuilder);
        ArgumentNullException.ThrowIfNull(workspace);

        _processRunner = processRunner;
        _nativeToolPaths = nativeToolPaths;
        _scriptBuilder = scriptBuilder;
        _workspace = workspace;
    }

    public Task<ProcessExecutionResult> DetailVdiskAsync(
        Guid runId,
        string validatedVhdxPath,
        CancellationToken cancellationToken) =>
        RunAsync(
            runId,
            validatedVhdxPath,
            _scriptBuilder.BuildDetailScript,
            cancellationToken);

    public Task<ProcessExecutionResult> CompactVdiskAsync(
        Guid runId,
        string validatedVhdxPath,
        CancellationToken cancellationToken) =>
        RunAsync(
            runId,
            validatedVhdxPath,
            _scriptBuilder.BuildCompactScript,
            cancellationToken);

    private async Task<ProcessExecutionResult> RunAsync(
        Guid runId,
        string validatedVhdxPath,
        Func<string, string> scriptFactory,
        CancellationToken cancellationToken)
    {
        if (runId == Guid.Empty)
        {
            throw new ArgumentException("The run identifier must not be empty.", nameof(runId));
        }

        cancellationToken.ThrowIfCancellationRequested();
        var script = scriptFactory(validatedVhdxPath);

        await using var lease = await _workspace
            .CreateScriptAsync(runId, script, cancellationToken)
            .ConfigureAwait(false);

        await lease.VerifyAsync(cancellationToken).ConfigureAwait(false);

        var result = await _processRunner
            .RunAsync(
                new ProcessInvocation(
                    _nativeToolPaths.DiskPartExePath,
                    ImmutableArray.Create("/s", lease.ScriptPath),
                    Timeout: null),
                output: null,
                cancellationToken)
            .ConfigureAwait(false);

        await lease.VerifyAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }
}
