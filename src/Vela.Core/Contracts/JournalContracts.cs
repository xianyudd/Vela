using System.Collections.Immutable;
using Vela.Core.Models;

namespace Vela.Core.Contracts;

public sealed record RunEventDraft(
    DateTimeOffset OccurredAtUtc,
    Guid RunId,
    RunPhase Phase,
    RunEventLevel Level,
    string OperationName,
    ImmutableArray<string> Arguments,
    int? ExitCode,
    TimeSpan? Duration,
    string? Output,
    TerminalResult? TerminalResult = null);

public sealed record JournalOperationResult(
    bool Succeeded,
    string? RunDirectory)
{
    public static JournalOperationResult Success(string? runDirectory) => new(true, runDirectory);

    public static JournalOperationResult Failure() => new(false, null);
}

public sealed record JournalAppendResult(
    bool Succeeded,
    RunEvent? Event)
{
    public static JournalAppendResult Success(RunEvent @event) => new(true, @event);

    public static JournalAppendResult Failure() => new(false, null);
}

public sealed record JournalReadResult(
    ImmutableArray<RunEvent> Events,
    bool Succeeded = true,
    string? ErrorMessage = null)
{
    public static JournalReadResult Success(ImmutableArray<RunEvent> events) =>
        new(events, Succeeded: true);

    public static JournalReadResult Failure(string? errorMessage = null) =>
        new(ImmutableArray<RunEvent>.Empty, Succeeded: false, errorMessage);
}

public interface IRunJournal
{
    Task<JournalOperationResult> CreateRunAsync(Guid runId, CancellationToken cancellationToken);

    Task<JournalOperationResult> OpenExistingRunAsync(Guid runId, CancellationToken cancellationToken);

    Task<JournalAppendResult> AppendAsync(RunEventDraft eventDraft, CancellationToken cancellationToken);

    Task<JournalOperationResult> WriteSummaryAsync(RunSummary summary, CancellationToken cancellationToken);

    Task<JournalReadResult> ReadEventsAsync(
        Guid runId,
        long afterSequence,
        CancellationToken cancellationToken);
}
