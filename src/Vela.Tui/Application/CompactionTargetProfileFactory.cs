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
