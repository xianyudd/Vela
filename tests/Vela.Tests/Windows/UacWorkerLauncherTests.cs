using System.Collections.Immutable;
using System.ComponentModel;
using System.Diagnostics;
using Vela.Core.Contracts;
using Vela.Core.Models;
using Vela.Tests.Fakes;
using Vela.Windows.Elevation;

namespace Vela.Tests.Windows;

public sealed class UacWorkerLauncherTests
{
    [Fact]
    public void CurrentExecutablePathProvider_prefers_the_launchable_apphost_for_dll_invocations()
    {
        var entryAssemblyPath = System.Reflection.Assembly.GetEntryAssembly()?.Location;
        var expectedAppHost = string.IsNullOrWhiteSpace(entryAssemblyPath)
            ? null
            : Path.Combine(
                Path.GetDirectoryName(entryAssemblyPath) ?? string.Empty,
                Path.GetFileNameWithoutExtension(entryAssemblyPath) + ".exe");

        var actualPath = new CurrentExecutablePathProvider().GetExecutablePath();

        Assert.True(Path.IsPathFullyQualified(actualPath));
        if (expectedAppHost is not null && File.Exists(expectedAppHost))
        {
            Assert.Equal(expectedAppHost, actualPath);
        }
        else
        {
            Assert.True(File.Exists(actualPath));
        }
    }

    [Fact]
    public async Task LaunchAsync_UsesTheExactRunAsWorkerArgumentBoundaries()
    {
        var runId = Guid.Parse("7cf7f32d-1780-446d-91a1-5d18c8aa74a6");
        var starter = new RecordingProcessStarter();
        var launcher = new UacWorkerLauncher(
            new FixedExecutablePathProvider(@"D:\Vela\Vela.exe"),
            starter);

        var result = await launcher.LaunchAsync(runId, CancellationToken.None);

        Assert.Equal(ElevatedWorkerLaunchStatus.Started, result.Status);
        var startInfo = Assert.Single(starter.StartInfos);
        Assert.Equal(@"D:\Vela\Vela.exe", startInfo.FileName);
        Assert.True(startInfo.UseShellExecute);
        Assert.Equal("runas", startInfo.Verb);
        Assert.Equal(
            new[] { "--worker", "--run-id", runId.ToString("D") },
            startInfo.Arguments);
    }

    [Fact]
    public async Task LaunchAsync_adds_the_entry_dll_when_dotnet_is_the_host()
    {
        var runId = Guid.Parse("1e75ec82-3c9f-44e5-b9f7-f26b47c4d30d");
        var starter = new RecordingProcessStarter();
        var launcher = new UacWorkerLauncher(
            new FixedExecutablePathProvider(@"C:\Program Files\dotnet\dotnet.exe"),
            starter);

        await launcher.LaunchAsync(runId, CancellationToken.None);

        var startInfo = Assert.Single(starter.StartInfos);
        var entryAssemblyPath = System.Reflection.Assembly.GetEntryAssembly()?.Location;
        Assert.False(string.IsNullOrWhiteSpace(entryAssemblyPath));
        Assert.Equal(
            new[] { entryAssemblyPath!, "--worker", "--run-id", runId.ToString("D") },
            startInfo.Arguments);
    }

    [Fact]
    public async Task StartAsync_CreatesTheRunBeforePublishingRequestAndLaunching()
    {
        var trace = new List<string>();
        var request = CreateRequest();
        var journal = new FakeRunJournal(trace.Add);
        var store = new RecordingRequestStore(trace, OperationRequestWriteResult.Success(@"D:\Vela\pending\request.json"));
        var launcher = new RecordingLauncher(trace, ElevatedWorkerLaunchStatus.Started);
        var coordinator = new ElevatedOperationCoordinator(
            journal,
            store,
            launcher,
            new FixedClock());

        var result = await coordinator.StartAsync(request, CancellationToken.None);

        Assert.Equal(ElevatedOperationStartStatus.Started, result.Status);
        Assert.Equal(
            new[] { "journal.create", "store.write", "launcher.launch" },
            trace.Take(3));
        Assert.Equal(request.RunId, Assert.Single(journal.CreatedRunIds));
        Assert.Equal(request.RunId, Assert.Single(store.WrittenRunIds));
        Assert.Equal(request.RunId, Assert.Single(launcher.RunIds));
    }

    [Fact]
    public async Task StartAsync_TransfersGateLeaseToCallerUntilPollingCompletes()
    {
        using var root = TestRoot.Create();
        var paths = new Vela.Windows.Diagnostics.AppPaths(root.Path);
        var request = CreateRequest();
        var coordinator = new ElevatedOperationCoordinator(
            new FakeRunJournal(),
            new RecordingRequestStore([], OperationRequestWriteResult.Success(paths.GetPendingRequestFilePath(request.RunId))),
            new RecordingLauncher([], ElevatedWorkerLaunchStatus.Started),
            new FixedClock(),
            new CompactRunGate(paths));

        var result = await coordinator.StartAsync(request, CancellationToken.None);

        Assert.Equal(ElevatedOperationStartStatus.Started, result.Status);
        Assert.NotNull(result.GateLease);
        Assert.True(File.Exists(paths.CompactGateFilePath));

        result.GateLease!.Dispose();

        Assert.False(File.Exists(paths.CompactGateFilePath));
    }

