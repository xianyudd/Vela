using System.Collections.Immutable;
using System.Text;
using Vela.Core.Contracts;
using Vela.Tests.Fakes;
using Vela.Windows.Processes;
using Vela.Windows.Storage;

namespace Vela.Tests.Windows;

public sealed class VhdxInspectorTests
{
    [Theory]
    [InlineData("This file is set as sparse.", true)]
    [InlineData("This file is NOT set as sparse.", false)]
    [InlineData("此文件设置为稀疏。", true)]
    [InlineData("此文件未设置为稀疏。", false)]
    [InlineData("此文件没有设置为稀疏", false)]
    public async Task InspectAsync_ParsesEnglishAndChineseSparseOutput(string nativeOutput, bool expectedSparse)
    {
        using var testFile = TestVhdxFile.Create(length: 1024);
        var runner = CreateRunner(ProcessExecutionStatus.Succeeded, exitCode: 0, nativeOutput);
        var paths = new NativeToolPaths();
        var inspector = new VhdxInspector(runner, paths);

        var result = await inspector.InspectAsync(testFile.VhdxPath, CancellationToken.None);

        var snapshot = Assert.IsType<Vela.Core.Models.VhdxSnapshot>(result.Snapshot);
        Assert.Equal(VhdxInspectionStatus.Succeeded, result.Status);
        Assert.Equal(expectedSparse, snapshot.IsSparse);
        Assert.Equal(Path.GetFullPath(testFile.VhdxPath), snapshot.Path);
        Assert.Equal(1024, snapshot.FileLengthBytes);
        Assert.Equal(testFile.LastWriteUtc, snapshot.LastWriteUtc);
        Assert.Equal(new DriveInfo(testFile.VhdxPath).RootDirectory.FullName, snapshot.Drive.RootPath);
        Assert.True(snapshot.Drive.TotalSizeBytes > 0);
        Assert.True(snapshot.Drive.AvailableFreeSpaceBytes >= 0);
        Assert.Equal(paths.FsutilExePath, Assert.Single(runner.Invocations).ExecutablePath);
        Assert.Equal(
            new[] { "sparse", "queryflag", Path.GetFullPath(testFile.VhdxPath) },
            runner.Invocations[0].Arguments);
        Assert.Equal(936, runner.Invocations[0].OutputEncoding?.CodePage);
    }

    [Fact]
    public async Task InspectAsync_ParsesUtf16LeRedirectedSparseOutput()
    {
        using var testFile = TestVhdxFile.Create(length: 1024);
        var runner = CreateRunner(
            ProcessExecutionStatus.Succeeded,
            exitCode: 0,
            Utf16LeAsByteCharacters("This file is set as sparse."));
        var inspector = new VhdxInspector(runner, new NativeToolPaths());

        var result = await inspector.InspectAsync(testFile.VhdxPath, CancellationToken.None);

        Assert.Equal(VhdxInspectionStatus.Succeeded, result.Status);
        Assert.True(result.Snapshot?.IsSparse);
    }

    [Fact]
    public async Task InspectAsync_WhenSparseQueryFails_ReportsUnknownSparseState()
    {
        using var testFile = TestVhdxFile.Create(length: 256);
        var runner = CreateRunner(ProcessExecutionStatus.Failed, exitCode: 1, "query failed");
        var inspector = new VhdxInspector(runner, new NativeToolPaths());

        var result = await inspector.InspectAsync(testFile.VhdxPath, CancellationToken.None);

        Assert.Equal(VhdxInspectionStatus.Succeeded, result.Status);
        Assert.NotNull(result.Snapshot);
        Assert.Null(result.Snapshot.IsSparse);
        Assert.Equal(1, runner.InvocationCount);
    }

    [Fact]
    public async Task InspectAsync_WhenSparseRunnerThrows_ReportsUnknownSparseState()
    {
        using var testFile = TestVhdxFile.Create(length: 256);
        var runner = new FakeProcessRunner();
        var inspector = new VhdxInspector(runner, new NativeToolPaths());

        var result = await inspector.InspectAsync(testFile.VhdxPath, CancellationToken.None);

        Assert.Equal(VhdxInspectionStatus.Succeeded, result.Status);
        Assert.NotNull(result.Snapshot);
        Assert.Null(result.Snapshot.IsSparse);
        Assert.Equal(1, runner.InvocationCount);
    }

