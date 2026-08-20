namespace Vela.Application.Tui;

/// <summary>
/// Workspace pages the user can navigate to. This controls the available
/// keyboard focus, not the visual layout.
/// </summary>
public enum TuiWorkspacePage
{
    /// <summary>Landing page with profile card and action list.</summary>
    Dashboard,
    /// <summary>Read-only preflight inspection.</summary>
    Preflight,
    /// <summary>Space-recovery estimate preview.</summary>
    ImpactPreview,
    /// <summary>Active compaction progress.</summary>
    Execution,
    /// <summary>Run history and log viewer.</summary>
    Logs,
    /// <summary>Profile list / selection.</summary>
    ProfileList,
    /// <summary>One-time startup confirmation gate.</summary>
    StartupConfirmation,
}
