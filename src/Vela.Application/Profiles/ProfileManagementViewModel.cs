using System.Collections.Immutable;

namespace Vela.Application.Profiles;

/// <summary>
/// Display-safe view model for the profile management page.
/// </summary>
/// <param name="Profiles">The list of profile rows to render.</param>
/// <param name="SelectedIndex">The currently highlighted row index.</param>
/// <param name="ActionsMessage">Instructional text for available actions.</param>
/// <param name="ValidationError">Optional validation message to surface.</param>
public sealed record ProfileManagementViewModel(
    ImmutableArray<ProfileListItemViewModel> Profiles,
    int SelectedIndex,
    string ActionsMessage,
    string? ValidationError = null);
