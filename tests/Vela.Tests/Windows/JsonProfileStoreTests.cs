using System.Collections.Immutable;
using System.Text.Json;
using Vela.Core.Models;
using Vela.Windows.Configuration;
using Vela.Windows.Diagnostics;

namespace Vela.Tests.Windows;

public sealed class JsonProfileStoreTests
{
    [Fact]
    public void CreateDefault_UsesLocalApplicationDataWithoutCreatingDirectories()
    {
        var paths = AppPaths.CreateDefault();

        Assert.Equal(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Vela"),
            paths.RootDirectory);
        Assert.Equal(Path.Combine(paths.RootDirectory, "config.json"), paths.ConfigurationFilePath);
    }

    [Fact]
    public async Task LoadAsync_WhenConfigIsMissing_CreatesSchemaVersionedInitialProfile()
    {
        using var testRoot = TestRoot.Create();
        var paths = new AppPaths(testRoot.RootDirectory);
        var store = new JsonProfileStore(paths);

        var state = await store.LoadAsync(CancellationToken.None);

        var profile = Assert.Single(state.Profiles);
        Assert.Equal(JsonProfileStore.CurrentSchemaVersion, state.SchemaVersion);
        Assert.Equal(profile.Id, state.LastProfileId);
        Assert.Equal(90, state.LogRetentionDays);
        Assert.Equal("Ubuntu 24.04 on D", profile.DisplayName);
        Assert.Equal("Ubuntu-24.04", profile.DistroName);
        Assert.Equal(@"D:\DevTools\WSL2\Ubuntu24.04\ext4.vhdx", profile.VhdxPath);
        Assert.Equal(ShutdownMode.Global, profile.ShutdownMode);
        Assert.Equal(TimeSpan.FromSeconds(45), profile.ShutdownTimeout);
        Assert.True(File.Exists(paths.ConfigurationFilePath));

        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(paths.ConfigurationFilePath));
        Assert.Equal(JsonProfileStore.CurrentSchemaVersion, document.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(45, document.RootElement.GetProperty("profiles")[0].GetProperty("shutdownTimeoutSeconds").GetInt32());
    }

    [Fact]
    public async Task SaveAsync_ReplacesConfigAtomicallyAndRoundTripsState()
    {
        using var testRoot = TestRoot.Create();
        var paths = new AppPaths(testRoot.RootDirectory);
        var store = new JsonProfileStore(paths);
        var initialState = await store.LoadAsync(CancellationToken.None);
        var updatedProfile = initialState.Profiles[0] with
        {
            DisplayName = "Ubuntu 24.04 compact target"
        };
        var updatedState = initialState with
        {
            Profiles = ImmutableArray.Create(updatedProfile)
        };

        await store.SaveAsync(updatedState, CancellationToken.None);

        Assert.True(File.Exists(paths.ConfigurationFilePath));
        Assert.False(File.Exists(paths.ConfigurationTemporaryFilePath));
        using (var document = JsonDocument.Parse(await File.ReadAllTextAsync(paths.ConfigurationFilePath)))
        {
            Assert.Equal("Ubuntu 24.04 compact target", document.RootElement.GetProperty("profiles")[0].GetProperty("displayName").GetString());
        }

        var reloadedState = await store.LoadAsync(CancellationToken.None);
        Assert.Equal(updatedState.SchemaVersion, reloadedState.SchemaVersion);
        Assert.Equal(updatedState.LastProfileId, reloadedState.LastProfileId);
        Assert.Equal(updatedState.LogRetentionDays, reloadedState.LogRetentionDays);
        Assert.Equal(updatedState.Profiles.ToArray(), reloadedState.Profiles);
    }

    [Fact]
    public async Task LoadAsync_WhenSchemaVersionIsUnsupported_DoesNotReplaceExistingConfig()
    {
        using var testRoot = TestRoot.Create();
        var paths = new AppPaths(testRoot.RootDirectory);
        Directory.CreateDirectory(paths.RootDirectory);
        const string unsupportedConfig = """{"schemaVersion":999,"lastProfileId":"00000000-0000-0000-0000-000000000000","logRetentionDays":90,"profiles":[]}""";
        await File.WriteAllTextAsync(paths.ConfigurationFilePath, unsupportedConfig);
        var store = new JsonProfileStore(paths);

        await Assert.ThrowsAsync<InvalidDataException>(() => store.LoadAsync(CancellationToken.None));

        Assert.Equal(unsupportedConfig, await File.ReadAllTextAsync(paths.ConfigurationFilePath));
    }

    [Fact]
    public async Task SaveAsync_WhenShutdownTimeoutIsNotWholeSeconds_RejectsLossySerialization()
    {
        using var testRoot = TestRoot.Create();
        var paths = new AppPaths(testRoot.RootDirectory);
        var store = new JsonProfileStore(paths);
        var initialState = await store.LoadAsync(CancellationToken.None);
        var fractionalTimeoutProfile = initialState.Profiles[0] with
        {
            ShutdownTimeout = TimeSpan.FromMilliseconds(45500)
        };
        var state = initialState with
        {
            Profiles = ImmutableArray.Create(fractionalTimeoutProfile)
        };

        await Assert.ThrowsAsync<ArgumentException>(() => store.SaveAsync(state, CancellationToken.None));
    }

    [Fact]
    public async Task SaveAsync_WhenProfileFailsDomainValidation_RejectsPersistingInvalidState()
    {
        using var testRoot = TestRoot.Create();
        var paths = new AppPaths(testRoot.RootDirectory);
        var store = new JsonProfileStore(paths);
        var initialState = await store.LoadAsync(CancellationToken.None);
        var invalidProfile = initialState.Profiles[0] with
        {
            VhdxPath = @"relative\ext4.vhdx"
        };
        var state = initialState with
        {
            Profiles = ImmutableArray.Create(invalidProfile)
        };

        await Assert.ThrowsAsync<ArgumentException>(() => store.SaveAsync(state, CancellationToken.None));
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
                "json-profile-store-tests",
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
