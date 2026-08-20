namespace Vela.Application.Startup;

/// <summary>
/// Distinguishes how startup initialization completed.
/// </summary>
public enum StartupInitializationKind
{
    /// <summary>Data root and profile store loaded successfully.</summary>
    Succeeded,
    /// <summary>Data root was missing or invalid; a fixed startup error is shown.</summary>
    Failed,
}

/// <summary>
/// Display-safe outcome of startup initialization. Does not expose raw paths
/// or store internals.
/// </summary>
public sealed record StartupInitializationOutcome(
    StartupInitializationKind Kind,
    string? ErrorMessage)
{
    /// <summary>
    /// Creates a successful outcome.
    /// </summary>
    public static StartupInitializationOutcome Succeeded() =>
        new(StartupInitializationKind.Succeeded, null);

    /// <summary>
    /// Creates a failed outcome with a fixed startup message.
    /// </summary>
    public static StartupInitializationOutcome Failed(string message) =>
        new(StartupInitializationKind.Failed, message);
}
