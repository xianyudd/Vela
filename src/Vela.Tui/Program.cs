using Spectre.Console;
using Vela.Tui.Menu;
using Vela.Tui.Rendering;
using Vela.Tui.Screens;
using Vela.Windows.Configuration;

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
    return;
}

var action = menu.Prompt();
switch (action)
{
    case MainMenuAction.Preflight:
        preflightScreen.Render(profile);
        break;

    case MainMenuAction.ExecuteCompaction:
    {
        var confirmation = MainMenu.CreateExecuteConfirmation(profile, []);
        renderer.Render(new RunProgressViewModel(
            RunProgressState.AwaitingConfirmation,
            confirmation.Prompt,
            Percent: null));

        var response = new SpectreConfirmationInput(ansiConsole).Read(confirmation);
        var accepted = MainMenu.IsConfirmationAccepted(confirmation, response);
        renderer.Render(new RunProgressViewModel(
            accepted
                ? RunProgressState.Running
                : RunProgressState.Failed,
            accepted
                ? "压缩工作流尚未连接。"
                : "确认输入未匹配 YES。",
            Percent: null));
        break;
    }

    case MainMenuAction.ManageProfiles:
        renderer.Render(new RunProgressViewModel(
            RunProgressState.Idle,
            "目标档案管理界面尚未连接。",
            Percent: null));
        break;

    case MainMenuAction.RecentRuns:
        renderer.Render(new RunProgressViewModel(
            RunProgressState.Idle,
            "最近运行记录界面尚未连接。",
            Percent: null));
        break;

    case MainMenuAction.OpenLogs:
        renderer.Render(new RunProgressViewModel(
            RunProgressState.Idle,
            "日志目录操作尚未连接。",
            Percent: null));
        break;

    case MainMenuAction.Exit:
        renderer.Render(new RunProgressViewModel(
            RunProgressState.Succeeded,
            "Vela 已退出。",
            Percent: 100));
        break;
}
