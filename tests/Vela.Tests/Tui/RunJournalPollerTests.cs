using System.Collections.Immutable;
using Vela.Core.Contracts;
using Vela.Core.Models;
using Vela.Tui.ProgramModes;

namespace Vela.Tests.Tui;

public sealed class RunJournalPollerTests
{
    [Fact]
    public async Task PollAsync_UsesSequenceCursorAndStopsAtWorkerTerminalEvent()
    {
        var runId = Guid.Parse("e4a6a96e-749f-46e2-9ac2-b5ca365f8145");
        var journal = new IncrementalJournal(
            runId,
            new RunEvent(
                1,
                DateTimeOffset.UnixEpoch,
                runId,
                RunPhase.Validation,
                RunEventLevel.Information,
                "RunCreated",
                ImmutableArray<string>.Empty,
                null,
                null,
                null),
            new RunEvent(
                2,
                DateTimeOffset.UnixEpoch,
                runId,
                RunPhase.Completed,
                RunEventLevel.Information,
                "WorkerCompleted",
                ImmutableArray<string>.Empty,
                0,
                TimeSpan.Zero,
                null,
                TerminalResult.Succeeded));
        var clock = new ImmediateClock();
        var poller = new RunJournalPoller(journal, clock, TimeSpan.FromMilliseconds(1));

        var result = await poller.PollAsync(runId, afterSequence: 0, CancellationToken.None);

        Assert.True(result.IsTerminal);
        Assert.Equal(2, result.LastSequence);
        Assert.Equal(TerminalResult.Succeeded, result.TerminalResult);
        Assert.Equal(new[] { "RunCreated", "WorkerCompleted" }, result.Events.Select(static item => item.OperationName));
        Assert.Equal(new long[] { 0, 1 }, journal.AfterSequences);
    }

    [Fact]
    public async Task PollAsync_RejectsEventsForAnotherRunId()
    {
        var runId = Guid.Parse("9ed8e0fb-1f50-44f8-9b6e-f171b4b43d99");
        var otherRunId = Guid.Parse("6ea0a499-fcab-4fa9-8e00-46fe3d470e0d");
        var journal = new IncrementalJournal(
            runId,
            new RunEvent(
                1,
                DateTimeOffset.UnixEpoch,
                otherRunId,
                RunPhase.Completed,
                RunEventLevel.Information,
                "WorkerCompleted",
                ImmutableArray<string>.Empty,
                0,
                TimeSpan.Zero,
                null,
                TerminalResult.Succeeded));
        var poller = new RunJournalPoller(journal, new ImmediateClock(), TimeSpan.FromMilliseconds(1));

        var result = await poller.WaitForTerminalAsync(runId, CancellationToken.None);

        Assert.Equal(RunJournalPollStatus.ReadFailed, result.Status);
        Assert.False(result.IsTerminal);
        Assert.Null(result.TerminalResult);
        Assert.Empty(result.Events);
        Assert.Equal(0, result.LastSequence);
    }

    [Fact]
    public async Task PollAsync_PreCancelled_ReturnsCancelledWithoutFabricatingTerminalResult()
    {
        var runId = Guid.NewGuid();
        var journal = new BatchJournal(JournalReadResult.Success(ImmutableArray<RunEvent>.Empty));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var poller = new RunJournalPoller(
            journal,
            new ImmediateClock(),
            new RunJournalPollOptions { Timeout = null });

        var result = await poller.WaitForTerminalAsync(runId, cancellation.Token);

        Assert.Equal(RunJournalPollStatus.Cancelled, result.Status);
        Assert.False(result.IsTerminal);
        Assert.Null(result.TerminalResult);
        Assert.Null(result.TerminalEvent);
        Assert.Empty(result.Events);
        Assert.Equal(0, journal.ReadCount);
    }

    [Fact]
    public async Task PollAsync_Timeout_ReturnsTimedOutWithoutFabricatingTerminalResult()
    {
        var runId = Guid.NewGuid();
        var observedEvent = CreateEvent(runId, 1, "RunCreated");
        var journal = new BatchJournal(
            JournalReadResult.Success(ImmutableArray.Create(observedEvent)),
            JournalReadResult.Success(ImmutableArray<RunEvent>.Empty));
        var poller = new RunJournalPoller(
            journal,
            new AdvancingClock(),
            new RunJournalPollOptions
            {
                PollInterval = TimeSpan.FromMilliseconds(1),
                Timeout = TimeSpan.FromMilliseconds(2)
            });

        var result = await poller.WaitForTerminalAsync(runId, CancellationToken.None);

        Assert.Equal(RunJournalPollStatus.TimedOut, result.Status);
        Assert.False(result.IsTerminal);
        Assert.Null(result.TerminalResult);
        Assert.Null(result.TerminalEvent);
        Assert.Equal(2, journal.ReadCount);
        Assert.Equal(1, result.LastSequence);
        Assert.Equal(observedEvent, Assert.Single(result.Events));
    }

