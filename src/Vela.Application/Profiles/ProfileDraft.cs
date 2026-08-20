using Vela.Core.Models;

namespace Vela.Application.Profiles;

/// <summary>
/// User-editable profile fields before validation and persistence.
/// </summary>
/// <param name="DisplayName">Human-readable name shown in the profile list.</param>
/// <param name="DistroName">WSL distribution name.</param>
/// <param name="VhdxPath">Absolute path to the target VHDX.</param>
/// <param name="ShutdownMode">Shutdown behavior for the distribution.</param>
/// <param name="ShutdownTimeout">Timeout for graceful shutdown.</param>
public sealed record ProfileDraft(
    string DisplayName,
    string DistroName,
    string VhdxPath,
    ShutdownMode ShutdownMode,
    TimeSpan ShutdownTimeout)
{
    /// <summary>
    /// Creates a draft from an existing persisted <see cref="Profile"/>.
    /// </summary>
    public static ProfileDraft FromProfile(Profile profile) => new(
        profile.DisplayName,
        profile.DistroName,
        profile.VhdxPath,
        profile.ShutdownMode,
        profile.ShutdownTimeout);

    /// <summary>
    /// Converts the draft back to a <see cref="Profile"/> with the supplied identifier.
    /// </summary>
    public Profile ToProfile(Guid id) => new(
        id,
        DisplayName,
        DistroName,
        VhdxPath,
        ShutdownMode,
        ShutdownTimeout);
}
