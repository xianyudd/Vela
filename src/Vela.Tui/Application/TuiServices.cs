using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Vela.Core.Models;
using Vela.Core.Validation;
using Vela.Tui.Menu;
using Vela.Tui.Rendering;
using Vela.Windows.Configuration;
using Vela.Windows.Diagnostics;

namespace Vela.Tui.Application;

public interface IProfileStore
{
    Task<ProfileStoreState> LoadAsync(CancellationToken cancellationToken = default);

    Task<ProfileStoreState> LoadRequiredAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(ProfileStoreState state, CancellationToken cancellationToken = default);
}

public interface IProfileService
{
    Profile CurrentProfile { get; }

    ImmutableArray<Profile> Profiles { get; }

    Task<ProfileStoreState> LoadAsync(CancellationToken cancellationToken = default);

    Task<Profile> SelectAsync(Guid profileId, CancellationToken cancellationToken = default);

    Task<Profile> CreateAsync(ProfileDraft draft, CancellationToken cancellationToken = default);

    Task<Profile> UpdateAsync(Guid profileId, ProfileDraft draft, CancellationToken cancellationToken = default);

    Task DeleteAsync(Guid profileId, CancellationToken cancellationToken = default);

    ProfileManagementViewModel CreateViewModel(int selectedIndex = -1);
}

public sealed class JsonProfileStoreAdapter : IProfileStore
{
    private readonly JsonProfileStore _store;

    public JsonProfileStoreAdapter(JsonProfileStore store) =>
        _store = store ?? throw new ArgumentNullException(nameof(store));

    public Task<ProfileStoreState> LoadAsync(CancellationToken cancellationToken = default) =>
        _store.LoadAsync(cancellationToken);

    public Task<ProfileStoreState> LoadRequiredAsync(CancellationToken cancellationToken = default) =>
        _store.LoadRequiredAsync(cancellationToken);

    public Task SaveAsync(ProfileStoreState state, CancellationToken cancellationToken = default) =>
        _store.SaveAsync(state, cancellationToken);
}

public sealed class ProfileService : IProfileService
{
    private readonly IProfileStore _store;
    private ProfileStoreState? _state;

    public ProfileService(JsonProfileStore store)
        : this(new JsonProfileStoreAdapter(store))
    {
    }

    public ProfileService(IProfileStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
    }

    public Profile CurrentProfile =>
        GetState().Profiles.Single(profile => profile.Id == GetState().LastProfileId);

    public ImmutableArray<Profile> Profiles => GetState().Profiles;

    public async Task<ProfileStoreState> LoadAsync(CancellationToken cancellationToken = default)
    {
        _state = await _store.LoadRequiredAsync(cancellationToken).ConfigureAwait(false);
        return _state;
    }

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

public interface IRunHistoryReader
{
    Task<RunHistorySnapshot> ReadAsync(CancellationToken cancellationToken = default);
}

public sealed record RunHistoryEntry(
    Guid RunId,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    string ProfileDisplayName,
    OperationIntent? Intent,
    TerminalResult? TerminalResult,
    long? ReclaimedBytes,
    bool IsMalformed,
    string? ErrorMessage)
{
    public string? DistroName { get; init; }

    public string? VhdxPath { get; init; }

    public TimeSpan? Elapsed =>
        StartedAtUtc is { } started && CompletedAtUtc is { } completed
            ? completed - started
            : null;
}

public sealed record RunHistorySnapshot(
    ImmutableArray<RunHistoryEntry> Entries,
    string? ErrorMessage)
{
    public static RunHistorySnapshot Empty(string? errorMessage) =>
        new(ImmutableArray<RunHistoryEntry>.Empty, errorMessage);
}

public sealed class RunHistoryReader : IRunHistoryReader
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly AppPaths _paths;

    public RunHistoryReader(AppPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _paths = paths;
    }

