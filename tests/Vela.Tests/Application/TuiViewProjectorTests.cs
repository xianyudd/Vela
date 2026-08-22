using System.Collections.Immutable;
using System.Text.Json;
using Vela.Application.Display;
using Vela.Application.Tui;
using Vela.Core.Contracts;
using Vela.Core.Models;
using Vela.Core.Validation;
using Vela.Core.Workflows;

namespace Vela.Tests.Application;

/// <summary>
/// Tests for <see cref="TuiViewProjector"/>. The projector is the sole bridge
/// from trusted session state to display-safe <see cref="TuiViewState"/>; no
/// raw paths, run identifiers, native output, or exception text may cross the
/// boundary. A bare file name is an intentional display field, but any full
/// path prefix must never appear.
/// </summary>
public sealed class TuiViewProjectorTests
{
    private static readonly string[] ForbiddenSubstrings =
    {
        @"D:\",
        @"C:\",
        "native output",
        "System.Exception",
        "Exception",
    };

    [Fact]
    public void Projector_ExcludesRawPathRunIdNativeOutputAndExceptionText()
    {
        var profile = new Profile(
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            "Ubuntu 24.04",
            "Ubuntu-24.04",
            @"D:\WSL\Ubuntu\ext4.vhdx",
            ShutdownMode.Distro,
            TimeSpan.FromSeconds(30));

        var trustedState = TuiSessionState.Initial() with
        {
            StartupStatus = StartupStatus.Ready,
            CurrentProfile = profile,
            LockedTarget = new LockedCompactionTarget(
                profile,
                profile.VhdxPath,
                LockedTargetQuality.SelectedProfile)
        };

        var viewState = TuiViewProjector.Project(trustedState);

        AssertNoForbiddenContent(viewState);
    }

    [Fact]
    public void Projector_ExcludesRawPathFromRunHistory()
    {
        var profile = new Profile(
            Guid.Parse("22222222-2222-2222-2222-222222222222"),
            "Debian",
            "Debian",
            @"D:\WSL\Debian\ext4.vhdx",
            ShutdownMode.Global,
            TimeSpan.FromSeconds(15));

        var displaySummary = DisplayRunSummary.FromTrusted(new RunSummary(
            Guid.Parse("33333333-3333-3333-3333-333333333333"),
            profile,
            OperationIntent.Compact,
            DateTimeOffset.UtcNow.AddMinutes(-10),
            DateTimeOffset.UtcNow,
            null,
            null,
            TerminalResult.Succeeded));

        var trustedState = TuiSessionState.Initial() with
        {
            StartupStatus = StartupStatus.Ready,
            CurrentProfile = profile,
            RunHistoryEntries = ImmutableArray.Create(displaySummary)
        };

        var viewState = TuiViewProjector.Project(trustedState);

        AssertNoForbiddenContent(viewState);
    }

    [Fact]
    public void Projector_ExcludesRawPathFromLogDetailEvents()
    {
        var trustedEvent = new DisplayRunEvent(
            Sequence: 1,
            OccurredAtUtc: DateTimeOffset.UtcNow,
            OperationName: "DiskPart 预检",
            Level: RunEventLevel.Information,
            ExitCodeSummary: null,
            Duration: null,
            SanitizedOutput: "日志格式无效");

        var trustedState = TuiSessionState.Initial() with
        {
            StartupStatus = StartupStatus.Ready,
            LogDetailEvents = ImmutableArray.Create(trustedEvent)
        };

        var viewState = TuiViewProjector.Project(trustedState);

        AssertNoForbiddenContent(viewState);
    }

    [Fact]
    public void Projector_MapsTrustedStateToDeterministicViewState()
    {
        var state = TuiSessionState.Initial();
        var projected = TuiViewProjector.Project(state);

        Assert.Equal(TuiWorkspacePage.StartupConfirmation, projected.Page);
        Assert.False(projected.IsBusy);
        Assert.NotNull(projected.Title);
        Assert.NotNull(projected.StatusMessage);
    }

    [Fact]
    public void Projector_IsBusyWhenRunning()
    {
        var running = TuiSessionState.Initial() with
        {
            StartupStatus = StartupStatus.Ready,
            CompactionStatus = CompactionStatus.Running
        };

        var projected = TuiViewProjector.Project(running);

        Assert.True(projected.IsBusy);
        Assert.Equal(TuiWorkspacePage.Execution, projected.Page);
    }

