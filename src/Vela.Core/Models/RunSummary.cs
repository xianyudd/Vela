namespace Vela.Core.Models;

public sealed record RunSummary(
    Guid RunId,
    Profile Profile,
    OperationIntent Intent,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset CompletedAtUtc,
    VhdxSnapshot? BeforeSnapshot,
    VhdxSnapshot? AfterSnapshot,
    TerminalResult TerminalResult)
{
    public long? ReclaimedBytes => BeforeSnapshot is null || AfterSnapshot is null
        ? null
        : BeforeSnapshot.FileLengthBytes - AfterSnapshot.FileLengthBytes;
}

public enum TerminalResult
{
    Succeeded,
    CompletedWithNoReclaim,
    ValidationFailed,
    ShutdownTimedOut,
    DiskPartPreflightFailed,
    DiskPartCompactFailed,
    WorkerInterrupted,
    CancelledBeforeElevation
}
