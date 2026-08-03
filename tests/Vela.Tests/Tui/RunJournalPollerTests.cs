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
                null));
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
    public async Task PollAsync_IgnoresEventsForAnotherRunId()
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
                null),
            new RunEvent(
                2,
                DateTimeOffset.UnixEpoch,
                runId,
                RunPhase.Elevation,
                RunEventLevel.Error,
                "UacCancelled",
                ImmutableArray<string>.Empty,
                null,
                null,
                null));
        var poller = new RunJournalPoller(journal, new ImmediateClock(), TimeSpan.FromMilliseconds(1));

        var result = await poller.WaitForTerminalAsync(runId, CancellationToken.None);

        Assert.Equal(TerminalResult.CancelledBeforeElevation, result.TerminalResult);
        Assert.Single(result.Events);
        Assert.Equal(runId, result.Events[0].RunId);
    }

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

    private sealed class ImmediateClock : IClock
    {
        public DateTimeOffset UtcNow => DateTimeOffset.UnixEpoch;

        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }
}
