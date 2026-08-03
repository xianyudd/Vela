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
        Assert.Contains(journal.Events, item => item.OperationName == "WorkerAdministratorProbeFailed");
        Assert.Equal(runId, Assert.Single(journal.Summaries).RunId);
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
            new RunSummary(
                request.RunId,
                request.Profile,
                request.Intent,
                DateTimeOffset.UnixEpoch,
                DateTimeOffset.UnixEpoch,
                null,
                null,
                terminalResult),
            new PreflightReport(
                Vela.Core.Validation.ValidationResult.Valid,
                null,
                null,
                null,
                null),
            ImmutableArray<WorkflowDiagnostic>.Empty,
            null);

    private sealed class RecordingJournal : IRunJournal
    {
        public List<RunEvent> Events { get; } = new();
        public List<RunSummary> Summaries { get; } = new();
        private long _sequence;

        public Task<JournalOperationResult> CreateRunAsync(Guid runId, CancellationToken cancellationToken) =>
            Task.FromResult(JournalOperationResult.Success(null));

        public Task<JournalOperationResult> OpenExistingRunAsync(Guid runId, CancellationToken cancellationToken) =>
            Task.FromResult(JournalOperationResult.Success(null));

        public Task<JournalAppendResult> AppendAsync(RunEventDraft eventDraft, CancellationToken cancellationToken)
        {
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
                eventDraft.Output);
            Events.Add(@event);
            return Task.FromResult(JournalAppendResult.Success(@event));
        }

        public Task<JournalOperationResult> WriteSummaryAsync(RunSummary summary, CancellationToken cancellationToken)
        {
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

        public RecordingStore(OperationRequest request, string sourcePath)
        {
            _request = request;
            _sourcePath = sourcePath;
        }

        public List<Guid> ConsumedRunIds { get; } = new();

        public Task<OperationRequestWriteResult> WriteAsync(OperationRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(OperationRequestWriteResult.Failure());

        public Task<OperationRequestReadResult> ReadAsync(Guid expectedRunId, CancellationToken cancellationToken) =>
            Task.FromResult(OperationRequestReadResult.Success(_request, _sourcePath));

        public Task<OperationRequestClaimResult> ClaimAsync(Guid expectedRunId, CancellationToken cancellationToken) =>
            Task.FromResult(OperationRequestClaimResult.Success(_request, _sourcePath));

        public Task<OperationRequestConsumeResult> ConsumeAsync(Guid expectedRunId, CancellationToken cancellationToken)
        {
            ConsumedRunIds.Add(expectedRunId);
            return Task.FromResult(OperationRequestConsumeResult.Success());
        }
    }

    private sealed class FixedAdministratorProbe : IAdministratorProbe
    {
        private readonly bool _value;
        public FixedAdministratorProbe(bool value) => _value = value;
        public bool IsAdministrator() => _value;
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
