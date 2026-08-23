using System.Collections.Immutable;
using Vela.Core.Models;

namespace Vela.Tests.Core;

public sealed class TerminalResultSemanticsTests
{
    [Theory]
    [InlineData(TerminalResult.Succeeded, true)]
    [InlineData(TerminalResult.CompletedWithNoReclaim, true)]
    [InlineData(TerminalResult.ValidationFailed, false)]
    [InlineData(TerminalResult.ShutdownTimedOut, false)]
    [InlineData(TerminalResult.DiskPartPreflightFailed, false)]
    [InlineData(TerminalResult.DiskPartCompactFailed, false)]
    [InlineData(TerminalResult.WorkerInterrupted, false)]
    [InlineData(TerminalResult.CancelledBeforeElevation, false)]
    public void IsSuccessful_ClassifiesEveryDefinedResult(TerminalResult result, bool expected) =>
        Assert.Equal(expected, TerminalResultSemantics.IsSuccessful(result));

    [Fact]
    public void IsSuccessful_WithAnUndefinedValue_FailsClosed() =>
        Assert.False(TerminalResultSemantics.IsSuccessful((TerminalResult)999));

    [Theory]
    [InlineData(TerminalResult.Succeeded, 0)]
    [InlineData(TerminalResult.CompletedWithNoReclaim, 0)]
    [InlineData(TerminalResult.ValidationFailed, 2)]
    [InlineData(TerminalResult.ShutdownTimedOut, 3)]
    [InlineData(TerminalResult.DiskPartPreflightFailed, 4)]
    [InlineData(TerminalResult.DiskPartCompactFailed, 5)]
    [InlineData(TerminalResult.WorkerInterrupted, 10)]
    [InlineData(TerminalResult.CancelledBeforeElevation, 10)]
    public void ToExitCode_MapsEveryDefinedResult(TerminalResult result, int expected) =>
        Assert.Equal(expected, TerminalResultSemantics.ToExitCode(result));

    [Fact]
    public void ToExitCode_WithAnUndefinedValue_FallsBackToTheInterruptedCode() =>
        Assert.Equal(10, TerminalResultSemantics.ToExitCode((TerminalResult)999));

    // A failure that reports exit code 0 would be read as success by every
    // caller of the worker, so the exit code and the success predicate must
    // never disagree. Adding a result without an exit-code arm keeps this
    // green (the fallback is non-zero); adding a successful result without
    // teaching IsSuccessful about it breaks here instead of in production.
    [Fact]
    public void ToExitCode_ReportsZeroOnlyForResultsThatAreSuccessful()
    {
        foreach (var result in Enum.GetValues<TerminalResult>())
        {
            Assert.Equal(
                TerminalResultSemantics.IsSuccessful(result),
                TerminalResultSemantics.ToExitCode(result) == 0);
        }
    }

    [Fact]
    public void NormalizeSummaryResult_WithoutASummary_Throws() =>
        Assert.Throws<ArgumentNullException>(
            () => TerminalResultSemantics.NormalizeSummaryResult(null!));

    // Reclaimed bytes must never promote a failed run. diskpart can shrink the
    // file and still fail afterwards, so the recorded failure wins.
    [Theory]
    [InlineData(TerminalResult.ValidationFailed)]
    [InlineData(TerminalResult.ShutdownTimedOut)]
    [InlineData(TerminalResult.DiskPartPreflightFailed)]
    [InlineData(TerminalResult.DiskPartCompactFailed)]
    [InlineData(TerminalResult.WorkerInterrupted)]
    [InlineData(TerminalResult.CancelledBeforeElevation)]
    public void NormalizeSummaryResult_WhenTheRunFailed_KeepsTheFailure(TerminalResult failure)
    {
        var summary = CreateSummary(failure, beforeBytes: 4_096, afterBytes: 1_024);

        Assert.Equal(failure, TerminalResultSemantics.NormalizeSummaryResult(summary));
    }

    [Theory]
    [InlineData(TerminalResult.Succeeded)]
    [InlineData(TerminalResult.CompletedWithNoReclaim)]
    public void NormalizeSummaryResult_WithoutBothSnapshots_KeepsTheRecordedResult(
        TerminalResult recorded)
    {
        Assert.Equal(
            recorded,
            TerminalResultSemantics.NormalizeSummaryResult(
                CreateSummary(recorded, beforeBytes: 4_096, afterBytes: null)));
        Assert.Equal(
            recorded,
            TerminalResultSemantics.NormalizeSummaryResult(
                CreateSummary(recorded, beforeBytes: null, afterBytes: 1_024)));
        Assert.Equal(
            recorded,
            TerminalResultSemantics.NormalizeSummaryResult(
                CreateSummary(recorded, beforeBytes: null, afterBytes: null)));
    }

