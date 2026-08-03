using System.Collections.Immutable;
using Spectre.Console;
using Vela.Core.Contracts;
using Vela.Core.Models;
using Vela.Core.Workflows;
using Vela.Tui.Menu;
using Vela.Tui.ProgramModes;
using Vela.Tui.Rendering;
using Vela.Tui.Screens;
using Vela.Windows.Configuration;
using Vela.Windows.Diagnostics;
using Vela.Windows.Elevation;
using Vela.Windows.Registry;
using Vela.Windows.Storage;
using Vela.Windows.Wsl;

if (args.Length != 0)
{
    var workerResult = await CreateWorkerMode().RunAsync(args, CancellationToken.None);
    return workerResult.ExitCode;
}

var state = JsonProfileStore.CreateInitialState();
var profile = state.Profiles.Single(candidate => candidate.Id == state.LastProfileId);
var ansiConsole = AnsiConsole.Console;
var console = new SpectreConsoleAdapter(ansiConsole);
var dashboard = new DashboardScreen(console);
var renderer = new RunRenderer(console);
var preflightScreen = new PreflightScreen(
    dashboard,
    renderer,
    new InMemoryPreflightViewModelSource());
var menu = new MainMenu(console, new SpectreMenuInput(ansiConsole));

dashboard.Render(DashboardViewModel.CreateInitial(profile));
renderer.Render(new RunProgressViewModel(
    RunProgressState.Idle,
    "预检尚未运行。",
    Percent: null));

if (Console.IsInputRedirected)
{
    console.RenderMenu(menu.ViewModel);
    return 0;
}

var action = menu.Prompt();
switch (action)
{
    case MainMenuAction.Preflight:
        preflightScreen.Render(profile);
        break;

    case MainMenuAction.ExecuteCompaction:
    {
        var runningDistributions = await GetRunningDistributionsAsync(CancellationToken.None);
        if (!runningDistributions.Succeeded)
        {
            renderer.Render(new RunProgressViewModel(
                RunProgressState.Failed,
                "获取当前运行中的 WSL 发行版失败，未执行压缩。",
                Percent: null));
            break;
        }

        var paths = AppPaths.CreateDefault();
        var confirmation = MainMenu.CreateExecuteConfirmation(
            profile,
            runningDistributions.Distributions,
            paths.RootDirectory);
        renderer.Render(new RunProgressViewModel(
            RunProgressState.AwaitingConfirmation,
            confirmation.Prompt,
            Percent: null));

        var response = new SpectreConfirmationInput(ansiConsole).Read(confirmation);
        var accepted = MainMenu.IsConfirmationAccepted(confirmation, response);
        if (!accepted)
        {
            renderer.Render(new RunProgressViewModel(
                RunProgressState.Failed,
                "确认输入未匹配 YES，操作已停止。",
                Percent: null));
            break;
        }

        var operationRequest = new OperationRequest(
            Guid.NewGuid(),
            profile,
            OperationIntent.Compact);
        var startResult = await CreateElevatedOperationCoordinator(paths)
            .StartAsync(operationRequest, CancellationToken.None);

        renderer.Render(new RunProgressViewModel(
            startResult.Status == ElevatedOperationStartStatus.Started
                ? RunProgressState.Running
                : RunProgressState.Failed,
            CreateElevationStatusMessage(startResult),
            Percent: null));

        if (startResult.Status == ElevatedOperationStartStatus.Started)
        {
            var poller = new RunJournalPoller(
                new FileRunJournal(paths),
                new SystemClock());
            var terminal = await poller.WaitForTerminalAsync(
                operationRequest.RunId,
                afterSequence: 0,
                CancellationToken.None);
            renderer.Render(CreateTerminalProgress(terminal));
        }

        break;
    }

    case MainMenuAction.ManageProfiles:
        renderer.Render(new RunProgressViewModel(
            RunProgressState.Idle,
            "目标配置管理尚未实现。",
            Percent: null));
        break;

    case MainMenuAction.RecentRuns:
        renderer.Render(new RunProgressViewModel(
            RunProgressState.Idle,
            "最近运行记录尚未实现。",
            Percent: null));
        break;

    case MainMenuAction.OpenLogs:
        renderer.Render(new RunProgressViewModel(
            RunProgressState.Idle,
            "日志目录打开尚未实现。",
            Percent: null));
        break;

    case MainMenuAction.Exit:
        renderer.Render(new RunProgressViewModel(
            RunProgressState.Succeeded,
            "Vela 已退出。",
            Percent: 100));
        break;
}

return 0;

static WorkerMode CreateWorkerMode()
{
    var paths = AppPaths.CreateDefault();
    var journal = new FileRunJournal(paths);
    var clock = new SystemClock();
    var workflow = new PreflightWorkflow(
        new WslClient(),
        new LxssProfileResolver(),
        new VhdxInspector(),
        journal,
        clock);

    return new WorkerMode(
        paths,
        new OperationRequestStore(paths),
        journal,
        new WindowsAdministratorProbe(),
        new LxssProfileResolver(),
        new PreflightWorkerOperationExecutor(workflow),
        clock);
}

static ElevatedOperationCoordinator CreateElevatedOperationCoordinator(AppPaths paths) =>
    new(
        new FileRunJournal(paths),
        new OperationRequestStore(paths),
        new UacWorkerLauncher(),
        new SystemClock());

static async Task<(bool Succeeded, ImmutableArray<WslDistribution> Distributions)>
    GetRunningDistributionsAsync(CancellationToken cancellationToken)
{
    try
    {
        var inventory = await new WslClient()
            .GetRunningInventoryAsync(cancellationToken)
            .ConfigureAwait(false);
        return (true, inventory.Distributions);
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
    {
        throw;
    }
    catch (Exception)
    {
        return (false, ImmutableArray<WslDistribution>.Empty);
    }
}

static string CreateElevationStatusMessage(ElevatedOperationStartResult startResult) =>
    startResult.Status switch
    {
        ElevatedOperationStartStatus.Started =>
            $"已启动 elevated worker，运行目录：{startResult.RunDirectory}",
        ElevatedOperationStartStatus.Cancelled =>
            "UAC 已取消，已写入终态日志。",
        ElevatedOperationStartStatus.ValidationFailed =>
            "执行请求验证失败，未启动 worker。",
        _ => "启动 worker 失败，已写入终态日志。"
    };

static RunProgressViewModel CreateTerminalProgress(RunJournalPollResult pollResult)
{
    var terminalResult = pollResult.TerminalResult ?? TerminalResult.WorkerInterrupted;
    var succeeded = terminalResult is TerminalResult.Succeeded or TerminalResult.CompletedWithNoReclaim;
    var message = pollResult.TerminalEvent is null
        ? "worker 未产生终态事件。"
        : $"worker 终态：{terminalResult}（事件 {pollResult.TerminalEvent.OperationName}）。";

    return new RunProgressViewModel(
        succeeded ? RunProgressState.Succeeded : RunProgressState.Failed,
        message,
        succeeded ? 100 : null);
}