    [Fact]
    public void Projector_ReturnsReadyPageAfterStartup()
    {
        var ready = TuiSessionState.Initial() with
        {
            StartupStatus = StartupStatus.Ready
        };

        var projected = TuiViewProjector.Project(ready);

        Assert.Equal(TuiWorkspacePage.Dashboard, projected.Page);
        Assert.False(projected.IsBusy);
    }

    [Fact]
    public void Projector_ProjectsTargetSummaryFromPreflightReport()
    {
        var profile = new Profile(
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            "Ubuntu 24.04",
            "Ubuntu-24.04",
            @"D:\WSL\Ubuntu\ext4.vhdx",
            ShutdownMode.Distro,
            TimeSpan.FromSeconds(30));

        var snapshot = new VhdxSnapshot(
            CapturedAtUtc: DateTimeOffset.UtcNow,
            Path: @"D:\WSL\Ubuntu\ext4.vhdx",
            FileLengthBytes: 10L * 1024 * 1024 * 1024,
            LastWriteUtc: DateTimeOffset.UtcNow,
            IsSparse: true,
            Drive: new DriveSnapshot(
                RootPath: @"D:\",
                TotalSizeBytes: 500L * 1024 * 1024 * 1024,
                AvailableFreeSpaceBytes: 120L * 1024 * 1024 * 1024));

        var report = new PreflightReport(
            ValidationResult.Valid,
            InstalledInventory: null,
            LxssResolution: new LxssProfileResolution(
                LxssResolutionStatus.Matched,
                "Ubuntu-24.04",
                @"D:\WSL\Ubuntu\ext4.vhdx",
                @"D:\WSL\Ubuntu\ext4.vhdx"),
            VhdxInspection: new VhdxInspectionResult(VhdxInspectionStatus.Succeeded, snapshot),
            RunningInventory: null);

        var state = TuiSessionState.Initial() with
        {
            StartupStatus = StartupStatus.Ready,
            CurrentProfile = profile,
            LockedTarget = new LockedCompactionTarget(profile, profile.VhdxPath, LockedTargetQuality.SelectedProfile),
            PreflightStatus = PreflightStatus.Ready,
            LastPreflightReport = report
        };

        var projected = TuiViewProjector.Project(state);

        var summary = Assert.Single(projected.TargetSummaries);
        Assert.Equal("ext4.vhdx", summary.FileName);
        Assert.Equal("VHDX", summary.FileType);
        Assert.Equal("10.00 GiB", summary.CurrentSize);
        Assert.Equal("已匹配", summary.MappingStatus);
        Assert.Equal("是", summary.SparseState);
        Assert.Equal("可用 120.00 GiB / 共 500.00 GiB", summary.HostCapacityStatus);
    }

    [Fact]
    public void Projector_ProjectsUnconfiguredTargetWithFallbacks()
    {
        var profile = new Profile(
            Guid.Parse("22222222-2222-2222-2222-222222222222"),
            "Ubuntu 24.04",
            "Ubuntu-24.04",
            @"D:\WSL\Ubuntu\ext4.vhdx",
            ShutdownMode.Distro,
            TimeSpan.FromSeconds(30));

        var state = TuiSessionState.Initial() with
        {
            StartupStatus = StartupStatus.Ready,
            CurrentProfile = profile,
            LockedTarget = new LockedCompactionTarget(profile, profile.VhdxPath, LockedTargetQuality.SelectedProfile),
            PreflightStatus = PreflightStatus.Idle
        };

        var projected = TuiViewProjector.Project(state);

        var summary = Assert.Single(projected.TargetSummaries);
        Assert.Equal("ext4.vhdx", summary.FileName);
        Assert.Equal("VHDX", summary.FileType);
        Assert.Equal("未知", summary.CurrentSize);
        Assert.Equal("尚未检查", summary.MappingStatus);
        Assert.Equal("未知", summary.SparseState);
        Assert.Equal("未知", summary.HostCapacityStatus);
    }

    private static void AssertNoForbiddenContent(TuiViewState viewState)
    {
        // Serialize the whole view state and check for any forbidden data.
        var serialized = JsonSerializer.Serialize(viewState);
        foreach (var forbidden in ForbiddenSubstrings)
        {
            Assert.DoesNotContain(forbidden, serialized, StringComparison.OrdinalIgnoreCase);
        }
    }
}
