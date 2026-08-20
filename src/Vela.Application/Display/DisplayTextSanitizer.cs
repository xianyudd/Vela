using System.Collections.Immutable;

namespace Vela.Application.Display;

/// <summary>
/// Bounds and sanitizes text that is safe to render in the TUI.
/// Raw paths, run identifiers, native output, and exception details are
/// removed and replaced with a fixed placeholder.
/// </summary>
public static class DisplayTextSanitizer
{
    private const string Placeholder = "日志格式无效";
    private const string PathPrefix = "原始路径：";

    /// <summary>
    /// Sanitizes a single line for display. If the line contains internal
    /// details (paths, run IDs, native output, or exception stack text),
    /// it is replaced with the fixed placeholder.
    /// </summary>
    /// <param name="text">The raw line from a trusted log or journal.</param>
    /// <returns>The sanitized line, never null.</returns>
    public static string SanitizeSingleLine(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return text ?? string.Empty;
        }

        if (text.Contains(PathPrefix, StringComparison.OrdinalIgnoreCase) ||
            text.Contains("D:\\", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("C:\\", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("RunId", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("native output", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Exception", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("   at ", StringComparison.Ordinal))
        {
            return Placeholder;
        }

        return text.Trim();
    }

    /// <summary>
    /// Sanitizes a collection of lines and bounds the result to
    /// <paramref name="maxLines"/>.
    /// </summary>
    public static ImmutableArray<string> SanitizeLines(
        IEnumerable<string>? lines,
        int maxLines = 100)
    {
        if (lines is null)
        {
            return ImmutableArray<string>.Empty;
        }

        var sanitized = lines
            .Select(SanitizeSingleLine)
            .Take(Math.Max(1, maxLines))
            .ToImmutableArray();
        return sanitized;
    }
}
