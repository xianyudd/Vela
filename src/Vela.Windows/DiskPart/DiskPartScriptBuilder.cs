using System.Text;

namespace Vela.Windows.DiskPart;

public sealed class DiskPartScriptBuilder
{
    private static readonly Encoding Ascii = Encoding.ASCII;

    public string BuildDetailScript(string validatedVhdxPath) =>
        BuildScript(validatedVhdxPath, "detail vdisk");

    public string BuildCompactScript(string validatedVhdxPath) =>
        BuildScript(validatedVhdxPath, "compact vdisk");

    public string BuildDetail(string validatedVhdxPath) => BuildDetailScript(validatedVhdxPath);

    public string BuildCompact(string validatedVhdxPath) => BuildCompactScript(validatedVhdxPath);

    public byte[] GetAsciiBytes(string script)
    {
        ArgumentNullException.ThrowIfNull(script);
        if (script.Any(static character => character > '\x7f'))
        {
            throw new ArgumentException("DiskPart scripts must contain only ASCII characters.", nameof(script));
        }

        return Ascii.GetBytes(script);
    }

    private static string BuildScript(string validatedVhdxPath, string operation)
    {
        ValidateVhdxPath(validatedVhdxPath);
        return string.Join(
                   "\r\n",
                   $"select vdisk file=\"{validatedVhdxPath}\"",
                   operation,
                   "exit") +
               "\r\n";
    }

    private static void ValidateVhdxPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) ||
            !IsWindowsAbsolutePath(path) ||
            !path.EndsWith(".vhdx", StringComparison.OrdinalIgnoreCase) ||
            path.Any(char.IsControl) ||
            path.Any(static character => character > '\x7f') ||
            path.Contains('"', StringComparison.Ordinal) ||
            path.Contains('<', StringComparison.Ordinal) ||
            path.Contains('>', StringComparison.Ordinal) ||
            path.Contains('|', StringComparison.Ordinal) ||
            path.Contains('?', StringComparison.Ordinal) ||
            path.Contains('*', StringComparison.Ordinal) ||
            ContainsInvalidColon(path))
        {
            throw new ArgumentException(
                "The VHDX path must be an absolute ASCII Windows .vhdx path without control characters.",
                nameof(path));
        }
    }

    private static bool IsWindowsAbsolutePath(string path) =>
        IsDriveRootedPath(path) || IsUncPath(path);

    private static bool IsDriveRootedPath(string path) =>
        path.Length >= 3 &&
        IsAsciiLetter(path[0]) &&
        path[1] == ':' &&
        path[2] is '\\' or '/';

    private static bool IsUncPath(string path)
    {
        if (path.Length < 5 || path[0] != '\\' || path[1] != '\\')
        {
            return false;
        }

        var serverSeparator = path.IndexOf('\\', 2);
        if (serverSeparator <= 2)
        {
            return false;
        }

        var shareStart = serverSeparator + 1;
        var shareSeparator = path.IndexOf('\\', shareStart);
        return shareSeparator > shareStart && shareSeparator < path.Length - 1;
    }

    private static bool ContainsInvalidColon(string path) =>
        path.Select((character, index) => (character, index))
            .Any(item => item.character == ':' &&
                         (item.index != 1 || !IsAsciiLetter(path[0])));

    private static bool IsAsciiLetter(char character) =>
        character is >= 'A' and <= 'Z' or >= 'a' and <= 'z';
}
