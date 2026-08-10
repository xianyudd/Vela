using Spectre.Console;
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

if (args.Length != 0)
{
    var workerResult = await CreateWorkerMode().RunAsync(args, CancellationToken.None);
    return workerResult.ExitCode;
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
    var firstRunFrame = new TuiFrameViewModel(
        new MainMenu().ViewModel,
        DashboardViewModel.CreateInitial(startupProfile),
        new RunProgressViewModel(
            RunProgressState.AwaitingConfirmation,
            confirmation.Prompt,
            Percent: null));

    if (Console.IsInputRedirected)
    {
        frameRenderer.RenderRedirected(ansiConsole, firstRunFrame);
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
try
{
    await profileService.LoadAsync().ConfigureAwait(false);
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

var profile = profileService.CurrentProfile;
var menu = new MainMenu();
var preflightSource = CreatePreflightViewModelSource();
var secondaryActions = new TuiSecondaryActionHandler(
    profileService,
    new RunHistoryReader(paths),
    new WindowsLogDirectoryOpener(paths));
var runLogReader = new RunLogReader(paths);
var dashboardViewModel = DashboardViewModel.CreateInitial(profile);
var progressViewModel = new RunProgressViewModel(
    RunProgressState.Idle,
    "预检尚未运行。",
    Percent: null);
var initialFrame = new TuiFrameViewModel(menu.ViewModel, dashboardViewModel, progressViewModel);

if (Console.IsInputRedirected)
{
    frameRenderer.RenderRedirected(ansiConsole, initialFrame);
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

    shell.SelectionPreviewRequested += (action, revision) =>
    {
        switch (action)
        {
            case MainMenuAction.RecentRuns:
                _ = ShowRecentRunsAsync(revision);
                break;
            case MainMenuAction.OpenLogs:
                _ = ShowLogsAsync(revision);
                break;
        }
    };

    shell.ActionRequested += action =>
    {
        switch (action)
        {
            case MainMenuAction.Preflight:
                shell.ShowOverview();
                _ = terminalHost.Start(profileService.CurrentProfile);
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
                _ = shell.HasLogAnalysis
                    ? OpenLogsAsync(shell.NavigationRevision)
                    : ShowLogsAsync(shell.NavigationRevision);
                break;
            case MainMenuAction.ExecuteCompaction:
                // The interactive TUI is deliberately a read-only control surface.
                // It may present the impact summary, but this path never creates a compact
                // request, starts elevation, or starts a worker.
                var targetProfile = shell.CreateLockedTargetProfile(profileService.CurrentProfile);
                if (targetProfile is null)
                {
                    shell.ShowStatus("当前锁定实例缺少可用 VHDX 路径，请返回 01 重新选择");
                    break;
                }

                shell.ShowConfirmation(MainMenu.CreateExecuteConfirmation(
                    targetProfile,
                    shell.Overview.InstalledDistros,
                    paths.RootDirectory));
                break;
            default:
                break;
        }
    };
    _ = terminalHost.Start(profileService.CurrentProfile);
    terminalApplication.Run(shell);

    void ShowProfiles() => shell.ShowWorkspacePage(
        "目标档案",
        profileService.Profiles.Select(candidate =>
            $"{(candidate.Id == profileService.CurrentProfile.Id ? "● 当前" : "○       ")}  {TuiDisplayText.Sanitize(candidate.DisplayName, 28)}  {TuiDisplayText.Sanitize(candidate.DistroName, 20)}  VHDX {(string.IsNullOrWhiteSpace(candidate.VhdxPath) ? "待配置" : "已配置")}"));

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

    async Task OpenLogsAsync(long revision)
    {
        var result = await new WindowsLogDirectoryOpener(paths).OpenAsync().ConfigureAwait(false);
        terminalApplication.Invoke(() =>
        {
            if (shell.IsCurrentSelection(MainMenuAction.OpenLogs, revision))
            {
                shell.ShowStatus(result.Message);
            }
        });
    }

    async Task ShowLogsAsync(long revision)
    {
        var snapshot = await runLogReader.ReadLatestAsync(maxLines: 20).ConfigureAwait(false);
        terminalApplication.Invoke(() =>
        {
            if (!shell.IsCurrentSelection(MainMenuAction.OpenLogs, revision))
            {
                return;
            }

            shell.ShowLogAnalysis(snapshot);
        });
    }
}

return 0;

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
    var diskPartClient = new DiskPartClient();
    var compactionWorkflow = new CompactionWorkflow(
        wslClient,
        lxssProfileResolver,
        vhdxInspector,
        diskPartClient,
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
