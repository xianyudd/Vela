using Vela.Core.Models;

namespace Vela.Application.Profiles;

/// <summary>
/// Display-safe row model for a single profile in the management list.
/// Raw VHDX paths are never exposed through this type.
/// </summary>
/// <param name="DisplayName">Human-readable name shown in the profile list.</param>
/// <param name="DistroName">WSL distribution name.</param>
/// <param name="TargetConfigured">Whether the VHDX target path is set.</param>
/// <param name="ShutdownMode">Shutdown behavior for the distribution.</param>
/// <param name="ShutdownTimeout">Timeout for graceful shutdown.</param>
/// <param name="IsCurrent">Whether this is the currently selected profile.</param>
/// <param name="IsSelected">Whether this row is currently highlighted in the UI.</param>
public sealed record ProfileListItemViewModel(
    string DisplayName,
    string DistroName,
    bool TargetConfigured,
    ShutdownMode ShutdownMode,
    TimeSpan ShutdownTimeout,
    bool IsCurrent,
    bool IsSelected);
