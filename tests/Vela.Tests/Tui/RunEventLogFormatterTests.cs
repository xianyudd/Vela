using System.Collections.Immutable;
using Vela.Core.Models;
using Vela.Tui.Application;

namespace Vela.Tests.Tui;

public sealed class RunEventLogFormatterTests
{
    [Fact]
    public void Format_keeps_timestamp_level_phase_and_operation_for_the_inline_console()
    {
        var @event = new RunEvent(
            7,
            new DateTimeOffset(2026, 8, 10, 2, 0, 1, 234, TimeSpan.Zero),
            Guid.NewGuid(),
            RunPhase.Snapshot,
            RunEventLevel.Warning,
            "VhdxSnapshot",
            ImmutableArray<string>.Empty,
            null,
            null,
            null);

        var line = RunEventLogFormatter.Format(@event);

        Assert.Contains("[2026-08-10 02:00:01.234Z]", line, StringComparison.Ordinal);
        Assert.Contains("WARN", line, StringComparison.Ordinal);
        Assert.Contains("Snapshot", line, StringComparison.Ordinal);
        Assert.Contains("VhdxSnapshot", line, StringComparison.Ordinal);
    }
}
