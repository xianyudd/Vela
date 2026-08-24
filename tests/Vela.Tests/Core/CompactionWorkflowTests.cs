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
    public async Task ExecuteAsync_WhenDetailWorkspaceValidationThrows_ReturnsDiskPartPreflightFailed()
    {
        var wsl = ReadyWsl();
        var diskPart = new RecordingDiskPartClient
        {
            DetailException = new InvalidOperationException("Privileged workspace validation failed."),
        };
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

        // diskpart 还没启动就抛出时, 事件里除了 "失败" 必须留下原因, 否则日志上
        // 只剩 exitCode=null / output=null, 没法把工作区问题和真实的 diskpart 失败区分开。
        var preflight = Assert.Single(journal.Events.Where(item => item.OperationName == "DiskPart detail vdisk"));
        Assert.Equal(RunEventLevel.Error, preflight.Level);
        Assert.Contains("InvalidOperationException", preflight.Output);
        Assert.Contains("Privileged workspace validation failed.", preflight.Output);
    }

    [Fact]
    public async Task ExecuteAsync_WhenCompactWorkspaceValidationThrows_ReturnsDiskPartCompactFailed()
    {
        var wsl = ReadyWsl();
        var diskPart = new RecordingDiskPartClient
        {
            CompactException = new InvalidOperationException("Privileged workspace validation failed."),
        };
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

        // diskpart 还没启动就抛出时, 事件里除了 "失败" 必须留下原因, 否则日志上
        // 只剩 exitCode=null / output=null, 没法把工作区问题和真实的 diskpart 失败区分开。
        var preflight = Assert.Single(journal.Events.Where(item => item.OperationName == "DiskPart compact vdisk"));
        Assert.Equal(RunEventLevel.Error, preflight.Level);
        Assert.Contains("InvalidOperationException", preflight.Output);
        Assert.Contains("Privileged workspace validation failed.", preflight.Output);
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

    [Fact]
    public async Task ExecuteAsync_TargetVhdxStillHeld_FailsPreflightBeforeInvokingDiskPart()
    {
        // WSL2 在发行版启动后会把磁盘挂到共享的工具 VM 上,直到该 VM 关闭才卸载;
        // 因此「运行中列表已达目标」并不代表 diskpart 能拿到独占句柄。
        var wsl = ReadyWsl();
        var resolver = new FakeLxssProfileResolver(MatchedResolution());
        var inspector = new ScriptedInspector(SucceededInspection(10_000), SucceededInspection(8_000));
        var diskPart = new RecordingDiskPartClient();
        var journal = new FakeRunJournal();
        var probe = new FakeVhdxHandleProbe { State = VhdxHandleState.Held };
        var workflow = CreateWorkflow(wsl, resolver, inspector, diskPart, journal, probe);

        var result = await workflow.ExecuteAsync(Request(ShutdownMode.Distro));

        Assert.Equal(TerminalResult.DiskPartPreflightFailed, result.Summary.TerminalResult);
        Assert.False(result.IsSuccessful);
        // 关键:被占用时绝不调用 diskpart,避免把裸的共享冲突当成压缩失败。
        Assert.Empty(diskPart.DetailPaths);
        Assert.Empty(diskPart.CompactPaths);
        Assert.Equal(VhdxPath, Assert.Single(probe.ProbedPaths));
        var diagnostic = Assert.Single(
            result.Diagnostics,
            item => item.Code == WorkflowDiagnosticCode.TargetVhdxInUse);
        Assert.Equal(RunPhase.DiskPartPreflight, diagnostic.Phase);
        Assert.Equal(RunEventLevel.Error, diagnostic.Level);
        Assert.Contains(journal.Events, item => item.OperationName == "Target VHDX handle probe");
    }

    [Fact]
    public async Task ExecuteAsync_TargetVhdxHeldInDistroMode_ExplainsShutdownScopeAndSparseAlternative()
    {
        var journal = new FakeRunJournal();
        var workflow = CreateWorkflow(
            ReadyWsl(),
            new FakeLxssProfileResolver(MatchedResolution()),
            new ScriptedInspector(SucceededInspection(10_000), SucceededInspection(8_000)),
            new RecordingDiskPartClient(),
            journal,
            new FakeVhdxHandleProbe { State = VhdxHandleState.Held });

        var result = await workflow.ExecuteAsync(Request(ShutdownMode.Distro));

        var message = Assert
            .Single(result.Diagnostics, item => item.Code == WorkflowDiagnosticCode.TargetVhdxInUse)
            .Message;
        // 诚实化的核心:必须点明 Distro 范围不卸载磁盘,并给出可执行的替代路径。
        Assert.Contains("does not detach the disk", message, StringComparison.Ordinal);
        Assert.Contains("wsl --shutdown", message, StringComparison.Ordinal);
        Assert.Contains("--set-sparse true --allow-unsafe", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecuteAsync_TargetVhdxHeldInGlobalMode_PointsAtAnExternalHolder()
    {
        var workflow = CreateWorkflow(
            ReadyWsl(),
            new FakeLxssProfileResolver(MatchedResolution()),
            new ScriptedInspector(SucceededInspection(10_000), SucceededInspection(8_000)),
            new RecordingDiskPartClient(),
            new FakeRunJournal(),
            new FakeVhdxHandleProbe { State = VhdxHandleState.Held });

        var result = await workflow.ExecuteAsync(Request(ShutdownMode.Global));

        var message = Assert
            .Single(result.Diagnostics, item => item.Code == WorkflowDiagnosticCode.TargetVhdxInUse)
            .Message;
        // Global 已经执行过 wsl --shutdown,再提示它毫无意义,应指向 WSL 之外的占用者。
        Assert.Contains("Another process still holds the file", message, StringComparison.Ordinal);
        Assert.DoesNotContain("Distro shutdown mode", message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(VhdxHandleState.Free)]
    [InlineData(VhdxHandleState.Unknown)]
    public async Task ExecuteAsync_HandleProbeDoesNotReportHeld_ProceedsToDiskPart(VhdxHandleState state)
    {
        var diskPart = new RecordingDiskPartClient();
        var workflow = CreateWorkflow(
            ReadyWsl(),
            new FakeLxssProfileResolver(MatchedResolution()),
            new ScriptedInspector(SucceededInspection(10_000), SucceededInspection(8_000)),
            diskPart,
            new FakeRunJournal(),
            new FakeVhdxHandleProbe { State = state });

        var result = await workflow.ExecuteAsync(Request(ShutdownMode.Global));

        // Unknown 代表「探测不出结论」,不是证据;必须放行,不能让新闸门拖垮原本能成功的运行。
        Assert.Equal(TerminalResult.Succeeded, result.Summary.TerminalResult);
        Assert.Equal(VhdxPath, Assert.Single(diskPart.CompactPaths));
        Assert.DoesNotContain(result.Diagnostics, item => item.Code == WorkflowDiagnosticCode.TargetVhdxInUse);
    }

    [Fact]
    public async Task ExecuteAsync_HandleProbeThrows_FailsOpenAndStillCompacts()
    {
        var diskPart = new RecordingDiskPartClient();
        var workflow = CreateWorkflow(
            ReadyWsl(),
            new FakeLxssProfileResolver(MatchedResolution()),
            new ScriptedInspector(SucceededInspection(10_000), SucceededInspection(8_000)),
            diskPart,
            new FakeRunJournal(),
            new FakeVhdxHandleProbe { Failure = new UnauthorizedAccessException("denied") });

        var result = await workflow.ExecuteAsync(Request(ShutdownMode.Global));

        // 探测器抛异常同样只是「无结论」,绝不能变成一次失败的压缩。
        Assert.Equal(TerminalResult.Succeeded, result.Summary.TerminalResult);
        Assert.Equal(VhdxPath, Assert.Single(diskPart.CompactPaths));
        Assert.DoesNotContain(result.Diagnostics, item => item.Code == WorkflowDiagnosticCode.TargetVhdxInUse);
    }

    private static CompactionWorkflow CreateWorkflow(
        IWslClient wsl,
        ILxssProfileResolver resolver,
        IVhdxInspector inspector,
        IDiskPartClient diskPart,
        IRunJournal journal,
        IVhdxHandleProbe? handleProbe = null,
        TimeSpan? pollInterval = null) => new(
        wsl,
        resolver,
        inspector,
        diskPart,
        // 默认放行:已有用例不受新增的句柄闸门影响。
        handleProbe ?? new FakeVhdxHandleProbe(),
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
        public Exception? DetailException { get; init; }
        public Exception? CompactException { get; init; }
        public List<string> DetailPaths { get; } = new();
        public List<string> CompactPaths { get; } = new();

        public Task<ProcessExecutionResult> DetailVdiskAsync(Guid runId, string validatedVhdxPath, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DetailPaths.Add(validatedVhdxPath);
            _onInvoked?.Invoke("diskpart.detail");
            if (DetailException is not null)
            {
                return Task.FromException<ProcessExecutionResult>(DetailException);
            }
            return Task.FromResult(DetailResult);
        }

        public Task<ProcessExecutionResult> CompactVdiskAsync(Guid runId, string validatedVhdxPath, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CompactPaths.Add(validatedVhdxPath);
            _onInvoked?.Invoke("diskpart.compact");
            if (CompactException is not null)
            {
                return Task.FromException<ProcessExecutionResult>(CompactException);
            }
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
