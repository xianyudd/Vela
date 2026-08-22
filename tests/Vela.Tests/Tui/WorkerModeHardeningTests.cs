using System.Collections.Immutable;
using Vela.Core.Contracts;
using Vela.Core.Models;
using Vela.Core.Workflows;
using Vela.Tui.ProgramModes;
using Vela.Windows.Diagnostics;

namespace Vela.Tests.Tui;

public sealed class WorkerModeHardeningTests
{
    [Fact]
    public async Task RunAsync_WhenWorkflowSummaryUsesAnotherRunId_WritesOnlyTrustedCliRunId()
    {
        using var root = TestRoot.Create();
        var paths = new AppPaths(root.RootDirectory);
        var runId = Guid.Parse("b7a0f0c4-0a19-4db9-98b0-e2b11917b569");
        var request = CreateRequest(runId);
        var journal = new RecordingJournal();
        var store = new RecordingStore(request, paths.GetPendingRequestFilePath(runId));
        var wrongSummary = new RunSummary(
            Guid.Parse("2b7a3ec6-447e-4e76-9fa0-25c22e8bf122"),
            request.Profile,
            request.Intent,
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch,
            null,
            null,
            TerminalResult.Succeeded);
        var mode = new WorkerMode(
            paths,
            store,
            journal,
            new FixedAdministratorProbe(true),
            new FixedResolver(),
            new FixedExecutor(new WorkflowResult(
                wrongSummary,
                new PreflightReport(
                    Vela.Core.Validation.ValidationResult.Valid,
                    null,
                    null,
                    null,
                    null),
                ImmutableArray<WorkflowDiagnostic>.Empty,
                null)),
            new FixedClock());

        var result = await mode.RunAsync(
            ["--worker", "--run-id", runId.ToString("D")],
            CancellationToken.None);

        Assert.Equal(TerminalResult.WorkerInterrupted, result.TerminalResult);
        Assert.Equal(10, result.ExitCode);
        Assert.Contains(journal.Events, item =>
            item.RunId == runId && item.OperationName == "WorkerSummaryRunIdMismatch");
        Assert.Equal(runId, Assert.Single(journal.Summaries).RunId);
        Assert.Equal(runId, Assert.Single(store.ConsumedRunIds));
    }

    [Fact]
    public async Task RunAsync_WhenAdministratorProbeThrows_MapsToWorkerInterrupted()
    {
        using var root = TestRoot.Create();
        var paths = new AppPaths(root.RootDirectory);
        var runId = Guid.Parse("c2f9f715-2163-49ef-970c-9ef2d4ed8e3d");
        var request = CreateRequest(runId);
        var journal = new RecordingJournal();
        var store = new RecordingStore(request, paths.GetPendingRequestFilePath(runId));
        var mode = new WorkerMode(
            paths,
            store,
            journal,
            new ThrowingAdministratorProbe(),
            new FixedResolver(),
            new FixedExecutor(CreateWorkflow(request, TerminalResult.Succeeded)),
            new FixedClock());

        var result = await mode.RunAsync(
            ["--worker", "--run-id", runId.ToString("D")],
            CancellationToken.None);

        Assert.Equal(TerminalResult.WorkerInterrupted, result.TerminalResult);
        Assert.Equal(10, result.ExitCode);
        Assert.Empty(journal.Operations);
        Assert.Empty(store.ClaimedRunIds);
        Assert.Empty(store.ConsumedRunIds);
    }

    [Fact]
    public async Task RunAsync_WhenNotAdministrator_DoesNotOpenJournalClaimConsumeOrAppend()
    {
        using var root = TestRoot.Create();
        var paths = new AppPaths(root.RootDirectory);
        var runId = Guid.Parse("e5b3c1a2-0f4d-4e7b-9c1a-2b3d4e5f6a7b");
        var request = CreateRequest(runId);
        var journal = new RecordingJournal();
        var store = new RecordingStore(request, paths.GetPendingRequestFilePath(runId));
        var mode = new WorkerMode(
            paths,
            store,
            journal,
            new FixedAdministratorProbe(false),
            new FixedResolver(),
            new FixedExecutor(CreateWorkflow(request, TerminalResult.Succeeded)),
            new FixedClock());

        var result = await mode.RunAsync(
            ["--worker", "--run-id", runId.ToString("D")],
            CancellationToken.None);

        Assert.Equal(TerminalResult.ValidationFailed, result.TerminalResult);
        Assert.Equal(2, result.ExitCode);
        Assert.Empty(store.ClaimedRunIds);
        Assert.Empty(store.ConsumedRunIds);
        Assert.Empty(journal.Operations);
    }

