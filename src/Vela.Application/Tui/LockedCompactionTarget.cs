using Vela.Core.Models;

namespace Vela.Application.Tui;

/// <summary>
/// Quality of the target lock; high-quality locks carry more trusted state.
/// </summary>
public enum LockedTargetQuality
{
    /// <summary>Lock was created from the currently selected profile.</summary>
    SelectedProfile,
    /// <summary>Lock was restored from a previous successful preflight.</summary>
    RestoredFromPreflight,
}

/// <summary>
/// Represents a target that has been locked for compaction. All navigation
/// and execution decisions use this trusted target, not the currently
/// highlighted profile.
/// </summary>
public sealed record LockedCompactionTarget(
    Profile Profile,
    string VhdxPath,
    LockedTargetQuality Quality);
