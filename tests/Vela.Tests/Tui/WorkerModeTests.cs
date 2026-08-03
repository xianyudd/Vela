using System.Collections.Immutable;
using Vela.Core.Contracts;
using Vela.Core.Models;
using Vela.Core.Workflows;
using Vela.Tests.Fakes;
using Vela.Tui.Menu;
using Vela.Tui.ProgramModes;
using Vela.Windows.Diagnostics;

namespace Vela.Tests.Tui;

public sealed class WorkerModeTests
{
    [Theory]
    [InlineData("--worker", "--run-id", "d5c06a7b-b696-4b8f-a65a-b8e5317f3832", true)]
    [InlineData("--worker", "--run-id", "d5c06a7bb6964b8fa65ab8e5317f3832", false)]
    public void Parse_OnlyAcceptsTheExactWorkerAndDFormatGuidArguments(
        string first,
        string second,
        string third,
        bool expectedValid)
    {
        var parsed = WorkerCommandLineParser.Parse([first, second, third]);

        Assert.True(parsed.IsWorkerInvocation);
        Assert.Equal(expectedValid, parsed.IsValid);
    }

    [Fact]
    public void Parse_RejectsExtraArguments()
    {
        var parsed = WorkerCommandLineParser.Parse(
            ["--worker", "--run-id", "d5c06a7b-b696-4b8f-a65a-b8e5317f3832", "extra"]);

        Assert.True(parsed.IsWorkerInvocation);
        Assert.False(parsed.IsValid);
    }

    [Theory]
    [InlineData(TerminalResult.Succeeded, 0)]
    [InlineData(TerminalResult.CompletedWithNoReclaim, 0)]
    [InlineData(TerminalResult.ValidationFailed, 2)]
    [InlineData(TerminalResult.ShutdownTimedOut, 3)]
    [InlineData(TerminalResult.DiskPartPreflightFailed, 4)]
    [InlineData(TerminalResult.DiskPartCompactFailed, 5)]
    [InlineData(TerminalResult.WorkerInterrupted, 10)]
    public void MapExitCode_UsesTheDocumentedTerminalResultCodes(
        TerminalResult terminalResult,
        int expectedExitCode)
    {
        Assert.Equal(expectedExitCode, WorkerExitCodes.FromTerminalResult(terminalResult));
    }

    [Fact]
    public async Task RunAsync_WhenRequestRunIdDiffers_ReturnsValidationFailedWithoutExecuting()
    {
        using var root = TestRoot.Create();
        var paths = new AppPaths(root.RootDirectory);
        var runId = Guid.Parse("53571b49-b71b-4a17-b039-4f25740b9d32");
        var mismatchedRequest = CreateRequest(Guid.Parse("0d0e7c05-a1eb-44c0-bd3e-0ec1d0b6268c"));
        var store = new FixedRequestStore(OperationRequestReadResult.Success(
            mismatchedRequest,
            paths.GetPendingRequestFilePath(runId)));
        var executor = new RecordingWorkerExecutor(CreateWorkflowResult(mismatchedRequest, TerminalResult.Succeeded));
        var mode = CreateMode(paths, store, administrator: true, CreateMatchedResolver(), executor);

        var result = await mode.RunAsync(WorkerArguments(runId), CancellationToken.None);

        Assert.Equal(TerminalResult.ValidationFailed, result.TerminalResult);
        Assert.Equal(2, result.ExitCode);
        Assert.Equal(0, executor.CallCount);
        Assert.Equal(1, store.ConsumeCalls);
    }

    [Fact]
    public async Task RunAsync_WhenRequestIsNotCompact_ReturnsValidationFailedWithoutExecuting()
    {
        using var root = TestRoot.Create();
        var paths = new AppPaths(root.RootDirectory);
        var request = CreateRequest(intent: OperationIntent.Preflight);
        var store = new FixedRequestStore(OperationRequestReadResult.Success(
            request,
            paths.GetPendingRequestFilePath(request.RunId)));
        var executor = new RecordingWorkerExecutor(CreateWorkflowResult(request, TerminalResult.Succeeded));
        var mode = CreateMode(paths, store, administrator: true, CreateMatchedResolver(), executor);

        var result = await mode.RunAsync(WorkerArguments(request.RunId), CancellationToken.None);

        Assert.Equal(TerminalResult.ValidationFailed, result.TerminalResult);
        Assert.Equal(2, result.ExitCode);
        Assert.Equal(0, executor.CallCount);
    }

