using System.Text;
using Vela.Tui.Application;
using Vela.Windows.Configuration;
using Vela.Windows.Diagnostics;

namespace Vela.Tests.Tui;

public sealed class StartupGateTests
{
    [Fact]
    public void Inspect_IsReadOnlyWhenTheDataDirectoryIsMissing()
    {
        using var testRoot = TestRoot.CreateMissing();
        var paths = new AppPaths(testRoot.RootDirectory);
        var gate = new StartupGate(paths, new JsonProfileStore(paths));

        var inspection = gate.Inspect();

        Assert.False(inspection.IsComplete);
        Assert.False(inspection.RootDirectoryExists);
        Assert.False(inspection.ConfigurationFileExists);
        Assert.False(inspection.PendingDirectoryExists);
        Assert.False(inspection.LogsDirectoryExists);
        Assert.False(Directory.Exists(paths.RootDirectory));
        Assert.False(File.Exists(paths.ConfigurationTemporaryFilePath));
    }

    [Fact]
    public async Task InitializeAfterConfirmation_CreatesMissingRootDirectoriesAndConfig()
    {
        using var testRoot = TestRoot.CreateMissing();
        var paths = new AppPaths(testRoot.RootDirectory);
        var gate = new StartupGate(paths, new JsonProfileStore(paths));

        var result = await gate.InitializeAfterConfirmationAsync();

        Assert.Equal(StartupGateStatus.Initialized, result.Status);
        Assert.True(result.IsReady);
        Assert.True(result.Inspection.IsComplete);
        Assert.True(Directory.Exists(paths.RootDirectory));
        Assert.True(Directory.Exists(paths.PendingDirectoryPath));
        Assert.True(Directory.Exists(paths.LogsDirectoryPath));
        Assert.True(File.Exists(paths.ConfigurationFilePath));
        Assert.False(File.Exists(paths.ConfigurationTemporaryFilePath));
        Assert.Contains("初始化完成", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InitializeAfterConfirmation_PreservesExistingConfigAndDirectories()
    {
        using var testRoot = TestRoot.CreateMissing();
        var paths = new AppPaths(testRoot.RootDirectory);
        Directory.CreateDirectory(paths.RootDirectory);
        Directory.CreateDirectory(paths.PendingDirectoryPath);
        Directory.CreateDirectory(paths.LogsDirectoryPath);
        var store = new JsonProfileStore(paths);
        await store.SaveAsync(JsonProfileStore.CreateInitialState(), CancellationToken.None);
        var configBytes = await File.ReadAllBytesAsync(paths.ConfigurationFilePath);
        var pendingMarker = Path.Combine(paths.PendingDirectoryPath, "keep.marker");
        var logsMarker = Path.Combine(paths.LogsDirectoryPath, "keep.marker");
        await File.WriteAllTextAsync(pendingMarker, "pending");
        await File.WriteAllTextAsync(logsMarker, "logs");

        var result = await new StartupGate(paths, new JsonProfileStore(paths))
            .InitializeAfterConfirmationAsync();

        Assert.Equal(StartupGateStatus.Ready, result.Status);
        Assert.True(result.IsReady);
        Assert.Equal(configBytes, await File.ReadAllBytesAsync(paths.ConfigurationFilePath));
        Assert.Equal("pending", await File.ReadAllTextAsync(pendingMarker));
        Assert.Equal("logs", await File.ReadAllTextAsync(logsMarker));
        Assert.False(File.Exists(paths.ConfigurationTemporaryFilePath));
    }

    [Fact]
    public async Task InitializeAfterConfirmation_FillsOnlyMissingDirectoriesWhenConfigExists()
    {
        using var testRoot = TestRoot.CreateMissing();
        var paths = new AppPaths(testRoot.RootDirectory);
        Directory.CreateDirectory(paths.RootDirectory);
        var store = new JsonProfileStore(paths);
        await store.SaveAsync(JsonProfileStore.CreateInitialState(), CancellationToken.None);
        var configBytes = await File.ReadAllBytesAsync(paths.ConfigurationFilePath);

        var result = await new StartupGate(paths, new JsonProfileStore(paths))
            .InitializeAfterConfirmationAsync();

        Assert.Equal(StartupGateStatus.Initialized, result.Status);
        Assert.True(Directory.Exists(paths.PendingDirectoryPath));
        Assert.True(Directory.Exists(paths.LogsDirectoryPath));
        Assert.Equal(configBytes, await File.ReadAllBytesAsync(paths.ConfigurationFilePath));
        Assert.False(File.Exists(paths.ConfigurationTemporaryFilePath));
    }

    [Fact]
    public async Task InitializeAfterConfirmation_ReturnsStableFailureWhenConfigPathIsDirectory()
    {
        using var testRoot = TestRoot.CreateMissing();
        var paths = new AppPaths(testRoot.RootDirectory);
        Directory.CreateDirectory(paths.RootDirectory);
        Directory.CreateDirectory(paths.ConfigurationFilePath);

        var result = await new StartupGate(paths, new JsonProfileStore(paths))
            .InitializeAfterConfirmationAsync();

        Assert.Equal(StartupGateStatus.Failed, result.Status);
        Assert.False(result.IsReady);
        Assert.Contains("初始化失败", result.Message, StringComparison.Ordinal);
        Assert.True(Directory.Exists(paths.ConfigurationFilePath));
        Assert.False(File.Exists(paths.ConfigurationTemporaryFilePath));
    }

    [Fact]
    public async Task InitializeAfterConfirmation_PropagatesCancellationBeforeWriting()
    {
        using var testRoot = TestRoot.CreateMissing();
        var paths = new AppPaths(testRoot.RootDirectory);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new StartupGate(paths, new JsonProfileStore(paths))
                .InitializeAfterConfirmationAsync(cancellation.Token));

        Assert.False(Directory.Exists(paths.RootDirectory));
        Assert.False(File.Exists(paths.ConfigurationTemporaryFilePath));
    }

    private sealed class TestRoot : IDisposable
    {
        private TestRoot(string rootDirectory) => RootDirectory = rootDirectory;

        public string RootDirectory { get; }

        public static TestRoot CreateMissing()
        {
            var rootDirectory = Path.Combine(
                FindRepositoryRoot(),
                "artifacts",
                "test-data",
                "startup-gate-tests",
                Guid.NewGuid().ToString("N"));
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
