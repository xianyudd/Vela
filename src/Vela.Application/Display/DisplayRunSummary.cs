using System.Collections.Immutable;

namespace Vela.Application.Display;

/// <summary>
/// Display-safe run-history entry. Raw VHDX paths and run identifiers are
/// not exposed.
/// </summary>
public sealed record DisplayRunSummary(
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    string ProfileDisplayName,
    string? DistroName,
    Vela.Core.Models.OperationIntent? Intent,
    Vela.Core.Models.TerminalResult? TerminalResult,
    long? ReclaimedBytes,
    bool IsMalformed,
    string? ErrorMessage)
{
    /// <summary>
    /// Elapsed time between start and completion when both are known.
    /// </summary>
    public TimeSpan? Elapsed =>
        StartedAtUtc is { } started && CompletedAtUtc is { } completed
            ? completed - started
            : null;

    /// <summary>
    /// Creates an empty snapshot with an optional error message.
    /// </summary>
    public static ImmutableArray<DisplayRunSummary> Empty() =>
        ImmutableArray<DisplayRunSummary>.Empty;

    /// <summary>
    /// Maps a trusted <see cref="Vela.Core.Models.RunSummary"/> to a
    /// display-safe entry.
    /// </summary>
    public static DisplayRunSummary FromTrusted(Vela.Core.Models.RunSummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);
        return new DisplayRunSummary(
            summary.StartedAtUtc,
            summary.CompletedAtUtc,
            summary.Profile.DisplayName,
            summary.Profile.DistroName,
            summary.Intent,
            summary.TerminalResult,
            summary.ReclaimedBytes,
            IsMalformed: false,
            ErrorMessage: null);
    }

    /// <summary>
    /// Creates a malformed entry with a fixed error message.
    /// </summary>
    public static DisplayRunSummary Malformed(string message) =>
        new(
            StartedAtUtc: null,
            CompletedAtUtc: null,
            ProfileDisplayName: "未知档案",
            DistroName: null,
            Intent: null,
            TerminalResult: null,
            ReclaimedBytes: null,
            IsMalformed: true,
            ErrorMessage: message);
}
