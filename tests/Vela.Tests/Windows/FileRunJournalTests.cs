using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using Vela.Core.Contracts;
using Vela.Core.Models;
using Vela.Windows.Diagnostics;

namespace Vela.Tests.Windows;

public sealed class FileRunJournalTests
{
    [Fact]
    public async Task CreateRunAsync_DerivesRunDirectoryAndWritesRunCreatedEventAndLog()
    {
        using var testRoot = JournalTestRoot.Create();
        var paths = new AppPaths(testRoot.RootDirectory);
        var journal = new FileRunJournal(paths);
        var runId = Guid.NewGuid();

        var creation = await journal.CreateRunAsync(runId, CancellationToken.None);
        var events = await journal.ReadEventsAsync(runId, afterSequence: 0, CancellationToken.None);

        Assert.True(creation.Succeeded);
        Assert.Equal(paths.GetRunDirectory(runId), creation.RunDirectory);
        Assert.True(Directory.Exists(paths.GetRunDirectory(runId)));
        Assert.True(File.Exists(paths.GetEventsFilePath(runId)));
        Assert.True(File.Exists(paths.GetRunLogFilePath(runId)));
        var runCreated = Assert.Single(events.Events);
        Assert.Equal(1, runCreated.Sequence);
        Assert.Equal(runId, runCreated.RunId);
        Assert.Equal("RunCreated", runCreated.OperationName);
        Assert.Contains("RunCreated", await File.ReadAllTextAsync(paths.GetRunLogFilePath(runId)));
    }

    [Fact]
    public async Task OpenExistingRunAsync_ContinuesNdjsonSequenceWithoutWritingAnotherRunCreatedEvent()
    {
        using var testRoot = JournalTestRoot.Create();
        var paths = new AppPaths(testRoot.RootDirectory);
        var parentJournal = new FileRunJournal(paths);
        var workerJournal = new FileRunJournal(paths);
        var runId = Guid.NewGuid();

        Assert.True((await parentJournal.CreateRunAsync(runId, CancellationToken.None)).Succeeded);
        var parentAppend = await parentJournal.AppendAsync(
            CreateDraft(runId, RunPhase.Inventory, "InstalledInventory"),
            CancellationToken.None);
        var opened = await workerJournal.OpenExistingRunAsync(runId, CancellationToken.None);
        var workerAppend = await workerJournal.AppendAsync(
            CreateDraft(runId, RunPhase.Snapshot, "VhdxSnapshot"),
            CancellationToken.None);
        var events = await workerJournal.ReadEventsAsync(runId, afterSequence: 0, CancellationToken.None);

        Assert.True(parentAppend.Succeeded);
        Assert.True(opened.Succeeded);
        Assert.True(workerAppend.Succeeded);
        Assert.Equal(new long[] { 1, 2, 3 }, events.Events.Select(static item => item.Sequence));
        Assert.Equal(1, events.Events.Count(static item => item.OperationName == "RunCreated"));
        Assert.Equal("VhdxSnapshot", events.Events[^1].OperationName);
    }

    [Fact]
    public async Task WriteSummaryAsync_WritesValidSummaryUsingRunDerivedPath()
    {
        using var testRoot = JournalTestRoot.Create();
        var paths = new AppPaths(testRoot.RootDirectory);
        var journal = new FileRunJournal(paths);
        var runId = Guid.NewGuid();
        Assert.True((await journal.CreateRunAsync(runId, CancellationToken.None)).Succeeded);
        var summary = new RunSummary(
            runId,
            CreateProfile(),
            OperationIntent.Preflight,
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch.AddSeconds(1),
            BeforeSnapshot: null,
            AfterSnapshot: null,
            TerminalResult.Succeeded);

        var result = await journal.WriteSummaryAsync(summary, CancellationToken.None);
        var replacementResult = await journal.WriteSummaryAsync(
            summary with
            {
                TerminalResult = TerminalResult.CompletedWithNoReclaim
            },
            CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.True(replacementResult.Succeeded);
        Assert.Equal(paths.GetRunDirectory(runId), result.RunDirectory);
        Assert.True(File.Exists(paths.GetSummaryFilePath(runId)));
        Assert.False(File.Exists(paths.GetSummaryTemporaryFilePath(runId)));
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(paths.GetSummaryFilePath(runId)));
        Assert.Equal(runId, document.RootElement.GetProperty("runId").GetGuid());
        Assert.Equal("CompletedWithNoReclaim", document.RootElement.GetProperty("terminalResult").GetString());
    }

