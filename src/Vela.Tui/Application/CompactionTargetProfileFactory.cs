using System.Collections.Immutable;
using Vela.Core.Contracts;
using Vela.Core.Models;

namespace Vela.Tui.Application;

/// <summary>
/// Binds the immutable execution profile to the instance selected in menu 01.
/// The stored profile supplies display and shutdown settings; the locked WSL
/// inventory row supplies the distro and VHDX that the operation addresses.
/// </summary>
public static class CompactionTargetProfileFactory
{
    public static OperationRequest? CreateRequest(
        Guid runId,
        Profile baseProfile,
        WslDistribution? lockedTarget)
    {
        if (runId == Guid.Empty)
        {
            return null;
        }

        var profile = Create(baseProfile, lockedTarget);
        return profile is null
            ? null
            : new OperationRequest(runId, profile, OperationIntent.Compact);
    }

    /// <summary>
    /// Reports whether the locked inventory row addresses a different distro
    /// than the stored profile describes. When it does, the profile's shutdown
    /// scope and display name were written for another distro, so the operator
    /// must be warned before the operation is allowed to proceed.
    /// </summary>
    public static bool IsTargetMismatch(Profile baseProfile, WslDistribution? lockedTarget)
    {
        ArgumentNullException.ThrowIfNull(baseProfile);

        if (lockedTarget is null || string.IsNullOrWhiteSpace(lockedTarget.Name))
        {
            return false;
        }

        return !string.Equals(
            lockedTarget.Name,
            baseProfile.DistroName,
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Finds the stored profile that owns the locked inventory row.
    /// </summary>
    /// <param name="profiles">All stored profiles, in store order.</param>
    /// <param name="lockedTarget">The WSL instance locked on the target-selection page.</param>
    /// <returns>
    /// The first profile whose <see cref="Profile.DistroName"/> equals the locked
    /// name case-insensitively, or <see langword="null"/> when the locked target
    /// is absent or no profile matches.
    /// </returns>
    /// <remarks>
    /// When several profiles share a distro name the first one in store order
    /// wins; the caller has no basis to disambiguate further.
    /// </remarks>
    public static Profile? FindProfileForTarget(
        ImmutableArray<Profile> profiles,
        WslDistribution? lockedTarget)
    {
        if (lockedTarget is null || string.IsNullOrWhiteSpace(lockedTarget.Name))
        {
            return null;
        }

        return profiles.FirstOrDefault(profile =>
            string.Equals(
                profile.DistroName,
                lockedTarget.Name,
                StringComparison.OrdinalIgnoreCase));
    }

    public static Profile? Create(Profile baseProfile, WslDistribution? lockedTarget)
    {
        ArgumentNullException.ThrowIfNull(baseProfile);

        if (lockedTarget is null || string.IsNullOrWhiteSpace(lockedTarget.Name))
        {
            return null;
        }

        var vhdxPath = lockedTarget.VhdxPath;
        if (string.IsNullOrWhiteSpace(vhdxPath) &&
            string.Equals(
                lockedTarget.Name,
                baseProfile.DistroName,
                StringComparison.OrdinalIgnoreCase))
        {
            vhdxPath = baseProfile.VhdxPath;
        }

        return string.IsNullOrWhiteSpace(vhdxPath)
            ? null
            : baseProfile with
            {
                DistroName = lockedTarget.Name,
                VhdxPath = vhdxPath
            };
    }
}
