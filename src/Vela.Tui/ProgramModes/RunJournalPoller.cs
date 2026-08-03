using System.Collections.Immutable;
using Vela.Core.Contracts;
using Vela.Core.Models;

namespace Vela.Tui.ProgramModes;

public sealed record RunJournalPollResult(
    bool IsTerminal,
    ImmutableArray<RunEvent> Events,
    RunEvent? TerminalEvent,
    long LastSequence)
{
    public TerminalResult? TerminalResult => TerminalEvent is null
        ? null
        : RunJournalPoller.MapTerminalResult(TerminalEvent);

    public static RunJournalPollResult Empty(long afterSequence) =>
        new(false, ImmutableArray<RunEvent>.Empty, null, afterSequence);
}

public sealed class RunJournalPoller
{
    private static readonly ImmutableHashSet<string> TerminalOperationNames =
        ImmutableHashSet.Create(
            StringComparer.Ordinal,
            "WorkerCompleted",
            "WorkerFailed",
            "UacCancelled",
            "UacLaunchFailed");

    private readonly IRunJournal _journal;
    private readonly IClock _clock;
    private readonly TimeSpan _pollInterval;

    public RunJournalPoller(
        IRunJournal journal,
        IClock clock,
        TimeSpan? pollInterval = null)
    {
        ArgumentNullException.ThrowIfNull(journal);
        ArgumentNullException.ThrowIfNull(clock);

        var interval = pollInterval ?? TimeSpan.FromMilliseconds(100);
        if (interval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(pollInterval));
        }

        _journal = journal;
        _clock = clock;
        _pollInterval = interval;
    }

    public Task<RunJournalPollResult> PollAsync(
        Guid runId,
        CancellationToken cancellationToken) =>
        PollAsync(runId, afterSequence: 0, cancellationToken);

    public async Task<RunJournalPollResult> PollAsync(
        Guid runId,
        long afterSequence,
        CancellationToken cancellationToken)
    {
        if (runId == Guid.Empty)
        {
            return RunJournalPollResult.Empty(afterSequence);
        }

        var lastSequence = afterSequence;
        var observed = ImmutableArray.CreateBuilder<RunEvent>();

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            JournalReadResult read;
            try
            {
                read = await _journal
                    .ReadEventsAsync(runId, lastSequence, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                await _clock.DelayAsync(_pollInterval, cancellationToken).ConfigureAwait(false);
                continue;
            }

            foreach (var @event in read.Events
                         .Where(candidate =>
                             candidate.RunId == runId &&
                             candidate.Sequence > lastSequence)
                         .OrderBy(candidate => candidate.Sequence))
            {
                observed.Add(@event);
                lastSequence = Math.Max(lastSequence, @event.Sequence);

                if (IsTerminalEvent(@event))
                {
                    return new RunJournalPollResult(
                        true,
                        observed.ToImmutable(),
                        @event,
                        lastSequence);
                }
            }

            await _clock.DelayAsync(_pollInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    public Task<RunJournalPollResult> WaitForTerminalAsync(
        Guid runId,
        CancellationToken cancellationToken) =>
        PollAsync(runId, cancellationToken);

    public Task<RunJournalPollResult> WaitForTerminalAsync(
        Guid runId,
        long afterSequence,
        CancellationToken cancellationToken) =>
        PollAsync(runId, afterSequence, cancellationToken);

    public Task<RunJournalPollResult> ReadUntilTerminalAsync(
        Guid runId,
        long afterSequence,
        CancellationToken cancellationToken) =>
        PollAsync(runId, afterSequence, cancellationToken);

    public async Task<ImmutableArray<RunEvent>> ReadIncrementAsync(
        Guid runId,
        long afterSequence,
        CancellationToken cancellationToken)
    {
        if (runId == Guid.Empty)
        {
            return ImmutableArray<RunEvent>.Empty;
        }

        var read = await _journal
            .ReadEventsAsync(runId, afterSequence, cancellationToken)
            .ConfigureAwait(false);
        return read.Events
            .Where(candidate =>
                candidate.RunId == runId &&
                candidate.Sequence > afterSequence)
            .OrderBy(candidate => candidate.Sequence)
            .ToImmutableArray();
    }

    public static bool IsTerminalEvent(RunEvent @event)
    {
        ArgumentNullException.ThrowIfNull(@event);
        return TerminalOperationNames.Contains(@event.OperationName) ||
               (@event.Phase is RunPhase.Completed or RunPhase.Failed &&
                @event.ExitCode is not null);
    }

    public static TerminalResult MapTerminalResult(RunEvent @event)
    {
        ArgumentNullException.ThrowIfNull(@event);

        return @event.OperationName switch
        {
            "UacCancelled" => TerminalResult.CancelledBeforeElevation,
            "UacLaunchFailed" => TerminalResult.WorkerInterrupted,
            "WorkerCompleted" when @event.ExitCode == 0 => TerminalResult.Succeeded,
            "WorkerFailed" when @event.ExitCode == 2 => TerminalResult.ValidationFailed,
            "WorkerFailed" when @event.ExitCode == 3 => TerminalResult.ShutdownTimedOut,
            "WorkerFailed" when @event.ExitCode == 4 => TerminalResult.DiskPartPreflightFailed,
            "WorkerFailed" when @event.ExitCode == 5 => TerminalResult.DiskPartCompactFailed,
            "WorkerFailed" when @event.ExitCode == 10 => TerminalResult.WorkerInterrupted,
            "WorkerCompleted" => TerminalResult.Succeeded,
            _ when @event.Phase == RunPhase.Completed => TerminalResult.Succeeded,
            _ when @event.ExitCode == 2 => TerminalResult.ValidationFailed,
            _ => TerminalResult.WorkerInterrupted
        };
    }
}