    [Fact]
    public async Task CleanupExpiredRunsAsync_PreservesActiveRunId()
    {
        using var testRoot = JournalTestRoot.Create();
        var paths = new AppPaths(testRoot.RootDirectory);
        var journal = new FileRunJournal(paths);
        var activeRunId = Guid.NewGuid();
        var expiredRunId = Guid.NewGuid();
        Assert.True((await journal.CreateRunAsync(activeRunId, CancellationToken.None)).Succeeded);
        Assert.True((await journal.CreateRunAsync(expiredRunId, CancellationToken.None)).Succeeded);
        var expiredTimestamp = DateTime.UtcNow.AddDays(-10);
        Directory.SetLastWriteTimeUtc(paths.GetRunDirectory(activeRunId), expiredTimestamp);
        Directory.SetLastWriteTimeUtc(paths.GetRunDirectory(expiredRunId), expiredTimestamp);

        var deletedRunCount = await journal.CleanupExpiredRunsAsync(
            retentionDays: 1,
            activeRunId,
            CancellationToken.None);

        Assert.Equal(1, deletedRunCount);
        Assert.True(Directory.Exists(paths.GetRunDirectory(activeRunId)));
        Assert.False(Directory.Exists(paths.GetRunDirectory(expiredRunId)));
    }

    [Fact]
    public async Task ReadEventsAsync_OnlyReturnsCompleteLinesWhileWriterKeepsFileOpen()
    {
        using var testRoot = JournalTestRoot.Create();
        var paths = new AppPaths(testRoot.RootDirectory);
        var journal = new FileRunJournal(paths);
        var runId = Guid.NewGuid();
        Assert.True((await journal.CreateRunAsync(runId, CancellationToken.None)).Succeeded);
        var workerEvent = new RunEvent(
            Sequence: 2,
            DateTimeOffset.UnixEpoch.AddMinutes(1),
            runId,
            RunPhase.Inventory,
            RunEventLevel.Information,
            "WorkerInventory",
            ImmutableArray<string>.Empty,
            ExitCode: 0,
            Duration: TimeSpan.FromMilliseconds(1),
            Output: "worker evidence");
        var serializedEvent = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(workerEvent) + "\n");
        var splitIndex = serializedEvent.Length / 2;

        await using (var writer = new FileStream(
            paths.GetEventsFilePath(runId),
            FileMode.Append,
            FileAccess.Write,
            FileShare.Read,
            bufferSize: 4096,
            useAsync: true))
        {
            await writer.WriteAsync(serializedEvent.AsMemory(0, splitIndex));
            await writer.FlushAsync();
            writer.Flush(flushToDisk: true);

            var partialRead = await journal.ReadEventsAsync(runId, afterSequence: 1, CancellationToken.None);
            Assert.Empty(partialRead.Events);

            await writer.WriteAsync(serializedEvent.AsMemory(splitIndex));
            await writer.FlushAsync();
            writer.Flush(flushToDisk: true);

            var completeRead = await journal.ReadEventsAsync(runId, afterSequence: 1, CancellationToken.None);
            var completedEvent = Assert.Single(completeRead.Events);
            Assert.Equal(2, completedEvent.Sequence);
            Assert.Equal("WorkerInventory", completedEvent.OperationName);
        }
    }

    private static RunEventDraft CreateDraft(Guid runId, RunPhase phase, string operationName) =>
        new(
            DateTimeOffset.UtcNow,
            runId,
            phase,
            RunEventLevel.Information,
            operationName,
            ImmutableArray<string>.Empty,
            ExitCode: null,
            Duration: null,
            Output: null);

    private static Profile CreateProfile() =>
        new(
            Guid.Parse("c411dd1a-d72b-4f32-9a4f-4de0602699a1"),
            "Ubuntu 24.04 on D",
            "Ubuntu-24.04",
            @"D:\DevTools\WSL2\Ubuntu24.04\ext4.vhdx",
            ShutdownMode.Global,
            TimeSpan.FromSeconds(45));

    private sealed class JournalTestRoot : IDisposable
    {
        private JournalTestRoot(string rootDirectory)
        {
            RootDirectory = rootDirectory;
        }

        public string RootDirectory { get; }

        public static JournalTestRoot Create()
        {
            var rootDirectory = Path.Combine(
                FindRepositoryRoot(),
                "artifacts",
                "test-data",
                "file-run-journal-tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(rootDirectory);
            return new JournalTestRoot(rootDirectory);
        }

        public void Dispose()
        {
            if (Directory.Exists(RootDirectory))
            {
                Directory.Delete(RootDirectory, recursive: true);
            }
        }
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Vela.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("The Vela repository root was not found.");
    }
}
