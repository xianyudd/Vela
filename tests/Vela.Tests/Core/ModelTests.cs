using System.Collections.Immutable;
using Vela.Core.Models;

namespace Vela.Tests.Core;

public sealed class ModelTests
{
    [Fact]
    public void Profile_WithMatchingValues_UsesValueEquality()
    {
        var first = CreateProfile();
        var second = CreateProfile();

        Assert.Equal(first, second);
    }

    [Fact]
    public void Profile_PreservesAllConstructorValues()
    {
        var profile = CreateProfile();

        Assert.Equal(Guid.Parse("7ac4ef71-05b1-4b89-ae2d-ef644c9ae7eb"), profile.Id);
        Assert.Equal("Ubuntu 24.04", profile.DisplayName);
        Assert.Equal("Ubuntu-24.04", profile.DistroName);
        Assert.Equal("D:\\DevTools\\WSL2\\Ubuntu-24.04\\ext4.vhdx", profile.VhdxPath);
        Assert.Equal(ShutdownMode.Global, profile.ShutdownMode);
        Assert.Equal(TimeSpan.FromSeconds(45), profile.ShutdownTimeout);
    }

    [Fact]
    public void OperationRequest_RunId_RoundTripsInDFormat()
    {
        var runId = Guid.Parse("4f868c08-864f-4ca8-b181-69973c8ee32e");
        var request = new OperationRequest(runId, CreateProfile(), OperationIntent.Compact);

        var roundTrippedRunId = Guid.ParseExact(request.RunId.ToString("D"), "D");

        Assert.Equal(runId, roundTrippedRunId);
    }

    [Fact]
    public void OperationRequest_PreservesAllConstructorValues()
    {
        var runId = Guid.Parse("f9fda4fb-c4b0-4cd0-ac55-6454406153d0");
        var profile = CreateProfile();
        var request = new OperationRequest(runId, profile, OperationIntent.Preflight);

        Assert.Equal(runId, request.RunId);
        Assert.Same(profile, request.Profile);
        Assert.Equal(OperationIntent.Preflight, request.Intent);
    }

    [Fact]
    public void VhdxAndDriveSnapshots_PreserveAllConstructorValues()
    {
        var capturedAtUtc = DateTimeOffset.UnixEpoch.AddMinutes(10);
        var lastWriteUtc = DateTimeOffset.UnixEpoch.AddMinutes(5);
        var drive = new DriveSnapshot("D:\\", 100_000L, 50_000L);
        var snapshot = new VhdxSnapshot(
            capturedAtUtc,
            "D:\\DevTools\\WSL2\\Ubuntu-24.04\\ext4.vhdx",
            10_000L,
            lastWriteUtc,
            false,
            drive);

        Assert.Equal(capturedAtUtc, snapshot.CapturedAtUtc);
        Assert.Equal("D:\\DevTools\\WSL2\\Ubuntu-24.04\\ext4.vhdx", snapshot.Path);
        Assert.Equal(10_000L, snapshot.FileLengthBytes);
        Assert.Equal(lastWriteUtc, snapshot.LastWriteUtc);
        Assert.Equal((bool?)false, snapshot.IsSparse);
        Assert.Same(drive, snapshot.Drive);
        Assert.Equal("D:\\", drive.RootPath);
        Assert.Equal(100_000L, drive.TotalSizeBytes);
        Assert.Equal(50_000L, drive.AvailableFreeSpaceBytes);
    }

    [Fact]
    public void OperationIntent_DefinesOnlyPreflightAndCompact()
    {
        var intents = Enum.GetValues<OperationIntent>();

        Assert.Equal([OperationIntent.Preflight, OperationIntent.Compact], intents);
    }