    [Fact]
    public async Task RunAsync_WhenArgumentsInvalid_DoesNotProbeAdministrator()
    {
        using var root = TestRoot.Create();
        var paths = new AppPaths(root.RootDirectory);
        var runId = Guid.Parse("f6c4d2b3-1a5e-4f8c-8d2b-3c4e5f6a7b8c");
        var request = CreateRequest(runId);
        var journal = new RecordingJournal();
        var store = new RecordingStore(request, paths.GetPendingRequestFilePath(runId));
        var probe = new RecordingAdministratorProbe(false);
        var mode = new WorkerMode(
            paths,
            store,
            journal,
            probe,
            new FixedResolver(),
            new FixedExecutor(CreateWorkflow(request, TerminalResult.Succeeded)),
            new FixedClock());

        var result = await mode.RunAsync(
            ["--worker", "--run-id", "not-a-guid"],
            CancellationToken.None);

        Assert.Equal(TerminalResult.ValidationFailed, result.TerminalResult);
        Assert.Equal(2, result.ExitCode);
        Assert.Equal(0, probe.CallCount);
        Assert.Empty(journal.Operations);
        Assert.Empty(store.ClaimedRunIds);
        Assert.Empty(store.ConsumedRunIds);
    }

    [Fact]
    public async Task RunAsync_WhenConsumeFails_ReturnsTheDurableTerminalResultAndKeepsCanonicalEvent()
    {
        using var root = TestRoot.Create();
        var paths = new AppPaths(root.RootDirectory);
        var runId = Guid.Parse("d3e0d0d7-1f3c-4a9b-9ad0-8adf61f8f6f5");
        var request = CreateRequest(runId);
        var journal = new RecordingJournal();
        var store = new RecordingStore(request, paths.GetPendingRequestFilePath(runId), consumeSucceeds: false);
        var mode = new WorkerMode(
            paths,
            store,
            journal,
            new FixedAdministratorProbe(true),
            new FixedResolver(),
            new FixedExecutor(CreateWorkflow(request, TerminalResult.ValidationFailed)),
            new FixedClock());

        var result = await mode.RunAsync(
            ["--worker", "--run-id", runId.ToString("D")],
            CancellationToken.None);

        Assert.Equal(TerminalResult.ValidationFailed, result.TerminalResult);
        Assert.Equal(2, result.ExitCode);
        Assert.Contains(journal.Events, item =>
            item.OperationName == "WorkerFailed" &&
            item.TerminalResult == TerminalResult.ValidationFailed &&
            item.ExitCode == 2);
        Assert.Contains(journal.Events, item => item.OperationName == "WorkerRequestConsumeFailed");
        Assert.Equal(TerminalResult.ValidationFailed, Assert.Single(journal.Summaries).TerminalResult);
        Assert.Equal(runId, Assert.Single(store.ConsumedRunIds));
        // The lifecycle breadcrumbs precede the terminal event: they exist so a
        // worker that dies mid-run still shows how far it got.
        Assert.Equal(
            new[]
            {
                "open",
                "append:WorkerRequestClaimed",
                "append:WorkerTargetResolved",
                "append:WorkerFailed",
                "summary",
                "append:WorkerRequestConsumeFailed"
            },
            journal.Operations);
    }

