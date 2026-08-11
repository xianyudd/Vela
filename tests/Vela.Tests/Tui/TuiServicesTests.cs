using System.Collections.Immutable;
using System.Text.Json;
using Vela.Core.Models;
using Vela.Tui;
using Vela.Tui.Application;
using Vela.Tui.Menu;
using Vela.Tui.Rendering;
using Vela.Windows.Configuration;
using Vela.Windows.Diagnostics;

namespace Vela.Tests.Tui;

public sealed class TuiServicesTests
{
    [Theory]
    [InlineData(AutomaticPreflightStatus.Ready, VelaTerminalTheme.Success)]
    [InlineData(AutomaticPreflightStatus.Checking, VelaTerminalTheme.Info)]
    [InlineData(AutomaticPreflightStatus.Attention, VelaTerminalTheme.Attention)]
    [InlineData(AutomaticPreflightStatus.Failed, VelaTerminalTheme.Error)]
    [InlineData(AutomaticPreflightStatus.Stale, VelaTerminalTheme.Muted)]
    public void TerminalTheme_maps_preflight_state_to_semantic_color(
        AutomaticPreflightStatus status,
        string expectedScheme) =>
        Assert.Equal(expectedScheme, VelaTerminalTheme.SchemeForPreflight(status));

    [Fact]
    public async Task RunLogReader_reads_the_newest_trusted_log_and_bounds_visible_lines()
    {
        using var root = TestRoot.Create();
        var paths = new AppPaths(root.Path);
        var older = Guid.NewGuid();
        var newer = Guid.NewGuid();
        Directory.CreateDirectory(paths.GetRunDirectory(older));
        Directory.CreateDirectory(paths.GetRunDirectory(newer));
        await File.WriteAllLinesAsync(paths.GetRunLogFilePath(older), ["old"]);
        await File.WriteAllLinesAsync(
            paths.GetRunLogFilePath(newer),
            Enumerable.Range(1, 30).Select(index => $"line-{index}"));
        Directory.SetLastWriteTimeUtc(paths.GetRunDirectory(older), DateTime.UtcNow.AddMinutes(-2));
        Directory.SetLastWriteTimeUtc(paths.GetRunDirectory(newer), DateTime.UtcNow);

        var result = await new RunLogReader(paths).ReadLatestAsync(maxLines: 12);

        Assert.Null(result.ErrorMessage);
        Assert.Equal(12, result.Lines.Length);
        Assert.All(result.Lines, line => Assert.Equal("日志格式无效", line.Text));
    }

