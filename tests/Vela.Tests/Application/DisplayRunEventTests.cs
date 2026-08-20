using System.Collections.Immutable;
using Vela.Application.Display;
using Vela.Core.Models;

namespace Vela.Tests.Application;

/// <summary>
/// Tests for mapping trusted run events into sanitized display events.
/// </summary>
public sealed class DisplayRunEventTests
{
    [Fact]
    public void FromTrusted_MapsFieldsAndSanitizesOutput()
    {
        var trusted = new RunEvent(
            Sequence: 42,
            OccurredAtUtc: new DateTimeOffset(2026, 8, 20, 1, 2, 3, TimeSpan.Zero),
            RunId: Guid.NewGuid(),
            Phase: RunPhase.Compacting,
            Level: RunEventLevel.Warning,
            OperationName: "DiskPart 压缩",
            Arguments: ImmutableArray<string>.Empty,
            ExitCode: 0,
            Duration: TimeSpan.FromSeconds(5),
            Output: "正常输出");

        var display = DisplayRunEvent.FromTrusted(trusted);

        Assert.Equal(42, display.Sequence);
        Assert.Equal(new DateTimeOffset(2026, 8, 20, 1, 2, 3, TimeSpan.Zero), display.OccurredAtUtc);
        Assert.Equal("DiskPart 压缩", display.OperationName);
        Assert.Equal(RunEventLevel.Warning, display.Level);
        Assert.Equal("0", display.ExitCodeSummary);
        Assert.Equal(TimeSpan.FromSeconds(5), display.Duration);
        Assert.Equal("正常输出", display.SanitizedOutput);
    }

    [Fact]
    public void FromTrusted_ReplacesInternalDetailsInOutput()
    {
        var trusted = new RunEvent(
            Sequence: 1,
            OccurredAtUtc: DateTimeOffset.UtcNow,
            RunId: Guid.NewGuid(),
            Phase: RunPhase.DiskPartPreflight,
            Level: RunEventLevel.Error,
            OperationName: "DiskPart 预检",
            Arguments: ImmutableArray<string>.Empty,
            ExitCode: null,
            Duration: null,
            Output: @"读取 D:\WSL\ext4.vhdx 失败");

        var display = DisplayRunEvent.FromTrusted(trusted);

        Assert.Equal("日志格式无效", display.SanitizedOutput);
        Assert.Null(display.ExitCodeSummary);
    }

    [Fact]
    public void FromTrusted_NullOutputBecomesEmptyString()
    {
        var trusted = new RunEvent(
            Sequence: 2,
            OccurredAtUtc: DateTimeOffset.UtcNow,
            RunId: Guid.NewGuid(),
            Phase: RunPhase.Validation,
            Level: RunEventLevel.Information,
            OperationName: "校验",
            Arguments: ImmutableArray<string>.Empty,
            ExitCode: null,
            Duration: null,
            Output: null);

        var display = DisplayRunEvent.FromTrusted(trusted);

        Assert.Equal(string.Empty, display.SanitizedOutput);
    }
}
