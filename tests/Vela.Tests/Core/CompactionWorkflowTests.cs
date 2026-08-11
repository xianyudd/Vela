using System.Collections.Immutable;
using Vela.Core.Contracts;
using Vela.Core.Models;
using Vela.Core.Workflows;
using Vela.Tests.Fakes;

namespace Vela.Tests.Core;

public sealed class CompactionWorkflowTests
{
    private const string Distro = "Ubuntu-24.04";
    private const string VhdxPath = "D:\\DevTools\\WSL2\\Ubuntu24.04\\ext4.vhdx";

    [Fact]
    public async Task ExecuteAsync_GlobalShutdown_WaitsThenRunsDetailAndCompactAndComputesReclaim()
    {
        var trace = new List<string>();
        var wsl = new ScriptedWslClient(
            installed: Inventory(Distro, WslDistributionState.Stopped),
            running: [Inventory(Distro, WslDistributionState.Running), Inventory(Distro, WslDistributionState.Stopped)],
            onInvoked: trace.Add);
        var resolver = new FakeLxssProfileResolver(MatchedResolution(), trace.Add);
        var inspector = new ScriptedInspector(
            SucceededInspection(10_000),
            SucceededInspection(8_000),
            trace.Add);
        var diskPart = new RecordingDiskPartClient(trace.Add);
        var journal = new FakeRunJournal(trace.Add);
        var workflow = CreateWorkflow(wsl, resolver, inspector, diskPart, journal);

        var result = await workflow.ExecuteAsync(Request(ShutdownMode.Global));

        Assert.Equal(TerminalResult.Succeeded, result.Summary.TerminalResult);
        Assert.Equal(2_000, result.Summary.ReclaimedBytes);
        Assert.Equal(VhdxPath, Assert.Single(diskPart.DetailPaths));
        Assert.Equal(VhdxPath, Assert.Single(diskPart.CompactPaths));
        Assert.Equal(1, wsl.ShutdownAllCalls);
        Assert.Empty(wsl.TerminatedDistros);
        Assert.Equal(
            ["wsl.installed", "lxss.resolve", "vhdx.inspect.before", "wsl.running", "wsl.shutdown-all", "wsl.running", "diskpart.detail", "diskpart.compact", "vhdx.inspect.after"],
            trace.Where(item => !item.StartsWith("journal", StringComparison.Ordinal)).ToArray());
        Assert.Contains(journal.Events, item => item.OperationName == "DiskPart detail vdisk");
        Assert.Contains(journal.Events, item => item.OperationName == "DiskPart compact vdisk");
        Assert.Equal(TerminalResult.Succeeded, Assert.Single(journal.Summaries).TerminalResult);
    }

    [Fact]
    public async Task ExecuteAsync_DistroShutdownTerminatesOnlyTargetAndWaitsForTargetToStop()
    {
        var wsl = new ScriptedWslClient(
            installed: Inventory(Distro, WslDistributionState.Stopped),
            running: [
                new WslInventory(DateTimeOffset.UnixEpoch, ImmutableArray.Create(
                    new WslDistribution(Distro, WslDistributionState.Running, 2, true),
                    new WslDistribution("Other", WslDistributionState.Running, 2, false))),
                new WslInventory(DateTimeOffset.UnixEpoch, ImmutableArray.Create(
                    new WslDistribution(Distro, WslDistributionState.Stopped, 2, true),
                    new WslDistribution("Other", WslDistributionState.Running, 2, false))) ]);
        var diskPart = new RecordingDiskPartClient();
        var journal = new FakeRunJournal();
        var workflow = CreateWorkflow(
            wsl,
            new FakeLxssProfileResolver(MatchedResolution()),
            new ScriptedInspector(SucceededInspection(10_000), SucceededInspection(9_000)),
            diskPart,
            journal);

        var result = await workflow.ExecuteAsync(Request(ShutdownMode.Distro));

        Assert.Equal(TerminalResult.Succeeded, result.Summary.TerminalResult);
        Assert.Equal([Distro], wsl.TerminatedDistros.ToArray());
        Assert.Equal(0, wsl.ShutdownAllCalls);
        Assert.Single(diskPart.CompactPaths);
    }