    [Fact]
    public async Task PollAsync_StopsAtConsecutiveReadFailureThresholdAndSanitizesError()
    {
        var runId = Guid.NewGuid();
        var journal = new BatchJournal(
            JournalReadResult.Failure("[31m读取失败[0m"));
        var poller = new RunJournalPoller(
            journal,
            new ImmediateClock(),
            new RunJournalPollOptions
            {
                PollInterval = TimeSpan.FromMilliseconds(1),
                MaxConsecutiveReadFailures = 3,
                Timeout = null
            });

        var result = await poller.WaitForTerminalAsync(runId, CancellationToken.None);

        Assert.Equal(RunJournalPollStatus.ReadFailed, result.Status);
        Assert.Equal(3, result.ConsecutiveReadFailures);
        Assert.Equal(3, journal.ReadCount);
        var statusMessage = Assert.IsType<string>(result.StatusMessage);
        Assert.DoesNotContain('', statusMessage);
        Assert.Contains("读取失败", statusMessage, StringComparison.Ordinal);
        Assert.Null(result.TerminalResult);
    }

    [Fact]
    public async Task PollAsync_SuccessfulReadResetsConsecutiveFailureCount()
    {
        var runId = Guid.NewGuid();
        var journal = new BatchJournal(
            JournalReadResult.Failure("第一次读取失败"),
            JournalReadResult.Success(ImmutableArray.Create(
                CreateEvent(runId, 1, "RunCreated"))),
            JournalReadResult.Failure("第二次读取失败"),
            JournalReadResult.Failure("第三次读取失败"));
        var poller = new RunJournalPoller(
            journal,
            new ImmediateClock(),
            new RunJournalPollOptions
            {
                PollInterval = TimeSpan.FromMilliseconds(1),
                MaxConsecutiveReadFailures = 2,
                Timeout = null
            });

        var result = await poller.WaitForTerminalAsync(runId, CancellationToken.None);

        Assert.Equal(RunJournalPollStatus.ReadFailed, result.Status);
        Assert.Equal(2, result.ConsecutiveReadFailures);
        Assert.Equal(4, journal.ReadCount);
        Assert.Equal(1, result.LastSequence);
        Assert.Equal("RunCreated", Assert.Single(result.Events).OperationName);
    }

    [Theory]
    [InlineData(2, 0)]
    [InlineData(1, 1)]
    public async Task PollAsync_RejectsSequenceGapsAndDuplicates(
        long firstSequence,
        long expectedLastSequence)
    {
        var runId = Guid.NewGuid();
        var events = firstSequence == 1
            ? ImmutableArray.Create(
                CreateEvent(runId, 1, "First"),
                CreateEvent(runId, 1, "Duplicate"))
            : ImmutableArray.Create(CreateEvent(runId, firstSequence, "Gap"));
        var poller = new RunJournalPoller(
            new BatchJournal(JournalReadResult.Success(events)),
            new ImmediateClock(),
            new RunJournalPollOptions { Timeout = null });

        var result = await poller.WaitForTerminalAsync(runId, CancellationToken.None);

        Assert.Equal(RunJournalPollStatus.ReadFailed, result.Status);
        Assert.Equal(expectedLastSequence, result.LastSequence);
        Assert.Null(result.TerminalResult);
    }

    [Fact]
    public async Task PollAsync_RejectsNonMonotonicBatchInItsOriginalOrder()
    {
        var runId = Guid.NewGuid();
        var journal = new BatchJournal(JournalReadResult.Success(
            ImmutableArray.Create(
                CreateEvent(runId, 2, "Second"),
                CreateEvent(runId, 1, "First"))));
        var poller = new RunJournalPoller(
            journal,
            new ImmediateClock(),
            new RunJournalPollOptions { Timeout = null });

        var result = await poller.WaitForTerminalAsync(runId, CancellationToken.None);

        Assert.Equal(RunJournalPollStatus.ReadFailed, result.Status);
        Assert.Empty(result.Events);
        Assert.Equal(0, result.LastSequence);
        Assert.Null(result.TerminalResult);
    }

