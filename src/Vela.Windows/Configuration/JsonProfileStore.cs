using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using Vela.Core.Models;
using Vela.Core.Validation;
using Vela.Windows.Diagnostics;

namespace Vela.Windows.Configuration;

public sealed record ProfileStoreState(
    int SchemaVersion,
    Guid LastProfileId,
    int LogRetentionDays,
    ImmutableArray<Profile> Profiles);

public sealed class JsonProfileStore
{
    public const int CurrentSchemaVersion = 1;
    public const int DefaultLogRetentionDays = 90;

    private static readonly Guid InitialProfileId = Guid.Parse("4c2bc4fc-c14e-4f33-8df4-e9d52c9a1019");
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly AppPaths _paths;

    public JsonProfileStore(AppPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _paths = paths;
    }

    public async Task<ProfileStoreState> LoadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var existingState = await LoadExistingAsync(cancellationToken).ConfigureAwait(false);
        if (existingState is not null)
        {
            return existingState;
        }

        var initialState = CreateInitialState();
        await SaveAsync(initialState, cancellationToken).ConfigureAwait(false);
        return initialState;
    }

    public async Task<ProfileStoreState?> LoadExistingAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (Directory.Exists(_paths.ConfigurationFilePath))
        {
            throw new InvalidDataException("The Vela profile configuration path is not a file.");
        }

        if (!File.Exists(_paths.ConfigurationFilePath))
        {
            return null;
        }

        try
        {
            await using var stream = new FileStream(
                _paths.ConfigurationFilePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 4096,
                useAsync: true);
            var persistedState = await JsonSerializer.DeserializeAsync<ProfileStoreFile>(
                stream,
                SerializerOptions,
                cancellationToken).ConfigureAwait(false);
            if (persistedState is null)
            {
                throw new InvalidDataException("The Vela profile configuration is empty.");
            }

            return ToState(persistedState);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                "The Vela profile configuration is not valid JSON.",
                exception);
        }
    }

    public async Task<ProfileStoreState> LoadRequiredAsync(
        CancellationToken cancellationToken = default)
    {
        var state = await LoadExistingAsync(cancellationToken).ConfigureAwait(false);
        return state ?? throw new InvalidDataException(
            "The Vela profile configuration is missing.");
    }

    public async Task SaveAsync(ProfileStoreState state, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(state);
        ValidateState(state, invalidData: false);
        cancellationToken.ThrowIfCancellationRequested();

        Directory.CreateDirectory(_paths.RootDirectory);
        var temporaryPath = _paths.ConfigurationTemporaryFilePath;

        try
        {
            await WriteJsonAsync(temporaryPath, ToPersistedState(state), cancellationToken).ConfigureAwait(false);
            ReplaceAtomically(temporaryPath, _paths.ConfigurationFilePath);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    public async Task<bool> SaveIfMissingAsync(
        ProfileStoreState state,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(state);
        ValidateState(state, invalidData: false);
        cancellationToken.ThrowIfCancellationRequested();

        Directory.CreateDirectory(_paths.RootDirectory);
        if (File.Exists(_paths.ConfigurationFilePath))
        {
            return false;
        }

        var temporaryPath = _paths.ConfigurationTemporaryFilePath;
        try
        {
            await WriteJsonAsync(
                    temporaryPath,
                    ToPersistedState(state),
                    cancellationToken)
                .ConfigureAwait(false);

            try
            {
                File.Move(
                    temporaryPath,
                    _paths.ConfigurationFilePath,
                    overwrite: false);
                return true;
            }
            catch (IOException) when (File.Exists(_paths.ConfigurationFilePath))
            {
                return false;
            }
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    public static ProfileStoreState CreateInitialState()
    {
        var profile = new Profile(
            InitialProfileId,
            "Ubuntu 24.04 on D",
            "Ubuntu-24.04",
            @"D:\DevTools\WSL2\Ubuntu24.04\ext4.vhdx",
            ShutdownMode.Global,
            TimeSpan.FromSeconds(45));

        return new ProfileStoreState(
            CurrentSchemaVersion,
            profile.Id,
            DefaultLogRetentionDays,
            ImmutableArray.Create(profile));
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

    private static ProfileStoreState ToState(ProfileStoreFile persistedState)
    {
        if (persistedState.Profiles.IsDefault)
        {
            throw new InvalidDataException("The Vela profile configuration does not contain a profiles array.");
        }

        var state = new ProfileStoreState(
            persistedState.SchemaVersion,
            persistedState.LastProfileId,
            persistedState.LogRetentionDays,
            persistedState.Profiles.Select(
                static profile => new Profile(
                    profile.Id,
                    profile.DisplayName,
                    profile.DistroName,
                    profile.VhdxPath,
                    profile.ShutdownMode,
                    TimeSpan.FromSeconds(profile.ShutdownTimeoutSeconds))).ToImmutableArray());

        ValidateState(state, invalidData: true);
        return state;
    }

    private static ProfileStoreFile ToPersistedState(ProfileStoreState state) =>
        new(
            state.SchemaVersion,
            state.LastProfileId,
            state.LogRetentionDays,
            state.Profiles.Select(
                static profile => new ProfileFile(
                    profile.Id,
                    profile.DisplayName,
                    profile.DistroName,
                    profile.VhdxPath,
                    profile.ShutdownMode,
                    ToWholeSeconds(profile.ShutdownTimeout))).ToImmutableArray());

    private static int ToWholeSeconds(TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero || timeout.Ticks % TimeSpan.TicksPerSecond != 0)
        {
            throw new ArgumentException("Profile shutdown timeouts must be positive whole seconds.");
        }

        return checked((int)timeout.TotalSeconds);
    }

    private static void ValidateState(ProfileStoreState state, bool invalidData)
    {
        if (state.SchemaVersion != CurrentSchemaVersion)
        {
            ThrowInvalidState("The Vela profile configuration schema version is not supported.", invalidData);
        }

        if (state.LogRetentionDays < 1)
        {
            ThrowInvalidState("The Vela log retention period must be at least one day.", invalidData);
        }

        if (state.Profiles.IsDefaultOrEmpty)
        {
            ThrowInvalidState("The Vela profile configuration must contain at least one profile.", invalidData);
        }

        if (state.Profiles
            .GroupBy(static profile => profile.Id)
            .Any(static profiles => profiles.Key == Guid.Empty || profiles.Count() != 1))
        {
            ThrowInvalidState("The Vela profile configuration contains duplicate profile identifiers.", invalidData);
        }

        if (state.LastProfileId == Guid.Empty ||
            !state.Profiles.Any(profile => profile.Id == state.LastProfileId))
        {
            ThrowInvalidState("The Vela profile configuration has an invalid last profile identifier.", invalidData);
        }

        foreach (var profile in state.Profiles)
        {
            if (profile.Id == Guid.Empty || !ProfileValidator.Validate(profile).IsValid)
            {
                ThrowInvalidState("The Vela profile configuration contains an invalid profile.", invalidData);
            }
        }
    }

    private static void ThrowInvalidState(string message, bool invalidData)
    {
        if (invalidData)
        {
            throw new InvalidDataException(message);
        }

        throw new ArgumentException(message);
    }

    private sealed record ProfileStoreFile(
        int SchemaVersion,
        Guid LastProfileId,
        int LogRetentionDays,
        ImmutableArray<ProfileFile> Profiles);

    private sealed record ProfileFile(
        Guid Id,
        string DisplayName,
        string DistroName,
        string VhdxPath,
        ShutdownMode ShutdownMode,
        int ShutdownTimeoutSeconds);
}
