using Vela.Core.Models;
using Vela.Windows.Diagnostics;
using Vela.Windows.Elevation;

namespace Vela.Tests.Windows;

public sealed class OperationRequestClaimTests
{
    [Fact]
    public async Task ClaimAsync_OnlyOneWorkerClaimsThePendingRequest()
    {
        using var root = TestRoot.Create();
        var paths = new AppPaths(root.RootDirectory);
        var journal = new FileRunJournal(paths);
        var store = new OperationRequestStore(paths);
        var request = CreateRequest();
        await journal.CreateRunAsync(request.RunId, CancellationToken.None);
        await store.WriteAsync(request, CancellationToken.None);

        var claims = await Task.WhenAll(
            store.ClaimAsync(request.RunId, CancellationToken.None),
            store.ClaimAsync(request.RunId, CancellationToken.None));

        Assert.Single(claims, static claim => claim.Succeeded);
        Assert.Single(claims, static claim => !claim.Succeeded);
        Assert.True(File.Exists(paths.GetPendingRequestInflightFilePath(request.RunId)));
        Assert.False(File.Exists(paths.GetPendingRequestFilePath(request.RunId)));
    }

    [Fact]
    public async Task ClaimAsync_RejectsRequestFilesLargerThanTheBound()
    {
        using var root = TestRoot.Create();
        var paths = new AppPaths(root.RootDirectory);
        var journal = new FileRunJournal(paths);
        var store = new OperationRequestStore(paths);
        var request = CreateRequest();
        await journal.CreateRunAsync(request.RunId, CancellationToken.None);
        Directory.CreateDirectory(paths.PendingDirectoryPath);
        await File.WriteAllTextAsync(
            paths.GetPendingRequestFilePath(request.RunId),
            new string('x', (int)OperationRequestStore.MaxRequestBytes + 1));

        var claimed = await store.ClaimAsync(request.RunId, CancellationToken.None);

        Assert.False(claimed.Succeeded);
        Assert.True(File.Exists(paths.GetPendingRequestFilePath(request.RunId)));
        Assert.False(File.Exists(paths.GetPendingRequestInflightFilePath(request.RunId)));
    }

    private static OperationRequest CreateRequest() =>
        new(
            Guid.Parse("fcb93520-f5b5-45bc-b7cf-eaa16a81e9ab"),
            new Profile(
                Guid.Parse("cd7972c0-6e08-4f8d-83f4-d23bf8ae0858"),
                "Ubuntu 24.04",
                "Ubuntu-24.04",
                @"D:\DevTools\WSL2\Ubuntu24.04\ext4.vhdx",
                ShutdownMode.Global,
                TimeSpan.FromSeconds(30)),
            OperationIntent.Compact);

    private sealed class TestRoot : IDisposable
    {
        private TestRoot(string rootDirectory) => RootDirectory = rootDirectory;
        public string RootDirectory { get; }
        public static TestRoot Create()
        {
            var path = Path.Combine(FindRepositoryRoot(), "artifacts", "test-data", "operation-request-claim", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return new TestRoot(path);
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
        public void Dispose()
        {
            if (Directory.Exists(RootDirectory))
            {
                Directory.Delete(RootDirectory, recursive: true);
            }
        }
    }
}
