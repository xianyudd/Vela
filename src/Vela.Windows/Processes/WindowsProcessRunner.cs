using System.Collections.Immutable;
using System.Diagnostics;
using Vela.Core.Contracts;

namespace Vela.Windows.Processes;

public sealed class WindowsProcessRunner : IProcessRunner
{
    public async Task<ProcessExecutionResult> RunAsync(
        ProcessInvocation invocation,
        IProgress<ProcessOutput>? output,
        CancellationToken cancellationToken)
    {
        var startedAtUtc = DateTimeOffset.UtcNow;

        if (cancellationToken.IsCancellationRequested)
        {
            return CreateResult(
                ProcessExecutionStatus.Cancelled,
                exitCode: null,
                ImmutableArray<string>.Empty,
                ImmutableArray<string>.Empty,
                startedAtUtc,
                startedAtUtc);
        }

        if (!IsValid(invocation))
        {
            return CreateResult(
                ProcessExecutionStatus.LaunchFailed,
                exitCode: null,
                ImmutableArray<string>.Empty,
                ImmutableArray<string>.Empty,
                startedAtUtc,
                startedAtUtc);
        }

        if (invocation.Timeout is { } timeout && timeout <= TimeSpan.Zero)
        {
            return CreateResult(
                ProcessExecutionStatus.TimedOut,
                exitCode: null,
                ImmutableArray<string>.Empty,
                ImmutableArray<string>.Empty,
                startedAtUtc,
                startedAtUtc);
        }

        ProcessStartInfo startInfo;

        try
        {
            startInfo = CreateStartInfo(invocation);
        }
        catch (Exception)
        {
            return CreateResult(
                ProcessExecutionStatus.LaunchFailed,
                exitCode: null,
                ImmutableArray<string>.Empty,
                ImmutableArray<string>.Empty,
                startedAtUtc,
                DateTimeOffset.UtcNow);
        }

        using var process = new Process { StartInfo = startInfo };
        var outputGate = new object();
        var standardOutput = new List<string>();
        var standardError = new List<string>();
        var stopwatch = Stopwatch.StartNew();

        try
        {
            if (!process.Start())
            {
                return CreateResult(
                    ProcessExecutionStatus.LaunchFailed,
                    exitCode: null,
                    ImmutableArray<string>.Empty,
                    ImmutableArray<string>.Empty,
                    startedAtUtc,
                    CompleteAt(startedAtUtc, stopwatch));
            }
        }
        catch (Exception)
        {
            return CreateResult(
                ProcessExecutionStatus.LaunchFailed,
                exitCode: null,
                ImmutableArray<string>.Empty,
                ImmutableArray<string>.Empty,
                startedAtUtc,
                CompleteAt(startedAtUtc, stopwatch));
        }

        var standardOutputTask = CaptureAsync(
            process.StandardOutput,
            standardOutput,
            outputGate,
            ProcessOutputStream.StandardOutput,
            output);
        var standardErrorTask = CaptureAsync(
            process.StandardError,
            standardError,
            outputGate,
            ProcessOutputStream.StandardError,
            output);

        using var timeoutCancellation = new CancellationTokenSource();
        if (invocation.Timeout is { } executionTimeout)
        {
            timeoutCancellation.CancelAfter(executionTimeout);
        }

        using var executionCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutCancellation.Token);

        var status = ProcessExecutionStatus.LaunchFailed;
        int? exitCode = null;

        try
        {
            await process.WaitForExitAsync(executionCancellation.Token).ConfigureAwait(false);
            exitCode = process.ExitCode;
            status = exitCode == 0
                ? ProcessExecutionStatus.Succeeded
                : ProcessExecutionStatus.Failed;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            status = ProcessExecutionStatus.Cancelled;
            await StopProcessAsync(process).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeoutCancellation.IsCancellationRequested)
        {
            status = ProcessExecutionStatus.TimedOut;
            await StopProcessAsync(process).ConfigureAwait(false);
        }
        catch (Exception)
        {
            status = ProcessExecutionStatus.LaunchFailed;
            await StopProcessAsync(process).ConfigureAwait(false);
        }

        try
        {
            await Task.WhenAll(standardOutputTask, standardErrorTask).ConfigureAwait(false);
        }
        catch (Exception)
        {
            status = ProcessExecutionStatus.LaunchFailed;
            exitCode = null;
        }

        if (status is ProcessExecutionStatus.Cancelled or ProcessExecutionStatus.TimedOut)
        {
            exitCode = null;
        }

        return CreateResult(
            status,
            exitCode,
            Snapshot(standardOutput, outputGate),
            Snapshot(standardError, outputGate),
            startedAtUtc,
            CompleteAt(startedAtUtc, stopwatch));
    }

    private static bool IsValid(ProcessInvocation invocation) =>
        invocation is not null &&
        !string.IsNullOrWhiteSpace(invocation.ExecutablePath) &&
        Path.IsPathFullyQualified(invocation.ExecutablePath) &&
        !invocation.Arguments.IsDefault &&
        invocation.Arguments.All(static argument => argument is not null);

    private static ProcessStartInfo CreateStartInfo(ProcessInvocation invocation)
    {
        var startInfo = new ProcessStartInfo(invocation.ExecutablePath)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        if (invocation.OutputEncoding is { } outputEncoding)
        {
            startInfo.StandardOutputEncoding = outputEncoding;
            startInfo.StandardErrorEncoding = outputEncoding;
        }

        foreach (var argument in invocation.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return startInfo;
    }

    private static async Task CaptureAsync(
        StreamReader reader,
        List<string> capturedOutput,
        object outputGate,
        ProcessOutputStream stream,
        IProgress<ProcessOutput>? output)
    {
        string? line;
        while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) is not null)
        {
            lock (outputGate)
            {
                capturedOutput.Add(line);
            }

            output?.Report(new ProcessOutput(DateTimeOffset.UtcNow, stream, line));
        }
    }

    private static async Task StopProcessAsync(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // The process finished after its state was read and before termination began.
        }

        await process.WaitForExitAsync().ConfigureAwait(false);
    }

    private static ImmutableArray<string> Snapshot(List<string> capturedOutput, object outputGate)
    {
        lock (outputGate)
        {
            return capturedOutput.ToImmutableArray();
        }
    }

    private static DateTimeOffset CompleteAt(DateTimeOffset startedAtUtc, Stopwatch stopwatch) =>
        startedAtUtc + stopwatch.Elapsed;

    private static ProcessExecutionResult CreateResult(
        ProcessExecutionStatus status,
        int? exitCode,
        ImmutableArray<string> standardOutput,
        ImmutableArray<string> standardError,
        DateTimeOffset startedAtUtc,
        DateTimeOffset completedAtUtc) =>
        new ProcessResult(
            status,
            exitCode,
            standardOutput,
            standardError,
            startedAtUtc,
            completedAtUtc).ToExecutionResult();
}
