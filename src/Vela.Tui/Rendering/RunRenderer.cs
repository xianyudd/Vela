using Vela.Tui.Screens;

namespace Vela.Tui.Rendering;

public enum RunProgressState
{
    Idle,
    Preflighting,
    AwaitingConfirmation,
    Running,
    Succeeded,
    Failed
}

public sealed record RunProgressViewModel(
    RunProgressState State,
    string Message,
    int? Percent);

public sealed class RunRenderer
{
    private readonly IVelaConsole _console;

    public RunRenderer(IVelaConsole console)
    {
        ArgumentNullException.ThrowIfNull(console);
        _console = console;
    }

    public void Render(RunProgressViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        _console.RenderProgress(viewModel);
    }
}