    [Fact]
    public async Task ExecuteAsync_WhenShutdownNeverReachesTarget_ReturnsTimeoutAndSkipsDiskPart()
    {
        var wsl = new ScriptedWslClient(
            installed: Inventory(Distro, WslDistributionState.Stopped),
            running: Enumerable.Repeat(Inventory(Distro, WslDistributionState.Running), 8).ToArray());
        var diskPart = new RecordingDiskPartClient();
        var journal = new FakeRunJournal();
        var workflow = CreateWorkflow(
            wsl,
            new FakeLxssProfileResolver(MatchedResolution()),
            new ScriptedInspector(SucceededInspection(10_000), SucceededInspection(9_000)),
            diskPart,
            journal,
            pollInterval: TimeSpan.FromSeconds(1));

        var result = await workflow.ExecuteAsync(Request(ShutdownMode.Global, timeout: TimeSpan.FromSeconds(5)));

        Assert.Equal(TerminalResult.ShutdownTimedOut, result.Summary.TerminalResult);
        Assert.Empty(diskPart.DetailPaths);
        Assert.Empty(diskPart.CompactPaths);
        Assert.Contains(journal.Events, item => item.OperationName == "WSL shutdown timeout");
        Assert.Equal(TerminalResult.ShutdownTimedOut, Assert.Single(journal.Summaries).TerminalResult);
    }

    [Fact]
    public async Task ExecuteAsync_WhenDiskPartDetailFails_SkipsCompactAndReturnsPreflightFailure()
    {
        var wsl = ReadyWsl();
        var diskPart = new RecordingDiskPartClient { DetailResult = FailedProcessResult(17) };
        var journal = new FakeRunJournal();
        var workflow = CreateWorkflow(
            wsl,
            new FakeLxssProfileResolver(MatchedResolution()),
            new ScriptedInspector(SucceededInspection(10_000), SucceededInspection(9_000)),
            diskPart,
            journal);

        var result = await workflow.ExecuteAsync(Request(ShutdownMode.Global));

        Assert.Equal(TerminalResult.DiskPartPreflightFailed, result.Summary.TerminalResult);
        Assert.Single(diskPart.DetailPaths);
        Assert.Empty(diskPart.CompactPaths);
        Assert.Null(result.Summary.AfterSnapshot);
    }

    [Fact]
    public async Task ExecuteAsync_WhenDiskPartCompactFails_ReturnsCompactFailure()
    {
        var wsl = ReadyWsl();
        var diskPart = new RecordingDiskPartClient { CompactResult = FailedProcessResult(18) };
        var journal = new FakeRunJournal();
        var workflow = CreateWorkflow(
            wsl,
            new FakeLxssProfileResolver(MatchedResolution()),
            new ScriptedInspector(SucceededInspection(10_000), SucceededInspection(9_000)),
            diskPart,
            journal);

        var result = await workflow.ExecuteAsync(Request(ShutdownMode.Global));

        Assert.Equal(TerminalResult.DiskPartCompactFailed, result.Summary.TerminalResult);
        Assert.Single(diskPart.DetailPaths);
        Assert.Single(diskPart.CompactPaths);
        Assert.Null(result.Summary.AfterSnapshot);
    }

    [Fact]
    public async Task ExecuteAsync_WhenLengthDoesNotChange_ReturnsCompletedWithNoReclaim()
    {
        var wsl = ReadyWsl();
        var journal = new FakeRunJournal();
        var workflow = CreateWorkflow(
            wsl,
            new FakeLxssProfileResolver(MatchedResolution()),
            new ScriptedInspector(SucceededInspection(10_000), SucceededInspection(10_000)),
            new RecordingDiskPartClient(),
            journal);

        var result = await workflow.ExecuteAsync(Request(ShutdownMode.Global));

        Assert.Equal(TerminalResult.CompletedWithNoReclaim, result.Summary.TerminalResult);
        Assert.Equal(0, result.Summary.ReclaimedBytes);
        Assert.True(result.IsSuccessful);
    }

    [Fact]
    public async Task ExecuteAsync_WhenAfterLengthGrows_DoesNotReportNegativeReclaim()
    {
        var wsl = ReadyWsl();
        var journal = new FakeRunJournal();
        var workflow = CreateWorkflow(
            wsl,
            new FakeLxssProfileResolver(MatchedResolution()),
            new ScriptedInspector(SucceededInspection(10_000), SucceededInspection(12_000)),
            new RecordingDiskPartClient(),
            journal);

        var result = await workflow.ExecuteAsync(Request(ShutdownMode.Global));

        Assert.Equal(TerminalResult.CompletedWithNoReclaim, result.Summary.TerminalResult);
        Assert.Equal(0, result.Summary.ReclaimedBytes);
        Assert.Contains(
            journal.Events,
            @event => @event.OperationName == "Compaction completed" &&
                @event.Arguments.SequenceEqual(["0"]));
    }

