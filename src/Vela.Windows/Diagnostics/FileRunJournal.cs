using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Vela.Core.Contracts;
using Vela.Core.Models;

namespace Vela.Windows.Diagnostics;

public sealed class FileRunJournal : IRunJournal
{
    private const int ReadRetryCount = 3;
    private static readonly TimeSpan ReadRetryDelay = TimeSpan.FromMilliseconds(20);
    private static readonly Encoding Utf8WithoutBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly SemaphoreSlim _appendLock = new(1, 1);
    private readonly AppPaths _paths;

    public FileRunJournal(AppPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _paths = paths;
    }

    public async Task<JournalOperationResult> CreateRunAsync(
        Guid runId,
        CancellationToken cancellationToken)
    {
        if (runId == Guid.Empty)
        {
            return JournalOperationResult.Failure();
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var runDirectory = _paths.GetRunDirectory(runId);
            if (!_paths.IsTrustedRootDirectory() ||
                !_paths.IsTrustedLogsDirectory() ||
                !_paths.IsTrustedRunDirectory(runId))
            {
                return JournalOperationResult.Failure();
            }

            if (Directory.Exists(runDirectory))
            {
                return JournalOperationResult.Failure();
            }

            Directory.CreateDirectory(runDirectory);
            var runCreated = await AppendAsync(
                new RunEventDraft(
                    DateTimeOffset.UtcNow,
                    runId,
                    RunPhase.Validation,
                    RunEventLevel.Information,
                    "RunCreated",
                    ImmutableArray<string>.Empty,
                    ExitCode: null,
                    Duration: null,
                    Output: null),
                cancellationToken).ConfigureAwait(false);

            return runCreated.Succeeded
                ? JournalOperationResult.Success(runDirectory)
                : JournalOperationResult.Failure();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return JournalOperationResult.Failure();
        }
    }

    public async Task<JournalOperationResult> OpenExistingRunAsync(
        Guid runId,
        CancellationToken cancellationToken)
    {
        if (runId == Guid.Empty)
        {
            return JournalOperationResult.Failure();
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var runDirectory = _paths.GetRunDirectory(runId);
            if (!_paths.IsExpectedRunDirectory(runId, runDirectory) ||
                !_paths.IsTrustedRootDirectory() ||
                !_paths.IsTrustedLogsDirectory() ||
                !_paths.IsTrustedRunDirectory(runId) ||
                !Directory.Exists(runDirectory) ||
                !File.Exists(_paths.GetEventsFilePath(runId)))
            {
                return JournalOperationResult.Failure();
            }

            var events = await ReadCompleteEventsAsync(
                    _paths.GetEventsFilePath(runId),
                    cancellationToken)
                .ConfigureAwait(false);
            var first = events.IsDefaultOrEmpty ? null : events[0];
            return first is not null &&
                   first.RunId == runId &&
                   first.Sequence == 1 &&
                   string.Equals(first.OperationName, "RunCreated", StringComparison.Ordinal)
                ? JournalOperationResult.Success(runDirectory)
                : JournalOperationResult.Failure();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return JournalOperationResult.Failure();
        }
    }
    public async Task<JournalAppendResult> AppendAsync(
        RunEventDraft eventDraft,
        CancellationToken cancellationToken)
    {
        if (eventDraft is null || eventDraft.RunId == Guid.Empty)
        {
            return JournalAppendResult.Failure();
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var runDirectory = _paths.GetRunDirectory(eventDraft.RunId);
            if (!_paths.IsTrustedRunDirectory(eventDraft.RunId) ||
                !_paths.IsTrustedPath(_paths.GetEventsFilePath(eventDraft.RunId)) ||
                !_paths.IsTrustedPath(_paths.GetRunLogFilePath(eventDraft.RunId)) ||
                !Directory.Exists(runDirectory))
            {
                return JournalAppendResult.Failure();
            }

            await _appendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var existingEvents = await ReadCompleteEventsAsync(
                    _paths.GetEventsFilePath(eventDraft.RunId),
                    cancellationToken).ConfigureAwait(false);
                var sequence = existingEvents.IsDefaultOrEmpty
                    ? 1
                    : checked(existingEvents.Max(static @event => @event.Sequence) + 1);
                var @event = new RunEvent(
                    sequence,
                    eventDraft.OccurredAtUtc,
                    eventDraft.RunId,
                    eventDraft.Phase,
                    eventDraft.Level,
                    eventDraft.OperationName,
                    eventDraft.Arguments,
                    eventDraft.ExitCode,
                    eventDraft.Duration,
                    eventDraft.Output);

                await AppendEventAsync(
                    _paths.GetEventsFilePath(eventDraft.RunId),
                    @event,
                    cancellationToken).ConfigureAwait(false);
                await AppendLogAsync(
                    _paths.GetRunLogFilePath(eventDraft.RunId),
                    @event,
                    cancellationToken).ConfigureAwait(false);

                return JournalAppendResult.Success(@event);
            }
            finally
            {
                _appendLock.Release();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return JournalAppendResult.Failure();
        }
    }