    [Fact]
    public async Task StartAsync_releases_gate_when_cancelled_after_gate_acquisition()
    {
        using var root = TestRoot.Create();
        var paths = new Vela.Windows.Diagnostics.AppPaths(root.Path);
        var request = CreateRequest();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var coordinator = new ElevatedOperationCoordinator(
            new FakeRunJournal(),
            new RecordingRequestStore([], OperationRequestWriteResult.Success(paths.GetPendingRequestFilePath(request.RunId))),
            new RecordingLauncher([], ElevatedWorkerLaunchStatus.Started),
            new FixedClock(),
            new CompactRunGate(paths));

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            coordinator.StartAsync(request, cancellation.Token));

        Assert.False(File.Exists(paths.CompactGateFilePath));
    }

    [Fact]
    public async Task StartAsync_WhenUacIsCancelled_WritesCancelledSummaryThenConsumesPendingRequest()
    {
        var request = CreateRequest();
        var journal = new FakeRunJournal();
        var store = new RecordingRequestStore([], OperationRequestWriteResult.Success(@"D:\Vela\pending\request.json"));
        var coordinator = new ElevatedOperationCoordinator(
            journal,
            store,
            new RecordingLauncher([], ElevatedWorkerLaunchStatus.Cancelled),
            new FixedClock());

        var result = await coordinator.StartAsync(request, CancellationToken.None);

        Assert.Equal(ElevatedOperationStartStatus.Cancelled, result.Status);
        Assert.Equal(TerminalResult.CancelledBeforeElevation, result.TerminalResult);
        Assert.Contains(journal.Events, static @event => @event.OperationName == "UacCancelled" && @event.Phase == RunPhase.Elevation);
        Assert.Equal(TerminalResult.CancelledBeforeElevation, Assert.Single(journal.Summaries).TerminalResult);
        Assert.Equal(
            new[] { "append:UacCancelled", "summary" },
            journal.Operations.TakeLast(2));
        Assert.Equal(1, store.ConsumeCalls);
    }

    [Fact]
    public async Task StartAsync_WhenUacLaunchFails_WritesElevationErrorSummary()
    {
        var request = CreateRequest();
        var journal = new FakeRunJournal();
        var store = new RecordingRequestStore([], OperationRequestWriteResult.Success(@"D:\Vela\pending\request.json"));
        var coordinator = new ElevatedOperationCoordinator(
            journal,
            store,
            new RecordingLauncher([], ElevatedWorkerLaunchStatus.Failed),
            new FixedClock());

        var result = await coordinator.StartAsync(request, CancellationToken.None);

        Assert.Equal(ElevatedOperationStartStatus.Failed, result.Status);
        Assert.Equal(TerminalResult.WorkerInterrupted, result.TerminalResult);
        Assert.Contains(journal.Events, static @event => @event.OperationName == "UacLaunchFailed" && @event.Level == RunEventLevel.Error);
        Assert.Equal(TerminalResult.WorkerInterrupted, Assert.Single(journal.Summaries).TerminalResult);
        Assert.Equal(
            new[] { "append:UacLaunchFailed", "summary" },
            journal.Operations.TakeLast(2));
        Assert.Equal(1, store.ConsumeCalls);
    }

    [Fact]
    public async Task StartAsync_keeps_cancelled_status_when_summary_persistence_fails()
    {
        var request = CreateRequest();
        var journal = new FakeRunJournal
        {
            ThrowOnWriteSummary = true
        };
        var store = new RecordingRequestStore([], OperationRequestWriteResult.Success(@"D:\Vela\pending\request.json"));
        var coordinator = new ElevatedOperationCoordinator(
            journal,
            store,
            new RecordingLauncher([], ElevatedWorkerLaunchStatus.Cancelled),
            new FixedClock());

        var result = await coordinator.StartAsync(request, CancellationToken.None);

        Assert.Equal(ElevatedOperationStartStatus.Cancelled, result.Status);
        Assert.Equal(TerminalResult.CancelledBeforeElevation, result.TerminalResult);
        Assert.Contains(journal.Events, static @event => @event.OperationName == "UacCancelled");
        Assert.Equal(1, store.ConsumeCalls);
    }

    [Theory]
    [InlineData(1223, ElevatedWorkerLaunchStatus.Cancelled)]
    [InlineData(5, ElevatedWorkerLaunchStatus.Failed)]
    public async Task LaunchAsync_MapsUacExceptionsToDeterministicStatus(
        int nativeErrorCode,
        ElevatedWorkerLaunchStatus expectedStatus)
    {
        var launcher = new UacWorkerLauncher(
            new FixedExecutablePathProvider(@"D:\Vela\Vela.exe"),
            new ThrowingProcessStarter(new Win32Exception(nativeErrorCode)));

        var result = await launcher.LaunchAsync(
            Guid.Parse("44f3a14b-4f4c-415a-93a1-7050d0893713"),
            CancellationToken.None);

        Assert.Equal(expectedStatus, result.Status);
    }

    private static OperationRequest CreateRequest() =>
        new(
            Guid.Parse("776f6d4d-51cf-4cbe-bd3f-0605f673f4a5"),
            new Profile(
                Guid.Parse("26868c45-fd56-424b-9c75-47e1e998a563"),
                "Ubuntu 24.04 on D",
                "Ubuntu-24.04",
                @"D:\DevTools\WSL2\Ubuntu24.04\ext4.vhdx",
                ShutdownMode.Global,
                TimeSpan.FromSeconds(45)),
            OperationIntent.Compact);

    private sealed class TestRoot : IDisposable
    {
        private TestRoot(string path) => Path = path;

        public string Path { get; }

        public static TestRoot Create()
        {
            var path = System.IO.Path.Combine(
                FindRepositoryRoot(),
                "artifacts",
                "test-data",
                "uac-launcher-tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return new TestRoot(path);
        }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }

        private static string FindRepositoryRoot()
        {
            DirectoryInfo? directory = new(AppContext.BaseDirectory);
            while (directory is not null)
            {
                if (File.Exists(System.IO.Path.Combine(directory.FullName, "Vela.sln")))
                {
                    return directory.FullName;
                }

                directory = directory.Parent;
            }

            throw new DirectoryNotFoundException("The Vela repository root was not found.");
        }
    }

    private sealed class FixedExecutablePathProvider : IExecutablePathProvider
    {
        private readonly string _path;

        public FixedExecutablePathProvider(string path)
        {
            _path = path;
        }

        public string GetExecutablePath() => _path;
    }

    private sealed class RecordingProcessStarter : IUacProcessStarter
    {
        private readonly List<CapturedProcessStartInfo> _startInfos = [];

        public ImmutableArray<CapturedProcessStartInfo> StartInfos => _startInfos.ToImmutableArray();

        public void Start(ProcessStartInfo startInfo)
        {
            _startInfos.Add(new CapturedProcessStartInfo(
                startInfo.FileName,
                startInfo.UseShellExecute,
                startInfo.Verb,
                startInfo.ArgumentList.ToImmutableArray()));
        }
    }

    private sealed class ThrowingProcessStarter : IUacProcessStarter
    {
        private readonly Exception _exception;

        public ThrowingProcessStarter(Exception exception)
        {
            _exception = exception;
        }

        public void Start(ProcessStartInfo startInfo) => throw _exception;
    }

    private sealed record CapturedProcessStartInfo(
        string FileName,
        bool UseShellExecute,
        string Verb,
        ImmutableArray<string> Arguments);

    private sealed class RecordingLauncher : IElevatedWorkerLauncher
    {
        private readonly List<string> _trace;
        private readonly ElevatedWorkerLaunchStatus _status;
        private readonly List<Guid> _runIds = [];

        public RecordingLauncher(List<string> trace, ElevatedWorkerLaunchStatus status)
        {
            _trace = trace;
            _status = status;
        }

        public ImmutableArray<Guid> RunIds => _runIds.ToImmutableArray();

        public Task<ElevatedWorkerLaunchResult> LaunchAsync(Guid runId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _trace.Add("launcher.launch");
            _runIds.Add(runId);
            return Task.FromResult(new ElevatedWorkerLaunchResult(_status));
        }
    }

    private sealed class RecordingRequestStore : IOperationRequestStore
    {
        private readonly List<string> _trace;
        private readonly OperationRequestWriteResult _writeResult;
        private readonly List<Guid> _writtenRunIds = [];

        public RecordingRequestStore(List<string> trace, OperationRequestWriteResult writeResult)
        {
            _trace = trace;
            _writeResult = writeResult;
        }

        public int ConsumeCalls { get; private set; }

        public ImmutableArray<Guid> WrittenRunIds => _writtenRunIds.ToImmutableArray();

        public Task<OperationRequestWriteResult> WriteAsync(OperationRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _trace.Add("store.write");
            _writtenRunIds.Add(request.RunId);
            return Task.FromResult(_writeResult);
        }

        public Task<OperationRequestReadResult> ReadAsync(Guid expectedRunId, CancellationToken cancellationToken) =>
            Task.FromResult(OperationRequestReadResult.Failure());

        public Task<OperationRequestConsumeResult> ConsumeAsync(Guid expectedRunId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ConsumeCalls++;
            return Task.FromResult(OperationRequestConsumeResult.Success());
        }
    }
}