    [Fact]
    public void NormalizeSummaryResult_WhenNothingWasReclaimed_DowngradesToCompletedWithNoReclaim() =>
        Assert.Equal(
            TerminalResult.CompletedWithNoReclaim,
            TerminalResultSemantics.NormalizeSummaryResult(
                CreateSummary(TerminalResult.Succeeded, beforeBytes: 4_096, afterBytes: 4_096)));

    // RunSummary clamps a grown file to zero reclaimed bytes, so a compaction
    // that made the vhdx larger must read as "no reclaim", not as a success.
    [Fact]
    public void NormalizeSummaryResult_WhenTheVhdxGrew_ReportsNoReclaim() =>
        Assert.Equal(
            TerminalResult.CompletedWithNoReclaim,
            TerminalResultSemantics.NormalizeSummaryResult(
                CreateSummary(TerminalResult.Succeeded, beforeBytes: 1_024, afterBytes: 4_096)));

    [Theory]
    [InlineData(TerminalResult.Succeeded)]
    [InlineData(TerminalResult.CompletedWithNoReclaim)]
    public void NormalizeSummaryResult_WhenBytesWereReclaimed_ReportsSucceeded(
        TerminalResult recorded) =>
        Assert.Equal(
            TerminalResult.Succeeded,
            TerminalResultSemantics.NormalizeSummaryResult(
                CreateSummary(recorded, beforeBytes: 4_096, afterBytes: 1_024)));

