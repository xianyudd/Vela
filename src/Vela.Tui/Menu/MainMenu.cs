using System.Collections.Immutable;
using Spectre.Console;
using Vela.Core.Contracts;
using Vela.Tui.Screens;
using Profile = Vela.Core.Models.Profile;

namespace Vela.Tui.Menu;

public enum MainMenuAction
{
    Preflight,
    ExecuteCompaction,
    ManageProfiles,
    RecentRuns,
    OpenLogs,
    Exit
}

public sealed record MainMenuItem(MainMenuAction Action, string Label);

public sealed record MainMenuViewModel(
    string Title,
    ImmutableArray<MainMenuItem> Items);

public sealed record ConfirmationViewModel(
    string Prompt,
    string RequiredInput,
    ImmutableArray<string> RunningDistros);

public interface IMenuInput
{
    MainMenuAction Select(MainMenuViewModel viewModel);
}

public interface IConfirmationInput
{
    string Read(ConfirmationViewModel viewModel);
}

public sealed class MainMenu
{
    public const string ApplicationTitle = "Vela — WSL VHDX Compact";

    private static readonly ImmutableArray<MainMenuItem> MenuItems =
        ImmutableArray.Create(
            new MainMenuItem(MainMenuAction.Preflight, "预检（只读）"),
            new MainMenuItem(MainMenuAction.ExecuteCompaction, "执行压缩"),
            new MainMenuItem(MainMenuAction.ManageProfiles, "管理目标档案"),
            new MainMenuItem(MainMenuAction.RecentRuns, "查看最近运行记录"),
            new MainMenuItem(MainMenuAction.OpenLogs, "打开日志目录"),
            new MainMenuItem(MainMenuAction.Exit, "退出"));

    private readonly IVelaConsole _console;
    private readonly IMenuInput _input;

    public MainMenu(IVelaConsole console, IMenuInput input)
    {
        ArgumentNullException.ThrowIfNull(console);
        ArgumentNullException.ThrowIfNull(input);

        _console = console;
        _input = input;
        ViewModel = new MainMenuViewModel(ApplicationTitle, MenuItems);
    }

    public MainMenuViewModel ViewModel { get; }

    public MainMenuAction Prompt()
    {
        _console.RenderMenu(ViewModel);
        return _input.Select(ViewModel);
    }

    public static ConfirmationViewModel CreateExecuteConfirmation(
        Profile profile,
        ImmutableArray<WslDistribution> distributions)
    {
        ArgumentNullException.ThrowIfNull(profile);

        var runningDistros = distributions.IsDefault
            ? ImmutableArray<string>.Empty
            : distributions
                .Where(static distribution => distribution.State == WslDistributionState.Running)
                .Select(static distribution => distribution.Name)
                .ToImmutableArray();
        var runningDistroSummary = runningDistros.IsDefaultOrEmpty
            ? "当前没有运行中的发行版。"
            : $"运行中的发行版：{string.Join(", ", runningDistros)}。";

        return new ConfirmationViewModel(
            $"即将为档案“{profile.DisplayName}”执行压缩。{Environment.NewLine}{runningDistroSummary}{Environment.NewLine}输入 YES 继续。",
            "YES",
            runningDistros);
    }

    public static bool IsConfirmationAccepted(ConfirmationViewModel viewModel, string? response)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        return string.Equals(response, viewModel.RequiredInput, StringComparison.Ordinal);
    }
}

public sealed class SpectreMenuInput : IMenuInput
{
    private readonly IAnsiConsole _console;

    public SpectreMenuInput(IAnsiConsole console)
    {
        ArgumentNullException.ThrowIfNull(console);
        _console = console;
    }

    public MainMenuAction Select(MainMenuViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);

        var prompt = new SelectionPrompt<MainMenuItem>()
            .Title($"[bold blue]{Markup.Escape(viewModel.Title)}[/]")
            .UseConverter(static item => Markup.Escape(item.Label))
            .AddCancelResult(new MainMenuItem(MainMenuAction.Exit, "退出"))
            .AddChoices(viewModel.Items);

        return _console.Prompt(prompt).Action;
    }
}

public sealed class SpectreConfirmationInput : IConfirmationInput
{
    private readonly IAnsiConsole _console;

    public SpectreConfirmationInput(IAnsiConsole console)
    {
        ArgumentNullException.ThrowIfNull(console);
        _console = console;
    }

    public string Read(ConfirmationViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);

        return _console.Prompt(
            new TextPrompt<string>(Markup.Escape(viewModel.Prompt)));
    }
}