    [Fact]
    public async Task ExecuteAsync_WhenOpeningAnExistingRun_DefersSummaryPublicationToTheWorker()
    {
        var wsl = ReadyWsl();
        var journal = new FakeRunJournal();
        var workflow = CreateWorkflow(
            wsl,
            new FakeLxssProfileResolver(MatchedResolution()),
            new ScriptedInspector(SucceededInspection(10_000), SucceededInspection(9_000)),
            new RecordingDiskPartClient(),
            journal);

        var result = await workflow.ExecuteAsync(
            Request(ShutdownMode.Global),
            RunJournalAccessMode.OpenExisting,
            CancellationToken.None);

        Assert.Equal(TerminalResult.Succeeded, result.Summary.TerminalResult);
        Assert.Empty(journal.Summaries);
    }

    [Fact]
    public async Task ExecuteAsync_WhenLxssMappingChanges_SkipsAllActions()
    {
        var wsl = ReadyWsl();
        var diskPart = new RecordingDiskPartClient();
        var journal = new FakeRunJournal();
        var workflow = CreateWorkflow(
            wsl,
            new FakeLxssProfileResolver(new LxssProfileResolution(
                LxssResolutionStatus.Mismatched,
                Distro,
                "D:\\Other\\ext4.vhdx",
                VhdxPath)),
            new ScriptedInspector(SucceededInspection(10_000), SucceededInspection(9_000)),
            diskPart,
            journal);

        var result = await workflow.ExecuteAsync(Request(ShutdownMode.Global));

        Assert.Equal(TerminalResult.ValidationFailed, result.Summary.TerminalResult);
        Assert.Equal(0, wsl.ShutdownAllCalls);
        Assert.Empty(wsl.TerminatedDistros);
        Assert.Empty(diskPart.DetailPaths);
        Assert.Empty(diskPart.CompactPaths);
    }

    [Fact]
    public async Task ExecuteAsync_WhenJournalAppendThrows_ReturnsOperationResultAndDiagnostic()
    {
        var wsl = ReadyWsl();
        var journal = new FakeRunJournal { ThrowOnAppend = true };
        var workflow = CreateWorkflow(
            wsl,
            new FakeLxssProfileResolver(MatchedResolution()),
            new ScriptedInspector(SucceededInspection(10_000), SucceededInspection(9_000)),
            new RecordingDiskPartClient(),
            journal);

        var result = await workflow.ExecuteAsync(Request(ShutdownMode.Global));

        Assert.Equal(TerminalResult.Succeeded, result.Summary.TerminalResult);
        Assert.Contains(result.Diagnostics, item => item.Code == WorkflowDiagnosticCode.JournalFailure);
    }

    [Fact]
    public async Task ExecuteAsync_WhenJournalSummaryThrows_ReturnsOperationResultAndDiagnostic()
    {
        var wsl = ReadyWsl();
        var journal = new FakeRunJournal { ThrowOnWriteSummary = true };
        var workflow = CreateWorkflow(
            wsl,
            new FakeLxssProfileResolver(MatchedResolution()),
            new ScriptedInspector(SucceededInspection(10_000), SucceededInspection(9_000)),
            new RecordingDiskPartClient(),
            journal);

        var result = await workflow.ExecuteAsync(Request(ShutdownMode.Global));

        Assert.Equal(TerminalResult.Succeeded, result.Summary.TerminalResult);
        Assert.Contains(result.Diagnostics, item => item.Code == WorkflowDiagnosticCode.JournalFailure);
    }

    private static CompactionWorkflow CreateWorkflow(
        IWslClient wsl,
        ILxssProfileResolver resolver,
        IVhdxInspector inspector,
        IDiskPartClient diskPart,
        IRunJournal journal,
        TimeSpan? pollInterval = null) => new(
        wsl,
        resolver,
        inspector,
        diskPart,
        journal,
        new FixedClock(),
        pollInterval);

    private static ScriptedWslClient ReadyWsl() => new(
        Inventory(Distro, WslDistributionState.Stopped),
        [Inventory(Distro, WslDistributionState.Running), Inventory(Distro, WslDistributionState.Stopped)]);

    private static OperationRequest Request(ShutdownMode mode, TimeSpan? timeout = null) => new(
        Guid.Parse("58d25bb8-b714-4fa8-bc8c-11233c05c173"),
        new Profile(
            Guid.Parse("b5798574-bc95-4bf6-a09a-994934e58e8d"),
            "Ubuntu 24.04",
            Distro,
            VhdxPath,
            mode,
            timeout ?? TimeSpan.FromSeconds(45)),
        OperationIntent.Compact);

    private static WslInventory Inventory(string distroName, WslDistributionState state) => new(
        DateTimeOffset.UnixEpoch,
        ImmutableArray.Create(new WslDistribution(distroName, state, 2, true)));