    public async Task<RunHistorySnapshot> ReadAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!_paths.IsTrustedRootDirectory() || !_paths.IsTrustedLogsDirectory())
        {
            return RunHistorySnapshot.Empty("日志目录不受信任。");
        }

        if (!Directory.Exists(_paths.LogsDirectoryPath))
        {
            return RunHistorySnapshot.Empty(null);
        }

        var candidates = new List<RunHistoryCandidate>();
        try
        {
            foreach (var directory in Directory.EnumerateDirectories(_paths.LogsDirectoryPath))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var directoryName = Path.GetFileName(directory);
                if (!Guid.TryParseExact(directoryName, "D", out var runId) ||
                    !_paths.IsExpectedRunDirectory(runId, directory) ||
                    !_paths.IsTrustedRunDirectory(runId))
                {
                    continue;
                }

                candidates.Add(
                    await ReadCandidateAsync(runId, directory, cancellationToken)
                        .ConfigureAwait(false));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return RunHistorySnapshot.Empty("日志目录无法读取。");
        }

        return new RunHistorySnapshot(
            candidates
                .OrderByDescending(static candidate => candidate.SortTimestampUtc)
                .Take(20)
                .Select(static candidate => candidate.Entry)
                .ToImmutableArray(),
            null);
    }

    private async Task<RunHistoryCandidate> ReadCandidateAsync(
        Guid runId,
        string directory,
        CancellationToken cancellationToken)
    {
        var sortTimestampUtc = GetLastWriteTimeUtc(directory);
        var summaryPath = _paths.GetSummaryFilePath(runId);
        if (!_paths.IsTrustedPath(summaryPath) || !File.Exists(summaryPath))
        {
            return Invalid(runId, sortTimestampUtc, "summary.json 缺失。");
        }

        try
        {
            await using var stream = new FileStream(
                summaryPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite,
                bufferSize: 4096,
                useAsync: true);
            var summary = await JsonSerializer.DeserializeAsync<RunSummary>(
                    stream,
                    SerializerOptions,
                    cancellationToken)
                .ConfigureAwait(false);

            if (!IsValidSummary(summary, runId))
            {
                return Invalid(runId, sortTimestampUtc, "summary.json 无效。");
            }

            return new RunHistoryCandidate(
                summary!.StartedAtUtc,
                new RunHistoryEntry(
                    summary.RunId,
                    summary.StartedAtUtc,
                    summary.CompletedAtUtc,
                    summary.Profile.DisplayName,
                    summary.Intent,
                    summary.TerminalResult,
                    summary.ReclaimedBytes,
                    IsMalformed: false,
                    ErrorMessage: null)
                {
                    DistroName = summary.Profile.DistroName,
                    VhdxPath = summary.Profile.VhdxPath
                });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return Invalid(runId, sortTimestampUtc, "summary.json 无法读取。");
        }
    }

    private static bool IsValidSummary(RunSummary? summary, Guid directoryRunId) =>
        summary is not null &&
        summary.RunId != Guid.Empty &&
        summary.RunId == directoryRunId &&
        summary.Profile is not null &&
        summary.Profile.Id != Guid.Empty &&
        ProfileValidator.Validate(summary.Profile).IsValid &&
        Enum.IsDefined(summary.Intent) &&
        Enum.IsDefined(summary.TerminalResult) &&
        summary.CompletedAtUtc >= summary.StartedAtUtc;

    private static RunHistoryCandidate Invalid(
        Guid runId,
        DateTimeOffset sortTimestampUtc,
        string message) =>
        new(
            sortTimestampUtc,
            new RunHistoryEntry(
                runId,
                StartedAtUtc: null,
                CompletedAtUtc: null,
                ProfileDisplayName: "未知档案",
                Intent: null,
                TerminalResult: null,
                ReclaimedBytes: null,
                IsMalformed: true,
                ErrorMessage: message));

    private static DateTimeOffset GetLastWriteTimeUtc(string path)
    {
        try
        {
            return new DateTimeOffset(Directory.GetLastWriteTimeUtc(path), TimeSpan.Zero);
        }
        catch (Exception)
        {
            return DateTimeOffset.MinValue;
        }
    }

    private sealed record RunHistoryCandidate(
        DateTimeOffset SortTimestampUtc,
        RunHistoryEntry Entry);
}

