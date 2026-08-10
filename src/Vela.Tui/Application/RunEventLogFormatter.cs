using System.Globalization;
using Vela.Core.Models;

namespace Vela.Tui.Application;

/// <summary>
/// Projects one durable journal event into the compact line shown by the
/// live TUI console. The line keeps the diagnostic facts needed at a glance:
/// timestamp, severity, phase, and operation.
/// </summary>
public static class RunEventLogFormatter
{
    public static string Format(RunEvent @event)
    {
        ArgumentNullException.ThrowIfNull(@event);

        var timestamp = @event.OccurredAtUtc
            .ToUniversalTime()
            .ToString("yyyy-MM-dd HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
        var level = @event.Level switch
        {
            RunEventLevel.Trace => "TRACE",
            RunEventLevel.Warning => "WARN",
            RunEventLevel.Error => "ERROR",
            _ => "INFO"
        };
        var phase = TuiDisplayText.SafeToken(@event.Phase.ToString(), 20, "UnknownPhase");
        var operation = TuiDisplayText.SafeToken(@event.OperationName, 48, "UnknownOperation");
        return $"[{timestamp}] {level,-5} {phase,-20} {operation}";
    }
}