    [Fact]
    public async Task RunAsync_WhenSucceeded_WritesCanonicalTerminalEventFieldsAndExitCode()
    {
        using var root = TestRoot.Create();
        var paths = new AppPaths(root.RootDirectory);
        var runId = Guid.Parse("a1b2c3d4-e5f6-4a7b-8c9d-0e1f2a3b4c5d");
        var request = CreateRequest(runId);
        var journal = new RecordingJournal();
        var store = new RecordingStore(request, paths.GetPendingRequestFilePath(runId));
        var mode = new WorkerMode(
            paths,
            store,
            journal,
            new FixedAdministratorProbe(true),
            new FixedResolver(),
            new FixedExecutor(CreateWorkflow(request, TerminalResult.Succeeded)),
            new FixedClock());

        var result = await mode.RunAsync(
            ["--worker", "--run-id", runId.ToString("D")],
            CancellationToken.None);

        Assert.Equal(TerminalResult.Succeeded, result.TerminalResult);
        Assert.Equal(0, result.ExitCode);
        var terminal = Assert.Single(
            journal.Events,
            item => item.OperationName == "WorkerCompleted");
        Assert.Equal(runId, terminal.RunId);
        Assert.Equal(TerminalResult.Succeeded, terminal.TerminalResult);
        Assert.Equal(0, terminal.ExitCode);
        Assert.Equal(
            WorkerExitCodes.FromTerminalResult(terminal.TerminalResult!.Value),
            terminal.ExitCode);
        Assert.Equal(runId, Assert.Single(journal.Summaries).RunId);
        Assert.Equal(runId, Assert.Single(store.ConsumedRunIds));
    }
    [Fact]
    public async Task RunAsync_WritesLifecycleBreadcrumbsAsTraceEventsWithoutRawOutput()
    {
        using var root = TestRoot.Create();
        var paths = new AppPaths(root.RootDirectory);
        var runId = Guid.Parse("b6c1f2a3-7d8e-4f90-8a1b-2c3d4e5f6a7b");
        var request = CreateRequest(runId);
        var journal = new RecordingJournal();
        var mode = new WorkerMode(
            paths,
            new RecordingStore(request, paths.GetPendingRequestFilePath(runId)),
            journal,
            new FixedAdministratorProbe(true),
            new FixedResolver(),
            new FixedExecutor(CreateWorkflow(request, TerminalResult.Succeeded)),
            new FixedClock());

        await mode.RunAsync(["--worker", "--run-id", runId.ToString("D")], CancellationToken.None);

        foreach (var operationName in new[] { "WorkerRequestClaimed", "WorkerTargetResolved" })
        {
            var breadcrumb = Assert.Single(journal.Events, item => item.OperationName == operationName);
            Assert.Equal(RunEventLevel.Trace, breadcrumb.Level);
            Assert.Equal(RunPhase.Validation, breadcrumb.Phase);
            Assert.Equal(runId, breadcrumb.RunId);
            Assert.Contains($"distro={request.Profile.DistroName}", breadcrumb.Arguments);
            // Breadcrumbs carry no process output and never a stack trace.
            Assert.Null(breadcrumb.Output);
            Assert.Null(breadcrumb.TerminalResult);
        }
    }

    private static OperationRequest CreateRequest(Guid runId) =>
        new(
            runId,
            new Profile(
                Guid.Parse("7a0af7d9-3ad1-4a67-88c3-d08475a0ac6a"),
                "Ubuntu 24.04",
                "Ubuntu-24.04",
                @"D:\DevTools\WSL2\Ubuntu24.04\ext4.vhdx",
                ShutdownMode.Global,
                TimeSpan.FromSeconds(30)),
            OperationIntent.Compact);

    private static WorkflowResult CreateWorkflow(OperationRequest request, TerminalResult terminalResult) =>
        new(
            CreateSummary(request, terminalResult),
            new PreflightReport(
                Vela.Core.Validation.ValidationResult.Valid,
                null,
                null,
                null,
                null),
            ImmutableArray<WorkflowDiagnostic>.Empty,
            null);

    private static RunSummary CreateSummary(OperationRequest request, TerminalResult terminalResult)
    {
        var success = terminalResult is TerminalResult.Succeeded or TerminalResult.CompletedWithNoReclaim;
        var beforeLength = 10_000L;
        var afterLength = terminalResult == TerminalResult.CompletedWithNoReclaim ? beforeLength : 7_500L;
        return new(
            request.RunId,
            request.Profile,
            request.Intent,
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch,
            success ? CreateSnapshot(beforeLength) : null,
            success ? CreateSnapshot(afterLength) : null,
            terminalResult);
    }

