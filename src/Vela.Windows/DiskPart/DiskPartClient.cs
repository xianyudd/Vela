using System.Collections.Immutable;
using System.Text;
using Vela.Core.Contracts;
using Vela.Windows.Processes;

namespace Vela.Windows.DiskPart;

public sealed class DiskPartClient : IDiskPartClient
{
    private readonly IProcessRunner _processRunner;
    private readonly NativeToolPaths _nativeToolPaths;
    private readonly DiskPartScriptBuilder _scriptBuilder;
    private readonly string _temporaryDirectory;

    public DiskPartClient()
        : this(
            new WindowsProcessRunner(),
            new NativeToolPaths(),
            new DiskPartScriptBuilder(),
            Path.Combine(Path.GetTempPath(), "Vela"))
    {
    }

    public DiskPartClient(
        IProcessRunner processRunner,
        NativeToolPaths nativeToolPaths,
        DiskPartScriptBuilder scriptBuilder,
        string temporaryDirectory)
    {
        ArgumentNullException.ThrowIfNull(processRunner);
        ArgumentNullException.ThrowIfNull(nativeToolPaths);
        ArgumentNullException.ThrowIfNull(scriptBuilder);

        if (string.IsNullOrWhiteSpace(temporaryDirectory))
        {
            throw new ArgumentException("A temporary directory is required.", nameof(temporaryDirectory));
        }

        _processRunner = processRunner;
        _nativeToolPaths = nativeToolPaths;
        _scriptBuilder = scriptBuilder;
        _temporaryDirectory = Path.GetFullPath(temporaryDirectory);
    }

    public Task<ProcessExecutionResult> DetailVdiskAsync(
        string validatedVhdxPath,
        CancellationToken cancellationToken) =>
        RunAsync(
            validatedVhdxPath,
            _scriptBuilder.BuildDetailScript,
            cancellationToken);

    public Task<ProcessExecutionResult> CompactVdiskAsync(
        string validatedVhdxPath,
        CancellationToken cancellationToken) =>
        RunAsync(
            validatedVhdxPath,
            _scriptBuilder.BuildCompactScript,
            cancellationToken);

    private async Task<ProcessExecutionResult> RunAsync(
        string validatedVhdxPath,
        Func<string, string> scriptFactory,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var script = scriptFactory(validatedVhdxPath);
        var scriptPath = Path.Combine(
            _temporaryDirectory,
            $"vela-diskpart-{Guid.NewGuid():D}.txt");
        var createdDirectory = false;

        try
        {
            if (!Directory.Exists(_temporaryDirectory))
            {
                Directory.CreateDirectory(_temporaryDirectory);
                createdDirectory = true;
            }

            await File.WriteAllTextAsync(
                    scriptPath,
                    script,
                    Encoding.ASCII,
                    cancellationToken)
                .ConfigureAwait(false);

            return await _processRunner
                .RunAsync(
                    new ProcessInvocation(
                        _nativeToolPaths.DiskPartExePath,
                        ImmutableArray.Create("/s", scriptPath),
                        Timeout: null),
                    output: null,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            TryDeleteFile(scriptPath);
            if (createdDirectory)
            {
                TryDeleteDirectory(_temporaryDirectory);
            }
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception)
        {
            // The process result is more useful than a best-effort cleanup exception.
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any())
            {
                Directory.Delete(path);
            }
        }
        catch (Exception)
        {
            // The process result is more useful than a best-effort cleanup exception.
        }
    }
}