    [Fact]
    public async Task RunLogReader_projects_journal_metadata_without_native_output_or_paths()
    {
        using var root = TestRoot.Create();
        var paths = new AppPaths(root.Path);
        var runId = Guid.NewGuid();
        Directory.CreateDirectory(paths.GetRunDirectory(runId));
        await File.WriteAllTextAsync(paths.GetRunLogFilePath(runId),
            "[6] 2026-08-09T17:11:57Z Error Inventory PreflightDiagnostic D:\\private\\ext4.vhdx native output");

        var result = await new RunLogReader(paths).ReadLatestAsync();

        var line = Assert.Single(result.Lines);
        Assert.Equal("[6] 2026-08-09T17:11:57Z Error Inventory PreflightDiagnostic", line.Text);
        Assert.Equal(RunEventLevel.Error, line.Level);
        Assert.DoesNotContain("private", line.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("native output", line.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RunLogReader_replaces_malformed_structured_lines_with_a_fixed_message()
    {
        using var root = TestRoot.Create();
        var paths = new AppPaths(root.Path);
        var runId = Guid.NewGuid();
        Directory.CreateDirectory(paths.GetRunDirectory(runId));
        await File.WriteAllTextAsync(
            paths.GetRunLogFilePath(runId),
            "[7] 2026-08-09T17:11:57Z Error Inventory D:\\private\\ext4.vhdx");

        var result = await new RunLogReader(paths).ReadLatestAsync();

        var projected = Assert.Single(result.Lines);
        Assert.Equal("日志格式无效", projected.Text);
        Assert.DoesNotContain("private", projected.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ext4", projected.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RunLogReader_returns_a_stable_empty_state_when_no_log_exists()
    {
        using var root = TestRoot.Create();
        var paths = new AppPaths(root.Path);
        Directory.CreateDirectory(paths.LogsDirectoryPath);

        var result = await new RunLogReader(paths).ReadLatestAsync();

        Assert.Empty(result.Lines);
        Assert.Equal("尚无可读取的运行日志。", result.ErrorMessage);
    }

    [Fact]
    public async Task RunLogReader_reads_the_selected_run_for_the_inline_detail_view()
    {
        using var root = TestRoot.Create();
        var paths = new AppPaths(root.Path);
        var selectedRun = Guid.NewGuid();
        var otherRun = Guid.NewGuid();
        Directory.CreateDirectory(paths.GetRunDirectory(selectedRun));
        Directory.CreateDirectory(paths.GetRunDirectory(otherRun));
        await File.WriteAllTextAsync(
            paths.GetRunLogFilePath(selectedRun),
            "[1] 2026-08-10T14:15:04.102Z Information Validation SelectedRun");
        await File.WriteAllTextAsync(
            paths.GetRunLogFilePath(otherRun),
            "[1] 2026-08-10T14:15:04.102Z Information Validation OtherRun");

        var result = await new RunLogReader(paths).ReadAsync(selectedRun);

        var line = Assert.Single(result.Lines);
        Assert.Contains("SelectedRun", line.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("OtherRun", line.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProfileService_SelectAsync_PersistsLastProfileIdAndCurrentProfile()
    {
        using var root = TestRoot.Create();
        var paths = new AppPaths(root.Path);
        var store = new JsonProfileStore(paths);
        var initialState = await store.LoadAsync(CancellationToken.None);
        var secondProfile = new Profile(
            Guid.Parse("f0c9b6af-10d9-4f51-a66e-4e7ecf4e8401"),
            "Secondary target",
            "Ubuntu-22.04",
            @"D:\DevTools\WSL2\Ubuntu22.04\ext4.vhdx",
            ShutdownMode.Distro,
            TimeSpan.FromSeconds(30));
        await store.SaveAsync(
            initialState with
            {
                Profiles = initialState.Profiles.Add(secondProfile)
            },
            CancellationToken.None);

        var service = new ProfileService(store);
        await service.LoadAsync();
        var selected = await service.SelectAsync(secondProfile.Id);
        var reloaded = await store.LoadAsync(CancellationToken.None);

        Assert.Equal(secondProfile, selected);
        Assert.Equal(secondProfile, service.CurrentProfile);
        Assert.Equal(secondProfile.Id, reloaded.LastProfileId);
        var current = Assert.Single(service.CreateViewModel().Profiles.Where(item => item.IsCurrent));
        Assert.Equal(secondProfile.DisplayName, current.DisplayName);
        Assert.Equal(secondProfile.DistroName, current.DistroName);
        Assert.True(current.TargetConfigured);
        Assert.DoesNotContain(secondProfile.VhdxPath, JsonSerializer.Serialize(current), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunHistoryReader_MarksBadSummariesAndIgnoresNonGuidDirectories()
    {
        using var root = TestRoot.Create();
        var paths = new AppPaths(root.Path);
        Directory.CreateDirectory(paths.LogsDirectoryPath);
        Directory.CreateDirectory(Path.Combine(paths.LogsDirectoryPath, "not-a-run"));

        var profile = JsonProfileStore.CreateInitialState().Profiles[0];
        var validRunId = Guid.Parse("a7e70e4c-4e9a-4e65-a8de-8d1f9dca2d4b");
        var invalidRunId = Guid.Parse("f2e95e1f-4ab2-4c7c-bfd5-2e5f07f8c6d0");
        await WriteSummaryAsync(
            paths,
            new RunSummary(
                validRunId,
                profile,
                OperationIntent.Compact,
                DateTimeOffset.UtcNow.AddMinutes(-2),
                DateTimeOffset.UtcNow.AddMinutes(-1),
                null,
                null,
                TerminalResult.Succeeded));
        var invalidDirectory = paths.GetRunDirectory(invalidRunId);
        Directory.CreateDirectory(invalidDirectory);
        await File.WriteAllTextAsync(paths.GetSummaryFilePath(invalidRunId), "{not json");

        var result = await new RunHistoryReader(paths).ReadAsync();

        Assert.Equal(2, result.Entries.Length);
        Assert.Contains(
            result.Entries,
            entry => entry.RunId == validRunId &&
                     !entry.IsMalformed &&
                     entry.DistroName == profile.DistroName &&
                     entry.VhdxPath == profile.VhdxPath);
        var invalid = Assert.Single(result.Entries, entry => entry.RunId == invalidRunId);
        Assert.True(invalid.IsMalformed);
        Assert.DoesNotContain(result.Entries, entry => entry.ProfileDisplayName == "not-a-run");
    }

    [Fact]
    public async Task RunHistoryReader_LimitsToTwentyAndUsesNewestStartedAtFirst()
    {
        using var root = TestRoot.Create();
        var paths = new AppPaths(root.Path);
        var profile = JsonProfileStore.CreateInitialState().Profiles[0];
        var oldest = DateTimeOffset.UtcNow.AddHours(-30);

        for (var index = 0; index < 21; index++)
        {
            var runId = Guid.NewGuid();
            var started = oldest.AddMinutes(index);
            await WriteSummaryAsync(
                paths,
                new RunSummary(
                    runId,
                    profile,
                    OperationIntent.Preflight,
                    started,
                    started.AddSeconds(1),
                    null,
                    null,
                    TerminalResult.CompletedWithNoReclaim));
        }

        var result = await new RunHistoryReader(paths).ReadAsync();

        Assert.Equal(20, result.Entries.Length);
        Assert.True(result.Entries.Zip(result.Entries.Skip(1)).All(pair => pair.First.StartedAtUtc >= pair.Second.StartedAtUtc));
        Assert.DoesNotContain(result.Entries, entry => entry.StartedAtUtc == oldest);
    }

    [Fact]
    public async Task TuiSecondaryActionHandler_RecentRuns_RoutesListDetailAndBackToMenu()
    {
        using var root = TestRoot.Create();
        var paths = new AppPaths(root.Path);
        var store = new JsonProfileStore(paths);
        await store.LoadAsync(CancellationToken.None);
        var profileService = new ProfileService(store);
        await profileService.LoadAsync();
        var runId = Guid.Parse("e0d6d9f3-9ec2-43b5-9f90-76d949d17f08");
        var started = DateTimeOffset.UtcNow.AddMinutes(-2);
        await WriteSummaryAsync(paths, new RunSummary(
            runId,
            profileService.CurrentProfile,
            OperationIntent.Compact,
            started,
            started.AddSeconds(12),
            new VhdxSnapshot(started, "before", 200, started, false, new DriveSnapshot("D:\\", 1000, 500)),
            new VhdxSnapshot(started.AddSeconds(12), "after", 150, started.AddSeconds(12), false, new DriveSnapshot("D:\\", 1000, 500)),
            TerminalResult.Succeeded));

        var opener = new FakeLogDirectoryOpener();
        var handler = new TuiSecondaryActionHandler(profileService, new RunHistoryReader(paths), opener);
        var input = new FakeTuiInput(
            new ConsoleKeyInfo('\0', ConsoleKey.DownArrow, false, false, false),
            new ConsoleKeyInfo('\0', ConsoleKey.UpArrow, false, false, false),
            new ConsoleKeyInfo('\0', ConsoleKey.Enter, false, false, false),
            new ConsoleKeyInfo('\0', ConsoleKey.Enter, false, false, false),
            new ConsoleKeyInfo('\0', ConsoleKey.O, false, false, false),
            new ConsoleKeyInfo('\0', ConsoleKey.Escape, false, false, false),
            new ConsoleKeyInfo('\0', ConsoleKey.Escape, false, false, false),
            new ConsoleKeyInfo('\0', ConsoleKey.Escape, false, false, false));
        var sink = new RecordingFrameSink();
        var application = new TuiApplication(
            CreateFrame(profileService.CurrentProfile) with { SelectedMenuIndex = 3 },
            input,
            sink,
            (action, context, cancellationToken) => action == MainMenuAction.RecentRuns
                ? handler.HandleAsync(action, context, cancellationToken)
                : Task.FromResult(true));

        await application.RunAsync();

        var detail = sink.Frames.Last(frame => frame.Page is RecentRunDetailPageViewModel);
        var detailPage = Assert.IsType<RecentRunDetailPageViewModel>(detail.Page);
        Assert.Equal(TerminalResult.Succeeded, detailPage.TerminalResult);
        Assert.Contains("耗时", new FrameRenderer().BuildMarkup(detail), StringComparison.Ordinal);
        Assert.DoesNotContain(runId.ToString(), new FrameRenderer().BuildMarkup(detail), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(runId, opener.RunId);
        Assert.IsType<DashboardPageViewModel>(sink.Frames[^1].Page);
    }

    [Fact]
    public async Task TuiSecondaryActionHandler_OpenLogs_InvokesInjectedOpener()
    {
        using var root = TestRoot.Create();
        var paths = new AppPaths(root.Path);
        var store = new JsonProfileStore(paths);
        await store.LoadAsync(CancellationToken.None);
        var profileService = new ProfileService(store);
        await profileService.LoadAsync();
        var opener = new FakeLogDirectoryOpener();
        var handler = new TuiSecondaryActionHandler(
            profileService,
            new RunHistoryReader(paths),
            opener);
        var input = new FakeTuiInput(
            new ConsoleKeyInfo('\0', ConsoleKey.Enter, false, false, false),
            new ConsoleKeyInfo('\0', ConsoleKey.Escape, false, false, false));
        var sink = new RecordingFrameSink();
        var frame = CreateFrame(profileService.CurrentProfile) with { SelectedMenuIndex = 4 };
        var application = new TuiApplication(
            frame,
            input,
            sink,
            (action, context, cancellationToken) => action == MainMenuAction.OpenLogs
                ? handler.HandleAsync(action, context, cancellationToken)
                : Task.FromResult(true));

        await application.RunAsync();

        Assert.Equal(1, opener.CallCount);
        Assert.Contains(sink.Frames, rendered => rendered.Progress.Message == "opened");
    }

    [Fact]
    public async Task TuiProfileEditor_HidesPathsAndSavesTypedModeAndMinimumTimeout()
    {
        const string existingPath = @"D:\private\existing.vhdx";
        const string replacementPath = @"E:\targets\updated.vhdx";
        var profile = CreateProfile(existingPath);
        var store = new InMemoryProfileStore(CreateState(profile));
        var profileService = new ProfileService(store);
        await profileService.LoadAsync();
        var handler = CreateSecondaryHandler(profileService);
        var keys = new List<ConsoleKeyInfo>
        {
            Key(ConsoleKey.Enter),
            Key(ConsoleKey.E),
            Key(ConsoleKey.Enter),
            Key(ConsoleKey.Enter),
        };
        keys.AddRange(replacementPath.Select(Character));
        keys.Add(Key(ConsoleKey.Enter));
        keys.Add(Key(ConsoleKey.RightArrow));
        keys.Add(Key(ConsoleKey.Enter));
        keys.Add(Key(ConsoleKey.Backspace));
        keys.Add(Key(ConsoleKey.Backspace));
        keys.Add(Character('5'));
        keys.Add(Key(ConsoleKey.Enter));
        keys.AddRange("YES".Select(Character));
        keys.Add(Key(ConsoleKey.Enter));
        keys.Add(Key(ConsoleKey.Escape));
        keys.Add(Key(ConsoleKey.Escape));
        var sink = new RecordingFrameSink();
        var application = new TuiApplication(
            CreateFrame(profile) with { SelectedMenuIndex = 2 },
            new FakeTuiInput(keys.ToArray()),
            sink,
            (action, context, cancellationToken) => action == MainMenuAction.ManageProfiles
                ? handler.HandleAsync(action, context, cancellationToken)
                : Task.FromResult(true));

        await application.RunAsync();

        var pathFrames = sink.Frames
            .Where(frame => frame.Page is ProfileEditPageViewModel
            {
                Field: ProfileEditField.VhdxPath
            })
            .ToArray();
        Assert.NotEmpty(pathFrames);
        Assert.Contains(
            pathFrames,
            frame => ((ProfileEditPageViewModel)frame.Page).DisplayValue ==
                "当前路径不会显示；输入新路径后按 Enter");
        Assert.Contains(
            pathFrames,
            frame => ((ProfileEditPageViewModel)frame.Page).DisplayValue ==
                $"已输入 {replacementPath.Length} 个字符");
        foreach (var frame in sink.Frames)
        {
            var markup = new FrameRenderer().BuildMarkup(frame, 120, 30);
            Assert.DoesNotContain(existingPath, markup, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(replacementPath, markup, StringComparison.OrdinalIgnoreCase);
        }

        var saved = Assert.Single(store.State.Profiles);
        Assert.Equal(replacementPath, saved.VhdxPath);
        Assert.Equal(ShutdownMode.Distro, saved.ShutdownMode);
        Assert.Equal(TimeSpan.FromSeconds(5), saved.ShutdownTimeout);
        Assert.Equal(1, store.SaveCallCount);
    }

    [Fact]
    public async Task TuiProfileEditor_AcceptsMaximumIntegerTimeoutAfterExactConfirmation()
    {
        var profile = CreateProfile();
        var store = new InMemoryProfileStore(CreateState(profile));
        var profileService = new ProfileService(store);
        await profileService.LoadAsync();
        var handler = CreateSecondaryHandler(profileService);
        var keys = CreateTimeoutEditKeys("300", "YES", inputIsValid: true);
        var application = new TuiApplication(
            CreateFrame(profile) with { SelectedMenuIndex = 2 },
            new FakeTuiInput(keys),
            new RecordingFrameSink(),
            (action, context, cancellationToken) => action == MainMenuAction.ManageProfiles
                ? handler.HandleAsync(action, context, cancellationToken)
                : Task.FromResult(true));

        await application.RunAsync();

        Assert.Equal(
            TimeSpan.FromSeconds(300),
            Assert.Single(store.State.Profiles).ShutdownTimeout);
        Assert.Equal(1, store.SaveCallCount);
    }

    [Theory]
    [InlineData("4")]
    [InlineData("301")]
    [InlineData("5.0")]
    [InlineData("-5")]
    [InlineData("5,0")]
    public async Task TuiProfileEditor_RejectsNonIntegerOrOutOfRangeTimeout(string timeout)
    {
        var profile = CreateProfile();
        var store = new InMemoryProfileStore(CreateState(profile));
        var profileService = new ProfileService(store);
        await profileService.LoadAsync();
        var handler = CreateSecondaryHandler(profileService);
        var sink = new RecordingFrameSink();
        var application = new TuiApplication(
            CreateFrame(profile) with { SelectedMenuIndex = 2 },
            new FakeTuiInput(CreateTimeoutEditKeys(timeout, null, inputIsValid: false)),
            sink,
            (action, context, cancellationToken) => action == MainMenuAction.ManageProfiles
                ? handler.HandleAsync(action, context, cancellationToken)
                : Task.FromResult(true));

        await application.RunAsync();

        Assert.Contains(
            sink.Frames,
            frame => frame.Page is ProfileEditPageViewModel
            {
                Field: ProfileEditField.ShutdownTimeout,
                ValidationError: "停止超时必须是 5–300 的整数秒。"
            });
        Assert.Equal(TimeSpan.FromSeconds(45), profileService.CurrentProfile.ShutdownTimeout);
        Assert.Equal(0, store.SaveCallCount);
    }

    [Fact]
    public async Task TuiProfileEditor_RejectsTargetChangeWithoutExactYes()
    {
        var profile = CreateProfile();
        var store = new InMemoryProfileStore(CreateState(profile));
        var profileService = new ProfileService(store);
        await profileService.LoadAsync();
        var handler = CreateSecondaryHandler(profileService);
        var sink = new RecordingFrameSink();
        var application = new TuiApplication(
            CreateFrame(profile) with { SelectedMenuIndex = 2 },
            new FakeTuiInput(CreateTimeoutEditKeys("300", "yes", inputIsValid: true)),
            sink,
            (action, context, cancellationToken) => action == MainMenuAction.ManageProfiles
                ? handler.HandleAsync(action, context, cancellationToken)
                : Task.FromResult(true));

        await application.RunAsync();

        Assert.Contains(
            sink.Frames,
            frame => frame.Progress.Message == "确认输入未匹配 YES，未保存档案。");
        Assert.Equal(TimeSpan.FromSeconds(45), profileService.CurrentProfile.ShutdownTimeout);
        Assert.Equal(0, store.SaveCallCount);
    }

    [Fact]
    public async Task ProfileService_UpdateAsync_UsesProfileValidator()
    {
        var profile = CreateProfile();
        var store = new InMemoryProfileStore(CreateState(profile));
        var profileService = new ProfileService(store);
        await profileService.LoadAsync();
        var invalid = ProfileDraft.FromProfile(profile) with
        {
            VhdxPath = @"relative\target.vhdx"
        };

        await Assert.ThrowsAsync<ArgumentException>(
            () => profileService.UpdateAsync(profile.Id, invalid));

        Assert.Equal(profile, profileService.CurrentProfile);
        Assert.Equal(0, store.SaveCallCount);
    }

    private static TuiSecondaryActionHandler CreateSecondaryHandler(
        IProfileService profileService) =>
        new(
            profileService,
            new EmptyRunHistoryReader(),
            new FakeLogDirectoryOpener());

    private static ConsoleKeyInfo[] CreateTimeoutEditKeys(
        string timeout,
        string? confirmation,
        bool inputIsValid)
    {
        var keys = new List<ConsoleKeyInfo>
        {
            Key(ConsoleKey.Enter),
            Key(ConsoleKey.E),
            Key(ConsoleKey.Enter),
            Key(ConsoleKey.Enter),
            Key(ConsoleKey.Enter),
            Key(ConsoleKey.Enter),
            Key(ConsoleKey.Backspace),
            Key(ConsoleKey.Backspace)
        };
        keys.AddRange(timeout.Select(Character));
        keys.Add(Key(ConsoleKey.Enter));
        if (inputIsValid)
        {
            keys.AddRange((confirmation ?? string.Empty).Select(Character));
            keys.Add(Key(ConsoleKey.Enter));
            keys.Add(Key(ConsoleKey.Escape));
            keys.Add(Key(ConsoleKey.Escape));
        }
        else
        {
            keys.Add(Key(ConsoleKey.Escape));
            keys.Add(Key(ConsoleKey.Escape));
            keys.Add(Key(ConsoleKey.Escape));
        }

        return keys.ToArray();
    }

    private static Profile CreateProfile(
        string vhdxPath = @"D:\DevTools\WSL2\Ubuntu24.04\ext4.vhdx") =>
        new(
            Guid.Parse("64d3e392-c081-4f1c-a95b-a7d0980527dd"),
            "Ubuntu 24.04 on D",
            "Ubuntu-24.04",
            vhdxPath,
            ShutdownMode.Global,
            TimeSpan.FromSeconds(45));

    private static ProfileStoreState CreateState(Profile profile) =>
        new(
            JsonProfileStore.CurrentSchemaVersion,
            profile.Id,
            JsonProfileStore.DefaultLogRetentionDays,
            ImmutableArray.Create(profile));

    private static ConsoleKeyInfo Key(ConsoleKey key) =>
        new('\0', key, false, false, false);

    private static ConsoleKeyInfo Character(char character) =>
        new(character, ConsoleKey.A, false, false, false);

    private static TuiFrameViewModel CreateFrame(Profile profile) =>
        new(
            new MainMenu().ViewModel,
            DashboardViewModel.CreateInitial(profile),
            new RunProgressViewModel(RunProgressState.Idle, "idle", null));

    private static async Task WriteSummaryAsync(AppPaths paths, RunSummary summary)
    {
        Directory.CreateDirectory(paths.GetRunDirectory(summary.RunId));
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
        };
        await File.WriteAllTextAsync(
            paths.GetSummaryFilePath(summary.RunId),
            JsonSerializer.Serialize(summary, options));
    }

    private sealed class EmptyRunHistoryReader : IRunHistoryReader
    {
        public Task<RunHistorySnapshot> ReadAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(RunHistorySnapshot.Empty(null));
        }
    }

    private sealed class InMemoryProfileStore : IProfileStore
    {
        public InMemoryProfileStore(ProfileStoreState state) => State = state;

        public ProfileStoreState State { get; private set; }

        public int SaveCallCount { get; private set; }

        public Task<ProfileStoreState> LoadAsync(
            CancellationToken cancellationToken = default) =>
            LoadRequiredAsync(cancellationToken);

        public Task<ProfileStoreState> LoadRequiredAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(State);
        }

        public Task SaveAsync(
            ProfileStoreState state,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            State = state;
            SaveCallCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeLogDirectoryOpener : ILogDirectoryOpener
    {
        public int CallCount { get; private set; }

        public Guid RunId { get; private set; }

        public Task<LogDirectoryOpenResult> OpenAsync(Guid runId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            RunId = runId;
            return Task.FromResult(new LogDirectoryOpenResult(true, "opened"));
        }

        public Task<LogDirectoryOpenResult> OpenAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            return Task.FromResult(new LogDirectoryOpenResult(true, "opened"));
        }
    }

    private sealed class FakeTuiInput : ITuiInput
    {
        private readonly Queue<ConsoleKeyInfo> _keys;

        public FakeTuiInput(params ConsoleKeyInfo[] keys) => _keys = new(keys);

        public ConsoleKeyInfo ReadKey() => _keys.Count == 0
            ? throw new InvalidOperationException("No fake input remains.")
            : _keys.Dequeue();
    }

    private sealed class RecordingFrameSink : ITuiFrameSink
    {
        public List<TuiFrameViewModel> Frames { get; } = [];

        public void Render(TuiFrameViewModel frame) => Frames.Add(frame);
    }

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
                "tui-services-tests",
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
