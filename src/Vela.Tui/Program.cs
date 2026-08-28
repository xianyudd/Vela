using System.Collections.Immutable;
using Spectre.Console;
using Vela.Application.Profiles;
using Vela.Core.Contracts;
using Terminal.Gui.App;
using Vela.Core.Models;
using Vela.Core.Workflows;
using Vela.Tui;
using Vela.Tui.Application;
using Vela.Tui.Menu;
using Vela.Tui.ProgramModes;
using Vela.Tui.Rendering;
using Vela.Tui.Views;
using Vela.Windows.Configuration;
using Vela.Windows.Diagnostics;
using Vela.Windows.DiskPart;
using Vela.Windows.Elevation;
using Vela.Windows.Registry;
using Vela.Windows.Storage;
using Vela.Windows.Wsl;
using CoreProfile = Vela.Core.Models.Profile;

// Command-line routing is strict: only an exact "--worker" first argument
// enters worker mode. Accepting anything non-empty would let a typo start a
// headless elevated worker instead of the interactive shell.
if (args.Length != 0)
{
    if (string.Equals(args[0], "--worker", StringComparison.Ordinal))
    {
        var workerResult = await CreateWorkerMode().RunAsync(args, CancellationToken.None);
        return workerResult.ExitCode;
    }

    if (args.Length == 1 && args[0] is "--help" or "-h" or "/?")
    {
        Console.Out.WriteLine(BuildUsageText());
        return 0;
    }

    if (args.Length == 1 && args[0] is "--version" or "-v")
    {
        Console.Out.WriteLine(BuildVersionText());
        return 0;
    }

    Console.Error.WriteLine("无法识别的命令行参数。");
    Console.Error.WriteLine(BuildUsageText());
    // 64 keeps usage errors distinct from every worker terminal-result code.
    return 64;
}

var paths = AppPaths.CreateDefault();
var profileStore = new JsonProfileStore(paths);
var ansiConsole = AnsiConsole.Console;
var frameRenderer = new FrameRenderer();

var startupGate = new StartupGate(paths, profileStore);
var startupInspection = startupGate.Inspect();
var startupProfile = JsonProfileStore.CreateInitialState().Profiles[0];
void RenderStartupFrame(
    CoreProfile profile,
    RunProgressState state,
    string message)
{
    var frame = new TuiFrameViewModel(
        new MainMenu().ViewModel,
        DashboardViewModel.CreateInitial(profile),
        new RunProgressViewModel(state, message, Percent: null));

    if (Console.IsInputRedirected || Console.IsOutputRedirected)
    {
        frameRenderer.RenderRedirected(ansiConsole, frame);
    }
    else
    {
        frameRenderer.Render(ansiConsole, frame);
    }
}