public sealed record ProfileDraft(
    string DisplayName,
    string DistroName,
    string VhdxPath,
    ShutdownMode ShutdownMode,
    TimeSpan ShutdownTimeout)
{
    public static ProfileDraft FromProfile(Profile profile) => new(
        profile.DisplayName,
        profile.DistroName,
        profile.VhdxPath,
        profile.ShutdownMode,
        profile.ShutdownTimeout);

    public Profile ToProfile(Guid id) => new(
        id,
        DisplayName,
        DistroName,
        VhdxPath,
        ShutdownMode,
        ShutdownTimeout);
}

public sealed record ProfileListItemViewModel(
    string DisplayName,
    string DistroName,
    bool TargetConfigured,
    ShutdownMode ShutdownMode,
    TimeSpan ShutdownTimeout,
    bool IsCurrent,
    bool IsSelected);

public sealed record ProfileManagementViewModel(
    ImmutableArray<ProfileListItemViewModel> Profiles,
    int SelectedIndex,
    string ActionsMessage,
    string? ValidationError = null);

public interface ILogDirectoryOpener
{
    Task<LogDirectoryOpenResult> OpenAsync(CancellationToken cancellationToken = default);

    Task<LogDirectoryOpenResult> OpenAsync(
        Guid runId,
        CancellationToken cancellationToken = default) =>
        OpenAsync(cancellationToken);
}

public sealed record LogDirectoryOpenResult(bool Succeeded, string Message);

public sealed class WindowsLogDirectoryOpener : ILogDirectoryOpener
{
    public WindowsLogDirectoryOpener(AppPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
    }

    public Task<LogDirectoryOpenResult> OpenAsync(
        Guid runId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _ = runId;
        return Task.FromResult(CreateTuiOnlyResult());
    }

    public Task<LogDirectoryOpenResult> OpenAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(CreateTuiOnlyResult());
    }

    private static LogDirectoryOpenResult CreateTuiOnlyResult() =>
        new(false, "请在 Vela TUI 的“日志归档”中查看运行日志。");
}

public sealed class TuiSecondaryActionHandler
{
    private readonly IProfileService _profiles;
    private readonly IRunHistoryReader _history;
    private readonly ILogDirectoryOpener _logDirectoryOpener;

