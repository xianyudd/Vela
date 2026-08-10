using System.Collections.Immutable;
using Vela.Core.Contracts;
using Vela.Core.Models;
using Vela.Tui.Application;

namespace Vela.Tui.ProgramModes;

public enum RunJournalPollStatus
{
    Polling,
    Terminal,
    Cancelled,
    TimedOut,
    ReadFailed
}

public sealed record RunJournalPollOptions
{
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMilliseconds(100);

    public int MaxConsecutiveReadFailures { get; init; } = 3;

    public TimeSpan? Timeout { get; init; } = TimeSpan.FromMinutes(5);

    internal void Validate()
    {
        if (PollInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(PollInterval));
        }

        if (MaxConsecutiveReadFailures < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxConsecutiveReadFailures));
        }

        if (Timeout is { } timeout && timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(Timeout));
        }
    }
}

public sealed record RunJournalPollResult(
    bool IsTerminal,
    ImmutableArray<RunEvent> Events,
    RunEvent? TerminalEvent,
    long LastSequence)
{
    public RunJournalPollStatus Status { get; init; } = IsTerminal
        ? RunJournalPollStatus.Terminal
        : RunJournalPollStatus.Polling;

    public string? StatusMessage { get; init; }

    public int ConsecutiveReadFailures { get; init; }

    public TerminalResult? TerminalResult => TerminalEvent is null
        ? null
        : TerminalResultSemantics.TryMapTerminalEvent(TerminalEvent, out var result)
            ? result
            : null;

    public static RunJournalPollResult Empty(long afterSequence) =>
        new(false, ImmutableArray<RunEvent>.Empty, null, afterSequence)
        {
            Status = RunJournalPollStatus.Polling
        };
}

public sealed class RunJournalPoller
{
    private readonly IRunJournal _journal;
    private readonly IClock _clock;
    private readonly RunJournalPollOptions _options;

    public RunJournalPoller(
        IRunJournal journal,
        IClock clock,
        TimeSpan? pollInterval = null)
        : this(
            journal,
            clock,
            new RunJournalPollOptions
            {
                PollInterval = pollInterval ?? TimeSpan.FromMilliseconds(100)
            })
    {
    }

    public RunJournalPoller(
        IRunJournal journal,
        IClock clock,
        RunJournalPollOptions options)
    {
        ArgumentNullException.ThrowIfNull(journal);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        _journal = journal;
        _clock = clock;
        _options = options;
    }

    public Task<RunJournalPollResult> PollAsync(
        Guid runId,
        CancellationToken cancellationToken) =>
        PollAsync(runId, afterSequence: 0, cancellationToken);

    public Task<RunJournalPollResult> PollAsync(
        Guid runId,
        CancellationToken cancellationToken,
        Func<RunEvent, Task>? onEventAsync) =>
        PollAsync(runId, afterSequence: 0, cancellationToken, onEventAsync);

    public Task<RunJournalPollResult> PollAsync(
        Guid runId,
        long afterSequence,
        CancellationToken cancellationToken) =>
        PollAsync(runId, afterSequence, cancellationToken, onEventAsync: null);

    public Task<RunJournalPollResult> PollAsync(
        Guid runId,
        long afterSequence,
        CancellationToken cancellationToken,
        Func<RunEvent, Task>? onEventAsync) =>
        PollCoreAsync(runId, afterSequence, cancellationToken, onEventAsync);

    private async Task<RunJournalPollResult> PollCoreAsync(
        Guid runId,
        long afterSequence,
        CancellationToken cancellationToken,
        Func<RunEvent, Task>? onEventAsync)
    {
        if (runId == Guid.Empty)
        {
            return RunJournalPollResult.Empty(afterSequence);
        }

        var lastSequence = afterSequence;
        var observed = ImmutableArray.CreateBuilder<RunEvent>();
        var consecutiveReadFailures = 0;
        var startedAt = _clock.UtcNow;

        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (HasTimedOut(startedAt))
                {
                    return CreateResult(
                        RunJournalPollStatus.TimedOut,
                        observed,
                        lastSequence,
                        consecutiveReadFailures,
                        "等待 worker journal 终态超时。worker 可能仍在运行。");
                }

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
                    read = JournalReadResult.Failure("读取 worker journal 失败。");
                }

