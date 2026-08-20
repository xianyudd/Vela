using System.Collections.Immutable;
using Vela.Core.Models;

namespace Vela.Application.Display;

/// <summary>
/// Display-safe projection of a single run event. Raw output and exception
/// details are not exposed.
/// </summary>
public sealed record DisplayRunEvent(
    long Sequence,
    DateTimeOffset OccurredAtUtc,
    string OperationName,
    RunEventLevel Level,
    string? ExitCodeSummary,
    TimeSpan? Duration,
    string SanitizedOutput)
{
    /// <summary>
    /// Maps a trusted journal event to a display-safe event.
    /// </summary>
    public static DisplayRunEvent FromTrusted(RunEvent runEvent)
    {
        ArgumentNullException.ThrowIfNull(runEvent);
        return new DisplayRunEvent(
            runEvent.Sequence,
            runEvent.OccurredAtUtc,
            runEvent.OperationName,
            runEvent.Level,
            runEvent.ExitCode?.ToString(System.Globalization.CultureInfo.InvariantCulture),
            runEvent.Duration,
            DisplayTextSanitizer.SanitizeSingleLine(runEvent.Output));
    }
}