    [Fact]
    public async Task ReadIncrementAsync_RejectsNonMonotonicBatchInItsOriginalOrder()
    {
        var runId = Guid.NewGuid();
        var journal = new BatchJournal(JournalReadResult.Success(
            ImmutableArray.Create(
                CreateEvent(runId, 2, "Second"),
                CreateEvent(runId, 1, "First"))));
        var poller = new RunJournalPoller(
            journal,
            new ImmediateClock(),
            new RunJournalPollOptions { Timeout = null });

        await Assert.ThrowsAsync<InvalidDataException>(
            () => poller.ReadIncrementAsync(runId, 0, CancellationToken.None));
    }

    [Fact]
    public async Task PollAsync_RejectsTerminalOperationWithoutCanonicalResult()
    {
        var runId = Guid.NewGuid();
        var terminal = CreateEvent(
            runId,
            1,
            "WorkerCompleted",
            RunPhase.Completed,
            terminalResult: null);
        var poller = new RunJournalPoller(
            new BatchJournal(JournalReadResult.Success(ImmutableArray.Create(terminal))),
            new ImmediateClock(),
            new RunJournalPollOptions { Timeout = null });

        var result = await poller.WaitForTerminalAsync(runId, CancellationToken.None);

        Assert.Equal(RunJournalPollStatus.ReadFailed, result.Status);
        Assert.Empty(result.Events);
        Assert.Equal(0, result.LastSequence);
        Assert.Null(result.TerminalResult);
    }

    [Fact]
    public async Task PollAsync_CallbackObservesEachEventExactlyOnceInOrder()
    {
        var runId = Guid.NewGuid();
        var events = ImmutableArray.Create(
            CreateEvent(runId, 1, "RunCreated"),
            CreateEvent(runId, 2, "Inspecting"),
            CreateEvent(
                runId,
                3,
                "WorkerCompleted",
                RunPhase.Completed,
                TerminalResult.CompletedWithNoReclaim));
        var observed = new List<string>();
        var poller = new RunJournalPoller(
            new BatchJournal(JournalReadResult.Success(events)),
            new ImmediateClock(),
            new RunJournalPollOptions { Timeout = null });

        var result = await poller.WaitForTerminalAsync(
            runId,
            afterSequence: 0,
            CancellationToken.None,
            @event =>
            {
                observed.Add(@event.OperationName);
                return Task.CompletedTask;
            });

        Assert.Equal(RunJournalPollStatus.Terminal, result.Status);
        Assert.Equal(
            new[] { "RunCreated", "Inspecting", "WorkerCompleted" },
            observed);
        Assert.Equal(observed, result.Events.Select(@event => @event.OperationName));
        Assert.Equal(TerminalResult.CompletedWithNoReclaim, result.TerminalResult);
    }

