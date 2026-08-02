using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Text;
using Vela.Core.Contracts;
using Vela.Windows.Processes;

namespace Vela.Tests.Windows;

public sealed class WindowsProcessRunnerTests
{
    private static readonly string PowerShellPath = Path.Combine(
        Environment.SystemDirectory,
        "WindowsPowerShell",
        "v1.0",
        "powershell.exe");

    [Fact]
    public async Task RunAsync_CapturesOutputExitCodeAndProgress()
    {
        using var helper = HelperScript.Create(
            """
            [Console]::Out.WriteLine("stdout-1")
            [Console]::Out.WriteLine("stdout-2")
            [Console]::Error.WriteLine("stderr-1")
            exit 17
            """);
        var progress = new RecordingProgress();
        var runner = new WindowsProcessRunner();

        var result = await runner.RunAsync(
            CreateInvocation(helper.ScriptPath, TimeSpan.FromSeconds(10)),
            progress,
            CancellationToken.None);

        Assert.Equal(ProcessExecutionStatus.Failed, result.Status);
        Assert.Equal(17, result.ExitCode);
        Assert.Equal(new[] { "stdout-1", "stdout-2" }, result.StandardOutput);
        Assert.Equal(new[] { "stderr-1" }, result.StandardError);
        Assert.True(result.StartedAtUtc <= result.CompletedAtUtc);
        Assert.True(result.Duration >= TimeSpan.Zero);
        Assert.Collection(
            progress.Outputs.Where(static item => item.Stream == ProcessOutputStream.StandardOutput),
            item => Assert.Equal("stdout-1", item.Text),
            item => Assert.Equal("stdout-2", item.Text));
        Assert.Collection(
            progress.Outputs.Where(static item => item.Stream == ProcessOutputStream.StandardError),
            item => Assert.Equal("stderr-1", item.Text));
        Assert.All(progress.Outputs, static item => Assert.Equal(TimeSpan.Zero, item.OccurredAtUtc.Offset));
    }

    [Fact]
    public async Task RunAsync_PreservesIndividualArgumentBoundaries()
    {
        using var helper = HelperScript.Create(
            """
            foreach ($argument in $args) {
                [Console]::Out.WriteLine("<$argument>")
            }
            exit 0
            """);
        var runner = new WindowsProcessRunner();

        var result = await runner.RunAsync(
            CreateInvocation(
                helper.ScriptPath,
                TimeSpan.FromSeconds(10),
                "alpha beta",
                "literal&value",
                "three words"),
            output: null,
            CancellationToken.None);

        Assert.Equal(ProcessExecutionStatus.Succeeded, result.Status);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(
            new[] { "<alpha beta>", "<literal&value>", "<three words>" },
            result.StandardOutput);
        Assert.Empty(result.StandardError);
    }