    public async Task<JournalOperationResult> WriteSummaryAsync(
        RunSummary summary,
        CancellationToken cancellationToken)
    {
        if (summary is null || summary.RunId == Guid.Empty)
        {
            return JournalOperationResult.Failure();
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var runDirectory = _paths.GetRunDirectory(summary.RunId);
            if (!_paths.IsTrustedRunDirectory(summary.RunId) ||
                !_paths.IsTrustedPath(_paths.GetSummaryFilePath(summary.RunId)) ||
                !_paths.IsTrustedPath(_paths.GetSummaryTemporaryFilePath(summary.RunId)) ||
                !Directory.Exists(runDirectory))
            {
                return JournalOperationResult.Failure();
            }

            var temporaryPath = _paths.GetSummaryTemporaryFilePath(summary.RunId);
            try
            {
                await WriteJsonAsync(temporaryPath, summary, cancellationToken).ConfigureAwait(false);
                ReplaceAtomically(temporaryPath, _paths.GetSummaryFilePath(summary.RunId));
            }
            finally
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }

            return JournalOperationResult.Success(runDirectory);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return JournalOperationResult.Failure();
        }
    }

    public async Task<JournalReadResult> ReadEventsAsync(
        Guid runId,
        long afterSequence,
        CancellationToken cancellationToken)
    {
        if (runId == Guid.Empty)
        {
            return new JournalReadResult(ImmutableArray<RunEvent>.Empty);
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_paths.IsTrustedRunDirectory(runId) ||
                !_paths.IsTrustedPath(_paths.GetEventsFilePath(runId)))
            {
                return new JournalReadResult(ImmutableArray<RunEvent>.Empty);
            }

            var events = await ReadCompleteEventsAsync(
                _paths.GetEventsFilePath(runId),
                cancellationToken).ConfigureAwait(false);

            return new JournalReadResult(events
                .Where(@event => @event.Sequence > afterSequence)
                .ToImmutableArray());
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return new JournalReadResult(ImmutableArray<RunEvent>.Empty);
        }
    }

    public Task<int> CleanupExpiredRunsAsync(
        int retentionDays,
        Guid? activeRunId,
        CancellationToken cancellationToken)
    {
        if (retentionDays < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(retentionDays));
        }

        cancellationToken.ThrowIfCancellationRequested();

        if (!Directory.Exists(_paths.LogsDirectoryPath))
        {
            return Task.FromResult(0);
        }

        var cutoffUtc = DateTime.UtcNow.AddDays(-retentionDays);
        var deletedRunCount = 0;

        foreach (var runDirectory in Directory.EnumerateDirectories(_paths.LogsDirectoryPath))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var directoryName = Path.GetFileName(runDirectory);
            if (!Guid.TryParseExact(directoryName, "D", out var runId) ||
                activeRunId == runId ||
                Directory.GetLastWriteTimeUtc(runDirectory) >= cutoffUtc)
            {
                continue;
            }

            Directory.Delete(runDirectory, recursive: true);
            deletedRunCount++;
        }

        return Task.FromResult(deletedRunCount);
    }

    private static async Task AppendEventAsync(
        string eventFilePath,
        RunEvent @event,
        CancellationToken cancellationToken)
    {
        var eventLine = JsonSerializer.Serialize(@event, SerializerOptions) + "\n";
        var encodedEvent = Utf8WithoutBom.GetBytes(eventLine);

        await using var stream = new FileStream(
            eventFilePath,
            FileMode.Append,
            FileAccess.Write,
            FileShare.Read,
            bufferSize: 4096,
            useAsync: true);
        await stream.WriteAsync(encodedEvent, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        stream.Flush(flushToDisk: true);
    }

    private static async Task AppendLogAsync(
        string logFilePath,
        RunEvent @event,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            logFilePath,
            FileMode.Append,
            FileAccess.Write,
            FileShare.Read,
            bufferSize: 4096,
            useAsync: true);
        await using var writer = new StreamWriter(stream, Utf8WithoutBom, leaveOpen: true);
        var output = @event.Output?
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal);
        await writer.WriteLineAsync(
            $"[{@event.Sequence}] {@event.OccurredAtUtc:O} {@event.Level} {@event.Phase} {@event.OperationName} {output}")
            .ConfigureAwait(false);
        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        stream.Flush(flushToDisk: true);
    }

    private static async Task<ImmutableArray<RunEvent>> ReadCompleteEventsAsync(
        string eventFilePath,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(eventFilePath))
        {
            return ImmutableArray<RunEvent>.Empty;
        }

        for (var attempt = 0; attempt < ReadRetryCount; attempt++)
        {
            try
            {
                await using var stream = new FileStream(
                    eventFilePath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite,
                    bufferSize: 4096,
                    useAsync: true);
                using var reader = new StreamReader(stream, Utf8WithoutBom, detectEncodingFromByteOrderMarks: true);
                var content = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
                return ParseCompleteEvents(content);
            }
            catch (IOException) when (attempt < ReadRetryCount - 1)
            {
                await Task.Delay(ReadRetryDelay, cancellationToken).ConfigureAwait(false);
            }
        }

        return ImmutableArray<RunEvent>.Empty;
    }

    private static ImmutableArray<RunEvent> ParseCompleteEvents(string content)
    {
        var finalNewlineIndex = content.LastIndexOf('\n');
        if (finalNewlineIndex < 0)
        {
            return ImmutableArray<RunEvent>.Empty;
        }

        var events = ImmutableArray.CreateBuilder<RunEvent>();
        foreach (var rawLine in content[..(finalNewlineIndex + 1)].Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            try
            {
                var @event = JsonSerializer.Deserialize<RunEvent>(line, SerializerOptions);
                if (@event is not null)
                {
                    events.Add(@event);
                }
            }
            catch (JsonException)
            {
                // A malformed complete line is not a recoverable event and is skipped by the polling reader.
            }
        }

        return events.ToImmutable();
    }

    private static async Task WriteJsonAsync<T>(
        string path,
        T value,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 4096,
            useAsync: true);
        await JsonSerializer.SerializeAsync(stream, value, SerializerOptions, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        stream.Flush(flushToDisk: true);
    }

    private static void ReplaceAtomically(string temporaryPath, string destinationPath)
    {
        if (File.Exists(destinationPath))
        {
            File.Replace(temporaryPath, destinationPath, destinationBackupFileName: null);
            return;
        }

        File.Move(temporaryPath, destinationPath);
    }
}