    [Fact]
    public void RunSummary_CalculatesReclaimedBytesFromSnapshots()
    {
        var beforeSnapshot = CreateSnapshot(fileLengthBytes: 10_000L);
        var afterSnapshot = CreateSnapshot(fileLengthBytes: 7_500L);
        var summary = new RunSummary(
            Guid.Parse("8b2c38df-a454-4e9b-b1aa-e093502d9c86"),
            CreateProfile(),
            OperationIntent.Compact,
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch.AddMinutes(1),
            beforeSnapshot,
            afterSnapshot,
            TerminalResult.Succeeded);

        Assert.Equal(2_500L, summary.ReclaimedBytes);
    }

    [Fact]
    public void RunSummary_PreservesAllConstructorValues()
    {
        var runId = Guid.Parse("15bd7482-6407-4b8e-ad3f-3b40a3b1ff73");
        var profile = CreateProfile();
        var startedAtUtc = DateTimeOffset.UnixEpoch;
        var completedAtUtc = startedAtUtc.AddMinutes(1);
        var beforeSnapshot = CreateSnapshot(fileLengthBytes: 10_000L);
        var afterSnapshot = CreateSnapshot(fileLengthBytes: 7_500L);
        var summary = new RunSummary(
            runId,
            profile,
            OperationIntent.Compact,
            startedAtUtc,
            completedAtUtc,
            beforeSnapshot,
            afterSnapshot,
            TerminalResult.Succeeded);

        Assert.Equal(runId, summary.RunId);
        Assert.Same(profile, summary.Profile);
        Assert.Equal(OperationIntent.Compact, summary.Intent);
        Assert.Equal(startedAtUtc, summary.StartedAtUtc);
        Assert.Equal(completedAtUtc, summary.CompletedAtUtc);
        Assert.Same(beforeSnapshot, summary.BeforeSnapshot);
        Assert.Same(afterSnapshot, summary.AfterSnapshot);
        Assert.Equal(TerminalResult.Succeeded, summary.TerminalResult);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void RunSummary_ReclaimedBytesIsUnknownWhenEitherSnapshotIsMissing(
        bool hasBeforeSnapshot,
        bool hasAfterSnapshot)
    {
        var summary = new RunSummary(
            Guid.Parse("7fe0680d-35c8-4c6d-a8c9-1882cd0b06ed"),
            CreateProfile(),
            OperationIntent.Preflight,
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch.AddMinutes(1),
            hasBeforeSnapshot ? CreateSnapshot(fileLengthBytes: 10_000L) : null,
            hasAfterSnapshot ? CreateSnapshot(fileLengthBytes: 7_500L) : null,
            TerminalResult.ValidationFailed);

        Assert.Null(summary.ReclaimedBytes);
    }

    [Fact]
    public void RunSummary_ReclaimedBytesPreservesANegativeDeltaWhenVhdxGrows()
    {
        var summary = new RunSummary(
            Guid.Parse("f8e2a2ec-441d-4f71-80a4-a8334e8dbdb6"),
            CreateProfile(),
            OperationIntent.Compact,
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch.AddMinutes(1),
            CreateSnapshot(fileLengthBytes: 7_500L),
            CreateSnapshot(fileLengthBytes: 10_000L),
            TerminalResult.Succeeded);

        Assert.Equal(-2_500L, summary.ReclaimedBytes);
    }

    [Theory]
    [InlineData(nameof(TerminalResult.Succeeded))]
    [InlineData(nameof(TerminalResult.CompletedWithNoReclaim))]
    [InlineData(nameof(TerminalResult.ValidationFailed))]
    [InlineData(nameof(TerminalResult.ShutdownTimedOut))]
    [InlineData(nameof(TerminalResult.DiskPartPreflightFailed))]
    [InlineData(nameof(TerminalResult.DiskPartCompactFailed))]
    [InlineData(nameof(TerminalResult.WorkerInterrupted))]
    [InlineData(nameof(TerminalResult.CancelledBeforeElevation))]
    public void TerminalResult_DefinesRequiredTerminalValue(string expectedName)
    {
        Assert.True(Enum.TryParse<TerminalResult>(expectedName, out _));
    }

    [Fact]
    public void TerminalResultSemantics_MapsCanonicalTerminalEventsStrictly()
    {
        var runId = Guid.Parse("61d3b78f-05db-4ca5-a019-a5d8dba7ce7e");
        var valid = new RunEvent(
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
            TerminalResult.CompletedWithNoReclaim);

        Assert.True(TerminalResultSemantics.TryMapTerminalEvent(valid, out var result));
        Assert.Equal(TerminalResult.CompletedWithNoReclaim, result);
        Assert.False(TerminalResultSemantics.TryMapTerminalEvent(
            valid with { ExitCode = 2 },
            out _));
        Assert.False(TerminalResultSemantics.TryMapTerminalEvent(
            valid with { Level = RunEventLevel.Error },
            out _));
    }
    [Fact]
    public void Profile_WithExpression_DoesNotModifyTheOriginalValue()
    {
        var original = CreateProfile();

        var renamed = original with { DisplayName = "Ubuntu development" };

        Assert.Equal("Ubuntu 24.04", original.DisplayName);
        Assert.Equal("Ubuntu development", renamed.DisplayName);
        Assert.NotEqual(original, renamed);
    }

    [Fact]
    public void Profile_DoesNotExposeAllowVhdxMismatch()
    {
        var profileType = typeof(Profile);

        Assert.Null(profileType.GetProperty("AllowVhdxMismatch"));
        Assert.Null(profileType.GetField("AllowVhdxMismatch"));
    }

    [Fact]
    public void RunEvent_UsesAnImmutableArgumentArray()
    {
        var arguments = ImmutableArray.Create("--shutdown");
        var runEvent = new RunEvent(
            Sequence: 1,
            OccurredAtUtc: DateTimeOffset.UnixEpoch,
            RunId: Guid.Parse("c393d19b-e2dc-41b4-9645-f4d00e1e3dc8"),
            Phase: RunPhase.Shutdown,
            Level: RunEventLevel.Information,
            OperationName: "wsl.exe",
            Arguments: arguments,
            ExitCode: 0,
            Duration: TimeSpan.FromSeconds(1),
            Output: "Shutdown complete.");

        Assert.Equal(1L, runEvent.Sequence);
        Assert.Equal(DateTimeOffset.UnixEpoch, runEvent.OccurredAtUtc);
        Assert.Equal(Guid.Parse("c393d19b-e2dc-41b4-9645-f4d00e1e3dc8"), runEvent.RunId);
        Assert.Equal(RunPhase.Shutdown, runEvent.Phase);
        Assert.Equal(RunEventLevel.Information, runEvent.Level);
        Assert.Equal("wsl.exe", runEvent.OperationName);
        Assert.IsType<ImmutableArray<string>>(runEvent.Arguments);
        Assert.Equal(arguments, runEvent.Arguments);
        Assert.Equal((int?)0, runEvent.ExitCode);
        Assert.Equal((TimeSpan?)TimeSpan.FromSeconds(1), runEvent.Duration);
        Assert.Equal("Shutdown complete.", runEvent.Output);
    }

    private static Profile CreateProfile() => new(
        Guid.Parse("7ac4ef71-05b1-4b89-ae2d-ef644c9ae7eb"),
        "Ubuntu 24.04",
        "Ubuntu-24.04",
        "D:\\DevTools\\WSL2\\Ubuntu-24.04\\ext4.vhdx",
        ShutdownMode.Global,
        TimeSpan.FromSeconds(45));

    private static VhdxSnapshot CreateSnapshot(long fileLengthBytes) => new(
        DateTimeOffset.UnixEpoch,
        "D:\\DevTools\\WSL2\\Ubuntu-24.04\\ext4.vhdx",
        fileLengthBytes,
        DateTimeOffset.UnixEpoch,
        false,
        new DriveSnapshot("D:\\", 100_000L, 50_000L));

}
