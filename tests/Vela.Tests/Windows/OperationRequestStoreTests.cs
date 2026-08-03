using System.Collections.Immutable;
using Vela.Core.Contracts;
using Vela.Core.Models;
using Vela.Windows.Diagnostics;
using Vela.Windows.Elevation;

namespace Vela.Tests.Windows;

public sealed class OperationRequestStoreTests
{
    [Fact]
    public async Task WriteAsync_RequiresRunCreatedBeforePublishingThePendingRequest()
    {
        using var testRoot = TestRoot.Create();
        var paths = new AppPaths(testRoot.RootDirectory);
        var store = new OperationRequestStore(paths);
        var request = CreateRequest();

        var beforeRun = await store.WriteAsync(request, CancellationToken.None);

        Assert.False(beforeRun.Succeeded);
        Assert.False(File.Exists(paths.GetPendingRequestFilePath(request.RunId)));

        var journal = new FileRunJournal(paths);
        var created = await journal.CreateRunAsync(request.RunId, CancellationToken.None);
        var eventsBeforePending = await journal.ReadEventsAsync(request.RunId, afterSequence: 0, CancellationToken.None);

        var afterRun = await store.WriteAsync(request, CancellationToken.None);

        Assert.True(created.Succeeded);
        Assert.Single(eventsBeforePending.Events);
        Assert.Equal("RunCreated", eventsBeforePending.Events[0].OperationName);
        Assert.True(afterRun.Succeeded);
        Assert.Equal(paths.GetPendingRequestFilePath(request.RunId), afterRun.RequestPath);
        Assert.True(File.Exists(paths.GetPendingRequestFilePath(request.RunId)));
    }

    [Fact]
    public async Task WriteAsync_PublishesAtomicJsonThenReadAndConsumeRoundTrip()
    {
        using var testRoot = TestRoot.Create();
        var paths = new AppPaths(testRoot.RootDirectory);
        var journal = new FileRunJournal(paths);
        var store = new OperationRequestStore(paths);
        var request = CreateRequest();
        await journal.CreateRunAsync(request.RunId, CancellationToken.None);

        var written = await store.WriteAsync(request, CancellationToken.None);
        var read = await store.ReadAsync(request.RunId, CancellationToken.None);
        var consumed = await store.ConsumeAsync(request.RunId, CancellationToken.None);

        Assert.True(written.Succeeded);
        Assert.False(File.Exists(paths.GetPendingRequestTemporaryFilePath(request.RunId)));
        Assert.True(read.Succeeded);
        Assert.Equal(request, read.Request);
        Assert.Equal(paths.GetPendingRequestFilePath(request.RunId), read.SourcePath);
        Assert.True(consumed.Succeeded);
        Assert.False(File.Exists(paths.GetPendingRequestFilePath(request.RunId)));
    }

    [Fact]
    public void AppPaths_OnlyAcceptsTheExactDerivedPendingRequestPath()
    {
        using var testRoot = TestRoot.Create();
        var paths = new AppPaths(testRoot.RootDirectory);
        var runId = Guid.Parse("e7621d0f-ead3-4760-b44e-e8c8e77bb1f2");

        Assert.True(paths.IsExpectedPendingRequestPath(runId, paths.GetPendingRequestFilePath(runId)));
        Assert.False(paths.IsExpectedPendingRequestPath(
            runId,
            Path.Combine(paths.RootDirectory + "-other", "pending", $"{runId:D}.json")));
        Assert.False(paths.IsExpectedPendingRequestPath(
            runId,
            Path.Combine(paths.PendingDirectoryPath, "..", "outside.json")));
    }

    [Fact]
    public async Task ParentAndWorkerJournals_ShareCompleteEventsAndIgnoreAnUnterminatedLine()
    {
        using var testRoot = TestRoot.Create();
        var paths = new AppPaths(testRoot.RootDirectory);
        var parentJournal = new FileRunJournal(paths);
        var workerJournal = new FileRunJournal(paths);
        var runId = Guid.Parse("6475b62b-831e-469f-9586-796247d48f1f");

        await parentJournal.CreateRunAsync(runId, CancellationToken.None);
        var opened = await workerJournal.OpenExistingRunAsync(runId, CancellationToken.None);
        var appended = await workerJournal.AppendAsync(
            new RunEventDraft(
                DateTimeOffset.UnixEpoch,
                runId,
                RunPhase.Elevation,
                RunEventLevel.Information,
                "WorkerAttached",
                ImmutableArray<string>.Empty,
                ExitCode: null,
                Duration: null,
                Output: null),
            CancellationToken.None);
        var sharedEvents = await parentJournal.ReadEventsAsync(runId, afterSequence: 0, CancellationToken.None);

        await File.AppendAllTextAsync(paths.GetEventsFilePath(runId), """{"sequence":999""");
        var partialLineEvents = await parentJournal.ReadEventsAsync(
            runId,
            afterSequence: appended.Event!.Sequence,
            CancellationToken.None);

        Assert.True(opened.Succeeded);
        Assert.True(appended.Succeeded);
        Assert.Equal(new[] { "RunCreated", "WorkerAttached" }, sharedEvents.Events.Select(static @event => @event.OperationName));
        Assert.Empty(partialLineEvents.Events);
    }

    private static OperationRequest CreateRequest(Guid? runId = null) =>
        new(
            runId ?? Guid.Parse("4a26889e-2c04-47ea-9a24-950e108bb24f"),
            new Profile(
                Guid.Parse("fa3093d7-6c8b-44e8-a39c-8a8966ce68a8"),
                "Ubuntu 24.04 on D",
                "Ubuntu-24.04",
                @"D:\DevTools\WSL2\Ubuntu24.04\ext4.vhdx",
                ShutdownMode.Global,
                TimeSpan.FromSeconds(45)),
            OperationIntent.Compact);

    private sealed class TestRoot : IDisposable
    {
        private TestRoot(string rootDirectory)
        {
            RootDirectory = rootDirectory;
        }

        public string RootDirectory { get; }

        public static TestRoot Create()
        {
            var rootDirectory = Path.Combine(
                FindRepositoryRoot(),
                "artifacts",
                "test-data",
                "operation-request-store-tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(rootDirectory);
            return new TestRoot(rootDirectory);
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