                if (!read.Succeeded)
                {
                    consecutiveReadFailures++;
                    if (consecutiveReadFailures >= _options.MaxConsecutiveReadFailures)
                    {
                        var detail = string.IsNullOrWhiteSpace(read.ErrorMessage)
                            ? "未知读取错误"
                            : TuiDisplayText.Sanitize(read.ErrorMessage, 160);
                        return CreateResult(
                            RunJournalPollStatus.ReadFailed,
                            observed,
                            lastSequence,
                            consecutiveReadFailures,
                            $"{TuiDisplayText.LabelForPollStatus(RunJournalPollStatus.ReadFailed)}：{detail}");
                    }

                    await DelayAsync(startedAt, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                consecutiveReadFailures = 0;
                var expectedSequence = lastSequence + 1;
                foreach (var @event in read.Events)
                {
                    if (@event.RunId != runId || @event.Sequence != expectedSequence)
                    {
                        return CreateResult(
                            RunJournalPollStatus.ReadFailed,
                            observed,
                            lastSequence,
                            consecutiveReadFailures,
                            "worker journal 序列无效，未继续消费事件。");
                    }

                    if (TerminalResultSemantics.IsTerminalOperation(@event.OperationName) &&
                        !TerminalResultSemantics.TryMapTerminalEvent(@event, out _))
                    {
                        return CreateResult(
                            RunJournalPollStatus.ReadFailed,
                            observed,
                            lastSequence,
                            consecutiveReadFailures,
                            "worker journal 终态事件无效，未继续消费事件。");
                    }

                    observed.Add(@event);
                    lastSequence = @event.Sequence;
                    expectedSequence++;

                    if (IsTerminalEvent(@event))
                    {
                        if (onEventAsync is not null)
                        {
                            try
                            {
                                await onEventAsync(@event).ConfigureAwait(false);
                            }
                            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                            {
                                throw;
                            }
                            catch (Exception)
                            {
                                return CreateResult(
                                    RunJournalPollStatus.ReadFailed,
                                    observed,
                                    lastSequence,
                                    consecutiveReadFailures,
                                    "worker journal 事件显示失败。");
                            }
                        }

                        return CreateResult(
                            RunJournalPollStatus.Terminal,
                            observed,
                            lastSequence,
                            consecutiveReadFailures,
                            null,
                            @event);
                    }

                    if (onEventAsync is not null)
                    {
                        try
                        {
                            await onEventAsync(@event).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                        {
                            throw;
                        }
                        catch (Exception)
                        {
                            return CreateResult(
                                RunJournalPollStatus.ReadFailed,
                                observed,
                                lastSequence,
                                consecutiveReadFailures,
                                "worker journal 事件显示失败。");
                        }
                    }
                }

                await DelayAsync(startedAt, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return CreateResult(
                RunJournalPollStatus.Cancelled,
                observed,
                lastSequence,
                consecutiveReadFailures,
                "等待 worker journal 被取消。worker 可能仍在运行，未伪造 worker 终态。");
        }
    }

    private async Task DelayAsync(DateTimeOffset startedAt, CancellationToken cancellationToken)
    {
        if (HasTimedOut(startedAt))
        {
            return;
        }

        await _clock.DelayAsync(_options.PollInterval, cancellationToken).ConfigureAwait(false);
    }

    private bool HasTimedOut(DateTimeOffset startedAt) =>
        _options.Timeout is { } timeout && _clock.UtcNow - startedAt >= timeout;

    private static RunJournalPollResult CreateResult(
        RunJournalPollStatus status,
        ImmutableArray<RunEvent>.Builder observed,
        long lastSequence,
        int consecutiveReadFailures,
        string? statusMessage,
        RunEvent? terminalEvent = null) =>
        new(
            status == RunJournalPollStatus.Terminal,
            observed.ToImmutable(),
            terminalEvent,
            lastSequence)
        {
            Status = status,
            StatusMessage = statusMessage,
            ConsecutiveReadFailures = consecutiveReadFailures
        };

    public Task<RunJournalPollResult> WaitForTerminalAsync(
        Guid runId,
        CancellationToken cancellationToken) =>
        PollAsync(runId, cancellationToken);

    public Task<RunJournalPollResult> WaitForTerminalAsync(
        Guid runId,
        long afterSequence,
        CancellationToken cancellationToken) =>
        PollAsync(runId, afterSequence, cancellationToken);

    public Task<RunJournalPollResult> WaitForTerminalAsync(
        Guid runId,
        long afterSequence,
        CancellationToken cancellationToken,
        Func<RunEvent, Task>? onEventAsync) =>
        PollAsync(runId, afterSequence, cancellationToken, onEventAsync);

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
        if (!read.Succeeded)
        {
            throw new IOException(
                TuiDisplayText.Sanitize(
                    read.ErrorMessage ?? "读取 worker journal 失败。",
                    160));
        }

        var expectedSequence = afterSequence + 1;
        foreach (var @event in read.Events)
        {
            if (@event.RunId != runId || @event.Sequence != expectedSequence)
            {
                throw new InvalidDataException("worker journal 序列无效。");
            }

            if (TerminalResultSemantics.IsTerminalOperation(@event.OperationName) &&
                !TerminalResultSemantics.TryMapTerminalEvent(@event, out _))
            {
                throw new InvalidDataException("worker journal 终态事件无效。");
            }

            expectedSequence++;
        }

        return read.Events;
    }

    public static bool IsTerminalEvent(RunEvent @event)
    {
        ArgumentNullException.ThrowIfNull(@event);
        return TerminalResultSemantics.TryMapTerminalEvent(@event, out _);
    }

    public static TerminalResult MapTerminalResult(RunEvent @event)
    {
        ArgumentNullException.ThrowIfNull(@event);
        return TerminalResultSemantics.TryMapTerminalEvent(@event, out var result)
            ? result
            : TerminalResult.WorkerInterrupted;
    }
}
