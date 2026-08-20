using System.Collections.Immutable;
using Vela.Core.Models;

namespace Vela.Application.Profiles;

/// <summary>
/// Narrow persistence boundary for profile store I/O.
/// </summary>
public interface IProfileStore
{
    /// <summary>
    /// Loads the store state, creating a default initial state when missing.
    /// </summary>
    Task<ProfileStoreState> LoadAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Loads the store state or throws when the configuration is missing.
    /// </summary>
    Task<ProfileStoreState> LoadRequiredAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Persists the supplied state atomically.
    /// </summary>
    Task SaveAsync(ProfileStoreState state, CancellationToken cancellationToken = default);
}