if (!startupInspection.IsComplete)
{
    if (Directory.Exists(paths.ConfigurationFilePath) || !startupInspection.PathsTrusted)
    {
        RenderStartupFrame(
            startupProfile,
            RunProgressState.Failed,
            "Vela 数据目录无法通过安全检查，未继续启动。");
        return 2;
    }

    if (startupInspection.ConfigurationFileExists)
    {
        try
        {
            var existingState = await profileStore
                .LoadRequiredAsync()
                .ConfigureAwait(false);
            startupProfile = existingState.Profiles.Single(
                profile => profile.Id == existingState.LastProfileId);
        }
        catch (InvalidDataException)
        {
            RenderStartupFrame(
                startupProfile,
                RunProgressState.Failed,
                "Vela 配置无效，未继续启动。请修复配置后重试。");
            return 2;
        }
        catch (Exception)
        {
            RenderStartupFrame(
                startupProfile,
                RunProgressState.Failed,
                "Vela 配置无法读取，未继续启动。");
            return 2;
        }
    }

    var confirmation = startupInspection.ConfigurationFileExists
        ? MainMenu.CreateRepairConfirmation(
            startupInspection.ConfigurationFileExists,
            startupInspection.PendingDirectoryExists,
            startupInspection.LogsDirectoryExists)
        : MainMenu.CreateFirstRunConfirmation(paths);
    if (Console.IsInputRedirected)
    {
        // Only the non-interactive path renders a frame here; the interactive
        // one goes straight into Terminal.Gui so the screen is painted once.
        frameRenderer.RenderRedirected(
            ansiConsole,
            new TuiFrameViewModel(
                new MainMenu().ViewModel,
                DashboardViewModel.CreateInitial(startupProfile),
                new RunProgressViewModel(
                    RunProgressState.AwaitingConfirmation,
                    confirmation.Prompt,
                    Percent: null)));
        return 2;
    }

    var confirmationResult = RunStartupConfirmation(confirmation, startupProfile);

    if (confirmationResult.Status is not ConfirmationInputStatus.Accepted)
    {
        RenderStartupFrame(
            startupProfile,
            confirmationResult.Status is ConfirmationInputStatus.Cancelled
                ? RunProgressState.Cancelled
                : RunProgressState.Failed,
            confirmationResult.Status is ConfirmationInputStatus.Cancelled
                ? "初始化确认已取消，未继续启动。"
                : "确认输入未匹配 YES，未继续启动。");
        return 2;
    }

    StartupGateResult initialization;
    try
    {
        initialization = await startupGate
            .InitializeAfterConfirmationAsync()
            .ConfigureAwait(false);
    }
    catch (OperationCanceledException)
    {
        RenderStartupFrame(
            startupProfile,
            RunProgressState.Cancelled,
            "数据目录初始化已取消，未继续启动。");
        return 2;
    }
    catch (Exception)
    {
        RenderStartupFrame(
            startupProfile,
            RunProgressState.Failed,
            "数据目录初始化失败，未继续启动。");
        return 2;
    }

    if (!initialization.IsReady)
    {
        RenderStartupFrame(
            startupProfile,
            RunProgressState.Failed,
            initialization.Message);
        return 2;
    }
}

static ConfirmationInputResult RunStartupConfirmation(
    ConfirmationViewModel confirmation,
    CoreProfile profile)
{
    ConfirmationInputResult? result = null;
    using var terminalApplication = Application.Create();
    terminalApplication.Init();
    VelaTerminalTheme.Register();
    using var shell = new Vela.Tui.Views.VelaTerminalShell(
        new MainMenu().ViewModel,
        DashboardViewModel.CreateInitial(profile));
    shell.ConfirmationSubmitted += submitted =>
    {
        result = submitted;
        if (submitted.Status is ConfirmationInputStatus.Accepted or ConfirmationInputStatus.Cancelled)
        {
            terminalApplication.RequestStop();
        }
    };
    shell.ShowConfirmation(confirmation);
    terminalApplication.Run(shell);
    return result ?? new ConfirmationInputResult(ConfirmationInputStatus.Cancelled, string.Empty);
}

var profileService = new ProfileService(profileStore);
var logRetentionDays = JsonProfileStore.DefaultLogRetentionDays;
try
{
    logRetentionDays = (await profileService.LoadAsync().ConfigureAwait(false)).LogRetentionDays;
}
catch (InvalidDataException)
{
    RenderStartupFrame(
        startupProfile,
        RunProgressState.Failed,
        "Vela 配置无效，未继续启动。请修复配置后重试。");
    return 2;
}
catch (Exception)
{
    RenderStartupFrame(
        startupProfile,
        RunProgressState.Failed,
        "Vela 启动配置无法读取，未继续启动。");
    return 2;
}

// Housekeeping before the shell opens. A crashed worker leaves a compaction
// gate and a pending request behind; reclaiming them here means the operator
// never meets a spurious "a compaction is already running" much later.
var startupReclaim = new CompactRunGate(paths).ReconcileStaleGate();
var startupPrune = new RunLogRetention(paths).Prune(logRetentionDays);

// app.manifest requests requireAdministrator, so this normally holds. It is a
// defensive check for the cases where the manifest is not in effect, such as
// running through `dotnet run`, where compaction would fail much later instead.
var startupIsElevated = TryDetectAdministrator();

var startupNotices = BuildStartupNotices(
    startupReclaim,
    startupPrune,
    startupIsElevated,
    logRetentionDays);