    private static LxssProfileResolution MatchedResolution() => new(
        LxssResolutionStatus.Matched,
        Distro,
        VhdxPath,
        VhdxPath);

    private static VhdxInspectionResult SucceededInspection(long length) => new(
        VhdxInspectionStatus.Succeeded,
        new VhdxSnapshot(
            DateTimeOffset.UnixEpoch,
            VhdxPath,
            length,
            DateTimeOffset.UnixEpoch,
            true,
            new DriveSnapshot("D:\\", 1_000_000, 500_000)));

    private static ProcessExecutionResult FailedProcessResult(int exitCode) => new(
        ProcessExecutionStatus.Failed,
        exitCode,
        ImmutableArray<string>.Empty,
        ImmutableArray.Create("error"),
        DateTimeOffset.UnixEpoch,
        DateTimeOffset.UnixEpoch);

    private sealed class ScriptedWslClient : IWslClient
    {
        private readonly Queue<WslInventory> _running;
        private readonly Action<string>? _onInvoked;
        private ImmutableArray<string> _terminated = ImmutableArray<string>.Empty;

        public ScriptedWslClient(WslInventory installed, IEnumerable<WslInventory> running, Action<string>? onInvoked = null)
        {
            Installed = installed;
            _running = new Queue<WslInventory>(running);
            _onInvoked = onInvoked;
        }

        public WslInventory Installed { get; }
        public int ShutdownAllCalls { get; private set; }
        public ImmutableArray<string> TerminatedDistros => _terminated;

        public Task<WslInventory> GetInstalledInventoryAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _onInvoked?.Invoke("wsl.installed");
            return Task.FromResult(Installed);
        }

        public Task<WslInventory> GetRunningInventoryAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _onInvoked?.Invoke("wsl.running");
            if (_running.Count == 0)
            {
                return Task.FromResult(Inventory(Distro, WslDistributionState.Stopped));
            }

            var inventory = _running.Dequeue();
            return Task.FromResult(inventory);
        }

        public Task<ProcessExecutionResult> ShutdownAllAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ShutdownAllCalls++;
            _onInvoked?.Invoke("wsl.shutdown-all");
            return Task.FromResult(SucceededProcessResult());
        }

        public Task<ProcessExecutionResult> TerminateDistroAsync(string distroName, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _terminated = _terminated.Add(distroName);
            _onInvoked?.Invoke("wsl.terminate");
            return Task.FromResult(SucceededProcessResult());
        }
    }

    private sealed class ScriptedInspector : IVhdxInspector
    {
        private readonly Queue<VhdxInspectionResult> _results;
        private readonly Action<string>? _onInvoked;

        public ScriptedInspector(VhdxInspectionResult before, VhdxInspectionResult after, Action<string>? onInvoked = null)
        {
            _results = new Queue<VhdxInspectionResult>([before, after]);
            _onInvoked = onInvoked;
        }

        public Task<VhdxInspectionResult> InspectAsync(string vhdxPath, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _onInvoked?.Invoke(_results.Count == 2 ? "vhdx.inspect.before" : "vhdx.inspect.after");
            return Task.FromResult(_results.Count == 0 ? new VhdxInspectionResult(VhdxInspectionStatus.Failed, null) : _results.Dequeue());
        }
    }

    private sealed class RecordingDiskPartClient : IDiskPartClient
    {
        private readonly Action<string>? _onInvoked;

        public RecordingDiskPartClient(Action<string>? onInvoked = null) => _onInvoked = onInvoked;

        public ProcessExecutionResult DetailResult { get; init; } = SucceededProcessResult();
        public ProcessExecutionResult CompactResult { get; init; } = SucceededProcessResult();
        public List<string> DetailPaths { get; } = new();
        public List<string> CompactPaths { get; } = new();

        public Task<ProcessExecutionResult> DetailVdiskAsync(string validatedVhdxPath, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DetailPaths.Add(validatedVhdxPath);
            _onInvoked?.Invoke("diskpart.detail");
            return Task.FromResult(DetailResult);
        }

        public Task<ProcessExecutionResult> CompactVdiskAsync(string validatedVhdxPath, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CompactPaths.Add(validatedVhdxPath);
            _onInvoked?.Invoke("diskpart.compact");
            return Task.FromResult(CompactResult);
        }
    }

    private static ProcessExecutionResult SucceededProcessResult() => new(
        ProcessExecutionStatus.Succeeded,
        0,
        ImmutableArray.Create("ok"),
        ImmutableArray<string>.Empty,
        DateTimeOffset.UnixEpoch,
        DateTimeOffset.UnixEpoch);
}