    public TuiSecondaryActionHandler(
        IProfileService profiles,
        IRunHistoryReader history,
        ILogDirectoryOpener logDirectoryOpener)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(logDirectoryOpener);
        _profiles = profiles;
        _history = history;
        _logDirectoryOpener = logDirectoryOpener;
    }

    public async Task<bool> HandleAsync(
        MainMenuAction action,
        TuiApplicationContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        switch (action)
        {
            case MainMenuAction.ManageProfiles:
                {
                    var controller = new ProfilePageController(_profiles);
                    context.OpenPage(controller);
                    await controller.RenderAsync(context, null).ConfigureAwait(false);
                    return false;
                }
            case MainMenuAction.RecentRuns:
                {
                    var snapshot = await _history.ReadAsync(cancellationToken).ConfigureAwait(false);
                    var controller = new RecentRunsPageController(snapshot, _logDirectoryOpener);
                    context.OpenPage(controller);
                    await controller.RenderListAsync(
                            context,
                            snapshot.ErrorMessage ?? $"已加载 {snapshot.Entries.Length} 条最近运行记录。")
                        .ConfigureAwait(false);
                    return false;
                }
            case MainMenuAction.OpenLogs:
                {
                    var result = await _logDirectoryOpener
                        .OpenAsync(cancellationToken)
                        .ConfigureAwait(false);
                    await context.RenderAsync(frame => frame with
                    {
                        Page = new DashboardPageViewModel(TuiScreen.Result),
                        Progress = new RunProgressViewModel(
                            result.Succeeded
                                ? RunProgressState.Succeeded
                                : RunProgressState.Failed,
                            result.Message,
                            result.Succeeded ? 100 : null)
                    }).ConfigureAwait(false);
                    return false;
                }
            default:
                return false;
        }
    }

    private sealed class ProfilePageController : ITuiPageController
    {
        private readonly IProfileService _profiles;
        private int _selectedIndex;

        public ProfilePageController(IProfileService profiles)
        {
            _profiles = profiles;
            _selectedIndex = profiles.CreateViewModel().SelectedIndex;
        }

        public async Task HandleKeyAsync(
            ConsoleKeyInfo key,
            TuiApplicationContext context,
            CancellationToken cancellationToken)
        {
            var count = _profiles.Profiles.Length;
            if (key.Key is ConsoleKey.UpArrow or ConsoleKey.DownArrow)
            {
                var offset = key.Key == ConsoleKey.UpArrow ? -1 : 1;
                _selectedIndex = (_selectedIndex + offset + count) % count;
                await RenderAsync(context, null).ConfigureAwait(false);
                return;
            }

            if (key.Key == ConsoleKey.Escape)
            {
                await context.ReturnToMenuAsync(new RunProgressViewModel(
                    RunProgressState.Idle,
                    "已返回主菜单。",
                    Percent: null)).ConfigureAwait(false);
                return;
            }

            if (key.Key == ConsoleKey.Enter)
            {
                var selected = _profiles.Profiles[_selectedIndex];
                var current = await _profiles
                    .SelectAsync(selected.Id, cancellationToken)
                    .ConfigureAwait(false);
                await context.ReturnToMenuAsync(new RunProgressViewModel(
                    RunProgressState.Succeeded,
                    $"已切换到档案：{current.DisplayName}。",
                    Percent: null)).ConfigureAwait(false);
                await context.RenderAsync(frame => frame with
                {
                    Dashboard = DashboardViewModel.CreateInitial(current)
                }).ConfigureAwait(false);
                return;
            }

            if (key.Key == ConsoleKey.N)
            {
                var editor = new ProfileEditorController(
                    this,
                    _profiles,
                    profileId: null,
                    new ProfileDraft(
                        "新档案",
                        "Ubuntu-24.04",
                        string.Empty,
                        ShutdownMode.Global,
                        TimeSpan.FromSeconds(45)));
                context.OpenPage(editor);
                await editor.RenderAsync(context).ConfigureAwait(false);
                return;
            }

            if (key.Key == ConsoleKey.E)
            {
                var selected = _profiles.Profiles[_selectedIndex];
                var editor = new ProfileEditorController(
                    this,
                    _profiles,
                    selected.Id,
                    ProfileDraft.FromProfile(selected));
                context.OpenPage(editor);
                await editor.RenderAsync(context).ConfigureAwait(false);
                return;
            }

            if (key.Key == ConsoleKey.D)
            {
                var selected = _profiles.Profiles[_selectedIndex];
                await context.RequestConfirmationAsync(
                    new ConfirmationViewModel(
                        $"确认删除档案“{selected.DisplayName}”？输入 YES；Esc 取消。",
                        "YES",
                        ImmutableArray<string>.Empty),
                    async (result, confirmationContext, token) =>
                    {
                        if (result.Status == ConfirmationInputStatus.Accepted)
                        {
                            try
                            {
                                await _profiles.DeleteAsync(selected.Id, token).ConfigureAwait(false);
                                _selectedIndex = Math.Clamp(
                                    _selectedIndex,
                                    0,
                                    _profiles.Profiles.Length - 1);
                                await RenderAsync(confirmationContext, "档案已删除。")
                                    .ConfigureAwait(false);
                            }
                            catch (OperationCanceledException) when (token.IsCancellationRequested)
                            {
                                throw;
                            }
                            catch (Exception)
                            {
                                await RenderAsync(
                                        confirmationContext,
                                        "档案删除失败，未保存更改。")
                                    .ConfigureAwait(false);
                            }
                        }
                        else
                        {
                            await RenderAsync(
                                    confirmationContext,
                                    result.Status == ConfirmationInputStatus.Cancelled
                                        ? "已取消删除档案。"
                                        : "确认输入未匹配 YES，未删除档案。")
                                .ConfigureAwait(false);
                        }
                    }).ConfigureAwait(false);
            }
        }

        public Task RenderAsync(
            TuiApplicationContext context,
            string? message) =>
            context.RenderAsync(frame => frame with
            {
                Page = new ProfileListPageViewModel(
                    _profiles.CreateViewModel(_selectedIndex) with
                    {
                        ValidationError = message
                    }),
                Progress = new RunProgressViewModel(
                    RunProgressState.Idle,
                    message ?? "档案管理：N 新建，E 编辑，D 删除，Enter 切换。",
                    Percent: null)
            });

        public async Task ReturnFromEditorAsync(
            TuiApplicationContext context,
            string message,
            int? selectedIndex = null)
        {
            if (selectedIndex is { } index)
            {
                _selectedIndex = Math.Clamp(index, 0, _profiles.Profiles.Length - 1);
            }

            context.OpenPage(this);
            await RenderAsync(context, message).ConfigureAwait(false);
        }
    }

    private sealed class ProfileEditorController : ITuiPageController
    {
        private const int MaxNameLength = 96;
        private const int MaxPathLength = 260;

        private readonly ProfilePageController _parent;
        private readonly IProfileService _profiles;
        private readonly Guid? _profileId;
        private readonly ProfileDraft _initial;
        private ProfileEditField _field = ProfileEditField.DisplayName;
        private string _displayName;
        private string _distroName;
        private string _vhdxPath;
        private ShutdownMode _shutdownMode;
        private string _timeoutText;
        private string _buffer;
        private bool _pathChanged;
        private string? _validationError;

        public ProfileEditorController(
            ProfilePageController parent,
            IProfileService profiles,
            Guid? profileId,
            ProfileDraft initial)
        {
            _parent = parent;
            _profiles = profiles;
            _profileId = profileId;
            _initial = initial;
            _displayName = initial.DisplayName;
            _distroName = initial.DistroName;
            _vhdxPath = initial.VhdxPath;
            _shutdownMode = initial.ShutdownMode;
            _timeoutText = ((int)initial.ShutdownTimeout.TotalSeconds)
                .ToString(CultureInfo.InvariantCulture);
            _buffer = _displayName;
        }

        public async Task HandleKeyAsync(
            ConsoleKeyInfo key,
            TuiApplicationContext context,
            CancellationToken cancellationToken)
        {
            if (key.Key == ConsoleKey.Escape)
            {
                await _parent.ReturnFromEditorAsync(context, "已取消档案编辑。")
                    .ConfigureAwait(false);
                return;
            }

            if (_field == ProfileEditField.ShutdownMode)
            {
                if (key.Key is ConsoleKey.LeftArrow or ConsoleKey.RightArrow or
                    ConsoleKey.UpArrow or ConsoleKey.DownArrow)
                {
                    _shutdownMode = _shutdownMode == ShutdownMode.Global
                        ? ShutdownMode.Distro
                        : ShutdownMode.Global;
                    _validationError = null;
                    await RenderAsync(context).ConfigureAwait(false);
                    return;
                }
            }
            else
            {
                var maxLength = _field == ProfileEditField.VhdxPath
                    ? MaxPathLength
                    : MaxNameLength;
                var updated = UpdateBuffer(_buffer, key, maxLength);
                if (!string.Equals(updated, _buffer, StringComparison.Ordinal))
                {
                    _buffer = updated;
                    _validationError = null;
                    await RenderAsync(context).ConfigureAwait(false);
                    return;
                }
            }

            if (key.Key != ConsoleKey.Enter)
            {
                return;
            }

            if (!TryCommitCurrentField())
            {
                await RenderAsync(context).ConfigureAwait(false);
                return;
            }

            if (_field != ProfileEditField.ShutdownTimeout)
            {
                AdvanceField();
                await RenderAsync(context).ConfigureAwait(false);
                return;
            }

            var draft = new ProfileDraft(
                _displayName,
                _distroName,
                _vhdxPath,
                _shutdownMode,
                TimeSpan.FromSeconds(int.Parse(_timeoutText, CultureInfo.InvariantCulture)));

            if (_profileId is not null && TargetChanged(draft))
            {
                await context.RequestConfirmationAsync(
                    new ConfirmationViewModel(
                        "编辑涉及执行目标，输入 YES 保存；Esc 取消。",
                        "YES",
                        ImmutableArray<string>.Empty),
                    async (result, confirmationContext, token) =>
                    {
                        if (result.Status != ConfirmationInputStatus.Accepted)
                        {
                            await _parent.ReturnFromEditorAsync(
                                    confirmationContext,
                                    result.Status == ConfirmationInputStatus.Cancelled
                                        ? "已取消保存档案。"
                                        : "确认输入未匹配 YES，未保存档案。")
                                .ConfigureAwait(false);
                            return;
                        }

                        await SaveAsync(draft, confirmationContext, token).ConfigureAwait(false);
                    }).ConfigureAwait(false);
                return;
            }

            await SaveAsync(draft, context, cancellationToken).ConfigureAwait(false);
        }

        public Task RenderAsync(TuiApplicationContext context)
        {
            var sensitive = _field == ProfileEditField.VhdxPath;
            var displayValue = sensitive
                ? _buffer.Length == 0
                    ? "当前路径不会显示；输入新路径后按 Enter"
                    : $"已输入 {_buffer.Length} 个字符"
                : _field == ProfileEditField.ShutdownMode
                    ? TuiDisplayText.LabelForShutdownMode(_shutdownMode)
                    : _buffer;
            var instruction = _field == ProfileEditField.ShutdownMode
                ? "使用方向键选择停止模式，Enter 下一步，Esc 取消。"
                : "输入值，Backspace 删除，Enter 下一步，Esc 取消。";

            return context.RenderAsync(frame => frame with
            {
                Page = new ProfileEditPageViewModel(
                    _profileId is null ? "新建档案" : "编辑档案",
                    _field,
                    FieldLabel(_field),
                    displayValue,
                    sensitive,
                    _validationError),
                Progress = new RunProgressViewModel(
                    RunProgressState.AwaitingConfirmation,
                    instruction,
                    Percent: null)
            });
        }

        private async Task SaveAsync(
            ProfileDraft draft,
            TuiApplicationContext context,
            CancellationToken cancellationToken)
        {
            try
            {
                if (_profileId is { } profileId)
                {
                    var updated = await _profiles
                        .UpdateAsync(profileId, draft, cancellationToken)
                        .ConfigureAwait(false);
                    await _parent.ReturnFromEditorAsync(
                            context,
                            $"已更新档案：{updated.DisplayName}。")
                        .ConfigureAwait(false);
                }
                else
                {
                    var created = await _profiles
                        .CreateAsync(draft, cancellationToken)
                        .ConfigureAwait(false);
                    await _parent.ReturnFromEditorAsync(
                            context,
                            $"已新建档案：{created.DisplayName}。",
                            _profiles.Profiles.Length - 1)
                        .ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                _validationError = "档案验证失败，请检查各字段。";
                context.OpenPage(this);
                await RenderAsync(context).ConfigureAwait(false);
            }
        }

        private bool TryCommitCurrentField()
        {
            _validationError = null;
            switch (_field)
            {
                case ProfileEditField.DisplayName:
                    if (string.IsNullOrWhiteSpace(_buffer))
                    {
                        _validationError = "显示名称不能为空。";
                        return false;
                    }

                    _displayName = _buffer;
                    return true;
                case ProfileEditField.DistroName:
                    if (string.IsNullOrWhiteSpace(_buffer))
                    {
                        _validationError = "发行版名称不能为空。";
                        return false;
                    }

                    _distroName = _buffer;
                    return true;
                case ProfileEditField.VhdxPath:
                    if (_buffer.Length > 0)
                    {
                        _vhdxPath = _buffer;
                        _pathChanged = true;
                    }

                    if (string.IsNullOrWhiteSpace(_vhdxPath))
                    {
                        _validationError = "请输入 VHDX 路径。";
                        return false;
                    }

                    return true;
                case ProfileEditField.ShutdownMode:
                    return true;
                case ProfileEditField.ShutdownTimeout:
                    if (!int.TryParse(
                            _buffer,
                            NumberStyles.None,
                            CultureInfo.InvariantCulture,
                            out var seconds) ||
                        seconds is < 5 or > 300)
                    {
                        _validationError = "停止超时必须是 5–300 的整数秒。";
                        return false;
                    }

                    _timeoutText = seconds.ToString(CultureInfo.InvariantCulture);
                    return true;
                default:
                    return false;
            }
        }

        private void AdvanceField()
        {
            _field = _field switch
            {
                ProfileEditField.DisplayName => ProfileEditField.DistroName,
                ProfileEditField.DistroName => ProfileEditField.VhdxPath,
                ProfileEditField.VhdxPath => ProfileEditField.ShutdownMode,
                ProfileEditField.ShutdownMode => ProfileEditField.ShutdownTimeout,
                _ => ProfileEditField.ShutdownTimeout
            };
            _buffer = _field switch
            {
                ProfileEditField.DisplayName => _displayName,
                ProfileEditField.DistroName => _distroName,
                ProfileEditField.VhdxPath => string.Empty,
                ProfileEditField.ShutdownTimeout => _timeoutText,
                _ => string.Empty
            };
            _validationError = null;
        }

        private bool TargetChanged(ProfileDraft draft) =>
            _pathChanged ||
            !string.Equals(
                draft.DistroName,
                _initial.DistroName,
                StringComparison.Ordinal) ||
            draft.ShutdownMode != _initial.ShutdownMode ||
            draft.ShutdownTimeout != _initial.ShutdownTimeout;

        private static string UpdateBuffer(
            string value,
            ConsoleKeyInfo key,
            int maxLength)
        {
            if (key.Key == ConsoleKey.Backspace)
            {
                return value.Length == 0 ? value : value[..^1];
            }

            return !char.IsControl(key.KeyChar) && value.Length < maxLength
                ? value + key.KeyChar
                : value;
        }

        private static string FieldLabel(ProfileEditField field) => field switch
        {
            ProfileEditField.DisplayName => "显示名称",
            ProfileEditField.DistroName => "发行版名称",
            ProfileEditField.VhdxPath => "VHDX 路径",
            ProfileEditField.ShutdownMode => "停止模式",
            ProfileEditField.ShutdownTimeout => "停止超时秒数",
            _ => "档案字段"
        };
    }

    private sealed class RecentRunsPageController : ITuiPageController
    {
        private readonly RunHistorySnapshot _snapshot;
        private readonly ILogDirectoryOpener _logDirectoryOpener;
        private int _selectedIndex;
        private bool _showDetail;

        public RecentRunsPageController(
            RunHistorySnapshot snapshot,
            ILogDirectoryOpener logDirectoryOpener)
        {
            _snapshot = snapshot;
            _logDirectoryOpener = logDirectoryOpener;
        }

        public async Task HandleKeyAsync(
            ConsoleKeyInfo key,
            TuiApplicationContext context,
            CancellationToken cancellationToken)
        {
            if (_showDetail)
            {
                if (key.Key == ConsoleKey.Escape)
                {
                    _showDetail = false;
                    await RenderListAsync(context, "已返回最近运行列表。")
                        .ConfigureAwait(false);
                    return;
                }

                if (key.Key == ConsoleKey.O &&
                    CurrentEntry() is { IsMalformed: false } entry)
                {
                    var result = await _logDirectoryOpener
                        .OpenAsync(entry.RunId, cancellationToken)
                        .ConfigureAwait(false);
                    await RenderDetailAsync(context, result.Message).ConfigureAwait(false);
                }

                return;
            }

            if (key.Key is ConsoleKey.UpArrow or ConsoleKey.DownArrow &&
                !_snapshot.Entries.IsDefaultOrEmpty)
            {
                var offset = key.Key == ConsoleKey.UpArrow ? -1 : 1;
                _selectedIndex =
                    (_selectedIndex + offset + _snapshot.Entries.Length) %
                    _snapshot.Entries.Length;
                await RenderListAsync(context, "↑↓ 选择记录，Enter 查看详情。")
                    .ConfigureAwait(false);
                return;
            }

            if (key.Key == ConsoleKey.Enter)
            {
                if (_snapshot.Entries.IsDefaultOrEmpty)
                {
                    await RenderListAsync(context, "暂无可查看的运行记录。")
                        .ConfigureAwait(false);
                    return;
                }

                _showDetail = true;
                await RenderDetailAsync(
                        context,
                        "只读详情：Esc 返回列表；日志在日志归档中查看。")
                    .ConfigureAwait(false);
                return;
            }

            if (key.Key == ConsoleKey.Escape)
            {
                await context.ReturnToMenuAsync(new RunProgressViewModel(
                    RunProgressState.Idle,
                    "已返回主菜单。",
                    Percent: null)).ConfigureAwait(false);
            }
        }

        public Task RenderListAsync(
            TuiApplicationContext context,
            string message) =>
            context.RenderAsync(frame => frame with
            {
                Page = new RecentRunsPageViewModel(
                    _snapshot.Entries
                        .Select(static entry => new RecentRunListItemViewModel(
                            entry.StartedAtUtc,
                            entry.ProfileDisplayName,
                            entry.Intent,
                            entry.TerminalResult,
                            entry.ReclaimedBytes,
                            entry.IsMalformed,
                            entry.ErrorMessage))
                        .ToImmutableArray(),
                    _selectedIndex,
                    _snapshot.ErrorMessage),
                Progress = new RunProgressViewModel(
                    _snapshot.ErrorMessage is null
                        ? RunProgressState.Succeeded
                        : RunProgressState.Failed,
                    message,
                    _snapshot.ErrorMessage is null ? 100 : null)
            });

        private Task RenderDetailAsync(
            TuiApplicationContext context,
            string message)
        {
            var entry = CurrentEntry()
                ?? throw new InvalidOperationException("The selected run is unavailable.");
            return context.RenderAsync(frame => frame with
            {
                Page = new RecentRunDetailPageViewModel(
                    entry.IsMalformed,
                    entry.ProfileDisplayName,
                    entry.Intent,
                    entry.TerminalResult,
                    entry.StartedAtUtc,
                    entry.CompletedAtUtc,
                    entry.Elapsed,
                    entry.ReclaimedBytes,
                    LogsAvailable: !entry.IsMalformed,
                    entry.ErrorMessage),
                Progress = new RunProgressViewModel(
                    entry.IsMalformed
                        ? RunProgressState.Failed
                        : RunProgressState.Succeeded,
                    message,
                    entry.IsMalformed ? null : 100)
            });
        }

        private RunHistoryEntry? CurrentEntry() =>
            _snapshot.Entries.IsDefaultOrEmpty
                ? null
                : _snapshot.Entries[
                    Math.Clamp(_selectedIndex, 0, _snapshot.Entries.Length - 1)];
    }
}