    [Fact]
    public async Task RunAsync_WhenPendingPathIsOutsideThePendingRoot_ReturnsValidationFailedWithoutExecuting()
    {
        using var root = TestRoot.Create();
        var paths = new AppPaths(root.RootDirectory);
        var request = CreateRequest();
        var store = new FixedRequestStore(OperationRequestReadResult.Success(
            request,
            Path.Combine(root.RootDirectory + "-outside", $"{request.RunId:D}.json")));
        var executor = new RecordingWorkerExecutor(CreateWorkflowResult(request, TerminalResult.Succeeded));
        var mode = CreateMode(paths, store, administrator: true, CreateMatchedResolver(), executor);

        var result = await mode.RunAsync(WorkerArguments(request.RunId), CancellationToken.None);

        Assert.Equal(TerminalResult.ValidationFailed, result.TerminalResult);
        Assert.Equal(2, result.ExitCode);
        Assert.Equal(0, executor.CallCount);
    }

    [Fact]
    public async Task RunAsync_WhenWorkerIsNotAdministrator_WritesElevationFailureAndSkipsExecution()
    {
        using var root = TestRoot.Create();
        var paths = new AppPaths(root.RootDirectory);
        var request = CreateRequest();
        var store = new FixedRequestStore(OperationRequestReadResult.Success(
            request,
            paths.GetPendingRequestFilePath(request.RunId)));
        var journal = new FakeRunJournal();
        var executor = new RecordingWorkerExecutor(CreateWorkflowResult(request, TerminalResult.Succeeded));
        var mode = CreateMode(
            paths,
            store,
            administrator: false,
            CreateMatchedResolver(),
            executor,
            journal);

        var result = await mode.RunAsync(WorkerArguments(request.RunId), CancellationToken.None);

        Assert.Equal(TerminalResult.ValidationFailed, result.TerminalResult);
        Assert.Equal(2, result.ExitCode);
        Assert.Equal(0, executor.CallCount);
        Assert.Contains(
            journal.Events,
            static @event => @event.OperationName == "WorkerNotElevated" &&
                             @event.Phase == RunPhase.Elevation &&
                             @event.Level == RunEventLevel.Error);
        Assert.Equal(TerminalResult.ValidationFailed, Assert.Single(journal.Summaries).TerminalResult);
        Assert.Equal(1, store.ConsumeCalls);
    }

    [Fact]
    public async Task RunAsync_WhenSecondLxssResolutionMismatches_LeavesWslAndDiskPartActionsAtZero()
    {
        using var root = TestRoot.Create();
        var paths = new AppPaths(root.RootDirectory);
        var request = CreateRequest();
        var store = new FixedRequestStore(OperationRequestReadResult.Success(
            request,
            paths.GetPendingRequestFilePath(request.RunId)));
        var wsl = new FakeWslClient { ThrowOnAction = false };
        var diskPart = new FakeDiskPartClient { ThrowOnInvocation = false };
        var executor = new ActionInvokingWorkerExecutor(
            wsl,
            diskPart,
            CreateWorkflowResult(request, TerminalResult.Succeeded));
        var resolver = new FakeLxssProfileResolver(
            new LxssProfileResolution(
                LxssResolutionStatus.Mismatched,
                request.Profile.DistroName,
                @"D:\Other\ext4.vhdx",
                request.Profile.VhdxPath));
        var mode = CreateMode(paths, store, administrator: true, resolver, executor);

        var result = await mode.RunAsync(WorkerArguments(request.RunId), CancellationToken.None);

        Assert.Equal(TerminalResult.ValidationFailed, result.TerminalResult);
        Assert.Equal(2, result.ExitCode);
        Assert.Equal(0, executor.CallCount);
        Assert.Equal(0, wsl.ShutdownAllCalls);
        Assert.Equal(0, wsl.TerminateDistroCalls);
        Assert.Equal(0, diskPart.TotalCalls);
    }

    [Fact]
    public void WorkerMode_HasNoMainMenuOrConfirmationInputDependency()
    {
        var constructorParameterTypes = typeof(WorkerMode)
            .GetConstructors()
            .SelectMany(static constructor => constructor.GetParameters())
            .Select(static parameter => parameter.ParameterType);

        Assert.DoesNotContain(typeof(IMenuInput), constructorParameterTypes);
        Assert.DoesNotContain(typeof(IConfirmationInput), constructorParameterTypes);
    }

