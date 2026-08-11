using System.Collections.Immutable;
using System.Text;
using Vela.Core.Models;
using Vela.Windows.Diagnostics;

namespace Vela.Tui.Application;

public interface IRunLogReader
{
    Task<RunLogSnapshot> ReadLatestAsync(
        int maxLines = 20,
        CancellationToken cancellationToken = default);
}

public sealed record RunLogSnapshot(
    ImmutableArray<RunLogLine> Lines,
    bool WasTailTruncated,
    string? ErrorMessage);

public sealed record RunLogLine(string Text, RunEventLevel Level);

/// <summary>Reads a bounded tail from the newest trusted Vela run log.</summary>
public sealed class RunLogReader : IRunLogReader
{
    private const int MaximumTailBytes = 64 * 1024;
    private readonly AppPaths _paths;

    public RunLogReader(AppPaths paths) =>
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));

    public async Task<RunLogSnapshot> ReadLatestAsync(
        int maxLines = 20,
        CancellationToken cancellationToken = default)
    {
        if (maxLines is < 1 or > 200)
        {
            throw new ArgumentOutOfRangeException(nameof(maxLines));
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (!_paths.IsTrustedRootDirectory() || !_paths.IsTrustedLogsDirectory())
        {
            return Empty("日志目录不受信任。");
        }
        if (!Directory.Exists(_paths.LogsDirectoryPath))
        {
            return Empty("尚无可读取的运行日志。");
        }

        try
        {
            var candidate = Directory.EnumerateDirectories(_paths.LogsDirectoryPath)
                .Select(TryCreateCandidate)
                .Where(static item => item is not null)
                .OrderByDescending(static item => item!.LastWriteUtc)
                .FirstOrDefault();
            return candidate is null
                ? Empty("尚无可读取的运行日志。")
                : await ReadTailAsync(candidate.LogPath, maxLines, cancellationToken)
                    .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return Empty("运行日志读取失败。");
        }
    }

    public async Task<RunLogSnapshot> ReadAsync(
        Guid runId,
        int maxLines = 40,
        CancellationToken cancellationToken = default)
    {
        if (runId == Guid.Empty)
        {
            return Empty("运行记录标识无效。");
        }

        if (maxLines is < 1 or > 200)
        {
            throw new ArgumentOutOfRangeException(nameof(maxLines));
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (!_paths.IsTrustedRootDirectory() || !_paths.IsTrustedLogsDirectory())
        {
            return Empty("日志目录不受信任。");
        }

        var runDirectory = _paths.GetRunDirectory(runId);
        if (!_paths.IsExpectedRunDirectory(runId, runDirectory) ||
            !_paths.IsTrustedRunDirectory(runId) ||
            !Directory.Exists(runDirectory))
        {
            return Empty("运行记录不可用。");
        }

        var logPath = _paths.GetRunLogFilePath(runId);
        if (!_paths.IsTrustedPath(logPath) || !File.Exists(logPath))
        {
            return Empty("该运行没有可读取的日志。");
        }

        try
        {
            return await ReadTailAsync(logPath, maxLines, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return Empty("运行日志读取失败。");
        }
    }

    private LogCandidate? TryCreateCandidate(string directory)
    {
        var name = Path.GetFileName(directory);
        if (!Guid.TryParseExact(name, "D", out var runId) ||
            !_paths.IsExpectedRunDirectory(runId, directory) ||
            !_paths.IsTrustedRunDirectory(runId))
        {
            return null;
        }

        var logPath = _paths.GetRunLogFilePath(runId);
        return _paths.IsTrustedPath(logPath) && File.Exists(logPath)
            ? new LogCandidate(logPath, File.GetLastWriteTimeUtc(logPath))
            : null;
    }

    private static async Task<RunLogSnapshot> ReadTailAsync(
        string path,
        int maxLines,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 4096,
            options: FileOptions.Asynchronous | FileOptions.SequentialScan);
        var offset = Math.Max(0, stream.Length - MaximumTailBytes);
        stream.Seek(offset, SeekOrigin.Begin);
        using var reader = new StreamReader(
            stream,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false),
            detectEncodingFromByteOrderMarks: true,
            bufferSize: 4096,
            leaveOpen: false);
        if (offset > 0)
        {
            _ = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
        }

        var lines = new Queue<RunLogLine>(maxLines);
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            if (lines.Count == maxLines)
            {
                _ = lines.Dequeue();
            }
            lines.Enqueue(ProjectSafeLine(line));
        }

        return lines.Count == 0
            ? Empty("运行日志为空。")
            : new RunLogSnapshot(lines.ToImmutableArray(), offset > 0, ErrorMessage: null);
    }

    private static RunLogSnapshot Empty(string message) =>
        new(ImmutableArray<RunLogLine>.Empty, WasTailTruncated: false, message);

    /// <summary>
    /// Journal output may contain native command output and local paths. The TUI only needs
    /// sequence, timestamp, severity, phase and event name; the detail view keeps that
    /// projection inside the terminal.
    /// </summary>
    internal static RunLogLine ProjectSafeLine(string line)
    {
        var sanitized = TuiDisplayText.Sanitize(line, 160);
        var fields = sanitized.Split(' ', 6, StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length < 5 ||
            !TryParseSequence(fields[0], out var sequence) ||
            !DateTimeOffset.TryParse(
                fields[1],
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind,
                out _) ||
            !Enum.TryParse<RunEventLevel>(fields[2], ignoreCase: true, out var level) ||
            !Enum.TryParse<RunPhase>(fields[3], ignoreCase: true, out var phase))
        {
            return new RunLogLine("日志格式无效", RunEventLevel.Information);
        }

        var operation = TuiDisplayText.SafeToken(fields[4], 40, "未知事件");
        if (operation == "未知事件")
        {
            return new RunLogLine("日志格式无效", RunEventLevel.Information);
        }

        var projected = $"[{sequence}] {fields[1]} {level} {phase} {operation}";
        return new RunLogLine(projected, level);
    }

    private static bool TryParseSequence(string value, out long sequence)
    {
        sequence = 0;
        return value.Length >= 3 &&
               value[0] == '[' &&
               value[^1] == ']' &&
               long.TryParse(
                   value[1..^1],
                   System.Globalization.NumberStyles.None,
                   System.Globalization.CultureInfo.InvariantCulture,
                   out sequence) &&
               sequence > 0;
    }

    private sealed record LogCandidate(string LogPath, DateTime LastWriteUtc);
}
