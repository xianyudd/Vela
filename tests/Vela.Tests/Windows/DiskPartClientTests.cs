using System.Collections.Immutable;
using Vela.Core.Contracts;
using Vela.Windows.DiskPart;
using Vela.Windows.Processes;

namespace Vela.Tests.Windows;

public sealed class DiskPartClientTests
{
    private const string VhdxPath = "D:\\DevTools\\WSL2\\Ubuntu24.04\\ext4.vhdx";
    private static readonly Guid RunId = Guid.Parse("7a9156f1-1c2c-4fd0-a5d7-5aa8aa8b8d39");

    private static (RecordingWorkspace Workspace, RecordingProcessRunner Runner, DiskPartClient Client) CreateClient()
    {
        var workspace = new RecordingWorkspace();
        var runner = new RecordingProcessRunner();
        workspace.AttachRunner(runner);
        var client = new DiskPartClient(
            runner,
            new NativeToolPaths(),
            new DiskPartScriptBuilder(),
            workspace);
        return (workspace, runner, client);
    }

    [Fact]
    public async Task DetailAndCompact_PassTrustedRunIdToWorkspace()
    {
        var (workspace, _, client) = CreateClient();

        await client.DetailVdiskAsync(RunId, VhdxPath, CancellationToken.None);
        await client.CompactVdiskAsync(RunId, VhdxPath, CancellationToken.None);

        Assert.Equal(new[] { RunId, RunId }, workspace.CreateRunIds.ToArray());
    }

    [Fact]
    public async Task Detail_KeepsScriptLeaseOpenWhileProcessRuns()
    {
        var (workspace, runner, client) = CreateClient();

        await client.DetailVdiskAsync(RunId, VhdxPath, CancellationToken.None);

        Assert.False(
            runner.ObservedLeaseDisposedDuringInvocation,
            "Lease must remain open while diskpart.exe runs.");
        Assert.True(workspace.LastLeaseDisposed, "Lease must be disposed after the runner completes.");
    }

    [Fact]
    public async Task Detail_DisposesLeaseWhenRunnerThrows()
    {
        var (workspace, runner, client) = CreateClient();
        runner.ThrowOnInvocation = true;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.DetailVdiskAsync(RunId, VhdxPath, CancellationToken.None));

        Assert.True(workspace.LastLeaseDisposed);
    }

    [Fact]
    public async Task Detail_VerifiesLeaseBeforeLaunchAndAfterProcessExit()
    {
        var (workspace, _, client) = CreateClient();

        await client.DetailVdiskAsync(RunId, VhdxPath, CancellationToken.None);

        var lease = workspace.LastLease;
        Assert.NotNull(lease);
        Assert.Equal(2, lease.VerifyCalls);
        Assert.Equal(0, lease.RunnerCallsObservedAtFirstVerify);
        Assert.Equal(1, lease.RunnerCallsObservedAtSecondVerify);
    }

    [Fact]
    public async Task Detail_WhenPreLaunchVerifyFails_DoesNotInvokeRunner()
    {
        var (workspace, runner, client) = CreateClient();
        workspace.FailVerifyCalls = new HashSet<int> { 1 };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.DetailVdiskAsync(RunId, VhdxPath, CancellationToken.None));

        Assert.Empty(runner.Invocations);
        Assert.True(workspace.LastLeaseDisposed);
    }

    [Fact]
    public async Task Detail_WhenEmptyRunId_ThrowsArgumentException()
    {
        var (workspace, runner, client) = CreateClient();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            client.DetailVdiskAsync(Guid.Empty, VhdxPath, CancellationToken.None));

        Assert.Empty(runner.Invocations);
        Assert.Equal(0, workspace.CreateCalls);
    }

    /// <summary>
    /// Coordinates the workspace and runner so the runner can read the
    /// lease's current Disposed flag during invocation.
    /// </summary>
    private sealed class RecordingWorkspace : IPrivilegedDiskPartWorkspace
    {
        public List<Guid> CreateRunIds { get; } = new();
        public int CreateCalls { get; private set; }
        public RecordingLease? LastLease { get; private set; }
        public bool LastLeaseDisposed => LastLease?.Disposed ?? false;
        public HashSet<int>? FailVerifyCalls { get; set; }
        public RecordingProcessRunner? Runner { get; private set; }

        public void AttachRunner(RecordingProcessRunner runner)
        {
            Runner = runner;
            runner.Workspace = this;
        }

        public Task<IPrivilegedDiskPartScriptLease> CreateScriptAsync(
            Guid runId,
            string script,
            CancellationToken cancellationToken)
        {
            CreateCalls++;
            CreateRunIds.Add(runId);
            LastLease = new RecordingLease(
                $"C:\\Temp\\vela-diskpart-{runId:N}.txt",
                script,
                this);
            if (Runner is not null)
            {
                Runner.ActiveLease = LastLease;
            }
            return Task.FromResult<IPrivilegedDiskPartScriptLease>(LastLease);
        }

        internal bool IsFailVerifyCall(int callIndex) => FailVerifyCalls?.Contains(callIndex) == true;
        internal int GetRunnerCallCount() => Runner?.Invocations.Count ?? 0;
    }

    private sealed class RecordingLease : IPrivilegedDiskPartScriptLease
    {
        private readonly RecordingWorkspace _workspace;

        public RecordingLease(string scriptPath, string script, RecordingWorkspace workspace)
        {
            ScriptPath = scriptPath;
            Script = script;
            _workspace = workspace;
        }

        public string Script { get; }
        public string ScriptPath { get; }
        public bool Disposed { get; private set; }
        public int VerifyCalls { get; private set; }
        public int RunnerCallsObservedAtFirstVerify { get; private set; }
        public int RunnerCallsObservedAtSecondVerify { get; private set; }

        public ValueTask VerifyAsync(CancellationToken cancellationToken)
        {
            VerifyCalls++;
            var observed = _workspace.GetRunnerCallCount();
            switch (VerifyCalls)
            {
                case 1:
                    RunnerCallsObservedAtFirstVerify = observed;
                    break;
                case 2:
                    RunnerCallsObservedAtSecondVerify = observed;
                    break;
            }

            if (_workspace.IsFailVerifyCall(VerifyCalls))
            {
                throw new InvalidOperationException($"Lease verification failure at call {VerifyCalls}.");
            }

            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingProcessRunner : IProcessRunner
    {
        public bool ThrowOnInvocation { get; set; }
        public List<ProcessInvocation> Invocations { get; } = new();
        public RecordingLease? ActiveLease { get; set; }
        public RecordingWorkspace? Workspace { get; set; }

        public bool ObservedLeaseDisposedDuringInvocation { get; private set; }

        public Task<ProcessExecutionResult> RunAsync(
            ProcessInvocation invocation,
            IProgress<ProcessOutput>? output,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Invocations.Add(invocation);
            ObservedLeaseDisposedDuringInvocation = ActiveLease?.Disposed ?? false;

            if (ThrowOnInvocation)
            {
                throw new InvalidOperationException("runner failure");
            }

            return Task.FromResult(new ProcessExecutionResult(
                ProcessExecutionStatus.Succeeded,
                0,
                ImmutableArray.Create("detail output"),
                ImmutableArray<string>.Empty,
                DateTimeOffset.UnixEpoch,
                DateTimeOffset.UnixEpoch));
        }
    }
}