    private static WorkerMode CreateMode(
        AppPaths paths,
        IOperationRequestStore store,
        bool administrator,
        ILxssProfileResolver resolver,
        IWorkerOperationExecutor executor,
        IRunJournal? journal = null) =>
        new(
            paths,
            store,
            journal ?? new FakeRunJournal(),
            new FixedAdministratorProbe(administrator),
            resolver,
            executor,
            new FixedClock());

    private static FakeLxssProfileResolver CreateMatchedResolver() =>
        new(
            new LxssProfileResolution(
                LxssResolutionStatus.Matched,
                "Ubuntu-24.04",
                @"D:\DevTools\WSL2\Ubuntu24.04\ext4.vhdx",
                @"D:\DevTools\WSL2\Ubuntu24.04\ext4.vhdx"));

    private static string[] WorkerArguments(Guid runId) =>
        ["--worker", "--run-id", runId.ToString("D")];

    private static OperationRequest CreateRequest(
        Guid? runId = null,
        OperationIntent intent = OperationIntent.Compact) =>
        new(
            runId ?? Guid.Parse("215cbe09-6278-455c-8fec-9f46156b4cf0"),
            new Profile(
                Guid.Parse("47c96ef0-706e-4e93-b7bb-0a18f0b696d8"),
                "Ubuntu 24.04 on D",
                "Ubuntu-24.04",
                @"D:\DevTools\WSL2\Ubuntu24.04\ext4.vhdx",
                ShutdownMode.Global,
                TimeSpan.FromSeconds(45)),
            intent);

    private static WorkflowResult CreateWorkflowResult(
        OperationRequest request,
        TerminalResult terminalResult) =>
        new(
            new RunSummary(
                request.RunId,
                request.Profile,
                request.Intent,
                DateTimeOffset.UnixEpoch,
                DateTimeOffset.UnixEpoch,
                BeforeSnapshot: null,
                AfterSnapshot: null,
                terminalResult),
            new PreflightReport(
                Vela.Core.Validation.ValidationResult.Valid,
                InstalledInventory: null,
                LxssResolution: null,
                VhdxInspection: null,
                RunningInventory: null),
            ImmutableArray<WorkflowDiagnostic>.Empty,
            RunDirectory: null);

    private sealed class FixedAdministratorProbe : IAdministratorProbe
    {
        private readonly bool _isAdministrator;

        public FixedAdministratorProbe(bool isAdministrator)
        {
            _isAdministrator = isAdministrator;
        }

        public bool IsAdministrator() => _isAdministrator;
    }

    private sealed class FixedRequestStore : IOperationRequestStore
    {
        private readonly OperationRequestReadResult _readResult;

        public FixedRequestStore(OperationRequestReadResult readResult)
        {
            _readResult = readResult;
        }

        public int ConsumeCalls { get; private set; }

        public Task<OperationRequestWriteResult> WriteAsync(OperationRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(OperationRequestWriteResult.Failure());

        public Task<OperationRequestReadResult> ReadAsync(Guid expectedRunId, CancellationToken cancellationToken) =>
            Task.FromResult(_readResult);

        public Task<OperationRequestConsumeResult> ConsumeAsync(Guid expectedRunId, CancellationToken cancellationToken)
        {
            ConsumeCalls++;
            return Task.FromResult(OperationRequestConsumeResult.Success());
        }
    }

    private sealed class RecordingWorkerExecutor : IWorkerOperationExecutor
    {
        private readonly WorkflowResult _result;

        public RecordingWorkerExecutor(WorkflowResult result)
        {
            _result = result;
        }

        public int CallCount { get; private set; }

        public Task<WorkflowResult> ExecuteAsync(OperationRequest request, CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(_result);
        }
    }

    private sealed class ActionInvokingWorkerExecutor : IWorkerOperationExecutor
    {
        private readonly FakeWslClient _wsl;
        private readonly FakeDiskPartClient _diskPart;
        private readonly WorkflowResult _result;

        public ActionInvokingWorkerExecutor(
            FakeWslClient wsl,
            FakeDiskPartClient diskPart,
            WorkflowResult result)
        {
            _wsl = wsl;
            _diskPart = diskPart;
            _result = result;
        }

        public int CallCount { get; private set; }

        public async Task<WorkflowResult> ExecuteAsync(OperationRequest request, CancellationToken cancellationToken)
        {
            CallCount++;
            await _wsl.ShutdownAllAsync(cancellationToken);
            await _diskPart.CompactVdiskAsync(request.Profile.VhdxPath, cancellationToken);
            return _result;
        }
    }

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
                "worker-mode-tests",
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
