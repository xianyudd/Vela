using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using Vela.Core.Contracts;

namespace Vela.Application.Display;

/// <summary>
/// Bounds and sanitizes text that is safe to render in the TUI.
/// Raw paths, run identifiers, native output, and exception details are
/// removed and replaced with a fixed placeholder. Control and escape
/// sequences are stripped, and long strings are truncated to a cell budget.
/// </summary>
public static class DisplayTextSanitizer
{
    private const string Placeholder = "日志格式无效";
    private const string PathPrefix = "原始路径：";
    private const long Gibibyte = 1024L * 1024L * 1024L;
    private const long Tebibyte = 1024L * Gibibyte;

    /// <summary>
    /// Sanitizes a single line for display. If the line contains internal
    /// details (paths, run IDs, native output, or exception stack text),
    /// it is replaced with the fixed placeholder. Otherwise control and
    /// escape sequences are stripped and the line is trimmed.
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

        return StripControlAndEscapeSequences(text).Trim();
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

    /// <summary>
    /// Strips control and escape sequences and truncates the result to at most
    /// <paramref name="maxCells"/> display cells, appending an ellipsis when
    /// truncated.
    /// </summary>
    public static string Sanitize(string? value, int maxCells = 240)
    {
        if (maxCells <= 0 || string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var normalized = StripControlAndEscapeSequences(value);
        if (DisplayWidth(normalized) <= maxCells)
        {
            return normalized;
        }

        if (maxCells == 1)
        {
            return "…";
        }

        var builder = new StringBuilder(normalized.Length);
        var width = 0;
        foreach (var rune in normalized.EnumerateRunes())
        {
            var runeWidth = GetRuneWidth(rune.Value);
            if (width + runeWidth > maxCells - 1)
            {
                break;
            }

            builder.Append(rune.ToString());
            width += runeWidth;
        }

        return builder.Append('…').ToString();
    }

    /// <summary>
    /// Formats a byte count as a human-readable GiB/TiB string, or "未知" when
    /// unknown or negative.
    /// </summary>
    public static string FormatBytes(long? bytes)
    {
        if (bytes is not { } value || value < 0)
        {
            return "未知";
        }

        var (scaled, unit) = value >= Tebibyte
            ? (value / (double)Tebibyte, "TiB")
            : (value / (double)Gibibyte, "GiB");

        return string.Create(CultureInfo.InvariantCulture, $"{scaled:0.00} {unit}");
    }

    /// <summary>
    /// Returns only the file-name portion of a path, without any directory.
    /// </summary>
    public static string FileNameOnly(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        return System.IO.Path.GetFileName(path) ?? string.Empty;
    }

    /// <summary>
    /// Localizes the sparse-state flag.
    /// </summary>
    public static string FormatSparseState(bool? isSparse) => isSparse switch
    {
        true => "是",
        false => "否",
        _ => "未知"
    };

    /// <summary>
    /// Localizes an LXSS profile-resolution status. A null status means the
    /// resolution has not run yet, and unknown enum values share that same
    /// stable fallback, so callers never need a fallback string of their own.
    /// </summary>
    public static string FormatMappingStatus(LxssResolutionStatus? status) => status switch
    {
        LxssResolutionStatus.Matched => "已匹配",
        LxssResolutionStatus.Mismatched => "不匹配",
        LxssResolutionStatus.NotFound => "未找到",
        LxssResolutionStatus.Failed => "解析失败",
        _ => "尚未检查"
    };

    private static string StripControlAndEscapeSequences(string value)
    {
        var builder = new StringBuilder(value.Length);
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (character == '\x1b')
            {
                index = SkipEscapeSequence(value, index);
                continue;
            }

            if (char.IsControl(character) || character == '\x7f')
            {
                builder.Append(' ');
            }
            else
            {
                builder.Append(character);
            }
        }

        return builder.ToString();
    }

    private static int SkipEscapeSequence(string value, int escapeIndex)
    {
        if (escapeIndex + 1 >= value.Length)
        {
            return escapeIndex;
        }

        var next = value[escapeIndex + 1];
        if (next == '[')
        {
            var index = escapeIndex + 2;
            while (index < value.Length)
            {
                var character = value[index];
                if (character is >= '@' and <= '~')
                {
                    return index;
                }

                index++;
            }

            return value.Length - 1;
        }

        if (next == ']')
        {
            for (var index = escapeIndex + 2; index < value.Length; index++)
            {
                if (value[index] == '\x07')
                {
                    return index;
                }

                if (value[index] == '\x1b' && index + 1 < value.Length && value[index + 1] == '\\')
                {
                    return index + 1;
                }
            }

            return value.Length - 1;
        }

        return escapeIndex + 1;
    }

    /// <summary>
    /// Returns how many terminal cells <paramref name="value"/> occupies,
    /// counting combining marks as zero and CJK/emoji runes as two.
    /// </summary>
    /// <remarks>
    /// Public because callers that lay text out in fixed columns need the same
    /// width arithmetic <see cref="Sanitize"/> truncates by. A second
    /// implementation would drift from this one and mis-pad.
    /// </remarks>
    public static int DisplayWidth(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return 0;
        }

        var width = 0;
        foreach (var rune in value.EnumerateRunes())
        {
            width += GetRuneWidth(rune.Value);
        }

        return width;
    }

    private static int GetRuneWidth(int value)
    {
        if (value == 0 || value is >= 0x0300 and <= 0x036f ||
            value is >= 0x1ab0 and <= 0x1aff ||
            value is >= 0x1dc0 and <= 0x1dff ||
            value is >= 0x20d0 and <= 0x20ff ||
            value is >= 0xfe00 and <= 0xfe0f ||
            value is >= 0xfe20 and <= 0xfe2f ||
            value is >= 0xe0100 and <= 0xe01ef)
        {
            return 0;
        }

        return value switch
        {
            >= 0x1100 and <= 0x115f => 2,
            >= 0x2329 and <= 0x232a => 2,
            >= 0x2e80 and <= 0xa4cf => 2,
            >= 0xac00 and <= 0xd7a3 => 2,
            >= 0xf900 and <= 0xfaff => 2,
            >= 0xfe10 and <= 0xfe19 => 2,
            >= 0xfe30 and <= 0xfe6f => 2,
            >= 0xff00 and <= 0xff60 => 2,
            >= 0xffe0 and <= 0xffe6 => 2,
            >= 0x1f300 and <= 0x1faff => 2,
            _ => 1
        };
    }
}
