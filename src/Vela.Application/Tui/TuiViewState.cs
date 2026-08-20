using System.Collections.Immutable;
using Vela.Application.Display;

namespace Vela.Application.Tui;

/// <summary>
/// Immutable display projection of the TUI. Terminal.Gui views only see this
/// type; they never see <see cref="TuiSessionState"/> or any trusted state.
/// </summary>
public sealed record TuiViewState(
    TuiWorkspacePage Page,
    string Title,
    string StatusMessage,
    DisplayMessageSeverity StatusSeverity,
    int SelectedIndex,
    ImmutableArray<DisplayVhdxSummary> TargetSummaries,
    ImmutableArray<DisplayRunSummary> RunHistory,
    ImmutableArray<DisplayRunEvent> LogEvents,
    bool IsBusy,
    string? ErrorMessage)
{
    /// <summary>
    /// Creates an empty view state for the initial frame.
    /// </summary>
    public static TuiViewState Initial() => new(
        TuiWorkspacePage.Dashboard,
        "Vela",
        "就绪",
        DisplayMessageSeverity.Info,
        0,
        ImmutableArray<DisplayVhdxSummary>.Empty,
        ImmutableArray<DisplayRunSummary>.Empty,
        ImmutableArray<DisplayRunEvent>.Empty,
        false,
        null);
}
