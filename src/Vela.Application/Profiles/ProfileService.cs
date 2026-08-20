using System.Collections.Immutable;
using Vela.Core.Models;
using Vela.Core.Validation;

namespace Vela.Application.Profiles;

/// <summary>
/// Coordinates profile selection, creation, update, and deletion
/// through a pluggable <see cref="IProfileStore"/>.
/// </summary>
public sealed class ProfileService : IProfileService
{
    private readonly IProfileStore _store;
    private ProfileStoreState? _state;

    /// <summary>
    /// Initializes a new <see cref="ProfileService"/> with the supplied store.
    /// </summary>
    public ProfileService(IProfileStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
    }

    /// <inheritdoc />
    public Profile CurrentProfile =>
        GetState().Profiles.Single(profile => profile.Id == GetState().LastProfileId);

    /// <inheritdoc />
    public ImmutableArray<Profile> Profiles => GetState().Profiles;

    /// <inheritdoc />
    public async Task<ProfileStoreState> LoadAsync(CancellationToken cancellationToken = default)
    {
        _state = await _store.LoadRequiredAsync(cancellationToken).ConfigureAwait(false);
        return _state;
    }

    /// <inheritdoc />
    public async Task<Profile> SelectAsync(
        Guid profileId,
        CancellationToken cancellationToken = default)
    {
        var state = GetState();
        var profile = FindProfile(state, profileId);
        if (state.LastProfileId == profileId)
        {
            return profile;
        }

        await SaveStateAsync(state with { LastProfileId = profileId }, cancellationToken)
            .ConfigureAwait(false);
        return profile;
    }

    /// <inheritdoc />
    public async Task<Profile> CreateAsync(
        ProfileDraft draft,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var profile = draft.ToProfile(Guid.NewGuid());
        ValidateProfile(profile);
        var state = GetState();
        await SaveStateAsync(
                state with { Profiles = state.Profiles.Add(profile) },
                cancellationToken)
            .ConfigureAwait(false);
        return profile;
    }

    /// <inheritdoc />
    public async Task<Profile> UpdateAsync(
        Guid profileId,
        ProfileDraft draft,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var state = GetState();
        _ = FindProfile(state, profileId);
        var profile = draft.ToProfile(profileId);
        ValidateProfile(profile);
        await SaveStateAsync(
                state with
                {
                    Profiles = state.Profiles
                        .Select(candidate => candidate.Id == profileId ? profile : candidate)
                        .ToImmutableArray()
                },
                cancellationToken)
            .ConfigureAwait(false);
        return profile;
    }

    /// <inheritdoc />
    public async Task DeleteAsync(
        Guid profileId,
        CancellationToken cancellationToken = default)
    {
        var state = GetState();
        _ = FindProfile(state, profileId);
        if (state.Profiles.Length <= 1)
        {
            throw new InvalidOperationException("至少保留一个档案，无法删除最后一个档案。");
        }

        if (state.LastProfileId == profileId)
        {
            throw new InvalidOperationException("当前档案不可删除，请先切换到其他档案。");
        }

        await SaveStateAsync(
                state with
                {
                    Profiles = state.Profiles
                        .Where(profile => profile.Id != profileId)
                        .ToImmutableArray()
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public ProfileManagementViewModel CreateViewModel(int selectedIndex = -1)
    {
        var state = GetState();
        var currentIndex = state.Profiles
            .Select((profile, index) => (profile, index))
            .First(candidate => candidate.profile.Id == state.LastProfileId)
            .index;
        var normalizedIndex = selectedIndex < 0
            ? currentIndex
            : Math.Clamp(selectedIndex, 0, state.Profiles.Length - 1);

        return new ProfileManagementViewModel(
            state.Profiles
                .Select((profile, index) => new ProfileListItemViewModel(
                    profile.DisplayName,
                    profile.DistroName,
                    !string.IsNullOrWhiteSpace(profile.VhdxPath),
                    profile.ShutdownMode,
                    profile.ShutdownTimeout,
                    profile.Id == state.LastProfileId,
                    index == normalizedIndex))
                .ToImmutableArray(),
            normalizedIndex,
            "N 新建  E 编辑  D 删除  Enter 切换  Esc 返回");
    }

    private async Task SaveStateAsync(
        ProfileStoreState state,
        CancellationToken cancellationToken)
    {
        await _store.SaveAsync(state, cancellationToken).ConfigureAwait(false);
        _state = state;
    }

    private static Profile FindProfile(ProfileStoreState state, Guid profileId)
    {
        if (profileId == Guid.Empty)
        {
            throw new ArgumentException("A non-empty profile identifier is required.", nameof(profileId));
        }

        return state.Profiles.FirstOrDefault(profile => profile.Id == profileId)
            ?? throw new KeyNotFoundException("The requested profile does not exist.");
    }

    private static void ValidateProfile(Profile profile)
    {
        var validation = ProfileValidator.Validate(profile);
        if (!validation.IsValid)
        {
            throw new ArgumentException(
                string.Join("；", validation.Errors.Select(error => error.Message)));
        }
    }

    private ProfileStoreState GetState() =>
        _state ?? throw new InvalidOperationException(
            "The profile service must be loaded before use.");
}