    [Fact]
    public async Task RunAsync_ReportsMeasuredDurationForSuccessfulProcess()
    {
        using var helper = HelperScript.Create(
            """
            Start-Sleep -Milliseconds 250
            [Console]::Out.WriteLine("complete")
            exit 0
            """);
        var runner = new WindowsProcessRunner();

        var result = await runner.RunAsync(
            CreateInvocation(helper.ScriptPath, TimeSpan.FromSeconds(10)),
            output: null,
            CancellationToken.None);

        Assert.Equal(ProcessExecutionStatus.Succeeded, result.Status);
        Assert.InRange(result.Duration, TimeSpan.FromMilliseconds(150), TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task RunAsync_WhenTimeoutExpires_ReturnsTimedOut()
    {
        using var helper = HelperScript.Create(
            """
            Start-Sleep -Seconds 10
            exit 0
            """);
        var runner = new WindowsProcessRunner();

        var result = await runner.RunAsync(
            CreateInvocation(helper.ScriptPath, TimeSpan.FromMilliseconds(300)),
            output: null,
            CancellationToken.None);

        Assert.Equal(ProcessExecutionStatus.TimedOut, result.Status);
        Assert.Null(result.ExitCode);
        Assert.InRange(result.Duration, TimeSpan.Zero, TimeSpan.FromSeconds(8));
    }

    [Fact]
    public async Task RunAsync_WhenCancelledAfterOutput_ReturnsCancelled()
    {
        using var helper = HelperScript.Create(
            """
            [Console]::Out.WriteLine("ready")
            Start-Sleep -Seconds 10
            exit 0
            """);
        using var cancellation = new CancellationTokenSource();
        using var readinessTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var progress = new RecordingProgress();
        var runner = new WindowsProcessRunner();

        var execution = runner.RunAsync(
            CreateInvocation(helper.ScriptPath, timeout: null),
            progress,
            cancellation.Token);
        await progress.WaitForOutputAsync("ready", readinessTimeout.Token);
        cancellation.Cancel();

        var result = await execution;

        Assert.Equal(ProcessExecutionStatus.Cancelled, result.Status);
        Assert.Null(result.ExitCode);
        Assert.InRange(result.Duration, TimeSpan.Zero, TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task RunAsync_WhenExecutableCannotBeStarted_ReturnsLaunchFailed()
    {
        var runner = new WindowsProcessRunner();
        var invocation = new ProcessInvocation(
            Path.Combine(FindRepositoryRoot(), "artifacts", "test-data", "missing-helper.exe"),
            ImmutableArray<string>.Empty,
            Timeout: null);

        var result = await runner.RunAsync(invocation, output: null, CancellationToken.None);

        Assert.Equal(ProcessExecutionStatus.LaunchFailed, result.Status);
        Assert.Null(result.ExitCode);
    }

    [Fact]
    public async Task RunAsync_WhenExecutablePathIsRelative_ReturnsLaunchFailedWithoutStartingProcess()
    {
        var runner = new WindowsProcessRunner();
        var invocation = new ProcessInvocation(
            "powershell.exe",
            ImmutableArray<string>.Empty,
            Timeout: null);

        var result = await runner.RunAsync(invocation, output: null, CancellationToken.None);

        Assert.Equal(ProcessExecutionStatus.LaunchFailed, result.Status);
        Assert.Null(result.ExitCode);
    }

    [Fact]
    public async Task RunAsync_WhenAlreadyCancelled_ReturnsCancelledWithoutStartingProcess()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var runner = new WindowsProcessRunner();
        var invocation = new ProcessInvocation(
            PowerShellPath,
            ImmutableArray<string>.Empty,
            Timeout: null);

        var result = await runner.RunAsync(invocation, output: null, cancellation.Token);

        Assert.Equal(ProcessExecutionStatus.Cancelled, result.Status);
        Assert.Null(result.ExitCode);
        Assert.Empty(result.StandardOutput);
        Assert.Empty(result.StandardError);
    }

    [Fact]
    public async Task RunAsync_WhenTimeoutIsNotPositive_ReturnsTimedOutWithoutStartingProcess()
    {
        var runner = new WindowsProcessRunner();
        var invocation = new ProcessInvocation(
            PowerShellPath,
            ImmutableArray<string>.Empty,
            Timeout: TimeSpan.Zero);

        var result = await runner.RunAsync(invocation, output: null, CancellationToken.None);

        Assert.Equal(ProcessExecutionStatus.TimedOut, result.Status);
        Assert.Null(result.ExitCode);
        Assert.Empty(result.StandardOutput);
        Assert.Empty(result.StandardError);
    }

    private static ProcessInvocation CreateInvocation(
        string scriptPath,
        TimeSpan? timeout,
        params string[] scriptArguments)
    {
        Assert.True(File.Exists(PowerShellPath), $"Missing harmless helper process: {PowerShellPath}");

        var arguments = ImmutableArray.CreateBuilder<string>();
        arguments.Add("-NoProfile");
        arguments.Add("-NonInteractive");
        arguments.Add("-ExecutionPolicy");
        arguments.Add("Bypass");
        arguments.Add("-File");
        arguments.Add(scriptPath);
        arguments.AddRange(scriptArguments);

        return new ProcessInvocation(PowerShellPath, arguments.ToImmutable(), timeout);
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Vela.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("The Vela repository root was not found.");
    }

    private sealed class RecordingProgress : IProgress<ProcessOutput>
    {
        private readonly ConcurrentQueue<ProcessOutput> _outputs = new();
        private readonly ConcurrentDictionary<string, TaskCompletionSource<ProcessOutput>> _outputWaiters =
            new(StringComparer.Ordinal);

        public ImmutableArray<ProcessOutput> Outputs => _outputs.ToImmutableArray();

        public void Report(ProcessOutput value)
        {
            _outputs.Enqueue(value);

            if (_outputWaiters.TryGetValue(value.Text, out var waiter))
            {
                waiter.TrySetResult(value);
            }
        }

        public Task<ProcessOutput> WaitForOutputAsync(string output, CancellationToken cancellationToken)
        {
            foreach (var capturedOutput in _outputs)
            {
                if (string.Equals(capturedOutput.Text, output, StringComparison.Ordinal))
                {
                    return Task.FromResult(capturedOutput);
                }
            }

            var waiter = _outputWaiters.GetOrAdd(
                output,
                static _ => new TaskCompletionSource<ProcessOutput>(
                    TaskCreationOptions.RunContinuationsAsynchronously));

            foreach (var capturedOutput in _outputs)
            {
                if (string.Equals(capturedOutput.Text, output, StringComparison.Ordinal))
                {
                    waiter.TrySetResult(capturedOutput);
                    break;
                }
            }

            return waiter.Task.WaitAsync(cancellationToken);
        }
    }

    private sealed class HelperScript : IDisposable
    {
        private HelperScript(string directoryPath, string scriptPath)
        {
            DirectoryPath = directoryPath;
            ScriptPath = scriptPath;
        }

        public string DirectoryPath { get; }

        public string ScriptPath { get; }

        public static HelperScript Create(string content)
        {
            var directoryPath = Path.Combine(
                FindRepositoryRoot(),
                "artifacts",
                "test-data",
                "process-runner-tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directoryPath);

            var scriptPath = Path.Combine(directoryPath, "helper.ps1");
            File.WriteAllText(
                scriptPath,
                content,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            return new HelperScript(directoryPath, scriptPath);
        }

        public void Dispose()
        {
            if (Directory.Exists(DirectoryPath))
            {
                Directory.Delete(DirectoryPath, recursive: true);
            }
        }
    }
}