    [Theory]
    [InlineData("UacCancelled")]
    [InlineData("UacLaunchFailed")]
    [InlineData("WorkerCompleted")]
    [InlineData("WorkerFailed")]
    public void IsTerminalOperation_RecognisesEveryTerminalOperation(string operationName) =>
        Assert.True(TerminalResultSemantics.IsTerminalOperation(operationName));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("WorkerStarted")]
    [InlineData("DiskPartCompactClassified")]
    [InlineData("workercompleted")]
    [InlineData("WorkerCompleted ")]
    public void IsTerminalOperation_RejectsEverythingElse(string? operationName) =>
        Assert.False(TerminalResultSemantics.IsTerminalOperation(operationName));

    [Fact]
    public void TryMapTerminalEvent_WithoutAnEvent_Throws() =>
        Assert.Throws<ArgumentNullException>(
            () => TerminalResultSemantics.TryMapTerminalEvent(null!, out _));

    [Theory]
    [InlineData("WorkerStarted")]
    [InlineData("workerfailed")]
    [InlineData("")]
    public void TryMapTerminalEvent_ForANonTerminalOperation_ReturnsFalse(string operationName) =>
        Assert.False(TryMap(
            CreateEvent(operationName, RunPhase.Failed, RunEventLevel.Error, exitCode: 2)));

    [Theory]
    [InlineData(null)]
    [InlineData(10)]
    public void TryMapTerminalEvent_ForUacCancelled_MapsToCancelledBeforeElevation(int? exitCode)
    {
        var cancelled = CreateEvent(
            "UacCancelled",
            RunPhase.Elevation,
            RunEventLevel.Error,
            exitCode);

        Assert.True(TerminalResultSemantics.TryMapTerminalEvent(cancelled, out var result));
        Assert.Equal(TerminalResult.CancelledBeforeElevation, result);
    }

    [Fact]
    public void TryMapTerminalEvent_ForUacCancelled_AcceptsTheMatchingRecordedResult()
    {
        var cancelled = CreateEvent(
            "UacCancelled",
            RunPhase.Elevation,
            RunEventLevel.Error,
            exitCode: 10,
            TerminalResult.CancelledBeforeElevation);

        Assert.True(TerminalResultSemantics.TryMapTerminalEvent(cancelled, out var result));
        Assert.Equal(TerminalResult.CancelledBeforeElevation, result);
    }

    [Fact]
    public void TryMapTerminalEvent_ForUacCancelled_RejectsAContradictoryEvent()
    {
        var cancelled = CreateEvent(
            "UacCancelled",
            RunPhase.Elevation,
            RunEventLevel.Error,
            exitCode: 10);

        Assert.False(TryMap(cancelled with { Phase = RunPhase.Completed }));
        Assert.False(TryMap(cancelled with { Level = RunEventLevel.Warning }));
        Assert.False(TryMap(cancelled with { ExitCode = 2 }));
        Assert.False(TryMap(cancelled with { TerminalResult = TerminalResult.Succeeded }));
        Assert.False(TryMap(cancelled with { TerminalResult = TerminalResult.WorkerInterrupted }));
        Assert.False(TryMap(cancelled with { TerminalResult = (TerminalResult)999 }));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(10)]
    public void TryMapTerminalEvent_ForUacLaunchFailed_MapsToWorkerInterrupted(int? exitCode)
    {
        var launchFailed = CreateEvent(
            "UacLaunchFailed",
            RunPhase.Elevation,
            RunEventLevel.Error,
            exitCode);

        Assert.True(TerminalResultSemantics.TryMapTerminalEvent(launchFailed, out var result));
        Assert.Equal(TerminalResult.WorkerInterrupted, result);
    }

    [Fact]
    public void TryMapTerminalEvent_ForUacLaunchFailed_RejectsAContradictoryEvent()
    {
        var launchFailed = CreateEvent(
            "UacLaunchFailed",
            RunPhase.Elevation,
            RunEventLevel.Error,
            exitCode: 10,
            TerminalResult.WorkerInterrupted);

        Assert.False(TryMap(launchFailed with { Phase = RunPhase.Failed }));
        Assert.False(TryMap(launchFailed with { Level = RunEventLevel.Information }));
        Assert.False(TryMap(launchFailed with { ExitCode = 4 }));
        Assert.False(TryMap(launchFailed with
        {
            TerminalResult = TerminalResult.CancelledBeforeElevation
        }));
        Assert.False(TryMap(launchFailed with { TerminalResult = (TerminalResult)999 }));
    }

    [Theory]
    [InlineData(null, TerminalResult.Succeeded)]
    [InlineData(TerminalResult.Succeeded, TerminalResult.Succeeded)]
    [InlineData(TerminalResult.CompletedWithNoReclaim, TerminalResult.CompletedWithNoReclaim)]
    public void TryMapTerminalEvent_ForWorkerCompleted_UsesTheRecordedResultOrAssumesSucceeded(
        TerminalResult? recorded,
        TerminalResult expected)
    {
        var completed = CreateEvent(
            "WorkerCompleted",
            RunPhase.Completed,
            RunEventLevel.Information,
            exitCode: 0,
            recorded);

        Assert.True(TerminalResultSemantics.TryMapTerminalEvent(completed, out var result));
        Assert.Equal(expected, result);
    }

    // A completion event is the only thing that can report success, so every
    // field of it is checked: a failure recorded on a "completed" event, or a
    // non-zero exit code, must not be read as a successful run.
    [Fact]
    public void TryMapTerminalEvent_ForWorkerCompleted_RejectsAnythingButACleanCompletion()
    {
        var completed = CreateEvent(
            "WorkerCompleted",
            RunPhase.Completed,
            RunEventLevel.Information,
            exitCode: 0);

        Assert.False(TryMap(completed with { Phase = RunPhase.Failed }));
        Assert.False(TryMap(completed with { Level = RunEventLevel.Error }));
        Assert.False(TryMap(completed with { ExitCode = 5 }));
        Assert.False(TryMap(completed with { ExitCode = null }));
        Assert.False(TryMap(completed with
        {
            TerminalResult = TerminalResult.DiskPartCompactFailed
        }));
        Assert.False(TryMap(completed with { TerminalResult = (TerminalResult)999 }));
    }

    [Theory]
    [InlineData(TerminalResult.ValidationFailed, 2)]
    [InlineData(TerminalResult.ShutdownTimedOut, 3)]
    [InlineData(TerminalResult.DiskPartPreflightFailed, 4)]
    [InlineData(TerminalResult.DiskPartCompactFailed, 5)]
    [InlineData(TerminalResult.WorkerInterrupted, 10)]
    [InlineData(TerminalResult.CancelledBeforeElevation, 10)]
    public void TryMapTerminalEvent_ForWorkerFailed_TrustsARecordedResultThatMatchesTheExitCode(
        TerminalResult recorded,
        int exitCode)
    {
        var failed = CreateEvent(
            "WorkerFailed",
            RunPhase.Failed,
            RunEventLevel.Error,
            exitCode,
            recorded);

        Assert.True(TerminalResultSemantics.TryMapTerminalEvent(failed, out var result));
        Assert.Equal(recorded, result);
    }

    [Theory]
    [InlineData(2, TerminalResult.ValidationFailed)]
    [InlineData(3, TerminalResult.ShutdownTimedOut)]
    [InlineData(4, TerminalResult.DiskPartPreflightFailed)]
    [InlineData(5, TerminalResult.DiskPartCompactFailed)]
    [InlineData(10, TerminalResult.WorkerInterrupted)]
    public void TryMapTerminalEvent_ForWorkerFailed_FallsBackToTheExitCode(
        int exitCode,
        TerminalResult expected)
    {
        var failed = CreateEvent("WorkerFailed", RunPhase.Failed, RunEventLevel.Error, exitCode);

        Assert.True(TerminalResultSemantics.TryMapTerminalEvent(failed, out var result));
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(6)]
    [InlineData(64)]
    public void TryMapTerminalEvent_ForWorkerFailed_RejectsAnUnmappedExitCode(int? exitCode) =>
        Assert.False(TryMap(
            CreateEvent("WorkerFailed", RunPhase.Failed, RunEventLevel.Error, exitCode)));

    [Fact]
    public void TryMapTerminalEvent_ForWorkerFailed_RejectsASuccessfulOrContradictoryResult()
    {
        var failed = CreateEvent(
            "WorkerFailed",
            RunPhase.Failed,
            RunEventLevel.Error,
            exitCode: 2,
            TerminalResult.ValidationFailed);

        Assert.False(TryMap(failed with { Phase = RunPhase.Completed }));
        Assert.False(TryMap(failed with { Level = RunEventLevel.Warning }));
        Assert.False(TryMap(failed with { ExitCode = 3 }));
        Assert.False(TryMap(failed with { TerminalResult = TerminalResult.Succeeded }));
        Assert.False(TryMap(failed with
        {
            TerminalResult = TerminalResult.CompletedWithNoReclaim
        }));
        Assert.False(TryMap(failed with { TerminalResult = (TerminalResult)999 }));
    }

    // Both results share exit code 10, so only the recorded result can tell a
    // user-cancelled elevation apart from a worker that died on its own.
    [Fact]
    public void TryMapTerminalEvent_ForWorkerFailed_KeepsCancellationDistinctFromInterruption()
    {
        var cancelled = CreateEvent(
            "WorkerFailed",
            RunPhase.Failed,
            RunEventLevel.Error,
            exitCode: 10,
            TerminalResult.CancelledBeforeElevation);
        var interrupted = CreateEvent(
            "WorkerFailed",
            RunPhase.Failed,
            RunEventLevel.Error,
            exitCode: 10);

        Assert.True(TerminalResultSemantics.TryMapTerminalEvent(cancelled, out var first));
        Assert.True(TerminalResultSemantics.TryMapTerminalEvent(interrupted, out var second));
        Assert.Equal(TerminalResult.CancelledBeforeElevation, first);
        Assert.Equal(TerminalResult.WorkerInterrupted, second);
    }

    private static bool TryMap(RunEvent @event) =>
        TerminalResultSemantics.TryMapTerminalEvent(@event, out _);

    private static RunEvent CreateEvent(
        string operationName,
        RunPhase phase,
        RunEventLevel level,
        int? exitCode,
        TerminalResult? terminalResult = null) => new(
        1,
        DateTimeOffset.UnixEpoch,
        Guid.Parse("0c2d9dbb-4a58-4a24-9bd1-6cb2b0a48ff4"),
        phase,
        level,
        operationName,
        ImmutableArray<string>.Empty,
        exitCode,
        TimeSpan.Zero,
        null,
        terminalResult);

    private static RunSummary CreateSummary(
        TerminalResult terminalResult,
        long? beforeBytes,
        long? afterBytes) => new(
        Guid.Parse("2f6f3f4c-8f6d-4b3c-9d1e-2a5c7e0f9b31"),
        new Profile(
            Guid.Parse("7ac4ef71-05b1-4b89-ae2d-ef644c9ae7eb"),
            "Ubuntu 24.04",
            "Ubuntu-24.04",
            "D:\\DevTools\\WSL2\\Ubuntu-24.04\\ext4.vhdx",
            ShutdownMode.Global,
            TimeSpan.FromSeconds(45)),
        OperationIntent.Compact,
        DateTimeOffset.UnixEpoch,
        DateTimeOffset.UnixEpoch,
        beforeBytes is long before ? CreateSnapshot(before) : null,
        afterBytes is long after ? CreateSnapshot(after) : null,
        terminalResult);

    private static VhdxSnapshot CreateSnapshot(long fileLengthBytes) => new(
        DateTimeOffset.UnixEpoch,
        "D:\\DevTools\\WSL2\\Ubuntu-24.04\\ext4.vhdx",
        fileLengthBytes,
        DateTimeOffset.UnixEpoch,
        false,
        new DriveSnapshot("D:\\", 100_000L, 50_000L));
}