    [Fact]
    public async Task InspectAsync_WhenCancellationArrivesDuringSparseQuery_PropagatesCancellation()
    {
        using var testFile = TestVhdxFile.Create(length: 256);
        using var cancellation = new CancellationTokenSource();
        using var invocationTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var runner = new CoordinatedProcessRunner();
        var inspector = new VhdxInspector(runner, new NativeToolPaths());

        var inspection = inspector.InspectAsync(testFile.VhdxPath, cancellation.Token);
        await runner.WaitForInvocationAsync(invocationTimeout.Token);
        cancellation.Cancel();
        runner.Complete(CreateProcessResult(ProcessExecutionStatus.Succeeded, exitCode: 0, "This file is set as sparse."));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => inspection);
    }

    [Fact]
    public async Task InspectAsync_WhenVhdxDoesNotExist_ReturnsMissingWithoutQueryingFsutil()
    {
        var runner = CreateRunner(ProcessExecutionStatus.Succeeded, exitCode: 0);
        var inspector = new VhdxInspector(runner, new NativeToolPaths());
        var missingPath = Path.Combine(
            FindRepositoryRoot(),
            "artifacts",
            "test-data",
            "vhdx-inspector-tests",
            Guid.NewGuid().ToString("N"),
            "missing.vhdx");

        var result = await inspector.InspectAsync(missingPath, CancellationToken.None);

        Assert.Equal(VhdxInspectionStatus.Missing, result.Status);
        Assert.Null(result.Snapshot);
        Assert.Equal(0, runner.InvocationCount);
    }

    [Fact]
    public async Task InspectAsync_WhenPathIsNotAbsolute_ReturnsFailedWithoutQueryingFsutil()
    {
        var runner = CreateRunner(ProcessExecutionStatus.Succeeded, exitCode: 0);
        var inspector = new VhdxInspector(runner, new NativeToolPaths());

        var result = await inspector.InspectAsync(@"relative\ext4.vhdx", CancellationToken.None);

        Assert.Equal(VhdxInspectionStatus.Failed, result.Status);
        Assert.Null(result.Snapshot);
        Assert.Equal(0, runner.InvocationCount);
    }

    private static FakeProcessRunner CreateRunner(
        ProcessExecutionStatus status,
        int? exitCode,
        params string[] output) =>
        new()
        {
            ThrowOnInvocation = false,
            Result = CreateProcessResult(status, exitCode, output)
        };

    private static ProcessExecutionResult CreateProcessResult(
        ProcessExecutionStatus status,
        int? exitCode,
        params string[] output) =>
        new(
            status,
            exitCode,
            ImmutableArray.CreateRange(output),
            ImmutableArray<string>.Empty,
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch);

    private static string Utf16LeAsByteCharacters(string value) =>
        string.Concat(Encoding.Unicode.GetBytes(value).Select(static value => (char)value));

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

    private sealed class TestVhdxFile : IDisposable
    {
        private TestVhdxFile(string directoryPath, string path, DateTimeOffset lastWriteUtc)
        {
            DirectoryPath = directoryPath;
            VhdxPath = path;
            LastWriteUtc = lastWriteUtc;
        }

        public string DirectoryPath { get; }

        public string VhdxPath { get; }

        public DateTimeOffset LastWriteUtc { get; }

        public static TestVhdxFile Create(long length)
        {
            var directoryPath = Path.Combine(
                FindRepositoryRoot(),
                "artifacts",
                "test-data",
                "vhdx-inspector-tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directoryPath);

            var path = Path.Combine(directoryPath, "ext4.vhdx");
            using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.SetLength(length);
            }

            var configuredLastWrite = DateTime.UtcNow.AddMinutes(-5);
            File.SetLastWriteTimeUtc(path, configuredLastWrite);
            var persistedLastWrite = new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero);

            return new TestVhdxFile(directoryPath, path, persistedLastWrite);
        }

        public void Dispose()
        {
            if (Directory.Exists(DirectoryPath))
            {
                Directory.Delete(DirectoryPath, recursive: true);
            }
        }
    }

    private sealed class CoordinatedProcessRunner : IProcessRunner
    {
        private readonly TaskCompletionSource<bool> _invoked =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<ProcessExecutionResult> _result =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Complete(ProcessExecutionResult result) => _result.TrySetResult(result);

        public async Task<ProcessExecutionResult> RunAsync(
            ProcessInvocation invocation,
            IProgress<ProcessOutput>? output,
            CancellationToken cancellationToken)
        {
            _invoked.TrySetResult(true);
            return await _result.Task.ConfigureAwait(false);
        }

        public Task WaitForInvocationAsync(CancellationToken cancellationToken) =>
            _invoked.Task.WaitAsync(cancellationToken);
    }
}
