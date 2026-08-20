namespace Vela.Application.Display;

/// <summary>
/// Display-safe message with optional severity. Raw paths and exception
/// details are sanitized before use.
/// </summary>
public sealed record DisplayMessage(
    string Text,
    DisplayMessageSeverity Severity = DisplayMessageSeverity.Info)
{
    /// <summary>
    /// Creates a sanitized message from raw text.
    /// </summary>
    public static DisplayMessage FromSanitized(
        string? text,
        DisplayMessageSeverity severity = DisplayMessageSeverity.Info) =>
        new(DisplayTextSanitizer.SanitizeSingleLine(text), severity);
}

/// <summary>
/// Severity of a display message.
/// </summary>
public enum DisplayMessageSeverity
{
    /// <summary>Informational message.</summary>
    Info,
    /// <summary>Warning; user attention advised.</summary>
    Warning,
    /// <summary>Error; operation could not complete.</summary>
    Error,
}
