using System.Collections.Immutable;

namespace Vela.Application.Tui;

/// <summary>
/// Result of a pure reducer transition: the new session state plus any
/// effects that should be scheduled.
/// </summary>
public sealed record TuiTransition(
    TuiSessionState State,
    ImmutableArray<TuiEffect> Effects)
{
    /// <summary>
    /// Creates a transition with no effects.
    /// </summary>
    public static TuiTransition NoEffect(TuiSessionState state) =>
        new(state, ImmutableArray<TuiEffect>.Empty);

    /// <summary>
    /// Creates a transition with a single effect.
    /// </summary>
    public static TuiTransition WithEffect(TuiSessionState state, TuiEffect effect) =>
        new(state, ImmutableArray.Create(effect));
}