var profile = profileService.CurrentProfile;
var menu = new MainMenu();
var preflightSource = CreatePreflightViewModelSource();
var impactEstimator = new WslCompactionImpactEstimator();
// Used only to refresh the running inventory when a confirmation is raised, so
// the disclosed blast radius reflects the machine's current state.
var interactiveWslClient = new WslClient();
var runLogReader = new RunLogReader(paths);
var runHistoryReader = new RunHistoryReader(paths);
var dashboardViewModel = DashboardViewModel.CreateInitial(profile);

if (Console.IsInputRedirected)
{
    frameRenderer.RenderRedirected(
        ansiConsole,
        new TuiFrameViewModel(
            menu.ViewModel,
            dashboardViewModel,
            new RunProgressViewModel(RunProgressState.Idle, "预检尚未运行。", Percent: null)));
    return 0;
}

using (var terminalApplication = Application.Create())
{
    terminalApplication.Init();
    VelaTerminalTheme.Register();
    using var shell = new Vela.Tui.Views.VelaTerminalShell(menu.ViewModel, dashboardViewModel);
    using var shellHost = new Vela.Tui.Views.TerminalGuiShellHost(terminalApplication, shell);
    using var automaticPreflight = new AutomaticPreflightCoordinator(preflightSource.CreateAsync);
    using var terminalHost = new Vela.Tui.Views.VelaTerminalHost(
        shell,
        automaticPreflight,
        new TerminalGuiUiDispatcher(terminalApplication));
    using var executionCancellation = new CancellationTokenSource();
    var executionJournal = new FileRunJournal(paths);
    var executionClock = new SystemClock();
    var operationCoordinator = new ElevatedOperationCoordinator(
        executionJournal,
        new OperationRequestStore(paths),
        new UacWorkerLauncher(),
        executionClock,
        new CompactRunGate(paths));
    var journalPoller = new RunJournalPoller(
        executionJournal,
        executionClock,
        new RunJournalPollOptions
        {
            PollInterval = TimeSpan.FromMilliseconds(100),
            Timeout = TimeSpan.FromMinutes(10)
        });
    var executionHistory = new RunHistoryReader(paths);
    OperationRequest? pendingCompactionRequest = null;
    Task executionTask = Task.CompletedTask;

    shell.ConfirmationSubmitted += submitted =>
    {
        if (submitted.Status == ConfirmationInputStatus.Cancelled)
        {
            pendingCompactionRequest = null;
            return;
        }

        if (submitted.Status == ConfirmationInputStatus.Accepted &&
            pendingCompactionRequest is { } request)
        {
            pendingCompactionRequest = null;
            executionTask = StartCompactionAsync(request);
        }
    };

    shell.SelectionPreviewRequested += (action, revision) =>
    {
        switch (action)
        {
            case MainMenuAction.ExecuteCompaction:
                _ = ShowCompactionImpactAsync(revision);
                break;
            case MainMenuAction.RecentRuns:
                _ = ShowRecentRunsAsync(revision);
                break;
            case MainMenuAction.OpenLogs:
                _ = ShowLogsAsync(revision);
                break;
        }
    };

    shell.TargetPreflightRequested += () => _ = HandleTargetPreflightAsync();

    shell.ActionRequested += action =>
    {
        switch (action)
        {
            case MainMenuAction.Preflight:
                if (CompactionTargetProfileFactory.IsTargetMismatch(
                        profileService.CurrentProfile,
                        shell.LockedTarget))
                {
                    _ = HandleTargetPreflightAsync();
                    break;
                }

                var targetProfile = shell.CreateLockedTargetProfile(profileService.CurrentProfile);
                var preserveTargetSelection = shell.LockedTarget is not null && targetProfile is not null;
                if (!preserveTargetSelection)
                {
                    shell.ShowOverview();
                }

                _ = terminalHost.Start(
                    targetProfile ?? profileService.CurrentProfile,
                    preserveTargetSelection);
                break;
            case MainMenuAction.Exit:
                terminalApplication.RequestStop();
                break;
            case MainMenuAction.ManageProfiles:
                ShowProfiles();
                break;
            case MainMenuAction.RecentRuns:
                _ = ShowRecentRunsAsync(shell.NavigationRevision);
                break;
            case MainMenuAction.OpenLogs:
                _ = ShowLogsAsync(shell.NavigationRevision);
                break;
            case MainMenuAction.ExecuteCompaction:
                if (CompactionTargetProfileFactory.IsTargetMismatch(
                        profileService.CurrentProfile,
                        shell.LockedTarget))
                {
                    shell.ShowStatus("锁定实例与当前档案的发行版不一致，已阻止执行压缩：请先锁定与档案匹配的实例");
                    break;
                }

                var request = shell.CreateLockedCompactionRequest(
                    profileService.CurrentProfile,
                    Guid.NewGuid());
                if (request is null)
                {
                    shell.ShowStatus("当前锁定实例缺少可用 VHDX 路径，请返回 01 重新选择");
                    break;
                }

                _ = ShowExecuteConfirmationAsync(request);
                break;
            default:
                break;
        }
    };
    foreach (var notice in startupNotices)
    {
        shell.ShowStatus(notice);
    }

    _ = terminalHost.Start(profileService.CurrentProfile);
    terminalApplication.Run(shell);
    executionCancellation.Cancel();
    try
    {
        await executionTask.ConfigureAwait(false);
    }
    catch (OperationCanceledException) when (executionCancellation.IsCancellationRequested)
    {
    }

    void ShowProfiles() => shell.ShowWorkspacePage(
        "目标档案",
        profileService.Profiles.Select(candidate =>
            $"{(candidate.Id == profileService.CurrentProfile.Id ? "● 当前" : "○       ")}  {TuiDisplayText.Sanitize(candidate.DisplayName, 28)}  {TuiDisplayText.Sanitize(candidate.DistroName, 20)}  VHDX {(string.IsNullOrWhiteSpace(candidate.VhdxPath) ? "待配置" : "已配置")}"));

    // Locking an instance that another profile owns must rebind the session to
    // that profile; lending the current profile's shutdown scope to a foreign
    // distro is no longer allowed, so an unmatched lock stops here.
    async Task HandleTargetPreflightAsync()
    {
        // Read on the UI thread, before any await.
        var lockedTarget = shell.LockedTarget;
        var matching = CompactionTargetProfileFactory.FindProfileForTarget(
            profileService.Profiles,
            lockedTarget);
        if (matching is null)
        {
            shell.ShowStatus(
                $"锁定实例 {TuiDisplayText.Sanitize(lockedTarget?.Name ?? string.Empty, 32)} 没有发行版匹配的档案，已阻止压缩：请先创建对应的档案或锁定与当前档案匹配的实例。");
            return;
        }

        if (matching.Id != profileService.CurrentProfile.Id)
        {
            await profileService.SelectAsync(matching.Id).ConfigureAwait(false);
        }

        try
        {
            terminalApplication.Invoke(() =>
            {
                shell.ShowStatus($"已自动切换到匹配档案：{TuiDisplayText.Sanitize(matching.DisplayName, 32)}。");
                _ = terminalHost.Start(matching, preserveTargetSelection: true);
            });
        }
        catch (ObjectDisposedException)
        {
        }
        catch (InvalidOperationException) when (executionCancellation.IsCancellationRequested)
        {
        }
    }

    async Task ShowRecentRunsAsync(long revision)
    {
        var snapshot = await new RunHistoryReader(paths).ReadAsync().ConfigureAwait(false);
        terminalApplication.Invoke(() =>
        {
            if (!shell.IsCurrentSelection(MainMenuAction.RecentRuns, revision))
            {
                return;
            }

            var lines = snapshot.ErrorMessage is not null
                ? new[] { snapshot.ErrorMessage }
                : snapshot.Entries.IsDefaultOrEmpty
                    ? ["暂无运行记录；完成一次只读预检后会显示在这里。"]
                    : snapshot.Entries.Take(12).Select(entry =>
                    {
                        var marker = entry.TerminalResult is TerminalResult.Succeeded or TerminalResult.CompletedWithNoReclaim ? "✓" : "!";
                        var started = entry.StartedAtUtc?.ToLocalTime().ToString("MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture) ?? "--/-- --:--";
                        return $"{marker} {started}  {TuiDisplayText.Sanitize(entry.ProfileDisplayName, 24)}  {TuiDisplayText.LabelForIntent(entry.Intent),-8}  {TuiDisplayText.LabelForTerminal(entry.TerminalResult)}";
                    }).ToArray();

            shell.ShowWorkspacePage(
                "最近运行",
                lines);
        });
    }

    async Task ShowLogsAsync(long revision)
    {
        if (shell.SelectedLogEntry is { } selectedEntry)
        {
            var detailSnapshot = await runLogReader
                .ReadAsync(selectedEntry.RunId, maxLines: 40)
                .ConfigureAwait(false);
            terminalApplication.Invoke(() =>
            {
                if (shell.IsCurrentSelection(MainMenuAction.OpenLogs, revision))
                {
                    shell.ShowLogDetail(selectedEntry, detailSnapshot);
                }
            });
            return;
        }

        var history = await runHistoryReader.ReadAsync().ConfigureAwait(false);
        terminalApplication.Invoke(() =>
        {
            if (!shell.IsCurrentSelection(MainMenuAction.OpenLogs, revision))
            {
                return;
            }

            shell.ShowLogArchive(history);
        });
    }

    async Task ShowExecuteConfirmationAsync(OperationRequest request)
    {
        // The overview snapshot dates from the last preflight, so the blast
        // radius it would show may already be wrong. Re-read the running
        // inventory here and fall back to the snapshot only if that fails.
        var runningDistros = shell.Overview.InstalledDistros;
        try
        {
            var inventory = await interactiveWslClient
                .GetRunningInventoryAsync(executionCancellation.Token)
                .ConfigureAwait(false);
            if (!inventory.Distributions.IsDefault)
            {
                runningDistros = inventory.Distributions;
            }
        }
        catch (OperationCanceledException) when (executionCancellation.IsCancellationRequested)
        {
            return;
        }
        catch (Exception)
        {
            // Keep the stale snapshot rather than blocking the confirmation.
        }

        var mismatch = CompactionTargetProfileFactory.IsTargetMismatch(
            profileService.CurrentProfile,
            shell.LockedTarget);
        if (mismatch)
        {
            shell.ShowStatus("锁定实例与当前档案的发行版不一致，已阻止执行压缩：请先锁定与档案匹配的实例");
            return;
        }

        try
        {
            terminalApplication.Invoke(() =>
            {
                pendingCompactionRequest = request;
                shell.ShowConfirmation(MainMenu.CreateExecuteConfirmation(
                    request.Profile,
                    runningDistros,
                    paths.RootDirectory));
            });
        }
        catch (ObjectDisposedException)
        {
        }
        catch (InvalidOperationException) when (executionCancellation.IsCancellationRequested)
        {
        }
    }

    async Task ShowCompactionImpactAsync(long revision)
    {
        var target = shell.LockedTarget;
        var targetPath = shell.LockedTargetVhdxPath;
        var currentSize = shell.LockedTargetVhdxSizeBytes ?? TryReadVhdxLength(targetPath);
        if (target is null)
        {
            return;
        }

        var estimate = currentSize is { } sizeBytes
            ? await impactEstimator
                .EstimateAsync(
                    target.Name,
                    targetPath ?? string.Empty,
                    sizeBytes,
                    target.State == WslDistributionState.Running,
                    executionCancellation.Token)
                .ConfigureAwait(false)
            : new CompactionImpactEstimate(
                CompactionImpactStatus.Unavailable,
                null,
                null,
                null,
                "目标 VHDX 当前体积尚未采集。");

        try
        {
            terminalApplication.Invoke(() =>
                shell.ApplyCompactionImpactEstimate(revision, target.Name, estimate));
        }
        catch (ObjectDisposedException)
        {
        }
        catch (InvalidOperationException) when (executionCancellation.IsCancellationRequested)
        {
        }
    }

    static long? TryReadVhdxLength(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Any(char.IsControl))
        {
            return null;
        }

        try
        {
            var file = new FileInfo(path);
            return file.Exists && file.Length >= 0 ? file.Length : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    async Task StartCompactionAsync(OperationRequest request)
    {
        var target = request.Profile;
        var logLines = ImmutableArray<string>.Empty;
        ShowProgressOnUi(new RunProgressViewModel(
            RunProgressState.Running,
            "正在创建压缩请求。",
            Percent: null,
            TargetName: target.DistroName,
            VhdxPath: target.VhdxPath,
            LogLines: logLines));

        ElevatedOperationStartResult start;
        try
        {
            start = await operationCoordinator
                .StartAsync(request, executionCancellation.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (executionCancellation.IsCancellationRequested)
        {
            ShowProgressOnUi(CreateExecutionProgress(
                request,
                RunProgressState.Cancelled,
                "压缩流程已取消，未伪造 worker 终态。",
                logLines));
            return;
        }
        catch (Exception)
        {
            ShowProgressOnUi(CreateExecutionProgress(
                request,
                RunProgressState.Failed,
                "压缩流程启动失败；请查看日志。",
                logLines));
            return;
        }

        try
        {
            if (start.Status != ElevatedOperationStartStatus.Started)
            {
                ShowProgressOnUi(CreateExecutionProgress(
                    request,
                    start.Status == ElevatedOperationStartStatus.Cancelled
                        ? RunProgressState.Cancelled
                        : RunProgressState.Failed,
                    FormatStartStatus(start.Status),
                    logLines));
                return;
            }

            var pollResult = await journalPoller
                .PollAsync(
                    request.RunId,
                    afterSequence: 0,
                    executionCancellation.Token,
                    @event =>
                    {
                        var line = FormatRunEvent(@event);
                        logLines = AppendLogLine(logLines, line);
                        var eventProgress = RunProgressMapper.FromEvent(@event) with
                        {
                            TargetName = target.DistroName,
                            VhdxPath = target.VhdxPath,
                            LogLines = logLines
                        };
                        ShowProgressOnUi(eventProgress);
                        return Task.CompletedTask;
                    })
                .ConfigureAwait(false);
            var history = await executionHistory
                .ReadAsync(executionCancellation.Token)
                .ConfigureAwait(false);
            var entry = history.Entries.FirstOrDefault(item => item.RunId == request.RunId);
            var terminalProgress = RunProgressMapper.FromTerminal(pollResult) with
            {
                TargetName = target.DistroName,
                VhdxPath = target.VhdxPath,
                Elapsed = entry?.Elapsed,
                ReclaimedBytes = entry?.ReclaimedBytes,
                LogLines = logLines
            };
            ShowProgressOnUi(terminalProgress);
        }
        catch (OperationCanceledException) when (executionCancellation.IsCancellationRequested)
        {
            ShowProgressOnUi(CreateExecutionProgress(
                request,
                RunProgressState.Cancelled,
                "等待 worker journal 已取消；worker 终态以日志为准。",
                logLines));
        }
        catch (Exception)
        {
            ShowProgressOnUi(CreateExecutionProgress(
                request,
                RunProgressState.Failed,
                "读取 worker journal 失败；请打开日志分析。",
                logLines));
        }
        finally
        {
            start.GateLease?.Dispose();
        }
    }

    void ShowProgressOnUi(RunProgressViewModel progress)
    {
        try
        {
            terminalApplication.Invoke(() => shell.ShowRunProgress(progress));
        }
        catch (ObjectDisposedException)
        {
        }
        catch (InvalidOperationException) when (executionCancellation.IsCancellationRequested)
        {
        }
    }

    static RunProgressViewModel CreateExecutionProgress(
        OperationRequest request,
        RunProgressState state,
        string message,
        ImmutableArray<string> logLines) =>
        new(
            state,
            message,
            Percent: null,
            TargetName: request.Profile.DistroName,
            VhdxPath: request.Profile.VhdxPath,
            LogLines: logLines);

    static string FormatStartStatus(ElevatedOperationStartStatus status) => status switch
    {
        ElevatedOperationStartStatus.ValidationFailed => "压缩请求校验失败。",
        ElevatedOperationStartStatus.AlreadyRunning => "已有压缩任务运行中。",
        ElevatedOperationStartStatus.Cancelled => "UAC 提示已取消。",
        ElevatedOperationStartStatus.Failed => "worker 启动失败；请查看日志。",
        _ => "压缩请求未启动。"
    };

    static string FormatRunEvent(RunEvent @event)
        => RunEventLogFormatter.Format(@event);

    static ImmutableArray<string> AppendLogLine(
        ImmutableArray<string> lines,
        string line)
    {
        var next = lines.Add(line);
        return next.Length <= 18
            ? next
            : next.RemoveAt(0);
    }
}

return 0;

static bool? TryDetectAdministrator()
{
    try
    {
        return new WindowsAdministratorProbe().IsAdministrator();
    }
    catch (Exception)
    {
        // An unavailable probe must not block startup; the worker re-checks.
        return null;
    }
}

static ImmutableArray<string> BuildStartupNotices(
    CompactGateReconcileResult reclaim,
    RunLogRetentionResult prune,
    bool? isElevated,
    int logRetentionDays)
{
    var notices = ImmutableArray.CreateBuilder<string>();

    if (reclaim.ReclaimedAnything)
    {
        notices.Add(
            $"已清理上次异常退出残留的压缩锁与请求（锁 {reclaim.ReclaimedGates} 个，请求 {reclaim.ReclaimedPendingRequests} 个）。");
    }

    if (prune.RemovedAnything)
    {
        notices.Add($"已按 {logRetentionDays} 天留存清理 {prune.RemovedRunDirectories} 条过期运行记录。");
    }

    if (isElevated == false)
    {
        notices.Add("当前进程未以管理员身份运行：只读预检可用，压缩将无法执行。");
    }

    return notices.ToImmutable();
}

static string BuildUsageText() =>
    string.Join(
        Environment.NewLine,
        "Vela — WSL2 虚拟磁盘压缩工具",
        string.Empty,
        "用法：",
        "  Vela                     启动交互式界面（默认）",
        "  Vela --help              显示本帮助",
        "  Vela --version           显示版本",
        "  Vela --worker --run-id <GUID>",
        "                           内部提权工作进程；请勿手动调用",
        string.Empty,
        "压缩操作必须由界面发起，以便完成确认与审计记录。");

static string BuildVersionText()
{
    var assembly = System.Reflection.Assembly.GetEntryAssembly();
    var informational = assembly?
        .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
        .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
        .FirstOrDefault()?
        .InformationalVersion;
    var version = string.IsNullOrWhiteSpace(informational)
        ? assembly?.GetName().Version?.ToString()
        : informational;
    return $"Vela {version ?? "未知版本"}";
}

static IPreflightViewModelSource CreatePreflightViewModelSource()
{
    var paths = AppPaths.CreateDefault();
    var journal = new FileRunJournal(paths);
    var clock = new SystemClock();
    var wslClient = new WslClient();
    var lxssProfileResolver = new LxssProfileResolver();
    var vhdxInspector = new VhdxInspector();
    var workflow = new PreflightWorkflow(
        wslClient,
        lxssProfileResolver,
        vhdxInspector,
        journal,
        clock);

    return new WorkflowPreflightViewModelSource(workflow);
}

static WorkerMode CreateWorkerMode()
{
    var paths = AppPaths.CreateDefault();
    var journal = new FileRunJournal(paths);
    var clock = new SystemClock();
    var wslClient = new WslClient();
    var lxssProfileResolver = new LxssProfileResolver();
    var vhdxInspector = new VhdxInspector();
    var diskPartClient = new DiskPartClient(journal);
    var vhdxHandleProbe = new VhdxHandleProbe();
    var compactionWorkflow = new CompactionWorkflow(
        wslClient,
        lxssProfileResolver,
        vhdxInspector,
        diskPartClient,
        vhdxHandleProbe,
        journal,
        clock);

    return new WorkerMode(
        paths,
        new OperationRequestStore(paths),
        journal,
        new WindowsAdministratorProbe(),
        lxssProfileResolver,
        new CompactionWorkerOperationExecutor(compactionWorkflow),
        clock);
}
