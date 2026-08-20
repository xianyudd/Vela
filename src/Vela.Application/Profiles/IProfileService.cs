using System.Collections.Immutable;
using Vela.Core.Models;

namespace Vela.Application.Profiles;

/// <summary>
/// Domain service that coordinates profile selection, creation, update, and deletion
/// through a pluggable <see cref="IProfileStore"/>.
/// </summary>
public interface IProfileService
{
    /// <summary>
    /// The currently selected profile.
    /// </summary>
    Profile CurrentProfile { get; }

    /// <summary>
    /// All persisted profiles.
    /// </summary>
    ImmutableArray<Profile> Profiles { get; }

    /// <summary>
    /// Loads the persisted state and makes the service ready for use.
    /// </summary>
    Task<ProfileStoreState> LoadAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Switches the current profile to the supplied identifier.
    /// </summary>
    Task<Profile> SelectAsync(Guid profileId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a new profile from the supplied draft.
    /// </summary>
    Task<Profile> CreateAsync(ProfileDraft draft, CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates an existing profile from the supplied draft.
    /// </summary>
    Task<Profile> UpdateAsync(Guid profileId, ProfileDraft draft, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes the profile with the supplied identifier.
    /// </summary>
    Task DeleteAsync(Guid profileId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Builds a display-safe view model for the profile management page.
    /// </summary>
    ProfileManagementViewModel CreateViewModel(int selectedIndex = -1);
}
