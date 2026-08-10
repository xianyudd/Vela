using System.Text;
using System.Text.Json;
using Vela.Core.Models;
using Vela.Windows.Diagnostics;
using Vela.Windows.Elevation;

namespace Vela.Tests.Windows;

public sealed class CompactRunGateTests
{
    [Fact]
    public void TryAcquire_ReturnsLeaseThatReleasesTheExactGateIdempotently()
    {
        using var root = TestRoot.Create();
        var paths = new AppPaths(root.Path);
        var gate = new CompactRunGate(paths);
        var request = CreateRequest();

        var result = gate.TryAcquire(request);

        Assert.Equal(CompactRunGateStatus.Acquired, result.Status);
        Assert.NotNull(result.Lease);
        Assert.True(File.Exists(paths.CompactGateFilePath));
        Assert.Equal(request.RunId, result.Lease!.RunId);

        result.Lease.Dispose();
        result.Lease.Dispose();

        Assert.False(File.Exists(paths.CompactGateFilePath));
    }

    [Fact]
    public void TryAcquire_ReportsASecondTrustedGateAsAlreadyRunningWithoutALease()
    {
        using var root = TestRoot.Create();
        var paths = new AppPaths(root.Path);
        var firstGate = new CompactRunGate(paths);
        var secondGate = new CompactRunGate(paths);
        var firstRequest = CreateRequest();
        var secondRequest = CreateRequest() with
        {
            RunId = Guid.Parse("d9c54d6a-4f28-4a59-83c0-3d8bf3519df2")
        };

        var acquired = firstGate.TryAcquire(firstRequest);
        var alreadyRunning = secondGate.TryAcquire(secondRequest);

        Assert.Equal(CompactRunGateStatus.Acquired, acquired.Status);
        Assert.Equal(CompactRunGateStatus.AlreadyRunning, alreadyRunning.Status);
        Assert.Equal(firstRequest.RunId, alreadyRunning.ActiveRunId);
        Assert.Null(alreadyRunning.Lease);

        acquired.Lease!.Dispose();
    }

    [Fact]
    public void TryAcquire_RetainsMalformedGateAndRejectsAcquisition()
    {
        using var root = TestRoot.Create();
        var paths = new AppPaths(root.Path);
        Directory.CreateDirectory(paths.RootDirectory);
        File.WriteAllText(paths.CompactGateFilePath, "not-a-gate", Encoding.UTF8);

        var result = new CompactRunGate(paths).TryAcquire(CreateRequest());

        Assert.Equal(CompactRunGateStatus.Invalid, result.Status);
        Assert.Null(result.Lease);
        Assert.True(File.Exists(paths.CompactGateFilePath));
    }

    [Fact]
    public void TryAcquire_RejectsInvalidRequestAndDoesNotCreateGate()
    {
        using var root = TestRoot.Create();
        var paths = new AppPaths(root.Path);
        var request = CreateRequest() with { Intent = OperationIntent.Preflight };

        var result = new CompactRunGate(paths).TryAcquire(request);

        Assert.Equal(CompactRunGateStatus.Invalid, result.Status);
        Assert.False(File.Exists(paths.CompactGateFilePath));
    }

    [Fact]
    public void TryAcquire_RecognizesATrustedPendingRequestAsAlreadyRunning()
    {
        using var root = TestRoot.Create();
        var paths = new AppPaths(root.Path);
        var pendingRunId = Guid.Parse("01e7c5df-7566-4cf3-a0bd-ff7cbe76a4e2");
        Directory.CreateDirectory(paths.PendingDirectoryPath);
        File.WriteAllText(
            paths.GetPendingRequestFilePath(pendingRunId),
            JsonSerializer.Serialize(CreateRequest() with { RunId = pendingRunId }));

        var result = new CompactRunGate(paths).TryAcquire(CreateRequest());

        Assert.Equal(CompactRunGateStatus.AlreadyRunning, result.Status);
        Assert.Equal(pendingRunId, result.ActiveRunId);
        Assert.Null(result.Lease);
        Assert.False(File.Exists(paths.CompactGateFilePath));
    }

    private static OperationRequest CreateRequest() =>
        new(
            Guid.Parse("6e7f3f8e-7c52-4224-9b85-9a7cfd71dc2e"),
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
                "compact-gate-tests",
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
}