    [Fact]
    public async Task PollAsync_CallbackFailureStopsBeforeLaterEventsAndDoesNotFabricateTerminal()
    {
        var runId = Guid.NewGuid();
        var events = ImmutableArray.Create(
            CreateEvent(runId, 1, "RunCreated"),
            CreateEvent(runId, 2, "Inspecting"),
            CreateEvent(
                runId,
                3,
                "WorkerCompleted",
                RunPhase.Completed,
                TerminalResult.Succeeded));
        var callbacks = 0;
        var poller = new RunJournalPoller(
            new BatchJournal(JournalReadResult.Success(events)),
            new ImmediateClock(),
            new RunJournalPollOptions { Timeout = null });

        var result = await poller.WaitForTerminalAsync(
            runId,
            afterSequence: 0,
            CancellationToken.None,
            _ => ++callbacks == 2
                ? Task.FromException(new InvalidOperationException("display secret"))
                : Task.CompletedTask);

        Assert.Equal(RunJournalPollStatus.ReadFailed, result.Status);
        Assert.Equal(2, callbacks);
        Assert.Equal(2, result.LastSequence);
        Assert.Equal(2, result.Events.Length);
        Assert.Null(result.TerminalEvent);
        Assert.Null(result.TerminalResult);
        Assert.DoesNotContain("display secret", result.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PollAsync_CallbackCancellationReturnsCancelledWithConsumedCursor()
    {
        var runId = Guid.NewGuid();
        var first = CreateEvent(runId, 1, "RunCreated");
        var terminal = CreateEvent(
            runId,
            2,
            "WorkerCompleted",
            RunPhase.Completed,
            TerminalResult.Succeeded);
        using var cancellation = new CancellationTokenSource();
        var callbacks = 0;
        var poller = new RunJournalPoller(
            new BatchJournal(JournalReadResult.Success(
                ImmutableArray.Create(first, terminal))),
            new ImmediateClock(),
            new RunJournalPollOptions { Timeout = null });

        var result = await poller.WaitForTerminalAsync(
            runId,
            afterSequence: 0,
            cancellation.Token,
            _ =>
            {
                callbacks++;
                cancellation.Cancel();
                return Task.FromCanceled(cancellation.Token);
            });

        Assert.Equal(RunJournalPollStatus.Cancelled, result.Status);
        Assert.Equal(1, callbacks);
        Assert.Equal(1, result.LastSequence);
        Assert.Equal(first, Assert.Single(result.Events));
        Assert.Null(result.TerminalEvent);
        Assert.Null(result.TerminalResult);
    }

    private static RunEvent CreateEvent(
        Guid runId,
        long sequence,
        string operationName,
        RunPhase phase = RunPhase.Validation,
        TerminalResult? terminalResult = null) =>
        new(
            sequence,
            DateTimeOffset.UnixEpoch,
            runId,
            phase,
            operationName == "WorkerCompleted"
                ? RunEventLevel.Information
                : terminalResult is null
                    ? RunEventLevel.Information
                    : RunEventLevel.Error,
            operationName,
            ImmutableArray<string>.Empty,
            ExitCode: operationName == "WorkerCompleted"
                ? terminalResult is null ? null : 0
                : terminalResult is { } result
                    ? TerminalResultSemantics.ToExitCode(result)
                    : null,
            Duration: null,
            Output: null,
            terminalResult);

    private sealed class IncrementalJournal : IRunJournal
    {
        private readonly Guid _runId;
        private readonly ImmutableArray<RunEvent> _events;
        private int _readCount;

        public IncrementalJournal(Guid runId, params RunEvent[] events)
        {
            _runId = runId;
            _events = events.ToImmutableArray();
        }

        public List<long> AfterSequences { get; } = new();

        public Task<JournalOperationResult> CreateRunAsync(Guid runId, CancellationToken cancellationToken) =>
            Task.FromResult(JournalOperationResult.Failure());

        public Task<JournalOperationResult> OpenExistingRunAsync(Guid runId, CancellationToken cancellationToken) =>
            Task.FromResult(JournalOperationResult.Success(null));

        public Task<JournalAppendResult> AppendAsync(RunEventDraft eventDraft, CancellationToken cancellationToken) =>
            Task.FromResult(JournalAppendResult.Failure());

        public Task<JournalOperationResult> WriteSummaryAsync(RunSummary summary, CancellationToken cancellationToken) =>
            Task.FromResult(JournalOperationResult.Failure());

        public Task<JournalReadResult> ReadEventsAsync(
            Guid runId,
            long afterSequence,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(_runId, runId);
            AfterSequences.Add(afterSequence);
            var index = Math.Min(_readCount++, _events.Length - 1);
            var events = _events.IsDefaultOrEmpty
                ? ImmutableArray<RunEvent>.Empty
                : ImmutableArray.Create(_events[index]);
            return Task.FromResult(new JournalReadResult(events));
        }
    }

    private sealed class BatchJournal : IRunJournal
    {
        private readonly Queue<JournalReadResult> _reads;
        private JournalReadResult _lastRead;

        public BatchJournal(params JournalReadResult[] reads)
        {
            if (reads.Length == 0)
            {
                throw new ArgumentException("At least one read is required.", nameof(reads));
            }

            _reads = new Queue<JournalReadResult>(reads);
            _lastRead = reads[^1];
        }

        public int ReadCount { get; private set; }

        public Task<JournalOperationResult> CreateRunAsync(
            Guid runId,
            CancellationToken cancellationToken) =>
            Task.FromResult(JournalOperationResult.Failure());

        public Task<JournalOperationResult> OpenExistingRunAsync(
            Guid runId,
            CancellationToken cancellationToken) =>
            Task.FromResult(JournalOperationResult.Success(null));

        public Task<JournalAppendResult> AppendAsync(
            RunEventDraft eventDraft,
            CancellationToken cancellationToken) =>
            Task.FromResult(JournalAppendResult.Failure());

        public Task<JournalOperationResult> WriteSummaryAsync(
            RunSummary summary,
            CancellationToken cancellationToken) =>
            Task.FromResult(JournalOperationResult.Failure());

        public Task<JournalReadResult> ReadEventsAsync(
            Guid runId,
            long afterSequence,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReadCount++;
            if (_reads.Count > 0)
            {
                _lastRead = _reads.Dequeue();
            }

            return Task.FromResult(_lastRead);
        }
    }

    private sealed class ImmediateClock : IClock
    {
        public DateTimeOffset UtcNow => DateTimeOffset.UnixEpoch;

        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }

    private sealed class AdvancingClock : IClock
    {
        private DateTimeOffset _now = DateTimeOffset.UnixEpoch;

        public DateTimeOffset UtcNow => _now;

        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _now += delay;
            return Task.CompletedTask;
        }
    }
}