    private static VhdxSnapshot CreateSnapshot(long fileLengthBytes) =>
        new(
            DateTimeOffset.UnixEpoch,
            @"D:\DevTools\WSL2\Ubuntu24.04\ext4.vhdx",
            fileLengthBytes,
            DateTimeOffset.UnixEpoch,
            null,
            new DriveSnapshot(@"D:\", 100_000L, 50_000L));

    private sealed class RecordingJournal : IRunJournal
    {
        public List<RunEvent> Events { get; } = new();
        public List<RunSummary> Summaries { get; } = new();
        public List<string> Operations { get; } = new();
        private long _sequence;

        public Task<JournalOperationResult> CreateRunAsync(Guid runId, CancellationToken cancellationToken)
        {
            Operations.Add("create");
            return Task.FromResult(JournalOperationResult.Success(null));
        }

        public Task<JournalOperationResult> OpenExistingRunAsync(Guid runId, CancellationToken cancellationToken)
        {
            Operations.Add("open");
            return Task.FromResult(JournalOperationResult.Success(null));
        }

        public Task<JournalAppendResult> AppendAsync(RunEventDraft eventDraft, CancellationToken cancellationToken)
        {
            Operations.Add($"append:{eventDraft.OperationName}");
            var @event = new RunEvent(
                ++_sequence,
                eventDraft.OccurredAtUtc,
                eventDraft.RunId,
                eventDraft.Phase,
                eventDraft.Level,
                eventDraft.OperationName,
                eventDraft.Arguments,
                eventDraft.ExitCode,
                eventDraft.Duration,
                eventDraft.Output,
                eventDraft.TerminalResult);
            Events.Add(@event);
            return Task.FromResult(JournalAppendResult.Success(@event));
        }

        public Task<JournalOperationResult> WriteSummaryAsync(RunSummary summary, CancellationToken cancellationToken)
        {
            Operations.Add("summary");
            Summaries.Add(summary);
            return Task.FromResult(JournalOperationResult.Success(null));
        }

        public Task<JournalReadResult> ReadEventsAsync(Guid runId, long afterSequence, CancellationToken cancellationToken) =>
            Task.FromResult(new JournalReadResult(Events.Where(item => item.RunId == runId && item.Sequence > afterSequence).ToImmutableArray()));
    }

    private sealed class RecordingStore : IOperationRequestStore
    {
        private readonly OperationRequest _request;
        private readonly string _sourcePath;

        private readonly bool _consumeSucceeds;

        public RecordingStore(
            OperationRequest request,
            string sourcePath,
            bool consumeSucceeds = true)
        {
            _request = request;
            _sourcePath = sourcePath;
            _consumeSucceeds = consumeSucceeds;
        }

        public List<Guid> ClaimedRunIds { get; } = new();

        public List<Guid> ConsumedRunIds { get; } = new();

        public Task<OperationRequestWriteResult> WriteAsync(OperationRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(OperationRequestWriteResult.Failure());

        public Task<OperationRequestReadResult> ReadAsync(Guid expectedRunId, CancellationToken cancellationToken) =>
            Task.FromResult(OperationRequestReadResult.Success(_request, _sourcePath));

        public Task<OperationRequestClaimResult> ClaimAsync(Guid expectedRunId, CancellationToken cancellationToken)
        {
            ClaimedRunIds.Add(expectedRunId);
            return Task.FromResult(OperationRequestClaimResult.Success(_request, _sourcePath));
        }

        public Task<OperationRequestConsumeResult> ConsumeAsync(Guid expectedRunId, CancellationToken cancellationToken)
        {
            ConsumedRunIds.Add(expectedRunId);
            return Task.FromResult(
                _consumeSucceeds
                    ? OperationRequestConsumeResult.Success()
                    : OperationRequestConsumeResult.Failure());
        }
    }

    private sealed class FixedAdministratorProbe : IAdministratorProbe
    {
        private readonly bool _value;
        public FixedAdministratorProbe(bool value) => _value = value;
        public bool IsAdministrator() => _value;
    }

    private sealed class RecordingAdministratorProbe : IAdministratorProbe
    {
        private readonly bool _value;
        public RecordingAdministratorProbe(bool value) => _value = value;
        public int CallCount { get; private set; }
        public bool IsAdministrator()
        {
            CallCount++;
            return _value;
        }
    }

    private sealed class ThrowingAdministratorProbe : IAdministratorProbe
    {
        public bool IsAdministrator() => throw new InvalidOperationException("probe failure");
    }

    private sealed class FixedResolver : ILxssProfileResolver
    {
        public Task<LxssProfileResolution> ResolveAsync(string distroName, string requestedVhdxPath, CancellationToken cancellationToken) =>
            Task.FromResult(new LxssProfileResolution(
                LxssResolutionStatus.Matched,
                distroName,
                requestedVhdxPath,
                requestedVhdxPath));
    }

    private sealed class FixedExecutor : IWorkerOperationExecutor
    {
        private readonly WorkflowResult _result;
        public FixedExecutor(WorkflowResult result) => _result = result;
        public Task<WorkflowResult> ExecuteAsync(OperationRequest request, CancellationToken cancellationToken) => Task.FromResult(_result);
    }

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow => DateTimeOffset.UnixEpoch;
        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class TestRoot : IDisposable
    {
        private TestRoot(string rootDirectory) => RootDirectory = rootDirectory;
        public string RootDirectory { get; }
        public static TestRoot Create()
        {
            var path = Path.Combine(FindRepositoryRoot(), "artifacts", "test-data", "worker-mode-hardening", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return new TestRoot(path);
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
        public void Dispose()
        {
            if (Directory.Exists(RootDirectory))
            {
                Directory.Delete(RootDirectory, recursive: true);
            }
        }
    }
}
